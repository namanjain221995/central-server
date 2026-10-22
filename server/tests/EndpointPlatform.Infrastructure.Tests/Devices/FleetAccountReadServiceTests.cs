using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// The fleet-wide local-account summary: accounts are grouped case-insensitively
/// across active devices, the counts are device counts, built-ins are flagged, and
/// a scoped administrator sees only their devices.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FleetAccountReadServiceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FleetAccountReadService Read(EndpointPlatformDbContext db) =>
        new(db, NullLogger<FleetAccountReadService>.Instance);

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

    private static DeviceLocalUser User(Guid deviceId, string name, bool admin = false, bool enabled = true) =>
        new(deviceId, "S-1-5-21-" + Guid.CreateVersion7().ToString("N"), name,
            null, null, enabled, true, false, null, admin, Start);

    [Fact]
    public async Task Accounts_are_grouped_case_insensitively_with_device_counts_and_builtin_flags()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, d1) = await SeedDeviceAsync(db, "FA1-A");
        var d2 = await AddDeviceAsync(db, orgId, "FA1-B");
        var d3 = await AddDeviceAsync(db, orgId, "FA1-C");

        db.DeviceLocalUsers.AddRange(
            User(d1.Id, "ITAdmin", admin: true, enabled: true),
            User(d2.Id, "itadmin", admin: false, enabled: true),
            User(d3.Id, "ITAdmin", admin: true, enabled: false),
            User(d1.Id, "Administrator", admin: true, enabled: false),
            User(d1.Id, "Guest", admin: false, enabled: false));
        await db.SaveChangesAsync();

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        summary.ActiveDevices.ShouldBe(3);
        summary.Truncated.ShouldBeFalse();
        summary.Accounts.Count.ShouldBe(3);

        // Most devices first; ties by name — so the case-variant group leads.
        var itAdmin = summary.Accounts[0];
        itAdmin.Name.ShouldBe("ITAdmin"); // the most common original casing (2 of 3)
        itAdmin.IsBuiltIn.ShouldBeFalse();
        itAdmin.OnDevices.ShouldBe(3);
        itAdmin.AdminOn.ShouldBe(2);
        itAdmin.EnabledOn.ShouldBe(2);
        itAdmin.Devices.Select(d => d.Hostname).ShouldBe(new[] { "FA1-A", "FA1-B", "FA1-C" });
        itAdmin.Devices[0].IsAdmin.ShouldBeTrue();
        itAdmin.Devices[1].IsAdmin.ShouldBeFalse();
        itAdmin.Devices[2].Enabled.ShouldBeFalse();

        summary.Accounts[1].Name.ShouldBe("Administrator");
        summary.Accounts[1].IsBuiltIn.ShouldBeTrue();
        summary.Accounts[1].OnDevices.ShouldBe(1);

        summary.Accounts[2].Name.ShouldBe("Guest");
        summary.Accounts[2].IsBuiltIn.ShouldBeTrue();
    }

    [Fact]
    public async Task A_scoped_admin_sees_only_their_devices_and_their_denominator()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, mine) = await SeedDeviceAsync(db, "FA2-A");
        var other = await AddDeviceAsync(db, orgId, "FA2-B");

        db.DeviceLocalUsers.AddRange(
            User(mine.Id, "Shared"),
            User(other.Id, "Shared"),
            User(other.Id, "TheirsOnly"));
        await db.SaveChangesAsync();

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: new[] { mine.Id });

        summary.ActiveDevices.ShouldBe(1);
        var account = summary.Accounts.ShouldHaveSingleItem();
        account.Name.ShouldBe("Shared");
        account.OnDevices.ShouldBe(1);
        account.Devices.ShouldHaveSingleItem().DeviceId.ShouldBe(mine.Id);
    }

    [Fact]
    public async Task A_retired_device_counts_nowhere()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, active) = await SeedDeviceAsync(db, "FA3-A");
        var retired = await AddDeviceAsync(db, orgId, "FA3-B");

        db.DeviceLocalUsers.AddRange(
            User(active.Id, "Helpdesk"),
            User(retired.Id, "Helpdesk"),
            User(retired.Id, "GhostAccount"));
        retired.Retire();
        await db.SaveChangesAsync();

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        summary.ActiveDevices.ShouldBe(1);
        var account = summary.Accounts.ShouldHaveSingleItem();
        account.Name.ShouldBe("Helpdesk");
        account.OnDevices.ShouldBe(1);
        account.Devices.ShouldHaveSingleItem().Hostname.ShouldBe("FA3-A");
    }

    [Fact]
    public async Task An_org_with_devices_but_no_reported_accounts_returns_the_denominator_only()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, _) = await SeedDeviceAsync(db, "FA4");

        var summary = await Read(db).GetSummaryAsync(orgId, scopedDeviceIds: null);

        summary.ActiveDevices.ShouldBe(1);
        summary.Accounts.ShouldBeEmpty();
        summary.Truncated.ShouldBeFalse();
    }
}
