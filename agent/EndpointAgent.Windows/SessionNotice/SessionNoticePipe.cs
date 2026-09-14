using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EndpointAgent.Windows.SessionNotice;

/// <summary>
/// The named pipe between the LocalSystem service and the notifier running in
/// each signed-in user's session, and how the notifier decides to trust it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Direction.</b> The service is the only server and the only writer. A
/// notifier connects read-only and never sends a byte, so there is no request
/// the service could be tricked into acting on: the pipe cannot become a way to
/// ask a SYSTEM process to do anything.
/// </para>
/// <para>
/// <b>Why trust is the server's session, and nothing else.</b> A user who wanted
/// to forge an "IT has scheduled a restart" notice would create a pipe of this
/// name and wait for the notifier to connect. The notifier runs as that same
/// ordinary user, so it cannot open the server process to inspect it: measured
/// on Windows 11 26200 from a non-elevated session, <c>OpenProcess</c> with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is denied (error 5) on every SYSTEM
/// process tried, and so is <c>ProcessIdToSessionId</c>. Checking the server's
/// account or image path is therefore not possible from where the check runs,
/// and a check that cannot be made is not a check.
/// </para>
/// <para>
/// What <em>is</em> available is <c>GetNamedPipeServerSessionId</c>, which asks
/// the pipe driver rather than the process. Measured the same way it returns 0
/// for the real <c>lsass</c>, <c>services.exe</c>, <c>eventlog</c> and
/// <c>InitShutdown</c> pipes, and the caller's own session for a pipe the caller
/// created. Since Vista, session 0 holds services only; no interactive user can
/// start a process there. So a server in session 0 is a service -- and a pipe
/// created by anyone signed in is refused, whatever it is called.
/// </para>
/// <para>
/// What that does not stop: another service, which only an administrator can
/// install, squatting the name. An administrator can already do anything this
/// notice could, so that is outside what the notice defends. The service also
/// creates the pipe with <see cref="PipeOptions.FirstPipeInstance"/>, so while it
/// is running nobody else can own the name.
/// </para>
/// </remarks>
public static class SessionNoticePipe
{
    /// <summary>The pipe's name (without the <c>\\.\pipe\</c> prefix).</summary>
    public const string Name = "EndpointPlatformAgent.SessionNotice";

    /// <summary>The session every Windows service runs in, and no interactive user can.</summary>
    public const uint ServiceSessionId = 0;

    /// <summary>Why a pipe server was, or was not, trusted.</summary>
    public enum Trust
    {
        /// <summary>The server is in session 0: a service.</summary>
        Trusted,

        /// <summary>The server's session could not be determined. Refused: unknown is not trusted.</summary>
        SessionUnknown,

        /// <summary>The server is in an interactive session -- a signed-in user's process, never the agent.</summary>
        InteractiveSession,
    }

    /// <summary>The pure rule, separated from the Windows call so every branch can be tested.</summary>
    public static Trust Evaluate(uint? serverSessionId) => serverSessionId switch
    {
        null => Trust.SessionUnknown,
        ServiceSessionId => Trust.Trusted,
        _ => Trust.InteractiveSession,
    };

    /// <summary>Whether the server at the other end of a connected client pipe may be believed.</summary>
    public static Trust EvaluateServer(NamedPipeClientStream client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Evaluate(ServerSessionId(client.SafePipeHandle));
    }

    /// <summary>The session of the process serving a pipe, or null when Windows will not say.</summary>
    public static uint? ServerSessionId(SafePipeHandle pipe) =>
        NativeMethods.GetNamedPipeServerSessionId(pipe, out var session) ? session : null;

    /// <summary>
    /// The session of the client connected to a server instance, or null when
    /// Windows will not say. Bookkeeping for the server -- which session already
    /// has a notifier -- never a trust decision: the server sends the same fixed
    /// notice to every reader regardless.
    /// </summary>
    public static uint? ClientSessionId(SafePipeHandle pipe) =>
        NativeMethods.GetNamedPipeClientSessionId(pipe, out var session) ? session : null;

    /// <summary>The process connected to a server instance, or null when Windows will not say.</summary>
    public static int? ClientProcessId(SafePipeHandle pipe) =>
        NativeMethods.GetNamedPipeClientProcessId(pipe, out var processId) ? (int)processId : null;

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    }
}
