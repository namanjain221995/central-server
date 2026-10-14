using System.Globalization;
using System.IO.Pipes;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.Windows.CompanyPortal;

/// <summary>
/// The named pipe between the LocalSystem service and the Company Portal running
/// in a signed-in user's session: its name, and what each side may conclude
/// about the other.
/// </summary>
/// <remarks>
/// <para>
/// A second pipe rather than a second message on the session-notice pipe,
/// because the two are different in the one way that matters: the notice pipe
/// is one-way and carries no request, and <see cref="SessionNoticePipe"/> and
/// ADR-0005 lean on that. This pipe is the one place a user's process sends
/// anything to the service, and it is kept to one line per connection and three
/// operations (<see cref="EndpointAgent.Core.CompanyPortal.CompanyPortalProtocol"/>).
/// </para>
/// <para>
/// Trust runs the same way in both directions as for the notice pipe. The portal
/// believes a server only when the pipe driver puts it in session 0, which no
/// signed-in user can reach. The service believes nothing a client says about
/// itself: the session it serves is the one the pipe driver reports, and the
/// user's name is looked up from that session. The session-id calls are shared
/// with the notice pipe rather than declared twice.
/// </para>
/// </remarks>
public static class CompanyPortalPipe
{
    /// <summary>The pipe's name (without the <c>\.\pipe\</c> prefix).</summary>
    public const string Name = "EndpointPlatformAgent.CompanyPortal";

    /// <summary>
    /// What the handler counts a caller by: the session the pipe driver reports,
    /// which the client cannot choose. A session that could not be read is one
    /// shared bucket, so an unreadable client is limited more tightly, not less.
    /// </summary>
    public static string CallerKeyFor(uint? sessionId) =>
        "session:" + (sessionId is { } session ? session.ToString(CultureInfo.InvariantCulture) : "unknown");

    /// <summary>
    /// Whether the server at the other end of a connected client pipe may be
    /// believed. The notice pipe's rule, for the notice pipe's reasons.
    /// </summary>
    public static SessionNoticePipe.Trust EvaluateServer(NamedPipeClientStream client) =>
        SessionNoticePipe.EvaluateServer(client);
}
