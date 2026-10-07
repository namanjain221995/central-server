using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Restarts;
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

internal sealed class RestartTestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset now) => _now = now;
}

/// <summary>
/// Scheduling, dispatching and cancelling a department restart, against real
/// PostgreSQL with a clock the test controls.
/// </summary>
/// <remarks>
/// Each test seeds its own organization, so the fleet-wide sweep only ever finds
/// what the test put there, and asserts through a fresh context, so what is
/// checked is what was committed.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RestartScheduleServiceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private const string ModernAgent = "1.14.0";
    private const string OldAgent = "1.13.4";

    private static AuditWriter Audit(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static DeviceTaskService Tasks(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);

    private static RestartScheduleService Service(
        EndpointPlatformDbContext db, TimeProvider time, int warningSeconds = 300, int missedAfterSeconds = 900) =>
        new(db, Tasks(db, time), new DeviceScopeAuthorizer(db), Audit(db, time), time,
            Options.Create(new RestartScheduleOptions { WarningSeconds = warningSeconds, MissedAfterSeconds = missedAfterSeconds }),
            Options.Create(new AgentServerOptions()),
            NullLogger<RestartScheduleService>.Instance);

    private sealed record Fleet(Guid OrgId, PlatformUser Admin, Guid GroupId, Guid TokenId);

    /// <summary>An administrator past invitation: a password hash makes the account Active.</summary>
    private static PlatformUser ActiveAdmin(Guid organizationId, string displayName)
    {
        var user = new PlatformUser(organizationId, $"{displayName.ToLowerInvariant()}-{Guid.CreateVersion7():N}@test.local", displayName);
        user.SetPasswordHash(Infrastructure.Security.PasswordHasher.Hash("correct horse battery staple 9!"), Start);
        user.Status.ShouldBe(PlatformUserStatus.Active);
        return user;
    }

    private async Task<Fleet> SeedFleetAsync(RestartTestClock time, string name)
    {
        await using var db = _fixture.CreateDbContext(time);
        var org = new Organization(name, ("r" + Guid.CreateVersion7().ToString("N"))[..18]);
        db.Organizations.Add(org);

        // Active, as a real administrator is once their password is set: the
        // sweeper restarts nothing on behalf of an invited or disabled account.
        var admin = ActiveAdmin(org.Id, "Admin");
        admin.GrantAllDeviceScope();
        db.PlatformUsers.Add(admin);

        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddDays(1), 99);
        db.EnrollmentTokens.Add(token);

        var group = new DeviceGroup(org.Id, "Finance", "d", DeviceGroupType.Static);
        db.DeviceGroups.Add(group);
        await db.SaveChangesAsync();

        return new Fleet(org.Id, admin, group.Id, token.Id);
    }

    private async Task<Guid> SeedDeviceAsync(
        RestartTestClock time, Fleet fleet, string hostname, bool online = true, string agentVersion = ModernAgent)
    {
        await using var db = _fixture.CreateDbContext(time);
        var seen = online ? time.GetUtcNow() : time.GetUtcNow().AddHours(-2);
        var device = Device.Enroll(fleet.OrgId, hostname, "m-" + Guid.CreateVersion7().ToString("N"), agentVersion, null, fleet.TokenId, seen);
        device.MoveToGroup(fleet.GroupId);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    /// <summary>
    /// A heartbeat from each device at the clock's now. Online is "seen within
    /// the last three minutes", so a device seeded an hour of test-clock ago has
    /// to check in again before a dispatch that should find it online.
    /// </summary>
    private async Task TouchAsync(RestartTestClock time, params Guid[] deviceIds)
    {
        await using var db = _fixture.CreateDbContext(time);
        var now = time.GetUtcNow();
        await db.Devices.Where(d => deviceIds.Contains(d.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now));
    }

    private async Task<DeviceTask> TaskAsync(Guid taskId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
    }

    private async Task<List<DeviceTask>> TasksOfAsync(Guid deviceId, DeviceTaskType type)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().Where(t => t.DeviceId == deviceId && t.Type == type).ToListAsync();
    }

    private static Dictionary<Guid, RestartScheduleDeviceView> ByDevice(RestartScheduleView view) =>
        view.Devices.ToDictionary(d => d.DeviceId);

    /// <summary>The device accepts the restart: claims it and reports Windows' countdown, as the agent would.</summary>
    private async Task AcceptRestartAsync(RestartTestClock time, Guid deviceId, Guid taskId, int graceSeconds)
    {
        await using var db = _fixture.CreateDbContext(time);
        var tasks = Tasks(db, time);
        (await tasks.ClaimForDeviceAsync(deviceId)).Select(t => t.Id).ShouldContain(taskId);
        var restartAt = time.GetUtcNow().AddSeconds(graceSeconds);
        (await tasks.CompleteAsync(deviceId, taskId, true, "accepted",
            JsonSerializer.Serialize(new { graceSeconds, restartAt, outcome = "Scheduled" }))).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- create

    [Fact]
    public async Task Scheduling_holds_the_restart_and_plans_the_group_as_it_is_now()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Plan");
        var online = await SeedDeviceAsync(time, fleet, "ONLINE");
        var offline = await SeedDeviceAsync(time, fleet, "OFFLINE", online: false);
        var old = await SeedDeviceAsync(time, fleet, "OLD", agentVersion: OldAgent);

        await using var db = _fixture.CreateDbContext(time);
        var result = await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 7200);

        result.Status.ShouldBe(RestartScheduleCreateStatus.Created);
        var view = result.Schedule!;
        view.Status.ShouldBe("Pending");
        view.GroupName.ShouldBe("Finance");
        view.RequestedDelaySeconds.ShouldBe(7200);
        view.WarningSeconds.ShouldBe(300);
        view.RestartAt.ShouldBe(Start.AddSeconds(7200));
        view.DispatchAt.ShouldBe(Start.AddSeconds(6900));
        view.CanCancelCleanly.ShouldBeTrue();
        view.CreatedByDisplay.ShouldBe(fleet.Admin.Email);

        var devices = ByDevice(view);
        devices[online].State.ShouldBe("WillRestart");
        devices[offline].State.ShouldBe("OfflineNow");
        devices[old].State.ShouldBe("WillRestart");
        devices[old].SupportsCancel.ShouldBeFalse("1.13.x cannot abort a countdown");
        devices[online].SupportsCancel.ShouldBeTrue();

        (await TasksOfAsync(online, DeviceTaskType.RestartDevice)).ShouldBeEmpty("nothing reaches a device before dispatch");
    }

    [Fact]
    public async Task A_short_delay_hands_the_whole_delay_to_windows_and_is_due_at_once()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Short");
        await using var db = _fixture.CreateDbContext(time);

        var view = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 120)).Schedule!;

        view.WarningSeconds.ShouldBe(120);
        view.DispatchAt.ShouldBe(Start);
        view.CanCancelCleanly.ShouldBeFalse("it goes out on the next tick; there is no clean window");
    }

    [Fact]
    public async Task One_pending_schedule_per_department()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "One");
        await using var db = _fixture.CreateDbContext(time);
        var service = Service(db, time);

        var first = await service.CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 3600);
        var second = await service.CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 7200);

        first.Status.ShouldBe(RestartScheduleCreateStatus.Created);
        second.Status.ShouldBe(RestartScheduleCreateStatus.AlreadyScheduled);
        second.Schedule!.Id.ShouldBe(first.Schedule!.Id, "the existing schedule is returned");
    }

    [Fact]
    public async Task A_delay_outside_the_rules_or_a_group_outside_scope_is_refused()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Refuse");
        await using var db = _fixture.CreateDbContext(time);
        var service = Service(db, time);

        (await service.CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 30)).Status
            .ShouldBe(RestartScheduleCreateStatus.InvalidDelay);
        (await service.CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, Guid.CreateVersion7(), 3600)).Status
            .ShouldBe(RestartScheduleCreateStatus.GroupNotFound);

        var scoped = ActiveAdmin(fleet.OrgId, "Scoped");
        db.PlatformUsers.Add(scoped);
        await db.SaveChangesAsync();
        (await service.CreateAsync(fleet.OrgId, scoped.Id, scoped.Email, fleet.GroupId, 3600)).Status
            .ShouldBe(RestartScheduleCreateStatus.GroupNotFound, "a group outside the caller's scope does not exist for them");
    }

    // -------------------------------------------------------------- dispatch

    [Fact]
    public async Task At_the_dispatch_moment_each_online_member_gets_the_ordinary_restart_task_with_the_warning_as_grace()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Dispatch");
        var a = await SeedDeviceAsync(time, fleet, "A");
        var b = await SeedDeviceAsync(time, fleet, "B");
        var offline = await SeedDeviceAsync(time, fleet, "OFF", online: false);

        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 7200)).Schedule!.Id;
        }

        time.Advance(TimeSpan.FromSeconds(6899));
        await TouchAsync(time, a, b);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
        }

        (await TasksOfAsync(a, DeviceTaskType.RestartDevice)).ShouldBeEmpty("one second early is early");

        time.Advance(TimeSpan.FromSeconds(1));
        await TouchAsync(time, a, b);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
        }

        RestartScheduleView view;
        await using (var db = _fixture.CreateDbContext(time))
        {
            view = (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!;
        }

        view.Status.ShouldBe("Dispatched");
        view.DispatchedAt.ShouldBe(Start.AddSeconds(6900));
        var devices = ByDevice(view);
        devices[a].State.ShouldBe("Queued");
        devices[b].State.ShouldBe("Queued");
        devices[offline].State.ShouldBe("SkippedOffline");
        devices[offline].RestartTaskId.ShouldBeNull();

        foreach (var id in new[] { a, b })
        {
            var task = (await TasksOfAsync(id, DeviceTaskType.RestartDevice)).ShouldHaveSingleItem();
            devices[id].RestartTaskId.ShouldBe(task.Id);
            task.CreatedByDisplay.ShouldBe(fleet.Admin.Email, "the restart is on the scheduler's behalf");
            var payload = JsonDocument.Parse(task.PayloadJson!).RootElement;
            payload.GetProperty("graceSeconds").GetInt32().ShouldBe(300);
            payload.GetProperty("message").GetString().ShouldBe(RestartGrace.MessageFor(300));
        }

        (await TasksOfAsync(offline, DeviceTaskType.RestartDevice)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_skips_excluded_devices_devices_already_restarting_and_devices_the_scheduler_lost_authority_over()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Skip");
        var excluded = await SeedDeviceAsync(time, fleet, "EXCL");
        var busy = await SeedDeviceAsync(time, fleet, "BUSY");
        var plain = await SeedDeviceAsync(time, fleet, "PLAIN");

        // A scoped administrator schedules it, then loses the group.
        Guid scheduleId;
        Guid scopedId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            var scoped = ActiveAdmin(fleet.OrgId, "Scoped");
            db.PlatformUsers.Add(scoped);
            db.AdminDeviceScopes.Add(new AdminDeviceScope(scoped.Id, fleet.GroupId));
            await db.SaveChangesAsync();
            scopedId = scoped.Id;

            var service = Service(db, time);
            scheduleId = (await service.CreateAsync(fleet.OrgId, scoped.Id, scoped.Email, fleet.GroupId, 600)).Schedule!.Id;
            (await service.CancelAsync(fleet.OrgId, scoped.Id, scoped.Email, scheduleId, [excluded])).Devices
                .ShouldHaveSingleItem().Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.Excluded);

            db.DeviceTasks.Add(DeviceTask.Create(fleet.OrgId, busy, DeviceTaskType.RestartDevice, null, fleet.Admin.Id, "x", time.GetUtcNow(), TimeSpan.FromMinutes(15)));
            await db.SaveChangesAsync();
        }

        time.Advance(TimeSpan.FromSeconds(300));
        await TouchAsync(time, excluded, busy, plain);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
            var view = (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!;
            var devices = ByDevice(view);
            devices[excluded].State.ShouldBe("Excluded");
            devices[busy].State.ShouldBe("SkippedBusy");
            devices[plain].State.ShouldBe("Queued");
        }

        (await TasksOfAsync(excluded, DeviceTaskType.RestartDevice)).ShouldBeEmpty();
        (await TasksOfAsync(busy, DeviceTaskType.RestartDevice)).ShouldHaveSingleItem().CreatedByDisplay.ShouldBe("x");

        // Second schedule, scope revoked before dispatch: nothing on their behalf.
        var fleet2 = await SeedFleetAsync(time, "Scope");
        var device2 = await SeedDeviceAsync(time, fleet2, "D2");
        Guid schedule2;
        await using (var db = _fixture.CreateDbContext(time))
        {
            var scoped = ActiveAdmin(fleet2.OrgId, "Scoped");
            db.PlatformUsers.Add(scoped);
            var scope = new AdminDeviceScope(scoped.Id, fleet2.GroupId);
            db.AdminDeviceScopes.Add(scope);
            await db.SaveChangesAsync();
            schedule2 = (await Service(db, time).CreateAsync(fleet2.OrgId, scoped.Id, scoped.Email, fleet2.GroupId, 600)).Schedule!.Id;
            db.AdminDeviceScopes.Remove(scope);
            await db.SaveChangesAsync();
        }

        time.Advance(TimeSpan.FromSeconds(300));
        await TouchAsync(time, device2);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
            ByDevice((await Service(db, time).GetAsync(fleet2.OrgId, fleet2.Admin.Id, schedule2))!)[device2].State
                .ShouldBe("SkippedUnauthorized");
        }

        (await TasksOfAsync(device2, DeviceTaskType.RestartDevice)).ShouldBeEmpty();
        _ = scopedId;
    }

    /// <summary>
    /// An administrator disabled after scheduling is no longer anyone the
    /// platform acts for. Their plan still runs its course -- it is dispatched
    /// and recorded -- but no device is restarted on their behalf.
    /// </summary>
    [Fact]
    public async Task A_schedule_whose_administrator_was_disabled_restarts_nothing()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Disabled");
        var device = await SeedDeviceAsync(time, fleet, "LOYAL");
        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;
            var admin = await db.PlatformUsers.SingleAsync(u => u.Id == fleet.Admin.Id);
            admin.Disable();
            await db.SaveChangesAsync();
        }

        time.Advance(TimeSpan.FromSeconds(300));
        await TouchAsync(time, device);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
            var view = (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!;
            view.Status.ShouldBe("Dispatched");
            ByDevice(view)[device].State.ShouldBe("SkippedUnauthorized");
        }

        (await TasksOfAsync(device, DeviceTaskType.RestartDevice)).ShouldBeEmpty("a disabled administrator restarts nothing");
    }

    [Fact]
    public async Task A_schedule_found_long_after_its_moment_is_marked_missed_not_sent_late()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Missed");
        var device = await SeedDeviceAsync(time, fleet, "LATE");
        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;
        }

        // The server was down: the next tick is 16 minutes after the dispatch moment.
        time.Advance(TimeSpan.FromSeconds(300 + 901));
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
            var view = (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!;
            view.Status.ShouldBe("Missed");
            view.MissedAt.ShouldBe(time.GetUtcNow());
            view.Devices.ShouldBeEmpty();
        }

        (await TasksOfAsync(device, DeviceTaskType.RestartDevice)).ShouldBeEmpty("a restart nobody expects any more is not sent");
    }

    [Fact]
    public async Task A_schedule_a_little_late_is_still_sent()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Late");
        var device = await SeedDeviceAsync(time, fleet, "OK");
        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;
        }

        time.Advance(TimeSpan.FromSeconds(300 + 60));
        await TouchAsync(time, device);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
            (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!.Status.ShouldBe("Dispatched");
        }

        (await TasksOfAsync(device, DeviceTaskType.RestartDevice)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Dispatch_is_all_or_nothing_for_a_schedule()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Atomic");
        var first = await SeedDeviceAsync(time, fleet, "FIRST");
        var second = await SeedDeviceAsync(time, fleet, "SECOND");
        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;
        }

        time.Advance(TimeSpan.FromSeconds(300));

        // A second context holds the schedule and will cancel it while the
        // sweep is part-way through: the sweep's write then fails on the row
        // version, and everything it queued is rolled back with it.
        await using var sweeping = _fixture.CreateDbContext(time);
        await using var cancelling = _fixture.CreateDbContext(time);
        var sweepSchedule = await sweeping.Set<RestartSchedule>().SingleAsync(s => s.Id == scheduleId);
        (await Service(cancelling, time).CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, null)).Status
            .ShouldBe(RestartScheduleCancelStatus.Ok);

        sweepSchedule.MarkDispatched(time.GetUtcNow());
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => sweeping.SaveChangesAsync());

        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
            (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!.Status.ShouldBe("Cancelled");
        }

        (await TasksOfAsync(first, DeviceTaskType.RestartDevice)).ShouldBeEmpty();
        (await TasksOfAsync(second, DeviceTaskType.RestartDevice)).ShouldBeEmpty();
    }

    // ---------------------------------------------------- cancel before dispatch

    [Fact]
    public async Task Cancelling_a_pending_schedule_sends_nothing_and_is_final()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "CleanCancel");
        var device = await SeedDeviceAsync(time, fleet, "D");
        await using var db = _fixture.CreateDbContext(time);
        var service = Service(db, time);
        var scheduleId = (await service.CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;

        var result = await service.CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, null);

        result.Status.ShouldBe(RestartScheduleCancelStatus.Ok);
        result.Schedule!.Status.ShouldBe("Cancelled");
        result.Schedule.CancelledByDisplay.ShouldBe(fleet.Admin.Email);
        result.Devices.ShouldBeEmpty();

        time.Advance(TimeSpan.FromSeconds(600));
        await service.DispatchDueAsync(50);
        (await TasksOfAsync(device, DeviceTaskType.RestartDevice)).ShouldBeEmpty();

        (await service.CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, null)).Status
            .ShouldBe(RestartScheduleCancelStatus.NotCancellable);

        var overview = (await service.GetOverviewAsync(fleet.OrgId, fleet.Admin.Id, fleet.GroupId))!;
        overview.Active.ShouldBeNull();
        overview.Recent.ShouldHaveSingleItem().Id.ShouldBe(scheduleId);
        overview.Group.DeviceCount.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_for_some_devices_excludes_them_and_a_device_outside_the_department_is_reported()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Exclude");
        var keep = await SeedDeviceAsync(time, fleet, "KEEP");
        var drop = await SeedDeviceAsync(time, fleet, "DROP");
        var stranger = Guid.CreateVersion7();
        await using var db = _fixture.CreateDbContext(time);
        var service = Service(db, time);
        var scheduleId = (await service.CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;

        var result = await service.CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, [drop, stranger, drop]);

        result.Status.ShouldBe(RestartScheduleCancelStatus.Ok);
        result.Schedule!.Status.ShouldBe("Pending", "the department's restart stands; one device left it");
        result.Devices.Count.ShouldBe(2, "a device named twice is one exclusion");
        result.Devices.Single(d => d.DeviceId == drop).Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.Excluded);
        result.Devices.Single(d => d.DeviceId == stranger).Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.NotInSchedule);
        ByDevice(result.Schedule)[drop].State.ShouldBe("Excluded");
        ByDevice(result.Schedule)[keep].State.ShouldBe("WillRestart");

        (await service.CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, [drop])).Devices
            .ShouldHaveSingleItem().Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.AlreadyExcluded);
    }

    // ----------------------------------------------------- cancel after dispatch

    [Fact]
    public async Task After_dispatch_a_queued_restart_is_cancelled_where_it_sits_and_an_accepted_one_is_aborted_on_the_device()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "LateCancel");
        var queued = await SeedDeviceAsync(time, fleet, "QUEUED");
        var accepted = await SeedDeviceAsync(time, fleet, "ACCEPTED");
        var old = await SeedDeviceAsync(time, fleet, "OLD", agentVersion: OldAgent);
        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;
        }

        time.Advance(TimeSpan.FromSeconds(300));
        await TouchAsync(time, queued, accepted, old);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
        }

        var acceptedTask = (await TasksOfAsync(accepted, DeviceTaskType.RestartDevice)).Single();
        var oldTask = (await TasksOfAsync(old, DeviceTaskType.RestartDevice)).Single();
        time.Advance(TimeSpan.FromSeconds(10));
        await AcceptRestartAsync(time, accepted, acceptedTask.Id, 300);
        await AcceptRestartAsync(time, old, oldTask.Id, 300);

        RestartScheduleCancelResult result;
        await using (var db = _fixture.CreateDbContext(time))
        {
            result = await Service(db, time).CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, null);
        }

        result.Status.ShouldBe(RestartScheduleCancelStatus.Ok);
        result.Schedule!.Status.ShouldBe("Dispatched");
        result.Schedule.CancelledByDisplay.ShouldBe(fleet.Admin.Email);
        var outcomes = result.Devices.ToDictionary(d => d.DeviceId, d => d.Outcome);
        outcomes[queued].ShouldBe(RestartScheduleCancelDeviceOutcome.CancelledBeforeDelivery);
        outcomes[accepted].ShouldBe(RestartScheduleCancelDeviceOutcome.CancelRequested);
        outcomes[old].ShouldBe(RestartScheduleCancelDeviceOutcome.Unsupported);

        var states = ByDevice(result.Schedule);
        states[queued].State.ShouldBe("Cancelled");
        states[accepted].State.ShouldBe("CancelRequested");
        states[old].State.ShouldBe("CancelUnsupported");
        states[old].SupportsCancel.ShouldBeFalse();

        (await TaskAsync(states[queued].RestartTaskId!.Value)).Status.ShouldBe(DeviceTaskStatus.Cancelled);

        var cancelTask = (await TasksOfAsync(accepted, DeviceTaskType.CancelRestart)).ShouldHaveSingleItem();
        states[accepted].CancelTaskId.ShouldBe(cancelTask.Id);
        var payload = JsonDocument.Parse(cancelTask.PayloadJson!).RootElement;
        payload.GetProperty("restartTaskId").GetGuid().ShouldBe(acceptedTask.Id);
        payload.GetProperty("requestedBy").GetString().ShouldBe(fleet.Admin.Email);
        (await TasksOfAsync(old, DeviceTaskType.CancelRestart)).ShouldBeEmpty("an agent without the executor is never sent one");

        // The device confirms: the restart it undid is now Cancelled, and the
        // page says so for that device.
        await using (var db = _fixture.CreateDbContext(time))
        {
            var tasks = Tasks(db, time);
            (await tasks.ClaimForDeviceAsync(accepted)).Select(t => t.Id).ShouldContain(cancelTask.Id);
            (await tasks.CompleteAsync(accepted, cancelTask.Id, true, "Restart cancelled.",
                JsonSerializer.Serialize(new { outcome = "Cancelled", restartTaskId = acceptedTask.Id }))).ShouldBeTrue();
        }

        var restartAfter = await TaskAsync(acceptedTask.Id);
        restartAfter.Status.ShouldBe(DeviceTaskStatus.Cancelled);
        restartAfter.ResultMessage.ShouldBe($"Cancelled by {fleet.Admin.Email}: the device aborted the restart before it happened.");

        await using (var db = _fixture.CreateDbContext(time))
        {
            var view = (await Service(db, time).GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!;
            ByDevice(view)[accepted].State.ShouldBe("Cancelled");

            // Asking again changes nothing and says so.
            var again = await Service(db, time).CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, [accepted, queued]);
            again.Devices.Single(d => d.DeviceId == accepted).Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.AlreadyCancelled);
            again.Devices.Single(d => d.DeviceId == queued).Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.AlreadyCancelled);
        }
    }

    [Fact]
    public async Task A_device_whose_restart_moment_has_passed_has_nothing_to_cancel()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "TooLate");
        var device = await SeedDeviceAsync(time, fleet, "GONE");
        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;
        }

        time.Advance(TimeSpan.FromSeconds(300));
        await TouchAsync(time, device);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
        }

        var task = (await TasksOfAsync(device, DeviceTaskType.RestartDevice)).Single();
        await AcceptRestartAsync(time, device, task.Id, 300);

        time.Advance(TimeSpan.FromSeconds(301));
        await using (var db = _fixture.CreateDbContext(time))
        {
            var service = Service(db, time);
            ByDevice((await service.GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!)[device].State.ShouldBe("Restarted");

            var result = await service.CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, [device]);
            result.Devices.ShouldHaveSingleItem().Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.NothingToCancel);
        }

        (await TasksOfAsync(device, DeviceTaskType.CancelRestart)).ShouldBeEmpty();
        (await TaskAsync(task.Id)).Status.ShouldBe(DeviceTaskStatus.Succeeded, "a restart that happened is not rewritten");
    }

    [Fact]
    public async Task A_cancellation_the_device_refused_is_shown_as_failed_and_can_be_tried_again()
    {
        var time = new RestartTestClock(Start);
        var fleet = await SeedFleetAsync(time, "Retry");
        var device = await SeedDeviceAsync(time, fleet, "STUBBORN");
        Guid scheduleId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            scheduleId = (await Service(db, time).CreateAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, fleet.GroupId, 600)).Schedule!.Id;
        }

        time.Advance(TimeSpan.FromSeconds(300));
        await TouchAsync(time, device);
        await using (var db = _fixture.CreateDbContext(time))
        {
            await Service(db, time).DispatchDueAsync(50);
        }

        var restart = (await TasksOfAsync(device, DeviceTaskType.RestartDevice)).Single();
        await AcceptRestartAsync(time, device, restart.Id, 300);

        Guid cancelId;
        await using (var db = _fixture.CreateDbContext(time))
        {
            var result = await Service(db, time).CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, [device]);
            cancelId = ByDevice(result.Schedule!)[device].CancelTaskId!.Value;
        }

        await using (var db = _fixture.CreateDbContext(time))
        {
            var tasks = Tasks(db, time);
            await tasks.ClaimForDeviceAsync(device);
            (await tasks.CompleteAsync(device, cancelId, false, "Cancel failed: Access is denied.",
                JsonSerializer.Serialize(new { outcome = "Failed", code = 5 }))).ShouldBeTrue();
        }

        (await TaskAsync(restart.Id)).Status.ShouldBe(DeviceTaskStatus.Succeeded, "a refused cancellation leaves the restart as Windows has it");

        await using (var db = _fixture.CreateDbContext(time))
        {
            var service = Service(db, time);
            var view = (await service.GetAsync(fleet.OrgId, fleet.Admin.Id, scheduleId))!;
            ByDevice(view)[device].State.ShouldBe("CancelFailed");
            ByDevice(view)[device].Detail.ShouldBe("Cancel failed: Access is denied.");

            var again = await service.CancelAsync(fleet.OrgId, fleet.Admin.Id, fleet.Admin.Email, scheduleId, [device]);
            again.Devices.ShouldHaveSingleItem().Outcome.ShouldBe(RestartScheduleCancelDeviceOutcome.CancelRequested);
        }

        (await TasksOfAsync(device, DeviceTaskType.CancelRestart)).Count.ShouldBe(2);
    }
}
