using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EndpointAgent.Windows.SessionNotice;

/// <summary>
/// Starts the session notifier in a signed-in user's session, from the
/// LocalSystem service, the way Windows documents for exactly this:
/// <c>WTSQueryUserToken</c> for the session user's own token, then
/// <c>CreateProcessAsUserW</c> on the interactive desktop.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one place in the agent that creates a process</b>, and
/// <c>AgentSafetyTests</c> pins it to this file and to how it is used here. What
/// ADR-0005 excludes is composing <em>what to run</em> from data. Nothing here is
/// composed: the image is the fixed path the caller resolved inside the service's
/// own directory, it is passed as <c>lpApplicationName</c> so Windows never parses
/// a path out of a command line, the command line is that same path quoted and
/// nothing else, and there is no parameter through which an argument could ever be
/// supplied. No request from the pipe, no file and no registry value reaches this
/// call.
/// </para>
/// <para>
/// <b>As the user, and never more.</b> The token is the session user's own primary
/// token. If it is the elevated half of a UAC pair -- an administrator's session --
/// the limited half is used instead (<see cref="SessionUserToken.WithoutElevation"/>),
/// so the notifier runs with exactly what the user has at their desktop. The
/// environment is the user's own, the working directory is the install folder, no
/// handle is inherited across the boundary, and the process gets the user's default
/// security descriptor. The service closes its handles to the process at once; it
/// never waits on it, signals it or reads from it. The only thing anyone who
/// controlled every input to this call could achieve is starting, as themselves, a
/// program they can already start from their Start menu.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSessionProcessHost : ISessionProcessHost
{
    private const string InteractiveDesktop = @"winsta0\default";

    private readonly Func<uint, SafeAccessTokenHandle?> _openSessionToken;

    public WindowsSessionProcessHost()
        : this(SessionUserToken.Open)
    {
    }

    /// <summary>
    /// For tests, which cannot hold the privilege <c>WTSQueryUserToken</c> needs: a
    /// token for the test's own session, so the launch itself runs for real.
    /// </summary>
    internal WindowsSessionProcessHost(Func<uint, SafeAccessTokenHandle?> openSessionToken)
    {
        _openSessionToken = openSessionToken;
    }

    public IReadOnlyList<uint> InteractiveSessions()
    {
        if (!NativeMethods.WTSEnumerateSessionsW(NativeMethods.WTS_CURRENT_SERVER_HANDLE, 0, 1, out var buffer, out var count))
        {
            throw new Win32Exception();
        }

        try
        {
            var sessions = new List<uint>();
            var size = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFOW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFOW>(buffer + i * size);
                if (info.SessionId != SessionNoticePipe.ServiceSessionId
                    && info.State is NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive or NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                {
                    sessions.Add(info.SessionId);
                }
            }

            return sessions;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    public int? StartInSession(uint sessionId, string imagePath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var token = _openSessionToken(sessionId);
        if (token is null)
        {
            return null;
        }

        // The user's own environment. Without this the child would inherit the
        // service's -- SYSTEM's profile paths -- which is wrong even when harmless.
        if (!NativeMethods.CreateEnvironmentBlock(out var environment, token, bInherit: false))
        {
            throw new Win32Exception();
        }

        try
        {
            var startup = new NativeMethods.STARTUPINFOW
            {
                cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFOW>(),
                lpDesktop = InteractiveDesktop,
            };

            // The image, quoted, and nothing after it. CreateProcessAsUserW may
            // write into this buffer, so it is a builder rather than a literal.
            var commandLine = new StringBuilder(imagePath.Length + 2).Append('"').Append(imagePath).Append('"');

            if (!NativeMethods.CreateProcessAsUserW(
                    token,
                    lpApplicationName: imagePath,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                    lpEnvironment: environment,
                    lpCurrentDirectory: workingDirectory,
                    lpStartupInfo: ref startup,
                    lpProcessInformation: out var process))
            {
                throw new Win32Exception();
            }

            // Not kept: the service has no further business with the process.
            _ = NativeMethods.CloseHandle(process.hThread);
            _ = NativeMethods.CloseHandle(process.hProcess);
            return (int)process.dwProcessId;
        }
        finally
        {
            _ = NativeMethods.DestroyEnvironmentBlock(environment);
        }
    }

    private static class NativeMethods
    {
        internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
        internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        internal enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,
            WTSConnected,
            WTSConnectQuery,
            WTSShadow,
            WTSDisconnected,
            WTSIdle,
            WTSListen,
            WTSReset,
            WTSDown,
            WTSInit,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WTS_SESSION_INFOW
        {
            public uint SessionId;
            public IntPtr pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFOW
        {
            public uint cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSEnumerateSessionsW(
            IntPtr hServer, uint reserved, uint version, out IntPtr ppSessionInfo, out uint pCount);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateEnvironmentBlock(
            out IntPtr lpEnvironment, SafeAccessTokenHandle hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUserW(
            SafeAccessTokenHandle hToken,
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
