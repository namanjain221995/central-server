using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Groups;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Tests.Groups;

/// <summary>
/// Membership races, made deterministic.
/// </summary>
/// <remarks>
/// <para>
/// A race that only sometimes happens proves nothing when it does not. So where
/// the interleaving matters, a second connection takes a real row lock and holds
/// it; the service under test is started and observed -- in
/// <c>pg_stat_activity</c> -- to be blocked on that lock; then the second
/// connection commits. The service resumes against the committed state, which is
/// exactly the moment the defence has to hold.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DeviceGroupConcurrencyTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private DeviceGroupService Service(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, new AuditWriter(db, TimeProvider.System, new CorrelationIdAccessor(), new HttpContextAccessor()),
            TimeProvider.System, new DeviceScopeAuthorizer(db), Options.Create(new AgentServerOptions()));

    private sealed record World(Guid Org, Guid AllAdmin, Guid AllDevices, Guid Device, Guid[] Groups);

    /// <summary>An organization, an all-scope administrator, <paramref name="groups"/> custom groups and one device in the first.</summary>
    private async Task<World> SeedAsync(int groups)
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization("Race", ("r" + Guid.CreateVersion7().ToString("N"))[..18]);
        db.Organizations.Add(org);

        var admin = new PlatformUser(org.Id, $"race-{Guid.CreateVersion7():N}@test.local", "Race Admin");
        admin.GrantAllDeviceScope();
        db.PlatformUsers.Add(admin);

        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var created = Enumerable.Range(0, groups)
            .Select(i => new DeviceGroup(org.Id, $"G{i}-{Guid.CreateVersion7():N}"[..20], null, DeviceGroupType.Static))
            .ToArray();
        db.DeviceGroups.AddRange(created);

        var device = Device.Enroll(org.Id, "RACE-PC", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, DateTimeOffset.UtcNow);
        device.MoveToGroup(created[0].Id);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var allDevices = await db.DeviceGroups.Where(g => g.OrganizationId == org.Id && g.IsBuiltIn).Select(g => g.Id).SingleAsync();
        return new World(org.Id, admin.Id, allDevices, device.Id, created.Select(g => g.Id).ToArray());
    }

    private async Task<Guid> ScopedAdminAsync(Guid org, params Guid[] groups)
    {
        await using var db = _fixture.CreateDbContext();
        var admin = new PlatformUser(org, $"scoped-{Guid.CreateVersion7():N}@test.local", "Scoped");
        db.PlatformUsers.Add(admin);
        await db.SaveChangesAsync();
        db.AdminDeviceScopes.AddRange(groups.Select(g => new AdminDeviceScope(admin.Id, g)));
        await db.SaveChangesAsync();
        return admin.Id;
    }

    private async Task<Guid> GroupOfAsync(Guid device)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.Devices.AsNoTracking().Where(d => d.Id == device).Select(d => d.DeviceGroupId).SingleAsync();
    }

    /// <summary>Waits until some other session is blocked waiting on a lock.</summary>
    private async Task WaitForBlockedSessionAsync()
    {
        await using var probe = new NpgsqlConnection(_fixture.ConnectionString);
        await probe.OpenAsync();
        for (var i = 0; i < 200; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> pg_backend_pid()", probe);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The service never blocked on the held lock; the interleaving this test needs did not happen.");
    }

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// The authorization time-of-check/time-of-use race.
    /// </summary>
    /// <remarks>
    /// A scoped administrator controls G0 and G1 and moves a device from G0 to G1.
    /// Between reading the device (in G0 -- authorized) and writing it, another
    /// administrator moves it to G2, which this one has no authority over. A
    /// blind update would now pull the device out of G2. The conditional update
    /// re-evaluates against the committed row, finds it is no longer in G0, and
    /// touches nothing.
    /// </remarks>
    [Fact]
    public async Task A_device_moved_out_from_under_an_authorized_move_is_not_moved_again()
    {
        var world = await SeedAsync(groups: 3);
        var scoped = await ScopedAdminAsync(world.Org, world.Groups[0], world.Groups[1]);

        // The interfering administrator's move to G2, held uncommitted.
        await using var interferer = new NpgsqlConnection(_fixture.ConnectionString);
        await interferer.OpenAsync();
        await using var tx = await interferer.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE endpoint_platform.devices SET device_group_id = '{world.Groups[2]}' WHERE id = '{world.Device}'", interferer, tx))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = _fixture.CreateDbContext();
        var move = Service(db).AddDevicesAsync(world.Org, scoped, "scoped", world.Groups[1], [world.Device]);

        await WaitForBlockedSessionAsync();
        await tx.CommitAsync();

        var result = await move;

        result.Devices.ShouldHaveSingleItem().Outcome.ShouldBe(GroupDeviceOutcome.ChangedConcurrently);
        (await GroupOfAsync(world.Device)).ShouldBe(world.Groups[2],
            "the device must stay in the group the scoped administrator was never authorized against");
    }

    /// <summary>
    /// Deleting a group while a device is being moved into it. The delete moves
    /// the group's devices to All Devices and removes the row; a device committed
    /// into the group in between makes the delete fail on the foreign key and roll
    /// back entirely, rather than leaving that device pointing at nothing.
    /// </summary>
    [Fact]
    public async Task A_device_moved_into_a_group_being_deleted_is_never_orphaned()
    {
        var world = await SeedAsync(groups: 2);
        var doomed = world.Groups[1];

        // A move of the device into the doomed group, held uncommitted. The
        // delete's own UPDATE cannot see it; its DELETE must wait on it.
        await using var mover = new NpgsqlConnection(_fixture.ConnectionString);
        await mover.OpenAsync();
        await using var tx = await mover.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE endpoint_platform.devices SET device_group_id = '{doomed}' WHERE id = '{world.Device}'", mover, tx))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = _fixture.CreateDbContext();
        var delete = Service(db).DeleteAsync(world.Org, world.AllAdmin, "admin", doomed);

        await WaitForBlockedSessionAsync();
        await tx.CommitAsync();

        var result = await delete;

        result.Status.ShouldBe(GroupChangeStatus.Conflict);
        (await GroupOfAsync(world.Device)).ShouldBe(doomed, "the committed move stands");

        await using var check = _fixture.CreateDbContext();
        (await check.DeviceGroups.AnyAsync(g => g.Id == doomed)).ShouldBeTrue(
            "the delete rolled back, so the device still points at a group that exists");
        (await check.Devices.AsNoTracking().AnyAsync(d => d.Id == world.Device && d.Status == DeviceStatus.Active))
            .ShouldBeTrue("no device is ever deleted by a group delete");
    }

    /// <summary>
    /// Many administrators moving one device into many groups at once. Whatever
    /// the order, the device ends in exactly one of them and every request gets
    /// an honest answer.
    /// </summary>
    [Fact]
    public async Task Concurrent_moves_of_one_device_leave_it_in_exactly_one_group()
    {
        var world = await SeedAsync(groups: 6);
        var destinations = world.Groups[1..];

        var moves = destinations.Select(async destination =>
        {
            await using var db = _fixture.CreateDbContext();
            return await Service(db).AddDevicesAsync(world.Org, world.AllAdmin, "admin", destination, [world.Device]);
        }).ToList();

        var results = await Task.WhenAll(moves);
        var outcomes = results.Select(r => r.Devices.ShouldHaveSingleItem().Outcome).ToList();

        outcomes.ShouldAllBe(o => o == GroupDeviceOutcome.Moved || o == GroupDeviceOutcome.ChangedConcurrently);
        outcomes.Count(o => o == GroupDeviceOutcome.Moved).ShouldBeGreaterThanOrEqualTo(1);

        var finalGroup = await GroupOfAsync(world.Device);
        destinations.ShouldContain(finalGroup, "the device must end in one of the requested groups -- and only one");
    }

    /// <summary>
    /// Deleting the same group twice at once: one succeeds, the other finds
    /// nothing to delete, and the devices end in All Devices exactly once.
    /// </summary>
    [Fact]
    public async Task Concurrent_deletes_of_one_group_move_its_devices_once()
    {
        var world = await SeedAsync(groups: 1);
        var doomed = world.Groups[0];

        var deletes = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = _fixture.CreateDbContext();
            return await Service(db).DeleteAsync(world.Org, world.AllAdmin, "admin", doomed);
        }).ToList();

        var results = await Task.WhenAll(deletes);

        results.Count(r => r.Status == GroupChangeStatus.Ok).ShouldBe(1);
        results.Where(r => r.Status != GroupChangeStatus.Ok)
            .ShouldAllBe(r => r.Status == GroupChangeStatus.NotFound || r.Status == GroupChangeStatus.Conflict);
        (await GroupOfAsync(world.Device)).ShouldBe(world.AllDevices);
    }
}
