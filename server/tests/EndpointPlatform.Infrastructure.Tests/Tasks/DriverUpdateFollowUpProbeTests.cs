using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Tasks;

file sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private readonly DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// The heartbeat's follow-up probe, on a controlled clock: which open questions it
/// asks a scan to answer, how long a scan that was asked for holds the next one
/// back, and when it gives a question up. Also: a database blip while queueing a
/// follow-up never undoes or fails the work that prompted it.
/// </summary>
/// <remarks>
/// <para>The three questions, each only within <see cref="DeviceTaskService.FollowUpLookback"/>:</para>
/// <list type="bullet">
/// <item>(a) a reboot-pending install attempted before the current boot, and no
/// successful scan claimed after that boot;</item>
/// <item>(b) a driver install that expired after delivery, and no successful scan
/// claimed after that delivery;</item>
/// <item>(c) an install remembered as unverified, and no successful scan claimed
/// after its attempt.</item>
/// </list>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DriverUpdateFollowUpProbeTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private readonly PostgresFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.CreateVersion7();
    private const string RequesterDisplay = "operator@test";
    private const string U1 = "cccccccc-0000-0000-0000-000000000001";
    private const string U2 = "cccccccc-0000-0000-0000-000000000002";

    private static AuditWriter Audit(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static DeviceTaskService Service(EndpointPlatformDbContext db, TimeProvider clock) =>
        new(db, Audit(db, clock), clock, NullLogger<DeviceTaskService>.Instance);

    private static string Payload(params string[] ids) =>
        JsonSerializer.Serialize(new { updateIds = ids, issuedAt = Now });

    private async Task<Device> SeedDeviceAsync(string tag, DateTimeOffset? bootedAt, DateTimeOffset? enrolledAt = null)
    {
        var enrolled = enrolledAt ?? Now.AddDays(-60);
        await using var db = _fixture.CreateDbContext();
        var org = new Organization(tag, ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", enrolled.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1.26.0", null, token.Id, enrolled);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        await db.Devices.Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBootAt, bootedAt));
        return device;
    }

    /// <summary>
    /// An install claimed at <paramref name="attemptedAt"/> that reported back, and the
    /// memory it left for one update.
    /// </summary>
    private async Task<Guid> AddInstallAsync(
        Device device, DateTimeOffset attemptedAt, DriverUpdateInstallStatus remembered,
        Guid? requester = null, string updateId = U1)
    {
        await using var db = _fixture.CreateDbContext();
        var install = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.InstallDriverUpdate,
            Payload(updateId), requester ?? Requester, RequesterDisplay, attemptedAt.AddMinutes(-1), TimeSpan.FromHours(1));
        install.TryDeliver(attemptedAt).ShouldBeTrue();
        install.TryComplete(true, "Installed 1 driver update(s).", null, attemptedAt.AddMinutes(3)).ShouldBeTrue();
        db.DeviceTasks.Add(install);

        var record = new DeviceDriverUpdateInstallRecord(device.Id, updateId, 1, "Update", attemptedAt);
        record.RecordAttempt(remembered, 2, 0, install.Id, null, 1, attemptedAt);
        db.DeviceDriverUpdateInstallRecords.Add(record);

        await db.SaveChangesAsync();
        return install.Id;
    }

    /// <summary>A driver install the device claimed at <paramref name="deliveredAt"/> and then went silent on.</summary>
    private async Task<Guid> AddSilentInstallAsync(Device device, DateTimeOffset deliveredAt)
    {
        await using var db = _fixture.CreateDbContext();
        var install = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.InstallDriverUpdate,
            Payload(U2), Requester, RequesterDisplay, deliveredAt.AddMinutes(-1), TimeSpan.FromHours(1));
        install.TryDeliver(deliveredAt).ShouldBeTrue();
        install.TryExpire(deliveredAt.AddHours(1)).ShouldBeTrue();
        db.DeviceTasks.Add(install);
        await db.SaveChangesAsync();
        return install.Id;
    }

    public enum ScanEnd
    {
        /// <summary>Queued, or (when claimed) still Delivered.</summary>
        StillOpen,
        Succeeded,
        Failed,
        Expired,
    }

    /// <summary>
    /// A driver-update scan created at <paramref name="createdAt"/>, optionally claimed
    /// at <paramref name="deliveredAt"/>, and settled at <paramref name="endedAt"/>.
    /// </summary>
    private async Task<Guid> AddScanAsync(
        Device device, DateTimeOffset createdAt, DateTimeOffset? deliveredAt = null,
        ScanEnd end = ScanEnd.StillOpen, DateTimeOffset? endedAt = null)
    {
        await using var db = _fixture.CreateDbContext();
        var scan = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.ScanDriverUpdates, null,
            Requester, RequesterDisplay + DeviceTaskService.AutoRescanSuffix, createdAt, TimeSpan.FromMinutes(30));
        if (deliveredAt is { } claimed)
        {
            scan.TryDeliver(claimed).ShouldBeTrue();
        }

        var ended = endedAt ?? (deliveredAt ?? createdAt).AddMinutes(1);
        switch (end)
        {
            case ScanEnd.Succeeded:
                scan.TryComplete(true, "Found 0 driver update(s).", """{"updates":[]}""", ended).ShouldBeTrue();
                break;
            case ScanEnd.Failed:
                scan.TryComplete(false, "Windows Update failed.", null, ended).ShouldBeTrue();
                break;
            case ScanEnd.Expired:
                // A claimed scan ran under its execution grace until the sweeper expired it.
                scan.ExtendForExecution(ended);
                scan.TryExpire(ended).ShouldBeTrue();
                break;
        }

        db.DeviceTasks.Add(scan);
        await db.SaveChangesAsync();

        // The audit interceptor stamps "now"; put the row where the scenario has it.
        await db.DeviceTasks.Where(t => t.Id == scan.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, createdAt));
        return scan.Id;
    }

    private async Task<DeviceTask?> ProbeAsync(Device device, DateTimeOffset at, IInterceptor[]? interceptors = null)
    {
        var clock = new TestClock(at);
        await using var db = _fixture.CreateDbContext(clock, extraInterceptors: interceptors);
        return await Service(db, clock).QueueDriverFollowUpScanIfNeededAsync(device.OrganizationId, device.Id);
    }

    private async Task<List<DeviceTask>> ScansAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ScanDriverUpdates)
            .ToListAsync();
    }

    private async Task<DeviceDriverUpdateInstallRecord> RecordAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceDriverUpdateInstallRecords.AsNoTracking().SingleAsync(r => r.DeviceId == deviceId);
    }

    private async Task<DeviceTask> TaskAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    // ---- M1: the half-hour hold runs from when the last scan ENDED --------------

    // A follow-up scan that ran long (Windows Update slow, then failed or expired
    // under its execution grace) was created more than half an hour ago, so the old
    // hold -- measured from its creation -- let the probe queue the next one the
    // moment it ended: back-to-back scans. The hold now runs from the latest of its
    // creation, its claim and its end.
    [Theory]
    [InlineData(ScanEnd.Failed)]
    [InlineData(ScanEnd.Expired)]
    public async Task A_follow_up_scan_that_ran_long_holds_the_next_one_for_half_an_hour_after_it_ended(ScanEnd end)
    {
        var device = await SeedDeviceAsync($"FP1-{end}", bootedAt: Now.AddHours(-3));
        await AddInstallAsync(device, Now.AddHours(-5), DriverUpdateInstallStatus.PendingReboot);

        // Asked for 70 minutes ago (after the restart), claimed 65 minutes ago, ended 5 minutes ago.
        var ranLong = await AddScanAsync(device, Now.AddMinutes(-70), deliveredAt: Now.AddMinutes(-65),
            end, endedAt: Now.AddMinutes(-5));

        (await ProbeAsync(device, Now)).ShouldBeNull("it ended five minutes ago: not asked again straight away");
        (await ScansAsync(device.Id)).ShouldHaveSingleItem().Id.ShouldBe(ranLong);

        // Half an hour after it ended, the still-open question is asked again -- once.
        var next = (await ProbeAsync(device, Now.AddMinutes(26))).ShouldNotBeNull();
        next.Status.ShouldBe(DeviceTaskStatus.Queued);
        (await ProbeAsync(device, Now.AddMinutes(27))).ShouldBeNull("the new one is pending");
        (await ScansAsync(device.Id)).Count.ShouldBe(2);
    }

    // ---- M2: an unverified install is a question too ----------------------------

    // The install result without a verification list queued one follow-up scan;
    // the device went off for longer than its half-hour lifetime and it expired
    // unclaimed. Neither the restart question nor the silent-install question
    // covers the record, and install-all skips "Verifying..." -- it stuck forever.
    [Fact]
    public async Task An_unverified_install_whose_verification_scan_expired_unclaimed_gets_a_scan_when_the_device_is_back()
    {
        var device = await SeedDeviceAsync("FP2", bootedAt: Now.AddHours(-10));
        var install = await AddInstallAsync(device, Now.AddHours(-3), DriverUpdateInstallStatus.InstalledUnverified);
        var unused = await AddScanAsync(device, Now.AddHours(-3).AddMinutes(4),
            end: ScanEnd.Expired, endedAt: Now.AddHours(-3).AddMinutes(34));

        var scan = (await ProbeAsync(device, Now)).ShouldNotBeNull();

        scan.Status.ShouldBe(DeviceTaskStatus.Queued);
        scan.CreatedByUserId.ShouldBe(Requester, "on behalf of whoever asked for the install");
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);
        (await ProbeAsync(device, Now.AddMinutes(1))).ShouldBeNull("one scan, not one per heartbeat");
        (await ScansAsync(device.Id)).Select(s => s.Id).ShouldBe(new[] { unused, scan.Id }, ignoreOrder: true);
        (await TaskAsync(install)).Status.ShouldBe(DeviceTaskStatus.Succeeded);
    }

    [Fact]
    public async Task A_scan_claimed_before_an_unverified_attempt_does_not_answer_it()
    {
        var device = await SeedDeviceAsync("FP3", bootedAt: Now.AddHours(-10));
        await AddInstallAsync(device, Now.AddHours(-3), DriverUpdateInstallStatus.InstalledUnverified);
        await AddScanAsync(device, Now.AddHours(-4).AddMinutes(-1), deliveredAt: Now.AddHours(-4), end: ScanEnd.Succeeded);

        (await ProbeAsync(device, Now)).ShouldNotBeNull("a list taken before the install cannot say whether it stuck");
    }

    [Fact]
    public async Task An_unverified_install_a_later_scan_answered_queues_nothing()
    {
        var device = await SeedDeviceAsync("FP4", bootedAt: Now.AddHours(-10));
        await AddInstallAsync(device, Now.AddHours(-3), DriverUpdateInstallStatus.InstalledUnverified);
        await AddScanAsync(device, Now.AddHours(-2).AddMinutes(-1), deliveredAt: Now.AddHours(-2), end: ScanEnd.Succeeded);

        (await ProbeAsync(device, Now)).ShouldBeNull();
    }

    // The questions fold into ONE observe point, the latest: a scan claimed after
    // the restart but before a later unverified attempt cannot answer the attempt,
    // so it must not hold the follow-up back -- and the follow-up is on behalf of
    // whoever asked for the install that defines that latest point.
    [Fact]
    public async Task Overlapping_questions_are_answered_by_one_scan_after_the_latest_point()
    {
        var device = await SeedDeviceAsync("FP5", bootedAt: Now.AddHours(-2));
        await AddInstallAsync(device, Now.AddHours(-5), DriverUpdateInstallStatus.PendingReboot, updateId: U1);
        var laterRequester = Guid.CreateVersion7();
        await AddInstallAsync(device, Now.AddHours(-1), DriverUpdateInstallStatus.InstalledUnverified,
            requester: laterRequester, updateId: U2);

        // Claimed 90 minutes ago -- after the restart, before the unverified attempt -- and still running.
        await AddScanAsync(device, Now.AddMinutes(-91), deliveredAt: Now.AddMinutes(-90));

        var scan = (await ProbeAsync(device, Now)).ShouldNotBeNull();

        scan.CreatedByUserId.ShouldBe(laterRequester);
        (await ProbeAsync(device, Now.AddMinutes(1))).ShouldBeNull();
    }

    // ---- L3/L4: every question is given up after the lookback -------------------

    [Theory]
    [InlineData(6, true)]
    [InlineData(8, false)]
    public async Task A_restart_question_is_asked_only_within_the_lookback_of_the_restart(int restartedDaysAgo, bool asked)
    {
        var device = await SeedDeviceAsync($"FP6-{restartedDaysAgo}", bootedAt: Now.AddDays(-restartedDaysAgo));
        await AddInstallAsync(device, Now.AddDays(-restartedDaysAgo - 1), DriverUpdateInstallStatus.PendingReboot);

        (await ProbeAsync(device, Now) is not null).ShouldBe(asked);

        (await RecordAsync(device.Id)).Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot,
            "given up or not, the memory keeps saying what it knows");
    }

    // A restart is new evidence however old the attempt that waits for it.
    [Fact]
    public async Task A_fresh_restart_reopens_an_old_reboot_pending_install()
    {
        var device = await SeedDeviceAsync("FP7", bootedAt: Now.AddHours(-1));
        await AddInstallAsync(device, Now.AddDays(-30), DriverUpdateInstallStatus.PendingReboot);

        (await ProbeAsync(device, Now)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(8, false)]
    public async Task A_silent_install_is_followed_up_only_within_the_lookback_of_its_delivery(int deliveredDaysAgo, bool asked)
    {
        var device = await SeedDeviceAsync($"FP8-{deliveredDaysAgo}", bootedAt: Now.AddDays(-20));
        await AddSilentInstallAsync(device, Now.AddDays(-deliveredDaysAgo));

        (await ProbeAsync(device, Now) is not null).ShouldBe(asked);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(8, false)]
    public async Task An_unverified_install_is_followed_up_only_within_the_lookback_of_its_attempt(int attemptedDaysAgo, bool asked)
    {
        var device = await SeedDeviceAsync($"FP9-{attemptedDaysAgo}", bootedAt: Now.AddDays(-20));
        await AddInstallAsync(device, Now.AddDays(-attemptedDaysAgo), DriverUpdateInstallStatus.InstalledUnverified);

        (await ProbeAsync(device, Now) is not null).ShouldBe(asked);

        (await RecordAsync(device.Id)).Status.ShouldBe(DriverUpdateInstallStatus.InstalledUnverified,
            "given up or not, the record keeps its status; the operator can still scan");
    }

    /// <summary>Records every command a context sends.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    // L4: the probe runs on every heartbeat. On an ordinary one -- a device with
    // history but no open question -- it is ONE statement, translated whole (nothing
    // evaluated client-side, which would show as further statements), and every
    // question in it is range-limited by the lookback inside the SQL itself.
    [Fact]
    public async Task An_ordinary_heartbeats_probe_is_one_range_limited_statement()
    {
        var device = await SeedDeviceAsync("FP10", bootedAt: Now.AddHours(-5));
        await AddInstallAsync(device, Now.AddDays(-20), DriverUpdateInstallStatus.Installed);
        await AddSilentInstallAsync(device, Now.AddDays(-3));
        await AddScanAsync(device, Now.AddDays(-3).AddHours(2), deliveredAt: Now.AddDays(-3).AddHours(2), end: ScanEnd.Succeeded);
        await AddScanAsync(device, Now.AddHours(-1), deliveredAt: Now.AddHours(-1), end: ScanEnd.Succeeded);

        var recorder = new CommandRecorder();
        (await ProbeAsync(device, Now, [recorder])).ShouldBeNull();

        var sql = recorder.Commands.ShouldHaveSingleItem("one round trip; client evaluation would add statements");
        _output.WriteLine(sql);

        // Each question is bounded in SQL by the lookback cutoff: (a) on the boot
        // time, (b) on the install's delivery, (c) on the attempt, and every "has a
        // scan answered it since" subquery on the scan's delivery.
        sql.ShouldContain("d.last_boot_at > @");
        sql.ShouldContain(".last_attempt_at > @");
        System.Text.RegularExpressions.Regex.Matches(sql, @"\.delivered_at > @").Count
            .ShouldBeGreaterThanOrEqualTo(4, "the silent install itself, and the scan subquery of each of the three questions");
    }

    // A probe with an open question stays server-side too: the probe, the hold, the
    // install lookup and the queue are a fixed handful of statements, never one per
    // remembered install or per task.
    [Fact]
    public async Task An_open_questions_probe_issues_a_fixed_number_of_statements()
    {
        var device = await SeedDeviceAsync("FP11", bootedAt: Now.AddHours(-2));
        for (var i = 0; i < 5; i++)
        {
            await AddSilentInstallAsync(device, Now.AddDays(-1).AddMinutes(i));
        }

        await AddInstallAsync(device, Now.AddHours(-1), DriverUpdateInstallStatus.InstalledUnverified);

        var recorder = new CommandRecorder();
        (await ProbeAsync(device, Now, [recorder])).ShouldNotBeNull();

        foreach (var command in recorder.Commands)
        {
            _output.WriteLine(command);
            _output.WriteLine("----");
        }

        // probe, hold, install lookup, the device read in QueueAsync, and its save.
        recorder.Commands.Count.ShouldBeLessThanOrEqualTo(5);
    }

    // ---- L5: a blip after the commit never fails or aborts the work -------------

    /// <summary>
    /// Once a save has committed, fails the first command whose text contains
    /// <paramref name="match"/> with something that is NOT a <see cref="DbUpdateException"/>
    /// -- a dropped connection, a timeout -- exactly once.
    /// </summary>
    private sealed class FailOnceAfterCommit(string match) : IDbCommandInterceptor, ISaveChangesInterceptor
    {
        private bool _committed;

        public int Failures { get; private set; }

        public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            _committed = true;
            return result;
        }

        public ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            _committed = true;
            return ValueTask.FromResult(result);
        }

        private void ThrowIfTarget(DbCommand command)
        {
            if (_committed && Failures == 0 && command.CommandText.Contains(match, StringComparison.Ordinal))
            {
                Failures++;
                throw new InvalidOperationException("Simulated connection failure after the commit.");
            }
        }

        public InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfTarget(command);
            return result;
        }

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTarget(command);
            return ValueTask.FromResult(result);
        }
    }

    private static string LegacyInstalled(string updateId) =>
        JsonSerializer.Serialize(new
        {
            rebootRequired = false,
            installed = new[] { new { updateId, title = "Update", resultCode = 2, hresult = 0 } },
            failed = Array.Empty<object>(),
        });

    // The install result is committed before its follow-up scan is queued. A blip
    // in the follow-up -- the dedup query or the queue itself -- used to escape as a
    // 500, and the agent then dropped a result the server had already recorded.
    [Theory]
    [InlineData("endpoint_platform.device_tasks AS")] // the "already asked?" query
    [InlineData("endpoint_platform.devices AS")] // QueueAsync's device read
    public async Task A_blip_queueing_an_install_results_follow_up_still_accepts_the_result(string failing)
    {
        var device = await SeedDeviceAsync($"FP12-{failing.Length}", bootedAt: Now.AddHours(-10));
        Guid installId;
        await using (var db = _fixture.CreateDbContext())
        {
            var install = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.InstallDriverUpdate,
                Payload(U1), Requester, RequesterDisplay, Now.AddMinutes(-11), TimeSpan.FromHours(1));
            install.TryDeliver(Now.AddMinutes(-10)).ShouldBeTrue();
            db.DeviceTasks.Add(install);
            await db.SaveChangesAsync();
            installId = install.Id;
        }

        var blip = new FailOnceAfterCommit(failing);
        var clock = new TestClock(Now);
        await using (var db = _fixture.CreateDbContext(clock, extraInterceptors: [blip]))
        {
            (await Service(db, clock).CompleteAsync(device.Id, installId, true, "Installed 1 driver update(s).",
                LegacyInstalled(U1))).ShouldBeTrue("the result was recorded; the follow-up is best effort");
        }

        blip.Failures.ShouldBe(1);
        (await TaskAsync(installId)).Status.ShouldBe(DeviceTaskStatus.Succeeded);
        (await RecordAsync(device.Id)).Status.ShouldBe(DriverUpdateInstallStatus.InstalledUnverified);
        (await ScansAsync(device.Id)).ShouldBeEmpty();

        // And the record does not stick at "Verifying...": the next heartbeat asks.
        (await ProbeAsync(device, Now.AddMinutes(1))).ShouldNotBeNull();
    }

    // The sweeper follows up each silent install after committing the expiries. A
    // blip queueing one install's scan used to throw out of the sweep, abandoning
    // the follow-ups (and expiry notes) of every install after it in the batch.
    [Fact]
    public async Task A_blip_queueing_one_silent_installs_scan_does_not_abort_the_rest_of_the_sweep()
    {
        // Pinned years before anything the sibling suites stamp, so this sweep -- which
        // is fleet-wide by design -- finds these two installs and nothing else.
        var at = new DateTimeOffset(2019, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var first = await SeedDeviceAsync("FP13a", bootedAt: at.AddDays(-1), enrolledAt: at.AddDays(-30));
        var second = await SeedDeviceAsync("FP13b", bootedAt: at.AddDays(-1), enrolledAt: at.AddDays(-30));

        async Task<Guid> ClaimedInstallAsync(Device device, DateTimeOffset claimedAt)
        {
            await using var db = _fixture.CreateDbContext();
            var install = DeviceTask.Create(device.OrganizationId, device.Id, DeviceTaskType.InstallDriverUpdate,
                Payload(U1), Requester, RequesterDisplay, claimedAt.AddMinutes(-1), TimeSpan.FromHours(1));
            install.TryDeliver(claimedAt).ShouldBeTrue();
            db.DeviceTasks.Add(install);
            await db.SaveChangesAsync();
            return install.Id;
        }

        // The first expires first, so its follow-up is the one that hits the blip.
        var firstInstall = await ClaimedInstallAsync(first, at.AddHours(-4));
        var secondInstall = await ClaimedInstallAsync(second, at.AddHours(-3));

        var blip = new FailOnceAfterCommit("endpoint_platform.devices AS");
        var clock = new TestClock(at);
        int expired;
        await using (var db = _fixture.CreateDbContext(clock, extraInterceptors: [blip]))
        {
            expired = await Service(db, clock).SweepExpiredAsync(500);
        }

        expired.ShouldBe(2);
        blip.Failures.ShouldBe(1);

        (await TaskAsync(firstInstall)).ResultMessage.ShouldBe(
            DeviceTask.ExpiredUnreportedMessage + "; " + DeviceTaskService.VerificationScanNotQueuedNote + ".");
        (await ScansAsync(first.Id)).ShouldBeEmpty();

        (await TaskAsync(secondInstall)).ResultMessage.ShouldBe(
            DeviceTask.ExpiredUnreportedMessage + "; " + DeviceTaskService.VerificationScanQueuedNote + ".");
        var scan = (await ScansAsync(second.Id)).ShouldHaveSingleItem();
        scan.Status.ShouldBe(DeviceTaskStatus.Queued);

        // Leave nothing claimable dated 2019 behind for a sibling suite's sweep to count.
        await using (var db = _fixture.CreateDbContext())
        {
            var queued = await db.DeviceTasks.SingleAsync(t => t.Id == scan.Id);
            queued.TryCancel(at, "test cleanup").ShouldBeTrue();
            await db.SaveChangesAsync();
        }
    }
}
