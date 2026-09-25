using System.Runtime.Versioning;
using System.Security.Principal;
using EndpointAgent.Core.Inventory;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>One loaded user profile hive: who it belongs to, and how to read it.</summary>
/// <param name="Sid">The account's SID -- the stable identity, since names are renameable.</param>
/// <param name="Account">The resolved account name, or the SID when it cannot be resolved.</param>
public readonly record struct LoadedUserHive(string Sid, string Account);

/// <summary>One user profile the machine knows, whether or not that user is signed in.</summary>
/// <remarks>
/// No account name, unlike <see cref="LoadedUserHive"/>, and on purpose: resolving
/// one is an LSA lookup that, for a domain SID whose account is gone or whose
/// domain controller is out of reach (a laptop at home), can wait out a
/// discovery timeout of seconds -- and a profile list holds every account that
/// ever signed in, stale ones included, so a shared PC accumulates dozens. A
/// caller that reports a name resolves it with
/// <see cref="WindowsUserHives.ResolveAccountName"/> for the few profiles it
/// actually reports on, not for every entry in the list.
/// </remarks>
/// <param name="Sid">The account's SID -- the stable identity, since names are renameable.</param>
/// <param name="Path">The profile directory, absolute and local, as the machine's profile list records it.</param>
public readonly record struct UserProfile(string Sid, string Path);

/// <summary>
/// The user profile hives Windows currently has mounted, and the user profiles
/// the machine knows.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every per-user discovery source, so they agree on which hives exist,
/// which are people, and what to call them. Before this, that logic lived inside
/// the uninstall-registry reader; a second source copying it would have been a
/// second place for the SYSTEM-profile mistake to come back.
/// </para>
/// <para>
/// <b>Only mounted hives.</b> A user who is fully signed out has none, and their
/// per-user software is not reported until they next sign in. Reading it anyway
/// would mean <c>RegLoadKey</c> on a profile the agent does not own, which can
/// fail on a locked or roaming profile and, if a hive were left mounted, block
/// that user's next logon. Under-reporting a signed-out user is the safer
/// failure and is a deliberate, documented choice.
/// </para>
/// <para>
/// <b>Two enumerations, on purpose.</b> <see cref="Loaded"/> answers "whose
/// registry can be read right now" and <see cref="AllProfiles"/> answers "whose
/// files are on this disk". They differ because a profile <em>directory</em> is
/// readable by the service whether or not its owner is signed in, whereas a
/// profile <em>hive</em> is mounted only while they are. So per-user discovery
/// that reads files (Chrome's profile data, say) covers signed-out users, while
/// per-user discovery that reads the registry deliberately does not, for the
/// reason above. A source must pick the enumeration that matches what it reads,
/// or it will either miss people it could have seen or try to mount a hive.
/// They also differ in what they resolve: a mounted hive belongs to someone
/// signed in, whose name resolves at once, whereas the profile list holds every
/// account that ever signed in, so <see cref="AllProfiles"/> leaves names to
/// the caller (see <see cref="UserProfile"/>).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsUserHives
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string ShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

    /// <summary>
    /// Every mounted hive that belongs to a person.
    /// </summary>
    /// <remarks>
    /// HKEY_USERS holds one subkey per mounted hive, named by SID. The well-known
    /// service SIDs (S-1-5-18/19/20 -- SYSTEM and the two service accounts) are
    /// not people, and every hive may also appear with a <c>_Classes</c> suffix
    /// holding COM and package registration rather than a user's own settings.
    /// Neither is a profile in its own right.
    /// </remarks>
    public static IReadOnlyList<LoadedUserHive> Loaded()
    {
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);

            return users.GetSubKeyNames()
                .Where(IsRealUserSid)
                .Select(sid => new LoadedUserHive(sid, ResolveAccountName(sid)))
                .ToArray();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Every user profile the machine's profile list records for a person, with
    /// its directory -- signed in or not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from HKLM's ProfileList, which is what Windows itself consults at
    /// logon, rather than from HKEY_USERS: the point of this enumeration is the
    /// users whose hive is <em>not</em> mounted. A SID whose profile directory is
    /// unknown, or is not a local directory, is dropped -- there is nothing on
    /// disk to read for it, and a redirected profile on a share is not somewhere
    /// the service goes on a user's behalf.
    /// </para>
    /// <para>
    /// This lists directories, not permission to read what is inside them. A
    /// caller that walks a profile still has to expect an unreadable file and
    /// treat it as missing from this snapshot.
    /// </para>
    /// <para>
    /// Unlike <see cref="Loaded"/>, this throws when the profile list itself
    /// cannot be read (a registry <see cref="System.Security.SecurityException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/>).
    /// An empty list here would make "no users" and "could not look" the same
    /// answer, and a caller's section status exists to keep them apart: the
    /// Chrome collector marks its section errored so the server's
    /// keep-last-known rule can act on it. One unreadable entry is still
    /// skipped, as before.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<UserProfile> AllProfiles(CancellationToken cancellationToken = default)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var list = baseKey.OpenSubKey(ProfileList);
        if (list is null)
        {
            return [];
        }

        var profiles = new List<UserProfile>();
        foreach (var sid in list.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // When Windows fails to load a profile it renames its entry to
            // "<SID>.bak" and signs the user into a temporary one under a
            // fresh "<SID>" entry. The .bak entry still passes the SID-shape
            // check but is not an account; carrying it would report the same
            // person twice, once under a name no server can parse as a SID.
            if (!IsRealUserSid(sid) || sid.Contains('.'))
            {
                continue;
            }

            var path = ProfilePath(sid);
            if (path is null)
            {
                continue;
            }

            profiles.Add(new UserProfile(sid, path));
        }

        return profiles;
    }

    /// <summary>
    /// Whether this HKEY_USERS subkey is a human's profile hive.
    /// </summary>
    /// <remarks>
    /// Real accounts are S-1-5-21-... (local or domain) or S-1-12-1-... (Entra).
    /// </remarks>
    public static bool IsRealUserSid(string sid) =>
        !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)
        && (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
            || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The account a SID names, falling back to the SID itself.</summary>
    /// <remarks>
    /// A deleted or unresolvable account still had software installed, so the SID
    /// is reported rather than dropping the entry: an unattributed application is
    /// more useful than a missing one.
    /// </remarks>
    public static string ResolveAccountName(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid;
        }
    }

    /// <summary>
    /// Opens a subkey of one user's hive, or null when it is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// The caller disposes. <paramref name="classes"/> selects the companion
    /// <c>&lt;SID&gt;_Classes</c> hive, which is where package registration lives.
    /// </remarks>
    public static RegistryKey? OpenUserSubKey(string sid, string subKeyPath, bool classes = false)
    {
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            return users.OpenSubKey(classes ? sid + "_Classes" : sid)?.OpenSubKey(subKeyPath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The user's profile directory, from the machine's profile list, or null
    /// when it is unknown or not a local directory.
    /// </summary>
    /// <remarks>
    /// Read from HKLM rather than from the user's own hive: the profile list is
    /// what Windows itself consults, and it is readable by the service for every
    /// account whether or not that account is signed in.
    /// </remarks>
    public static string? ProfilePath(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || sid.Contains('\\'))
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var profile = baseKey.OpenSubKey(ProfileList + "\\" + sid);
            return LocalDirectory(profile?.GetValue("ProfileImagePath") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The user's own Start Menu Programs folder: where the shell was told it is,
    /// if that is a local directory, otherwise the profile default.
    /// </summary>
    /// <remarks>
    /// A redirected folder on a network share is refused rather than walked: the
    /// service must not reach for a share on a user's behalf.
    /// </remarks>
    public static string? StartMenuPrograms(string sid)
    {
        try
        {
            using var shellFolders = OpenUserSubKey(sid, ShellFolders);
            if (LocalDirectory(shellFolders?.GetValue("Programs") as string) is { } redirected)
            {
                return redirected;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through to the default.
        }

        var profile = ProfilePath(sid);
        return profile is null
            ? null
            : Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
    }

    /// <summary>An absolute local directory with no traversal, or null. The same rule executables get.</summary>
    private static string? LocalDirectory(string? raw) => ExecutablePath.Normalize(raw);
}
