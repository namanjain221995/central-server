using System.Text.Json;
using EndpointAgent.Core.Restarts;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Accepts a restart the server scheduled for a later moment and arms it on this
/// device, so it happens whether or not the device can reach the server then.
/// </summary>
/// <remarks>
/// <para>
/// <b>The moment is measured on the server's clock.</b> The payload says when
/// (<c>restartAt</c>, server time) and the task says what the server's clock
/// read when it handed the task out (<see cref="AgentTask.ServerTime"/>). The
/// due time is "that far from now" on this machine's clock, so a device whose
/// clock is wrong still restarts at the right moment.
/// </para>
/// <para>
/// Reports <c>Armed</c> at once -- the task's result is "the device has it",
/// not "the device restarted". Whether it restarted is shown by the boot time
/// the device reports on its next heartbeat after the moment.
/// </para>
/// </remarks>
public sealed class ScheduleRestartTaskExecutor(
    RestartScheduler scheduler,
    ILogger<ScheduleRestartTaskExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly RestartScheduler _scheduler = scheduler;
    private readonly ILogger<ScheduleRestartTaskExecutor> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "ScheduleRestart";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        DateTimeOffset restartAt;
        int warningSeconds;
        string? message;
        try
        {
            using var doc = JsonDocument.Parse(task.PayloadJson ?? "");
            var root = doc.RootElement;
            restartAt = root.GetProperty("restartAt").GetDateTimeOffset();
            warningSeconds = root.GetProperty("warningSeconds").GetInt32();
            message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new AgentTaskResult(false, "Malformed scheduled-restart payload; nothing was armed.", Result("Failed", null, null, 0));
        }

        var now = _time.GetUtcNow();
        var serverNow = task.ServerTime ?? now;
        var dueAt = now + (restartAt - serverNow);

        if (dueAt <= now)
        {
            _logger.LogWarning("Scheduled restart {TaskId} refused: its moment {RestartAt:u} had already passed.", task.TaskId, restartAt);
            return new AgentTaskResult(
                false,
                "Restart refused: its scheduled moment had already passed when the device received it; nothing was armed.",
                Result("Expired", restartAt, null, warningSeconds));
        }

        warningSeconds = Math.Clamp(warningSeconds, 30, 3600);
        if (message is { Length: > 512 })
        {
            message = message[..512];
        }

        await _scheduler.ArmAsync(new ArmedRestart(task.TaskId, dueAt, warningSeconds, message, now), cancellationToken);

        return new AgentTaskResult(
            true,
            $"Restart armed on the device: it restarts at {restartAt:u} with a {warningSeconds}s warning, with or without the network.",
            Result("Armed", restartAt, dueAt, warningSeconds));
    }

    /// <summary>
    /// <c>restartAt</c> is the server's moment, which the console compares with
    /// its own clock; <c>dueAt</c> is the same moment on this device's clock.
    /// </summary>
    private static string Result(string outcome, DateTimeOffset? restartAt, DateTimeOffset? dueAt, int warningSeconds) =>
        JsonSerializer.Serialize(new { outcome, restartAt, dueAt, warningSeconds });
}
