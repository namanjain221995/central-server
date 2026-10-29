using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// The security-update reads: the fleet overview counts and orders devices by their
/// pending updates, and the per-device list returns what one machine has waiting.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SecurityUpdateReadServiceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SecurityUpdateReadService Read(EndpointPlatformDbContext db)
    {
        var time = new FixedClock(Start);
        return new SecurityUpdateReadService(db, time, Options.Create(new AgentServerOptions()));
    }

    private static async Task<(Guid OrgId, Device Device)> SeedDeviceAsync(EndpointPlatformDbContext db, string tag)
    {
        var org = new Organization(tag, ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, Start);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (org.Id, device);
    }

    [Fact]
    public async Task The_overview_counts_pending_and_preview_per_device()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "SO1");

        db.DeviceSecurityUpdates.AddRange(
            new DeviceSecurityUpdate(device.Id, "11111111-1111-1111-1111-111111111111", 1, "A", "KB5044284", "Critical", false, 10, Start),
            new DeviceSecurityUpdate(device.Id, "22222222-2222-2222-2222-222222222222", 1, "B", "KB5043178", "Important", false, 20, Start),
            new DeviceSecurityUpdate(device.Id, "33333333-3333-3333-3333-333333333333", 1, "C (preview)", null, null, true, 30, Start));
        await db.SaveChangesAsync();

        var overview = await Read(db).GetOverviewAsync(orgId, scopedDeviceIds: null);

        overview.Summary.DevicesWithPending.ShouldBe(1);
        overview.Summary.TotalPending.ShouldBe(3);

        var row = overview.Devices.ShouldHaveSingleItem();
        row.DeviceId.ShouldBe(device.Id);
        row.PendingCount.ShouldBe(3);
        row.PreviewCount.ShouldBe(1);
        row.IsOnline.ShouldBeTrue();
        row.LastScanAt.ShouldNotBeNull();
        row.RebootPending.ShouldBeFalse();
    }

    [Fact]
    public async Task A_device_with_nothing_pending_is_absent_from_the_overview()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, _) = await SeedDeviceAsync(db, "SO2");

        var overview = await Read(db).GetOverviewAsync(orgId, scopedDeviceIds: null);

        overview.Summary.DevicesWithPending.ShouldBe(0);
        overview.Devices.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scoped_admin_sees_only_their_devices_in_the_overview()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, mine) = await SeedDeviceAsync(db, "SO3");
        var other = Device.Enroll(orgId, "SO3b", "m-" + Guid.CreateVersion7().ToString("N"), "1", null,
            (await db.EnrollmentTokens.FirstAsync(t => t.OrganizationId == orgId)).Id, Start);
        db.Devices.Add(other);
        db.DeviceSecurityUpdates.AddRange(
            new DeviceSecurityUpdate(mine.Id, "11111111-1111-1111-1111-111111111111", 1, "Mine", null, null, false, 1, Start),
            new DeviceSecurityUpdate(other.Id, "22222222-2222-2222-2222-222222222222", 1, "Theirs", null, null, false, 1, Start));
        await db.SaveChangesAsync();

        var overview = await Read(db).GetOverviewAsync(orgId, scopedDeviceIds: new[] { mine.Id });

        overview.Devices.ShouldHaveSingleItem().DeviceId.ShouldBe(mine.Id);
    }

    [Fact]
    public async Task The_per_device_list_returns_non_preview_first()
    {
        await using var db = _fixture.CreateDbContext();
        var (_, device) = await SeedDeviceAsync(db, "SO4");

        db.DeviceSecurityUpdates.AddRange(
            new DeviceSecurityUpdate(device.Id, "33333333-3333-3333-3333-333333333333", 1, "Zebra preview", null, null, true, null, Start),
            new DeviceSecurityUpdate(device.Id, "11111111-1111-1111-1111-111111111111", 1, "Alpha", "KB5044284", "Important", false, null, Start));
        await db.SaveChangesAsync();

        var list = await Read(db).GetForDeviceAsync(device.Id);

        list.Updates.Count.ShouldBe(2);
        list.Updates[0].IsPreview.ShouldBeFalse();
        list.Updates[0].Title.ShouldBe("Alpha");
        list.Updates[0].KbIds.ShouldBe("KB5044284");
        list.Updates[0].Severity.ShouldBe("Important");
        list.Updates[1].IsPreview.ShouldBeTrue();
        list.LastScanAt.ShouldNotBeNull();
        list.RebootPending.ShouldBeFalse();
    }

    // ---- install memory, hidden updates and in-flight installs ----------------------

    private const string U1 = "11111111-1111-1111-1111-111111111111";
    private const string U2 = "22222222-2222-2222-2222-222222222222";
    private const string U3 = "33333333-3333-3333-3333-333333333333";
    private const string U4 = "44444444-4444-4444-4444-444444444444";
    private const string U5 = "55555555-5555-5555-5555-555555555555";
    private const string U6 = "66666666-6666-6666-6666-666666666666";
    private const string U7 = "77777777-7777-7777-7777-777777777777";

    private static DeviceSecurityUpdateInstallRecord Record(
        Guid deviceId, string updateId, SecurityUpdateInstallStatus status, int? hresult = 0)
    {
        var record = new DeviceSecurityUpdateInstallRecord(deviceId, updateId, 1, "Update " + updateId[0], Start.AddHours(-1));
        record.RecordAttempt(status, 2, hresult, Guid.CreateVersion7(), null, 1, Start.AddHours(-1));
        return record;
    }

    [Fact]
    public async Task The_per_device_list_carries_install_state_hides_suppressed_updates_and_shows_the_install_in_flight()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "SO5");

        foreach (var id in new[] { U1, U2, U3, U4, U5, U6 })
        {
            db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(device.Id, id, 1, "Update " + id[0], "KB5044284", "Critical", false, 1, Start));
        }

        db.DeviceSecurityUpdateInstallRecords.AddRange(
            Record(device.Id, U2, SecurityUpdateInstallStatus.InstalledUnverified),
            Record(device.Id, U3, SecurityUpdateInstallStatus.PendingReboot),
            Record(device.Id, U4, SecurityUpdateInstallStatus.ReOffered),
            Record(device.Id, U5, SecurityUpdateInstallStatus.Failed, hresult: unchecked((int)0x80240017)));

        db.DeviceSecurityUpdateSuppressions.Add(new DeviceSecurityUpdateSuppression(
            device.Id, U6, "Update 6", "breaks the line-of-business app", Guid.CreateVersion7(), "operator@test"));

        // A confirmed install from two days ago and one from last month.
        var recent = Record(device.Id, U7, SecurityUpdateInstallStatus.InstalledUnverified);
        recent.ConfirmInstalled(Start.AddDays(-2));
        db.DeviceSecurityUpdateInstallRecords.Add(recent);
        var old = Record(device.Id, "88888888-8888-8888-8888-888888888888", SecurityUpdateInstallStatus.InstalledUnverified);
        old.ConfirmInstalled(Start.AddDays(-30));
        db.DeviceSecurityUpdateInstallRecords.Add(old);

        var install = Domain.Tasks.DeviceTask.Create(orgId, device.Id, Domain.Tasks.DeviceTaskType.InstallSecurityUpdate,
            $$"""{"updateIds":["{{U1}}"],"issuedAt":"2026-01-01T12:00:00+00:00"}""", Guid.CreateVersion7(), "operator@test",
            // A deadline no other test's sweep clock reaches: the database is shared
            // across the collection, and a stale in-flight task would be swept (and
            // counted) by tests that assert exact sweep counts.
            Start.AddMinutes(-2), TimeSpan.FromDays(3650));
        install.TryDeliver(Start.AddMinutes(-1)).ShouldBeTrue();
        db.DeviceTasks.Add(install);
        await db.SaveChangesAsync();

        var list = await Read(db).GetForDeviceAsync(device.Id);

        list.Updates.Select(u => u.UpdateId).ShouldNotContain(U6, "hidden updates are listed separately, not as actionable");
        list.Updates.Count.ShouldBe(5);

        string StatusOf(string id) => list.Updates.Single(u => u.UpdateId == id).InstallStatus;
        StatusOf(U1).ShouldBe("installing");
        StatusOf(U2).ShouldBe("verifying");
        StatusOf(U3).ShouldBe("pendingReboot");
        StatusOf(U4).ShouldBe("reOffered");
        StatusOf(U5).ShouldBe("failed");

        var failed = list.Updates.Single(u => u.UpdateId == U5);
        failed.Attempts.ShouldBe(1);
        failed.LastHresult.ShouldBe(unchecked((int)0x80240017));
        failed.LastAttemptAt.ShouldNotBeNull();
        failed.KbIds.ShouldBe("KB5044284", "existing fields are unchanged");
        failed.Severity.ShouldBe("Critical");
        var never = list.Updates.Single(u => u.UpdateId == U1);
        never.Attempts.ShouldBe(0);
        never.LastAttemptAt.ShouldBeNull();
        never.LastHresult.ShouldBeNull();

        list.ActiveInstall.ShouldNotBeNull();
        list.ActiveInstall.TaskId.ShouldBe(install.Id);
        list.ActiveInstall.Status.ShouldBe("Delivered");
        list.ActiveInstall.UpdateIds.ShouldBe(new[] { U1 });

        var hidden = list.Hidden.ShouldHaveSingleItem();
        hidden.UpdateId.ShouldBe(U6);
        hidden.Reason.ShouldBe("breaks the line-of-business app");
        hidden.HiddenBy.ShouldBe("operator@test");

        list.RecentInstalls.ShouldHaveSingleItem().UpdateId.ShouldBe(U7, "only confirmations in the last seven days");
        list.RebootPending.ShouldBeTrue("a security install is waiting for a restart, whatever the stale inventory says");
    }

    [Fact]
    public async Task Without_an_install_in_flight_there_is_no_active_install_and_reboot_falls_back_to_inventory()
    {
        await using var db = _fixture.CreateDbContext();
        var (_, device) = await SeedDeviceAsync(db, "SO6");
        db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(device.Id, U1, 1, "A", null, null, false, 1, Start));
        await db.SaveChangesAsync();

        var list = await Read(db).GetForDeviceAsync(device.Id);

        list.ActiveInstall.ShouldBeNull();
        list.Hidden.ShouldBeEmpty();
        list.RecentInstalls.ShouldBeEmpty();
        list.RebootPending.ShouldBeFalse();
        list.Updates.Single().InstallStatus.ShouldBe("none");
    }

    // The driver install memory is a different feature: it must never colour the security list.
    [Fact]
    public async Task A_driver_install_record_for_the_same_update_id_does_not_leak_into_the_security_list()
    {
        await using var db = _fixture.CreateDbContext();
        var (_, device) = await SeedDeviceAsync(db, "SO8");
        db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(device.Id, U1, 1, "A", null, null, false, 1, Start));
        var driverRecord = new DeviceDriverUpdateInstallRecord(device.Id, U1, 1, "A", Start.AddHours(-1));
        driverRecord.RecordAttempt(DriverUpdateInstallStatus.PendingReboot, 2, 0, Guid.CreateVersion7(), null, 1, Start.AddHours(-1));
        db.DeviceDriverUpdateInstallRecords.Add(driverRecord);
        await db.SaveChangesAsync();

        var list = await Read(db).GetForDeviceAsync(device.Id);

        list.Updates.Single().InstallStatus.ShouldBe("none");
        list.RebootPending.ShouldBeFalse();
    }

    [Fact]
    public async Task The_overview_counts_re_offered_but_not_hidden_updates_as_pending_and_flags_both_as_needing_attention()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "SO7");

        foreach (var id in new[] { U1, U2, U3, U4, U5 })
        {
            db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(device.Id, id, 1, "U", null, null, isPreview: id == U5, 1, Start));
        }

        db.DeviceSecurityUpdateInstallRecords.AddRange(
            Record(device.Id, U2, SecurityUpdateInstallStatus.ReOffered),
            Record(device.Id, U3, SecurityUpdateInstallStatus.ReOffered),
            Record(device.Id, U4, SecurityUpdateInstallStatus.PendingReboot));

        // U3 is both re-offered and hidden: counted once.
        db.DeviceSecurityUpdateSuppressions.AddRange(
            new DeviceSecurityUpdateSuppression(device.Id, U3, "U", "vendor says skip", Guid.CreateVersion7(), "operator@test"),
            new DeviceSecurityUpdateSuppression(device.Id, U5, "U", "preview channel not approved", Guid.CreateVersion7(), "operator@test"));

        // A second device with nothing offered but an install waiting for a restart --
        // the everyday state of a cumulative update that installed.
        var restartOnly = Device.Enroll(orgId, "SO7b", "m-" + Guid.CreateVersion7().ToString("N"), "1", null,
            (await db.EnrollmentTokens.FirstAsync(t => t.OrganizationId == orgId)).Id, Start);
        db.Devices.Add(restartOnly);
        db.DeviceSecurityUpdateInstallRecords.Add(Record(restartOnly.Id, U6, SecurityUpdateInstallStatus.PendingReboot));
        await db.SaveChangesAsync();

        var overview = await Read(db).GetOverviewAsync(orgId, scopedDeviceIds: null);

        overview.Devices.Count.ShouldBe(2);
        var row = overview.Devices.Single(d => d.DeviceId == device.Id);
        row.PendingCount.ShouldBe(3,
            "U1, U4 and the re-offered U2 are actionable -- a re-offered security patch is retried; U3 and U5 are hidden");
        row.PreviewCount.ShouldBe(0, "the only preview is hidden");
        row.CannotCompleteCount.ShouldBe(3, "needs attention: re-offered U2, re-offered-and-hidden U3 (once), hidden U5");
        row.NeedsRestartCount.ShouldBe(1);
        row.RebootPending.ShouldBeTrue();

        var other = overview.Devices.Single(d => d.DeviceId == restartOnly.Id);
        other.PendingCount.ShouldBe(0);
        other.NeedsRestartCount.ShouldBe(1);
        other.RebootPending.ShouldBeTrue();
        other.LastScanAt.ShouldBeNull();

        overview.Summary.DevicesWithPending.ShouldBe(1);
        overview.Summary.TotalPending.ShouldBe(3);
        overview.Summary.NeedsRestartCount.ShouldBe(2);
        overview.Summary.CannotCompleteCount.ShouldBe(3);
        overview.Summary.DevicesRebootPending.ShouldBe(2);
    }

    // A device whose only pending security update rolled back and came back must not
    // vanish from the "has pending" view: it is unpatched, and a retry usually works.
    [Fact]
    public async Task A_device_whose_only_pending_update_was_re_offered_still_counts_as_having_one_pending()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "SO9");
        db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(device.Id, U1, 1, "2026-10 Cumulative Update", "KB5044284", "Critical", false, 1, Start));
        db.DeviceSecurityUpdateInstallRecords.Add(Record(device.Id, U1, SecurityUpdateInstallStatus.ReOffered));
        await db.SaveChangesAsync();

        var overview = await Read(db).GetOverviewAsync(orgId, scopedDeviceIds: null);

        var row = overview.Devices.ShouldHaveSingleItem();
        row.PendingCount.ShouldBe(1);
        row.CannotCompleteCount.ShouldBe(1, "and it is flagged as needing attention");
        overview.Summary.DevicesWithPending.ShouldBe(1);
        overview.Summary.TotalPending.ShouldBe(1);

        // The item still says why, so the console can warn.
        (await Read(db).GetForDeviceAsync(device.Id)).Updates.Single().InstallStatus.ShouldBe("reOffered");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
