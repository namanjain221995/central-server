using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Claims queued tasks, routes each to its executor, and reports the result.
/// </summary>
/// <remarks>
/// <para>
/// Every task is executed and reported independently: one task's failure (or a
/// missing executor) never blocks the others, and an executor throwing is turned
/// into a reported failure, never an unhandled exception that kills the agent.
/// </para>
/// <para>
/// A result whose POST fails is not lost (agent 1.27.0): unless the server answered
/// 404 -- it has no such task for this device -- or refused it outright
/// (<see cref="AgentApiStatus.Rejected"/>, any other 4xx but 401: it understood and
/// said no, so the identical request can never succeed), the result goes to the
/// <see cref="ITaskResultSpool"/>, and <see cref="FlushSpooledResultsAsync"/>, which
/// the heartbeat loop calls on every successful tick, delivers it later.
/// </para>
/// </remarks>
public sealed class TaskDispatcher
{
    private readonly IAgentApiClient _apiClient;
    private readonly IReadOnlyDictionary<string, ITaskExecutor> _executors;
    private readonly ILogger<TaskDispatcher> _logger;
    private readonly ITaskResultSpool? _resultSpool;
    private readonly TimeProvider _time;

    /// <param name="apiClient">The server.</param>
    /// <param name="executors">One executor per supported task type.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="resultSpool">
    /// Where undeliverable results wait. Optional: absent (an older composition, a
    /// test) means a failed post is logged and the result is lost, as before 1.27.0.
    /// </param>
    /// <param name="timeProvider">The clock that stamps and ages spooled results.</param>
    public TaskDispatcher(
        IAgentApiClient apiClient,
        IEnumerable<ITaskExecutor> executors,
        ILogger<TaskDispatcher> logger,
        ITaskResultSpool? resultSpool = null,
        TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resultSpool = resultSpool;
        _time = timeProvider ?? TimeProvider.System;

        var map = new Dictionary<string, ITaskExecutor>(StringComparer.Ordinal);
        foreach (var executor in executors)
        {
            map[executor.TaskType] = executor;
        }

        _executors = map;
    }

    /// <summary>Claims and runs all currently-queued tasks for this device.</summary>
    public async Task RunPendingAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        var claim = await _apiClient.ClaimTasksAsync(credential, cancellationToken);

        if (!claim.IsSuccess || claim.Value is null || claim.Value.Tasks.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Claimed {Count} task(s) for execution.", claim.Value.Tasks.Count);

        foreach (var task in claim.Value.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteOneAsync(task, cancellationToken);
            await ReportAsync(task, result, credential, cancellationToken);
        }
    }

    /// <summary>
    /// Delivers spooled results, oldest first. A no-op -- no I/O, no request -- when
    /// the spool is empty, which is what makes it cheap enough to run on every
    /// heartbeat tick.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>Older than <see cref="TaskResultSpoolLimits.MaxAge"/>: dropped unsent.</item>
    ///   <item>Accepted (2xx): removed.</item>
    ///   <item>404: removed -- the server has no such task for this device any more
    ///   (unknown, or cancelled before delivery), so it can never be accepted.</item>
    ///   <item>Rejected (any other 4xx but 401): removed, with a warning naming the
    ///   task -- the server understood the request and said no, and re-posting the
    ///   identical bytes every heartbeat for 48 hours cannot change that answer. The
    ///   pass moves on to the next entry.</item>
    ///   <item>Transient failure (network, timeout, 5xx) or 401: kept, and this pass
    ///   stops -- the server is unreachable or the credential is being replaced, so
    ///   the rest would fail the same way. The next successful heartbeat tries again.</item>
    ///   <item>Any status this code does not know: kept (bounded by age and the entry
    ///   cap) and the pass moves on to the next entry.</item>
    /// </list>
    /// </remarks>
    public async Task FlushSpooledResultsAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        if (_resultSpool is not { HasEntries: true })
        {
            return;
        }

        var entries = await _resultSpool.LoadAsync(cancellationToken);
        var now = _time.GetUtcNow();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (now - entry.SpooledAt > TaskResultSpoolLimits.MaxAge)
            {
                _logger.LogWarning(
                    "Spooled result for task {TaskId} is older than {MaxAge} and was dropped undelivered.",
                    entry.TaskId, TaskResultSpoolLimits.MaxAge);
                await _resultSpool.RemoveAsync(entry.TaskId, cancellationToken);
                continue;
            }

            var post = await _apiClient.PostTaskResultAsync(entry.TaskId, entry.Result, credential, cancellationToken);

            if (post.IsSuccess)
            {
                _logger.LogInformation("Delivered the spooled result for task {TaskId}.", entry.TaskId);
                await _resultSpool.RemoveAsync(entry.TaskId, cancellationToken);
                continue;
            }

            if (post.Status == AgentApiStatus.NotFound)
            {
                _logger.LogWarning(
                    "The server no longer has task {TaskId}; its spooled result was dropped.", entry.TaskId);
                await _resultSpool.RemoveAsync(entry.TaskId, cancellationToken);
                continue;
            }

            if (post.Status == AgentApiStatus.Rejected)
            {
                _logger.LogWarning(
                    "The server rejected the spooled result for task {TaskId}; retrying cannot change that, so it was dropped.",
                    entry.TaskId);
                await _resultSpool.RemoveAsync(entry.TaskId, cancellationToken);
                continue;
            }

            if (post.Status is AgentApiStatus.TransientFailure or AgentApiStatus.Unauthorized)
            {
                _logger.LogInformation(
                    "Spooled results not delivered ({Status}); they will be retried on the next heartbeat.", post.Status);
                return;
            }

            _logger.LogWarning(
                "The spooled result for task {TaskId} was not delivered ({Status}); it is kept and retried until it ages out.",
                entry.TaskId, post.Status);
        }
    }

    /// <summary>
    /// Posts one result; spools it when the post fails for any reason other than
    /// 404 or <see cref="AgentApiStatus.Rejected"/> (both final answers from a server
    /// that understood the request), including the service stopping mid-post (an
    /// agent update restarts the service, and the result must outlive that).
    /// </summary>
    private async Task ReportAsync(
        AgentTask task, AgentTaskResult result, DeviceCredential credential, CancellationToken cancellationToken)
    {
        AgentApiResult<Unit> post;
        try
        {
            post = await _apiClient.PostTaskResultAsync(task.TaskId, result, credential, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SpoolAsync(task, result, "the agent is stopping");
            throw;
        }

        if (post.IsSuccess)
        {
            return;
        }

        if (post.Status == AgentApiStatus.NotFound)
        {
            // The server has no such task for this device: there is nobody to deliver
            // it to, now or later.
            _logger.LogWarning(
                "The server did not recognise task {TaskId} ({Type}); its result was not delivered.",
                task.TaskId, task.Type);
            return;
        }

        if (post.Status == AgentApiStatus.Rejected)
        {
            // Any other 4xx but 401: the server understood the result and said no.
            // Re-posting the identical result every heartbeat cannot change that.
            _logger.LogWarning(
                "The server rejected the result of task {TaskId} ({Type}); retrying cannot change that, so it was not spooled.",
                task.TaskId, task.Type);
            return;
        }

        // TransientFailure (network, timeout, 5xx), Unauthorized (credential being
        // replaced) and anything unforeseen: worth another try on a later heartbeat.
        await SpoolAsync(task, result, post.Status.ToString());
    }

    private async Task SpoolAsync(AgentTask task, AgentTaskResult result, string reason)
    {
        if (_resultSpool is null)
        {
            _logger.LogWarning(
                "The result of task {TaskId} ({Type}) could not be delivered ({Reason}) and there is no spool; it is lost.",
                task.TaskId, task.Type, reason);
            return;
        }

        try
        {
            // CancellationToken.None: this runs on the way out of a cancelled post
            // too, and a half-written entry would be worse than none.
            await _resultSpool.SaveAsync(new SpooledTaskResult(task.TaskId, result, _time.GetUtcNow()), CancellationToken.None);
            _logger.LogWarning(
                "The result of task {TaskId} ({Type}) could not be delivered ({Reason}); it was spooled and will be "
                + "retried on the next heartbeat.",
                task.TaskId, task.Type, reason);
        }
        catch (Exception ex)
        {
            // Never let the spool break task dispatch: the worst case is the
            // pre-1.27.0 behaviour, a lost result.
            _logger.LogError(ex, "Could not spool the result of task {TaskId} ({Type}); it is lost.", task.TaskId, task.Type);
        }
    }

    private async Task<AgentTaskResult> ExecuteOneAsync(AgentTask task, CancellationToken cancellationToken)
    {
        if (!_executors.TryGetValue(task.Type, out var executor))
        {
            _logger.LogWarning("No executor for task type '{Type}' (task {TaskId}).", task.Type, task.TaskId);
            return new AgentTaskResult(false, $"Unsupported task type '{task.Type}'.", null);
        }

        try
        {
            _logger.LogInformation("Executing task {TaskId} ({Type}).", task.TaskId, task.Type);
            return await executor.ExecuteAsync(task, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} ({Type}) threw.", task.TaskId, task.Type);

            // Carry the exception's message, not just its type. An operator reading
            // "Execution failed: Win32Exception." in the dashboard learns nothing and
            // has to go digging through agent logs on the endpoint; the message
            // usually names the actual problem ("Access denied", "password does not
            // meet complexity requirements"). Exception messages from these paths
            // describe the Windows failure, never the values passed in, so this does
            // not leak a password into the result.
            var detail = ex.Message.Trim();
            var message = string.IsNullOrEmpty(detail)
                ? $"Execution failed: {ex.GetType().Name}."
                : $"Execution failed: {detail}";

            // The result column is bounded server-side; keep well inside it.
            if (message.Length > 500)
            {
                message = message[..500];
            }

            return new AgentTaskResult(false, message, null);
        }
    }
}
