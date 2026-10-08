using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Restarts;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Restarts;

/// <summary>
/// A department restart handed to each device when it is scheduled (agent
/// 1.15.0+), so the device restarts at the moment even if it has lost the
/// network by then. Older agents keep the dispatch-shortly-before path.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RestartScheduleArmingTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    private const string ArmingAgent = "1.15.0";
    private const string LegacyAgent = "1.14.0";

    private static AuditWriter Audit(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static DeviceTaskService Tasks(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);

    private static RestartScheduleService Service(EndpointPlatformDbContext db, TimeProvider time, int missedAfterSeconds = 300) =>
        new(db, Tasks(db, time), new RestartCancellationService(db, Tasks(db, time), time, NullLogger<RestartCancellationService>.Instance),
            new DeviceScopeAuthorizer(db), Audit(db, time), time,
            Options.Create(new RestartScheduleOptions { WarningSeconds = 300, MissedAfterSeconds = missedAfterSeconds }),
            Options.Create(new AgentServerOptions()),
            NullLogger<RestartScheduleService>.Instance);

    private sealed record Fleet(Guid OrgId, PlatformUser Admin, Guid GroupId, Guid TokenId);

    private async Task<Fleet> SeedFleetAsync(RestartTestClock time, string name)
    {
        await using var db = _fixture.CreateDbContext(time);
        var org = new Organization(name, ("a" + Guid.CreateVersion7().ToString("N"))[..18]);
        db.Organizations.Add(org);

        var admin = new PlatformUser(org.Id, $"admin-{Guid.CreateVersion7():N}@test.local", "Admin");
        admin.SetPasswordHash(PasswordHasher.Hash("correct horse battery staple 9!"), Start);
        admin.GrantAllDeviceScope();
        db.PlatformUsers.Add(admin);

        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddDays(1), 99);
        db.EnrollmentTokens.Add(token);

        var group = new DeviceGroup(org.Id, "Sales", "d", DeviceGroupType.Static);
        db.DeviceGroups.Add(group);
        await db.SaveChangesAsync();

        return new Fleet(org.Id, admin, group.Id, token.Id);
    }

    private async Task<Guid> SeedDeviceAsync(RestartTestClock time, Fleet fleet, string hostname, string agentVersion, bool online = true)
    {
        await using var db = _fixture.CreateDbContext(time);
        var seen = online ? time.GetUtcNow() : time.GetUtcNow().AddHours(-2);
        var device = Device.Enroll(fleet.OrgId, hostname, "m-" + Guid.CreateVersion7().ToString("N"), agentVersion, null, fleet.TokenId, seen);
        device.MoveToGroup(fleet.GroupId);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task TouchAsync(RestartTestClock time, params Guid[] deviceIds)
    {
        await using var db = _fixture.CreateDbContext(time);
        var now = time.GetUtcNow();
        await db.Devices.Where(d => deviceIds.Contains(d.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now));
    }

    private async Task SetBootAsync(Guid deviceId, DateTimeOffset bootedAt, DateTimeOffset seenAt)
    {
        await using var db = _fixture.CreateDbContext();
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBootAt, bootedAt).SetProperty(d => d.LastSeenAt, seenAt));
    }

    private async Task<List<DeviceTask>> TasksOfAsync(Guid deviceId, DeviceTaskType type)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().Where(t => t.DeviceId == deviceId && t.Type == type).ToListAsync();
    }

    private async Task<DeviceTask> TaskAsync(Guid taskId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
    }

    private async Task<RestartScheduleView> ViewAsync(RestartTestClock time, Fleet fleet, Guid scheduleId)
    {
        await using var db = _fixture.CreateDbContext(time);
        return (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!;
    }

    private static Dictionary<Guid, RestartScheduleDeviceView> ByDevice(RestartScheduleView view) =>
        view.Devices.ToDictionary(d => d.DeviceId);

    /// <summary>The device claims the armed restart and reports it held, as agent 1.15.0 does.</summary>
    private async Task ArmOnDeviceAsync(RestartTestClock time, Guid deviceId, Guid taskId, DateTimeOffset restartAt)
    {
        await using var db = _fixture.CreateDbContext(time);
        var tasks = Tasks(db, time);
        (await tasks.ClaimForDeviceAsync(deviceId)).Select(t => t.Id).ShouldContain(taskId);
        (await tasks.CompleteAsync(deviceId, taskId, true, "armed",
            JsonSerializer.Serialize(new { outcome = "Armed", restartAt, dueAt = restartAt, warningSeconds = 300 }))).ShouldBeTrue();
    }

    private async Task<Guid> ScheduleAsync(RestartTestClock time, Fleet fleet, int delaySeconds)
    {
        await using var db = _fixture.CreateDbContext(time);
        return (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, delaySeconds)).Schedule!.Id;
    }

    private async Task DispatchAsync(RestartTestClock time)
    {
        await using var db = _fixture.CreateDbContext(time);
        await Service(db, time).DispatchDueAsync(50);
    }

    [Fact]
    public async Task Scheduling_hands_the_restart_to_new_agents_at_once_online_or_not_and_leaves_old_agents_for_dispatch()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Arm");
        var online = await SeedDeviceAsync(time, fleet, "NEW-ON", ArmingAgent);
        var offline = await SeedDeviceAsync(time, fleet, "NEW-OFF", ArmingAgent, online: false);
        var legacy = await SeedDeviceAsync(time, fleet, "LEGACY", LegacyAgent);

        var scheduleId = await ScheduleAsync(time, fleet, 1800);
        var restartAt = Start.AddSeconds(1800);

        foreach (var id in new[] { online, offline })
        {
            var task = (await TasksOfAsync(id, DeviceTaskType.ScheduleRestart)).ShouldHaveSingleItem();
            task.Status.ShouldBe(DeviceTaskStatus.Queued);
            task.ExpiresAt.ShouldBe(restartAt, "pointless after its moment");
            var payload = JsonDocument.Parse(task.PayloadJson!).RootElement;
            payload.GetProperty("restartAt").GetDateTimeOffset().ShouldBe(restartAt);
            payload.GetProperty("warningSeconds").GetInt32().ShouldBe(300);
        }

        (await TasksOfAsync(legacy, DeviceTaskType.ScheduleRestart)).ShouldBeEmpty("1.14 cannot hold a restart");

        var view = await ViewAsync(time, fleet, scheduleId);
        view.Status.ShouldBe("Pending");
        view.CanCancelCleanly.ShouldBeFalse("devices already hold it");
        var devices = ByDevice(view);
        devices[online].State.ShouldBe("AwaitingDevice");
        devices[offline].State.ShouldBe("AwaitingDevice");
        devices[legacy].State.ShouldBe("WillRestart");
        devices[online].SupportsOfflineRestart.ShouldBeTrue();
        devices[legacy].SupportsOfflineRestart.ShouldBeFalse();

        await ArmOnDeviceAsync(time, online, devices[online].RestartTaskId!.Value, restartAt);
        ByDevice(await ViewAsync(time, fleet, scheduleId))[online].State.ShouldBe("Armed");

        // The final pass, shortly before the moment: only the old agent is sent
        // anything, and the armed devices are not sent a second restart.
        time.Set(Start.AddSeconds(1500));
        await TouchAsync(time, online, legacy);
        await DispatchAsync(time);

        (await TasksOfAsync(legacy, DeviceTaskType.RestartDevice)).ShouldHaveSingleItem();
        (await TasksOfAsync(online, DeviceTaskType.RestartDevice)).ShouldBeEmpty();
        (await TasksOfAsync(offline, DeviceTaskType.RestartDevice)).ShouldBeEmpty();

        view = await ViewAsync(time, fleet, scheduleId);
        view.Status.ShouldBe("Dispatched");
        devices = ByDevice(view);
        devices.Count.ShouldBe(3);
        devices[online].State.ShouldBe("Armed");
        devices[offline].State.ShouldBe("AwaitingDevice", "it can still check in and arm before the moment");
        devices[legacy].State.ShouldBe("Queued");
    }

    [Fact]
    public async Task A_short_delay_is_left_to_the_immediate_dispatch_and_not_armed_as_well()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Short");
        var device = await SeedDeviceAsync(time, fleet, "NEW", ArmingAgent);

        await ScheduleAsync(time, fleet, 120);

        (await TasksOfAsync(device, DeviceTaskType.ScheduleRestart)).ShouldBeEmpty();
        await DispatchAsync(time);
        (await TasksOfAsync(device, DeviceTaskType.RestartDevice)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Cancelling_the_department_before_dispatch_withdraws_queued_restarts_and_asks_armed_devices_to_cancel()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "CancelAll");
        var armed = await SeedDeviceAsync(time, fleet, "ARMED", ArmingAgent);
        var waiting = await SeedDeviceAsync(time, fleet, "WAITING", ArmingAgent, online: false);
        var legacy = await SeedDeviceAsync(time, fleet, "LEGACY", LegacyAgent);

        var scheduleId = await ScheduleAsync(time, fleet, 3600);
        var armedTask = (await TasksOfAsync(armed, DeviceTaskType.ScheduleRestart)).Single();
        var waitingTask = (await TasksOfAsync(waiting, DeviceTaskType.ScheduleRestart)).Single();
        await ArmOnDeviceAsync(time, armed, armedTask.Id, Start.AddSeconds(3600));

        time.Advance(TimeSpan.FromMinutes(5));
        RestartScheduleCancelResult result;
        await using (var db = _fixture.CreateDbContext(time))
        {
            result = await Service(db, time).CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, null);
        }

        result.Status.ShouldBe(RestartScheduleCancelStatus.Ok);
        result.Schedule!.Status.ShouldBe("Cancelled");
        var outcomes = result.Devices.ToDictionary(d => d.DeviceId, d => d.Outcome);
        outcomes[armed].ShouldBe(RestartScheduleCancelDeviceOutcome.CancelRequested);
        outcomes[waiting].ShouldBe(RestartScheduleCancelDeviceOutcome.CancelledBeforeDelivery);
        outcomes.ContainsKey(legacy).ShouldBeFalse("nothing was ever sent to it");

        (await TaskAsync(waitingTask.Id)).Status.ShouldBe(DeviceTaskStatus.Cancelled);
        var cancel = (await TasksOfAsync(armed, DeviceTaskType.CancelRestart)).ShouldHaveSingleItem();
        JsonDocument.Parse(cancel.PayloadJson!).RootElement.GetProperty("restartTaskId").GetGuid().ShouldBe(armedTask.Id);

        // Nothing goes out later either.
        time.Set(Start.AddSeconds(3300));
        await TouchAsync(time, armed, legacy);
        await DispatchAsync(time);
        (await TasksOfAsync(legacy, DeviceTaskType.RestartDevice)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_one_armed_device_cancels_it_there_while_an_old_agent_is_simply_excluded()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "CancelOne");
        var armed = await SeedDeviceAsync(time, fleet, "ARMED", ArmingAgent);
        var keep = await SeedDeviceAsync(time, fleet, "KEEP", ArmingAgent);
        var legacy = await SeedDeviceAsync(time, fleet, "LEGACY", LegacyAgent);

        var scheduleId = await ScheduleAsync(time, fleet, 3600);
        var armedTask = (await TasksOfAsync(armed, DeviceTaskType.ScheduleRestart)).Single();
        await ArmOnDeviceAsync(time, armed, armedTask.Id, Start.AddSeconds(3600));

        RestartScheduleCancelResult result;
        await using (var db = _fixture.CreateDbContext(time))
        {
            result = await Service(db, time).CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, [armed, legacy]);
        }

        result.Schedule!.Status.ShouldBe("Pending", "the rest of the department still restarts");
        var outcomes = result.Devices.ToDictionary(d => d.DeviceId, d => d.Outcome);
        outcomes[armed].ShouldBe(RestartScheduleCancelDeviceOutcome.CancelRequested);
        outcomes[legacy].ShouldBe(RestartScheduleCancelDeviceOutcome.Excluded);

        var devices = ByDevice(result.Schedule);
        devices[armed].State.ShouldBe("CancelRequested");
        devices[legacy].State.ShouldBe("Excluded");
        devices[keep].State.ShouldBe("AwaitingDevice");
        (await TasksOfAsync(keep, DeviceTaskType.ScheduleRestart)).Single().Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    [Fact]
    public async Task A_missed_final_pass_still_lets_armed_devices_restart_and_records_the_rest_as_missed()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Missed");
        var armed = await SeedDeviceAsync(time, fleet, "ARMED", ArmingAgent);
        var legacy = await SeedDeviceAsync(time, fleet, "LEGACY", LegacyAgent);

        var scheduleId = await ScheduleAsync(time, fleet, 3600);
        var armedTask = (await TasksOfAsync(armed, DeviceTaskType.ScheduleRestart)).Single();
        await ArmOnDeviceAsync(time, armed, armedTask.Id, Start.AddSeconds(3600));

        // The server was down through the dispatch moment and well past it.
        time.Set(Start.AddSeconds(3300 + 600));
        await TouchAsync(time, armed, legacy);
        await DispatchAsync(time);

        var view = await ViewAsync(time, fleet, scheduleId);
        view.Status.ShouldBe("Dispatched", "the armed devices carried it");
        var devices = ByDevice(view);
        devices[legacy].State.ShouldBe("SkippedMissed");
        devices[armed].State.ShouldBe("Armed");
        (await TasksOfAsync(legacy, DeviceTaskType.RestartDevice)).ShouldBeEmpty("a restart is never sent late");
    }

    [Fact]
    public async Task A_schedule_with_no_armed_device_is_still_simply_missed()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "MissedLegacy");
        var legacy = await SeedDeviceAsync(time, fleet, "LEGACY", LegacyAgent);

        var scheduleId = await ScheduleAsync(time, fleet, 3600);
        time.Set(Start.AddSeconds(3300 + 600));
        await TouchAsync(time, legacy);
        await DispatchAsync(time);

        (await ViewAsync(time, fleet, scheduleId)).Status.ShouldBe("Missed");
    }

    [Fact]
    public async Task After_the_moment_the_devices_boot_time_says_whether_it_restarted()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Boot");
        var restarted = await SeedDeviceAsync(time, fleet, "RESTARTED", ArmingAgent);
        var stubborn = await SeedDeviceAsync(time, fleet, "STUBBORN", ArmingAgent);
        var quiet = await SeedDeviceAsync(time, fleet, "QUIET", ArmingAgent);
        var never = await SeedDeviceAsync(time, fleet, "NEVER", ArmingAgent);

        var scheduleId = await ScheduleAsync(time, fleet, 1800);
        var restartAt = Start.AddSeconds(1800);
        foreach (var id in new[] { restarted, stubborn, quiet })
        {
            await ArmOnDeviceAsync(time, id, (await TasksOfAsync(id, DeviceTaskType.ScheduleRestart)).Single().Id, restartAt);
        }

        time.Set(restartAt.AddMinutes(30));
        await DispatchAsync(time);

        await SetBootAsync(restarted, restartAt.AddSeconds(40), restartAt.AddMinutes(29));
        await SetBootAsync(stubborn, Start.AddDays(-3), restartAt.AddMinutes(29));
        await SetBootAsync(quiet, Start.AddDays(-3), restartAt.AddMinutes(-2));

        var devices = ByDevice(await ViewAsync(time, fleet, scheduleId));
        devices[restarted].State.ShouldBe("Restarted");
        devices[stubborn].State.ShouldBe("NotRestarted");
        devices[quiet].State.ShouldBe("Armed", "not heard from since; it may yet report a restart");
        devices[never].State.ShouldBe("Expired", "offline until after the moment; nothing restarted");
    }
}
