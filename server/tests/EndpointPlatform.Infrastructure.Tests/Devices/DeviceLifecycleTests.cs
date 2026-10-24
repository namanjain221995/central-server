using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// Phase 14: offboarding revokes credentials and retires a device (blocking
/// check-in and re-enrollment), and reactivation reverses it against real
/// PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceLifecycleTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static AuditWriter Audit(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, TimeProvider.System, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static async Task<(Organization Org, EnrollmentToken Token)> SeedOrgAndTokenAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db)
    {
        var org = new Organization("L", ("l" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new EnrollmentToken(
            org.Id, "t", SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 99);
        db.EnrollmentTokens.Add(token);
        await db.SaveChangesAsync();
        return (org, token);
    }

    /// <summary>
    /// The suite's shared Redis connection, for the re-enrollment approval gate.
    /// </summary>
    /// <remarks>
    /// abortConnect=false so merely CONSTRUCTING the enrollment service never
    /// requires a reachable Redis: only the one test that exercises the gate
    /// actually talks to it, and with Redis down that test fails with the store's
    /// own fail-closed refusal rather than at setup.
    /// </remarks>
    private static readonly Lazy<IConnectionMultiplexer> Redis = new(() =>
        ConnectionMultiplexer.Connect(TestRedis.ConnectionString + ",abortConnect=false"));

    private static PendingEnrollmentStore PendingStore() =>
        new(Redis.Value,
            // An empty key makes the protector generate a process-local one, which is
            // fine here: re-enrollment records never carry a sealed token secret.
            new AesGcmSecretProtector(Options.Create(new SecretProtectionOptions())),
            NullLogger<PendingEnrollmentStore>.Instance);

    private static AgentEnrollmentService Enrollment(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, Audit(db), TimeProvider.System, NullLogger<AgentEnrollmentService>.Instance,
            // Unconfigured: these tests predate automatic escrow and assert enrollment
            // behaviour that must not change when no sealing key is present.
            new Infrastructure.Security.EscrowSealingKeyProvider(null),
            PendingStore());

    [Fact]
    public async Task Offboarding_revokes_credentials_and_retires_the_device()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, _) = await SeedOrgAndTokenAsync(db);
        var secret = await IssueTokenSecretAsync(db, org.Id);
        var machineId = "m-" + Guid.CreateVersion7().ToString("N");

        var enrolled = await Enrollment(db).EnrollAsync(secret, "OFF-PC", machineId, "1.0", null);
        enrolled.Success.ShouldBeTrue();

        var service = new DeviceLifecycleService(db, Audit(db), TimeProvider.System);
        (await service.OffboardAsync(org.Id, enrolled.DeviceId, Guid.CreateVersion7(), "admin"))
            .ShouldBe(DeviceLifecycleResult.Success);

        await using var verify = _fixture.CreateDbContext();
        (await verify.Devices.SingleAsync(d => d.Id == enrolled.DeviceId)).Status.ShouldBe(DeviceStatus.Retired);
        (await verify.AgentCredentials.Where(c => c.DeviceId == enrolled.DeviceId).ToListAsync())
            .ShouldAllBe(c => c.RevokedAt != null);
    }

    [Fact]
    /// <summary>
    /// A retired machine enrolling again gets a new device, not its old one back.
    /// </summary>
    /// <remarks>
    /// This test previously asserted that the machine was refused outright until an
    /// administrator reactivated the retired record by hand. That made retirement a
    /// trap: a reissued laptop could never be enrolled again without someone first
    /// undoing the very thing that had been done deliberately. The invariant that
    /// actually matters is narrower -- the retired record is never revived -- and it
    /// is preserved here by asserting the successor has a different id and the
    /// original is still Retired.
    /// </remarks>
    public async Task A_retired_device_enrolls_again_as_a_new_device()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, _) = await SeedOrgAndTokenAsync(db);
        var machineId = "m-" + Guid.CreateVersion7().ToString("N");

        var first = await Enrollment(db).EnrollAsync(await IssueTokenSecretAsync(db, org.Id), "RE-PC", machineId, "1.0", null);
        first.Success.ShouldBeTrue();

        var service = new DeviceLifecycleService(db, Audit(db), TimeProvider.System);
        await service.OffboardAsync(org.Id, first.DeviceId, Guid.CreateVersion7(), "admin");

        // Same machine, fresh token: it enrolls, as a successor record.
        var successor = await Enrollment(db).EnrollAsync(
            await IssueTokenSecretAsync(db, org.Id), "RE-PC", machineId, "1.0", null);

        successor.Success.ShouldBeTrue("a retired machine may enroll again");
        successor.DeviceId.ShouldNotBe(first.DeviceId, "as a new device, not the retired one");
        successor.ReEnrolled.ShouldBeFalse("this is a first enrollment for the new record");

        await using var verify = _fixture.CreateDbContext();

        // The retired record is untouched, and its credentials stay revoked.
        (await verify.Devices.SingleAsync(d => d.Id == first.DeviceId)).Status
            .ShouldBe(DeviceStatus.Retired);
        (await verify.AgentCredentials.CountAsync(c => c.DeviceId == first.DeviceId && c.RevokedAt == null))
            .ShouldBe(0, "retiring revoked them and enrolling elsewhere must not restore them");

        // The successor is active and holds exactly one live credential.
        (await verify.Devices.SingleAsync(d => d.Id == successor.DeviceId)).Status
            .ShouldBe(DeviceStatus.Active);
        (await verify.AgentCredentials.CountAsync(c => c.DeviceId == successor.DeviceId && c.RevokedAt == null))
            .ShouldBe(1, "the new device gets exactly one fresh active credential");

        // Exactly one active row for the machine, which is the real constraint.
        (await verify.Devices.CountAsync(
            d => d.MachineIdentifier == machineId && d.Status == DeviceStatus.Active))
            .ShouldBe(1);
    }

    /// <summary>
    /// Reactivation still works, and still requires a fresh enrollment for a
    /// credential. Kept because it is a separate administrative path from the
    /// successor flow above, and undoing a retirement must remain possible.
    /// </summary>
    /// <remarks>
    /// A reactivated device is ACTIVE again, so its re-enrollment now rides the
    /// re-enrollment approval gate like any other active device's: the first
    /// token attempt parks a pending request and is refused, an administrator
    /// approves it, and the next attempt completes in place. This test walks that
    /// whole path at the service level.
    /// </remarks>
    [Fact]
    public async Task A_reactivated_device_re_enrolls_in_place_after_approval()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, _) = await SeedOrgAndTokenAsync(db);
        var machineId = "m-" + Guid.CreateVersion7().ToString("N");

        var first = await Enrollment(db).EnrollAsync(await IssueTokenSecretAsync(db, org.Id), "RA-PC", machineId, "1.0", null);
        first.Success.ShouldBeTrue();

        var service = new DeviceLifecycleService(db, Audit(db), TimeProvider.System);
        await service.OffboardAsync(org.Id, first.DeviceId, Guid.CreateVersion7(), "admin");

        (await service.ReactivateAsync(org.Id, first.DeviceId, Guid.CreateVersion7(), "admin"))
            .ShouldBe(DeviceLifecycleResult.Success);

        var secret = await IssueTokenSecretAsync(db, org.Id);

        // Active device + token = parked behind the approval gate, not re-enrolled.
        var gated = await Enrollment(db).EnrollAsync(secret, "RA-PC", machineId, "1.0", null);
        gated.Success.ShouldBeFalse("re-enrolling an ACTIVE device waits for an administrator");

        (await activeCredentialCount()).ShouldBe(0, "the gated attempt must not have issued anything");

        // The administrator approves the parked request…
        var requestId = PendingEnrollmentStore.DeriveReEnrollRequestId(org.Id, machineId);
        (await PendingStore().DecideAsync(
                requestId, PendingEnrollmentStatus.Approved, org.Id, tokenSecret: null, "approver@test.local"))
            .ShouldNotBeNull("the gated attempt must have parked a decidable request");

        // …and the agent's own retry completes in place.
        var reenrolled = await Enrollment(db).EnrollAsync(secret, "RA-PC", machineId, "1.0", null);

        reenrolled.Success.ShouldBeTrue("an approved re-enrollment completes on the next retry");
        reenrolled.DeviceId.ShouldBe(first.DeviceId, "reactivation keeps the original record");

        (await activeCredentialCount()).ShouldBe(1, "re-enrollment issues exactly one fresh active credential");

        async Task<int> activeCredentialCount()
        {
            await using var verify = _fixture.CreateDbContext();
            return await verify.AgentCredentials.CountAsync(
                c => c.DeviceId == first.DeviceId && c.RevokedAt == null);
        }
    }

    [Fact]
    public async Task Offboarding_an_unknown_device_reports_not_found()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, _) = await SeedOrgAndTokenAsync(db);
        var service = new DeviceLifecycleService(db, Audit(db), TimeProvider.System);

        (await service.OffboardAsync(org.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), "admin"))
            .ShouldBe(DeviceLifecycleResult.NotFound);
    }

    /// <summary>Issues a fresh enrollment token and returns its usable secret.</summary>
    private static async Task<string> IssueTokenSecretAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db, Guid organizationId)
    {
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(
            organizationId, "t-" + Guid.CreateVersion7().ToString("N"), SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 99));
        await db.SaveChangesAsync();
        return secret;
    }
}
