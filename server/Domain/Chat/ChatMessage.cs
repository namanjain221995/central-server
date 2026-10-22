using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Chat;

/// <summary>Who wrote a chat line. Stored as text, like every persisted enum.</summary>
public enum ChatMessageSender
{
    /// <summary>An administrator, from the console.</summary>
    Admin = 0,

    /// <summary>The person at the managed device.</summary>
    Employee = 1,

    /// <summary>The platform itself -- a status line the session adds on its own.</summary>
    System = 2,
}

/// <summary>
/// One line in a <see cref="ChatSession"/>: who said it, in what order, and what
/// they said.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plain text, tightly bounded.</b> The body travels to a chat window the agent
/// draws and to the console, so it is validated the same way an announcement is:
/// no control characters other than line breaks and tabs, and never longer than
/// <see cref="MaxBodyLength"/>. It is a message to read, never markup to render.
/// </para>
/// <para>
/// <b>Ordered by <see cref="Seq"/>, not by time.</b> The sequence is monotonic per
/// session starting at 1, assigned by the service as each line is appended, so
/// both sides can ask "everything after N" and page the conversation without
/// depending on clocks that may disagree.
/// </para>
/// </remarks>
public sealed class ChatMessage : AuditableEntity
{
    public const int MaxBodyLength = 2000;

    public const int MaxSenderDisplayLength = 320;

    private ChatMessage()
    {
        SenderDisplay = null!;
        Body = null!;
    }

    public ChatMessage(
        Guid chatSessionId,
        int seq,
        ChatMessageSender sender,
        string senderDisplay,
        string body,
        DateTimeOffset sentAt)
    {
        ChatSessionId = Guard.NotEmpty(chatSessionId);
        Seq = seq > 0
            ? seq
            : throw new ArgumentOutOfRangeException(nameof(seq), "A message sequence starts at 1 and only increases.");
        Sender = sender;
        SenderDisplay = Guard.NotNullOrWhiteSpace(senderDisplay, nameof(senderDisplay), MaxSenderDisplayLength);
        Body = PlainText(Guard.NotNullOrWhiteSpace(body, nameof(body), MaxBodyLength), nameof(body));
        SentAt = sentAt;
    }

    public Guid ChatSessionId { get; private set; }

    /// <summary>Monotonic per session, starting at 1. Assigned by the service as lines are appended.</summary>
    public int Seq { get; private set; }

    public ChatMessageSender Sender { get; private set; }

    public string SenderDisplay { get; private set; }

    public string Body { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    /// <summary>
    /// Whether text may travel as a chat message body: no control characters other
    /// than newlines and tabs.
    /// </summary>
    /// <remarks>
    /// The same plain-text rule an announcement's title and message are held to
    /// (<c>Announcement.IsPlainText</c>) and the screen-lock message is held to:
    /// the body is displayed verbatim and never interpreted, so a control character
    /// has no business in it.
    /// </remarks>
    public static bool IsPlainText(string? text) =>
        text is not null && !text.Any(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    private static string PlainText(string value, string paramName) =>
        IsPlainText(value)
            ? value
            : throw new ArgumentException(
                "A chat message is plain text: control characters other than line breaks and tabs are not allowed.",
                paramName);
}
