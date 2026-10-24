using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The Phase 1 enrollment security test matrix, end to end over HTTP against a
/// real database: valid, invalid, expired, revoked, reuse, max-use, duplicate
/// identity, and information-leak checks.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class EnrollmentEndpointTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    // ---------------------------------------------------------------- helpers

    private async Task<(Guid TokenId, string Secret)> IssueTokenAsync(
        int maxUses = 5,
        TimeSpan? lifetime = null,
        bool revoked = false)
    {
        await using var dbContext = _fixture.CreateDbContext();

        var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();

        var token = new EnrollmentToken(
            organization.Id,
            $"test-token-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(),
            "test-admin@company.local",
            DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(1)),
            maxUses);

        if (revoked)
        {
            token.Revoke(DateTimeOffset.UtcNow);
        }

        dbContext.EnrollmentTokens.Add(token);
        await dbContext.SaveChangesAsync();

        return (token.Id, secret);
    }

    private HttpClient CreateClient() => _fixture.Factory.CreateClient();

    private static EnrollRequest MakeRequest(string tokenSecret, string? machineIdentifier = null) =>
        new(
            tokenSecret,
            "TEST-PC-01",
            machineIdentifier ?? $"machine-{Guid.CreateVersion7():N}",
            "1.0.0",
            "Windows 11 Test");

    private static Task<HttpResponseMessage> PostEnrollAsync(HttpClient client, EnrollRequest request)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll, UriKind.Relative))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        return client.SendAsync(message);
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Valid_enrollment_succeeds_and_returns_a_credential()
    {
        var (_, secret) = await IssueTokenAsync();
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EnrollResponse>();
        body.ShouldNotBeNull();
        body.DeviceId.ShouldNotBe(Guid.Empty);
        body.CredentialKeyId.Length.ShouldBe(32);
        body.CredentialSecret.Length.ShouldBe(64);
        body.ReEnrolled.ShouldBeFalse();

        // The device record exists, and the credential is stored hashed - the
        // returned secret must appear nowhere in the database.
        await using var dbContext = _fixture.CreateDbContext();
        var device = await dbContext.Devices.SingleAsync(d => d.Id == body.DeviceId);
        device.Hostname.ShouldBe("TEST-PC-01");

        var credential = await dbContext.AgentCredentials.SingleAsync(c => c.KeyId == body.CredentialKeyId);
        credential.SecretHash.ShouldNotBe(body.CredentialSecret);
        credential.SecretHash.ShouldBe(SecretGenerator.HashSecret(body.CredentialSecret));
    }

    [Fact]
    public async Task An_invalid_token_is_refused()
    {
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(SecretGenerator.GenerateSecret()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (_, secret) = await IssueTokenAsync(lifetime: TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        var (_, secret) = await IssueTokenAsync(revoked: true);
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refusals_are_indistinguishable_between_unknown_expired_and_revoked()
    {
        // If the response differed per reason, a caller could probe the token
        // space and learn which guesses were once-valid tokens.
        var (_, expiredSecret) = await IssueTokenAsync(lifetime: TimeSpan.FromMilliseconds(1));
        var (_, revokedSecret) = await IssueTokenAsync(revoked: true);
        await Task.Delay(50);
        using var client = CreateClient();

        var unknown = await PostEnrollAsync(client, MakeRequest(SecretGenerator.GenerateSecret()));
        var expired = await PostEnrollAsync(client, MakeRequest(expiredSecret));
        var revoked = await PostEnrollAsync(client, MakeRequest(revokedSecret));

        unknown.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        expired.StatusCode.ShouldBe(unknown.StatusCode);
        revoked.StatusCode.ShouldBe(unknown.StatusCode);

        // Bodies must be identical except for the per-request correlation id.
        var unknownBody = Normalize(await unknown.Content.ReadAsStringAsync());
        var expiredBody = Normalize(await expired.Content.ReadAsStringAsync());
        var revokedBody = Normalize(await revoked.Content.ReadAsStringAsync());

        expiredBody.ShouldBe(unknownBody, "refusal bodies must not reveal the reason");
        revokedBody.ShouldBe(unknownBody, "refusal bodies must not reveal the reason");

        static string Normalize(string problemJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(problemJson);
            var stripped = document.RootElement.EnumerateObject()
                .Where(p => p.Name != "correlationId")
                .ToDictionary(p => p.Name, p => p.Value.ToString());
            return System.Text.Json.JsonSerializer.Serialize(stripped);
        }
    }

    [Fact]
    public async Task Maximum_use_enforcement_refuses_the_extra_enrollment()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 2);
        using var client = CreateClient();

        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_single_use_token_cannot_be_reused()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 1);
        using var client = CreateClient();

        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------- the re-enrollment approval gate
    //
    // A valid token plus a machine identifier — which is printed on the asset, not
    // secret — used to re-enroll an ACTIVE device immediately: revoke the real
    // machine's credential and hand its identity (queued tasks, device-bound
    // secrets) to the caller. These tests pin the gate that closed that hole:
    // every token-path re-enrollment of an active device parks a pending request
    // and waits for an administrator; the agent's own 5-minute retry completes it
    // after approval. The tests drive approval through the store directly — the
    // admin HTTP endpoints belong to the Admin API suite.

    /// <summary>Where the gate parks this machine's request.</summary>
    private async Task<string> ReEnrollRequestIdAsync(string machineIdentifier)
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organizationId = await dbContext.Organizations
            .OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        return PendingEnrollmentStore.DeriveReEnrollRequestId(organizationId, machineIdentifier);
    }

    private async Task<PendingEnrollment?> FindPendingAsync(string requestId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();
        return await store.FindAsync(requestId, CancellationToken.None);
    }

    private async Task DecideReEnrollAsync(string requestId, PendingEnrollmentStatus decision)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();

        await using var dbContext = _fixture.CreateDbContext();
        var organizationId = await dbContext.Organizations
            .OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();

        (await store.DecideAsync(requestId, decision, organizationId, tokenSecret: null, "approver@test.local"))
            .ShouldNotBeNull("the decision must land on a live pending record");
    }

    private async Task<int> TokenUseCountAsync(Guid tokenId)
    {
        await using var dbContext = _fixture.CreateDbContext();
        return await dbContext.EnrollmentTokens
            .Where(t => t.Id == tokenId).Select(t => t.UseCount).SingleAsync();
    }

    [Fact]
    public async Task Re_enrolling_an_active_machine_is_parked_for_approval_not_completed()
    {
        var (tokenId, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        // The same machine identifier, while the device is ACTIVE: refused, parked.
        var second = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        second.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Indistinguishable on the wire from a bad token, so machine identifiers
        // cannot be probed with a stolen-but-valid token.
        var unknown = await PostEnrollAsync(client, MakeRequest(SecretGenerator.GenerateSecret()));
        NormalizeProblem(await second.Content.ReadAsStringAsync())
            .ShouldBe(NormalizeProblem(await unknown.Content.ReadAsStringAsync()),
                "a gated refusal must not reveal that an active device matched");

        // Parked: a pending RE-ENROLLMENT record naming the matched device.
        var requestId = await ReEnrollRequestIdAsync(machineIdentifier);
        var pending = (await FindPendingAsync(requestId)).ShouldNotBeNull();
        pending.Kind.ShouldBe(PendingEnrollmentKind.ReEnrollment);
        pending.Status.ShouldBe(PendingEnrollmentStatus.Pending);
        pending.ExistingDeviceId.ShouldBe(firstBody.DeviceId);
        pending.TokenId.ShouldBe(tokenId);

        // Nothing was taken from anyone: the token kept its remaining uses and the
        // real device kept its working credential and its record.
        (await TokenUseCountAsync(tokenId)).ShouldBe(1, "a gated attempt must not consume a token use");

        await using var dbContext = _fixture.CreateDbContext();
        (await dbContext.AgentCredentials.SingleAsync(c => c.KeyId == firstBody.CredentialKeyId))
            .IsActive.ShouldBeTrue("the existing credential must survive a gated attempt");
        (await dbContext.Devices.CountAsync(d => d.MachineIdentifier == machineIdentifier))
            .ShouldBe(1);

        // And the security signal is on the record: one device.re_enroll denial.
        (await dbContext.AuditLogEntries.CountAsync(
                a => a.Action == "device.re_enroll"
                     && a.Result == Domain.Auditing.AuditResult.Denied
                     && a.DeviceId == firstBody.DeviceId))
            .ShouldBe(1, "parking a re-enrollment is a security event and must be audited");
    }

    [Fact]
    public async Task A_gated_retry_refreshes_the_request_without_a_second_audit_entry()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        // The released agent parks and retries every five minutes. Each retry must
        // be refused, keep the ONE pending record fresh, and not bury the audit
        // trail under one Denied row per knock.
        (await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostEnrollAsync(
                client, MakeRequest(secret, machineIdentifier) with { Hostname = "TEST-PC-01-RENAMED" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var requestId = await ReEnrollRequestIdAsync(machineIdentifier);
        var pending = (await FindPendingAsync(requestId)).ShouldNotBeNull();
        pending.Status.ShouldBe(PendingEnrollmentStatus.Pending);
        pending.Hostname.ShouldBe("TEST-PC-01-RENAMED", "a retry must refresh the snapshot the admin decides on");

        await using var dbContext = _fixture.CreateDbContext();
        (await dbContext.AuditLogEntries.CountAsync(
                a => a.Action == "device.re_enroll"
                     && a.Result == Domain.Auditing.AuditResult.Denied
                     && a.DeviceId == firstBody.DeviceId))
            .ShouldBe(1, "only the FIRST knock is audited; retries refresh silently");
    }

    [Fact]
    public async Task An_approved_re_enrollment_completes_on_the_agents_next_retry()
    {
        var (tokenId, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        (await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var requestId = await ReEnrollRequestIdAsync(machineIdentifier);
        await DecideReEnrollAsync(requestId, PendingEnrollmentStatus.Approved);

        // The agent's own next retry — same token, same request — now completes.
        var retry = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var retryBody = (await retry.Content.ReadFromJsonAsync<EnrollResponse>())!;

        retryBody.DeviceId.ShouldBe(firstBody.DeviceId, "same machine must map to the same device");
        retryBody.ReEnrolled.ShouldBeTrue();
        retryBody.CredentialKeyId.ShouldNotBe(firstBody.CredentialKeyId, "re-enrollment must rotate the credential");

        // The approval is single-use and spent.
        (await FindPendingAsync(requestId)).ShouldBeNull("a completed approval must not remain claimable");

        // Only the completing attempt consumed a token use: 1 (first enroll) + 1.
        (await TokenUseCountAsync(tokenId)).ShouldBe(2);

        await using var dbContext = _fixture.CreateDbContext();

        (await dbContext.Devices.CountAsync(d => d.MachineIdentifier == machineIdentifier))
            .ShouldBe(1, "no duplicate device row");

        (await dbContext.AgentCredentials.SingleAsync(c => c.KeyId == firstBody.CredentialKeyId))
            .IsActive.ShouldBeFalse("the old credential must be revoked on re-enrollment");

        // The audit names who allowed the handover, on the entry itself.
        var audit = await dbContext.AuditLogEntries
            .Where(a => a.Action == "device.re_enroll"
                        && a.Result == Domain.Auditing.AuditResult.Success
                        && a.DeviceId == firstBody.DeviceId)
            .SingleAsync();
        (audit.NewState ?? "").ShouldContain("approver@test.local");
    }

    [Fact]
    public async Task A_rejected_re_enrollment_stays_refused_through_its_cooling_off()
    {
        var (tokenId, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        (await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var requestId = await ReEnrollRequestIdAsync(machineIdentifier);
        await DecideReEnrollAsync(requestId, PendingEnrollmentStatus.Rejected);

        // Still knocking, still refused — and the knock must NOT re-pend the
        // request, or an administrator's "no" would turn back into a question
        // five minutes after it was given.
        (await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var record = (await FindPendingAsync(requestId)).ShouldNotBeNull();
        record.Status.ShouldBe(PendingEnrollmentStatus.Rejected, "the rejection must survive further retries");

        (await TokenUseCountAsync(tokenId)).ShouldBe(1, "refused retries must not consume token uses");

        await using var dbContext = _fixture.CreateDbContext();
        (await dbContext.AgentCredentials.SingleAsync(c => c.KeyId == firstBody.CredentialKeyId))
            .IsActive.ShouldBeTrue("a rejected re-enrollment must leave the device's credential alone");
    }

    [Fact]
    public async Task A_new_machine_still_enrolls_immediately_with_no_pending_record()
    {
        var (_, secret) = await IssueTokenAsync();
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        // No active device matches, so the gate does not apply: a first enrollment
        // with a valid token is exactly as immediate as it always was.
        (await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FindPendingAsync(await ReEnrollRequestIdAsync(machineIdentifier)))
            .ShouldBeNull("a new-device enrollment must not create re-enrollment state");
    }

    /// <summary>
    /// The request/claim path still re-enrolls an active device in one step after
    /// its OWN approval. The approval already happened (that is what minted the
    /// token the claim consumes), so sending the machine through the token gate as
    /// well would lose the approval — its Redis record is redeemed before the
    /// enrollment runs — and strand the agent.
    /// </summary>
    [Fact]
    public async Task The_claim_path_re_enrolls_an_active_device_without_a_second_approval()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        // The machine asks via request/claim (as a credential-less reinstall does).
        var requestSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var requestId = PendingEnrollmentStore.DeriveRequestId(requestSecret);

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.EnrollRequest, UriKind.Relative))
        {
            Content = JsonContent.Create(new EnrollmentRequestRequest(
                requestId, machineIdentifier, "TEST-PC-01", "1.0.0", "Windows 11 Test")),
        };
        request.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // An administrator approves the REQUEST (the existing flow, which mints
        // the single-use token the claim will consume).
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var approvals = scope.ServiceProvider.GetRequiredService<EnrollmentApprovalService>();
            await using var dbContext = _fixture.CreateDbContext();
            var organizationId = await dbContext.Organizations
                .OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();

            (await approvals.ApproveAsync(
                    requestId, organizationId, Guid.CreateVersion7(), "claim-approver@test.local"))
                .Success.ShouldBeTrue();
        }

        // The claim completes AS a re-enrollment of the active device, first time.
        var claim = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.EnrollClaim, UriKind.Relative))
        {
            Content = JsonContent.Create(new EnrollmentClaimRequest(requestSecret)),
        };
        claim.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());

        var claimed = await client.SendAsync(claim);
        claimed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var claimedBody = (await claimed.Content.ReadFromJsonAsync<EnrollmentClaimResponse>())!;

        claimedBody.Status.ShouldBe("approved");
        claimedBody.DeviceId.ShouldBe(firstBody.DeviceId, "the approved machine keeps its device record");
        claimedBody.ReEnrolled.ShouldBeTrue();

        await using (var dbContext = _fixture.CreateDbContext())
        {
            (await dbContext.AgentCredentials.SingleAsync(c => c.KeyId == firstBody.CredentialKeyId))
                .IsActive.ShouldBeFalse("the claim-path re-enrollment rotates the credential as always");
        }

        (await FindPendingAsync(await ReEnrollRequestIdAsync(machineIdentifier)))
            .ShouldBeNull("an approved claim must not ALSO park a re-enrollment request");
    }

    [Fact]
    public async Task The_old_credential_stops_authenticating_after_an_approved_re_enrollment()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        (await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await DecideReEnrollAsync(
            await ReEnrollRequestIdAsync(machineIdentifier), PendingEnrollmentStatus.Approved);
        (await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Replay the superseded credential against heartbeat.
        var heartbeat = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Heartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest("TEST-PC-01", "1.0.0", null, DateTimeOffset.UtcNow)),
        };
        heartbeat.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        heartbeat.Headers.Add(
            AgentProtocol.Headers.Credential,
            $"{firstBody.CredentialKeyId}.{firstBody.CredentialSecret}");

        var response = await client.SendAsync(heartbeat);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// With the pending store unreachable, a re-enrollment is REFUSED — "could not
    /// check for approval" must never behave as "approved". Pinned at the service
    /// level because the HTTP fixture rightly runs against a live Redis.
    /// </summary>
    [Fact]
    public async Task The_gate_fails_closed_when_the_pending_store_is_unreachable()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        var (deadTokenId, deadStoreSecret) = await IssueTokenAsync(maxUses: 1);

        await using var dbContext = _fixture.CreateDbContext();

        var deadStore = new PendingEnrollmentStore(
            await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(
                "127.0.0.1:1,abortConnect=false,connectTimeout=100"),
            new Infrastructure.Security.AesGcmSecretProtector(
                Microsoft.Extensions.Options.Options.Create(new Infrastructure.Security.SecretProtectionOptions())),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PendingEnrollmentStore>.Instance);

        var service = new AgentEnrollmentService(
            dbContext,
            new Infrastructure.Auditing.AuditWriter(
                dbContext, TimeProvider.System,
                new Infrastructure.Hosting.CorrelationIdAccessor(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor()),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEnrollmentService>.Instance,
            new Infrastructure.Security.EscrowSealingKeyProvider(null),
            deadStore);

        var outcome = await service.EnrollAsync(
            deadStoreSecret, "TEST-PC-01", machineIdentifier, "1.0.0", "Windows 11 Test");

        outcome.Success.ShouldBeFalse("no reachable store means no provable approval, so refuse");

        (await TokenUseCountAsync(deadTokenId)).ShouldBe(0, "a fail-closed refusal must not spend the token");

        await using var verify = _fixture.CreateDbContext();
        (await verify.AgentCredentials.SingleAsync(c => c.KeyId == firstBody.CredentialKeyId))
            .IsActive.ShouldBeTrue("fail-closed must leave the real device untouched");
    }

    /// <summary>Problem+json with the per-request correlation id stripped.</summary>
    private static string NormalizeProblem(string problemJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(problemJson);
        var stripped = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "correlationId")
            .ToDictionary(p => p.Name, p => p.Value.ToString());
        return System.Text.Json.JsonSerializer.Serialize(stripped);
    }

    [Fact]
    public async Task A_wrong_protocol_version_is_rejected()
    {
        var (_, secret) = await IssueTokenAsync();
        using var client = CreateClient();

        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll, UriKind.Relative))
        {
            Content = JsonContent.Create(MakeRequest(secret)),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, "999");

        var response = await client.SendAsync(message);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refused_enrollments_are_recorded_in_the_audit_trail()
    {
        using var client = CreateClient();

        await PostEnrollAsync(client, MakeRequest(SecretGenerator.GenerateSecret()) with { Hostname = "PROBE-HOST" });

        await using var dbContext = _fixture.CreateDbContext();
        var denial = await dbContext.AuditLogEntries
            .Where(a => a.Action == "device.enroll"
                        && a.Result == Domain.Auditing.AuditResult.Denied
                        && a.ActorDisplay == "PROBE-HOST")
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync();

        denial.ShouldNotBeNull("a refused enrollment attempt is a security signal and must be audited");
        denial.FailureReason.ShouldNotBeNull();
    }

    [Fact]
    public async Task Successful_enrollment_is_recorded_in_the_audit_trail_without_the_secret()
    {
        var (tokenId, secret) = await IssueTokenAsync();
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        await using var dbContext = _fixture.CreateDbContext();
        var entry = await dbContext.AuditLogEntries
            .Where(a => a.Action == "device.enroll" && a.DeviceId == body.DeviceId)
            .SingleAsync();

        entry.Result.ShouldBe(Domain.Auditing.AuditResult.Success);
        entry.TargetId.ShouldBe(tokenId.ToString());

        // Neither the enrollment token secret nor the credential secret may appear
        // anywhere in the audit entry.
        var serialized = System.Text.Json.JsonSerializer.Serialize(new
        {
            entry.NewState,
            entry.PreviousState,
            entry.FailureReason,
            entry.TargetDisplay,
        });
        serialized.ShouldNotContain(secret);
        serialized.ShouldNotContain(body.CredentialSecret);
    }
}
