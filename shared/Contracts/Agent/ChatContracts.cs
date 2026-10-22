namespace EndpointPlatform.Contracts.Agent;

/// <summary>One chat message as the device sees it.</summary>
/// <param name="Seq">Monotonic per session, starting at 1; the agent's paging cursor.</param>
/// <param name="Sender"><c>Admin</c>, <c>Employee</c> or <c>System</c>, as a string.</param>
/// <param name="SenderDisplay">Who wrote it, for display.</param>
/// <param name="Body">The message text, plain and already length-bounded by the server.</param>
/// <param name="SentAt">When the server recorded it.</param>
public sealed record ChatMessageDto(
    int Seq, string Sender, string SenderDisplay, string Body, DateTimeOffset SentAt);

/// <summary>
/// Response body for <c>GET /agent/v1/chat/{sessionId}/messages</c>: whether the
/// session is still open and the messages after the agent's cursor.
/// </summary>
/// <remarks>
/// A simple immediate return of what exists now. A real long-poll that holds the
/// request open until a new message arrives is Phase 2; the device polls on its
/// heartbeat cadence until then.
/// </remarks>
/// <param name="Active">
/// False once the chat has ended, so the agent can close its window without needing
/// a separate signal.
/// </param>
public sealed record ChatPollResponse(bool Active, IReadOnlyList<ChatMessageDto> Messages);

/// <summary>
/// Request body for <c>POST /agent/v1/chat/{sessionId}/messages</c>: one message the
/// person at the device sends. Plain text, bounded and validated server-side.
/// </summary>
public sealed record ChatSendRequest(string Body);
