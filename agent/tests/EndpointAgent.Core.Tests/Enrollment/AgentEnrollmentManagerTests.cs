using Microsoft.Extensions.Options;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Enrollment;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Enrollment;

public sealed class AgentEnrollmentManagerTests
{
    private readonly FakeCredentialStore _store = new();
    private readonly FakeApiClient _api = new();
    private readonly FakeSystemInfo _systemInfo = new();

    private readonly FakeEnrollmentStateStore _enrollmentState = new();

    // --------------------------------------------- approval-gated enrollment

    [Fact]
    public async Task With_no_token_the_agent_asks_to_be_managed_instead_of_giving_up()
    {
        // The MSI ships no token, so "no credential and no token" is the normal state
        // of a freshly installed agent, not a misconfiguration.
        var result = await CreateManager().EnsureEnrolledAsync(enrollmentToken: null, "1.0.0");

        result.ShouldBeNull("no credential is issued until an administrator approves");
        _api.RequestCalls.ShouldBe(1);
        _api.EnrollCalls.ShouldBe(0, "the token-based path must not be used");
    }

    [Fact]
    public async Task The_request_carries_the_hash_and_never_the_secret()
    {
        await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        var sent = _api.LastRequest.ShouldNotBeNull();
        var stored = _enrollmentState.State.ShouldNotBeNull();

        // Proof-of-possession: only the digest is transmitted.
        sent.RequestId.Length.ShouldBe(64);
        sent.RequestId.ShouldBe(stored.RequestId);
        sent.RequestId.ShouldNotBe(stored.RequestSecret);

        // The whole serialized request must not contain the secret anywhere.
        System.Text.Json.JsonSerializer.Serialize(sent)
            .ShouldNotContain(stored.RequestSecret);
    }

    [Fact]
    public async Task The_agent_never_sends_an_organization()
    {
        await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        // An unauthenticated caller must not be able to choose its tenant; the
        // organization comes from the approving administrator.
        System.Text.Json.JsonSerializer.Serialize(_api.LastRequest!)
            .ShouldNotContain("rganization");
    }

    [Fact]
    public async Task The_secret_is_persisted_before_the_request_is_sent()
    {
        // If the process dies between the two, a request would exist that nothing can
        // claim — an entry an administrator could approve to no effect.
        _api.RequestResult = AgentApiResult<EnrollmentRequestResponse>.Transient();

        await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        _enrollmentState.State.ShouldNotBeNull("state must survive a failed submission");
    }

    [Fact]
    public async Task A_restart_resumes_the_same_request_rather_than_creating_another()
    {
        var manager = CreateManager();
        await manager.EnsureEnrolledAsync(null, "1.0.0");
        var firstId = _enrollmentState.State!.RequestId;

        // Second pass stands in for a service restart or reboot: same persisted state.
        await manager.EnsureEnrolledAsync(null, "1.0.0");

        _api.RequestCalls.ShouldBe(1, "a resumed agent must not register a second request");
        _enrollmentState.State!.RequestId.ShouldBe(firstId);
        _api.ClaimCalls.ShouldBe(2, "it should poll the existing request instead");
    }

    [Fact]
    public async Task State_belonging_to_another_machine_is_discarded()
    {
        // A state file copied from a different PC describes a request this agent
        // cannot claim; honouring it would leave the machine polling forever.
        _enrollmentState.State = new PendingEnrollmentState(
            "someone-elses-secret", new string('a', 64), "https://server.test",
            "a-different-machine", DateTimeOffset.UtcNow);

        await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        _api.RequestCalls.ShouldBe(1, "a fresh request must be made");
        _enrollmentState.State!.MachineIdentifier.ShouldNotBe("a-different-machine");
    }

    [Fact]
    public async Task An_approved_claim_stores_the_credential_and_clears_the_request()
    {
        var deviceId = Guid.CreateVersion7();
        _api.ClaimResult = AgentApiResult<EnrollmentClaimResponse>.Success(
            new EnrollmentClaimResponse(
                "approved", deviceId, new string('k', 32), new string('s', 64), false, 0));

        var result = await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        result.ShouldNotBeNull();
        result!.DeviceId.ShouldBe(deviceId);
        _store.Stored.ShouldNotBeNull("the credential must be persisted");
        _enrollmentState.State.ShouldBeNull("the finished request must not linger");
    }

    [Fact]
    public async Task A_rejected_request_is_dropped_so_the_agent_stops_polling_it()
    {
        _api.ClaimResult = AgentApiResult<EnrollmentClaimResponse>.Success(
            new EnrollmentClaimResponse("rejected", null, null, null, false, 0));

        var result = await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        result.ShouldBeNull();
        _enrollmentState.State.ShouldBeNull();
        _store.Stored.ShouldBeNull("a rejected machine must never receive a credential");
    }

    [Fact]
    public async Task A_request_the_server_no_longer_knows_is_dropped()
    {
        // 403 covers unknown, already-claimed and expired — all indistinguishable by
        // design, and all meaning "this request is dead".
        _api.ClaimResult = AgentApiResult<EnrollmentClaimResponse>.Rejected();

        await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        _enrollmentState.State.ShouldBeNull("a dead request must not be polled forever");
        _store.Stored.ShouldBeNull();
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_request_for_the_next_attempt()
    {
        _api.ClaimResult = AgentApiResult<EnrollmentClaimResponse>.Transient();

        await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        // A network blip is not a rejection: throwing the request away here would
        // orphan whatever the administrator is looking at.
        _enrollmentState.State.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_approval_without_a_complete_credential_is_refused()
    {
        _api.ClaimResult = AgentApiResult<EnrollmentClaimResponse>.Success(
            new EnrollmentClaimResponse("approved", Guid.CreateVersion7(), null, null, false, 0));

        var result = await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        result.ShouldBeNull();
        _store.Stored.ShouldBeNull("an incomplete credential must never be stored");
    }

    [Fact]
    public async Task An_existing_credential_short_circuits_the_whole_protocol()
    {
        _store.Stored = new DeviceCredential(
            Guid.CreateVersion7(), new string('a', 32), new string('b', 64));

        var result = await CreateManager().EnsureEnrolledAsync(null, "1.0.0");

        result.ShouldNotBeNull();
        _api.RequestCalls.ShouldBe(0, "an enrolled machine must never re-enrol");
        _api.ClaimCalls.ShouldBe(0);
    }


    private AgentEnrollmentManager CreateManager() =>
        new(_api,
            _store,
            _enrollmentState,
            _systemInfo,
            Options.Create(new AgentOptions { ServerBaseUrl = "https://server.test" }),
            NullLogger<AgentEnrollmentManager>.Instance);

    /// <summary>In-memory stand-in for the DPAPI-protected pending-request store.</summary>
    private sealed class FakeEnrollmentStateStore : IEnrollmentStateStore
    {
        public PendingEnrollmentState? State { get; set; }

        public int ClearCalls { get; private set; }

        public ValueTask<PendingEnrollmentState?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(State);

        public ValueTask SaveAsync(PendingEnrollmentState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            State = null;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task An_existing_credential_is_returned_without_contacting_the_server()
    {
        var existing = new DeviceCredential(Guid.CreateVersion7(), new string('a', 32), new string('b', 64));
        _store.Stored = existing;

        var result = await CreateManager().EnsureEnrolledAsync("some-token", "1.0.0");

        result.ShouldBe(existing);
        _api.EnrollCalls.ShouldBe(0, "a machine with an identity must not re-enroll on every start");
    }

    [Fact]
    public async Task Without_credential_or_token_the_agent_does_not_enroll()
    {
        var result = await CreateManager().EnsureEnrolledAsync(enrollmentToken: null, "1.0.0");

        result.ShouldBeNull();
        _api.EnrollCalls.ShouldBe(0);
    }

    /// <summary>
    /// Enrollment must persist the sealing-key fingerprint, not just the credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gap that made automatic escrow silently impossible in the first
    /// production run. The server pinned the fingerprint against the credential it
    /// issued, the response carried it, the DPAPI store round-tripped it correctly,
    /// and the gate read it -- but nothing ever put it into the credential the agent
    /// constructed. Every device therefore stored null, and the runner declined at
    /// its first gate without ever contacting the server.
    /// </para>
    /// <para>
    /// It failed safe, which is why it went unnoticed: the symptom was nothing
    /// happening at all. Nothing in the suite covered the step between "the server
    /// sent it" and "the agent kept it", so this test exists precisely there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Enrollment_persists_the_sealing_key_fingerprint()
    {
        const string fingerprint = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        _api.EnrollResult = AgentApiResult<EnrollResponse>.Success(new EnrollResponse(
            Guid.CreateVersion7(), new string('c', 32), new string('d', 64), ReEnrolled: false,
            SealingPublicKey: "spki-base64", SealingKeyFingerprint: fingerprint));

        var result = await CreateManager().EnsureEnrolledAsync("valid-token", "1.0.0");

        result.ShouldNotBeNull();
        result.SealingKeyFingerprint.ShouldBe(fingerprint);

        // ...and it must be in what was PERSISTED, not merely in what was returned.
        _store.Stored.ShouldNotBeNull();
        _store.Stored.SealingKeyFingerprint.ShouldBe(fingerprint);

        // The whole point of persisting it.
        _store.Stored.IsAutomaticEscrowEligible.ShouldBeTrue();
    }

    /// <summary>
    /// A server with no sealing key configured leaves the credential unpinned, and
    /// that is a working device, not a broken one.
    /// </summary>
    [Fact]
    public async Task Enrollment_without_a_sealing_key_yields_a_valid_but_ineligible_credential()
    {
        _api.EnrollResult = AgentApiResult<EnrollResponse>.Success(new EnrollResponse(
            Guid.CreateVersion7(), new string('c', 32), new string('d', 64), ReEnrolled: false));

        var result = await CreateManager().EnsureEnrolledAsync("valid-token", "1.0.0");

        result.ShouldNotBeNull();
        result.SealingKeyFingerprint.ShouldBeNull();

        // Usable for everything else: this is every device enrolled before the
        // feature existed, and it must keep working.
        result.KeyId.ShouldNotBeNullOrWhiteSpace();
        result.ToHeaderValue().ShouldContain(".");
        result.IsAutomaticEscrowEligible.ShouldBeFalse();
    }

    /// <summary>
    /// Re-enrollment replaces the stored fingerprint, so a device that re-enrolls
    /// after a rotation pins the key it was actually given.
    /// </summary>
    [Fact]
    public async Task Re_enrollment_persists_the_new_fingerprint()
    {
        const string oldFingerprint = "1111111111111111111111111111111111111111111111111111111111111111";
        const string newFingerprint = "2222222222222222222222222222222222222222222222222222222222222222";

        // A device that had already pinned an older key.
        _store.Stored = new DeviceCredential(
            Guid.CreateVersion7(), new string('a', 32), new string('b', 64), oldFingerprint);

        // Removing the credential is what triggers re-enrollment.
        _store.Stored = null;

        _api.EnrollResult = AgentApiResult<EnrollResponse>.Success(new EnrollResponse(
            Guid.CreateVersion7(), new string('c', 32), new string('d', 64), ReEnrolled: true,
            SealingPublicKey: "spki-base64", SealingKeyFingerprint: newFingerprint));

        var result = await CreateManager().EnsureEnrolledAsync("valid-token", "1.0.0");

        result!.SealingKeyFingerprint.ShouldBe(newFingerprint);
        _store.Stored!.SealingKeyFingerprint.ShouldBe(newFingerprint);
        _store.Stored.SealingKeyFingerprint.ShouldNotBe(oldFingerprint);
    }

    [Fact]
    public async Task Successful_enrollment_persists_the_credential_before_returning()
    {
        _api.EnrollResult = AgentApiResult<EnrollResponse>.Success(new EnrollResponse(
            Guid.CreateVersion7(), new string('c', 32), new string('d', 64), ReEnrolled: false));

        var result = await CreateManager().EnsureEnrolledAsync("valid-token", "1.0.0");

        result.ShouldNotBeNull();
        _store.Stored.ShouldBe(result, "the credential must be persisted so the identity survives a crash");
        _api.LastEnrollRequest.ShouldNotBeNull();
        _api.LastEnrollRequest.EnrollmentToken.ShouldBe("valid-token");
        _api.LastEnrollRequest.Hostname.ShouldBe(_systemInfo.HostName);
    }

    [Fact]
    public async Task A_rejected_enrollment_returns_null_and_stores_nothing()
    {
        _api.EnrollResult = AgentApiResult<EnrollResponse>.Rejected();

        var result = await CreateManager().EnsureEnrolledAsync("bad-token", "1.0.0");

        result.ShouldBeNull();
        _store.Stored.ShouldBeNull();
    }

    [Fact]
    public async Task A_transient_failure_returns_null_so_the_caller_can_back_off_and_retry()
    {
        _api.EnrollResult = AgentApiResult<EnrollResponse>.Transient();

        var result = await CreateManager().EnsureEnrolledAsync("valid-token", "1.0.0");

        result.ShouldBeNull();
        _store.Stored.ShouldBeNull();
    }

    [Fact]
    public async Task Discarding_a_rejected_credential_clears_the_store()
    {
        _store.Stored = new DeviceCredential(Guid.CreateVersion7(), new string('a', 32), new string('b', 64));

        await CreateManager().DiscardRejectedCredentialAsync();

        _store.Stored.ShouldBeNull();
    }

    [Fact]
    public void The_credential_never_prints_its_secret()
    {
        var credential = new DeviceCredential(Guid.CreateVersion7(), new string('a', 32), "SUPER-SECRET-VALUE");

        credential.ToString().ShouldNotContain("SUPER-SECRET-VALUE");
        credential.ToString().ShouldContain("<redacted>");
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeCredentialStore : IDeviceCredentialStore
    {
        public DeviceCredential? Stored { get; set; }

        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Stored);

        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Stored = credential;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Stored is not null);

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            Stored = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApiClient : IAgentApiClient
    {
        // Automatic escrow is not exercised by these tests. Throwing rather than
        // returning a bland success keeps them honest: if one of them ever starts
        // reaching this path, it fails loudly instead of quietly appearing to escrow.
        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.BitLockerEscrowStatusResponse>>
            GetBitLockerEscrowStatusAsync(DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.CatalogListResponse>> GetCatalogAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<byte[]>> GetCatalogIconAsync(
            Guid appId, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.CatalogInstallResponse>> RequestCatalogInstallAsync(
            Guid appId, EndpointPlatform.Contracts.Agent.CatalogInstallCommand command, DeviceCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyResponse>>
            EscrowRecoveryKeyAsync(
                EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyRequest request,
                DeviceCredential credential,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int RequestCalls { get; private set; }

        public int ClaimCalls { get; private set; }

        public EnrollmentRequestRequest? LastRequest { get; private set; }

        public string? LastClaimSecret { get; private set; }

        public AgentApiResult<EnrollmentRequestResponse> RequestResult { get; set; } =
            AgentApiResult<EnrollmentRequestResponse>.Success(new EnrollmentRequestResponse("pending", 30));

        public AgentApiResult<EnrollmentClaimResponse> ClaimResult { get; set; } =
            AgentApiResult<EnrollmentClaimResponse>.Success(
                new EnrollmentClaimResponse("pending", null, null, null, false, 30));

        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(
            EnrollmentRequestRequest request, CancellationToken cancellationToken = default)
        {
            RequestCalls++;
            LastRequest = request;
            return Task.FromResult(RequestResult);
        }

        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(
            EnrollmentClaimRequest request, CancellationToken cancellationToken = default)
        {
            ClaimCalls++;
            LastClaimSecret = request.RequestSecret;
            return Task.FromResult(ClaimResult);
        }

        public int EnrollCalls { get; private set; }

        public EnrollRequest? LastEnrollRequest { get; private set; }

        public AgentApiResult<EnrollResponse> EnrollResult { get; set; } =
            AgentApiResult<EnrollResponse>.Transient();

        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(
            EnrollRequest request, CancellationToken cancellationToken = default)
        {
            EnrollCalls++;
            LastEnrollRequest = request;
            return Task.FromResult(EnrollResult);
        }

        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(
            HeartbeatRequest request, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<HeartbeatResponse>.Transient());

        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(
            InventoryReport report, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<InventoryResponse>.Transient());

        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(

            UsbReport report, DeviceCredential credential, CancellationToken cancellationToken = default) =>

            Task.FromResult(AgentApiResult<UsbPolicyResponse>.Transient());


        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<AgentTaskListResponse>.Success(new AgentTaskListResponse([])));

        public Task<AgentApiResult<Unit>> PostTaskResultAsync(
            Guid taskId, AgentTaskResult result, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));

        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<AgentPolicyListResponse>.Success(new AgentPolicyListResponse([])));

        public Task<AgentApiResult<Unit>> PostComplianceAsync(
            AgentPolicyComplianceReport report, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));

        public Task<AgentApiResult<Unit>> DownloadPackageAsync(
            Guid packageId, Stream destination, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));

        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(
            Guid packageId, Stream destination, DeviceCredential credential,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));

        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(
            string secretReference, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<RedeemSecretResponse>.Success(new RedeemSecretResponse("unused")));
        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.AgentUpdateInfo>> GetAgentUpdateInfoAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(Guid releaseId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSystemInfo : ISystemInfoProvider
    {
        public string HostName => "FAKE-PC";

        public string GetHostName() => HostName;

        public ValueTask<string> GetOperatingSystemDescriptionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult("Fake OS 1.0");

        public ValueTask<string> GetMachineIdentifierAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult("fake-machine-id");
    }
}
