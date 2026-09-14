using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace EndpointAgent.Windows.SessionNotice;

/// <summary>
/// The token the notifier is started with: the session user's own, and never its
/// elevated form.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SessionUserToken
{
    /// <summary>Windows: no user is signed in to that session.</summary>
    internal const int ErrorNoToken = 1008;

    /// <summary>
    /// The primary token of the user signed in to <paramref name="sessionId"/>, or
    /// null when nobody is. Needs the service's own privilege (<c>SeTcbPrivilege</c>),
    /// which LocalSystem holds and no interactive user does.
    /// </summary>
    public static SafeAccessTokenHandle? Open(uint sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            token.Dispose();
            if (error == ErrorNoToken)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        using (token)
        {
            return WithoutElevation(token);
        }
    }

    /// <summary>
    /// A primary token that is not the elevated half of a UAC pair. An
    /// administrator's session has two tokens; the desktop runs on the limited
    /// one, and so must the notifier: a process with the user's environment
    /// variables and the user's elevated rights would be a way around UAC that no
    /// user is entitled to.
    /// </summary>
    internal static SafeAccessTokenHandle WithoutElevation(SafeAccessTokenHandle token)
    {
        var elevationType = QueryInt(token, NativeMethods.TokenElevationType);
        if (elevationType != NativeMethods.TokenElevationTypeFull)
        {
            return DuplicatePrimary(token);
        }

        if (!NativeMethods.GetTokenInformation(
                token, NativeMethods.TokenLinkedToken, out IntPtr linked, IntPtr.Size, out _))
        {
            throw new Win32Exception();
        }

        using var limited = new SafeAccessTokenHandle(linked);
        return DuplicatePrimary(limited);
    }

    /// <summary>Whether a token carries elevated rights. For tests and diagnostics.</summary>
    internal static bool IsElevated(SafeAccessTokenHandle token) =>
        QueryInt(token, NativeMethods.TokenElevation) != 0;

    private static SafeAccessTokenHandle DuplicatePrimary(SafeAccessTokenHandle token)
    {
        if (!NativeMethods.DuplicateTokenEx(
                token,
                NativeMethods.MAXIMUM_ALLOWED,
                IntPtr.Zero,
                NativeMethods.SecurityImpersonation,
                NativeMethods.TokenPrimary,
                out var duplicate))
        {
            throw new Win32Exception();
        }

        return duplicate;
    }

    private static int QueryInt(SafeAccessTokenHandle token, int informationClass)
    {
        if (!NativeMethods.GetTokenInformation(token, informationClass, out int value, sizeof(int), out _))
        {
            throw new Win32Exception();
        }

        return value;
    }

    private static class NativeMethods
    {
        internal const int TokenElevationType = 18;
        internal const int TokenLinkedToken = 19;
        internal const int TokenElevation = 20;
        internal const int TokenElevationTypeFull = 2;
        internal const uint MAXIMUM_ALLOWED = 0x02000000;
        internal const int SecurityImpersonation = 2;
        internal const int TokenPrimary = 1;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle phToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            SafeAccessTokenHandle tokenHandle, int tokenInformationClass, out int tokenInformation,
            int tokenInformationLength, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            SafeAccessTokenHandle tokenHandle, int tokenInformationClass, out IntPtr tokenInformation,
            int tokenInformationLength, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(
            SafeAccessTokenHandle existingToken, uint desiredAccess, IntPtr tokenAttributes,
            int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);
    }
}
