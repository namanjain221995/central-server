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
/// An <c>InstallDriverUpdate</c> result -- legacy or 1.27.0, success or failure --
/// is remembered per update, reconciled against every fresh offered list, and
/// followed by a server-queued scan whenever it leaves the pending list stale.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DriverUpdateInstallIngestionTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.CreateVersion7();
    private const string RequesterDisplay = "operator@test";

    private const string A = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string B = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string C = "aaaaaaaa-0000-0000-0000-000000000003";
    private const string D = "aaaaaaaa-0000-0000-0000-000000000004";
    private const string E = "aaaaaaaa-0000-0000-0000-000000000005";
    private const string F = "aaaaaaaa-0000-0000-0000-000000000006";

    private static AuditWriter Audit(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static string Payload(params string[] ids) =>
        JsonSerializer.Serialize(new { updateIds = ids, issuedAt = Start }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string ScanJson(params (string Id, int Revision)[] updates) =>
        JsonSerializer.Serialize(new
        {
            scannedAt = Start,
            updates = updates.Select(u => new { updateId = u.Id, revision = u.Revision, title = "Update " + u.Id[^1], isPreview = false }),
        });

    private static object ScanObject(params string[] ids) => new
    {
        scannedAt = Start,
        updates = ids.Select(id => new { updateId = id, revision = 1, title = "Update " + id[^1], isPreview = false }),
    };

    private static string Legacy(bool rebootRequired, string[] installed, params (string Id, long Hresult)[] failed) =>
        JsonSerializer.Serialize(new
        {
            rebootRequired,
            installed = installed.Select(id => new { updateId = id, title = "Update " + id[^1], resultCode = 2, hresult = 0 }),
            failed = failed.Select(f => new { updateId = f.Id, title = "Update " + f.Id[^1], resultCode = 4, hresult = f.Hresult }),
        });

    private async Task<Device> SeedDeviceAsync(string tag, params string[] pending)
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization(tag, ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        // An agent new enough for driver tasks, so the follow-up scan can be queued.
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1.26.0", null, token.Id, Start);
        db.Devices.Add(device);

        foreach (var id in pending)
        {
            db.DeviceDriverUpdates.Add(new DeviceDriverUpdate(
                device.Id, id, 1, "Update " + id[^1], "Display", "Vendor", null, false, 100, Start));
        }

        await db.SaveChangesAsync();

        // Booted well before anything in these tests happens.
        await db.Devices.Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBootAt, Start.AddHours(-5)));

        return device;
    }

    private async Task SetBootAsync(Guid deviceId, DateTimeOffset bootedAt)
    {
        await using var db = _fixture.CreateDbContext();
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBootAt, bootedAt));
    }

    /// <summary>Queues and delivers an install as the agent poll would, at <paramref name="at"/>.</summary>
    private async Task<DeviceTask> DeliverInstallAsync(Device device, DateTimeOffset at, params string[] ids)
    {
        await using var db = _fixture.CreateDbContext();
        var task = DeviceTask.Create(
            device.OrganizationId, device.Id, DeviceTaskType.InstallDriverUpdate, Payload(ids),
            Requester, RequesterDisplay, at, TimeSpan.FromHours(1));
        task.TryDeliver(at).ShouldBeTrue();
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private async Task<bool> CompleteAsync(DateTimeOffset at, Guid deviceId, Guid taskId, bool succeeded, string? json)
    {
        var clock = new TestClock(at);
        await using var db = _fixture.CreateDbContext();
        var service = new DeviceTaskService(db, Audit(db, clock), clock, NullLogger<DeviceTaskService>.Instance);
        return await service.CompleteAsync(deviceId, taskId, succeeded, "done", json);
    }

    /// <summary>Delivers the device's queued follow-up scan at <paramref name="at"/> and posts a result for it.</summary>
    private async Task CompleteQueuedScanAsync(Guid deviceId, DateTimeOffset at, string scanJson)
    {
        Guid scanId;
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = await db.DeviceTasks.SingleAsync(t =>
                t.DeviceId == deviceId && t.Type == DeviceTaskType.ScanDriverUpdates && t.Status == DeviceTaskStatus.Queued);
            scan.TryDeliver(at).ShouldBeTrue();
            await db.SaveChangesAsync();
            scanId = scan.Id;
        }

        (await CompleteAsync(at.AddMinutes(1), deviceId, scanId, true, scanJson)).ShouldBeTrue();
    }

    /// <summary>Queues (as an operator would) and completes a scan delivered at <paramref name="at"/>.</summary>
    private async Task ScanAsync(Device device, DateTimeOffset at, string scanJson)
    {
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanDriverUpdates, null,
                Guid.CreateVersion7(), "someone@test", at, TimeSpan.FromMinutes(30));
            db.DeviceTasks.Add(scan);
            await db.SaveChangesAsync();
        }

        await CompleteQueuedScanAsync(device.Id, at, scanJson);
    }

    private async Task<Dictionary<string, DeviceDriverUpdateInstallRecord>> RecordsAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceDriverUpdateInstallRecords.AsNoTracking()
            .Where(r => r.DeviceId == deviceId)
            .ToDictionaryAsync(r => r.UpdateId);
    }

    private async Task<List<DeviceTask>> ScansAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ScanDriverUpdates)
            .ToListAsync();
    }

    private async Task<List<string>> PendingAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceDriverUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .OrderBy(u => u.UpdateId)
            .Select(u => u.UpdateId)
            .ToListAsync();
    }

    // ---- legacy results (agents 1.21.0-1.26.0) -------------------------------

    [Fact]
    public async Task A_legacy_result_with_the_reboot_flag_is_remembered_and_followed_by_one_scan_for_the_requester()
    {
        var device = await SeedDeviceAsync("DI1", A, B, C);
        var install = await DeliverInstallAsync(device, Start, A, B, C);

        (await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true,
            Legacy(rebootRequired: true, [A, B], (C, unchecked((int)0x80240017))))).ShouldBeTrue();

        var records = await RecordsAsync(device.Id);
        records.Count.ShouldBe(3);
        records[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot, "the aggregate reboot flag carves out every installed update");
        records[B].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        records[C].Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        records[C].LastHresult.ShouldBe(unchecked((int)0x80240017));
        records[C].LastResultCode.ShouldBe(4);
        records[A].Attempts.ShouldBe(1);
        records[A].LastTaskId.ShouldBe(install.Id);
        records[A].Revision.ShouldBe(1, "the revision that was pending when the install ran");
        records[A].LastAttemptAt.ShouldBe(install.DeliveredAt!.Value, tolerance: TimeSpan.FromMilliseconds(1));

        // The pending list is the pre-install list until a scan says otherwise --
        // it is not judged against, and it is not touched.
        (await PendingAsync(device.Id)).ShouldBe(new[] { A, B, C });

        var scan = (await ScansAsync(device.Id)).ShouldHaveSingleItem();
        scan.Status.ShouldBe(DeviceTaskStatus.Queued);
        scan.CreatedByUserId.ShouldBe(Requester);
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);

        await using var db = _fixture.CreateDbContext();
        (await db.AuditLogEntries.AsNoTracking()
            .AnyAsync(a => a.Action == "task.queue.scandriverupdates"
                           && a.ActorDisplay == RequesterDisplay + DeviceTaskService.AutoRescanSuffix))
            .ShouldBeTrue("the auto-rescan is audited like any queued task, attributed to the install's requester");
    }

    [Fact]
    public async Task A_reboot_pending_install_still_offered_before_a_restart_is_never_branded_re_offered()
    {
        var device = await SeedDeviceAsync("DI2", A, B);
        var install = await DeliverInstallAsync(device, Start, A, B);
        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(rebootRequired: true, [A, B]));

        // The follow-up scan runs before any restart and still sees both offered.
        await CompleteQueuedScanAsync(device.Id, Start.AddMinutes(6), ScanJson((A, 1), (B, 1)));

        var before = await RecordsAsync(device.Id);
        before[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        before[B].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);

        // The machine restarts; the scan after it sees A offered again and B gone.
        await SetBootAsync(device.Id, Start.AddHours(1));
        await ScanAsync(device, Start.AddHours(1).AddMinutes(2), ScanJson((A, 1)));

        var after = await RecordsAsync(device.Id);
        after[A].Status.ShouldBe(DriverUpdateInstallStatus.ReOffered, "it survived a restart");
        after[B].Status.ShouldBe(DriverUpdateInstallStatus.Installed);
        after[B].ConfirmedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_legacy_install_without_the_reboot_flag_is_unverified_until_the_follow_up_scan_answers()
    {
        var device = await SeedDeviceAsync("DI3", A, B);
        var install = await DeliverInstallAsync(device, Start, A, B);

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(rebootRequired: false, [A, B]));

        var records = await RecordsAsync(device.Id);
        records[A].Status.ShouldBe(DriverUpdateInstallStatus.InstalledUnverified,
            "the pre-install list still lists it, and must not be read as 're-offered'");
        records[B].Status.ShouldBe(DriverUpdateInstallStatus.InstalledUnverified);

        await CompleteQueuedScanAsync(device.Id, Start.AddMinutes(6), ScanJson((A, 1)));

        var after = await RecordsAsync(device.Id);
        after[A].Status.ShouldBe(DriverUpdateInstallStatus.ReOffered, "no restart owed, and it came straight back");
        after[B].Status.ShouldBe(DriverUpdateInstallStatus.Installed);
        after[B].ConfirmedAt.ShouldNotBeNull();
        (await PendingAsync(device.Id)).ShouldBe(new[] { A });
    }

    [Fact]
    public async Task A_failed_install_result_is_remembered_too()
    {
        var device = await SeedDeviceAsync("DI4", A);
        var install = await DeliverInstallAsync(device, Start, A);

        (await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, succeeded: false,
            Legacy(rebootRequired: false, [], (A, -2145124329)))).ShouldBeTrue();

        var record = (await RecordsAsync(device.Id))[A];
        record.Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        record.LastHresult.ShouldBe(-2145124329);
    }

    [Fact]
    public async Task Every_attempt_is_counted()
    {
        var device = await SeedDeviceAsync("DI5", A);

        var first = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, first.Id, false, Legacy(false, [], (A, -2145124329)));

        var second = await DeliverInstallAsync(device, Start.AddMinutes(10), A);
        await CompleteAsync(Start.AddMinutes(14), device.Id, second.Id, true, Legacy(true, [A]));

        var record = (await RecordsAsync(device.Id))[A];
        record.Attempts.ShouldBe(2);
        record.Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        record.LastTaskId.ShouldBe(second.Id);
        record.LastHresult.ShouldBe(0);
    }

    // ---- 1.27.0 results ---------------------------------------------------------

    [Fact]
    public async Task A_classified_result_with_a_verification_list_refreshes_the_pending_list_by_itself()
    {
        var device = await SeedDeviceAsync("DI6", A, B, C, D, E);
        var install = await DeliverInstallAsync(device, Start, A, B, C, D, E);

        var json = JsonSerializer.Serialize(new
        {
            rebootRequired = true,
            installed = new[] { new { updateId = A, title = "Update 1", resultCode = 2, hresult = 0 } },
            failed = new[] { new { updateId = D, title = "Update 4", resultCode = 4, hresult = -2145124329 } },
            outcomes = new object[]
            {
                new { updateId = A, title = "Update 1", resultCode = 2, hresult = 0, rebootRequired = false, status = "Installed" },
                new { updateId = B, title = "Update 2", resultCode = 2, hresult = 0, rebootRequired = true, status = "PendingReboot" },
                new { updateId = C, title = "Update 3", resultCode = 2, hresult = 0, rebootRequired = false, status = "ReOffered" },
                // Unsigned on the wire: still the same HRESULT.
                new { updateId = D, title = "Update 4", resultCode = 4, hresult = 2149842967L, rebootRequired = false, status = "Failed" },
                new { updateId = E, title = "Update 5", resultCode = (int?)null, hresult = (long?)null, rebootRequired = false, status = "NotOffered" },
            },
            verified = true,
            postScan = ScanObject(B, C, D, F),
        });

        (await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, json)).ShouldBeTrue();

        var records = await RecordsAsync(device.Id);
        records[A].Status.ShouldBe(DriverUpdateInstallStatus.Installed);
        records[A].ConfirmedAt.ShouldNotBeNull("gone from the verification list: confirmed in this one round trip");
        records[B].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        records[C].Status.ShouldBe(DriverUpdateInstallStatus.ReOffered);
        records[D].Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        records[D].LastHresult.ShouldBe(unchecked((int)0x80240017));
        records.ShouldNotContainKey(E, "nothing reached the installer; there is no attempt to remember");

        (await PendingAsync(device.Id)).ShouldBe(new[] { B, C, D, F }, ignoreOrder: false); // the verification list replaced the pending list
        (await ScansAsync(device.Id)).ShouldBeEmpty("the result already carried the truth; no follow-up scan is needed");
    }

    [Fact]
    public async Task An_outcome_that_needs_a_restart_is_never_remembered_as_re_offered()
    {
        var device = await SeedDeviceAsync("DI7", A);
        var install = await DeliverInstallAsync(device, Start, A);

        var json = JsonSerializer.Serialize(new
        {
            rebootRequired = true,
            installed = Array.Empty<object>(),
            failed = Array.Empty<object>(),
            outcomes = new[] { new { updateId = A, title = "Update 1", resultCode = 2, hresult = 0, rebootRequired = true, status = "ReOffered" } },
            verified = true,
            postScan = ScanObject(A),
        });

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, json);

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
    }

    [Fact]
    public async Task A_reboot_pending_install_gone_from_the_list_keeps_saying_a_restart_is_owed_until_one_happens()
    {
        var device = await SeedDeviceAsync("DI8", A);
        var install = await DeliverInstallAsync(device, Start, A);

        var json = JsonSerializer.Serialize(new
        {
            rebootRequired = true,
            outcomes = new[] { new { updateId = A, title = "Update 1", resultCode = 2, hresult = 0, rebootRequired = true, status = "PendingReboot" } },
            verified = true,
            postScan = ScanObject(),
        });
        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, json);

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot,
            "Windows wants a restart to finish it; the console must keep saying so");

        await SetBootAsync(device.Id, Start.AddHours(1));
        await ScanAsync(device, Start.AddHours(1).AddMinutes(2), ScanJson());

        var after = (await RecordsAsync(device.Id))[A];
        after.Status.ShouldBe(DriverUpdateInstallStatus.Installed);
        after.ConfirmedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_late_result_does_not_count_a_restart_that_happened_after_its_verification_list()
    {
        var device = await SeedDeviceAsync("DI9", A);
        var install = await DeliverInstallAsync(device, Start, A);

        // The device restarted at +30 min; its spooled result (with a verification
        // list taken right after the install, before that restart) lands at +1 h.
        await SetBootAsync(device.Id, Start.AddMinutes(30));

        var json = JsonSerializer.Serialize(new
        {
            rebootRequired = true,
            outcomes = new[] { new { updateId = A, title = "Update 1", resultCode = 2, hresult = 0, rebootRequired = true, status = "PendingReboot" } },
            verified = true,
            postScan = ScanObject(A),
        });
        await CompleteAsync(Start.AddHours(1), device.Id, install.Id, true, json);

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot,
            "the list it carries predates the restart, so it cannot say what survived it");
    }

    // ---- reconcile details ---------------------------------------------------------

    [Fact]
    public async Task A_newer_revision_of_an_installed_update_is_a_new_offer_not_a_re_offer()
    {
        var device = await SeedDeviceAsync("DI10", A);
        var install = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(false, [A]));
        await CompleteQueuedScanAsync(device.Id, Start.AddMinutes(6), ScanJson());
        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.Installed);

        await ScanAsync(device, Start.AddDays(10), ScanJson((A, 2)));

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.Installed);
    }

    [Fact]
    public async Task An_installed_update_offered_again_at_the_same_revision_is_re_offered()
    {
        var device = await SeedDeviceAsync("DI11", A);
        var install = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(false, [A]));
        await CompleteQueuedScanAsync(device.Id, Start.AddMinutes(6), ScanJson());

        await ScanAsync(device, Start.AddDays(1), ScanJson((A, 1)));

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.ReOffered, "it came back");
    }

    // ---- follow-up scans and malformed input -----------------------------------------

    [Fact]
    public async Task The_follow_up_scan_is_deduplicated_against_one_already_queued_or_running()
    {
        var device = await SeedDeviceAsync("DI12", A, B);

        var first = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, first.Id, true, Legacy(false, [A]));

        var second = await DeliverInstallAsync(device, Start.AddMinutes(5), B);
        await CompleteAsync(Start.AddMinutes(8), device.Id, second.Id, true, Legacy(false, [B]));

        (await ScansAsync(device.Id)).Count.ShouldBe(1);

        // Delivered (running on the device) counts as in flight too -- when it was
        // claimed after the install it has to observe. (One claimed before that
        // install does not: see the stale-scan test below.)
        var third = await DeliverInstallAsync(device, Start.AddMinutes(10), A);
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = await db.DeviceTasks.SingleAsync(t => t.DeviceId == device.Id && t.Type == DeviceTaskType.ScanDriverUpdates);
            scan.TryDeliver(Start.AddMinutes(11)).ShouldBeTrue();
            await db.SaveChangesAsync();
        }

        await CompleteAsync(Start.AddMinutes(12), device.Id, third.Id, true, Legacy(false, [A]));

        (await ScansAsync(device.Id)).Count.ShouldBe(1);
    }

    /// <summary>Queues and delivers an operator scan at <paramref name="at"/>, leaving it Delivered.</summary>
    private async Task<DeviceTask> DeliverScanAsync(Device device, DateTimeOffset at)
    {
        await using var db = _fixture.CreateDbContext();
        var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanDriverUpdates, null,
            Guid.CreateVersion7(), "someone@test", at, TimeSpan.FromMinutes(30));
        scan.TryDeliver(at).ShouldBeTrue();
        db.DeviceTasks.Add(scan);
        await db.SaveChangesAsync();
        return scan;
    }

    // F3: a Delivered scan only stands in for the follow-up if it was claimed after
    // the point that needs observing; one claimed before (and killed) does not.
    [Theory]
    [InlineData(-5)] // claimed before the install, then killed (e.g. the agent update restarting the service)
    [InlineData(0)]  // claimed in the same batch as the install: nothing says it ran after it
    public async Task A_scan_claimed_before_the_install_ran_does_not_stand_in_for_its_follow_up(int scanClaimedMinutes)
    {
        var device = await SeedDeviceAsync($"DI24{scanClaimedMinutes}", A);
        var stale = await DeliverScanAsync(device, Start.AddMinutes(scanClaimedMinutes));
        var install = await DeliverInstallAsync(device, Start, A);

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(rebootRequired: false, [A]));

        var scans = await ScansAsync(device.Id);
        scans.Count.ShouldBe(2, "the still-'Delivered' scan could not have seen the install");
        var followUp = scans.Single(s => s.Id != stale.Id);
        followUp.Status.ShouldBe(DeviceTaskStatus.Queued);
        followUp.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);
    }

    // F1(a): the result lands after the restart; the heartbeat that saw the restart
    // came first and found nothing reboot-pending.
    [Fact]
    public async Task A_reboot_pending_result_that_lands_after_the_restart_queues_a_scan_from_after_it()
    {
        var device = await SeedDeviceAsync("DI21", A);
        var install = await DeliverInstallAsync(device, Start, A);

        // Restarted at +30 min; the spooled result -- reboot-pending, with a
        // verification list taken before that restart -- lands at +1 h.
        await SetBootAsync(device.Id, Start.AddMinutes(30));
        (await CompleteAsync(Start.AddHours(1), device.Id, install.Id, true,
            Classified([Outcome(A, "PendingReboot", rebootRequired: true)], postScan: ScanObject(A)))).ShouldBeTrue();

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        var scan = (await ScansAsync(device.Id)).ShouldHaveSingleItem(
            "only a scan taken after the restart can settle it; without one it says 'Needs restart' forever");
        scan.Status.ShouldBe(DeviceTaskStatus.Queued);
        scan.CreatedByUserId.ShouldBe(Requester);
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);

        // That scan runs after the restart and settles the record.
        await CompleteQueuedScanAsync(device.Id, Start.AddHours(1).AddMinutes(1), ScanJson());
        var settled = (await RecordsAsync(device.Id))[A];
        settled.Status.ShouldBe(DriverUpdateInstallStatus.Installed);
        settled.ConfirmedAt.ShouldNotBeNull();
    }

    // F1(a) + F3: the follow-up for a late reboot-pending result must observe after
    // the restart, so a scan claimed before the restart (and killed by it) is no stand-in.
    [Fact]
    public async Task A_late_reboot_pending_result_is_not_satisfied_by_a_scan_the_restart_killed()
    {
        var device = await SeedDeviceAsync("DI22", A);
        var install = await DeliverInstallAsync(device, Start, A);

        // An operator's scan was claimed at +10 min and died with the restart at +30.
        var killed = await DeliverScanAsync(device, Start.AddMinutes(10));
        await SetBootAsync(device.Id, Start.AddMinutes(30));

        await CompleteAsync(Start.AddHours(1), device.Id, install.Id, true, Legacy(rebootRequired: true, [A]));

        var scans = await ScansAsync(device.Id);
        scans.Count.ShouldBe(2);
        scans.Single(s => s.Id != killed.Id).Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    // F2(b): a retry the installer refused (no result code) must not erase "needs restart".
    [Fact]
    public async Task A_failure_that_never_reached_the_installer_does_not_erase_a_pending_restart()
    {
        var device = await SeedDeviceAsync("DI23", A);
        var first = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, first.Id, true,
            Classified([Outcome(A, "PendingReboot", rebootRequired: true)], postScan: ScanObject(A)));

        // Retried before the restart: Windows Update will not start an install while
        // a restart is owed, so nothing reaches the installer (no result code).
        var retry = await DeliverInstallAsync(device, Start.AddMinutes(10), A);
        (await CompleteAsync(Start.AddMinutes(11), device.Id, retry.Id, false, Classified(
            [Outcome(A, "Failed", resultCode: null, hresult: unchecked((int)0x80240016))],
            postScan: null,
            verified: false))).ShouldBeTrue();

        var record = (await RecordsAsync(device.Id))[A];
        record.Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot, "the restart still finishes the first install");
        record.Attempts.ShouldBe(1, "an attempt that never reached the installer is not counted");
        record.LastTaskId.ShouldBe(first.Id);
        record.LastHresult.ShouldBe(0);
        record.LastAttemptAt.ShouldBe(first.DeliveredAt!.Value, tolerance: TimeSpan.FromMilliseconds(1));
    }

    // F6: a scan claimed in the same batch as an install (same claim time) never
    // judges that install -- nothing says it ran after it.
    [Fact]
    public async Task A_scan_claimed_in_the_same_batch_as_an_install_never_rolls_its_result_back()
    {
        var device = await SeedDeviceAsync("DI25", A);
        var scan = await DeliverScanAsync(device, Start);
        var install = await DeliverInstallAsync(device, Start, A);

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true,
            Classified([Outcome(A, "Installed")], postScan: ScanObject()));
        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.Installed,
            "the install's own verification list judges it at the equal time");

        // The scan ran first in that batch; its spooled result lands afterwards and
        // still lists A, as it was before the install.
        (await CompleteAsync(Start.AddMinutes(6), device.Id, scan.Id, true, ScanJson((A, 1)))).ShouldBeTrue();

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.Installed,
            "a list from the same claim instant is not evidence the install came back");
    }

    // F9: Installed -> ReOffered only within the window after confirmation.
    [Fact]
    public async Task An_installed_update_offered_again_long_after_it_was_confirmed_is_a_fresh_offer()
    {
        var device = await SeedDeviceAsync("DI26", A);
        var install = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(false, [A]));
        await CompleteQueuedScanAsync(device.Id, Start.AddMinutes(6), ScanJson());
        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.Installed);

        // A month on (a rollback, a reimage) Windows offers it again at the same revision.
        await ScanAsync(device, Start.AddDays(30), ScanJson((A, 1)));

        (await RecordsAsync(device.Id)).ShouldNotContainKey(A,
            "a month later it is a fresh offer: the stale memory goes, so install-all and the console treat it as actionable");
        (await PendingAsync(device.Id)).ShouldBe(new[] { A });
    }

    [Fact]
    public async Task An_installed_update_offered_again_inside_the_window_is_still_re_offered()
    {
        var device = await SeedDeviceAsync("DI27", A);
        var install = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(false, [A]));
        await CompleteQueuedScanAsync(device.Id, Start.AddMinutes(6), ScanJson());

        await ScanAsync(device, Start.AddDays(6), ScanJson((A, 1)));

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.ReOffered);
    }

    [Fact]
    public async Task A_malformed_result_completes_the_task_and_leaves_the_install_memory_and_pending_list_untouched()
    {
        var device = await SeedDeviceAsync("DI13", A);
        var earlier = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, earlier.Id, false, Legacy(false, [], (A, 5)));

        var install = await DeliverInstallAsync(device, Start.AddMinutes(10), A);
        // Valid JSON (the result column is jsonb) of the wrong shape: what a buggy or
        // hostile agent could actually get past the database.
        (await CompleteAsync(Start.AddMinutes(14), device.Id, install.Id, true,
                """{ "outcomes": "not-a-list", "postScan": { "updates": 7 } }"""))
            .ShouldBeTrue("a malformed body must never fail the completion that carried it");

        var record = (await RecordsAsync(device.Id))[A];
        record.Attempts.ShouldBe(1);
        record.Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        record.LastTaskId.ShouldBe(earlier.Id);
        (await PendingAsync(device.Id)).ShouldBe(new[] { A });

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.SingleAsync(t => t.Id == install.Id)).Status.ShouldBe(DeviceTaskStatus.Succeeded);
    }

    [Fact]
    public async Task A_device_too_old_for_the_scan_simply_gets_none()
    {
        var device = await SeedDeviceAsync("DI14", A);
        await using (var db = _fixture.CreateDbContext())
        {
            await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.AgentVersion, "1.20.0"));
        }

        var install = await DeliverInstallAsync(device, Start, A);
        (await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Legacy(false, [A]))).ShouldBeTrue();

        (await ScansAsync(device.Id)).ShouldBeEmpty();
        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.InstalledUnverified);
    }

    // ---- what agent 1.27.0 actually emits ----------------------------------------------

    private static string Classified(object[] outcomes, object? postScan, bool verified = true) =>
        JsonSerializer.Serialize(new
        {
            rebootRequired = false,
            installed = Array.Empty<object>(),
            failed = Array.Empty<object>(),
            outcomes,
            verified,
            postScan,
        });

    private static object Outcome(string id, string status, int? resultCode = 2, long? hresult = 0,
        bool rebootRequired = false, string? title = null) =>
        new { updateId = id, title = title ?? "Update " + id[^1], resultCode, hresult, rebootRequired, status };

    [Fact]
    public async Task A_placeholder_title_never_replaces_the_real_one()
    {
        var device = await SeedDeviceAsync("DI15", A, B);

        // First attempt: a real record with the pending row's title.
        var first = await DeliverInstallAsync(device, Start, A);
        await CompleteAsync(Start.AddMinutes(4), device.Id, first.Id, false,
            Classified([Outcome(A, "Failed", 4, -2145124329)], postScan: null));

        // Later the pending list no longer has A, and the agent names A and B with
        // placeholder titles -- A as a failure that never reached the installer, B
        // as not offered.
        await CompleteQueuedScanAsync(device.Id, Start.AddMinutes(10), ScanJson((B, 1)));
        var second = await DeliverInstallAsync(device, Start.AddMinutes(20), A, B);
        await CompleteAsync(Start.AddMinutes(24), device.Id, second.Id, false, Classified(
            [
                Outcome(A, "Failed", resultCode: null, hresult: null, title: "(not offered by Windows Update)"),
                Outcome(B, "NotOffered", resultCode: null, hresult: null, title: "(not offered by Windows Update)"),
            ],
            postScan: null,
            verified: false));

        var records = await RecordsAsync(device.Id);
        records[A].Title.ShouldBe("Update 1", "a stored real title is never overwritten by an agent placeholder");
        records[A].Attempts.ShouldBe(2);
        records.ShouldNotContainKey(B, "not offered: nothing reached the installer, so there is no attempt to remember");
    }

    [Fact]
    public async Task A_new_record_takes_the_pending_rows_title_over_the_agents()
    {
        var device = await SeedDeviceAsync("DI16", A);
        var install = await DeliverInstallAsync(device, Start, A);

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true,
            Classified([Outcome(A, "PendingReboot", title: "(something else)")], postScan: null));

        (await RecordsAsync(device.Id))[A].Title.ShouldBe("Update 1");
    }

    [Fact]
    public async Task Reboot_pending_is_keyed_on_status_not_on_the_items_own_reboot_flag()
    {
        var device = await SeedDeviceAsync("DI17", A);
        var install = await DeliverInstallAsync(device, Start, A);

        // Filed under PendingReboot by the system-wide flag; the update's own flag is false.
        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true,
            Classified([Outcome(A, "PendingReboot", rebootRequired: false)], postScan: ScanObject(A)));

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
    }

    [Fact]
    public async Task A_failure_that_never_reached_the_installer_is_stored_with_the_codes_present()
    {
        var device = await SeedDeviceAsync("DI18", A, B);
        var install = await DeliverInstallAsync(device, Start, A, B);

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, false, Classified(
            [
                Outcome(A, "Failed", resultCode: null, hresult: unchecked((int)0x80240016)),
                Outcome(B, "Failed", resultCode: null, hresult: null),
            ],
            postScan: null,
            verified: false));

        var records = await RecordsAsync(device.Id);
        records[A].Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        records[A].LastResultCode.ShouldBeNull();
        records[A].LastHresult.ShouldBe(unchecked((int)0x80240016));
        records[B].Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        records[B].LastHresult.ShouldBeNull();
    }

    [Fact]
    public async Task An_install_that_could_not_run_is_treated_as_having_no_verification_list()
    {
        var device = await SeedDeviceAsync("DI19", A);
        var install = await DeliverInstallAsync(device, Start, A);

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, false, Classified(
            [Outcome(A, "Failed", resultCode: null, hresult: unchecked((int)0x8024001E))],
            postScan: null,
            verified: false));

        (await RecordsAsync(device.Id))[A].Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        (await PendingAsync(device.Id)).ShouldBe(new[] { A }, ignoreOrder: false);

        var scan = (await ScansAsync(device.Id)).ShouldHaveSingleItem("no fresh list arrived, so the server asks for one");
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);
    }

    [Fact]
    public async Task A_reboot_pending_batch_settles_per_update_after_the_restart()
    {
        // The agent files every successful item of a batch that needs a restart
        // under PendingReboot. After the restart, the one Windows no longer offers is
        // installed; the one it still offers survived the restart and is re-offered.
        var device = await SeedDeviceAsync("DI20", A, B);
        var install = await DeliverInstallAsync(device, Start, A, B);

        await CompleteAsync(Start.AddMinutes(4), device.Id, install.Id, true, Classified(
            [Outcome(A, "PendingReboot", rebootRequired: true), Outcome(B, "PendingReboot", rebootRequired: false)],
            postScan: ScanObject(A, B)));

        var before = await RecordsAsync(device.Id);
        before[A].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        before[B].Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        (await ScansAsync(device.Id)).ShouldBeEmpty();

        await SetBootAsync(device.Id, Start.AddHours(2));
        await ScanAsync(device, Start.AddHours(2).AddMinutes(1), ScanJson((B, 1)));

        var after = await RecordsAsync(device.Id);
        after[A].Status.ShouldBe(DriverUpdateInstallStatus.Installed);
        after[A].ConfirmedAt.ShouldNotBeNull();
        after[B].Status.ShouldBe(DriverUpdateInstallStatus.ReOffered);
    }
}
