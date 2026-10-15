namespace EndpointAgent.Core.SessionNotice;

/// <summary>
/// An administrator's message to whoever is signed in: the server's id for it, a
/// title, a message, and the moment it was shown. The one notice that carries
/// text.
/// </summary>
/// <remarks>
/// <para>
/// The title and message are DATA from an authenticated administrator, delivered
/// through the server's permission-checked, audited task channel and bounded by
/// <see cref="SessionNoticeProtocol"/>. They are rendered as plain text and
/// nothing else -- never parsed, never a link, never a path or command (ADR-0005,
/// announcements amendment). Everything that frames them on screen stays a
/// constant in <see cref="AnnouncementView"/>, so the window still says, in fixed
/// words, where the text came from.
/// </para>
/// <para>
/// <see cref="Id"/> is the server's announcement id, and it is what dismissal
/// keys on: the server may deliver the same announcement more than once -- a
/// re-queued task, the replay to a reconnecting notifier -- and each delivery is
/// stamped with a fresh <see cref="At"/>, so only the id says "this is the same
/// message". A user who closed it must not have it pop back up under a new
/// timestamp.
/// </para>
/// <para>
/// <see cref="At"/> is when the service sent it, stamped with the service's
/// clock like a cancellation. It bounds how long the message is shown and how
/// long it is worth replaying to a session that connects later.
/// </para>
/// </remarks>
public sealed record AnnouncementNotice(Guid Id, string Title, string Message, DateTimeOffset At);

/// <summary>
/// What the window shows for an announcement at a given moment: the
/// administrator's words inside fixed chrome, or nothing. Pure, so every state
/// is testable without a window.
/// </summary>
/// <param name="Visible">Whether there is anything to show.</param>
/// <param name="Title">The administrator's title, or empty while hidden.</param>
/// <param name="Message">The administrator's message, or empty while hidden.</param>
public sealed record AnnouncementView(bool Visible, string Title, string Message)
{
    /// <summary>Fixed. The window's title bar; never the administrator's words.</summary>
    public const string WindowTitle = "Announcement";

    /// <summary>Fixed. Says where the text below it came from, and nothing else.</summary>
    public const string Headline = "Message from your IT administrator";

    /// <summary>Fixed. Unlike a restart notice, an announcement may be closed for good.</summary>
    public const string Footer = "You can close this message.";

    /// <summary>
    /// How long an announcement stays on screen, and stays worth replaying to a
    /// session that signs in after it was sent. Long enough to be seen by someone
    /// away from their desk; short enough that stale news takes itself down.
    /// </summary>
    public const int LingerMinutes = 30;

    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(LingerMinutes);

    private static readonly AnnouncementView Hidden = new(false, string.Empty, string.Empty);

    /// <summary>
    /// The view for a notice at <paramref name="now"/>. A notice whose
    /// <see cref="AnnouncementNotice.Id"/> is in <paramref name="dismissedIds"/>
    /// stays hidden: closing a message dismisses that announcement for good,
    /// however often it is delivered again -- a replay, a re-queued task -- and
    /// whatever fresh timestamp each delivery carries. Only a different id is a
    /// new message.
    /// </summary>
    public static AnnouncementView For(AnnouncementNotice? notice, DateTimeOffset now, IReadOnlySet<Guid>? dismissedIds = null)
    {
        if (notice is null || dismissedIds?.Contains(notice.Id) is true)
        {
            return Hidden;
        }

        // Stamped in the future is a clock problem, not something to show early;
        // past the linger it is old news, like a cancellation long past.
        var since = now - notice.At;
        return since >= TimeSpan.Zero && since < Linger
            ? new AnnouncementView(true, notice.Title, notice.Message)
            : Hidden;
    }
}
