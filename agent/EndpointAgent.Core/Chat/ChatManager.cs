using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Chat;

/// <summary>The one live chat this device may hold: the session it belongs to and the administrator's display name.</summary>
public sealed record ChatSessionState(Guid SessionId, string AdminDisplay);

/// <summary>What starting a chat did: how many signed-in sessions now show the window.</summary>
public sealed record ChatStartOutcome(int SessionCount);

/// <summary>What ending a chat did: whether one was open, and which session it was.</summary>
public sealed record ChatEndOutcome(bool WasOpen, Guid? EndedSessionId);

/// <summary>
/// Shows the chat window in every signed-in session while a chat is open, and
/// takes them all down when it ends, by starting and stopping one fixed image in
/// each interactive session from the LocalSystem service.
/// </summary>
/// <remarks>
/// The service, as LocalSystem, starts the window in each interactive session the
/// way Windows documents for exactly this (see the Windows implementation and
/// ADR-0005). The window is one fixed image with no arguments; it reads the
/// session id, the admin name and the messages from the service over the pipe,
/// never from a command line.
/// </remarks>
public interface ILiveChatWindowLauncher
{
    /// <summary>
    /// Starts the window in every interactive session that does not have one, and
    /// returns every session that now has one (newly started or already running),
    /// so the caller can tell whether anyone is there to see it. Best-effort;
    /// never throws.
    /// </summary>
    IReadOnlyList<uint> EnsureWindowInAllSessions();

    /// <summary>Stops every chat window this device is running. Best-effort; never throws.</summary>
    void StopAllWindows();
}

/// <summary>
/// What the chat pipe asks of the service: the messages to show and whether the
/// chat is still open (poll), the one relay of a typed line to the server (send),
/// and the window's own report that it closed.
/// </summary>
public interface ILiveChatPipeHandler
{
    /// <summary>The messages after <paramref name="afterSeq"/>, the admin's name, and whether the chat is still open.</summary>
    ChatPipeResponse Poll(int afterSeq);

    /// <summary>
    /// Relays a message the person typed to the server over the device credential.
    /// True when the server took it; false when there is no open chat, the body is
    /// not acceptable, or the server refused. The service never shows or acts on the
    /// text -- it only forwards it, and the server's copy returns on the next poll.
    /// </summary>
    Task<bool> RelayEmployeeMessageAsync(string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// The window reported the person closed it. The chat stays OPEN on this
    /// device -- a dismissal, not an end -- and the window is shown again when the
    /// administrator's next message arrives. Only the administrator or the server
    /// ends a chat.
    /// </summary>
    void ReportClosed();
}

/// <summary>
/// Owns the one live chat this device may hold: opens it (launch the window in
/// every session, short-poll the server for new messages), relays the person's
/// replies up, and closes it -- on an <c>EndLiveChat</c> task, when the server says
/// the session has ended, or when the window reports it closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Short-poll, not long-poll, for v1.</b> While a chat is open the manager asks
/// the server for messages after its cursor about once every <see cref="PollInterval"/>;
/// this needs no server change and no proxy tuning. New messages are buffered here
/// and the window pulls them over the pipe at its own cadence; a line the person
/// types is relayed to the server and returns on the next poll as an Employee
/// message, so there is one source of truth and no local echo to de-duplicate.
/// </para>
/// <para>
/// <b>Nothing is persisted.</b> A chat is live, not durable: if the service
/// restarts mid-chat the window is not resurrected and no poll resumes. That is the
/// graceful choice for v1 -- a stale window with no one behind it is worse than
/// none -- and resume is a later phase. So, unlike the screen lock, there is no
/// store and no relock-on-start.
/// </para>
/// </remarks>
public sealed class ChatManager : BackgroundService, ILiveChatPipeHandler
{
    /// <summary>How often the open chat asks the server for new messages. Near-live without a long-poll.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>The server contract's sender name for an administrator's line.</summary>
    private const string AdminSender = "Admin";

    private readonly ILiveChatWindowLauncher _launcher;
    private readonly IAgentApiClient _apiClient;
    private readonly IDeviceCredentialStore _credentialStore;
    private readonly ILogger<ChatManager> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    // All written and read under _gate. The window and the poll loop both touch them.
    private ChatSessionState? _current;
    private bool _active;

    /// <summary>
    /// The person closed the window while the chat stayed open. A dismissal is
    /// respected -- the window is not pushed straight back -- until the
    /// administrator sends another message, which brings it back.
    /// </summary>
    private bool _dismissed;

    private int _serverCursor;
    private readonly List<ChatMessageDto> _messages = [];

    public ChatManager(
        ILiveChatWindowLauncher launcher,
        IAgentApiClient apiClient,
        IDeviceCredentialStore credentialStore,
        ILogger<ChatManager> logger,
        TimeProvider? timeProvider = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The session this device is chatting on right now, or null.</summary>
    public Guid? CurrentSessionId
    {
        get
        {
            lock (_gate)
            {
                return _active ? _current?.SessionId : null;
            }
        }
    }

    /// <summary>
    /// Opens a chat: records the session, shows the window in every signed-in
    /// session, and lets the poll loop start fetching messages. Idempotent per
    /// session; opening a different one replaces the first (the server allows only
    /// one chat per device, so this is a re-delivery, not a conflict).
    /// </summary>
    public Task<ChatStartOutcome> StartAsync(Guid sessionId, string adminDisplay, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_active && _current is { } existing && existing.SessionId == sessionId)
            {
                // The same chat delivered twice. Keep the transcript already gathered,
                // but honour the instruction: if the person had closed the window, a
                // re-delivered start shows it again.
                _dismissed = false;
            }
            else
            {
                _current = new ChatSessionState(sessionId, adminDisplay);
                _active = true;
                _dismissed = false;
                _serverCursor = 0;
                _messages.Clear();
            }
        }

        // Outside the gate: starting processes in user sessions must not hold the
        // lock the window's pipe poll also needs.
        var sessions = _launcher.EnsureWindowInAllSessions();
        _logger.LogInformation(
            "Live chat {SessionId} opened on the device; window shown in {Count} session(s).", sessionId, sessions.Count);
        return Task.FromResult(new ChatStartOutcome(sessions.Count));
    }

    /// <summary>
    /// Closes the chat the device is holding and takes down the window. Idempotent
    /// -- ending when nothing is open succeeds and says so. The active chat is ended
    /// whatever <paramref name="sessionId"/> is: an authenticated end instruction
    /// must never be refused over an id mismatch.
    /// </summary>
    public Task<ChatEndOutcome> EndAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var outcome = EndInternal();
        if (outcome.WasOpen && outcome.EndedSessionId != sessionId)
        {
            _logger.LogWarning(
                "Live chat ended: the open chat {Active} was closed by an end naming {Named}.",
                outcome.EndedSessionId, sessionId);
        }

        return Task.FromResult(outcome);
    }

    /// <inheritdoc />
    public ChatPipeResponse Poll(int afterSeq)
    {
        lock (_gate)
        {
            if (!_active || _current is not { } session)
            {
                return ChatPipeResponse.Polled(active: false, adminDisplay: null, messages: []);
            }

            var page = _messages
                .Where(m => m.Seq > afterSeq)
                .OrderBy(m => m.Seq)
                .Take(LiveChatPipeProtocol.MaxMessagesPerPoll)
                .ToList();

            return ChatPipeResponse.Polled(active: true, adminDisplay: session.AdminDisplay, messages: page);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RelayEmployeeMessageAsync(string body, CancellationToken cancellationToken = default)
    {
        Guid sessionId;
        lock (_gate)
        {
            if (!_active || _current is not { } session)
            {
                return false;
            }

            sessionId = session.SessionId;
        }

        if (!ChatText.IsValidBody(body))
        {
            return false;
        }

        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return false;
        }

        var result = await _apiClient.SendChatMessageAsync(sessionId, body, credential, cancellationToken);

        // Success is the server accepting it. On a Rejected (the session ended
        // server-side) the next poll returns active=false and the window closes;
        // nothing here needs to chase that.
        return result.IsSuccess;
    }

    /// <inheritdoc />
    public void ReportClosed()
    {
        Guid sessionId;
        lock (_gate)
        {
            if (!_active || _current is not { } session)
            {
                return;
            }

            sessionId = session.SessionId;
            _dismissed = true;
        }

        _logger.LogInformation(
            "Live chat {SessionId}: the person closed the window. The chat stays open on this device; "
            + "the window returns when the administrator sends another message.",
            sessionId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _time);
        do
        {
            try
            {
                await PollServerOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed poll must not stop the next: the chat is still open and
                // the next pass is a moment away.
                _logger.LogWarning(ex, "Live-chat poll pass failed; retrying.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One poll pass: fetch new messages for the open chat, buffer them, close on a
    /// server-side end -- and keep the window where it belongs. While the chat is
    /// open and not dismissed, every pass re-ensures the window exists (idempotent),
    /// so a window that died for any reason comes back within a pass. A dismissed
    /// window is respected until a new ADMIN message arrives, which re-shows it.
    /// Internal so tests can drive a pass directly.
    /// </summary>
    internal async Task PollServerOnceAsync(CancellationToken stoppingToken)
    {
        Guid sessionId;
        int cursor;
        lock (_gate)
        {
            if (!_active || _current is not { } session)
            {
                return;
            }

            sessionId = session.SessionId;
            cursor = _serverCursor;
        }

        var credential = await _credentialStore.LoadAsync(stoppingToken);
        if (credential is null)
        {
            return;
        }

        var result = await _apiClient.PollChatMessagesAsync(sessionId, cursor, credential, stoppingToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return;
        }

        var poll = result.Value;
        if (!poll.Active)
        {
            // The chat ended on the server. Close the window and stop polling, but
            // only if we are still on the same session (a new chat may have replaced it).
            var outcome = EndInternal(onlyIfSession: sessionId);
            if (outcome.WasOpen)
            {
                _logger.LogInformation("Live chat {SessionId} ended server-side; window closed.", sessionId);
            }

            return;
        }

        var newAdminMessage = false;
        var showWindow = false;
        var reshown = false;
        lock (_gate)
        {
            if (!_active || _current is not { } session || session.SessionId != sessionId)
            {
                return;
            }

            foreach (var message in poll.Messages.OrderBy(m => m.Seq))
            {
                if (message.Seq > _serverCursor)
                {
                    _messages.Add(message);
                    _serverCursor = message.Seq;
                    if (string.Equals(message.Sender, AdminSender, StringComparison.Ordinal))
                    {
                        newAdminMessage = true;
                    }
                }
            }

            if (_dismissed)
            {
                // The person closed the window; leave them be until the
                // administrator says something new, which brings it back.
                if (newAdminMessage)
                {
                    _dismissed = false;
                    showWindow = true;
                    reshown = true;
                }
            }
            else
            {
                // Self-heal: the window should be on screen for the whole open
                // chat. Ensure is idempotent, so this is a cheap liveness check.
                showWindow = true;
            }
        }

        if (showWindow)
        {
            var sessions = _launcher.EnsureWindowInAllSessions();
            if (reshown)
            {
                _logger.LogInformation(
                    "Live chat {SessionId}: the administrator sent a new message; window re-shown in {Count} session(s).",
                    sessionId, sessions.Count);
            }
        }
    }

    /// <summary>Clears the active chat and takes down the window. The gate is not held while windows are stopped.</summary>
    private ChatEndOutcome EndInternal(Guid? onlyIfSession = null)
    {
        Guid? endedSessionId;
        lock (_gate)
        {
            if (!_active || _current is not { } session)
            {
                return new ChatEndOutcome(WasOpen: false, EndedSessionId: null);
            }

            if (onlyIfSession is { } wanted && session.SessionId != wanted)
            {
                // A newer chat replaced the one this caller meant to end; leave it.
                return new ChatEndOutcome(WasOpen: false, EndedSessionId: null);
            }

            endedSessionId = session.SessionId;
            _current = null;
            _active = false;
            _dismissed = false;
            _serverCursor = 0;
            _messages.Clear();
        }

        _launcher.StopAllWindows();
        return new ChatEndOutcome(WasOpen: true, EndedSessionId: endedSessionId);
    }
}
