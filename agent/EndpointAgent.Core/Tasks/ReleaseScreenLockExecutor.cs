using System.Text.Json;
using EndpointAgent.Core.ScreenLock;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Releases the screen lock the device is holding, restoring the machine exactly,
/// and reports honestly whether there was a lock to release.
/// </summary>
/// <remarks>
/// <para>
/// <b>The undo of <see cref="EngageScreenLockExecutor"/>, and the first of the two
/// ways out.</b> The other is an IT operator at the device entering the unlock
/// code. Both run through <see cref="ScreenLockManager"/>, which restores every
/// setting the lockdown changed before clearing the lock.
/// </para>
/// <para>
/// <b>No deadline refusal, unlike the engage.</b> An engage that arrives late is a
/// surprise that locks a working user out; a release that arrives late only frees
/// a device that is still locked and does nothing otherwise. Running it is always
/// the safe choice, so an expired release is still executed.
/// </para>
/// <para>
/// <b>Already unlocked is still success.</b> The task asked for the device to be
/// unlocked, and it is. The message says the device was not locked, so an
/// administrator reading the result knows what happened.
/// </para>
/// </remarks>
public sealed class ReleaseScreenLockExecutor(
    ScreenLockManager manager,
    ILogger<ReleaseScreenLockExecutor> logger) : ITaskExecutor
{
    private readonly ScreenLockManager _manager = manager;
    private readonly ILogger<ReleaseScreenLockExecutor> _logger = logger;

    public string TaskType => "ReleaseScreenLock";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var lockId = ParseLockId(task.PayloadJson);

        var outcome = await _manager.ReleaseAsync(lockId, cancellationToken);

        if (!outcome.WasLocked)
        {
            _logger.LogInformation("Screen-unlock task {TaskId}: the device was not locked; nothing to release.", task.TaskId);
            return new AgentTaskResult(
                true,
                "Screen lock released: the device was not locked.",
                ResultJson("NotLocked", lockId, outcome.ReleasedLockId));
        }

        _logger.LogWarning("Screen-unlock task {TaskId}: lock {LockId} released.", task.TaskId, outcome.ReleasedLockId);
        return new AgentTaskResult(
            true,
            "Screen lock released; the machine was restored.",
            ResultJson("Released", lockId, outcome.ReleasedLockId));
    }

    private static Guid ParseLockId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return Guid.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("lockId", out var id) && id.TryGetGuid(out var guid)
                ? guid
                : Guid.Empty;
        }
        catch (JsonException)
        {
            return Guid.Empty;
        }
    }

    private static string ResultJson(string outcome, Guid requestedLockId, Guid? releasedLockId) =>
        JsonSerializer.Serialize(new { outcome, requestedLockId, releasedLockId });
}
