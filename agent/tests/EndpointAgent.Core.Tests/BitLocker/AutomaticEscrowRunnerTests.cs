using System.Security.Cryptography;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.BitLocker;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.BitLocker;

/// <summary>
/// The runtime that drives automatic escrow, and what it refuses to do.
/// </summary>
/// <remarks>
/// <para>
/// The gate tests prove the gates. These prove the <em>runtime</em> honours them:
/// that the thing actually running on an endpoint, given a realistic server
/// response, reaches <see cref="IRecoveryPasswordReader"/> exactly when it should
/// and never otherwise. Every blocked case asserts <c>Calls == 0</c>, because a
/// password read and discarded has still existed in an unerasable managed string.
/// </para>
/// <para>
/// The retry schedule is deliberately not modelled here. It lives on the server and
/// arrives as a <c>Due</c> flag, which is exactly why an agent restart cannot reset
/// it -- there is no local state to lose.
/// </para>
/// </remarks>
public sealed class AutomaticEscrowRunnerTests
{
    private const string Volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string Protector = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string OtherProtector = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private const string ValidPassword =
        "011000-011000-011000-011000-011000-011000-011000-011000";

    private static readonly RSA SealingKey = RSA.Create(3072);

    private static string Fingerprint => RecoveryPasswordSealer.Fingerprint(SealingKey);

    private static string PublicKey => Convert.ToBase64String(SealingKey.ExportSubjectPublicKeyInfo());

    /// <summary>Counts reads, which is the assertion most of these tests make.</summary>
    private sealed class SpyReader(RecoveryPasswordReadResult? result = null) : IRecoveryPasswordReader
    {
        public int Calls { get; private set; }

        public List<string> RequestedProtectors { get; } = [];

        public Task<RecoveryPasswordReadResult> ReadAsync(
            string volumeDeviceIdentifier, string keyProtectorId, CancellationToken cancellationToken = default)
        {
            Calls++;
            RequestedProtectors.Add(keyProtectorId);

            return Task.FromResult(result ?? new RecoveryPasswordReadResult(
                RecoveryPasswordReadStatus.Success, ValidPassword, keyProtectorId));
        }
    }

    /// <summary>
    /// A server that answers with whatever status it is given and records uploads.
    /// </summary>
    private sealed class FakeApi(
        BitLockerEscrowStatusResponse? status,
        bool uploadSucceeds = true) : IAgentApiClient
    {
        public List<EscrowRecoveryKeyRequest> Uploads { get; } = [];

        public int StatusCalls { get; private set; }

        public Task<AgentApiResult<BitLockerEscrowStatusResponse>> GetBitLockerEscrowStatusAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            StatusCalls++;

            return Task.FromResult(status is null
                ? new AgentApiResult<BitLockerEscrowStatusResponse>(null, AgentApiStatus.TransientFailure)
                : new AgentApiResult<BitLockerEscrowStatusResponse>(status, AgentApiStatus.Success));
        }

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

        public Task<AgentApiResult<EscrowRecoveryKeyResponse>> EscrowRecoveryKeyAsync(
            EscrowRecoveryKeyRequest request, DeviceCredential credential,
            CancellationToken cancellationToken = default)
        {
            Uploads.Add(request);

            return Task.FromResult(uploadSucceeds
                ? new AgentApiResult<EscrowRecoveryKeyResponse>(
                    new EscrowRecoveryKeyResponse("escrowed", Guid.CreateVersion7()), AgentApiStatus.Success)
                : new AgentApiResult<EscrowRecoveryKeyResponse>(null, AgentApiStatus.TransientFailure));
        }

        // Nothing else on the client is exercised by automatic escrow.
        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(EnrollRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(EnrollmentRequestRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(EnrollmentClaimRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest r, DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(InventoryReport r, DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(UsbReport r, DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostTaskResultAsync(Guid t, AgentTaskResult r, DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(AgentPolicyComplianceReport r, DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(string s, DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadPackageAsync(Guid p, Stream d, DeviceCredential c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(Guid p, Stream d, DeviceCredential c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentUpdateInfo>> GetAgentUpdateInfoAsync(DeviceCredential d, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(Guid r, Stream d, DeviceCredential c, CancellationToken t = default) => throw new NotSupportedException();
    }

    /// <summary>A credential pinned to the sealing key these tests use.</summary>
    private static DeviceCredential Credential() =>
        new(Guid.CreateVersion7(), new string('a', 32), new string('b', 64), Fingerprint);

    /// <summary>
    /// A credential with no pin: every device enrolled before automatic escrow.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Credential"/> on purpose. An optional parameter
    /// defaulting to the valid fingerprint made "pass null" silently mean "pass the
    /// good one", so the unpinned test asserted nothing while appearing to pass.
    /// </remarks>
    private static DeviceCredential UnpinnedCredential() =>
        new(Guid.CreateVersion7(), new string('a', 32), new string('b', 64), null);

    private static BitLockerEscrowStatusItem Item(
        bool escrowed = false, bool due = true, string state = "Pending", string? protector = null) =>
        new(Volume, protector ?? Protector, escrowed, escrowed ? DateTimeOffset.UtcNow : null, state, due, null);

    private static BitLockerEscrowStatusResponse Status(
        params BitLockerEscrowStatusItem[] items) =>
        new(true, Fingerprint, PublicKey, items);

    private static AutomaticEscrowRunner Runner(IAgentApiClient api, IRecoveryPasswordReader reader) =>
        new(api, new AutomaticEscrowGate(reader, NullLogger<AutomaticEscrowGate>.Instance),
            NullLogger<AutomaticEscrowRunner>.Instance);

    // ==== A-F: every blocked path reads nothing =============================

    /// <summary>A. No pinned fingerprint: the device is not eligible.</summary>
    [Fact]
    public async Task A_missing_fingerprint_reads_nothing()
    {
        var reader = new SpyReader();
        var api = new FakeApi(Status(Item()));

        var summary = await Runner(api, reader).RunAsync(UnpinnedCredential());

        reader.Calls.ShouldBe(0);
        api.Uploads.ShouldBeEmpty();
        summary.Escrowed.ShouldBe(0);

        // Not even a round trip: an ineligible device stops before asking.
        api.StatusCalls.ShouldBe(0);
    }

    /// <summary>B. The server offers a key that is not the pinned one.</summary>
    [Fact]
    public async Task B_fingerprint_mismatch_reads_nothing()
    {
        var reader = new SpyReader();
        using var attacker = RSA.Create(3072);

        var api = new FakeApi(new BitLockerEscrowStatusResponse(
            true, Fingerprint,
            Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo()),
            [Item()]));

        await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(0, "a substituted sealing key must not lead to a password being read");
        api.Uploads.ShouldBeEmpty();
    }

    /// <summary>C. Already filed.</summary>
    [Fact]
    public async Task C_already_escrowed_reads_nothing()
    {
        var reader = new SpyReader();
        var api = new FakeApi(Status(Item(escrowed: true, due: false, state: "Escrowed")));

        await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(0);
        api.Uploads.ShouldBeEmpty();
    }

    /// <summary>D. Scheduled, but not yet owed.</summary>
    [Fact]
    public async Task D_retry_not_due_reads_nothing()
    {
        var reader = new SpyReader();
        var api = new FakeApi(Status(Item(due: false, state: "Failed")));

        await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(0, "backoff is honoured by not reading, not by reading and discarding");
        api.Uploads.ShouldBeEmpty();
    }

    /// <summary>E. Every scheduled attempt used up.</summary>
    [Fact]
    public async Task E_retry_exhausted_reads_nothing()
    {
        var reader = new SpyReader();
        var api = new FakeApi(Status(Item(due: false, state: "RetryExhausted")));

        await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(0, "an exhausted protector must not be queried again without an admin reset");
        api.Uploads.ShouldBeEmpty();
    }

    /// <summary>F. The server has no sealing key configured at all.</summary>
    [Fact]
    public async Task F_missing_sealing_key_reads_nothing()
    {
        var reader = new SpyReader();
        var api = new FakeApi(new BitLockerEscrowStatusResponse(true, Fingerprint, null, [Item()]));

        await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(0);
        api.Uploads.ShouldBeEmpty();
    }

    /// <summary>The server may also withdraw eligibility, e.g. after revocation.</summary>
    [Fact]
    public async Task A_server_that_reports_ineligible_reads_nothing()
    {
        var reader = new SpyReader();
        var api = new FakeApi(new BitLockerEscrowStatusResponse(false, Fingerprint, PublicKey, [Item()]));

        await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(0);
        api.Uploads.ShouldBeEmpty();
    }

    /// <summary>An unreachable server is not a reason to start reading passwords.</summary>
    [Fact]
    public async Task An_unavailable_status_reads_nothing()
    {
        var reader = new SpyReader();
        var api = new FakeApi(status: null);

        await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(0);
        api.Uploads.ShouldBeEmpty();
    }

    // ==== G: the success path ==============================================

    /// <summary>G. Every gate passes: exactly one read, one upload, ciphertext only.</summary>
    [Fact]
    public async Task G_all_gates_pass_reads_exactly_once_and_uploads_an_envelope()
    {
        var reader = new SpyReader();
        var api = new FakeApi(Status(Item()));

        var summary = await Runner(api, reader).RunAsync(Credential());

        reader.Calls.ShouldBe(1, "a successful escrow must retrieve the password exactly once");
        reader.RequestedProtectors.ShouldHaveSingleItem().ShouldBe(Protector);

        summary.Escrowed.ShouldBe(1);

        var upload = api.Uploads.ShouldHaveSingleItem();
        upload.VolumeDeviceIdentifier.ShouldBe(Volume);
        upload.KeyProtectorId.ShouldBe(Protector);

        // L. What leaves the machine carries nothing of the password.
        upload.SealedEnvelope.ShouldNotContain(ValidPassword);
        upload.SealedEnvelope.ShouldNotContain("011000");
        upload.SealedEnvelope.ShouldNotMatch(@"\d{6}-\d{6}");

        // ...and it really is the sealed form, openable only with the private half.
        var envelope = System.Text.Json.JsonSerializer.Deserialize<RecoveryEscrowEnvelope>(
            upload.SealedEnvelope)!;

        envelope.Scheme.ShouldBe(RecoveryEscrowEnvelope.HybridRsaV1);
        envelope.KeyFingerprint.ShouldBe(Fingerprint);
    }

    // ==== H-K: failure, restart, retry, rotation ===========================

    /// <summary>H. A failed upload is reported so the server can advance the schedule.</summary>
    [Fact]
    public async Task H_an_upload_failure_is_counted_and_not_retried_locally()
    {
        var reader = new SpyReader();
        var api = new FakeApi(Status(Item()), uploadSucceeds: false);

        var summary = await Runner(api, reader).RunAsync(Credential());

        summary.Escrowed.ShouldBe(0);
        summary.Failed.ShouldBe(1);

        // One read, one upload attempt. A local retry loop would re-read the
        // password, which is what this design works hardest to avoid.
        reader.Calls.ShouldBe(1);
        api.Uploads.Count.ShouldBe(1);
    }

    /// <summary>
    /// A retrieval failure is reported without an envelope, so the server records a
    /// failed attempt and the backoff advances.
    /// </summary>
    [Fact]
    public async Task A_retrieval_failure_reports_the_attempt_without_key_material()
    {
        var reader = new SpyReader(
            RecoveryPasswordReadResult.Failed(RecoveryPasswordReadStatus.Refused, Protector));

        var api = new FakeApi(Status(Item()));

        var summary = await Runner(api, reader).RunAsync(Credential());

        summary.Failed.ShouldBe(1);

        var reported = api.Uploads.ShouldHaveSingleItem();
        reported.SealedEnvelope.ShouldBe(AutomaticEscrowRunner.CollectionFailedMarker);
        reported.SealedEnvelope.ShouldNotContain(ValidPassword);
    }

    /// <summary>
    /// I. Restarting the agent changes nothing, because the schedule is not held
    /// here. A fresh runner given the same server state behaves identically.
    /// </summary>
    [Fact]
    public async Task I_a_restarted_agent_does_not_reset_the_retry_schedule()
    {
        var credential = Credential();
        var notDue = Status(Item(due: false, state: "Failed"));

        for (var restart = 0; restart < 3; restart++)
        {
            var reader = new SpyReader();
            var api = new FakeApi(notDue);

            // A brand-new runner every time: this is what a service restart looks
            // like from here.
            await Runner(api, reader).RunAsync(credential);

            reader.Calls.ShouldBe(0, "restarting must not grant a fresh attempt");
            api.Uploads.ShouldBeEmpty();
        }
    }

    /// <summary>J. Once the server says an attempt is due again, exactly one happens.</summary>
    [Fact]
    public async Task J_a_due_retry_produces_exactly_one_successful_escrow()
    {
        var credential = Credential();

        var blocked = new SpyReader();
        await Runner(new FakeApi(Status(Item(due: false, state: "Failed"))), blocked)
            .RunAsync(credential);

        blocked.Calls.ShouldBe(0);

        var reader = new SpyReader();
        var api = new FakeApi(Status(Item(due: true, state: "Failed")));

        var summary = await Runner(api, reader).RunAsync(credential);

        reader.Calls.ShouldBe(1);
        summary.Escrowed.ShouldBe(1);
        api.Uploads.Count.ShouldBe(1);
    }

    /// <summary>
    /// K. A rotated protector is its own escrow target, with its own state, and the
    /// one already filed is not touched.
    /// </summary>
    [Fact]
    public async Task K_a_new_protector_is_escrowed_independently_of_the_old_one()
    {
        var reader = new SpyReader();

        var api = new FakeApi(Status(
            Item(escrowed: true, due: false, state: "Escrowed"),
            Item(escrowed: false, due: true, state: "Pending", protector: OtherProtector)));

        var summary = await Runner(api, reader).RunAsync(Credential());

        // Exactly one read, and for the new protector only.
        reader.Calls.ShouldBe(1);
        reader.RequestedProtectors.ShouldHaveSingleItem().ShouldBe(OtherProtector);

        summary.Escrowed.ShouldBe(1);
        api.Uploads.ShouldHaveSingleItem().KeyProtectorId.ShouldBe(OtherProtector);
    }

    /// <summary>
    /// A protector offered against a volume that did not report it is refused
    /// without a read, even though the server would refuse the upload anyway.
    /// </summary>
    [Fact]
    public async Task A_protector_from_another_volume_reads_nothing()
    {
        var reader = new SpyReader();

        var api = new FakeApi(new BitLockerEscrowStatusResponse(true, Fingerprint, PublicKey,
        [
            new BitLockerEscrowStatusItem(
                Volume, Protector, false, null, "Pending", true, null),
            new BitLockerEscrowStatusItem(
                @"\\?\Volume{22222222-2222-2222-2222-222222222222}\", OtherProtector,
                false, null, "Pending", true, null),
        ]));

        await Runner(api, reader).RunAsync(Credential());

        // Both are legitimately associated with their own volume, so both are read.
        reader.Calls.ShouldBe(2);
        reader.RequestedProtectors.ShouldBe([Protector, OtherProtector], ignoreOrder: true);
    }

    // ==== L: nothing on any path renders the password ======================

    [Fact]
    public async Task L_no_uploaded_body_on_any_path_contains_the_password()
    {
        foreach (var api in new[]
                 {
                     new FakeApi(Status(Item())),
                     new FakeApi(Status(Item()), uploadSucceeds: false),
                 })
        {
            await Runner(api, new SpyReader()).RunAsync(Credential());

            foreach (var upload in api.Uploads)
            {
                upload.SealedEnvelope.ShouldNotContain(ValidPassword);
                upload.SealedEnvelope.ShouldNotContain("011000");
                upload.SealedEnvelope.ShouldNotMatch(@"\d{6}-\d{6}");
            }
        }
    }
}
