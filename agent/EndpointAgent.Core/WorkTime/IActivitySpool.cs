using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.WorkTime;

/// <summary>
/// Durable, offline-safe storage for activity samples between the service receiving one
/// and the server accepting it. Nothing is dropped on an agent restart or an offline
/// stretch: a sample is removed only after the server has taken it.
/// </summary>
public interface IActivitySpool
{
    /// <summary>Persists one received sample.</summary>
    ValueTask AppendAsync(ActivitySampleDto sample, CancellationToken cancellationToken = default);

    /// <summary>The oldest up-to-<paramref name="max"/> samples, still held until <see cref="CommitAsync"/>.</summary>
    ValueTask<IReadOnlyList<ActivitySampleDto>> ClaimAsync(int max, CancellationToken cancellationToken = default);

    /// <summary>Removes the first <paramref name="count"/> claimed samples after the server accepted them.</summary>
    ValueTask CommitAsync(int count, CancellationToken cancellationToken = default);
}
