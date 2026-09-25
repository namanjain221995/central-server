using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The Phase 3 server-side enforcement matrix, exercised over real HTTP against
/// real PostgreSQL with sessions created through the real login endpoint:
/// unauthenticated callers get nothing; Helpdesk cannot perform admin
/// operations; Auditor cannot mutate; IT Admin and Super Admin can.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class RbacEnforcementTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Devices = new("/admin/v1/devices", UriKind.Relative);
    private static readonly Uri Tokens = new("/admin/v1/enrollment-tokens", UriKind.Relative);

    private static StringContent TokenBody() => new(
        """{"name":"rbac-test","lifetimeHours":1,"maxUses":1}""",
        System.Text.Encoding.UTF8,
        "application/json");

    // ---------------------------------------------------------- unauthorized

    [Theory]
    [InlineData("/admin/v1/devices")]
    [InlineData("/admin/v1/devices/counts")]
    [InlineData("/admin/v1/enrollment-tokens")]
    [InlineData("/admin/v1/auth/me")]
    [InlineData("/admin/v1/chrome/overview")]
    public async Task An_unauthenticated_caller_cannot_reach_any_privileged_endpoint(string path)
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_garbage_bearer_token_is_rejected()
    {
        using var client = _fixture.CreateClientFor(new string('a', 64));

        var response = await client.GetAsync(Devices);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_disabled_account_cannot_sign_in()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email = AdminApiPostgresFixture.DisabledEmail, password = AdminApiPostgresFixture.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------- helpdesk

    [Fact]
    public async Task Helpdesk_can_view_devices()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Helpdesk_cannot_issue_enrollment_tokens()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(Tokens, TokenBody());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "issuing enrollment tokens admits machines into management - not a helpdesk operation");
    }

    [Fact]
    public async Task Helpdesk_cannot_even_list_enrollment_tokens()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(Tokens)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------------- auditor

    [Fact]
    public async Task Auditor_can_view_devices()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Auditor_cannot_mutate_anything()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(Tokens, TokenBody()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Any device id: authorization must reject before the handler ever looks
        // the device up, so a 403 (not 404) proves the permission gate fired.
        (await client.PostAsync(
                new Uri($"/admin/v1/devices/{Guid.CreateVersion7()}/refresh-inventory", UriKind.Relative),
                content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Offboarding (device.retire) is destructive; the gate must fire for an auditor.
        (await client.PostAsync(
                new Uri($"/admin/v1/devices/{Guid.CreateVersion7()}/offboard", UriKind.Relative),
                content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------- it administrator

    [Fact]
    public async Task It_administrator_can_perform_allowed_operations()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var issue = await client.PostAsync(Tokens, TokenBody());
        issue.StatusCode.ShouldBe(HttpStatusCode.OK, "IT Administrator holds enrollment_token.issue");
    }

    // ------------------------------------------------------- super admin

    [Fact]
    public async Task Super_administrator_can_perform_all_exposed_operations()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Tokens)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(Tokens, TokenBody())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("/admin/v1/auth/me", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ----------------------------------------------------------- sessions

    [Fact]
    public async Task Disabling_a_user_kills_their_outstanding_sessions()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var dbContext = _fixture.CreateDbContext())
        {
            var user = await dbContext.PlatformUsers
                .SingleAsync(u => u.Email == AdminApiPostgresFixture.ItAdminEmail);
            user.Disable();
            await dbContext.SaveChangesAsync();
        }

        try
        {
            (await client.GetAsync(Devices)).StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                "disable rotates the security stamp, which must invalidate live sessions immediately");
        }
        finally
        {
            // Restore for the other tests in this collection.
            await using var dbContext = _fixture.CreateDbContext();
            var user = await dbContext.PlatformUsers
                .SingleAsync(u => u.Email == AdminApiPostgresFixture.ItAdminEmail);
            user.Enable();
            await dbContext.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Logout_revokes_the_session_server_side()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(new Uri("/admin/v1/auth/logout", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.GetAsync(Devices)).StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a signed-out token must be dead server-side even if the client kept a copy");
    }

    // -------------------------------------------------------------- audit

    [Fact]
    public async Task A_denied_operation_is_recorded_in_the_audit_trail()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        await client.PostAsync(Tokens, TokenBody());

        await using var dbContext = _fixture.CreateDbContext();
        var denial = await dbContext.AuditLogEntries
            .Where(a => a.Action == "authz.denied"
                        && a.ActorDisplay == AdminApiPostgresFixture.AuditorEmail)
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync();

        denial.ShouldNotBeNull("an authenticated user hitting a permission wall is a security signal");
        denial.Result.ShouldBe(Domain.Auditing.AuditResult.Denied);
        denial.RequiredPermission.ShouldNotBeNull();
        denial.RequiredPermission.ShouldContain("enrollment_token.issue");
    }

    [Fact]
    public async Task Failed_sign_ins_are_audited_and_lock_the_account()
    {
        // A user of its own so lockout does not disturb other tests.
        Guid userId;
        await using (var dbContext = _fixture.CreateDbContext())
        {
            var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var user = new Domain.Identity.PlatformUser(organization.Id, "lockout@test.local", "Lockout Target");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
                DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            dbContext.PlatformUsers.Add(user);
            await dbContext.SaveChangesAsync();
            userId = user.Id;
        }

        using var client = _fixture.Factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                new Uri("/admin/v1/auth/login", UriKind.Relative),
                new { email = "lockout@test.local", password = "definitely-wrong" });

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Even the RIGHT password is now refused: the account is locked.
        var lockedAttempt = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email = "lockout@test.local", password = AdminApiPostgresFixture.Password });

        lockedAttempt.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using (var verify = _fixture.CreateDbContext())
        {
            var user = await verify.PlatformUsers.SingleAsync(u => u.Id == userId);
            user.Status.ShouldBe(Domain.Identity.PlatformUserStatus.Locked);

            var failures = await verify.AuditLogEntries
                .CountAsync(a => a.Action == "auth.sign_in"
                                 && a.Result == Domain.Auditing.AuditResult.Failure
                                 && a.ActorDisplay == "lockout@test.local");

            failures.ShouldBeGreaterThanOrEqualTo(5);
        }
    }

    [Fact]
    public async Task Login_responses_do_not_reveal_whether_the_account_exists()
    {
        using var client = _fixture.Factory.CreateClient();

        var unknown = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email = "nobody@test.local", password = "whatever-1234" });

        var wrongPassword = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email = AdminApiPostgresFixture.HelpdeskEmail, password = "whatever-1234" });

        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrongPassword.StatusCode.ShouldBe(unknown.StatusCode);

        static async Task<string> Normalize(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return System.Text.Json.JsonSerializer.Serialize(
                document.RootElement.EnumerateObject()
                    .Where(p => p.Name != "correlationId")
                    .ToDictionary(p => p.Name, p => p.Value.ToString()));
        }

        (await Normalize(wrongPassword)).ShouldBe(await Normalize(unknown));
    }

    [Fact]
    public async Task Auditor_cannot_queue_a_device_restart()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(
            new Uri($"/admin/v1/devices/{Guid.CreateVersion7()}/actions/restart", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "restart is a high-impact action an auditor must never perform");
    }

    [Fact]
    public async Task Helpdesk_can_lock_but_cannot_shut_down_a_device()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        // Helpdesk holds device.lock but not device.shutdown. Both target a
        // non-existent device; lock reaches the handler (404), shutdown is blocked
        // at authorization (403) - the status distinguishes the two.
        var lockResp = await client.PostAsync(
            new Uri($"/admin/v1/devices/{Guid.CreateVersion7()}/actions/lock", UriKind.Relative), content: null);
        lockResp.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var shutdownResp = await client.PostAsync(
            new Uri($"/admin/v1/devices/{Guid.CreateVersion7()}/actions/shutdown", UriKind.Relative), content: null);
        shutdownResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
