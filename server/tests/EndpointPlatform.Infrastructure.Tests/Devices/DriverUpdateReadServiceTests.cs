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
/// The driver-update reads: the fleet overview counts and orders devices by their
/// pending updates, and the per-device list returns what one machine has waiting.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DriverUpdateReadServiceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static DriverUpdateReadService Read(EndpointPlatformDbContext db)
    {
        var time = new FixedClock(Start);
        return new DriverUpdateReadService(db, time, Options.Create(new AgentServerOptions()));
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
        var (orgId, device) = await SeedDeviceAsync(db, "RO1");

        db.DeviceDriverUpdates.AddRange(
            new DeviceDriverUpdate(device.Id, "11111111-1111-1111-1111-111111111111", 1, "A", "Display", "Intel", null, false, 10, Start),
            new DeviceDriverUpdate(device.Id, "22222222-2222-2222-2222-222222222222", 1, "B", "Net", "Realtek", null, false, 20, Start),
            new DeviceDriverUpdate(device.Id, "33333333-3333-3333-3333-333333333333", 1, "C (preview)", "Net", "Realtek", null, true, 30, Start));
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
        var (orgId, _) = await SeedDeviceAsync(db, "RO2");

        var overview = await Read(db).GetOverviewAsync(orgId, scopedDeviceIds: null);

        overview.Summary.DevicesWithPending.ShouldBe(0);
        overview.Devices.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scoped_admin_sees_only_their_devices_in_the_overview()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, mine) = await SeedDeviceAsync(db, "RO3");
        var other = Device.Enroll(orgId, "RO3b", "m-" + Guid.CreateVersion7().ToString("N"), "1", null,
            (await db.EnrollmentTokens.FirstAsync(t => t.OrganizationId == orgId)).Id, Start);
        db.Devices.Add(other);
        db.DeviceDriverUpdates.AddRange(
            new DeviceDriverUpdate(mine.Id, "11111111-1111-1111-1111-111111111111", 1, "Mine", null, null, null, false, 1, Start),
            new DeviceDriverUpdate(other.Id, "22222222-2222-2222-2222-222222222222", 1, "Theirs", null, null, null, false, 1, Start));
        await db.SaveChangesAsync();

        var overview = await Read(db).GetOverviewAsync(orgId, scopedDeviceIds: new[] { mine.Id });

        overview.Devices.ShouldHaveSingleItem().DeviceId.ShouldBe(mine.Id);
    }

    [Fact]
    public async Task The_per_device_list_returns_non_preview_first()
    {
        await using var db = _fixture.CreateDbContext();
        var (_, device) = await SeedDeviceAsync(db, "RO4");

        db.DeviceDriverUpdates.AddRange(
            new DeviceDriverUpdate(device.Id, "33333333-3333-3333-3333-333333333333", 1, "Zebra preview", null, null, null, true, null, Start),
            new DeviceDriverUpdate(device.Id, "11111111-1111-1111-1111-111111111111", 1, "Alpha", null, null, null, false, null, Start));
        await db.SaveChangesAsync();

        var list = await Read(db).GetForDeviceAsync(device.Id);

        list.Updates.Count.ShouldBe(2);
        list.Updates[0].IsPreview.ShouldBeFalse();
        list.Updates[0].Title.ShouldBe("Alpha");
        list.Updates[1].IsPreview.ShouldBeTrue();
        list.LastScanAt.ShouldNotBeNull();
        list.RebootPending.ShouldBeFalse();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
