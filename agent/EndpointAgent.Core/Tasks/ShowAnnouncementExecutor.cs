using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.SessionNotice;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Shows an administrator's announcement in every signed-in session, through the
/// session-notice pipe, and reports honestly how many sessions were there to see
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is validated before anything is shown.</b> The title and
/// message are checked against the same bounds the wire protocol enforces
/// (<see cref="SessionNoticeProtocol.IsValidTitle"/>), so a payload this
/// executor accepts always encodes to a line the notifier accepts -- a message
/// the service agreed to send can never be silently refused on arrival. A
/// payload that is missing, malformed or out of bounds is a failure result and
/// nothing reaches a screen.
/// </para>
/// <para>
/// <b>An expired task shows nothing.</b> Announcements carry a server TTL
/// precisely because stale news misleads: "the office closes early today" is
/// wrong tomorrow. A task whose deadline passed before the device executed it is
/// refused, like a late restart.
/// </para>
/// <para>
/// <b>Zero sessions is still success.</b> The announcement was sent and is kept
/// for replay; the user may simply be signed out, and anyone who signs in while
/// it lingers still sees it. The message says so, so an administrator reading
/// "0 sessions" knows what happened and what will.
/// </para>
/// </remarks>
public sealed class ShowAnnouncementExecutor(
    IAnnouncementNotifier notifier,
    ILogger<ShowAnnouncementExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly IAnnouncementNotifier _notifier = notifier;
    private readonly ILogger<ShowAnnouncementExecutor> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "ShowAnnouncement";

    public Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();

        if (task.ExpiresAt is { } deadline && now >= deadline)
        {
            _logger.LogWarning(
                "Announcement task {TaskId} refused: its deadline {Deadline:u} passed before execution (now {Now:u}).",
                task.TaskId, deadline, now);
            return Task.FromResult(new AgentTaskResult(
                false,
                $"Announcement refused: the task expired at {deadline:u} before the device executed it; nothing was shown.",
                null));
        }

        if (!TryParse(task.PayloadJson, out var announcementId, out var title, out var message, out var refusal))
        {
            _logger.LogWarning("Announcement task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return Task.FromResult(new AgentTaskResult(false, $"Announcement refused: {refusal} Nothing was shown.", null));
        }

        // Stamped now, with this clock: the linger window starts when the device
        // sends it, not when the administrator queued it. The payload's id rides
        // along so the notifier can recognise a re-delivery of the same message
        // and keep it dismissed if the user already closed it.
        var notice = new AnnouncementNotice(announcementId, title, message, now);

        // Read before the send, so the count describes the sessions this
        // broadcast actually reached rather than ones the send itself started.
        var sessions = _notifier.ConnectedSessionCount;
        try
        {
            _notifier.AnnouncementReceived(notice);
        }
        catch (Exception ex)
        {
            // Unlike a restart's courtesy notice, showing the message IS the
            // task: a notifier that cannot send is this task failing.
            _logger.LogError(ex, "Announcement task {TaskId} failed: the session notice could not be sent.", task.TaskId);
            return Task.FromResult(new AgentTaskResult(
                false,
                $"Announcement failed: the session notice could not be sent ({ex.Message.Trim().TrimEnd('.')}).",
                ResultJson(announcementId, sessions: 0)));
        }

        _logger.LogInformation(
            "Announcement task {TaskId}: announcement {AnnouncementId} sent to {Count} session(s).",
            task.TaskId, announcementId, sessions);

        var outcome = sessions == 0
            ? "Announcement shown to 0 sessions: nobody is signed in on this device right now. "
              + $"A user who signs in within the next {AnnouncementView.LingerMinutes} minutes will still see it."
            : $"Announcement shown to {sessions} session(s).";

        return Task.FromResult(new AgentTaskResult(true, outcome, ResultJson(announcementId, sessions)));
    }

    /// <summary>
    /// Reads the payload the server queued: an announcement id for the audit
    /// trail, and the words to show, held to the wire protocol's bounds. Unknown
    /// extra properties are tolerated -- the payload arrives over the
    /// authenticated task channel, and the strictness that defends the pipe lives
    /// in the protocol -- but the three that matter must be exactly right.
    /// </summary>
    private static bool TryParse(
        string? payloadJson, out Guid announcementId, out string title, out string message, out string refusal)
    {
        announcementId = Guid.Empty;
        title = string.Empty;
        message = string.Empty;

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

            if (!root.TryGetProperty("announcementId", out var idElement) || !idElement.TryGetGuid(out announcementId))
            {
                refusal = "the payload named no announcement id.";
                return false;
            }

            if (!root.TryGetProperty("title", out var titleElement)
                || titleElement.ValueKind != JsonValueKind.String
                || !SessionNoticeProtocol.IsValidTitle(titleElement.GetString()))
            {
                refusal = $"the title must be plain text of 1 to {SessionNoticeProtocol.MaxTitleChars} characters.";
                return false;
            }

            if (!root.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String
                || !SessionNoticeProtocol.IsValidMessage(messageElement.GetString()))
            {
                refusal = $"the message must be plain text of 1 to {SessionNoticeProtocol.MaxMessageChars} characters.";
                return false;
            }

            title = titleElement.GetString()!;
            message = messageElement.GetString()!;
            refusal = string.Empty;
            return true;
        }
    }

    /// <summary>The structured result the console reads: which announcement, and how many sessions saw it sent.</summary>
    private static string ResultJson(Guid announcementId, int sessions) =>
        JsonSerializer.Serialize(new { announcementId, sessions });
}
