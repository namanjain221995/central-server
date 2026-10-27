using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// "Install all" converges instead of thrashing: per device it leaves out hidden
/// updates, updates Windows re-offered after an install, and updates an install in
/// flight already names -- while the single-device install stays permissive.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DriverUpdateInstallAllTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = Guid.CreateVersion7();

    private const string Plain = "cccccccc-0000-0000-0000-000000000001";
    private const string Hidden = "cccccccc-0000-0000-0000-000000000002";
    private const string ReOffered = "cccccccc-0000-0000-0000-000000000003";
    private const string Installing = "cccccccc-0000-0000-0000-000000000004";
    private const string Preview = "cccccccc-0000-0000-0000-000000000005";

    private static DriverUpdateService Service(EndpointPlatformDbContext db)
    {
        var time = TimeProvider.System;
        var audit = new AuditWriter(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());
        var tasks = new DeviceTaskService(db, audit, time, NullLogger<DeviceTaskService>.Instance);
        var read = new DriverUpdateReadService(db, time, Options.Create(new AgentServerOptions()));
        return new DriverUpdateService(db, tasks, read, audit, time, NullLogger<DriverUpdateService>.Instance);
    }

    private async Task<(Guid OrgId, Guid DeviceId)> SeedAsync(string tag, bool excludeEverything = false)
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization(tag, ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1.26.0", null, token.Id, Start);
        db.Devices.Add(device);

        var offered = excludeEverything
            ? new[] { Hidden, ReOffered, Installing }
            : new[] { Plain, Hidden, ReOffered, Installing, Preview };
        foreach (var id in offered)
        {
            db.DeviceDriverUpdates.Add(new DeviceDriverUpdate(
                device.Id, id, 1, "Update", null, null, null, isPreview: id == Preview, 1, Start));
        }

        db.DeviceDriverUpdateSuppressions.Add(new DeviceDriverUpdateSuppression(
            device.Id, Hidden, "Update", "firmware", Actor, "operator@test"));

        var record = new DeviceDriverUpdateInstallRecord(device.Id, ReOffered, 1, "Update", Start);
        record.RecordAttempt(DriverUpdateInstallStatus.ReOffered, 2, 0, Guid.CreateVersion7(), null, 1, Start);
        db.DeviceDriverUpdateInstallRecords.Add(record);

        db.DeviceTasks.Add(DeviceTask.Create(org.Id, device.Id, DeviceTaskType.InstallDriverUpdate,
            JsonSerializer.Serialize(new { updateIds = new[] { Installing }, issuedAt = Start }),
            Actor, "operator@test", DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));

        await db.SaveChangesAsync();
        return (org.Id, device.Id);
    }

    private async Task<List<IReadOnlyList<string>>> QueuedInstallIdsAsync(Guid deviceId, string except)
    {
        await using var db = _fixture.CreateDbContext();
        var payloads = await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.InstallDriverUpdate)
            .Select(t => t.PayloadJson)
            .ToListAsync();

        return payloads
            .Select(p => JsonSerializer.Deserialize<TaskPayloads.InstallDriverUpdate>(p!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.UpdateIds)
            .Where(ids => !(ids.Count == 1 && ids[0] == except))
            .ToList();
    }

    [Fact]
    public async Task Install_all_leaves_out_hidden_re_offered_and_already_installing_updates()
    {
        var (orgId, deviceId) = await SeedAsync("IA1");

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).InstallAllAsync(orgId, scopedDeviceIds: null, Actor, "operator@test");
            result.DevicesQueued.ShouldBe(1);
            result.TotalUpdates.ShouldBe(1);
        }

        var queued = (await QueuedInstallIdsAsync(deviceId, except: Installing)).ShouldHaveSingleItem();
        queued.ShouldBe(new[] { Plain }, "never hidden, never re-offered, never one already in flight, never a preview");
    }

    [Fact]
    public async Task A_device_with_only_excluded_updates_counts_as_having_nothing_to_install()
    {
        var (orgId, deviceId) = await SeedAsync("IA2", excludeEverything: true);

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).InstallAllAsync(orgId, scopedDeviceIds: null, Actor, "operator@test");
            result.DevicesQueued.ShouldBe(0);
            result.DevicesSkippedNothing.ShouldBe(1);
        }

        (await QueuedInstallIdsAsync(deviceId, except: Installing)).ShouldBeEmpty();
    }

    // F2(a): an update whose last install waits on a restart or on its verification
    // scan is not finished being judged; re-sending it only fails ("installer busy /
    // restart pending") and used to overwrite the "needs restart" memory.
    [Fact]
    public async Task Install_all_leaves_out_updates_still_waiting_on_a_restart_or_a_verification_scan()
    {
        const string WaitingForRestart = "cccccccc-0000-0000-0000-000000000006";
        const string WaitingForScan = "cccccccc-0000-0000-0000-000000000007";

        Guid orgId, deviceId;
        await using (var db = _fixture.CreateDbContext())
        {
            var org = new Organization("IA4", ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
            db.Organizations.Add(org);
            var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
                Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
                Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
            db.EnrollmentTokens.Add(token);
            var device = Device.Enroll(org.Id, "IA4", "m-" + Guid.CreateVersion7().ToString("N"), "1.26.0", null, token.Id, Start);
            db.Devices.Add(device);

            foreach (var id in new[] { Plain, WaitingForRestart, WaitingForScan })
            {
                db.DeviceDriverUpdates.Add(new DeviceDriverUpdate(device.Id, id, 1, "Update", null, null, null, false, 1, Start));
            }

            var restart = new DeviceDriverUpdateInstallRecord(device.Id, WaitingForRestart, 1, "Update", Start);
            restart.RecordAttempt(DriverUpdateInstallStatus.PendingReboot, 2, 0, Guid.CreateVersion7(), null, 1, Start);
            db.DeviceDriverUpdateInstallRecords.Add(restart);

            var unverified = new DeviceDriverUpdateInstallRecord(device.Id, WaitingForScan, 1, "Update", Start);
            unverified.RecordAttempt(DriverUpdateInstallStatus.InstalledUnverified, 2, 0, Guid.CreateVersion7(), null, 1, Start);
            db.DeviceDriverUpdateInstallRecords.Add(unverified);

            await db.SaveChangesAsync();
            (orgId, deviceId) = (org.Id, device.Id);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).InstallAllAsync(orgId, scopedDeviceIds: null, Actor, "operator@test");
            result.DevicesQueued.ShouldBe(1);
            result.TotalUpdates.ShouldBe(1);
        }

        var queued = (await QueuedInstallIdsAsync(deviceId, except: "none")).ShouldHaveSingleItem();
        queued.ShouldBe(new[] { Plain }, "a restart or the follow-up scan settles the other two; retrying them only fails");
    }

    [Fact]
    public async Task A_single_device_install_stays_permissive_about_hidden_and_re_offered_updates()
    {
        var (orgId, deviceId) = await SeedAsync("IA3");

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).QueueInstallAsync(orgId, deviceId, [Hidden, ReOffered], Actor, "operator@test");
            result.Status.ShouldBe(DriverUpdateQueueStatus.Queued, "an operator who ticks one deliberately is acting deliberately");
        }

        var queued = (await QueuedInstallIdsAsync(deviceId, except: Installing)).ShouldHaveSingleItem();
        queued.OrderBy(id => id).ShouldBe(new[] { Hidden, ReOffered });
    }
}
