namespace EndpointAgent.Core.Chat;

/// <summary>
/// The bounds and the one plain-text rule the live chat shares with the wire
/// protocol, so a value the executor or the window accepts is one the pipe can
/// carry and the reverse can never surprise anyone.
/// </summary>
/// <remarks>
/// Every piece of chat text is drawn as data and never interpreted: no length here
/// admits a command, a path or a markup, because the rule is only "plain text,
/// within length". The message body matches the server's own 2000-character bound,
/// so a line the window sends is one the server will take.
/// </remarks>
public static class ChatText
{
    /// <summary>The longest message body, matching the server's bound.</summary>
    public const int MaxBodyChars = 2000;

    /// <summary>The longest administrator display name the window shows in its header.</summary>
    public const int MaxAdminDisplayChars = 200;

    /// <summary>The longest sender display name a message may carry.</summary>
    public const int MaxSenderDisplayChars = 200;

    /// <summary>A message body must be present, within length, and plain text.</summary>
    public static bool IsValidBody(string? body) => IsPlainText(body, MaxBodyChars);

    /// <summary>An admin display name must be present, within length, and plain text.</summary>
    public static bool IsValidAdminDisplay(string? adminDisplay) => IsPlainText(adminDisplay, MaxAdminDisplayChars);

    /// <summary>A sender display name must be present, within length, and plain text.</summary>
    public static bool IsValidSenderDisplay(string? senderDisplay) => IsPlainText(senderDisplay, MaxSenderDisplayChars);

    private static bool IsPlainText(string? text, int maxChars) =>
        !string.IsNullOrEmpty(text)
        && text.Length <= maxChars
        && !text.Any(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
}
