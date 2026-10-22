using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Chat;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Chat;

public enum ChatStartOutcome
{
    Started = 0,

    /// <summary>The device already has an active chat session.</summary>
    Duplicate = 1,

    /// <summary>No such device in the caller's organization, or it is retired.</summary>
    DeviceNotFound = 2,

    /// <summary>The device's agent is too old to carry the live-chat executor.</summary>
    AgentTooOld = 3,
}

public sealed record ChatStartResult(ChatStartOutcome Outcome, Guid? SessionId, string? Status);

public enum ChatSendOutcome
{
    Sent = 0,
    SessionNotFound = 1,
    SessionEnded = 2,
    InvalidBody = 3,
}

public sealed record ChatSendResult(ChatSendOutcome Outcome, int Seq, DateTimeOffset SentAt);

public enum ChatEndOutcome
{
    Ended = 0,
    NotFound = 1,
}

public sealed record ChatEndResult(ChatEndOutcome Outcome, string? Status);

/// <summary>One message as either side sees it. The sender is a string, never an ordinal.</summary>
public sealed record ChatMessageView(int Seq, string Sender, string SenderDisplay, string Body, DateTimeOffset SentAt);

/// <summary>An admin read of a session: where it stands and the messages after a cursor.</summary>
public sealed record ChatMessagesResult(string Status, bool Ended, IReadOnlyList<ChatMessageView> Messages);

/// <summary>A device's read of its own session: whether it is still open and the messages after a cursor.</summary>
public sealed record ChatAgentMessagesResult(bool Active, IReadOnlyList<ChatMessageView> Messages);

/// <summary>
/// One device as the console's chat picker shows it: every active device, carrying
/// the id of its open chat session when it has one, null when it does not.
/// </summary>
public sealed record ChatDeviceRow(
    Guid DeviceId, string Hostname, string? DisplayName, bool IsOnline,
    DateTimeOffset? LastSeenAt, string? LoggedOnUser, Guid? ActiveSessionId);

/// <summary>
/// Opens, drives and closes live IT-support chats, and is the single audited choke
/// point for them -- the way <see cref="Tasks.DeviceTaskService"/> is for tasks and
/// <see cref="RemoteSupport.RemoteSupportService"/> is for remote support.
/// </summary>
/// <remarks>
/// <para>
/// <b>One active session per device, enforced by the database.</b> Starting a chat
/// inserts a session row guarded by a partial unique index
/// (<c>ux_chat_sessions_active_per_device</c>); a second start for a device that
/// already has one loses that insert and comes back as
/// <see cref="ChatStartOutcome.Duplicate"/>, exactly the way a second in-flight
/// restart does. The window itself is opened by a <c>StartLiveChat</c> task queued
/// through the task service, which applies the minimum-agent gate and writes the
/// task's own audit record.
/// </para>
/// <para>
/// <b>Starting and ending are audited; individual messages are not.</b> A chat is a
/// stream of short lines, and one audit row per line would bury the events that
/// matter -- who opened a chat with which machine, and who closed it -- under
/// chatter. The messages live in <c>chat_messages</c>, readable through this
/// service; the audit trail records the session's boundaries.
/// </para>
/// <para>
/// <b>Scope is enforced here, not only at the endpoint.</b> Every session-keyed read
/// and write is filtered to the caller's organization and, for a scoped
/// administrator, to a device they may act on -- so a session id that is not theirs
/// to see answers "not found" rather than revealing that it exists.
/// </para>
/// </remarks>
public sealed class ChatService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions,
    ILogger<ChatService> logger)
{
    /// <summary>Most messages returned in one read, so a long chat cannot be pulled in a single huge payload.</summary>
    public const int MaxMessagesPerFetch = 500;

    /// <summary>
    /// What the EMPLOYEE's window shows as the administrator's name -- the window
    /// header and every admin message bubble. A generic label, deliberately: the
    /// operator's sign-in email is an internal identifier and has no business on an
    /// employee's screen. The real actor is still recorded on the session row, in
    /// the audit trail, and shown in the admin console.
    /// </summary>
    public const string EmployeeFacingAdminDisplay = "IT Support";

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);
    private readonly ILogger<ChatService> _logger = logger;

    // --------------------------------------------------------------- start

    /// <summary>
    /// Starts a chat with a device: records the session, enforces one-active-per-device,
    /// and queues the task that opens the window. One transaction under the execution
    /// strategy so a mid-way failure never leaves a start task with no session behind it.
    /// </summary>
    public Task<ChatStartResult> StartAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() =>
            StartInTransactionAsync(organizationId, deviceId, actorId, actorDisplay, cancellationToken));
    }

    private async Task<ChatStartResult> StartInTransactionAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay, CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var device = await _dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == organizationId && d.Status == DeviceStatus.Active,
                cancellationToken);

        if (device is null)
        {
            return new ChatStartResult(ChatStartOutcome.DeviceNotFound, null, null);
        }

        var session = new ChatSession(organizationId, deviceId, actorId, actorDisplay, _timeProvider.GetUtcNow());
        _dbContext.ChatSessions.Add(session);

        try
        {
            // Saved first so the partial unique index fires here: a device that
            // already has an active chat loses this insert, and we never queue a
            // window-open task for a conversation that will not exist.
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DeviceTaskService.IsDuplicateActiveTask(ex))
        {
            // The transaction is aborted; disposing it rolls back. Nothing else runs.
            return new ChatStartResult(ChatStartOutcome.Duplicate, null, null);
        }

        // The task service applies the minimum-agent gate and writes the task's own
        // audit record; nothing is tightened here. The device is active, so the only
        // refusal it can give is the agent-version floor. The payload deliberately
        // carries the generic employee-facing label, never the operator's email:
        // this string is what the window header shows on the employee's screen.
        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.StartLiveChat,
            new TaskPayloads.StartLiveChat(session.Id, EmployeeFacingAdminDisplay),
            actorId, actorDisplay, cancellationToken);

        if (task is null)
        {
            // Not committed: disposing the transaction rolls the session back too.
            return new ChatStartResult(ChatStartOutcome.AgentTooOld, null, null);
        }

        _auditWriter.Stage(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "chat.start", AuditResult.Success,
            a => a.OnDevice(device.Id, device.Hostname)
                  .OnTarget("chat_session", session.Id.ToString(), device.Hostname)
                  .Requiring(Permissions.Chat.Start)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      sessionId = session.Id,
                      deviceId,
                      taskId = task.Id,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Chat session {SessionId} started with device {DeviceId} by {Actor} (task {TaskId}).",
            session.Id, deviceId, actorDisplay, task.Id);

        return new ChatStartResult(ChatStartOutcome.Started, session.Id, session.Status.ToString());
    }

    // --------------------------------------------------------------- admin send

    /// <summary>
    /// Appends an administrator's message to a session and advances its activity
    /// clock. Not audited per message. Refuses an ended session and an invalid body.
    /// </summary>
    public async Task<ChatSendResult> SendAdminMessageAsync(
        Guid organizationId, Guid sessionId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        string actorDisplay, string body, CancellationToken cancellationToken = default)
    {
        if (!IsAcceptableBody(body))
        {
            return new ChatSendResult(ChatSendOutcome.InvalidBody, 0, default);
        }

        var session = await LoadScopedSessionAsync(organizationId, sessionId, scopedDeviceIds, cancellationToken);
        if (session is null)
        {
            return new ChatSendResult(ChatSendOutcome.SessionNotFound, 0, default);
        }

        if (session.Status == ChatSessionStatus.Ended)
        {
            return new ChatSendResult(ChatSendOutcome.SessionEnded, 0, default);
        }

        var now = _timeProvider.GetUtcNow();
        var seq = await NextSeqAsync(sessionId, cancellationToken);

        _dbContext.ChatMessages.Add(new ChatMessage(
            sessionId, seq, ChatMessageSender.Admin, actorDisplay, body, now));
        session.RecordActivity(now);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new ChatSendResult(ChatSendOutcome.Sent, seq, now);
    }

    // --------------------------------------------------------------- admin read

    /// <summary>
    /// A session's status and the messages after <paramref name="afterSeq"/>, for the
    /// console. Null when the session is not the caller's to see.
    /// </summary>
    public async Task<ChatMessagesResult?> GetMessagesAsync(
        Guid organizationId, Guid sessionId, int afterSeq, IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadScopedSessionAsync(organizationId, sessionId, scopedDeviceIds, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var messages = await FetchMessagesAsync(sessionId, afterSeq, cancellationToken);
        return new ChatMessagesResult(
            session.Status.ToString(), session.Status == ChatSessionStatus.Ended, messages);
    }

    /// <summary>
    /// Every active device in the caller's scope -- the console's chat device
    /// picker -- each carrying the id of its open chat session when it has one,
    /// null when it does not. No message bodies.
    /// </summary>
    /// <remarks>
    /// Deliberately every device, not only those already in a chat: this list is
    /// what an administrator picks a device FROM to start a conversation, so a list
    /// restricted to in-flight chats would make the feature unreachable -- with no
    /// chat open there would be nothing to click to open one.
    /// </remarks>
    public async Task<IReadOnlyList<ChatDeviceRow>> ListActiveDevicesAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from d in _dbContext.Devices.AsNoTracking()
            join s in _dbContext.ChatSessions.AsNoTracking()
                    .Where(s => s.Status == ChatSessionStatus.Active)
                on d.Id equals s.DeviceId into sessions
            from s in sessions.DefaultIfEmpty()
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            orderby d.Hostname
            select new
            {
                DeviceId = d.Id,
                d.Hostname,
                d.DisplayName,
                d.LastSeenAt,
                d.LoggedOnUser,
                ActiveSessionId = (Guid?)s!.Id,
            })
            .ToListAsync(cancellationToken);

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            rows = rows.Where(r => visible.Contains(r.DeviceId)).ToList();
        }

        var now = _timeProvider.GetUtcNow();
        return rows.Select(r =>
        {
            var online = r.LastSeenAt is { } lastSeen && now - lastSeen <= _staleAfter;
            return new ChatDeviceRow(
                r.DeviceId, r.Hostname, r.DisplayName, online, r.LastSeenAt, r.LoggedOnUser, r.ActiveSessionId);
        }).ToList();
    }

    // --------------------------------------------------------------- end

    /// <summary>
    /// Ends a session (reason <see cref="ChatEndedReason.AdminEnded"/>) and queues the
    /// task that closes the window. Idempotent: ending an already-ended session is a
    /// success that queues nothing. Null when the session is not the caller's to see.
    /// </summary>
    public Task<ChatEndResult> EndAsync(
        Guid organizationId, Guid sessionId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() =>
            EndInTransactionAsync(organizationId, sessionId, scopedDeviceIds, actorId, actorDisplay, cancellationToken));
    }

    private async Task<ChatEndResult> EndInTransactionAsync(
        Guid organizationId, Guid sessionId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var session = await LoadScopedSessionAsync(organizationId, sessionId, scopedDeviceIds, cancellationToken);
        if (session is null)
        {
            return new ChatEndResult(ChatEndOutcome.NotFound, null);
        }

        var now = _timeProvider.GetUtcNow();
        var wasActive = session.End(ChatEndedReason.AdminEnded, now);

        if (wasActive)
        {
            // Best-effort: a device too old or offline never gets the close task, but
            // the session is Ended regardless, so the device sees no active chat the
            // next time it polls. Ending the conversation server-side is the admin's
            // decision and must not depend on the endpoint being reachable.
            var task = await _taskService.QueueAsync(
                organizationId, session.DeviceId, DeviceTaskType.EndLiveChat,
                new TaskPayloads.EndLiveChat(session.Id),
                actorId, actorDisplay, cancellationToken);

            _auditWriter.Stage(
                organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
                action: "chat.end", AuditResult.Success,
                a => a.OnDevice(session.DeviceId, session.DeviceId.ToString())
                      .OnTarget("chat_session", session.Id.ToString(), session.DeviceId.ToString())
                      .Requiring(Permissions.Chat.Start)
                      .WithStateChange(
                          JsonSerializer.Serialize(new { status = nameof(ChatSessionStatus.Active) }),
                          JsonSerializer.Serialize(new
                          {
                              status = nameof(ChatSessionStatus.Ended),
                              reason = nameof(ChatEndedReason.AdminEnded),
                              taskId = task?.Id,
                          })));

            _logger.LogInformation(
                "Chat session {SessionId} ended by {Actor} (close task {TaskId}).",
                session.Id, actorDisplay, task?.Id);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ChatEndResult(ChatEndOutcome.Ended, session.Status.ToString());
    }

    // --------------------------------------------------------------- agent side

    /// <summary>
    /// Appends an employee's message, scoped so a device may only write to its own
    /// session. Refuses an ended session and an invalid body.
    /// </summary>
    public async Task<ChatSendResult> AppendEmployeeMessageAsync(
        Guid deviceId, Guid sessionId, string body, CancellationToken cancellationToken = default)
    {
        if (!IsAcceptableBody(body))
        {
            return new ChatSendResult(ChatSendOutcome.InvalidBody, 0, default);
        }

        var session = await _dbContext.ChatSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.DeviceId == deviceId, cancellationToken);

        if (session is null)
        {
            return new ChatSendResult(ChatSendOutcome.SessionNotFound, 0, default);
        }

        if (session.Status == ChatSessionStatus.Ended)
        {
            return new ChatSendResult(ChatSendOutcome.SessionEnded, 0, default);
        }

        // Whoever is signed in at the machine, so the console sees a name rather than
        // a bare "employee"; the device never sends one, and it is not an authenticator.
        var loggedOnUser = await _dbContext.Devices
            .Where(d => d.Id == deviceId)
            .Select(d => d.LoggedOnUser)
            .SingleOrDefaultAsync(cancellationToken);
        var senderDisplay = string.IsNullOrWhiteSpace(loggedOnUser) ? "Employee" : loggedOnUser!;

        var now = _timeProvider.GetUtcNow();
        var seq = await NextSeqAsync(sessionId, cancellationToken);

        _dbContext.ChatMessages.Add(new ChatMessage(
            sessionId, seq, ChatMessageSender.Employee, senderDisplay, body, now));
        session.RecordActivity(now);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new ChatSendResult(ChatSendOutcome.Sent, seq, now);
    }

    /// <summary>
    /// A device's own session: whether it is still open and the messages after a
    /// cursor. Scoped to the device, so a stolen credential cannot read another
    /// machine's chat. Null when the session is not this device's.
    /// </summary>
    public async Task<ChatAgentMessagesResult?> GetMessagesForDeviceAsync(
        Guid deviceId, Guid sessionId, int afterSeq, CancellationToken cancellationToken = default)
    {
        var status = await _dbContext.ChatSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.DeviceId == deviceId)
            .Select(s => (ChatSessionStatus?)s.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (status is null)
        {
            return null;
        }

        // The employee's window never sees the operator's email: every admin line is
        // re-labelled with the generic support name before it leaves for the device.
        // The admin console's own read (GetMessagesAsync) keeps the real names.
        var messages = (await FetchMessagesAsync(sessionId, afterSeq, cancellationToken))
            .Select(m => m.Sender == nameof(ChatMessageSender.Admin)
                ? m with { SenderDisplay = EmployeeFacingAdminDisplay }
                : m)
            .ToList();

        return new ChatAgentMessagesResult(status == ChatSessionStatus.Active, messages);
    }

    // --------------------------------------------------------------- helpers

    private async Task<ChatSession?> LoadScopedSessionAsync(
        Guid organizationId, Guid sessionId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken)
    {
        var session = await _dbContext.ChatSessions
            .SingleOrDefaultAsync(
                s => s.Id == sessionId && s.OrganizationId == organizationId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        // A scoped administrator sees only sessions on devices they may act on; an
        // out-of-scope session is indistinguishable from one that does not exist.
        if (scopedDeviceIds is not null && !scopedDeviceIds.Contains(session.DeviceId))
        {
            return null;
        }

        return session;
    }

    private async Task<IReadOnlyList<ChatMessageView>> FetchMessagesAsync(
        Guid sessionId, int afterSeq, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == sessionId && m.Seq > afterSeq)
            .OrderBy(m => m.Seq)
            .Take(MaxMessagesPerFetch)
            .Select(m => new { m.Seq, m.Sender, m.SenderDisplay, m.Body, m.SentAt })
            .ToListAsync(cancellationToken);

        return rows
            .Select(m => new ChatMessageView(m.Seq, m.Sender.ToString(), m.SenderDisplay, m.Body, m.SentAt))
            .ToList();
    }

    private async Task<int> NextSeqAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var last = await _dbContext.ChatMessages
            .Where(m => m.ChatSessionId == sessionId)
            .MaxAsync(m => (int?)m.Seq, cancellationToken) ?? 0;

        return last + 1;
    }

    /// <summary>
    /// Whether a message body may be stored: non-empty, within the length limit and
    /// plain text, the same rule the <see cref="ChatMessage"/> constructor enforces as
    /// a backstop. Checked here so a bad body is a clean outcome rather than a thrown
    /// construction.
    /// </summary>
    private static bool IsAcceptableBody(string? body) =>
        !string.IsNullOrWhiteSpace(body)
        && body.Trim().Length <= ChatMessage.MaxBodyLength
        && ChatMessage.IsPlainText(body);
}
