using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Shows an administrator's announcement to every signed-in user, through
/// whatever means the platform provides.
/// </summary>
/// <remarks>
/// The sibling of <see cref="IRestartNotifier"/>, and in production the same
/// pipe server behind both: announcements travel the same one-way,
/// session-0-trusted channel as the restart notice, as a distinct line kind
/// (ADR-0005, announcements amendment). Delivery is best effort per session --
/// a session that is not reading simply misses it -- but unlike a restart
/// notice, showing the message IS the task, so the caller reports honestly how
/// many sessions were connected when it was sent.
/// </remarks>
public interface IAnnouncementNotifier
{
    /// <summary>
    /// The sessions with a notifier connected right now, counted from the
    /// connections themselves -- one notifier per session -- never from session
    /// ids the platform may decline to name. Read at the moment of sending, this
    /// is the N in "shown to N session(s)": zero means nobody is signed in to
    /// see it, not that sending failed.
    /// </summary>
    int ConnectedSessionCount { get; }

    /// <summary>
    /// Broadcasts the announcement to every connected session, keeps it for
    /// replay to sessions that connect while it still lingers, and sees to it
    /// that every signed-in session has a notifier to receive it.
    /// </summary>
    void AnnouncementReceived(AnnouncementNotice notice);
}
