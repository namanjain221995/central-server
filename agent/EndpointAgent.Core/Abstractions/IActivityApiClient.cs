using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The agent's activity-upload call, kept separate from <see cref="IAgentApiClient"/> so
/// that interface's many fakes are unaffected and only the upload worker depends on it.
/// </summary>
/// <remarks>
/// <see cref="EndpointAgent.Core.Communication.AgentApiClient"/> implements both, so the
/// same typed <c>HttpClient</c> serves both.
/// </remarks>
public interface IActivityApiClient
{
    /// <summary>Uploads a batch of activity-presence samples. Idempotent server-side.</summary>
    Task<AgentApiResult<Unit>> UploadActivityAsync(
        IReadOnlyList<ActivitySampleDto> samples,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);
}
