using System.ComponentModel;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Calls off the restart Windows is counting down, through the Windows shutdown
/// API, and reports honestly whether there was one to call off.
/// </summary>
/// <remarks>
/// <para>
/// <b>The undo of <see cref="RestartTaskExecutor"/>.</b> That executor hands the
/// grace period to Windows and returns; from then on Windows owns the countdown
/// and nothing in the agent is waiting on it. So cancelling is not "stop a
/// timer" but <c>AbortSystemShutdown</c>, which Windows honours up to the moment
/// it begins shutting down. This is the only place on the endpoint that calls
/// it, and it is reachable only through a server task that was permission-checked
/// and audited like the restart it undoes.
/// </para>
/// <para>
/// <b>Three outcomes, all honest.</b> <c>Cancelled</c>: Windows aborted a pending
/// restart or shutdown. <c>NothingToCancel</c>: Windows reported nothing pending
/// (<c>ERROR_NO_SHUTDOWN_IN_PROGRESS</c>) -- the restart already happened, was
/// never accepted, or was aborted locally -- reported as a failure of this task
/// because the thing it was asked to do did not happen, with the reason on the
/// result. <c>Failed</c>: Windows refused for another reason, with the Win32
/// error. Reporting an abort that did not happen as success would tell an
/// administrator a machine will stay up when it is about to go down.
/// </para>
/// <para>
/// <b>No deadline refusal, unlike the restart.</b> A restart that arrives late
/// is a surprise nobody expects; a cancellation that arrives late aborts only
/// what is still pending and does nothing otherwise. Running it is always the
/// safe choice, so an expired cancellation is still executed.
/// </para>
/// </remarks>
public sealed class CancelRestartTaskExecutor(
    IDeviceControl deviceControl,
    ILogger<CancelRestartTaskExecutor> logger,
    IRestartNotifier? notifier = null,
    TimeProvider? timeProvider = null,
    Restarts.RestartScheduler? scheduler = null) : ITaskExecutor
{
    private readonly Restarts.RestartScheduler? _scheduler = scheduler;

    /// <summary>Windows: no system shutdown is in progress, so there is nothing to abort.</summary>
    internal const int ErrorNoShutdownInProgress = 1116;

    private readonly IDeviceControl _deviceControl = deviceControl;
    private readonly ILogger<CancelRestartTaskExecutor> _logger = logger;
    private readonly IRestartNotifier _notifier = notifier ?? NullRestartNotifier.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "CancelRestart";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var restartTaskId = ParseRestartTaskId(task.PayloadJson);

        // A scheduled restart still waiting on this device for its moment has
        // no Windows countdown yet: removing it is the whole cancellation.
        // Once its countdown has started it is no longer armed, and the abort
        // below is what takes it back.
        if (restartTaskId is { } armedId && _scheduler is not null
            && await _scheduler.DisarmAsync(armedId, cancellationToken))
        {
            return new AgentTaskResult(
                true,
                "Restart cancelled: the scheduled restart was removed from the device before its countdown began.",
                ResultJson("Cancelled", restartTaskId, null));
        }

        try
        {
            await _deviceControl.AbortRestartAsync(cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoShutdownInProgress)
        {
            _logger.LogWarning(
                "Cancel-restart task {TaskId}: Windows reports no restart or shutdown pending; nothing was cancelled.",
                task.TaskId);
            return new AgentTaskResult(
                false,
                "Nothing to cancel: Windows reports no restart or shutdown pending on this device. " +
                "It may already have restarted, or the restart was never accepted.",
                ResultJson("NothingToCancel", restartTaskId, ex.NativeErrorCode));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Cancel-restart task {TaskId} failed: Windows refused to abort the shutdown.", task.TaskId);
            var code = (ex as Win32Exception)?.NativeErrorCode;
            return new AgentTaskResult(
                false,
                $"Cancel failed: {ex.Message.Trim().TrimEnd('.')}.",
                ResultJson("Failed", restartTaskId, code));
        }

        var now = _time.GetUtcNow();
        _logger.LogWarning("Cancel-restart task {TaskId}: the pending restart was aborted at {At:u}.", task.TaskId, now);

        // The user was told a restart was coming; tell them it is not. A notice
        // that cannot be delivered changes nothing about the abort itself.
        try
        {
            _notifier.RestartCancelled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancel-restart task {TaskId}: the session notice could not be sent.", task.TaskId);
        }

        return new AgentTaskResult(
            true,
            "Restart cancelled: Windows aborted the pending restart before it happened.",
            ResultJson("Cancelled", restartTaskId, null));
    }

    private static Guid? ParseRestartTaskId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("restartTaskId", out var id) && id.TryGetGuid(out var guid)
                ? guid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The structured result the console reads: which outcome, and which restart it was about.</summary>
    private static string ResultJson(string outcome, Guid? restartTaskId, int? code) =>
        JsonSerializer.Serialize(new { outcome, restartTaskId, code });
}
