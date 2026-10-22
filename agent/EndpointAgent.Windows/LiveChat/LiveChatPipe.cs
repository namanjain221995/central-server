using System.Globalization;
using System.IO.Pipes;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.Windows.LiveChat;

/// <summary>
/// The named pipe between the LocalSystem service and the live-chat window running
/// in a signed-in user's session: its name, and what each side may conclude about
/// the other.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated duplex pipe, like the Company Portal's and the screen lock's. The
/// window asks it three bounded things and nothing else: <c>poll</c> (the messages
/// to show and whether the chat is still open), <c>send</c> (a line the person
/// typed) and <c>closed</c> (the window is closing). The service relays a sent line
/// to the server over the device credential and never acts on it; everything shown
/// is plain text the server already recorded. See
/// <see cref="EndpointAgent.Core.Chat.LiveChatPipeProtocol"/>.
/// </para>
/// <para>
/// Trust runs exactly as it does for the notice, portal and screen-lock pipes. The
/// window believes a server only when the pipe driver puts it in session 0, which
/// no signed-in user can reach, so a squatter never learns what the person types
/// and the window never shows a squatter's words. The service believes nothing a
/// client says about itself: the session it serves, and the limits it counts, come
/// from the pipe driver. The session-id calls are shared with the notice pipe
/// rather than declared again.
/// </para>
/// </remarks>
public static class LiveChatPipe
{
    /// <summary>The pipe's name (without the <c>\\.\pipe\</c> prefix).</summary>
    public const string Name = "EndpointPlatformAgent.LiveChat";

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
