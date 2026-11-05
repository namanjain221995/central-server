using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The agent's file-collection calls to the server, kept separate from
/// <see cref="IAgentApiClient"/> so the many existing fakes of that interface are
/// unaffected and only the collection worker depends on this capability.
/// </summary>
/// <remarks>
/// <see cref="EndpointAgent.Core.Communication.AgentApiClient"/> implements both
/// interfaces, so the same typed <c>HttpClient</c> serves both.
/// </remarks>
public interface IFileCollectionApiClient
{
    /// <summary>Publishes the file-collection key material for a transfer, before any frame.</summary>
    Task<AgentApiResult<Unit>> PostFileCollectionHeaderAsync(
        string transferId,
        AgentFileCollectionHeader header,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads one ciphertext frame. <see cref="AgentApiStatus.TransientFailure"/>
    /// means backpressure (retry the same frame); <see cref="AgentApiStatus.Rejected"/>
    /// or <see cref="AgentApiStatus.NotFound"/> means the transfer is gone (stop).
    /// </summary>
    Task<AgentApiResult<Unit>> UploadFileCollectionChunkAsync(
        string transferId,
        long seq,
        byte[] frame,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Reports the final result of a file collection.</summary>
    Task<AgentApiResult<Unit>> PostFileCollectionResultAsync(
        string transferId,
        AgentFileCollectionResult result,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);
}
