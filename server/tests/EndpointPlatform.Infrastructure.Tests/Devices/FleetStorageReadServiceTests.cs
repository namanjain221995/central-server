using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// The fleet-wide storage summary: volumes are graded by free percentage, a device's
/// severity is the worst of its volumes, the fullest device sorts first, a device with
/// no inventory is Unknown, and a scoped administrator sees only their devices.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FleetStorageReadServiceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const long Gib = 1L << 30;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static FleetStorageReadService Read(EndpointPlatformDbContext db) =>
        new(db, NullLogger<FleetStorageReadService>.Instance);

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

    private static async Task<Device> AddDeviceAsync(EndpointPlatformDbContext db, Guid orgId, string hostname)
    {
        var token = await db.EnrollmentTokens.FirstAsync(t => t.OrganizationId == orgId);
        var device = Device.Enroll(orgId, hostname, "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, Start);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static string Disks(params (string Name, long Size, long Free)[] volumes) =>
        JsonSerializer.Serialize(
            volumes.Select(v => new { name = v.Name, fileSystem = "NTFS", sizeBytes = v.Size, freeBytes = v.Free }),
            Web);

    private static async Task SetHardwareAsync(EndpointPlatformDbContext db, Guid deviceId, string disksJson)
    {
        var hardware = new DeviceHardware(deviceId);
        hardware.Apply(null, null, null, null, null, null, null, disksJson, Start);
        db.DeviceHardware.Add(hardware);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Volumes_are_graded_and_the_fullest_device_sorts_first()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, critical) = await SeedDeviceAsync(db, "ST1-CRIT");
        var low = await AddDeviceAsync(db, orgId, "ST1-LOW");
        var ok = await AddDeviceAsync(db, orgId, "ST1-OK");

        await SetHardwareAsync(db, critical.Id, Disks(("C:", 100 * Gib, 2 * Gib)));   // 2% -> Critical
        await SetHardwareAsync(db, low.Id, Disks(("C:", 100 * Gib, 8 * Gib)));        // 8% -> Low
        await SetHardwareAsync(db, ok.Id, Disks(("C:", 100 * Gib, 50 * Gib)));        // 50% -> Ok

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        summary.ActiveDevices.ShouldBe(3);
        summary.DevicesReporting.ShouldBe(3);
        summary.CriticalDiskDeviceCount.ShouldBe(1);
        summary.LowDiskDeviceCount.ShouldBe(1);
        summary.Truncated.ShouldBeFalse();
        summary.CriticalFreePercent.ShouldBe(FleetStorageReadService.CriticalFreePercent);

        summary.Devices.Select(d => d.Hostname).ShouldBe(new[] { "ST1-CRIT", "ST1-LOW", "ST1-OK" });
        summary.Devices[0].Severity.ShouldBe(StorageSeverity.Critical);
        summary.Devices[0].CollectedAt.ShouldBe(Start);
        summary.Devices[0].Volumes.ShouldHaveSingleItem().FreePercent.ShouldBe(2.0);
        summary.Devices[1].Severity.ShouldBe(StorageSeverity.Low);
        summary.Devices[2].Severity.ShouldBe(StorageSeverity.Ok);
    }

    [Fact]
    public async Task A_device_severity_is_the_worst_of_its_volumes()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "ST2");

        // System drive healthy, a data drive nearly full: the device is Critical.
        await SetHardwareAsync(db, device.Id, Disks(
            ("C:", 100 * Gib, 60 * Gib),   // 60% Ok
            ("D:", 200 * Gib, 4 * Gib)));  // 2% Critical

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        var row = summary.Devices.ShouldHaveSingleItem();
        row.Severity.ShouldBe(StorageSeverity.Critical);
        row.Volumes.Select(v => v.Name).ShouldBe(new[] { "C:", "D:" }); // volumes sorted by name
        row.Volumes[0].Severity.ShouldBe(StorageSeverity.Ok);
        row.Volumes[1].Severity.ShouldBe(StorageSeverity.Critical);
        summary.CriticalDiskDeviceCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_device_that_never_reported_hardware_is_unknown_and_sorts_last()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, reporting) = await SeedDeviceAsync(db, "ST3-HW");
        await AddDeviceAsync(db, orgId, "ST3-NOHW");

        await SetHardwareAsync(db, reporting.Id, Disks(("C:", 100 * Gib, 50 * Gib)));

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        summary.ActiveDevices.ShouldBe(2);
        summary.DevicesReporting.ShouldBe(1);
        summary.Devices.Count.ShouldBe(2);

        var unknown = summary.Devices[^1];
        unknown.Hostname.ShouldBe("ST3-NOHW");
        unknown.Severity.ShouldBe(StorageSeverity.Unknown);
        unknown.CollectedAt.ShouldBeNull();
        unknown.Volumes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_zero_size_volume_is_unknown_not_a_false_critical()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "ST4");

        await SetHardwareAsync(db, device.Id, Disks(("C:", 0, 0)));

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        var row = summary.Devices.ShouldHaveSingleItem();
        row.Volumes.ShouldHaveSingleItem().Severity.ShouldBe(StorageSeverity.Unknown);
        row.Severity.ShouldBe(StorageSeverity.Unknown);
        summary.CriticalDiskDeviceCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_scoped_admin_sees_only_their_devices_and_their_denominator()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, mine) = await SeedDeviceAsync(db, "ST5-MINE");
        var other = await AddDeviceAsync(db, orgId, "ST5-OTHER");

        await SetHardwareAsync(db, mine.Id, Disks(("C:", 100 * Gib, 3 * Gib)));
        await SetHardwareAsync(db, other.Id, Disks(("C:", 100 * Gib, 1 * Gib)));

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: new[] { mine.Id });

        summary.ActiveDevices.ShouldBe(1);
        var row = summary.Devices.ShouldHaveSingleItem();
        row.DeviceId.ShouldBe(mine.Id);
        row.Hostname.ShouldBe("ST5-MINE");
    }

    [Fact]
    public async Task A_retired_device_counts_nowhere()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, active) = await SeedDeviceAsync(db, "ST6-ACTIVE");
        var retired = await AddDeviceAsync(db, orgId, "ST6-RETIRED");

        await SetHardwareAsync(db, active.Id, Disks(("C:", 100 * Gib, 50 * Gib)));
        await SetHardwareAsync(db, retired.Id, Disks(("C:", 100 * Gib, 1 * Gib)));
        retired.Retire();
        await db.SaveChangesAsync();

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        summary.ActiveDevices.ShouldBe(1);
        var row = summary.Devices.ShouldHaveSingleItem();
        row.Hostname.ShouldBe("ST6-ACTIVE");
        summary.CriticalDiskDeviceCount.ShouldBe(0);
    }
}
