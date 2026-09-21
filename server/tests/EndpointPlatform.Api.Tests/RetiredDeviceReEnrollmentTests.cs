using System.Net;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Retiring a device, and what happens when that machine comes back.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle these tests describe is: retire the device, wipe or reinstall the
/// agent, let it enrol again, approve it, and get a <em>new</em> active device. The
/// retired record stays exactly where it was.
/// </para>
/// <para>
/// Two failure modes are being held apart, and they pull in opposite directions.
/// Resurrection -- a fresh enrolment quietly flipping the retired row back to Active
/// -- would make retirement meaningless. Permanent exclusion -- refusing the machine
/// outright, as this code did until now -- would make retirement unusable, because a
/// reissued laptop could never be enrolled again without an administrator undoing the
/// retirement by hand. Both are wrong; a successor record is right.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class RetiredDeviceReEnrollmentTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri Offboard(Guid id) =>
        new($"/admin/v1/devices/{id}/offboard", UriKind.Relative);

    /// <summary>An administrator, optionally without device scope.</summary>
    private async Task<HttpClient> AdminAsync(bool allDeviceScope = true, string? roleKey = null)
    {
        var email = $"ret-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == (roleKey ?? SystemRoles.SuperAdministrator));

            var user = new PlatformUser(org.Id, email, "Retire Admin");
            user.SetPasswordHash(
                PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);

            if (allDeviceScope)
            {
                user.GrantAllDeviceScope();
            }

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var token = SecretGenerator.GenerateSecret();

        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
            db.AdminSessions.Add(new AdminSession(
                user.Id, SecretGenerator.HashSecret(token), user.SecurityStamp,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
                sourceIp: null, userAgent: "retire-tests"));
            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    /// <summary>Enrols a device directly and returns its id and machine identifier.</summary>
    private async Task<(Guid DeviceId, string MachineIdentifier)> SeedDeviceAsync(
        string hostname, string agentVersion = "1.1.4")
    {
        await using var db = _fixture.CreateDbContext();

        var orgId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        var machineIdentifier = $"smbios-{Guid.CreateVersion7()}";

        var token = new EnrollmentToken(
            orgId, $"ret-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            Guid.CreateVersion7(), "retire-tests", DateTimeOffset.UtcNow.AddHours(1), 5);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            orgId, hostname, machineIdentifier, agentVersion, "Windows 11 Pro",
            token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return (device.Id, machineIdentifier);
    }

    private async Task<Device> ReloadAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
    }

    // ---- device scope ------------------------------------------------------

    /// <summary>
    /// The gap this class closes on the authorization side.
    /// </summary>
    /// <remarks>
    /// Retiring revokes every credential and takes a machine out of management, so it
    /// must not be reachable by an administrator scoped elsewhere just because the
    /// device shares their organization. Scope is deny-by-default, so an account with
    /// no scope rows reaches nothing, and the refusal is a 404 so the caller is not
    /// told whether the device exists.
    /// </remarks>
    [Fact]
    public async Task An_administrator_without_device_scope_cannot_retire_a_device()
    {
        var (deviceId, _) = await SeedDeviceAsync("RET-SCOPE");

        using var unscoped = await AdminAsync(allDeviceScope: false);

        (await unscoped.PostAsync(Offboard(deviceId), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ReloadAsync(deviceId)).Status.ShouldBe(
            DeviceStatus.Active, "a refused retirement must not have taken effect");
    }

    [Fact]
    public async Task A_scoped_administrator_can_retire_a_device()
    {
        var (deviceId, _) = await SeedDeviceAsync("RET-SCOPED-OK");

        using var scoped = await AdminAsync(allDeviceScope: true);

        (await scoped.PostAsync(Offboard(deviceId), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ReloadAsync(deviceId)).Status.ShouldBe(DeviceStatus.Retired);
    }

    /// <summary>Idempotent: retiring twice is not an error and not a second event.</summary>
    [Fact]
    public async Task Retiring_an_already_retired_device_is_a_no_op()
    {
        var (deviceId, _) = await SeedDeviceAsync("RET-IDEMPOTENT");
        using var client = await AdminAsync();

        (await client.PostAsync(Offboard(deviceId), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync(Offboard(deviceId), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ReloadAsync(deviceId)).Status.ShouldBe(DeviceStatus.Retired);
    }

    // ---- the successor record ---------------------------------------------

    /// <summary>
    /// The lifecycle end to end: retire, then let the machine enrol again.
    /// </summary>
    /// <remarks>
    /// Goes through the real enrollment service rather than constructing rows, so
    /// this exercises the identity-matching decision itself -- the one that used to
    /// refuse the machine outright.
    /// </remarks>
    [Fact]
    public async Task A_retired_machine_enrolling_again_becomes_a_new_active_device()
    {
        var (originalId, machineIdentifier) = await SeedDeviceAsync("RET-SUCCESSOR", "1.1.4");

        using var client = await AdminAsync();
        (await client.PostAsync(Offboard(originalId), null)).EnsureSuccessStatusCode();

        var successorId = await EnrollAsync(machineIdentifier, "RET-SUCCESSOR", "1.4.1");

        successorId.ShouldNotBe(originalId, "the machine must get a new identity");

        var successor = await ReloadAsync(successorId);
        successor.Status.ShouldBe(DeviceStatus.Active);
        successor.MachineIdentifier.ShouldBe(machineIdentifier);
        successor.AgentVersion.ShouldBe("1.4.1");

        // The retired record is untouched history, not a thing that was moved.
        var original = await ReloadAsync(originalId);
        original.Status.ShouldBe(DeviceStatus.Retired);
        original.AgentVersion.ShouldBe("1.1.4");
    }

    /// <summary>
    /// An <em>active</em> device enrolling again still re-enrols in place. This is the
    /// ordinary reinstall/upgrade path and must not have been turned into a
    /// device-duplicating one by the change above.
    /// </summary>
    [Fact]
    public async Task An_active_machine_enrolling_again_re_enrolls_in_place()
    {
        var (deviceId, machineIdentifier) = await SeedDeviceAsync("RET-REENROLL", "1.1.4");

        var sameId = await EnrollAsync(machineIdentifier, "RET-REENROLL", "1.4.1");

        sameId.ShouldBe(deviceId, "an active machine keeps its device record");

        var device = await ReloadAsync(deviceId);
        device.Status.ShouldBe(DeviceStatus.Active);
        device.AgentVersion.ShouldBe("1.4.1");

        await using var db = _fixture.CreateDbContext();
        (await db.Devices.CountAsync(d => d.MachineIdentifier == machineIdentifier))
            .ShouldBe(1, "re-enrolling an active device must not create a second row");
    }

    /// <summary>
    /// Retiring the successor works too, and leaves two independent retired records.
    /// A machine can go round the loop more than once.
    /// </summary>
    [Fact]
    public async Task A_machine_can_go_round_the_lifecycle_more_than_once()
    {
        var (firstId, machineIdentifier) = await SeedDeviceAsync("RET-TWICE");
        using var client = await AdminAsync();

        (await client.PostAsync(Offboard(firstId), null)).EnsureSuccessStatusCode();
        var secondId = await EnrollAsync(machineIdentifier, "RET-TWICE", "1.2.0");

        (await client.PostAsync(Offboard(secondId), null)).EnsureSuccessStatusCode();
        var thirdId = await EnrollAsync(machineIdentifier, "RET-TWICE", "1.4.1");

        new[] { firstId, secondId, thirdId }.Distinct().Count().ShouldBe(3);

        (await ReloadAsync(firstId)).Status.ShouldBe(DeviceStatus.Retired);
        (await ReloadAsync(secondId)).Status.ShouldBe(DeviceStatus.Retired);
        (await ReloadAsync(thirdId)).Status.ShouldBe(DeviceStatus.Active);

        await using var db = _fixture.CreateDbContext();
        (await db.Devices.CountAsync(
            d => d.MachineIdentifier == machineIdentifier && d.Status == DeviceStatus.Active))
            .ShouldBe(1, "exactly one active row per machine, always");
    }

    // ---- history survives ---------------------------------------------------

    /// <summary>
    /// Retirement keeps the record. Asserted on the row itself rather than on a
    /// count, because "not deleted" is the whole promise made to the operator in the
    /// confirmation dialog.
    /// </summary>
    [Fact]
    public async Task Retirement_never_deletes_the_device_row()
    {
        var (deviceId, _) = await SeedDeviceAsync("RET-KEEP");
        using var client = await AdminAsync();

        await client.PostAsync(Offboard(deviceId), null);

        await using var db = _fixture.CreateDbContext();
        (await db.Devices.AnyAsync(d => d.Id == deviceId)).ShouldBeTrue();
    }

    /// <summary>Retiring one device does not disturb any other.</summary>
    [Fact]
    public async Task Retiring_one_device_leaves_others_active()
    {
        var (target, _) = await SeedDeviceAsync("RET-TARGET");
        var (bystander, _) = await SeedDeviceAsync("RET-BYSTANDER");

        using var client = await AdminAsync();
        await client.PostAsync(Offboard(target), null);

        (await ReloadAsync(target)).Status.ShouldBe(DeviceStatus.Retired);
        (await ReloadAsync(bystander)).Status.ShouldBe(DeviceStatus.Active);
    }

    // ---- helper -------------------------------------------------------------

    /// <summary>
    /// Enrols through the real service, minting a token the way an approval does.
    /// </summary>
    private async Task<Guid> EnrollAsync(string machineIdentifier, string hostname, string agentVersion)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var enrollment = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Enrollment.AgentEnrollmentService>();

        string secret;
        await using (var db = _fixture.CreateDbContext())
        {
            var orgId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
            secret = SecretGenerator.GenerateSecret();

            db.EnrollmentTokens.Add(new EnrollmentToken(
                orgId, $"approved-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
                Guid.CreateVersion7(), "retire-tests", DateTimeOffset.UtcNow.AddMinutes(15), maxUses: 1));

            await db.SaveChangesAsync();
        }

        var outcome = await enrollment.EnrollAsync(
            secret, hostname, machineIdentifier, agentVersion, "Windows 11 Pro", CancellationToken.None);

        outcome.Success.ShouldBeTrue("enrollment should succeed for this machine");

        return outcome.DeviceId;
    }
}
