using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.BitLocker;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// IT remote support (RustDesk) over real HTTP against real PostgreSQL and Redis.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are the feature. Connecting reveals a device's unattended password
/// and hands over live control of its screen, so what matters is not that a session
/// starts but that everything which should stop one does: the dedicated permission,
/// the device scope, the agent-version floor, the requirement that the device be
/// enrolled and have reported a RustDesk ID, the step-up password and the rate
/// limit -- and the absence of the password from every other response and from the
/// audit trail.
/// </para>
/// <para>
/// The leakage tests assert over whole serialised payloads and whole audit rows
/// rather than named fields, so a field added later that carried the unattended
/// password fails them without anyone having to remember these tests exist.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class RemoteSupportEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Devices = new("/admin/v1/remote-support/devices", UriKind.Relative);

    private static Uri Configure(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/remote-support/configure", UriKind.Relative);

    private static Uri Connect(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/remote-support/connect", UriKind.Relative);

    private static JsonContent ConnectBody(
        string password = AdminApiPostgresFixture.Password, string justification = "user cannot print") =>
        JsonContent.Create(new { currentPassword = password, justification });

    // ---- clients -----------------------------------------------------------

    /// <summary>
    /// A client for an existing seeded account, session minted directly rather than
    /// signed in -- the login endpoint's per-minute budget is shared by the whole
    /// assembly and a stray sign-in here fails LoginRateLimitTests.
    /// </summary>
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
                userAgent: "remote-support-tests"));

            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    /// <summary>
    /// A fresh Super Administrator, used by every test that reveals a password.
    /// </summary>
    /// <remarks>
    /// The reveal limiter is keyed by (actor, device) and a successful reveal does
    /// not reset it, so tests sharing one account would exhaust each other's budget.
    /// A private account per test isolates them without weakening the control.
    /// </remarks>
    private async Task<HttpClient> NewAdminClientAsync()
    {
        var email = $"remote-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var user = new PlatformUser(org.Id, email, "Remote Admin");
            user.SetPasswordHash(
                PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        return await MintClientAsync(email);
    }

    // ---- seeding -----------------------------------------------------------

    /// <summary>A fresh active device. Defaults to an agent new enough for remote support.</summary>
    private async Task<Guid> SeedDeviceAsync(string agentVersion = "1.20.0")
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"rs-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "RS-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            agentVersion, null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        await db.SaveChangesAsync();
        return device.Id;
    }

    /// <summary>
    /// Stands in for the agent reporting its RustDesk ID on the heartbeat. Done with
    /// raw SQL because the field is set only through <c>Device.RecordHeartbeat</c> on
    /// the Agent API, which this Admin-API suite does not drive.
    /// </summary>
    private async Task SetRustDeskIdAsync(Guid deviceId, string rustDeskId)
    {
        await using var db = _fixture.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE endpoint_platform.devices SET remote_support_id = {rustDeskId} WHERE id = {deviceId}");
    }

    private async Task<Guid> EnrolAsync(HttpClient client, Guid deviceId)
    {
        var response = await client.PostAsync(Configure(deviceId), content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_can_do_nothing()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(Devices))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsync(Configure(deviceId), content: null))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsync(Connect(deviceId), ConnectBody()))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The central RBAC claim: remote support is administrator-only. First-line
    /// support cannot so much as open the device list, exactly as it cannot read a
    /// BitLocker recovery key.
    /// </summary>
    [Theory]
    [InlineData("helpdesk")]
    [InlineData("auditor")]
    public async Task A_non_administrator_cannot_list_configure_or_connect(string which)
    {
        var deviceId = await SeedDeviceAsync();

        var email = which == "helpdesk"
            ? AdminApiPostgresFixture.HelpdeskEmail
            : AdminApiPostgresFixture.AuditorEmail;

        using var client = await ClientAsync(email);

        (await client.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsync(Configure(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsync(Connect(deviceId), ConnectBody())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_outside_the_callers_scope_is_invisible()
    {
        var inScope = await SeedDeviceAsync();
        var outOfScope = await SeedDeviceAsync();

        var email = $"rs-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"RsScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            (await db.Devices.SingleAsync(d => d.Id == inScope)).MoveToGroup(group.Id);

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));

        // The listing shows only the in-scope device.
        var listed = await (await client.GetAsync(Devices)).Content.ReadFromJsonAsync<JsonElement>();
        var ids = listed.EnumerateArray().Select(d => d.GetProperty("deviceId").GetGuid()).ToList();
        ids.ShouldContain(inScope);
        ids.ShouldNotContain(outOfScope);

        // And acting on the out-of-scope device answers 404, not 403: an admin must
        // not be able to map the estate by probing.
        (await client.PostAsync(Configure(outOfScope), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(Connect(outOfScope), ConnectBody())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- configure (enrol) -------------------------------------------------

    [Fact]
    public async Task Configuring_queues_a_task_and_seals_a_password()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var taskId = await EnrolAsync(client, deviceId);

        await using var db = _fixture.CreateDbContext();

        var task = await db.DeviceTasks.SingleAsync(t => t.Id == taskId);
        task.DeviceId.ShouldBe(deviceId);
        task.Type.ShouldBe(DeviceTaskType.ConfigureRemoteSupport);

        var enrollment = await db.RemoteSupportEnrollments.SingleAsync(e => e.DeviceId == deviceId);
        enrollment.RevealedCount.ShouldBe(0);
        enrollment.SealedPassword.ShouldNotBeNullOrWhiteSpace();
        // The task payload carries a one-time secret reference, never the password,
        // and the sealed value is an envelope, never a plaintext password.
        AuditStateRedactor.ContainsSecretShape(enrollment.SealedPassword).ShouldBeFalse();
    }

    /// <summary>
    /// Re-enrolment supersedes the sealed password in place: one row per device, a
    /// fresh secret each time.
    /// </summary>
    [Fact]
    public async Task Reconfiguring_reseals_in_place_keeping_one_row_per_device()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        await EnrolAsync(client, deviceId);
        string firstSealed;
        await using (var db = _fixture.CreateDbContext())
        {
            firstSealed = (await db.RemoteSupportEnrollments.SingleAsync(e => e.DeviceId == deviceId)).SealedPassword;
        }

        await EnrolAsync(client, deviceId);

        await using (var db = _fixture.CreateDbContext())
        {
            var rows = await db.RemoteSupportEnrollments.Where(e => e.DeviceId == deviceId).ToListAsync();
            rows.Count.ShouldBe(1);
            rows[0].SealedPassword.ShouldNotBe(firstSealed);
        }
    }

    /// <summary>
    /// The agent-version floor is a precondition on the device, answered 409 rather
    /// than 400: the request is well formed, the device simply cannot carry the task.
    /// </summary>
    [Fact]
    public async Task Configuring_a_device_on_an_old_agent_is_refused()
    {
        var deviceId = await SeedDeviceAsync(agentVersion: "1.19.0");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(Configure(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.RemoteSupportEnrollments.AnyAsync(e => e.DeviceId == deviceId)).ShouldBeFalse();
    }

    // ---- connect (reveal) --------------------------------------------------

    [Theory]
    [InlineData("", "a justification")]
    [InlineData("password", "")]
    public async Task Connecting_requires_both_a_password_and_a_justification(string password, string justification)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        await EnrolAsync(client, deviceId);
        await SetRustDeskIdAsync(deviceId, "123456789");

        (await client.PostAsync(Connect(deviceId), ConnectBody(password, justification)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Connecting_to_an_unenrolled_device_is_not_found()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(Connect(deviceId), ConnectBody())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Enrolled but no RustDesk ID reported yet: there is nothing to dial, so the
    /// reveal is refused and no password is handed out.
    /// </summary>
    [Fact]
    public async Task Connecting_before_the_device_reports_its_rustdesk_id_is_refused()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await NewAdminClientAsync();
        await EnrolAsync(client, deviceId);

        var response = await client.PostAsync(Connect(deviceId), ConnectBody());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.RemoteSupportEnrollments.SingleAsync(e => e.DeviceId == deviceId)).RevealedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Connecting_requires_the_callers_own_password()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await NewAdminClientAsync();
        await EnrolAsync(client, deviceId);
        await SetRustDeskIdAsync(deviceId, "123456789");

        var wrong = await client.PostAsync(Connect(deviceId),
            ConnectBody(password: "definitely-not-the-password", justification: "helping a user"));

        wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using var db = _fixture.CreateDbContext();
        (await db.RemoteSupportEnrollments.SingleAsync(e => e.DeviceId == deviceId)).RevealedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_correct_step_up_returns_the_id_and_password_and_records_the_reveal()
    {
        const string RustDeskId = "987654321";

        var deviceId = await SeedDeviceAsync();
        using var client = await NewAdminClientAsync();
        await EnrolAsync(client, deviceId);
        await SetRustDeskIdAsync(deviceId, RustDeskId);

        var response = await client.PostAsync(Connect(deviceId), ConnectBody(justification: "fixing the printer"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rustDeskId").GetString().ShouldBe(RustDeskId);
        body.GetProperty("password").GetString().ShouldNotBeNullOrWhiteSpace();

        // The one response carrying the password must not be cached anywhere.
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        await using var db = _fixture.CreateDbContext();
        var enrollment = await db.RemoteSupportEnrollments.SingleAsync(e => e.DeviceId == deviceId);
        enrollment.RevealedCount.ShouldBe(1);
        enrollment.LastRevealedAt.ShouldNotBeNull();

        // The revealed password is the sealed one unsealed, never the stored envelope.
        enrollment.SealedPassword.ShouldNotContain(body.GetProperty("password").GetString()!);
    }

    // ---- rate limiting -----------------------------------------------------

    /// <summary>
    /// Five reveals per user per window, and a success does not reset it -- it is the
    /// successful reveals that need bounding, since a failed one yields nothing.
    /// </summary>
    [Fact]
    public async Task A_user_is_limited_to_five_reveals_per_window()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await NewAdminClientAsync();
        await EnrolAsync(client, deviceId);
        await SetRustDeskIdAsync(deviceId, "123123123");

        for (var i = 1; i <= RevealRateLimiter.MaxAttemptsPerWindow; i++)
        {
            (await client.PostAsync(Connect(deviceId), ConnectBody(justification: $"attempt {i}")))
                .StatusCode.ShouldBe(HttpStatusCode.OK, $"reveal {i} should be within the limit");
        }

        var blocked = await client.PostAsync(Connect(deviceId), ConnectBody(justification: "attempt 6"));

        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        blocked.Headers.Contains("Retry-After").ShouldBeTrue();
    }

    // ---- leakage -----------------------------------------------------------

    /// <summary>
    /// The listing exists to say a device is enrolled and give its RustDesk ID, not
    /// to carry a secret.
    /// </summary>
    [Fact]
    public async Task The_listing_returns_no_password_and_no_ciphertext()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        await EnrolAsync(client, deviceId);

        string sealedPassword;
        await using (var db = _fixture.CreateDbContext())
        {
            sealedPassword = (await db.RemoteSupportEnrollments.SingleAsync(e => e.DeviceId == deviceId))
                .SealedPassword;
        }

        var listBody = await (await client.GetAsync(Devices)).Content.ReadAsStringAsync();

        listBody.ShouldNotContain(sealedPassword);
        listBody.ShouldNotContain("sealed");
        listBody.ShouldNotContain("password");
        AuditStateRedactor.ContainsSecretShape(listBody).ShouldBeFalse();
    }

    /// <summary>
    /// The audit trail is append-only and enforced by database triggers, so a secret
    /// written into it could not be taken back.
    /// </summary>
    [Fact]
    public async Task No_audit_row_contains_the_password_or_the_ciphertext()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await NewAdminClientAsync();
        await EnrolAsync(client, deviceId);
        await SetRustDeskIdAsync(deviceId, "555666777");

        var revealed = await (await client.PostAsync(Connect(deviceId), ConnectBody(justification: "audit check")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var password = revealed.GetProperty("password").GetString()!;

        // A refusal too, so denials are covered by the same sweep.
        await client.PostAsync(Connect(deviceId), ConnectBody(password: "wrong", justification: "should be denied"));

        await using var db = _fixture.CreateDbContext();
        var sealedPassword = (await db.RemoteSupportEnrollments.SingleAsync(e => e.DeviceId == deviceId))
            .SealedPassword;

        var rows = await db.AuditLogEntries
            .Where(a => a.Action.StartsWith("remotesupport."))
            .ToListAsync();

        rows.ShouldNotBeEmpty();

        foreach (var row in rows)
        {
            var payload = (row.PreviousState ?? "") + (row.NewState ?? "") + (row.FailureReason ?? "");

            payload.ShouldNotContain(password);
            payload.ShouldNotContain(sealedPassword);
            payload.ShouldNotContain(AdminApiPostgresFixture.Password);
            AuditStateRedactor.ContainsSecretShape(payload).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Every_operation_is_audited_including_the_refusals()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await NewAdminClientAsync();
        await EnrolAsync(client, deviceId);
        await SetRustDeskIdAsync(deviceId, "111222333");

        await client.PostAsync(Connect(deviceId), ConnectBody(justification: "ok"));
        await client.PostAsync(Connect(deviceId), ConnectBody(password: "wrong", justification: "denied"));

        await using var db = _fixture.CreateDbContext();
        var actions = await db.AuditLogEntries
            .Where(a => a.Action.StartsWith("remotesupport."))
            .Select(a => a.Action)
            .ToListAsync();

        actions.ShouldContain("remotesupport.configured");
        actions.ShouldContain("remotesupport.revealed");
        actions.ShouldContain("remotesupport.reveal_denied");
    }
}
