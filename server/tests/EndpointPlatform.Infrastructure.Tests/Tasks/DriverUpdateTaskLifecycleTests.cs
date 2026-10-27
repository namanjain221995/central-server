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
/// A driver install's lifecycle stops lying: a claimed install gets time to run,
/// a silent one expires saying the device had it and a verification scan is queued,
/// and the device's late result is accepted and ingested instead of discarded.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DriverUpdateTaskLifecycleTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.CreateVersion7();
    private const string RequesterDisplay = "operator@test";
    private const string A = "bbbbbbbb-0000-0000-0000-000000000001";

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
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1.26.0", null, token.Id, Start);
        db.Devices.Add(device);
        db.DeviceDriverUpdates.Add(new DeviceDriverUpdate(device.Id, A, 1, "Update A", null, null, null, false, 1, Start));
        await db.SaveChangesAsync();
        return device;
    }

    private async Task<DeviceTask> QueueInstallAsync(Device device)
    {
        await using var db = _fixture.CreateDbContext();
        var task = DeviceTask.Create(
            device.OrganizationId, device.Id, DeviceTaskType.InstallDriverUpdate,
            JsonSerializer.Serialize(new { updateIds = new[] { A }, issuedAt = Start }),
            Requester, RequesterDisplay, Start, TimeSpan.FromHours(1));
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
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ScanDriverUpdates)
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
        var device = await SeedDeviceAsync("LC1");
        var install = await QueueInstallAsync(device);

        // Claimed five minutes before its one-hour claim deadline.
        await ClaimAsync(device.Id, Start.AddMinutes(55));

        var claimed = await TaskAsync(install.Id);
        claimed.Status.ShouldBe(DeviceTaskStatus.Delivered);
        claimed.ExpiresAt.ShouldBe(Start.AddMinutes(55).AddHours(2), tolerance: TimeSpan.FromMilliseconds(1));

        await SweepAsync(Start.AddMinutes(65));
        (await TaskAsync(install.Id)).Status.ShouldBe(DeviceTaskStatus.Delivered,
            "past the claim deadline, but well inside the time to run");

        await SweepAsync(Start.AddHours(3));
        (await TaskAsync(install.Id)).Status.ShouldBe(DeviceTaskStatus.Expired);
    }

    // M1: a Windows Update search can outlast the scan's half-hour lifetime. Claimed
    // late in it, the scan must not be expired mid-search: its honest result was
    // refused and the probe, seeing no answer, asked again at once.
    [Fact]
    public async Task A_claimed_scan_that_runs_past_its_lifetime_is_not_expired_and_its_result_is_accepted()
    {
        var device = await SeedDeviceAsync("LC10");
        Guid scanId;
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanDriverUpdates, null,
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
            (await Service(db, clock).CompleteAsync(device.Id, scanId, true, "Found 0 driver update(s).",
                """{"updates":[]}""")).ShouldBeTrue("the device's result lands while the scan is still in flight");
        }

        var settled = await TaskAsync(scanId);
        settled.Status.ShouldBe(DeviceTaskStatus.Succeeded);
        settled.ResultMessage.ShouldBe("Found 0 driver update(s).");
    }

    [Fact]
    public async Task Other_tasks_keep_their_deadline_when_claimed()
    {
        var device = await SeedDeviceAsync("LC2");
        Guid pingId;
        await using (var db = _fixture.CreateDbContext())
        {
            var ping = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.Ping, null,
                Requester, RequesterDisplay, Start, TimeSpan.FromMinutes(5));
            db.DeviceTasks.Add(ping);
            await db.SaveChangesAsync();
            pingId = ping.Id;
        }

        await ClaimAsync(device.Id, Start.AddMinutes(1));

        (await TaskAsync(pingId)).ExpiresAt.ShouldBe(Start.AddMinutes(5), tolerance: TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Expiring_a_silent_claimed_install_says_so_and_queues_a_verification_scan_for_the_requester()
    {
        var device = await SeedDeviceAsync("LC3");
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
    }

    // F5(a): the expiry says what actually became of the verification scan.
    [Fact]
    public async Task Expiring_a_silent_install_whose_verification_scan_is_already_pending_says_so()
    {
        var device = await SeedDeviceAsync("LC8");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));

        // An operator queued a scan shortly before the sweep; it is still waiting for
        // the device and will see whatever the install did.
        Guid operatorScanId;
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanDriverUpdates, null,
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
        var device = await SeedDeviceAsync("LC9");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));

        // The device's agent no longer meets the scan's minimum (downgraded): the
        // platform cannot queue a scan for it, and must not claim it did.
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
    public async Task Expiring_an_install_nobody_claimed_says_so_and_queues_nothing()
    {
        var device = await SeedDeviceAsync("LC4");
        var install = await QueueInstallAsync(device);

        await SweepAsync(Start.AddHours(2));

        (await TaskAsync(install.Id)).ResultMessage.ShouldBe(DeviceTask.ExpiredUnclaimedMessage);
        (await ScansAsync(device.Id)).ShouldBeEmpty("nothing ran, so there is nothing to verify");
    }

    [Fact]
    public async Task A_late_result_for_an_expired_claimed_install_is_accepted_and_ingested()
    {
        var device = await SeedDeviceAsync("LC5");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));
        await SweepAsync(Start.AddHours(4));

        var json = JsonSerializer.Serialize(new
        {
            rebootRequired = false,
            installed = new[] { new { updateId = A, title = "Update A", resultCode = 2, hresult = 0 } },
            failed = Array.Empty<object>(),
        });

        var clock = new TestClock(Start.AddHours(5));
        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, clock).CompleteAsync(device.Id, install.Id, true, "Installed 1 driver update(s).", json))
                .ShouldBeTrue();
        }

        var settled = await TaskAsync(install.Id);
        settled.Status.ShouldBe(DeviceTaskStatus.Succeeded);
        settled.ResultMessage.ShouldBe("Installed 1 driver update(s)." + DeviceTask.LateResultSuffix);

        await using var verify = _fixture.CreateDbContext();
        var record = await verify.DeviceDriverUpdateInstallRecords.AsNoTracking().SingleAsync(r => r.DeviceId == device.Id);
        record.UpdateId.ShouldBe(A);
        record.Status.ShouldBe(DriverUpdateInstallStatus.InstalledUnverified, "the late result goes through the same ingestion");
        record.LastTaskId.ShouldBe(install.Id);

        // The expiry already queued a verification scan; the late result does not add a second.
        (await ScansAsync(device.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_result_for_an_install_that_expired_unclaimed_is_still_refused()
    {
        var device = await SeedDeviceAsync("LC6");
        var install = await QueueInstallAsync(device);
        await SweepAsync(Start.AddHours(2));

        var clock = new TestClock(Start.AddHours(3));
        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, clock).CompleteAsync(device.Id, install.Id, true, "forged", null)).ShouldBeFalse();
        }

        (await TaskAsync(install.Id)).Status.ShouldBe(DeviceTaskStatus.Expired);

        await using var verify = _fixture.CreateDbContext();
        (await verify.DeviceDriverUpdateInstallRecords.AnyAsync(r => r.DeviceId == device.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Only_the_tasks_own_device_can_post_a_late_result()
    {
        var device = await SeedDeviceAsync("LC7");
        var other = await SeedDeviceAsync("LC7b");
        var install = await QueueInstallAsync(device);
        await ClaimAsync(device.Id, Start.AddMinutes(1));
        await SweepAsync(Start.AddHours(4));

        var clock = new TestClock(Start.AddHours(5));
        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, clock).CompleteAsync(other.Id, install.Id, true, "forged", null)).ShouldBeFalse();
        }

        (await TaskAsync(install.Id)).Status.ShouldBe(DeviceTaskStatus.Expired);
    }
}
