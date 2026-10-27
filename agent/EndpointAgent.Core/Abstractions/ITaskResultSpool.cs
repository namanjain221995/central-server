using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>A task result that was produced but could not be delivered to the server yet.</summary>
/// <param name="TaskId">The task the result belongs to.</param>
/// <param name="Result">Exactly what would have been posted.</param>
/// <param name="SpooledAt">When it was spooled, by this machine's clock; drives the age limit.</param>
public sealed record SpooledTaskResult(Guid TaskId, AgentTaskResult Result, DateTimeOffset SpooledAt);

/// <summary>
/// Durable holding area for task results whose one POST failed, so a result
/// survives a network blip, a server deploy, or an agent restart and is delivered
/// on a later heartbeat instead of being lost forever.
/// </summary>
/// <remarks>
/// <para>
/// Generic over every task type. The retry channel is the heartbeat tick, not the
/// task path: the server's <c>TasksPending</c> counts only Queued tasks, so once a
/// task is Delivered and its result lost, a quiet device would never enter the task
/// path again. <see cref="EndpointAgent.Core.Tasks.TaskDispatcher"/> saves here when
/// a post fails, and <see cref="EndpointAgent.Core.Heartbeat.HeartbeatLoop"/> flushes
/// through the dispatcher on every successful heartbeat.
/// </para>
/// <para>
/// <b>No secrets.</b> A spooled entry is only what would have gone over the wire
/// anyway, and task results carry no secrets by contract (<see cref="AgentTaskResult"/>:
/// "no secrets" on both the message and the result document). That is why the
/// Windows store is plain JSON under the ACL-protected state directory, not sealed.
/// </para>
/// <para>
/// Bounded: implementations keep at most <see cref="TaskResultSpoolLimits.MaxEntries"/>
/// entries (the oldest goes first), and the flush drops anything older than
/// <see cref="TaskResultSpoolLimits.MaxAge"/>.
/// </para>
/// </remarks>
public interface ITaskResultSpool
{
    /// <summary>
    /// Whether anything may be waiting. An in-memory flag -- initialised by one check
    /// of the store when the implementation is created (which also covers an agent
    /// restarted mid-post) and set by <see cref="SaveAsync"/> -- so the per-heartbeat
    /// flush costs nothing on the common, empty path.
    /// </summary>
    bool HasEntries { get; }

    /// <summary>Stores <paramref name="entry"/>, replacing any earlier entry for the same task, and enforces the entry cap.</summary>
    ValueTask SaveAsync(SpooledTaskResult entry, CancellationToken cancellationToken = default);

    /// <summary>Every stored entry, oldest first. Empty rather than throwing when nothing is stored or the store is unreadable.</summary>
    ValueTask<IReadOnlyList<SpooledTaskResult>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the entry for <paramref name="taskId"/>, if any.</summary>
    ValueTask RemoveAsync(Guid taskId, CancellationToken cancellationToken = default);
}

/// <summary>The bounds every <see cref="ITaskResultSpool"/> lives within.</summary>
public static class TaskResultSpoolLimits
{
    /// <summary>Most results held at once; saving beyond this drops the oldest.</summary>
    public const int MaxEntries = 50;

    /// <summary>
    /// How long a result is worth retrying. Past this the server has long since
    /// expired the task and re-scanned or re-asked; delivering it would only be noise.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);
}
