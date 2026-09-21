using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Re-arming automatic escrow for a protector that stopped retrying.
/// </summary>
/// <remarks>
/// <para>
/// The operation grants no access to any key -- it clears a failure count so the
/// endpoint may try again -- but it is still an administrator action on a
/// security feature, so it carries the same permission as filing a key, is device
/// scoped, and is audited.
/// </para>
/// <para>
/// It is addressed by <em>attempt</em> id rather than escrow id, and that is the
/// point of the resource: an escrow row exists only once a key has been filed, so
/// a protector that exhausted its attempts -- exactly the case this exists for --
/// has no escrow to name.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class EscrowAttemptResetTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string Volume = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\";

    private static Uri Reset(Guid attemptId) =>
        new($"/admin/v1/bitlocker-escrow-attempts/{attemptId}/reset", UriKind.Relative);

    private static Uri Attempts(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/bitlocker-escrow-attempts", UriKind.Relative);

    private async Task<HttpClient> NewAdminClientAsync(bool allDeviceScope = true)
    {
        var email = $"reset-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var user = new PlatformUser(org.Id, email, "Reset Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
                DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);

            if (allDeviceScope)
            {
                user.GrantAllDeviceScope();
            }

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        return await MintClientAsync(email);
    }

    /// <summary>
    /// Mints a session directly rather than signing in. The login endpoint is rate
    /// limited and that budget is shared across this assembly, so a class that
    /// signed in per test would fail whatever ran next.
    /// </summary>
    private async Task<HttpClient> MintClientAsync(string email)
    {
        var token = SecretGenerator.GenerateSecret();

        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);

            db.AdminSessions.Add(new AdminSession(
                user.Id,
                SecretGenerator.HashSecret(token),
                user.SecurityStamp,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1),
                sourceIp: null,
                userAgent: "reset-tests"));

            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    /// <summary>Seeds a device with one attempt row in the requested state.</summary>
    private async Task<(Guid DeviceId, Guid AttemptId)> SeedAsync(
        BitLockerEscrowAttemptState state)
    {
        await using var db = _fixture.CreateDbContext();

        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            org.Id, $"rst-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", now.AddHours(1), 9);

        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "RST-PC", "m-" + Guid.CreateVersion7().ToString("N"), "1.4.0", null, token.Id, now);

        db.Devices.Add(device);

        var attempt = new BitLockerEscrowAttempt(
            org.Id, device.Id, Volume, Guid.NewGuid().ToString(), now);

        // Drive it into the requested state through the domain, so the row is the
        // shape the runtime would actually have produced.
        if (state != BitLockerEscrowAttemptState.Pending)
        {
            var failures = state == BitLockerEscrowAttemptState.RetryExhausted
                ? BitLockerEscrowAttempt.MaxAttempts
                : 1;

            for (var i = 0; i < failures; i++)
            {
                attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, now);
            }
        }

        if (state == BitLockerEscrowAttemptState.Escrowed)
        {
            attempt.RecordSuccess(now);
        }

        db.BitLockerEscrowAttempts.Add(attempt);
        await db.SaveChangesAsync();

        return (device.Id, attempt.Id);
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_cannot_reset()
    {
        var (_, attemptId) = await SeedAsync(BitLockerEscrowAttemptState.RetryExhausted);

        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsync(Reset(attemptId), null))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Seeing that collection stopped is a view concern; restarting it is not.
    /// </summary>
    [Fact]
    public async Task Bitlocker_view_alone_cannot_reset()
    {
        var (deviceId, attemptId) = await SeedAsync(BitLockerEscrowAttemptState.RetryExhausted);

        // Helpdesk holds bitlocker.view and deliberately no recovery-key permission.
        using var client = await MintClientAsync(AdminApiPostgresFixture.HelpdeskEmail);

        // Reading the state is allowed...
        (await client.GetAsync(Attempts(deviceId))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...re-arming it is not.
        (await client.PostAsync(Reset(attemptId), null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await AssertStillExhaustedAsync(attemptId);
    }

    /// <summary>
    /// Scope is resolved from the attempt's own device, so quoting another group's
    /// attempt id reveals nothing -- not even that it exists.
    /// </summary>
    [Fact]
    public async Task An_attempt_outside_the_callers_scope_is_invisible()
    {
        var (_, attemptId) = await SeedAsync(BitLockerEscrowAttemptState.RetryExhausted);

        using var client = await NewAdminClientAsync(allDeviceScope: false);

        (await client.PostAsync(Reset(attemptId), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await AssertStillExhaustedAsync(attemptId);
    }

    [Fact]
    public async Task An_unknown_attempt_is_not_found()
    {
        using var client = await NewAdminClientAsync();

        (await client.PostAsync(Reset(Guid.CreateVersion7()), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- behaviour ---------------------------------------------------------

    [Fact]
    public async Task An_exhausted_protector_is_re_armed()
    {
        var (_, attemptId) = await SeedAsync(BitLockerEscrowAttemptState.RetryExhausted);

        using var client = await NewAdminClientAsync();

        (await client.PostAsync(Reset(attemptId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var attempt = await db.BitLockerEscrowAttempts.SingleAsync(a => a.Id == attemptId);

        attempt.State.ShouldBe(BitLockerEscrowAttemptState.Pending);
        attempt.AttemptCount.ShouldBe(0);
        attempt.LastFailure.ShouldBe(BitLockerEscrowFailureCategory.None);
        attempt.ResetByUserId.ShouldNotBeNull();

        // Due again, which is the entire point of the operation.
        attempt.IsDue(DateTimeOffset.UtcNow).ShouldBeTrue();
    }

    /// <summary>
    /// Resetting something that is not stopped would silently hand it extra
    /// attempts, so it is refused rather than quietly accepted.
    /// </summary>
    [Fact]
    public async Task A_protector_that_is_not_stopped_cannot_be_reset()
    {
        var (_, attemptId) = await SeedAsync(BitLockerEscrowAttemptState.Escrowed);

        using var client = await NewAdminClientAsync();

        (await client.PostAsync(Reset(attemptId), null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary>Only the named protector is re-armed; its neighbours are untouched.</summary>
    [Fact]
    public async Task Only_the_intended_protector_is_reset()
    {
        var (deviceId, first) = await SeedAsync(BitLockerEscrowAttemptState.RetryExhausted);

        Guid second;
        await using (var db = _fixture.CreateDbContext())
        {
            var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
            var now = DateTimeOffset.UtcNow;

            var other = new BitLockerEscrowAttempt(
                device.OrganizationId, deviceId, Volume, Guid.NewGuid().ToString(), now);

            for (var i = 0; i < BitLockerEscrowAttempt.MaxAttempts; i++)
            {
                other.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, now);
            }

            db.BitLockerEscrowAttempts.Add(other);
            await db.SaveChangesAsync();
            second = other.Id;
        }

        using var client = await NewAdminClientAsync();
        (await client.PostAsync(Reset(first), null)).EnsureSuccessStatusCode();

        await using var verify = _fixture.CreateDbContext();

        (await verify.BitLockerEscrowAttempts.SingleAsync(a => a.Id == first))
            .State.ShouldBe(BitLockerEscrowAttemptState.Pending);

        (await verify.BitLockerEscrowAttempts.SingleAsync(a => a.Id == second))
            .State.ShouldBe(BitLockerEscrowAttemptState.RetryExhausted);
    }

    // ---- audit and leakage -------------------------------------------------

    [Fact]
    public async Task A_reset_is_audited_without_any_key_material()
    {
        var (_, attemptId) = await SeedAsync(BitLockerEscrowAttemptState.RetryExhausted);

        using var client = await NewAdminClientAsync();
        (await client.PostAsync(Reset(attemptId), null)).EnsureSuccessStatusCode();

        await using var db = _fixture.CreateDbContext();

        var row = await db.AuditLogEntries
            .Where(a => a.Action == "bitlocker.recovery_key.auto_escrow_reset")
            .OrderByDescending(a => a.OccurredAt)
            .FirstAsync();

        row.TargetId.ShouldBe(attemptId.ToString());

        var payload = (row.PreviousState ?? "") + (row.NewState ?? "") + (row.FailureReason ?? "");

        payload.ShouldContain("RetryExhausted");
        payload.ShouldNotMatch(@"\d{6}-\d{6}");
        AuditStateRedactor.ContainsSecretShape(payload).ShouldBeFalse();
    }

    /// <summary>
    /// The listing feeds the console's status column, so it must carry state and
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task The_attempt_listing_carries_no_key_material()
    {
        var (deviceId, _) = await SeedAsync(BitLockerEscrowAttemptState.RetryExhausted);

        using var client = await NewAdminClientAsync();

        var response = await client.GetAsync(Attempts(deviceId));
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        raw.ShouldContain("RetryExhausted");

        raw.ShouldNotContain("sealed");
        raw.ShouldNotContain("ciphertext");
        raw.ShouldNotContain("wrappedKey");
        raw.ShouldNotMatch(@"\d{6}-\d{6}");
    }

    // ---- eligibility -------------------------------------------------------

    /// <summary>
    /// The fix for a real misreport: eligibility comes from the credential, not
    /// from whether any attempt happens to exist.
    /// </summary>
    /// <remarks>
    /// Inferring it from attempt rows told operators that a correctly pinned
    /// device needed re-enrolling, purely because the agent had not reached it
    /// yet. The two states mean opposite things -- one needs action, the other
    /// needs only time -- so they are now sourced separately.
    /// </remarks>
    [Fact]
    public async Task A_pinned_device_with_no_attempts_yet_is_still_eligible()
    {
        var deviceId = await SeedDeviceWithCredentialAsync(pinned: true);

        using var client = await NewAdminClientAsync();
        var status = await client.GetFromJsonAsync<StatusResponse>(Attempts(deviceId));

        status!.Attempts.ShouldBeEmpty("this device has never been contacted");

        // ...and is nonetheless perfectly able to participate.
        status.Eligible.ShouldBeTrue();
        status.SealingKeyFingerprint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unpinned_device_is_reported_ineligible()
    {
        var deviceId = await SeedDeviceWithCredentialAsync(pinned: false);

        using var client = await NewAdminClientAsync();
        var status = await client.GetFromJsonAsync<StatusResponse>(Attempts(deviceId));

        status!.Eligible.ShouldBeFalse();
        status.SealingKeyFingerprint.ShouldBeNull();
    }

    /// <summary>
    /// Revoking the credential withdraws eligibility with it, which is what makes
    /// re-enrollment the way back rather than an edit.
    /// </summary>
    [Fact]
    public async Task Revoking_the_credential_makes_the_device_ineligible()
    {
        var deviceId = await SeedDeviceWithCredentialAsync(pinned: true);

        await using (var db = _fixture.CreateDbContext())
        {
            foreach (var credential in await db.AgentCredentials
                         .Where(c => c.DeviceId == deviceId && c.RevokedAt == null)
                         .ToListAsync())
            {
                credential.Revoke(DateTimeOffset.UtcNow);
            }

            await db.SaveChangesAsync();
        }

        using var client = await NewAdminClientAsync();
        var status = await client.GetFromJsonAsync<StatusResponse>(Attempts(deviceId));

        status!.Eligible.ShouldBeFalse();
    }

    private sealed record StatusResponse(
        bool Eligible, string? SealingKeyFingerprint, IReadOnlyList<AttemptResponse> Attempts);

    private sealed record AttemptResponse(Guid Id, string State);

    /// <summary>A device with an active credential, pinned or not.</summary>
    private async Task<Guid> SeedDeviceWithCredentialAsync(bool pinned)
    {
        await using var db = _fixture.CreateDbContext();

        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            org.Id, $"elg-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", now.AddHours(1), 9);

        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "ELG-PC", "m-" + Guid.CreateVersion7().ToString("N"), "1.4.0", null, token.Id, now);

        db.Devices.Add(device);

        var credential = new AgentCredential(
            device.Id, SecretGenerator.GenerateKeyId(),
            SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()), now);

        if (pinned)
        {
            credential.PinSealingKey(new string('a', 64));
        }

        db.AgentCredentials.Add(credential);
        await db.SaveChangesAsync();

        return device.Id;
    }

    private async Task AssertStillExhaustedAsync(Guid attemptId)
    {
        await using var db = _fixture.CreateDbContext();

        (await db.BitLockerEscrowAttempts.SingleAsync(a => a.Id == attemptId))
            .State.ShouldBe(BitLockerEscrowAttemptState.RetryExhausted);
    }
}
