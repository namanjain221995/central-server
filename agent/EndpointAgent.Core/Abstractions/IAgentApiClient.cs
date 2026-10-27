using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The agent's view of the server. Abstracted so the enrollment/heartbeat logic
/// is unit-testable without a network.
/// </summary>
public interface IAgentApiClient
{
    /// <summary>
    /// Exchanges an enrollment token for a device credential. The token is used
    /// once and not retained by the client.
    /// </summary>
    Task<AgentApiResult<EnrollResponse>> EnrollAsync(
        EnrollRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Sends an authenticated heartbeat.</summary>
    /// <summary>
    /// Asks the server to record this machine as awaiting approval.
    /// </summary>
    /// <remarks>
    /// Anonymous, and carries no secret: only the SHA-256 of the request secret the
    /// agent keeps. Sending it repeatedly for the same request id is safe and is how
    /// a restarted agent resumes rather than duplicates.
    /// </remarks>
    Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(
        EnrollmentRequestRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Proves possession of the request secret and, once approved, collects the device
    /// credential exactly once.
    /// </summary>
    /// <remarks>
    /// The response is <c>pending</c> while an administrator has not decided,
    /// <c>rejected</c> when refused, and <c>approved</c> with a credential exactly
    /// once. The raw secret travels only here, only over HTTPS, and only after the
    /// agent has already been told to keep waiting.
    /// </remarks>
    Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(
        EnrollmentClaimRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(
        HeartbeatRequest request,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads an authenticated full inventory snapshot.</summary>
    Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(
        InventoryReport report,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports the USB devices attached to this machine and receives the storage
    /// policy the endpoint must enforce.
    /// </summary>
    /// <remarks>
    /// The response is the authoritative grant set, so this doubles as the
    /// convergence path: an agent that missed a pushed policy still gets the
    /// right answer the moment a user plugs something in. Nothing in the request
    /// can widen the response — grants come only from administrator decisions
    /// already recorded on the server.
    /// </remarks>
    Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(
        UsbReport report,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Claims queued tasks for this device.</summary>
    Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Reports the result of a completed task.</summary>
    Task<AgentApiResult<Unit>> PostTaskResultAsync(
        Guid taskId,
        AgentTaskResult result,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches the device's effective policies.</summary>
    Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Reports policy compliance results.</summary>
    Task<AgentApiResult<Unit>> PostComplianceAsync(
        AgentPolicyComplianceReport report,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a one-time secret reference (local-account passwords). The server
    /// deletes the secret atomically on read, so this succeeds at most once.
    /// </summary>
    Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(
        string secretReference,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a package's installer bytes to <paramref name="destination"/>. The
    /// caller is responsible for verifying the content hash and signer afterwards -
    /// this transfer is not a trust boundary.
    /// </summary>
    Task<AgentApiResult<Unit>> DownloadPackageAsync(
        Guid packageId,
        Stream destination,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams an approved driver package's archive to <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Addressed by package id, never by URL: there is deliberately no method on this
    /// client that fetches a caller-supplied address, so no task payload can direct
    /// the agent at arbitrary content. As with software packages, the caller verifies
    /// the hash, catalogue signature and signer pin afterwards -- this transfer is not
    /// a trust boundary.
    /// </remarks>
    Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(
        Guid packageId,
        Stream destination,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The published agent release the server currently offers. This is the
    /// agent's trust anchor for self-update: task payloads are cross-checked
    /// against it and refused on any disagreement.
    /// </summary>
    Task<AgentApiResult<EndpointPlatform.Contracts.Agent.AgentUpdateInfo>> GetAgentUpdateInfoAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Streams a published release's MSI into <paramref name="destination"/>.</summary>
    Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(
        Guid releaseId,
        Stream destination,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Asks which recovery protectors are owed an escrow.
    /// </summary>
    /// <remarks>
    /// Returns metadata only -- eligibility, the key to seal to, and each
    /// protector's escrow and retry position. No key material comes back, and the
    /// sealing key it does return is public and is verified against the pinned
    /// fingerprint before anything is sealed to it.
    /// </remarks>
    Task<AgentApiResult<EndpointPlatform.Contracts.Agent.BitLockerEscrowStatusResponse>>
        GetBitLockerEscrowStatusAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a sealed recovery envelope.
    /// </summary>
    /// <remarks>
    /// The request carries ciphertext only. A plaintext recovery password is never
    /// sent to the server by any path, and this contract has no field one could
    /// occupy.
    /// </remarks>
    Task<AgentApiResult<EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyResponse>>
        EscrowRecoveryKeyAsync(
            EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyRequest request,
            DeviceCredential credential,
            CancellationToken cancellationToken = default);

    /// <summary>
    /// The Company Portal catalog for this device: what it may offer its user,
    /// each entry with its state on this device.
    /// </summary>
    /// <remarks>
    /// Read-only and device-scoped. The three catalog calls are the only thing
    /// the service does on a signed-in user's request (ADR-0005, Company Portal
    /// amendment), and this one carries no input at all.
    /// </remarks>
    Task<AgentApiResult<CatalogListResponse>> GetCatalogAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>One catalog entry's PNG tile, bounded at the server's icon ceiling.</summary>
    Task<AgentApiResult<byte[]>> GetCatalogIconAsync(
        Guid appId,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the server to queue one catalog entry's install on this device.
    /// </summary>
    /// <remarks>
    /// Names an id; decides nothing. The server checks that the entry is
    /// published, visible to this device and needed, and the install that
    /// follows arrives as an ordinary InstallPackage task with every gate the
    /// executor already applies. The reported user is for the record only.
    /// </remarks>
    Task<AgentApiResult<CatalogInstallResponse>> RequestCatalogInstallAsync(
        Guid appId,
        CatalogInstallCommand command,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads this device's own live-chat session: whether it is still open, and the
    /// messages after <paramref name="afterSeq"/>.
    /// </summary>
    /// <remarks>
    /// Device-scoped: the server answers only for the session that belongs to the
    /// authenticated device, so a credential can never read another machine's chat.
    /// A simple immediate return of what exists now -- the agent short-polls it on
    /// its own cadence while the window is open; there is no long-poll to tune.
    /// </remarks>
    Task<AgentApiResult<ChatPollResponse>> PollChatMessagesAsync(
        Guid sessionId,
        int afterSeq,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends one message the person at the device typed to this device's own chat
    /// session. Plain text; the server bounds and validates it.
    /// </summary>
    /// <remarks>
    /// Names the session by id; the server scopes it to the authenticated device and
    /// refuses a session that is not this device's own or has already ended.
    /// </remarks>
    Task<AgentApiResult<Unit>> SendChatMessageAsync(
        Guid sessionId,
        string body,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>Placeholder for a response body-less call (204 No Content).</summary>
public sealed record Unit
{
    public static readonly Unit Value = new();
}

/// <summary>
/// Outcome of one API call, flattened to what retry logic needs: success with a
/// body, an authoritative rejection (do not retry with the same input), or a
/// transient failure (retry with backoff).
/// </summary>
public sealed record AgentApiResult<T>(T? Value, AgentApiStatus Status)
{
    public bool IsSuccess => Status == AgentApiStatus.Success;

    public static AgentApiResult<T> Success(T value) => new(value, AgentApiStatus.Success);

    public static AgentApiResult<T> Rejected() => new(default, AgentApiStatus.Rejected);

    public static AgentApiResult<T> Unauthorized() => new(default, AgentApiStatus.Unauthorized);

    public static AgentApiResult<T> Transient() => new(default, AgentApiStatus.TransientFailure);

    public static AgentApiResult<T> NotFound() => new(default, AgentApiStatus.NotFound);
}

public enum AgentApiStatus
{
    Success = 0,

    /// <summary>
    /// 4xx other than 401 (and other than 404 on a call that distinguishes
    /// <see cref="NotFound"/>): the server understood and said no. Retrying the same
    /// request is pointless.
    /// </summary>
    Rejected = 1,

    /// <summary>401: the credential is not accepted. Requires re-enrollment, not retry.</summary>
    Unauthorized = 2,

    /// <summary>Network failure, timeout or 5xx: retry with backoff.</summary>
    TransientFailure = 3,

    /// <summary>
    /// 404, reported ONLY by calls that need to tell it apart from other refusals --
    /// today the task-result post, where it means the server no longer has this task
    /// for this device (unknown, or cancelled before delivery), so a spooled result
    /// for it is dropped rather than retried. Every other call keeps reporting a 404
    /// as <see cref="Rejected"/>.
    /// </summary>
    NotFound = 4,
}
