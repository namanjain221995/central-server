using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgent.Windows.CompanyPortal;

/// <summary>
/// The name of the user signed in to a session, as Windows reports it.
/// </summary>
/// <remarks>
/// Asked of the session, never of the client: the service learns the session
/// from the pipe driver and the user from the session, so a process cannot name
/// someone else. The name goes to the server for the request history and is
/// used for nothing else -- the server authorises on the device credential and
/// the device's group, and a wrong name would change what the history says and
/// nothing more. So a lookup that fails yields null rather than an error.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class SessionUser
{
    /// <summary>
    /// <c>DOMAIN\user</c> for the user signed in to <paramref name="sessionId"/>,
    /// or null when nobody is, or Windows will not say.
    /// </summary>
    public static string? NameOf(uint sessionId)
    {
        // The two values Windows reads as "this session" and "any session" are
        // not sessions a pipe client can be in; passed through, the first would
        // answer with the service's own session.
        if (sessionId is NativeMethods.WTS_CURRENT_SESSION or NativeMethods.WTS_ANY_SESSION)
        {
            return null;
        }

        var user = Query(sessionId, NativeMethods.WTSUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        var domain = Query(sessionId, NativeMethods.WTSDomainName);
        return string.IsNullOrEmpty(domain) ? null : domain + "\\" + user;
    }

    private static string? Query(uint sessionId, int informationClass)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE, sessionId, informationClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    private static class NativeMethods
    {
        internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
        internal const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;
        internal const uint WTS_ANY_SESSION = 0xFFFFFFFE;
        internal const int WTSUserName = 5;
        internal const int WTSDomainName = 7;

        [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformationW(
            IntPtr server, uint sessionId, int informationClass, out IntPtr buffer, out uint bytesReturned);

        [DllImport("wtsapi32.dll", ExactSpelling = true)]
        internal static extern void WTSFreeMemory(IntPtr memory);
    }
}
