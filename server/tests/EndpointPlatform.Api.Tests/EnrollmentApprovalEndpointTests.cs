using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Approving and rejecting machines that have asked to be managed.
/// </summary>
/// <remarks>
/// This is the authorization boundary of the enrollment flow: the agent-facing side
/// is anonymous by necessity, so everything that turns a request into a device has to
/// be proven here. These tests seed pending requests through the real
/// <see cref="PendingEnrollmentStore"/> against the fixture's real Redis, so the
/// atomicity and expiry behaviour under test is the behaviour that ships.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class EnrollmentApprovalEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri PendingUri = new("/admin/v1/enrollments/pending", UriKind.Relative);

    private static Uri ApproveUri(string requestId) =>
        new($"/admin/v1/enrollments/{requestId}/approve", UriKind.Relative);

    private static Uri RejectUri(string requestId) =>
        new($"/admin/v1/enrollments/{requestId}/reject", UriKind.Relative);

    /// <summary>Mirrors what the agent does: keep a secret, publish only its digest.</summary>
    private static (string Secret, string RequestId) NewProof()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var requestId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        return (secret, requestId);
    }

    /// <summary>Seeds a pending request through the real store.</summary>
    private async Task<(string Secret, string RequestId)> SeedPendingAsync(string hostname = "TEST-PC")
    {
        var (secret, requestId) = NewProof();

        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();

        var stored = await store.RequestAsync(
            requestId,
            new PendingEnrollment(
                MachineIdentifier: Guid.CreateVersion7().ToString(),
                Hostname: hostname,
                OperatingSystem: "Windows 11 Pro",
                AgentVersion: "1.0.0",
                RequestedAt: DateTimeOffset.UtcNow,
                Status: PendingEnrollmentStatus.Pending),
            CancellationToken.None);

        stored.ShouldBeTrue("the pending store must be reachable for these tests to mean anything");
        return (secret, requestId);
    }

    private async Task<PendingEnrollment?> ReadAsync(string requestId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();
        return await store.FindAsync(requestId, CancellationToken.None);
    }

    // ------------------------------------------------------ authentication

    [Fact]
    public async Task An_anonymous_caller_cannot_list_pending_enrollments()
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(PendingUri)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_approve_or_reject()
    {
        // The whole point of approval gating: the anonymous side may ask, never decide.
        var (_, requestId) = await SeedPendingAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await ReadAsync(requestId))!.Status.ShouldBe(PendingEnrollmentStatus.Pending);
    }

    [Fact]
    public async Task An_auditor_cannot_approve_an_enrollment()
    {
        // Approving creates a managed device; read-only roles must not be able to.
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadAsync(requestId))!.Status.ShouldBe(PendingEnrollmentStatus.Pending);
    }

    [Fact]
    public async Task An_auditor_cannot_list_pending_enrollments()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(PendingUri)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------------- listing

    [Fact]
    public async Task An_administrator_sees_pending_machines_without_any_secret()
    {
        var (secret, requestId) = await SeedPendingAsync("LISTED-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(PendingUri);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("LISTED-PC");
        body.ShouldContain(requestId);

        // The proof secret must never be derivable from what an administrator sees.
        body.ShouldNotContain(secret);
        body.ShouldNotContain("sealedTokenSecret", Case.Insensitive);
        body.ShouldNotContain("requestSecret", Case.Insensitive);

        using var doc = JsonDocument.Parse(body);
        var entry = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("requestId").GetString() == requestId);
        entry.GetProperty("agentVersion").GetString().ShouldBe("1.0.0");
        entry.GetProperty("status").GetString().ShouldBe("Pending");
        entry.TryGetProperty("expiresAt", out _).ShouldBeTrue("an administrator needs to see the deadline");
    }

    // ------------------------------------------------------------ approval

    [Fact]
    public async Task Approving_marks_the_request_and_names_the_approver()
    {
        var (_, requestId) = await SeedPendingAsync("APPROVE-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(ApproveUri(requestId), null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = (await ReadAsync(requestId)).ShouldNotBeNull();
        stored.Status.ShouldBe(PendingEnrollmentStatus.Approved);
        stored.ApprovedBy.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);

        // The organization comes from the approver, never from the agent.
        stored.OrganizationId.ShouldNotBeNull();

        // The token that completes enrollment is sealed and stays server-side.
        stored.SealedTokenSecret.ShouldNotBeNull();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(stored.SealedTokenSecret!);
    }

    [Fact]
    public async Task Approving_creates_the_single_use_enrollment_token_the_claim_will_consume()
    {
        // Approval must feed the EXISTING enrollment path rather than invent a second
        // credential mechanism, so a real single-use token has to appear.
        var (_, requestId) = await SeedPendingAsync("TOKEN-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var minted = db.EnrollmentTokens
            .Where(t => t.Name == "approved-enrollment-TOKEN-PC")
            .OrderByDescending(t => t.CreatedAt)
            .First();

        minted.MaxUses.ShouldBe(1, "an approval must not be reusable");
        minted.UseCount.ShouldBe(0);
        minted.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_request_cannot_be_approved_twice()
    {
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Double approval would mint a second credential for one decision.
        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Concurrent_approvals_cannot_both_succeed()
    {
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var a = _fixture.CreateClientFor(token);
        using var b = _fixture.CreateClientFor(token);

        var results = await Task.WhenAll(
            a.PostAsync(ApproveUri(requestId), null),
            b.PostAsync(ApproveUri(requestId), null));

        results.Count(r => r.StatusCode == HttpStatusCode.OK)
            .ShouldBe(1, "the conditional transition must let exactly one approval win");
    }

    [Fact]
    public async Task An_unknown_or_expired_request_cannot_be_approved()
    {
        // Redis expiry makes "expired" and "never existed" the same observable state,
        // which is also what stops request ids being probed.
        var (_, unknownId) = NewProof();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(unknownId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ----------------------------------------------------------- rejection

    [Fact]
    public async Task Rejecting_is_terminal_and_issues_nothing()
    {
        var (_, requestId) = await SeedPendingAsync("REJECT-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = (await ReadAsync(requestId)).ShouldNotBeNull();
        stored.Status.ShouldBe(PendingEnrollmentStatus.Rejected);
        stored.SealedTokenSecret.ShouldBeNull("a rejected machine must never get a token");

        // And it cannot be revived.
        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_approved_request_cannot_then_be_rejected()
    {
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await ReadAsync(requestId))!.Status.ShouldBe(PendingEnrollmentStatus.Approved);
    }

    // ---------------------------------------------------- re-enrollment rows
    //
    // A re-enrollment is the same decision surface with a different stake: the
    // requester holds a valid token and asks to BECOME an existing active device,
    // which revokes that device's working credential. The rows are tenant-scoped,
    // carry the matched device and its liveness, and approving one additionally
    // demands device scope — plain device.enroll admits unknown machines; it must
    // not also hand over devices its holder cannot act on.

    /// <summary>Seeds an active device with a used credential, and its parked re-enrollment.</summary>
    private async Task<(string RequestId, Guid DeviceId, Guid OrganizationId)> SeedReEnrollmentAsync(
        string hostname, DateTimeOffset? credentialLastUsedAt = null)
    {
        Guid organizationId;
        Device device;

        await using (var db = _fixture.CreateDbContext())
        {
            organizationId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();

            var token = new EnrollmentToken(
                organizationId, $"reenroll-{Guid.CreateVersion7():N}",
                SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
                Guid.CreateVersion7(), "reenroll-tests", DateTimeOffset.UtcNow.AddHours(1), 5);
            db.EnrollmentTokens.Add(token);

            device = Device.Enroll(
                organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", "1.16.2", "Windows 11 Pro",
                token.Id, DateTimeOffset.UtcNow);
            db.Devices.Add(device);

            var credential = new AgentCredential(
                device.Id, SecretGenerator.GenerateKeyId(),
                SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()), DateTimeOffset.UtcNow);
            if (credentialLastUsedAt is { } used)
            {
                credential.RecordUse(used);
            }

            db.AgentCredentials.Add(credential);
            await db.SaveChangesAsync();
        }

        var requestId = PendingEnrollmentStore.DeriveReEnrollRequestId(organizationId, device.MachineIdentifier);

        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();

        var outcome = await store.UpsertReEnrollRequestAsync(
            requestId,
            new PendingEnrollment(
                device.MachineIdentifier,
                hostname + "-REQUESTER",
                "Windows 11 Pro",
                "1.24.0",
                DateTimeOffset.UtcNow,
                PendingEnrollmentStatus.Pending,
                OrganizationId: organizationId,
                Kind: PendingEnrollmentKind.ReEnrollment,
                ExistingDeviceId: device.Id,
                ExistingDeviceHostname: hostname,
                TokenId: Guid.CreateVersion7()),
            CancellationToken.None);

        outcome.State.ShouldBe(ReEnrollAttemptState.PendingCreated);
        return (requestId, device.Id, organizationId);
    }

    /// <summary>A parked re-enrollment whose record claims another organization.</summary>
    private async Task<string> SeedForeignReEnrollmentAsync(string hostname)
    {
        var foreignOrganizationId = Guid.CreateVersion7();
        var machineIdentifier = $"smbios-{Guid.CreateVersion7()}";
        var requestId = PendingEnrollmentStore.DeriveReEnrollRequestId(foreignOrganizationId, machineIdentifier);

        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();

        (await store.UpsertReEnrollRequestAsync(
                requestId,
                new PendingEnrollment(
                    machineIdentifier, hostname, "Windows 11 Pro", "1.24.0",
                    DateTimeOffset.UtcNow, PendingEnrollmentStatus.Pending,
                    OrganizationId: foreignOrganizationId,
                    Kind: PendingEnrollmentKind.ReEnrollment,
                    ExistingDeviceId: Guid.CreateVersion7(),
                    ExistingDeviceHostname: hostname,
                    TokenId: Guid.CreateVersion7()),
                CancellationToken.None))
            .State.ShouldBe(ReEnrollAttemptState.PendingCreated);

        return requestId;
    }

    /// <summary>An administrator holding device.enroll but NO device scope.</summary>
    private async Task<HttpClient> UnscopedAdminAsync()
    {
        var email = $"unscoped-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == SystemRoles.SuperAdministrator);

            var user = new PlatformUser(org.Id, email, "Unscoped Approver");
            user.SetPasswordHash(PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            // Deliberately no GrantAllDeviceScope and no scope rows: deny-by-default.
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(await _fixture.SignInAsync(email));
    }

    [Fact]
    public async Task The_list_marks_a_re_enrollment_with_its_matched_device_and_liveness()
    {
        var lastUsed = DateTimeOffset.UtcNow;
        var (requestId, deviceId, _) = await SeedReEnrollmentAsync("LIST-REENROLL", lastUsed);

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(PendingUri);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("requestId").GetString() == requestId);

        row.GetProperty("kind").GetString().ShouldBe("re-enrollment");
        row.GetProperty("matchedDeviceId").GetGuid().ShouldBe(deviceId);
        row.GetProperty("matchedDeviceHostname").GetString().ShouldBe("LIST-REENROLL");
        row.GetProperty("hostname").GetString().ShouldBe("LIST-REENROLL-REQUESTER");

        // The device's credential authenticated seconds ago: visibly ONLINE, which
        // is the signal that separates a takeover attempt from a reinstall.
        row.GetProperty("credentialLastUsedAt").GetDateTimeOffset()
            .ShouldBe(lastUsed, tolerance: TimeSpan.FromSeconds(1));
        row.GetProperty("online").GetBoolean().ShouldBeTrue();

        // A re-enrollment waits for a human much longer than a 15-minute request.
        row.GetProperty("expiresAt").GetDateTimeOffset()
            .ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(12));

        // Plain rows say so too, and carry no device fields.
        var (_, plainId) = await SeedPendingAsync("LIST-PLAIN");
        using var second = JsonDocument.Parse(await (await client.GetAsync(PendingUri)).Content.ReadAsStringAsync());
        var plain = second.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("requestId").GetString() == plainId);
        plain.GetProperty("kind").GetString().ShouldBe("enrollment");
        plain.GetProperty("matchedDeviceId").ValueKind.ShouldBe(JsonValueKind.Null);
        plain.GetProperty("online").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_device_with_no_recent_credential_use_reads_as_offline()
    {
        var (requestId, _, _) = await SeedReEnrollmentAsync(
            "LIST-OFFLINE", DateTimeOffset.UtcNow.AddHours(-2));

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        using var doc = JsonDocument.Parse(await (await client.GetAsync(PendingUri)).Content.ReadAsStringAsync());
        var row = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("requestId").GetString() == requestId);

        row.GetProperty("online").GetBoolean().ShouldBeFalse(
            "two hours without an authenticated call is far past the offline window");
    }

    [Fact]
    public async Task A_re_enrollment_from_another_organization_is_not_listed()
    {
        var requestId = await SeedForeignReEnrollmentAsync("FOREIGN-REENROLL");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var body = await (await client.GetAsync(PendingUri)).Content.ReadAsStringAsync();
        body.ShouldNotContain(requestId, customMessage:
            "a re-enrollment names a device in its own tenant and must not cross tenants");
    }

    [Fact]
    public async Task A_re_enrollment_from_another_organization_cannot_be_approved_or_rejected()
    {
        var requestId = await SeedForeignReEnrollmentAsync("FOREIGN-DECIDE");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        // Indistinguishable from "expired or never existed" — a request id must not
        // be probeable across tenants.
        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await ReadAsync(requestId))!.Status.ShouldBe(PendingEnrollmentStatus.Pending);
    }

    [Fact]
    public async Task Approving_a_re_enrollment_requires_scope_over_the_matched_device()
    {
        var (requestId, _, _) = await SeedReEnrollmentAsync("SCOPE-REENROLL");

        using var unscoped = await UnscopedAdminAsync();

        // device.enroll alone admits unknown machines; handing over an EXISTING
        // device additionally needs authority over that device. Out of scope is
        // 404 like everywhere else -- the list does not show this row to them.
        var refused = await unscoped.PostAsync(ApproveUri(requestId), null);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync()).ShouldNotContain(
            "SCOPE-REENROLL", customMessage: "the refusal must not name the device it protects");

        (await ReadAsync(requestId))!.Status.ShouldBe(
            PendingEnrollmentStatus.Pending, "a scope refusal must not have decided anything");

        // The same administrator can still approve a PLAIN enrollment: admitting an
        // unknown machine was never scope-gated, and must not have become so.
        var (_, plainId) = await SeedPendingAsync("SCOPE-PLAIN");
        (await unscoped.PostAsync(ApproveUri(plainId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_group_scoped_administrator_sees_and_decides_only_re_enrollments_of_their_own_devices()
    {
        var outHost = $"R8OUT-{Guid.NewGuid():N}"[..14];
        var inHost = $"R8IN-{Guid.NewGuid():N}"[..13];
        var (outRequest, outDevice, _) = await SeedReEnrollmentAsync(outHost);
        var (inRequest, inDevice, _) = await SeedReEnrollmentAsync(inHost);
        var (_, plainRequest) = await SeedPendingAsync($"R8PLAIN-{Guid.NewGuid():N}"[..15]);

        var groups = new GroupTestSupport(_fixture);
        using var itAdmin = await groups.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);
        var groupId = await groups.CreateGroupAsync(itAdmin, GroupTestSupport.UniqueName("R8"), inDevice);
        using var scoped = await groups.ScopedAdminAsync(groupId);

        // The list: the out-of-scope re-enrollment is absent -- its request id, the
        // matched device's id and its hostname all -- while the in-scope one and the
        // plain request (no device, no organization yet) remain.
        var scopedList = await (await scoped.GetAsync(PendingUri)).Content.ReadAsStringAsync();
        scopedList.ShouldNotContain(outRequest);
        scopedList.ShouldNotContain(outDevice.ToString());
        scopedList.ShouldNotContain(outHost);
        scopedList.ShouldContain(inRequest);
        scopedList.ShouldContain(plainRequest, customMessage: "plain enrollment rows are unchanged");

        var allList = await (await itAdmin.GetAsync(PendingUri)).Content.ReadAsStringAsync();
        allList.ShouldContain(outRequest, customMessage: "an all-device administrator is unchanged");
        allList.ShouldContain(inRequest);

        // Deciding the hidden row is 404 both ways, and decides nothing.
        (await scoped.PostAsync(ApproveUri(outRequest), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.PostAsync(RejectUri(outRequest), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadAsync(outRequest))!.Status.ShouldBe(PendingEnrollmentStatus.Pending);

        // Their own device's re-enrollment they still decide.
        (await scoped.PostAsync(RejectUri(inRequest), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(inRequest))!.Status.ShouldBe(PendingEnrollmentStatus.Rejected);

        // And the all-device administrator decides the other one exactly as before.
        (await itAdmin.PostAsync(ApproveUri(outRequest), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_scoped_administrator_approves_a_re_enrollment_without_minting_a_token()
    {
        var (requestId, deviceId, organizationId) = await SeedReEnrollmentAsync("APPROVE-REENROLL");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(ApproveUri(requestId), null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("kind").GetString().ShouldBe("re-enrollment");
        // The success copy must set the expectation that nothing is pushed: the
        // agent's own retry completes the hand-over.
        body.GetProperty("message").GetString()!.ShouldContain("5 minutes");

        var stored = (await ReadAsync(requestId)).ShouldNotBeNull();
        stored.Status.ShouldBe(PendingEnrollmentStatus.Approved);
        stored.ApprovedBy.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
        stored.OrganizationId.ShouldBe(organizationId, "approval must not move the record between tenants");
        stored.SealedTokenSecret.ShouldBeNull(
            "the re-enrolling agent already holds its token; approval must not mint another");

        // No minted approved-enrollment token either — the plain-approval mechanism
        // must not fire for re-enrollments.
        await using var db = _fixture.CreateDbContext();
        (await db.EnrollmentTokens.AnyAsync(t => t.Name == "approved-enrollment-APPROVE-REENROLL-REQUESTER"))
            .ShouldBeFalse();

        // Audited with the device it hands over.
        var audit = db.AuditLogEntries
            .Where(e => e.Action == "enrollment.approved" && e.TargetId == requestId)
            .OrderByDescending(e => e.OccurredAt)
            .First();
        audit.DeviceId.ShouldBe(deviceId);
        (audit.NewState ?? "").ShouldContain("re-enrollment");
    }

    [Fact]
    public async Task Rejecting_a_re_enrollment_keeps_the_cooling_off_record()
    {
        var (requestId, deviceId, _) = await SeedReEnrollmentAsync("REJECT-REENROLL");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(RejectUri(requestId), null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("kind").GetString().ShouldBe("re-enrollment");

        // The record stays, Rejected, so the still-knocking agent is refused
        // without re-pending; and the decision is terminal as ever.
        var stored = (await ReadAsync(requestId)).ShouldNotBeNull();
        stored.Status.ShouldBe(PendingEnrollmentStatus.Rejected);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        db.AuditLogEntries
            .Any(e => e.Action == "enrollment.rejected" && e.TargetId == requestId && e.DeviceId == deviceId)
            .ShouldBeTrue();
    }

    // --------------------------------------------------------------- audit

    [Fact]
    public async Task Approval_and_rejection_are_audited_without_leaking_material()
    {
        var (secret, approvedId) = await SeedPendingAsync("AUDIT-OK");
        var (_, rejectedId) = await SeedPendingAsync("AUDIT-NO");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(approvedId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(RejectUri(rejectedId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();

        var approved = db.AuditLogEntries
            .Where(e => e.Action == "enrollment.approved" && e.TargetId == approvedId)
            .OrderByDescending(e => e.OccurredAt)
            .First();
        approved.ActorDisplay.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
        approved.TargetDisplay.ShouldBe("AUDIT-OK");
        (approved.NewState ?? "").ShouldNotContain(secret);

        db.AuditLogEntries
            .Any(e => e.Action == "enrollment.rejected" && e.TargetId == rejectedId)
            .ShouldBeTrue();
    }
}
