using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Inventory.Chrome;

/// <summary>
/// Turns what the Windows collector found -- the installation, the profile
/// directories it could read, and the extension records in each -- into the
/// section the server will accept: de-duplicated, clamped to the wire limits,
/// and bounded.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the Windows collector on purpose. Reading the
/// registry and another user's <c>AppData</c> needs a real machine; deciding
/// which of two records for one extension is believed, or what counts as one
/// profile, does not, and those decisions are where the bugs live. Everything
/// here is exercised by fixtures, and nothing here reads a file, touches
/// Windows, or throws for bad data: an entry that cannot be reported is
/// dropped and the rest of the section is unaffected.
/// </para>
/// <para>
/// <b>The clamping is a correctness requirement, not tidiness.</b> The Agent API
/// validates the whole inventory report and rejects it outright -- hardware,
/// software, BitLocker and drivers included -- if any field of a section it
/// validates is over length or any list too long. It does that for the software
/// rows today, and the Chrome checks arrive with server-side ingestion of this
/// section (a later phase; in this one the server binds the section and ignores
/// it). Clamping here means that from the day it does, a person with a strangely
/// named profile, or an extension with an enormous update URL, still reports
/// everything else; letting it through would cost the machine's entire report.
/// </para>
/// </remarks>
public static class ChromeInventoryNormalizer
{
    // The statuses the contract allows. Pinned against InventoryChrome.Statuses
    // by tests, so a rename on either side fails the tests rather than producing
    // a section the server reads as "unknown".
    private const string StatusAvailable = "Available";
    private const string StatusNotInstalled = "NotInstalled";
    private const string StatusError = "Error";

    private const string ScopeMachine = "Machine";

    private const string InstallTypeUnknown = "Unknown";
    private const string InstallTypeExternalPolicyDownload = "ExternalPolicyDownload";
    private const string InstallTypeExternalPolicy = "ExternalPolicy";

    /// <summary>
    /// Chrome has defined manifest versions 1, 2 and 3. The bound is generous
    /// rather than exact so a future version 4 is reported, while a corrupt
    /// record's 0, negative or six-digit value is reported as unrecorded.
    /// </summary>
    private const int MinManifestVersion = 1;
    private const int MaxManifestVersion = 99;

    /// <summary>
    /// ASCII Unit Separator, joining a profile's SID and directory name into one
    /// identity string. Neither a SID nor a directory name can contain it, so two
    /// different profiles cannot collide into one identity.
    /// </summary>
    private const char IdentitySeparator = (char)0x1F;

    /// <summary>
    /// Produces the wire-ready section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Status.</b> <paramref name="enumerationFailed"/> wins: the section is
    /// "Error" and still carries whatever was read before the failure, because a
    /// partial section the server can flag beats an empty one it cannot tell
    /// from a clean machine. Otherwise the status says whether the collector
    /// found an <em>installation</em>, and nothing else. Profiles do not make
    /// Chrome "Available": Chrome's uninstaller leaves <c>User Data</c> behind
    /// unless the person ticks "also delete your browsing data" (unticked by
    /// default), so on every machine Chrome was ever removed from the profile
    /// directories outlive the browser, and a status read off them would count
    /// each such machine as having Chrome. So "NotInstalled" may carry profiles:
    /// they are still facts (the extensions an administrator may want to know
    /// about are recorded there), and the status says the browser is gone. The
    /// status is decided on what was <em>found</em>, not on what survives
    /// normalization: an installation whose version could not be read is
    /// dropped from the wire (the contract requires a version), but it is still
    /// an installation, so "NotInstalled" would be a false statement, whereas
    /// "Available" with no installation body is merely an incomplete one, which
    /// the server's keep-last-known rule already allows for.
    /// </para>
    /// <para>
    /// <b>Profiles.</b> The identity of a profile is (Windows SID, profile
    /// directory name), compared case-insensitively because NTFS is: a directory
    /// the collector reached twice by two spellings is one directory. The first
    /// occurrence wins and input order is kept, so the collector's ordering
    /// (user by user, then <c>Local State</c> order) is what the server sees.
    /// A profile with no SID, no directory name or no path cannot be identified
    /// or located and is dropped rather than reported with a blank required field
    /// that would cost the whole report.
    /// </para>
    /// <para>
    /// <b>Extensions.</b> The records from <c>Secure Preferences</c> are taken
    /// first and those from <c>Preferences</c> after, and the first record for an
    /// id wins. Secure Preferences is the file Chrome protects against tampering
    /// and the one it trusts for install state, so where the two disagree about
    /// an extension its record is the one reported; an extension only
    /// <c>Preferences</c> knows about is still reported. Ids are compared
    /// ordinally because a valid id is already lower-case a-p.
    /// </para>
    /// </remarks>
    public static InventoryChrome Normalize(
        DiscoveredChromeInstallation? installation,
        IReadOnlyList<DiscoveredChromeProfile> profiles,
        bool enumerationFailed)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var status = enumerationFailed
            ? StatusError
            : installation is null
                ? StatusNotInstalled
                : StatusAvailable;

        return new InventoryChrome(status, NormalizeInstallation(installation), NormalizeProfiles(profiles));
    }

    /// <summary>
    /// Chrome's <c>ManifestLocation</c> number as the wire name.
    /// </summary>
    /// <remarks>
    /// The numbers are Chromium's <c>extensions/common/mojom/manifest.mojom</c>
    /// and have been stable for years, but they remain an internal detail of
    /// another product: the name is what crosses the wire so that a renumbering
    /// in Chrome shows up here as an "Unknown" to investigate rather than as a
    /// silently wrong origin. Zero is Chrome's own "invalid location"; null is a
    /// record that carried no number at all. Both are unknown, not internal.
    /// </remarks>
    public static string InstallTypeOf(int? location) => location switch
    {
        1 => "Internal",
        2 => "ExternalPref",
        3 => "ExternalRegistry",
        4 => "Unpacked",
        5 => "Component",
        6 => "ExternalPrefDownload",
        7 => InstallTypeExternalPolicyDownload,
        8 => "CommandLine",
        9 => InstallTypeExternalPolicy,
        10 => "ExternalComponent",
        _ => InstallTypeUnknown,
    };

    /// <summary>
    /// Whether an install type means enterprise policy put the extension there.
    /// Only the two policy locations count; a Component extension is part of
    /// Chrome, not something an administrator chose.
    /// </summary>
    private static bool IsManagedInstallType(string installType) =>
        installType is InstallTypeExternalPolicyDownload or InstallTypeExternalPolicy;

    private static InventoryChromeInstallation? NormalizeInstallation(DiscoveredChromeInstallation? found)
    {
        if (found is null || Clamp(found.Version, InventoryChromeInstallation.MaxVersion) is not { } version)
        {
            // The contract requires a version, so an installation without one
            // cannot be put on the wire. The status still reports it as found.
            return null;
        }

        // The collector is the only producer of the scope, so a value outside the
        // contract's set is a bug in our own code rather than data worth
        // preserving. "Machine" is the safer reading: a machine-wide install
        // affects every user of the PC, so mistakenly calling it per-user would
        // understate its reach, while the reverse merely overstates it.
        var scope = Clamp(found.Scope, InventoryChromeInstallation.MaxInstallationScope);
        if (scope is null || !InventoryChromeInstallation.InstallationScopes.Contains(scope))
        {
            scope = ScopeMachine;
        }

        // A machine-wide install belongs to nobody; attributing it would be a lie.
        var user = scope == ScopeMachine
            ? null
            : Clamp(found.InstalledForUser, InventoryChromeInstallation.MaxInstalledForUser);

        return new InventoryChromeInstallation(
            version,
            Clamp(found.ExecutablePath, InventoryChromeInstallation.MaxExecutablePath),
            Clamp(found.Architecture, InventoryChromeInstallation.MaxArchitecture),
            Clamp(found.Channel, InventoryChromeInstallation.MaxChannel),
            scope,
            user,
            Clamp(found.UpdaterVersion, InventoryChromeInstallation.MaxUpdaterVersion),
            found.LastUpdateCheck);
    }

    private static IReadOnlyList<InventoryChromeProfile> NormalizeProfiles(IReadOnlyList<DiscoveredChromeProfile> found)
    {
        var kept = new List<InventoryChromeProfile>(Math.Min(found.Count, InventoryChrome.MaxProfiles));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in found)
        {
            if (profile is null || profile.Info is null)
            {
                continue;
            }

            // Clamp before comparing, for the reason SoftwareInventoryNormalizer
            // gives: two keys that differ only past the limit are the same on the
            // wire, and reporting both would be the duplicate the rule prevents.
            var sid = Clamp(profile.UserSid, InventoryChromeProfile.MaxUserSid);
            var key = Clamp(profile.Info.ProfileKey, InventoryChromeProfile.MaxProfileKey);
            var path = Clamp(profile.ProfilePath, InventoryChromeProfile.MaxProfilePath);
            if (sid is null || key is null || path is null)
            {
                continue;
            }

            if (!seen.Add(sid + IdentitySeparator + key))
            {
                continue;
            }

            kept.Add(new InventoryChromeProfile(
                sid,
                Clamp(profile.UserAccount, InventoryChromeProfile.MaxUserAccount),
                key,
                Clamp(profile.Info.Name, InventoryChromeProfile.MaxProfileName),
                path,
                profile.Info.IsManaged,
                profile.Info.LastActiveAt,
                NormalizeExtensions(profile.SecurePreferences, profile.Preferences)));

            if (kept.Count >= InventoryChrome.MaxProfiles)
            {
                break;
            }
        }

        return kept;
    }

    private static IReadOnlyList<InventoryChromeExtension> NormalizeExtensions(
        IReadOnlyList<ChromeExtensionEntry>? securePreferences,
        IReadOnlyList<ChromeExtensionEntry>? preferences)
    {
        var kept = new List<InventoryChromeExtension>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Secure Preferences first, so its record is the one a shared id keeps.
        foreach (var entry in (securePreferences ?? []).Concat(preferences ?? []))
        {
            // The parser already checks the shape, but the check is cheap and it
            // is the one the Agent API will apply to the whole report once it
            // ingests this section, as it does to the software rows today: an id
            // that slipped through here would then cost every other section.
            if (entry is null || !InventoryChromeExtension.IsValidExtensionId(entry.ExtensionId))
            {
                continue;
            }

            if (!seen.Add(entry.ExtensionId))
            {
                continue;
            }

            var installType = InstallTypeOf(entry.Location);

            kept.Add(new InventoryChromeExtension(
                entry.ExtensionId,
                Clamp(entry.Name, InventoryChromeExtension.MaxName),
                Clamp(entry.Version, InventoryChromeExtension.MaxVersion),
                PlausibleManifestVersion(entry.ManifestVersion),
                entry.Enabled,
                installType,
                IsManagedInstallType(installType),
                entry.FromWebStore,
                Clamp(entry.UpdateUrl, InventoryChromeExtension.MaxUpdateUrl),
                entry.InstalledAt,
                entry.UpdatedAt));

            if (kept.Count >= InventoryChromeProfile.MaxExtensions)
            {
                break;
            }
        }

        return kept;
    }

    /// <summary>The manifest version when it is one Chrome could have written; null otherwise.</summary>
    private static int? PlausibleManifestVersion(int? value) =>
        value is >= MinManifestVersion and <= MaxManifestVersion ? value : null;

    /// <summary>Trims and truncates; null for anything blank.</summary>
    private static string? Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
