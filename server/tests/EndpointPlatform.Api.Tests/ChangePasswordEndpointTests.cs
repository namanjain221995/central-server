using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Changing an administrator's own password, over real HTTP against real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing assertion is not that the new password works -- it is that
/// <b>every session minted with the old one stops working</b>, including the
/// caller's. If the reason for changing a password is that the old one leaked,
/// a change that left existing sessions alive would be theatre.
/// </para>
/// <para>
/// The tests use a dedicated account and restore its password afterwards, so the
/// shared fixture credentials other suites depend on are left as they were.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ChangePasswordEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri ChangePassword = new("/admin/v1/auth/change-password", UriKind.Relative);

    private const string Strong = "a-perfectly-adequate-passphrase";
    private const string AlsoStrong = "another-entirely-different-one";

    private static JsonContent Body(string? current, string? next, string? confirm = null) =>
        JsonContent.Create(new
        {
            currentPassword = current,
            newPassword = next,
            confirmPassword = confirm ?? next,
        });

    /// <summary>Puts the account's password back, so other suites are unaffected.</summary>
    private async Task RestorePasswordAsync(string email)
    {
        await using var db = _fixture.CreateDbContext();
        var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
        user.Enable();
        user.SetPasswordHash(
            EndpointPlatform.Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
            DateTimeOffset.UtcNow);
        AdminApiPostgresFixture.EnrolMfa(user);
        await db.SaveChangesAsync();
    }

    // ---- the property that matters -----------------------------------------

    /// <summary>
    /// A successful change invalidates every existing session, the caller's
    /// included.
    /// </summary>
    /// <remarks>
    /// Asserted through the API rather than against the session table, because
    /// what matters is that a token stops being *accepted*, not that a column
    /// changed. Two sessions are opened first so this cannot pass by only
    /// revoking the one that made the request.
    /// </remarks>
    [Fact]
    public async Task Changing_the_password_signs_out_every_existing_session()
    {
        var email = AdminApiPostgresFixture.HelpdeskEmail;

        try
        {
            var tokenA = await _fixture.SignInAsync(email);
            var tokenB = await _fixture.SignInAsync(email);

            // Both sessions work beforehand.
            foreach (var t in new[] { tokenA, tokenB })
            {
                (await _fixture.CreateClientFor(t).GetAsync("/admin/v1/auth/me"))
                    .StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            var response = await _fixture.CreateClientFor(tokenA)
                .PostAsync(ChangePassword, Body(AdminApiPostgresFixture.Password, Strong));

            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("changed").GetBoolean().ShouldBeTrue();
            body.GetProperty("sessionsRevoked").GetInt32().ShouldBeGreaterThanOrEqualTo(2);

            // The session that made the request is dead too -- no exemption.
            foreach (var t in new[] { tokenA, tokenB })
            {
                (await _fixture.CreateClientFor(t).GetAsync("/admin/v1/auth/me"))
                    .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
            }

            // And the new password genuinely works.
            var fresh = await _fixture.SignInAsync(email, Strong);
            (await _fixture.CreateClientFor(fresh).GetAsync("/admin/v1/auth/me"))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await RestorePasswordAsync(email);
        }
    }

    /// <summary>The old password stops working.</summary>
    [Fact]
    public async Task The_previous_password_no_longer_signs_in()
    {
        var email = AdminApiPostgresFixture.AuditorEmail;

        try
        {
            var token = await _fixture.SignInAsync(email);
            (await _fixture.CreateClientFor(token)
                .PostAsync(ChangePassword, Body(AdminApiPostgresFixture.Password, AlsoStrong)))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var old = await _fixture.Factory.CreateClient().PostAsJsonAsync(
                new Uri("/admin/v1/auth/login", UriKind.Relative),
                new { email, password = AdminApiPostgresFixture.Password });

            old.IsSuccessStatusCode.ShouldBeFalse();
        }
        finally
        {
            await RestorePasswordAsync(email);
        }
    }

    // ---- refusals ----------------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_cannot_change_a_password()
    {
        var response = await _fixture.Factory.CreateClient()
            .PostAsync(ChangePassword, Body(AdminApiPostgresFixture.Password, Strong));

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A live session is not sufficient; the current password is re-verified.
    /// </summary>
    /// <remarks>
    /// This is what stops a borrowed or stolen session from locking the real
    /// owner out of their own account.
    /// </remarks>
    [Fact]
    public async Task A_wrong_current_password_is_refused_even_with_a_valid_session()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await _fixture.CreateClientFor(token)
            .PostAsync(ChangePassword, Body("not-the-current-password", Strong));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The session still works: a refused change must not sign anyone out.
        (await _fixture.CreateClientFor(token).GetAsync("/admin/v1/auth/me"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_mismatched_confirmation_is_refused_before_anything_is_verified()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await _fixture.CreateClientFor(token).PostAsync(
            ChangePassword, Body(AdminApiPostgresFixture.Password, Strong, "something-else-entirely"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _fixture.CreateClientFor(token).GetAsync("/admin/v1/auth/me"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("")]
    public async Task A_password_failing_the_policy_is_refused(string weak)
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await _fixture.CreateClientFor(token)
            .PostAsync(ChangePassword, Body(AdminApiPostgresFixture.Password, weak));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _fixture.CreateClientFor(token).GetAsync("/admin/v1/auth/me"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Re-setting the same password is refused rather than quietly accepted.
    /// </summary>
    /// <remarks>
    /// It would rotate the security stamp and destroy every session for no
    /// security gain, which from the operator's side is indistinguishable from
    /// the platform malfunctioning.
    /// </remarks>
    [Fact]
    public async Task Reusing_the_current_password_is_refused()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await _fixture.CreateClientFor(token).PostAsync(
            ChangePassword, Body(AdminApiPostgresFixture.Password, AdminApiPostgresFixture.Password));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _fixture.CreateClientFor(token).GetAsync("/admin/v1/auth/me"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- audit carries no secret -------------------------------------------

    /// <summary>
    /// The audit records that a change happened, and nothing that helps guess it.
    /// </summary>
    /// <remarks>
    /// Checks the whole row -- action, target, and both state documents -- for
    /// any occurrence of either password. An audit trail is append-only and
    /// widely readable; a secret written into it cannot be taken back.
    /// </remarks>
    [Fact]
    public async Task The_audit_entry_contains_no_password_material()
    {
        var email = AdminApiPostgresFixture.SuperAdminEmail;

        try
        {
            var token = await _fixture.SignInAsync(email);
            (await _fixture.CreateClientFor(token).PostAsync(ChangePassword, Body(AdminApiPostgresFixture.Password, Strong)))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            await using var db = _fixture.CreateDbContext();
            var entries = await db.AuditLogEntries.AsNoTracking()
                .Where(e => e.Action.StartsWith("platform.user.password"))
                .ToListAsync();

            entries.ShouldNotBeEmpty();

            foreach (var e in entries)
            {
                var whole = string.Join('\n',
                    e.Action, e.ActorDisplay, e.TargetDisplay, e.PreviousState, e.NewState, e.FailureReason);

                whole.ShouldNotContain(Strong);
                whole.ShouldNotContain(AdminApiPostgresFixture.Password);
                // Nor a hash of it: an Argon2/PBKDF2 encoding is still credential
                // material and does not belong in an append-only trail.
                whole.ShouldNotContain("$argon", Case.Insensitive);
                whole.ShouldNotContain("pbkdf2", Case.Insensitive);
            }
        }
        finally
        {
            await RestorePasswordAsync(email);
        }
    }
}
