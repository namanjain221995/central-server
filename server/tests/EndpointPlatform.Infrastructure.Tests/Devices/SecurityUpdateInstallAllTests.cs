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
/// Security "install all" converges instead of thrashing: per device it leaves out
/// hidden updates, updates Windows re-offered after an install, updates still
/// waiting on a restart or a verification scan, and updates an install in flight
/// already names -- while the single-device install stays permissive, as for drivers.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SecurityUpdateInstallAllTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = Guid.CreateVersion7();

    private const string Plain = "5e000004-0000-0000-0000-000000000001";
    private const string Hidden = "5e000004-0000-0000-0000-000000000002";
    private const string ReOffered = "5e000004-0000-0000-0000-000000000003";
    private const string Installing = "5e000004-0000-0000-0000-000000000004";
    private const string Preview = "5e000004-0000-0000-0000-000000000005";
    private const string WaitingForRestart = "5e000004-0000-0000-0000-000000000006";
    private const string WaitingForScan = "5e000004-0000-0000-0000-000000000007";

    private static SecurityUpdateService Service(EndpointPlatformDbContext db)
    {
        var time = TimeProvider.System;
        var audit = new AuditWriter(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());
        var tasks = new DeviceTaskService(db, audit, time, NullLogger<DeviceTaskService>.Instance);
        var read = new SecurityUpdateReadService(db, time, Options.Create(new AgentServerOptions()));
        return new SecurityUpdateService(db, tasks, read, audit, time, NullLogger<SecurityUpdateService>.Instance);
    }

    private static DeviceSecurityUpdateInstallRecord Record(Guid deviceId, string updateId, SecurityUpdateInstallStatus status)
    {
        var record = new DeviceSecurityUpdateInstallRecord(deviceId, updateId, 1, "Update", Start);
        record.RecordAttempt(status, 2, 0, Guid.CreateVersion7(), null, 1, Start);
        return record;
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
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1.27.0", null, token.Id, Start);
        db.Devices.Add(device);

        // Re-offered is not in the "everything excluded" set: for a security patch it
        // is retried (see the re-offered tests below).
        var offered = excludeEverything
            ? new[] { Hidden, Installing, WaitingForRestart, WaitingForScan }
            : new[] { Plain, Hidden, ReOffered, Installing, Preview, WaitingForRestart, WaitingForScan };
        foreach (var id in offered)
        {
            db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(
                device.Id, id, 1, "Update", "KB5044284", "Important", isPreview: id == Preview, 1, Start));
        }

        db.DeviceSecurityUpdateSuppressions.Add(new DeviceSecurityUpdateSuppression(
            device.Id, Hidden, "Update", "breaks the line-of-business app", Actor, "operator@test"));

        db.DeviceSecurityUpdateInstallRecords.AddRange(
            Record(device.Id, ReOffered, SecurityUpdateInstallStatus.ReOffered),
            Record(device.Id, WaitingForRestart, SecurityUpdateInstallStatus.PendingReboot),
            Record(device.Id, WaitingForScan, SecurityUpdateInstallStatus.InstalledUnverified));

        db.DeviceTasks.Add(DeviceTask.Create(org.Id, device.Id, DeviceTaskType.InstallSecurityUpdate,
            JsonSerializer.Serialize(new { updateIds = new[] { Installing }, issuedAt = Start }),
            Actor, "operator@test", DateTimeOffset.UtcNow, TimeSpan.FromHours(2)));

        await db.SaveChangesAsync();
        return (org.Id, device.Id);
    }

    private async Task<List<IReadOnlyList<string>>> QueuedInstallIdsAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var payloads = await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.InstallSecurityUpdate)
            .Select(t => t.PayloadJson)
            .ToListAsync();

        return payloads
            .Select(p => JsonSerializer.Deserialize<TaskPayloads.InstallSecurityUpdate>(p!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.UpdateIds)
            .Where(ids => !(ids.Count == 1 && ids[0] == Installing))
            .ToList();
    }

    [Fact]
    public async Task Install_all_leaves_out_hidden_restart_pending_unverified_and_already_installing_updates_but_retries_re_offered_ones()
    {
        var (orgId, deviceId) = await SeedAsync("SA1");

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).InstallAllAsync(orgId, scopedDeviceIds: null, Actor, "operator@test");
            result.DevicesQueued.ShouldBe(1);
            result.TotalUpdates.ShouldBe(2);
        }

        // Never hidden, never one waiting for its restart or verification scan, never
        // one in flight, never a preview -- but a re-offered security patch is retried.
        var queued = (await QueuedInstallIdsAsync(deviceId)).ShouldHaveSingleItem();
        queued.ShouldBe(new[] { Plain, ReOffered }, ignoreOrder: true);
    }

    // The common security case: a cumulative update installed, rolled back during the
    // restart ("undoing changes") and is offered again. The machine is unpatched; a
    // retry is the fix, so install-all must not drop it for good.
    [Fact]
    public async Task Install_all_retries_a_security_update_windows_re_offered_after_a_rolled_back_install()
    {
        Guid orgId, deviceId;
        await using (var db = _fixture.CreateDbContext())
        {
            var org = new Organization("SA4", ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
            db.Organizations.Add(org);
            var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
                Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
                Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
            db.EnrollmentTokens.Add(token);
            var device = Device.Enroll(org.Id, "SA4", "m-" + Guid.CreateVersion7().ToString("N"), "1.27.0", null, token.Id, Start);
            db.Devices.Add(device);

            db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(
                device.Id, ReOffered, 1, "2026-10 Cumulative Update", "KB5044284", "Critical", false, 1, Start));
            var rolledBack = new DeviceSecurityUpdateInstallRecord(device.Id, ReOffered, 1, "2026-10 Cumulative Update", Start);
            rolledBack.RecordAttempt(SecurityUpdateInstallStatus.PendingReboot, 2, 0, Guid.CreateVersion7(), null, 1, Start);
            rolledBack.MarkReOffered(); // still offered after the restart: it rolled back
            db.DeviceSecurityUpdateInstallRecords.Add(rolledBack);

            await db.SaveChangesAsync();
            (orgId, deviceId) = (org.Id, device.Id);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).InstallAllAsync(orgId, scopedDeviceIds: null, Actor, "operator@test");
            result.DevicesQueued.ShouldBe(1, "a re-offered security patch is still something to install");
            result.DevicesSkippedNothing.ShouldBe(0);
        }

        (await QueuedInstallIdsAsync(deviceId)).ShouldHaveSingleItem().ShouldBe(new[] { ReOffered });
    }

    [Fact]
    public async Task A_device_with_only_excluded_updates_counts_as_having_nothing_to_install()
    {
        var (orgId, deviceId) = await SeedAsync("SA2", excludeEverything: true);

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).InstallAllAsync(orgId, scopedDeviceIds: null, Actor, "operator@test");
            result.DevicesQueued.ShouldBe(0);
            result.DevicesSkippedNothing.ShouldBe(1);
        }

        (await QueuedInstallIdsAsync(deviceId)).ShouldBeEmpty();
    }

    // Exactly as for drivers: an operator who ticks one deliberately is acting
    // deliberately. Hiding governs install-all and the actionable list, not an
    // explicit single-device choice.
    [Fact]
    public async Task A_single_device_install_stays_permissive_about_hidden_and_re_offered_updates()
    {
        var (orgId, deviceId) = await SeedAsync("SA3");

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await Service(db).QueueInstallAsync(orgId, deviceId, [Hidden, ReOffered], Actor, "operator@test");
            result.Status.ShouldBe(SecurityUpdateQueueStatus.Queued);
        }

        var queued = (await QueuedInstallIdsAsync(deviceId)).ShouldHaveSingleItem();
        queued.OrderBy(id => id).ShouldBe(new[] { Hidden, ReOffered });
    }
}
