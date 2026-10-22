using System.Text.Json;
using EndpointAgent.Core.Chat;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Closes the live chat window the device is holding and reports honestly whether
/// there was a chat to close.
/// </summary>
/// <remarks>
/// <para>
/// <b>The undo of <see cref="StartLiveChatExecutor"/>.</b> The chat also closes on
/// its own when the server reports the session has ended or when the person closes
/// the window; this task is the administrator's explicit end. All three run through
/// <see cref="ChatManager"/>.
/// </para>
/// <para>
/// <b>No deadline refusal, unlike the start.</b> A start that arrives late pops a
/// stale window; an end that arrives late only closes a window that may still be
/// open and does nothing otherwise. Running it is always the safe choice, so an
/// expired end is still executed -- exactly as a screen-lock release is.
/// </para>
/// <para>
/// <b>Already closed is still success.</b> The task asked for the window to be
/// gone, and it is. The message says there was no open chat, so an administrator
/// reading the result knows what happened.
/// </para>
/// </remarks>
public sealed class EndLiveChatExecutor(
    ChatManager manager,
    ILogger<EndLiveChatExecutor> logger) : ITaskExecutor
{
    private readonly ChatManager _manager = manager;
    private readonly ILogger<EndLiveChatExecutor> _logger = logger;

    public string TaskType => "EndLiveChat";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var sessionId = ParseSessionId(task.PayloadJson);

        var outcome = await _manager.EndAsync(sessionId, cancellationToken);

        if (!outcome.WasOpen)
        {
            _logger.LogInformation("Live-chat end task {TaskId}: no chat was open on this device; nothing to close.", task.TaskId);
            return new AgentTaskResult(
                true,
                "Live chat ended: no chat window was open on this device.",
                ResultJson("NotOpen", sessionId, outcome.EndedSessionId));
        }

        _logger.LogInformation("Live-chat end task {TaskId}: chat {SessionId} closed.", task.TaskId, outcome.EndedSessionId);
        return new AgentTaskResult(
            true,
            "Live chat ended; the window was closed.",
            ResultJson("Closed", sessionId, outcome.EndedSessionId));
    }

    private static Guid ParseSessionId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return Guid.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("sessionId", out var id) && id.TryGetGuid(out var guid)
                ? guid
                : Guid.Empty;
        }
        catch (JsonException)
        {
            return Guid.Empty;
        }
    }

    private static string ResultJson(string outcome, Guid requestedSessionId, Guid? endedSessionId) =>
        JsonSerializer.Serialize(new { outcome, requestedSessionId, endedSessionId });
}
