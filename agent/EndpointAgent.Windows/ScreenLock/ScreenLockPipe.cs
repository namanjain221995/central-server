using System.Globalization;
using System.IO.Pipes;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.Windows.ScreenLock;

/// <summary>
/// The named pipe between the LocalSystem service and the lock overlay running in
/// a signed-in user's session: its name, and what each side may conclude about
/// the other.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated pipe, duplex, like the Company Portal's. The overlay asks it two
/// bounded things and nothing else: <c>show</c> (what to display, and whether the
/// device is still locked) and <c>submit</c> (a candidate unlock code). The
/// verifier is never on it; the service does the verification and answers a single
/// bool. See <see cref="EndpointAgent.Core.ScreenLock.ScreenLockPipeProtocol"/>.
/// </para>
/// <para>
/// Trust runs exactly as it does for the notice and portal pipes. The overlay
/// believes a server only when the pipe driver puts it in session 0, which no
/// signed-in user can reach, so a squatter never learns the code a user types and
/// the overlay never shows a squatter's words. The service believes nothing a
/// client says about itself: the session it serves, and the limits it counts, come
/// from the pipe driver. The session-id calls are shared with the notice pipe
/// rather than declared again.
/// </para>
/// </remarks>
public static class ScreenLockPipe
{
    /// <summary>The pipe's name (without the <c>\\.\pipe\</c> prefix).</summary>
    public const string Name = "EndpointPlatformAgent.ScreenLock";

    /// <summary>
    /// What the handler counts a caller by: the session the pipe driver reports,
    /// which the client cannot choose. An unreadable session is one shared bucket,
    /// so it is limited more tightly, never less.
    /// </summary>
    public static string CallerKeyFor(uint? sessionId) =>
        "session:" + (sessionId is { } session ? session.ToString(CultureInfo.InvariantCulture) : "unknown");

    /// <summary>Whether the server at the other end of a connected client pipe may be believed. The notice pipe's rule.</summary>
    public static SessionNoticePipe.Trust EvaluateServer(NamedPipeClientStream client) =>
        SessionNoticePipe.EvaluateServer(client);
}
