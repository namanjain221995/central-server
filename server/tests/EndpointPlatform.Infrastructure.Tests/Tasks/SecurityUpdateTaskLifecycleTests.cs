using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Tasks;

file sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset to) => _now = to;
}

/// <summary>
/// A security install's lifecycle stops lying: a claimed install gets time to run
/// (a cumulative update is the longest install Windows Update does), a silent one
/// expires saying the device had it and a verification scan is queued, and the
/// device's late result is accepted and ingested instead of discarded.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SecurityUpdateTaskLifecycleTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.CreateVersion7();
    private const string RequesterDisplay = "operator@test";
    private const string A = "5e000003-0000-0000-0000-000000000001";

    private static AuditWriter Audit(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private DeviceTaskService Service(EndpointPlatformDbContext db, TimeProvider clock) =>
        new(db, Audit(db, clock), clock, NullLogger<DeviceTaskService>.Instance);

    private async Task<Device> SeedDeviceAsync(string tag)
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
        db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(device.Id, A, 1, "Update A", "KB5044284", "Critical", false, 1, Start));
        await db.SaveChangesAsync();
        return device;
    }

    /// <summary>Queues an install with the catalogue's two-hour lifetime, as the service would.</summary>
    private async Task<DeviceTask> QueueInstallAsync(Device device)
    {
        await using var db = _fixture.CreateDbContext();
        var task = DeviceTask.Create(
            device.OrganizationId, device.Id, DeviceTaskType.InstallSecurityUpdate,
            JsonSerializer.Serialize(new { updateIds = new[] { A }, issuedAt = Start }),
            Requester, RequesterDisplay, Start, TimeSpan.FromHours(2));
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private async Task<DeviceTask> TaskAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task<List<DeviceTask>> ScansAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ScanSecurityUpdates)
            .ToListAsync();
    }

    private async Task ClaimAsync(Guid deviceId, DateTimeOffset at)
    {
        var clock = new TestClock(at);
        await using var db = _fixture.CreateDbContext();
        (await Service(db, clock).ClaimForDeviceAsync(deviceId)).ShouldNotBeEmpty();
    }

    private async Task<int> SweepAsync(DateTimeOffset at)
    {
        var clock = new TestClock(at);
        await using var db = _fixture.CreateDbContext();
        return await Service(db, clock).SweepExpiredAsync(500);
    }

    [Fact]
    public async Task A_claimed_install_gets_its_execution_grace_and_is_not_expired_mid_install()
    {
        var device = await SeedDeviceAsync("SL1");
        var install = await QueueInstallAsync(device);

        // Claimed five minutes before its two-hour claim deadline.
        await ClaimAsync(device.Id, Start.AddMinutes(115));

        var claimed = await TaskAsync(install.Id);
        claimed.Status.ShouldBe(DeviceTaskStatus.Delivered);
        claimed.ExpiresAt.ShouldBe(Start.AddMinutes(115).AddHours(3), tolerance: TimeSpan.FromMilliseconds(1));

        // Two and a half hours into a cumulative update: past the claim deadline,
        // and past even a driver install's grace, but inside its own time to run.
        await SweepAsync(Start.AddMinutes(115).AddMinutes(150));
        (await TaskAsync(install.Id)).Status.ShouldBe(DeviceTaskStatus.Delivered,
            "past the claim deadline, but well inside the time to run");

        await SweepAsync(Start.AddHours(6));
        (await TaskAsync(install.Id)).Status.ShouldBe(DeviceTaskStatus.Expired);
    }

    [Fact]
    public async Task A_claimed_scan_that_runs_past_its_lifetime_is_not_expired_and_its_result_is_accepted()
    {
        var device = await SeedDeviceAsync("SL2");
        Guid scanId;
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanSecurityUpdates, null,
                Requester, RequesterDisplay, Start, TimeSpan.FromMinutes(30));
            db.DeviceTasks.Add(scan);
            await db.SaveChangesAsync();
            scanId = scan.Id;
        }

        // Claimed a minute before its half-hour claim deadline.
        await ClaimAsync(device.Id, Start.AddMinutes(29));
        (await TaskAsync(scanId)).ExpiresAt.ShouldBe(Start.AddMinutes(29).AddHours(1), tolerance: TimeSpan.FromMilliseconds(1));

        // Twenty minutes into the search: past the claim deadline, inside the time to run.
        await SweepAsync(Start.AddMinutes(49));
        (await TaskAsync(scanId)).Status.ShouldBe(DeviceTaskStatus.Delivered);

        var clock = new TestClock(Start.AddMinutes(52));
        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, clock).CompleteAsync(device.Id, scanId, true, "Found 0 security update(s).",
                """{"updates":[]}""")).ShouldBeTrue("the device's result lands while the scan is still in flight");
        }

        (await TaskAsync(scanId)).Status.ShouldBe(DeviceTaskStatus.Succeeded);

        // The grace is for running, not a licence for late results: a scan that does
        // run out its grace and expires still refuses a late one (only installs take one).
        Guid silentScanId;
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanSecurityUpdates, null,
                Requester, RequesterDisplay, Start.AddMinutes(60), TimeSpan.FromMinutes(30));
            db.DeviceTasks.Add(scan);
            await db.SaveChangesAsync();
            silentScanId = scan.Id;
        }

        await ClaimAsync(device.Id, Start.AddMinutes(61));
        await SweepAsync(Start.AddHours(3));
        (await TaskAsync(silentScanId)).Status.ShouldBe(DeviceTaskStatus.Expired);

        var late = new TestClock(Start.AddHours(4));
        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, late).CompleteAsync(device.Id, silentScanId, true, "late", """{"updates":[]}"""))
                .ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Expiring_a_silent_claimed_install_says_so_and_queues_a_verification_scan_for_the_requester()
    {
        var device = await SeedDeviceAsync("SL3");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));

        await SweepAsync(Start.AddHours(4));

        var expired = await TaskAsync(install.Id);
        expired.Status.ShouldBe(DeviceTaskStatus.Expired);
        expired.ResultMessage.ShouldBe(
            "The device received this task but never reported a result (it may have gone offline mid-task); "
            + "a verification scan has been queued.");

        var scan = (await ScansAsync(device.Id)).ShouldHaveSingleItem();
        scan.Status.ShouldBe(DeviceTaskStatus.Queued);
        scan.CreatedByUserId.ShouldBe(Requester);
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.AnyAsync(t => t.DeviceId == device.Id && t.Type == DeviceTaskType.ScanDriverUpdates))
            .ShouldBeFalse("a security install is verified by a security scan, not a driver scan");
    }

    [Fact]
    public async Task Expiring_a_silent_install_whose_verification_scan_is_already_pending_says_so()
    {
        var device = await SeedDeviceAsync("SL4");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));

        Guid operatorScanId;
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanSecurityUpdates, null,
                Guid.CreateVersion7(), "someone@test", Start.AddHours(3).AddMinutes(50), TimeSpan.FromMinutes(30));
            db.DeviceTasks.Add(scan);
            await db.SaveChangesAsync();
            operatorScanId = scan.Id;
        }

        await SweepAsync(Start.AddHours(4));

        (await TaskAsync(install.Id)).ResultMessage.ShouldBe(
            "The device received this task but never reported a result (it may have gone offline mid-task); "
            + "a verification scan was already queued or running.");
        (await ScansAsync(device.Id)).ShouldHaveSingleItem().Id.ShouldBe(operatorScanId);
    }

    [Fact]
    public async Task Expiring_a_silent_install_whose_verification_scan_cannot_be_queued_says_so()
    {
        var device = await SeedDeviceAsync("SL5");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));

        // The device's agent no longer meets the scan's minimum (downgraded).
        await using (var db = _fixture.CreateDbContext())
        {
            await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.AgentVersion, "1.20.0"));
        }

        await SweepAsync(Start.AddHours(4));

        (await TaskAsync(install.Id)).ResultMessage.ShouldBe(
            "The device received this task but never reported a result (it may have gone offline mid-task); "
            + "no verification scan could be queued, so scan the device once it is back.");
        (await ScansAsync(device.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_late_result_for_an_expired_claimed_install_is_accepted_and_ingested()
    {
        var device = await SeedDeviceAsync("SL6");
        var other = await SeedDeviceAsync("SL6b");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));

        // A second install the device never claimed: nothing ran, so nothing may land late.
        var unclaimed = await QueueInstallAsync(device);
        await SweepAsync(Start.AddHours(4));
        (await TaskAsync(unclaimed.Id)).ResultMessage.ShouldBe(DeviceTask.ExpiredUnclaimedMessage);

        var json = JsonSerializer.Serialize(new
        {
            rebootRequired = true,
            installed = new[] { new { updateId = A, title = "Update A", resultCode = 2, hresult = 0 } },
            failed = Array.Empty<object>(),
        });

        var clock = new TestClock(Start.AddHours(5));
        await using (var db = _fixture.CreateDbContext())
        {
            var service = Service(db, clock);

            // The widening is narrow: never for an install nobody claimed, never from another device.
            (await service.CompleteAsync(device.Id, unclaimed.Id, true, "forged", json)).ShouldBeFalse();
            (await service.CompleteAsync(other.Id, install.Id, true, "forged", json)).ShouldBeFalse();

            (await service.CompleteAsync(device.Id, install.Id, true, "Installed 1 security update(s).", json))
                .ShouldBeTrue();

            // And once settled, a replay cannot rewrite it.
            (await service.CompleteAsync(device.Id, install.Id, false, "tampered", null)).ShouldBeFalse();
        }

        (await TaskAsync(unclaimed.Id)).Status.ShouldBe(DeviceTaskStatus.Expired);

        var settled = await TaskAsync(install.Id);
        settled.Status.ShouldBe(DeviceTaskStatus.Succeeded);
        settled.ResultMessage.ShouldBe("Installed 1 security update(s)." + DeviceTask.LateResultSuffix);

        await using var verify = _fixture.CreateDbContext();
        var record = await verify.DeviceSecurityUpdateInstallRecords.AsNoTracking().SingleAsync(r => r.DeviceId == device.Id);
        record.UpdateId.ShouldBe(A);
        record.Status.ShouldBe(SecurityUpdateInstallStatus.PendingReboot, "the late result goes through the same ingestion");
        record.LastTaskId.ShouldBe(install.Id);

        // The expiry already queued a verification scan; the late result does not add a second.
        (await ScansAsync(device.Id)).Count.ShouldBe(1);
    }
}
