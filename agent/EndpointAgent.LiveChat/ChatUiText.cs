namespace EndpointAgent.LiveChat;

/// <summary>
/// Every fixed word the chat window shows, in one place.
/// </summary>
/// <remarks>
/// Nothing from the wire is displayed except the administrator's display name and
/// the message bodies, and those only as the text of a label that treats them as
/// text. Every title, placeholder, button and status is one of these constants,
/// chosen by a code path, never by a string that arrived over the pipe.
/// </remarks>
internal static class ChatUiText
{
    public const string Title = "IT Support";
    public const string Subtitle = "Live chat with your IT administrator";

    /// <summary>Shown in the header until the service reports who the administrator is.</summary>
    public const string UnknownAdmin = "IT Support";

    public const string InputPlaceholder = "Type a message…";
    public const string Send = "Send";

    public const string Connecting = "Connecting…";
    public const string Connected = "Connected.";
    public const string Ended = "This chat has ended.";
    public const string Disconnected = "The IT support agent is no longer available.";

    /// <summary>The sender values the server uses, matched to choose a bubble style. Never shown.</summary>
    public const string SenderAdmin = "Admin";
    public const string SenderEmployee = "Employee";
    public const string SenderSystem = "System";
}
