using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using EndpointAgent.Core.Inventory.Chrome;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Finds the installed Google Chrome, every local user's Chrome profiles and each
/// profile's extensions, from the registry and the files Chrome owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and structurally so.</b> Registry keys are opened for reading,
/// files are opened for reading with shared access, and nothing is launched
/// (ADR-0005). Nothing is ever written under a user profile: Chrome authenticates
/// its own preference files with a machine-bound MAC and treats an outside edit
/// as corruption, resetting the profile's settings. A collector that so much as
/// touched a preference file could reset every user's browser.
/// </para>
/// <para>
/// <b>Why per-user data comes from the profile list, not from the service's own
/// profile.</b> The service runs as LocalSystem, whose <c>AppData\Local</c> holds
/// no one's Chrome; HKCU and the special-folder API would both answer for SYSTEM
/// and find nothing, while looking as if per-user discovery were covered. Each
/// user's <c>User Data</c> directory is reached through
/// <see cref="WindowsUserHives.AllProfiles"/> instead, which also covers users who
/// are signed out: their files are on disk even when their hive is not mounted.
/// </para>
/// <para>
/// <b>Why the 32-bit registry view is read first.</b> Chrome is 64-bit, but
/// Google Update is a 32-bit product and registers everything -- the uninstall
/// entry, the client and client-state keys -- under <c>WOW6432Node</c>. The
/// 64-bit view is consulted second, for the rare install that registered
/// natively. Architecture is taken from the updater's own record for the same
/// reason: the view a key was found in says nothing about the browser's bitness.
/// </para>
/// <para>
/// Only what Chrome records is carried. Extension names and versions come from
/// Chrome's own settings, never from the extension's <c>manifest.json</c>, whose
/// name is an unlocalised <c>__MSG_*__</c> placeholder. Of the Google account
/// fields Chrome keeps beside each profile, only the person's name and the
/// account's domain are read -- Chrome builds the profile label from them --
/// and the e-mail address (<c>user_name</c>), account id and picture never are:
/// the parser in Core does not expose them and this class has no path that could.
/// </para>
/// <para>
/// <b>Only inside the user's own profile.</b> The profile root comes from HKLM
/// and is trusted; everything below it is the user's to rewrite, and a directory
/// junction needs no privilege to plant. So every directory between the root
/// and a file the service reads is checked to be a real directory, not a
/// reparse point (<see cref="RealDirectoryChain"/>), and a profile key from
/// <c>Local State</c> is trusted only as a plain directory name.
/// </para>
/// <para>
/// Fault-isolated like every other section: a file that cannot be read is
/// missing from this snapshot, a user whose profile misbehaves loses only their
/// own rows, and anything unexpected marks the section as errored while keeping
/// what was already collected. Nothing throws into the inventory path.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsChromeCollector(ILogger<WindowsChromeCollector> logger) : IChromeCollector
{
    private readonly ILogger<WindowsChromeCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private const string UninstallEntry = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Google Chrome";

    /// <summary>Chrome's application id with Google Update; fixed across every channel and version.</summary>
    private const string ChromeAppId = "{8A69D345-D564-463c-AFF1-A69D9E530F96}";

    private const string GoogleUpdate = @"SOFTWARE\Google\Update";
    private const string UpdateClient = GoogleUpdate + @"\Clients\" + ChromeAppId;
    private const string UpdateClientState = GoogleUpdate + @"\ClientState\" + ChromeAppId;

    private const string ExecutableName = "chrome.exe";
    private const string LocalStateFile = "Local State";
    private const string SecurePreferencesFile = "Secure Preferences";
    private const string PreferencesFile = "Preferences";

    /// <summary>
    /// The registry views in the order they are consulted: WOW6432Node first, for
    /// the reason in the class remarks.
    /// </summary>
    private static readonly RegistryView[] Views = [RegistryView.Registry32, RegistryView.Registry64];

    /// <summary>
    /// The Program Files variables, most specific first. These are machine-level
    /// and the same for every account, so reading them from the service's own
    /// environment is sound in a way that reading a per-user variable from it
    /// would not be (that environment belongs to LocalSystem).
    /// </summary>
    private static readonly string[] ProgramFilesVariables = ["ProgramW6432", "ProgramFiles", "ProgramFiles(x86)"];

    /// <inheritdoc />
    public ValueTask<InventoryChrome> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DiscoveredChromeInstallation? installation = null;
        var profiles = new List<DiscoveredChromeProfile>();
        var enumerationFailed = false;

        try
        {
            installation = ReadMachineInstallation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Machine-wide Chrome discovery failed; the installation is unrecorded this snapshot.");
            enumerationFailed = true;
        }

        try
        {
            foreach (var user in WindowsUserHives.AllProfiles(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The account name is resolved at most once per user, and only
                // once something of theirs is going to be reported. The lookup
                // is an LSA call that can wait on domain-controller discovery
                // for every stale domain SID in the profile list, and nothing on
                // the wire needs the name of a user who has no Chrome.
                var account = new Lazy<string>(() => WindowsUserHives.ResolveAccountName(user.Sid));

                try
                {
                    // A per-user copy counts only when no machine-wide one was
                    // found: the report carries one installation, and the
                    // machine-wide one is the one every account runs.
                    installation ??= ReadUserInstallation(user, account);
                    ReadUserProfiles(user, account, profiles, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One user's misbehaving profile must not cost the others
                    // theirs: keep what was read and say the section is incomplete.
                    _logger.LogWarning(ex, "Chrome discovery failed for {Sid}; that user's profiles are incomplete this snapshot.", user.Sid);
                    enumerationFailed = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes a profile list that could not be read at all: "no users"
            // and "could not look" must not be the same answer.
            _logger.LogWarning(ex, "Chrome per-user discovery stopped early; reporting {Count} profile(s).", profiles.Count);
            enumerationFailed = true;
        }

        _logger.LogDebug(
            "Chrome discovery: installation {Found}, {Profiles} profile(s), enumeration failed: {Failed}.",
            installation is null ? "not found" : "found",
            profiles.Count,
            enumerationFailed);

        return ValueTask.FromResult(ChromeInventoryNormalizer.Normalize(installation, profiles, enumerationFailed));
    }

    /// <summary>
    /// The machine-wide installation, from the uninstall entry and Google Update's
    /// keys, or null when there is none.
    /// </summary>
    private DiscoveredChromeInstallation? ReadMachineInstallation()
    {
        var (registered, version, installLocation) = ReadUninstallEntry();

        // The executable is reported only when it is there: an uninstall entry
        // that outlived its files is not an installation anyone can run. Without a
        // usable InstallLocation, the fixed Program Files location is tried, which
        // also catches a browser laid down by an image rather than the installer
        // and so never registered at all.
        var executable = installLocation is null ? null : ExistingExecutable(Path.Combine(installLocation, ExecutableName));
        executable ??= ProgramFilesExecutable();

        if (!registered && executable is null)
        {
            return null;
        }

        // DisplayVersion is what Windows records as installed and is preferred;
        // the executable's version resource stands in when the entry is missing
        // or silent.
        version ??= executable is null ? null : ProductVersion(executable);

        return new DiscoveredChromeInstallation(
            version,
            executable,
            ArchitectureFromAp(ReadMachineString(UpdateClientState, "ap")),
            ReadMachineString(UpdateClient, "channel"),
            Scope: "Machine",
            InstalledForUser: null,
            ReadMachineString(GoogleUpdate, "version"),
            ReadLastUpdateCheck());
    }

    /// <summary>
    /// A Chrome installed into one user's own profile, or null. Only consulted
    /// when no machine-wide installation was found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-user Chrome writes its uninstall entry and updater state into the
    /// user's own hive, which is mounted only while they are signed in (see
    /// <see cref="WindowsUserHives.AllProfiles"/>). The executable's version
    /// resource needs no hive, so it is what is reported; the updater facts are
    /// left unrecorded rather than read for the users who happen to be signed in
    /// and not for the rest.
    /// </para>
    /// <para>
    /// That version is user-controlled data: the file sits inside the user's own
    /// profile, so it is whatever they put there, and the first user in profile
    /// list order with such a file decides what the report says on a machine
    /// with no machine-wide Chrome. That is the nature of a per-user install and
    /// the server treats a "User" scope accordingly; the directories on the way
    /// to it are still checked to be real, so the file is at least the user's
    /// own and not one a junction points at.
    /// </para>
    /// </remarks>
    private DiscoveredChromeInstallation? ReadUserInstallation(UserProfile user, Lazy<string> account)
    {
        var application = RealDirectoryChain(user.Path, "AppData", "Local", "Google", "Chrome", "Application");
        if (application is null)
        {
            return null;
        }

        var executable = ExistingExecutable(Path.Combine(application, ExecutableName));
        if (executable is null)
        {
            return null;
        }

        return new DiscoveredChromeInstallation(
            ProductVersion(executable),
            executable,
            Architecture: null,
            Channel: null,
            Scope: "User",
            InstalledForUser: account.Value,
            UpdaterVersion: null,
            LastUpdateCheck: null);
    }

    /// <summary>
    /// Every profile <c>Local State</c> lists for one user whose directory is
    /// there, with the extension records from both preference files, appended to
    /// <paramref name="profiles"/>.
    /// </summary>
    /// <remarks>
    /// Internal so a test can hand it a profile root of its own making -- the
    /// one way to prove, on a CI agent, what a junction planted under a profile
    /// does and does not achieve.
    /// </remarks>
    internal void ReadUserProfiles(UserProfile user, Lazy<string> account, List<DiscoveredChromeProfile> profiles, CancellationToken cancellationToken)
    {
        var userData = RealDirectoryChain(user.Path, "AppData", "Local", "Google", "Chrome", "User Data");
        if (userData is null)
        {
            return;
        }

        var localStatePath = Path.Combine(userData, LocalStateFile);
        if (!File.Exists(localStatePath))
        {
            // Chrome has never run for this user; there is nothing to read and
            // nothing to log.
            return;
        }

        var localState = ReadFile<ChromeLocalStateInfo>(localStatePath, ChromeLocalState.MaxBytes, ChromeLocalState.Parse);
        if (localState is null)
        {
            return;
        }

        foreach (var info in localState.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (profiles.Count >= InventoryChrome.MaxProfiles)
            {
                _logger.LogWarning(
                    "The report already carries {Max} Chrome profiles; the rest of {Sid}'s are not read.",
                    InventoryChrome.MaxProfiles, user.Sid);
                return;
            }

            // The profile key names a directory under User Data and nothing else.
            // Local State is a user-writable file, so a key that is a path rather
            // than a name would let a user point the service, which reads as
            // LocalSystem, at a directory of their choosing. The parser in Core
            // already refused separators and traversal; this catches what only
            // Windows can judge (see IsPlainProfileKey).
            if (!IsPlainProfileKey(info.ProfileKey))
            {
                _logger.LogDebug("Skipping a Chrome profile whose key Windows would refuse or rewrite as a directory name for {Sid}.", user.Sid);
                continue;
            }

            var profileDirectory = Path.Combine(userData, info.ProfileKey);
            if (!IsRealDirectory(profileDirectory))
            {
                continue;
            }

            // Both files are read and carried separately; which one wins for an
            // extension recorded in both is the normalizer's rule.
            var secure = ReadFile<IReadOnlyList<ChromeExtensionEntry>>(
                Path.Combine(profileDirectory, SecurePreferencesFile), ChromeExtensionSettings.MaxBytes, ChromeExtensionSettings.Parse) ?? [];
            var preferences = ReadFile<IReadOnlyList<ChromeExtensionEntry>>(
                Path.Combine(profileDirectory, PreferencesFile), ChromeExtensionSettings.MaxBytes, ChromeExtensionSettings.Parse) ?? [];

            profiles.Add(new DiscoveredChromeProfile(user.Sid, account.Value, info, profileDirectory, secure, preferences));
        }
    }

    /// <summary>
    /// Whether a profile key from <c>Local State</c> is a plain directory name:
    /// no separators, no traversal, nothing the file system would refuse, and no
    /// spelling Win32 would quietly rewrite.
    /// </summary>
    /// <remarks>
    /// The twin of the Core parser's key rule (<c>ChromeLocalState.IsProfileKey</c>),
    /// and deliberately so: Core protects the identity of a record and is pinned
    /// on any OS; this protects the path join and adds what only the file
    /// system's own rules can say. Win32 path normalisation strips a trailing
    /// dot or space from a name and maps a reserved device name (<c>CON</c>,
    /// <c>NUL</c>, <c>COM1</c>) to the device, so such a key would name a
    /// different directory from the one written -- or no directory at all --
    /// and two keys could name one. Defence in depth on a user-writable file;
    /// loosening either must not loosen the other.
    /// </remarks>
    internal static bool IsPlainProfileKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key is "." || key.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        if (key[^1] is '.' or ' ')
        {
            return false;
        }

        // The device name is the stem before the first dot: "NUL.old" is NUL.
        var dot = key.IndexOf('.');
        if (ReservedDeviceNames.Contains(dot < 0 ? key : key[..dot]))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(key), key, StringComparison.Ordinal);
    }

    /// <summary>The names Win32 reserves for devices in every directory.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The directory at the end of a chain of segments under a trusted root, or
    /// null when any segment is missing, is a reparse point, or cannot be inspected.
    /// </summary>
    /// <remarks>
    /// <b>Why every segment, not just the last.</b> The root is the profile
    /// directory from HKLM's profile list and is the machine's to say; everything
    /// below it is the user's, and a directory junction needs no privilege to
    /// plant. Every file API follows a junction wherever it sits in a path, so a
    /// check on the leaf alone would pass a leaf that is a perfectly real
    /// directory inside a target of the user's choosing: another user's
    /// <c>User Data</c>, say, read as LocalSystem and reported under the
    /// planter's SID, or any directory on the machine holding a file by the
    /// right name. The service reads on a user's behalf only inside that user's
    /// own profile. Internal so a test can plant a junction and prove it is refused.
    /// </remarks>
    internal string? RealDirectoryChain(string root, params string[] segments)
    {
        var path = root;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            if (!IsRealDirectory(path))
            {
                return null;
            }
        }

        return path;
    }

    /// <summary>
    /// Whether a directory exists and is a real directory rather than a junction
    /// or symbolic link.
    /// </summary>
    /// <remarks>
    /// A reparse point where a directory should be is not followed. The service
    /// reads as LocalSystem, so a link a user planted under their own profile
    /// would let it read files elsewhere on the machine on that user's behalf.
    /// Nothing under a profile may lead the service outside the profile.
    /// </remarks>
    private bool IsRealDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return false;
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger.LogDebug("{Path} is a reparse point and is not read.", path);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Could not inspect directory {Path}.", path);
            return false;
        }
    }

    /// <summary>
    /// Opens one of Chrome's files for reading and hands it to a parser, or null
    /// when it is absent, oversized, a link, or unreadable this instant.
    /// </summary>
    private T? ReadFile<T>(string path, long maxBytes, Func<Stream, T?> parse) where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            // A file that is a link is not something Chrome writes; the same
            // reasoning as for a profile directory that is one.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger.LogDebug("{Path} is a reparse point and is not read.", path);
                return null;
            }

            // A cheap early refusal, not the enforcement: the file is the user's
            // and can grow between this check and the read, so the parser reads
            // no stream past the cap whatever length it reports (BoundedRead).
            if (info.Length > maxBytes)
            {
                _logger.LogDebug("{Path} is {Length} bytes, over the {Max}-byte cap, and is not read.", path, info.Length, maxBytes);
                return null;
            }

            // FileShare.ReadWrite | FileShare.Delete, because Chrome keeps these
            // files open and replaces them atomically: it writes a temporary file
            // and renames it over the old one. Without FileShare.Delete our open
            // handle would make that rename fail and cost Chrome a write; with
            // it, a read that races the swap finishes on the old contents or
            // fails, and either way the file is merely missing from this snapshot.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Missing from one snapshot is a state the section's status and the
            // server's keep-last-known rule already allow for.
            _logger.LogDebug(ex, "Could not read {Path}; it is missing from this snapshot.", path);
            return null;
        }
    }

    /// <summary>The uninstall entry, from whichever registry view holds it.</summary>
    private (bool Registered, string? Version, string? InstallLocation) ReadUninstallEntry()
    {
        var registered = false;
        string? version = null;
        string? location = null;

        foreach (var view in Views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var entry = baseKey.OpenSubKey(UninstallEntry);
                if (entry is null)
                {
                    continue;
                }

                registered = true;
                version ??= Text(entry.GetValue("DisplayVersion"));
                location ??= ExecutablePath.Normalize(Text(entry.GetValue("InstallLocation")));
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Could not read the Chrome uninstall entry in the {View} registry view.", view);
            }
        }

        return (registered, version, location);
    }

    /// <summary>
    /// <c>chrome.exe</c> in the fixed machine-wide location under any Program
    /// Files directory, or null.
    /// </summary>
    private static string? ProgramFilesExecutable()
    {
        foreach (var variable in ProgramFilesVariables)
        {
            var root = ExecutablePath.Normalize(Environment.GetEnvironmentVariable(variable));
            if (root is null)
            {
                continue;
            }

            var candidate = ExistingExecutable(Path.Combine(root, "Google", "Chrome", "Application", ExecutableName));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The normalised path when it names an executable that exists, else null.</summary>
    private static string? ExistingExecutable(string path)
    {
        var normalized = ExecutablePath.Normalize(path);
        return normalized is not null && File.Exists(normalized) ? normalized : null;
    }

    /// <summary>
    /// The product version from an executable's version resource, or null.
    /// </summary>
    /// <remarks>
    /// The resource section is parsed from the file as data; the image is never
    /// loaded. The same read <see cref="WindowsExecutableMetadataReader"/> makes.
    /// </remarks>
    private string? ProductVersion(string executable)
    {
        try
        {
            return Text(FileVersionInfo.GetVersionInfo(executable).ProductVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            _logger.LogDebug(ex, "Could not read the version resource of {Path}.", executable);
            return null;
        }
    }

    /// <summary>
    /// The browser's architecture as Google Update's <c>ap</c> value records it
    /// (<c>-arch_x64-statsdef_1</c> and the like), or null when it does not say.
    /// </summary>
    internal static string? ArchitectureFromAp(string? ap)
    {
        if (string.IsNullOrWhiteSpace(ap))
        {
            return null;
        }

        if (ap.Contains("-arch_x64", StringComparison.OrdinalIgnoreCase))
        {
            return "x64";
        }

        if (ap.Contains("-arch_arm64", StringComparison.OrdinalIgnoreCase))
        {
            return "arm64";
        }

        if (ap.Contains("-arch_x86", StringComparison.OrdinalIgnoreCase))
        {
            return "x86";
        }

        return null;
    }

    /// <summary>When Google Update last checked for updates, or null when it has not recorded one.</summary>
    private DateTimeOffset? ReadLastUpdateCheck()
    {
        foreach (var view in Views)
        {
            // A DWORD arrives as int; one past 2^31 would arrive negative, so it is
            // widened unsigned rather than compared as signed. Zero is "never".
            long seconds = ReadMachineValue(view, GoogleUpdate, "LastChecked") switch
            {
                int dword => unchecked((uint)dword),
                long qword => qword,
                _ => 0,
            };

            if (seconds > 0)
            {
                // Through the same plausibility bounds Chrome's own clocks get, so
                // a corrupt value becomes no date rather than a wrong one.
                return ChromeTime.FromUnixSeconds(seconds);
            }
        }

        return null;
    }

    /// <summary>A string value from the first registry view that holds it non-empty, or null.</summary>
    private string? ReadMachineString(string subKey, string valueName)
    {
        foreach (var view in Views)
        {
            if (Text(ReadMachineValue(view, subKey, valueName)) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private object? ReadMachineValue(RegistryView view, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Could not read {Value} under {Key} in the {View} registry view.", valueName, subKey, view);
            return null;
        }
    }

    private static string? Text(object? value)
    {
        var text = (value as string)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
