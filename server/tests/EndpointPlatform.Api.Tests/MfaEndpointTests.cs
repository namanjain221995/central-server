using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The whole multi-factor journey, over HTTP: enrol, sign in with a code, and
/// the refusals that make the second factor worth having.
/// </summary>
/// <remarks>
/// <para>
/// The only class here that deliberately creates an account WITHOUT calling
/// <c>AdminApiPostgresFixture.EnrolMfa</c> — enrolment is what it is testing.
/// Everywhere else an un-enrolled account would just produce 403s.
/// </para>
/// <para>
/// Each test makes its own administrator. Enrolment is one-per-account and the
/// replay guard is per-account, so sharing one would make these tests depend on
/// the order they run in.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class MfaEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    /// <summary>Creates an administrator with a password and NO second factor.</summary>
    private async Task<string> CreateUnenrolledAsync()
    {
        var email = $"mfa-{Guid.CreateVersion7():N}@test.local";

        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var role = await db.Roles.SingleAsync(r => r.IsBuiltIn && r.Key == SystemRoles.ItAdministrator);

        var user = new PlatformUser(org.Id, email, "MFA Test");
        user.SetPasswordHash(
            PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
        user.AssignRole(role.Id);
        user.GrantAllDeviceScope();

        db.PlatformUsers.Add(user);
        await db.SaveChangesAsync();

        return email;
    }

    /// <summary>Signs in with a password only, returning the raw response.</summary>
    private async Task<JsonElement> PasswordSignInAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email, password = AdminApiPostgresFixture.Password });

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ------------------------------------------------------------- enrolment

    /// <summary>
    /// An un-enrolled administrator signs in, but can reach nothing except
    /// enrolment.
    /// </summary>
    /// <remarks>
    /// This is what "mandatory" means in practice. The session is real — the
    /// password was correct — and every endpoint outside the allowlist answers
    /// 403 with a marker the console can route on.
    /// </remarks>
    [Fact]
    public async Task An_unenrolled_administrator_is_confined_to_enrolment()
    {
        var email = await CreateUnenrolledAsync();
        using var anonymous = _fixture.Factory.CreateClient();

        var body = await PasswordSignInAsync(anonymous, email);
        body.TryGetProperty("mfaRequired", out _).ShouldBeFalse();

        using var client = _fixture.CreateClientFor(body.GetProperty("sessionToken").GetString()!);

        var me = await client.GetFromJsonAsync<JsonElement>(new Uri("/admin/v1/auth/me", UriKind.Relative));
        me.GetProperty("mfaEnrolmentRequired").GetBoolean().ShouldBeTrue();

        var blocked = await client.GetAsync(new Uri("/admin/v1/devices", UriKind.Relative));
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("mfaEnrolmentRequired").GetBoolean().ShouldBeTrue();
    }

    /// <summary>Enrol, confirm, and the account is no longer confined.</summary>
    [Fact]
    public async Task Enrolment_issues_a_secret_recovery_codes_and_lifts_the_restriction()
    {
        var email = await CreateUnenrolledAsync();
        using var anonymous = _fixture.Factory.CreateClient();
        var body = await PasswordSignInAsync(anonymous, email);

        using var client = _fixture.CreateClientFor(body.GetProperty("sessionToken").GetString()!);

        var start = await client.PostAsync(new Uri("/admin/v1/auth/mfa/enroll", UriKind.Relative), null);
        start.EnsureSuccessStatusCode();
        var enrolment = await start.Content.ReadFromJsonAsync<JsonElement>();

        var secret = enrolment.GetProperty("secret").GetString()!;
        secret.ShouldNotBeNullOrWhiteSpace();
        enrolment.GetProperty("otpAuthUri").GetString().ShouldStartWith("otpauth://totp/");
        enrolment.GetProperty("qrCodeSvg").GetString()!.ShouldContain("<svg");

        // An enrolment that has not been confirmed is NOT a second factor: the
        // account is still confined until a code proves the app works.
        var stillBlocked = await client.GetAsync(new Uri("/admin/v1/devices", UriKind.Relative));
        stillBlocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var confirm = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/confirm", UriKind.Relative),
            new { code = Code(secret) });

        confirm.EnsureSuccessStatusCode();
        var codes = (await confirm.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToArray();

        codes.Length.ShouldBe(MfaService.RecoveryCodeCount);
        codes.ShouldBeUnique();

        var allowed = await client.GetAsync(new Uri("/admin/v1/devices", UriKind.Relative));
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Confirming_with_a_wrong_code_is_refused()
    {
        var email = await CreateUnenrolledAsync();
        using var anonymous = _fixture.Factory.CreateClient();
        var body = await PasswordSignInAsync(anonymous, email);
        using var client = _fixture.CreateClientFor(body.GetProperty("sessionToken").GetString()!);

        await client.PostAsync(new Uri("/admin/v1/auth/mfa/enroll", UriKind.Relative), null);

        var confirm = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/confirm", UriKind.Relative),
            new { code = "000000" });

        confirm.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------- sign-in

    /// <summary>The password alone no longer produces a session.</summary>
    [Fact]
    public async Task An_enrolled_account_gets_a_challenge_not_a_session()
    {
        var email = await CreateUnenrolledAsync();
        await EnrolAsync(email);

        using var client = _fixture.Factory.CreateClient();
        var body = await PasswordSignInAsync(client, email);

        body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        body.GetProperty("challengeToken").GetString().ShouldNotBeNullOrWhiteSpace();

        // The crux: no session came back. A caller that ignored mfaRequired must
        // not find anything usable in this response.
        body.TryGetProperty("sessionToken", out _).ShouldBeFalse();
        body.TryGetProperty("permissions", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_correct_code_completes_the_sign_in()
    {
        var email = await CreateUnenrolledAsync();
        await EnrolAsync(email);

        using var client = _fixture.Factory.CreateClient();
        var challenge = await PasswordSignInAsync(client, email);

        var verified = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new
            {
                challengeToken = challenge.GetProperty("challengeToken").GetString(),
                code = Code(AdminApiPostgresFixture.TotpSecret),
            });

        verified.EnsureSuccessStatusCode();
        var session = await verified.Content.ReadFromJsonAsync<JsonElement>();
        session.GetProperty("sessionToken").GetString().ShouldNotBeNullOrWhiteSpace();
        session.GetProperty("email").GetString().ShouldBe(email);
    }

    /// <summary>
    /// A code cannot be used twice, even inside the window where it is still
    /// arithmetically valid.
    /// </summary>
    /// <remarks>
    /// The reason <c>PlatformUser.TotpLastCounter</c> exists. Without it a code
    /// read off somebody's screen stays usable for up to ninety seconds, which is
    /// ample time to type it in somewhere else.
    /// </remarks>
    [Fact]
    public async Task A_code_cannot_be_used_twice()
    {
        var email = await CreateUnenrolledAsync();
        await EnrolAsync(email);

        var code = Code(AdminApiPostgresFixture.TotpSecret);
        using var client = _fixture.Factory.CreateClient();

        var first = await PasswordSignInAsync(client, email);
        var firstVerify = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = first.GetProperty("challengeToken").GetString(), code });
        firstVerify.EnsureSuccessStatusCode();

        // A second, entirely fresh challenge — and the same code.
        var second = await PasswordSignInAsync(client, email);
        var secondVerify = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = second.GetProperty("challengeToken").GetString(), code });

        secondVerify.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_burns_the_challenge_after_five_tries()
    {
        var email = await CreateUnenrolledAsync();
        await EnrolAsync(email);

        using var client = _fixture.Factory.CreateClient();
        var challenge = await PasswordSignInAsync(client, email);
        var token = challenge.GetProperty("challengeToken").GetString();

        for (var attempt = 0; attempt < AdminMfaChallenge.MaxAttempts; attempt++)
        {
            var wrong = await client.PostAsJsonAsync(
                new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
                new { challengeToken = token, code = "000000" });

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Out of attempts: even the RIGHT code is now refused on this challenge.
        var correct = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = token, code = Code(AdminApiPostgresFixture.TotpSecret) });

        correct.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_challenge_token_is_refused()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = SecretGenerator.GenerateSecret(), code = "000000" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------- recovery

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_then_never_again()
    {
        var email = await CreateUnenrolledAsync();
        var codes = await EnrolAsync(email);
        var recovery = codes[0];

        using var client = _fixture.Factory.CreateClient();

        var first = await PasswordSignInAsync(client, email);
        var used = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = first.GetProperty("challengeToken").GetString(), code = recovery });

        used.EnsureSuccessStatusCode();

        var second = await PasswordSignInAsync(client, email);
        var reused = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = second.GetProperty("challengeToken").GetString(), code = recovery });

        reused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A mistyped six-digit code does not consume a recovery code.
    /// </summary>
    /// <remarks>
    /// The reason the server only tries recovery codes when the input is not six
    /// digits. Without that, fumbling the authenticator code would silently spend
    /// the codes somebody is keeping for an emergency.
    /// </remarks>
    [Fact]
    public async Task A_mistyped_totp_code_does_not_consume_a_recovery_code()
    {
        var email = await CreateUnenrolledAsync();
        var codes = await EnrolAsync(email);

        using var client = _fixture.Factory.CreateClient();
        var challenge = await PasswordSignInAsync(client, email);

        await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = challenge.GetProperty("challengeToken").GetString(), code = "000000" });

        await using var db = _fixture.CreateDbContext();
        var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
        var unused = await db.MfaRecoveryCodes.CountAsync(c => c.UserId == user.Id && c.UsedAt == null);

        unused.ShouldBe(codes.Length);
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Enrols an account through the real endpoints and returns its recovery codes.
    /// </summary>
    private async Task<string[]> EnrolAsync(string email)
    {
        using var anonymous = _fixture.Factory.CreateClient();
        var body = await PasswordSignInAsync(anonymous, email);
        using var client = _fixture.CreateClientFor(body.GetProperty("sessionToken").GetString()!);

        // The shared fixture secret, so the codes a test computes are valid for
        // this account too.
        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
            AdminApiPostgresFixture.EnrolMfa(user);
            await db.SaveChangesAsync();
        }

        // Re-confirm through the endpoint so the recovery codes are issued the way
        // a real enrolment issues them.
        var confirm = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/mfa/recovery-codes", UriKind.Relative),
            new { });

        confirm.EnsureSuccessStatusCode();

        return (await confirm.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToArray();
    }

    private static string Code(string base32Secret) =>
        Totp.ComputeCode(Totp.FromBase32(base32Secret), Totp.CounterFor(DateTimeOffset.UtcNow));
}
