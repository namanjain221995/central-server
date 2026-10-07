using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Power and session control via Win32 APIs.
/// </summary>
/// <remarks>
/// <para>
/// No shell command is ever built (ADR-0005). Restart/shutdown use
/// <c>InitiateSystemShutdownExW</c> after enabling <c>SeShutdownPrivilege</c> in
/// the process token; lock and sign-out target the active console session via
/// <c>WTSDisconnectSession</c> / <c>WTSLogoffSession</c>, because the
/// session-relative APIs (<c>LockWorkStation</c>, <c>ExitWindowsEx</c>) act on
/// the calling session and a service lives in Session 0 where there is nobody
/// to lock or sign out. Every entry point is only reachable through
/// an authenticated, permission-checked, audited server task.
/// </para>
/// <para>
/// These operations require the agent to run elevated (LocalSystem in
/// production). When unelevated, the Win32 call fails and the executor reports the
/// failure - it never silently no-ops.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceControl(ILogger<WindowsDeviceControl> logger) : IDeviceControl
{
    private readonly ILogger<WindowsDeviceControl> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    // Reason: SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | planned.
    private const uint ShutdownReasonPlannedOther = 0x00000000 | 0x00000000 | 0x80000000;

    public Task RestartAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default)
    {
        InitiateShutdown(graceSeconds, message, restart: true);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default)
    {
        InitiateShutdown(graceSeconds, message, restart: false);
        return Task.CompletedTask;
    }

    public Task AbortRestartAsync(CancellationToken cancellationToken = default)
    {
        // Same privilege as scheduling one: aborting a shutdown is the same
        // right exercised in the other direction.
        EnableShutdownPrivilege();

        if (!AbortSystemShutdownW(lpMachineName: null))
        {
            // ERROR_NO_SHUTDOWN_IN_PROGRESS (1116) comes through here too; the
            // executor tells it apart from a refusal and reports it as "nothing
            // to cancel" rather than as an error.
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"AbortSystemShutdown failed (error={error}).");
        }

        _logger.LogWarning("The pending restart or shutdown was aborted by an authorized server task.");
        return Task.CompletedTask;
    }

    public Task LockAsync(CancellationToken cancellationToken = default)
    {
        // LockWorkStation only works from within the interactive session, and the
        // agent is a service in Session 0 -- from there it always fails, which is
        // exactly what live acceptance against a real endpoint showed. The
        // service-safe equivalent is disconnecting the active console session:
        // the session keeps running with all its applications, and the physical
        // console drops to the secure logon screen. Same effect the user gets
        // from Win+L, initiated from outside the session.
        var sessionId = FindInteractiveSessionId();
        if (sessionId is null)
        {
            throw new InvalidOperationException(
                "No interactive user session to lock -- nobody is signed in.");
        }

        if (!WTSDisconnectSession(WtsCurrentServerHandle, sessionId.Value, bWait: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"WTSDisconnectSession failed for session {sessionId.Value}.");
        }

        _logger.LogInformation("Interactive session {SessionId} locked by an authorized server task.", sessionId.Value);
        return Task.CompletedTask;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // ExitWindowsEx logs off the CALLING process's session -- from a service
        // that is Session 0, so it could never sign out the person at the
        // machine. WTSLogoffSession targets the interactive session explicitly.
        var sessionId = FindInteractiveSessionId();
        if (sessionId is null)
        {
            throw new InvalidOperationException(
                "No interactive user session to sign out -- nobody is signed in.");
        }

        // bWait: false -- report that the sign-out was initiated rather than
        // blocking the task loop on applications that are slow to close.
        if (!WTSLogoffSession(WtsCurrentServerHandle, sessionId.Value, bWait: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"WTSLogoffSession failed for session {sessionId.Value}.");
        }

        _logger.LogWarning("Interactive session {SessionId} signed out by an authorized server task.", sessionId.Value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The session id of the signed-in user, or null when nobody is.
    /// </summary>
    /// <remarks>
    /// The active console session covers the physical machine -- the normal case
    /// for a managed endpoint. 0xFFFFFFFF means no console session is attached.
    /// A user connected only over RDP is not found by this; managing RDP-only
    /// sessions would need WTSEnumerateSessions and is deliberately out of scope
    /// until a real deployment needs it.
    /// </remarks>
    private static uint? FindInteractiveSessionId()
    {
        var consoleSession = WTSGetActiveConsoleSessionId();
        return consoleSession == 0xFFFFFFFF ? null : consoleSession;
    }

    private void InitiateShutdown(int graceSeconds, string? message, bool restart)
    {
        EnableShutdownPrivilege();

        var timeout = (uint)Math.Clamp(graceSeconds, 0, 3600);

        // bForceAppsClosed = false: give apps the grace period to save.
        var ok = InitiateSystemShutdownExW(
            lpMachineName: null,
            lpMessage: message,
            dwTimeout: timeout,
            bForceAppsClosed: false,
            bRebootAfterShutdown: restart,
            dwReason: ShutdownReasonPlannedOther);

        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"InitiateSystemShutdownEx failed (restart={restart}, error={error}).");
        }

        _logger.LogWarning(
            "{Action} initiated with a {Grace}s grace period by an authorized server task.",
            restart ? "Restart" : "Shutdown", timeout);
    }

    /// <summary>Enables SeShutdownPrivilege in the current process token.</summary>
    private void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed.");
            }

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed.");
            }

            // AdjustTokenPrivileges succeeds even if not all privileges were assigned;
            // ERROR_NOT_ALL_ASSIGNED means the process lacks the privilege (unelevated).
            var last = Marshal.GetLastWin32Error();
            if (last == 1300 /* ERROR_NOT_ALL_ASSIGNED */)
            {
                throw new Win32Exception(last,
                    "SeShutdownPrivilege could not be enabled; the agent is not running elevated.");
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    /// <summary>WTS_CURRENT_SERVER_HANDLE: operate on the local machine.</summary>
    private static readonly IntPtr WtsCurrentServerHandle = IntPtr.Zero;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, uint sessionId, bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, bool bWait);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownExW(
        string? lpMachineName,
        string? lpMessage,
        uint dwTimeout,
        [MarshalAs(UnmanagedType.Bool)] bool bForceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool bRebootAfterShutdown,
        uint dwReason);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AbortSystemShutdownW(string? lpMachineName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
