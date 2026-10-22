using System.Text.Json;
using EndpointAgent.Core.Chat;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Opens the live IT-support chat window on the device for a session: a small
/// window on the signed-in employee's desktop that shows the administrator's name
/// and relays plain text both ways. Reports honestly whether anyone was there to
/// see it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is validated before a window is shown.</b> The session id must be
/// a real id and the admin display name must be plain text within bounds
/// (<see cref="ChatText"/>), so a payload this executor accepts always carries a
/// name the window can draw and the pipe can carry. A payload that is missing,
/// malformed or out of bounds is a failure result and nothing is shown.
/// </para>
/// <para>
/// <b>An expired task opens nothing.</b> A chat is live; a StartLiveChat that sat
/// past its deadline (a long stall, a skewed clock) would pop a window for a
/// conversation the administrator has long since moved on from, so it is refused
/// rather than shown late -- exactly as a late screen lock is. The matching
/// <see cref="EndLiveChatExecutor"/>, like a release, runs whenever it arrives.
/// </para>
/// <para>
/// <b>Nobody signed in is still success.</b> The chat exists on the server and the
/// administrator is told the useful truth: the window was shown, or there was no
/// interactive user on the device to show it to.
/// </para>
/// </remarks>
public sealed class StartLiveChatExecutor(
    ChatManager manager,
    ILogger<StartLiveChatExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly ChatManager _manager = manager;
    private readonly ILogger<StartLiveChatExecutor> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "StartLiveChat";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var now = _time.GetUtcNow();

        if (task.ExpiresAt is { } deadline && now >= deadline)
        {
            _logger.LogWarning(
                "Live-chat task {TaskId} refused: its deadline {Deadline:u} passed before execution (now {Now:u}).",
                task.TaskId, deadline, now);
            return new AgentTaskResult(
                false,
                $"Live chat refused: the task expired at {deadline:u} before the device executed it; no window was shown.",
                null);
        }

        if (!TryParse(task.PayloadJson, out var sessionId, out var adminDisplay, out var refusal))
        {
            _logger.LogWarning("Live-chat task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return new AgentTaskResult(false, $"Live chat refused: {refusal} No window was shown.", null);
        }

        ChatStartOutcome outcome;
        try
        {
            outcome = await _manager.StartAsync(sessionId, adminDisplay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (outcome.SessionCount == 0)
        {
            _logger.LogInformation("Live-chat task {TaskId}: chat {SessionId} opened, but no interactive user is signed in.", task.TaskId, sessionId);
            return new AgentTaskResult(
                true,
                "Live chat started, but no interactive user is signed in on this device, so no window is shown yet.",
                ResultJson(sessionId, sessions: 0));
        }

        _logger.LogInformation(
            "Live-chat task {TaskId}: chat {SessionId} window shown in {Count} session(s).", task.TaskId, sessionId, outcome.SessionCount);
        return new AgentTaskResult(
            true,
            $"Live chat window shown in {outcome.SessionCount} session(s).",
            ResultJson(sessionId, outcome.SessionCount));
    }

    /// <summary>
    /// Reads the payload the server queued: the chat session id and the
    /// administrator's display name, held to the bounds the window and pipe
    /// enforce. Unknown extra properties are tolerated -- the payload arrives over
    /// the authenticated task channel -- but the two that matter must be right.
    /// </summary>
    private static bool TryParse(string? payloadJson, out Guid sessionId, out string adminDisplay, out string refusal)
    {
        sessionId = Guid.Empty;
        adminDisplay = string.Empty;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            refusal = "the task carried no payload.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            refusal = "the payload was not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusal = "the payload was not a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("sessionId", out var idElement) || !idElement.TryGetGuid(out sessionId) || sessionId == Guid.Empty)
            {
                refusal = "the payload named no chat session id.";
                return false;
            }

            if (!root.TryGetProperty("adminDisplay", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || !ChatText.IsValidAdminDisplay(nameElement.GetString()))
            {
                refusal = $"the admin display name must be plain text of 1 to {ChatText.MaxAdminDisplayChars} characters.";
                return false;
            }

            adminDisplay = nameElement.GetString()!;
            refusal = string.Empty;
            return true;
        }
    }

    private static string ResultJson(Guid sessionId, int sessions) => JsonSerializer.Serialize(new { sessionId, sessions });
}
