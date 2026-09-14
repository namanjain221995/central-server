using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.BitLocker;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// BitLocker recovery-key escrow over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are the feature. A recovery password unlocks a machine's disk
/// outright, so what matters is not that escrow works but that everything which
/// should stop a retrieval does: the dedicated permission, the device scope, the
/// step-up password, the rate limit, and the absence of the key from every other
/// response and from the audit trail.
/// </para>
/// <para>
/// The leakage tests assert over whole serialised payloads and whole audit rows
/// rather than named fields, so a field added later that carried key material
/// fails them without anyone having to remember these tests exist.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class BitLockerEscrowEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    /// <summary>Built, not taken from a real machine, so no genuine key is in the repo.</summary>
    private const string RecoveryPassword =
        "011000-011000-011000-011000-011000-011000-011000-011000";

    private const string SecondPassword =
        "022000-022000-022000-022000-022000-022000-022000-022000";

    private static Uri Escrows(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/bitlocker-escrows", UriKind.Relative);

    private static Uri Reveal(Guid escrowId) =>
        new($"/admin/v1/bitlocker-escrows/{escrowId}/reveal", UriKind.Relative);

    private static Uri Delete(Guid escrowId) =>
        new($"/admin/v1/bitlocker-escrows/{escrowId}", UriKind.Relative);

    /// <summary>
    /// A client for an existing seeded account, with the session minted directly.
    /// </summary>
    /// <remarks>
    /// This class deliberately performs no sign-in at all. The login endpoint is
    /// rate limited to three attempts per minute per address and that budget is
    /// shared by every test in the assembly -- LoginRateLimitTests asserts exactly
    /// three succeed, so a single extra sign-in from here fails a test that has
    /// nothing to do with escrow. Minting produces the same token hash and
    /// security stamp the login path would have, so nothing about the session or
    /// its validation is weakened.
    /// </remarks>
    private async Task<HttpClient> ClientAsync(string email) => await MintClientAsync(email);

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
                userAgent: "escrow-tests"));

            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    /// <summary>
    /// A fresh Super Administrator, used by every test that reveals a key.
    /// </summary>
    /// <remarks>
    /// The reveal limiter allows five attempts per user per fifteen minutes and a
    /// successful reveal deliberately does not reset it, so tests sharing one
    /// account exhaust the budget between them and fail each other. Giving each
    /// test its own account isolates them without touching the limit -- weakening
    /// the limit to make tests pass would remove the control being tested.
    /// </remarks>
    private async Task<HttpClient> NewAdminClientAsync()
    {
        var email = $"escrow-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var user = new PlatformUser(org.Id, email, "Escrow Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
                DateTimeOffset.UtcNow);
            user.AssignRole(role.Id);

            // Without this a new administrator is scoped to no devices at all --
            // the scope check is deny-by-default, so an unscoped account sees
            // nothing rather than everything.
            user.GrantAllDeviceScope();

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        return await MintClientAsync(email);
    }

    private sealed record Seeded(Guid DeviceId, string VolumeId, string ProtectorId);

    private async Task<Seeded> SeedAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"esc-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "ESC-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1.3.0", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var volumeId = $"\\\\?\\Volume{{{Guid.NewGuid()}}}\\";
        var protectorId = Guid.NewGuid().ToString();

        db.DeviceBitLockerVolumes.Add(new DeviceBitLockerVolume(
            device.Id, volumeId, "C:", "pv-1", 0, 1, 1, 100, 7, true, protectorId,
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync();
        return new Seeded(device.Id, volumeId, protectorId);
    }

    private static JsonContent Body(Seeded s, string password = RecoveryPassword) =>
        JsonContent.Create(new
        {
            volumeDeviceIdentifier = s.VolumeId,
            keyProtectorId = s.ProtectorId,
            recoveryPassword = password,
        });

    private async Task<Guid> EscrowAsync(HttpClient client, Seeded s, string password = RecoveryPassword)
    {
        var response = await client.PostAsync(Escrows(s.DeviceId), Body(s, password));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_can_do_nothing()
    {
        var s = await SeedAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(Escrows(s.DeviceId)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsync(Escrows(s.DeviceId), Body(s)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(Reveal(Guid.CreateVersion7()), new { currentPassword = "x", justification = "y" }))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The central RBAC claim: seeing encryption state does not let you read keys.
    /// </summary>
    [Theory]
    [InlineData("helpdesk")]
    [InlineData("auditor")]
    public async Task Bitlocker_view_alone_cannot_escrow_reveal_or_delete(string which)
    {
        var s = await SeedAsync();

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var escrowId = await EscrowAsync(admin, s);

        var email = which == "helpdesk"
            ? AdminApiPostgresFixture.HelpdeskEmail
            : AdminApiPostgresFixture.AuditorEmail;

        using var client = await ClientAsync(email);

        // May see that a key exists...
        (await client.GetAsync(Escrows(s.DeviceId))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...and may do nothing else with it.
        (await client.PostAsync(Escrows(s.DeviceId), Body(s)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync(Reveal(escrowId),
                new { currentPassword = AdminApiPostgresFixture.Password, justification = "trying" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.DeleteAsync(Delete(escrowId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_outside_the_callers_scope_is_invisible()
    {
        var inScope = await SeedAsync();
        var outOfScope = await SeedAsync();

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var outOfScopeEscrow = await EscrowAsync(admin, outOfScope);

        var email = $"esc-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"EscScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            (await db.Devices.SingleAsync(candidate => candidate.Id == inScope.DeviceId)).MoveToGroup(group.Id);

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
                DateTimeOffset.UtcNow);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));

        (await client.GetAsync(Escrows(inScope.DeviceId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Escrows(outOfScope.DeviceId))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Quoting another group's escrow id must not reach its key.
        (await client.PostAsJsonAsync(Reveal(outOfScopeEscrow),
                new { currentPassword = AdminApiPostgresFixture.Password, justification = "cross-scope" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- step-up -----------------------------------------------------------

    [Fact]
    public async Task Revealing_requires_the_callers_own_password()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();
        var escrowId = await EscrowAsync(client, s);

        var wrong = await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = "definitely-not-the-password", justification = "recovering a laptop" });

        wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await wrong.Content.ReadAsStringAsync()).ShouldNotContain("011000");
    }

    [Theory]
    [InlineData("", "a justification")]
    [InlineData("password", "")]
    public async Task Revealing_requires_both_a_password_and_a_justification(string password, string justification)
    {
        var s = await SeedAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var escrowId = await EscrowAsync(client, s);

        (await client.PostAsJsonAsync(Reveal(escrowId),
                new { currentPassword = password, justification }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_correct_step_up_returns_the_key_and_records_the_reveal()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();
        var escrowId = await EscrowAsync(client, s);

        var response = await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = AdminApiPostgresFixture.Password, justification = "laptop will not boot" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("recoveryPassword").GetString().ShouldBe(RecoveryPassword);

        // The one response carrying key material must not be cached anywhere.
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        await using var db = _fixture.CreateDbContext();
        var escrow = await db.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == escrowId);
        escrow.RevealedCount.ShouldBe(1);
        escrow.LastRevealedAt.ShouldNotBeNull();
    }

    // ---- storage and rotation ---------------------------------------------

    [Fact]
    public async Task The_stored_value_is_ciphertext_not_the_password()
    {
        var s = await SeedAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var escrowId = await EscrowAsync(client, s);

        await using var db = _fixture.CreateDbContext();
        var escrow = await db.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == escrowId);

        escrow.SealedRecoveryPassword.ShouldNotContain(RecoveryPassword);
        escrow.SealedRecoveryPassword.ShouldNotContain("011000");
        AuditStateRedactor.ContainsSecretShape(escrow.SealedRecoveryPassword).ShouldBeFalse();
    }

    /// <summary>
    /// Replacement supersedes rather than overwrites: a machine restored from an
    /// older backup may need the key that was current then.
    /// </summary>
    [Fact]
    public async Task Replacing_supersedes_the_previous_record_and_keeps_it()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();

        var first = await EscrowAsync(client, s);
        var second = await EscrowAsync(client, s, SecondPassword);

        first.ShouldNotBe(second);

        await using var db = _fixture.CreateDbContext();
        var old = await db.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == first);
        var current = await db.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == second);

        old.IsActive.ShouldBeFalse();
        old.SupersededById.ShouldBe(second);
        current.IsActive.ShouldBeTrue();

        // The superseded key is still retrievable, which is the point of keeping it.
        var response = await client.PostAsJsonAsync(Reveal(first),
            new { currentPassword = AdminApiPostgresFixture.Password, justification = "restored from backup" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("recoveryPassword").GetString().ShouldBe(RecoveryPassword);
    }

    [Fact]
    public async Task Deleting_destroys_the_ciphertext_and_prevents_further_reveals()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();
        var escrowId = await EscrowAsync(client, s);

        (await client.DeleteAsync(Delete(escrowId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var db = _fixture.CreateDbContext();
        var escrow = await db.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == escrowId);

        escrow.DeletedAt.ShouldNotBeNull();
        escrow.SealedRecoveryPassword.ShouldBe(Domain.BitLocker.BitLockerRecoveryEscrow.DeletedCiphertextMarker);

        (await client.PostAsJsonAsync(Reveal(escrowId),
                new { currentPassword = AdminApiPostgresFixture.Password, justification = "after delete" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- validation --------------------------------------------------------

    [Theory]
    [InlineData("011001-011000-011000-011000-011000-011000-011000-011000")] // checksum
    [InlineData("011000-011000-011000")]                                    // shape
    [InlineData("not-a-key")]
    public async Task An_invalid_recovery_password_is_refused_and_never_echoed(string candidate)
    {
        var s = await SeedAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await client.PostAsync(Escrows(s.DeviceId), Body(s, candidate));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(candidate);
    }

    [Fact]
    public async Task Escrowing_against_an_unreported_volume_is_refused()
    {
        var s = await SeedAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var bogus = new Seeded(s.DeviceId, "\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\", s.ProtectorId);

        (await client.PostAsync(Escrows(s.DeviceId), Body(bogus))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- leakage -----------------------------------------------------------

    /// <summary>
    /// The listing exists to say a key is filed, not what it is.
    /// </summary>
    [Fact]
    public async Task The_listing_returns_no_key_and_no_ciphertext()
    {
        var s = await SeedAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var escrowId = await EscrowAsync(client, s);

        await using var db = _fixture.CreateDbContext();
        var ciphertext = (await db.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == escrowId))
            .SealedRecoveryPassword;

        var body = await (await client.GetAsync(Escrows(s.DeviceId))).Content.ReadAsStringAsync();

        body.ShouldNotContain(RecoveryPassword);
        body.ShouldNotContain(ciphertext);
        body.ShouldNotContain("sealed");
        AuditStateRedactor.ContainsSecretShape(body).ShouldBeFalse();
    }

    /// <summary>
    /// The ordinary BitLocker endpoints must be unaffected by escrow existing.
    /// </summary>
    [Fact]
    public async Task The_ordinary_bitlocker_endpoints_expose_nothing_about_the_key()
    {
        var s = await SeedAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        await EscrowAsync(client, s);

        foreach (var uri in new[]
                 {
                     new Uri($"/admin/v1/devices/{s.DeviceId}/bitlocker-volumes", UriKind.Relative),
                     new Uri($"/admin/v1/devices/{s.DeviceId}/bitlocker-readiness", UriKind.Relative),
                 })
        {
            var body = await (await client.GetAsync(uri)).Content.ReadAsStringAsync();

            body.ShouldNotContain(RecoveryPassword);
            body.ShouldNotContain("011000");
            AuditStateRedactor.ContainsSecretShape(body).ShouldBeFalse();
        }
    }

    /// <summary>
    /// The audit trail is append-only and enforced by database triggers, so a
    /// secret written into it cannot be taken back.
    /// </summary>
    [Fact]
    public async Task No_audit_row_contains_the_password_or_the_ciphertext()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();

        var escrowId = await EscrowAsync(client, s);
        await EscrowAsync(client, s, SecondPassword);

        await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = AdminApiPostgresFixture.Password, justification = "audit check" });

        await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = "wrong", justification = "should be denied" });

        await client.DeleteAsync(Delete(escrowId));

        await using var db = _fixture.CreateDbContext();
        var ciphertext = (await db.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == escrowId))
            .SealedRecoveryPassword;

        var rows = await db.AuditLogEntries
            .Where(a => a.Action.StartsWith("bitlocker.recovery_key."))
            .ToListAsync();

        rows.ShouldNotBeEmpty();

        foreach (var row in rows)
        {
            var payload = (row.PreviousState ?? "") + (row.NewState ?? "") + (row.FailureReason ?? "");

            payload.ShouldNotContain(RecoveryPassword);
            payload.ShouldNotContain(SecondPassword);
            payload.ShouldNotContain("011000");
            payload.ShouldNotContain("022000");
            payload.ShouldNotContain(ciphertext);
            payload.ShouldNotContain(AdminApiPostgresFixture.Password);
            AuditStateRedactor.ContainsSecretShape(payload).ShouldBeFalse();
        }
    }

    /// <summary>
    /// A recovery password typed into the justification box is redacted before it
    /// reaches the audit trail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one field on the reveal path that carries free text an operator
    /// composes, and the audit trail is append-only -- a key pasted there, whether
    /// by habit or by accident, could not be removed afterwards. Nothing about the
    /// property name marks it as sensitive, so the only control standing between
    /// that mistake and a permanent record is the redactor's value-shape rule.
    /// </para>
    /// <para>
    /// The benign half of the test is what gives the redacted half its meaning: it
    /// proves the justification really is written to audit state, so the absence of
    /// the shaped value below is redaction rather than a field that was never
    /// stored in the first place.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_recovery_password_typed_into_the_justification_is_redacted()
    {
        const string Benign = "laptop will not boot after a firmware update";

        // Shaped like a recovery password but not one: two groups is enough for the
        // detector, and no genuine key belongs in this repository.
        const string Shaped = "reference 123456-123456 from the ticket";

        // Guards the assertions below against passing vacuously: the value really
        // is one the detector recognises, so its absence downstream is redaction.
        AuditStateRedactor.ContainsSecretShape(Shaped).ShouldBeTrue();
        AuditStateRedactor.ContainsSecretShape(Benign).ShouldBeFalse();

        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();

        var escrowId = await EscrowAsync(client, s);

        (await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = AdminApiPostgresFixture.Password, justification = Benign }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = AdminApiPostgresFixture.Password, justification = Shaped }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();

        // Scoped to this test's own escrow. The database is shared by the whole
        // collection and every other test in it reveals too, so an unfiltered
        // count would depend on execution order.
        var target = escrowId.ToString();

        var states = await db.AuditLogEntries
            .Where(a => a.Action == "bitlocker.recovery_key.revealed" && a.TargetId == target)
            .OrderBy(a => a.OccurredAt)
            .Select(a => a.NewState)
            .ToListAsync();

        states.Count.ShouldBe(2);
        states.ShouldAllBe(state => state != null);

        // Asserted by content rather than by position: two reveals a moment apart
        // can share an ordering key, and which row is which must not decide
        // whether a leak is caught.

        // The field really is audited -- an ordinary reason survives verbatim.
        states.Count(state => state!.Contains(Benign, StringComparison.Ordinal))
            .ShouldBe(1);

        // And the shaped one was redacted rather than stored.
        states.Count(state => state!.Contains(AuditStateRedactor.Placeholder, StringComparison.Ordinal))
            .ShouldBe(1);

        foreach (var state in states)
        {
            state!.ShouldNotContain("123456-123456");
            AuditStateRedactor.ContainsSecretShape(state).ShouldBeFalse();
        }
    }

    // ---- rate limiting -----------------------------------------------------

    /// <summary>
    /// Five reveals per user per fifteen minutes, and a success does not reset it.
    /// </summary>
    /// <remarks>
    /// The "success does not reset" half is the point. A limiter that reset on a
    /// correct reveal would bound only failed attempts -- but a failed reveal
    /// yields nothing, and it is precisely the successful ones that need bounding.
    /// </remarks>
    [Fact]
    public async Task A_user_is_limited_to_five_reveals_per_window()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();
        var escrowId = await EscrowAsync(client, s);

        object Payload(int i) =>
            new { currentPassword = AdminApiPostgresFixture.Password, justification = $"attempt {i}" };

        // Five succeed outright, proving success alone does not replenish budget.
        for (var i = 1; i <= RevealRateLimiter.MaxAttemptsPerWindow; i++)
        {
            (await client.PostAsJsonAsync(Reveal(escrowId), Payload(i)))
                .StatusCode.ShouldBe(HttpStatusCode.OK, $"reveal {i} should be within the limit");
        }

        var blocked = await client.PostAsJsonAsync(Reveal(escrowId), Payload(6));

        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        blocked.Headers.Contains("Retry-After").ShouldBeTrue();

        var body = await blocked.Content.ReadAsStringAsync();
        body.ShouldNotContain(RecoveryPassword);
        body.ShouldNotContain("011000");
    }

    /// <summary>
    /// A refusal is audited, and the audit record carries no key material and no
    /// rate-limit key that could identify anything but the actor and the device.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_reveal_is_audited_without_secrets()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();
        var escrowId = await EscrowAsync(client, s);

        for (var i = 0; i <= RevealRateLimiter.MaxAttemptsPerWindow; i++)
        {
            await client.PostAsJsonAsync(Reveal(escrowId),
                new { currentPassword = AdminApiPostgresFixture.Password, justification = $"attempt {i}" });
        }

        await using var db = _fixture.CreateDbContext();
        var denied = await db.AuditLogEntries
            .Where(a => a.Action == "bitlocker.recovery_key.reveal_denied")
            .ToListAsync();

        denied.ShouldNotBeEmpty();

        foreach (var row in denied)
        {
            var payload = (row.PreviousState ?? "") + (row.NewState ?? "") + (row.FailureReason ?? "");
            payload.ShouldNotContain(RecoveryPassword);
            payload.ShouldNotContain("011000");
            payload.ShouldNotContain(AdminApiPostgresFixture.Password);
        }
    }

    [Fact]
    public async Task Every_operation_is_audited_including_the_refusals()
    {
        var s = await SeedAsync();
        using var client = await NewAdminClientAsync();

        var escrowId = await EscrowAsync(client, s);
        await EscrowAsync(client, s, SecondPassword);
        await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = AdminApiPostgresFixture.Password, justification = "ok" });
        await client.PostAsJsonAsync(Reveal(escrowId),
            new { currentPassword = "wrong", justification = "denied" });
        await client.DeleteAsync(Delete(escrowId));

        await using var db = _fixture.CreateDbContext();
        var actions = await db.AuditLogEntries
            .Where(a => a.Action.StartsWith("bitlocker.recovery_key."))
            .Select(a => a.Action)
            .ToListAsync();

        actions.ShouldContain("bitlocker.recovery_key.escrowed");
        actions.ShouldContain("bitlocker.recovery_key.replaced");
        actions.ShouldContain("bitlocker.recovery_key.revealed");
        actions.ShouldContain("bitlocker.recovery_key.reveal_denied");
        actions.ShouldContain("bitlocker.recovery_key.deleted");
    }
}
