using EndpointAgent.Core.Inventory.Chrome;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Tests.Inventory.Chrome;

/// <summary>
/// The rules that decide what the Chrome section carries -- which of two
/// records for one extension is believed, what counts as one profile, and what
/// the server will accept -- proven with fixtures rather than a real machine,
/// because another user's Chrome profile cannot be made to hold a chosen shape
/// on a CI agent.
/// </summary>
public sealed class ChromeInventoryNormalizerTests
{
    private const string AliceSid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
    private const string BobSid = "S-1-5-21-1000000000-2000000000-3000000000-1002";
    private const string Alice = @"CONTOSO\alice";
    private const string Bob = @"CONTOSO\bob";
    private const string WebStoreUpdateUrl = "https://clients2.google.com/service/update2/crx";

    private static readonly DateTimeOffset Installed = new(2025, 3, 14, 9, 26, 53, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A valid extension id -- 32 characters, each a-p -- distinct for each
    /// <paramref name="n"/>: the number written in base 16 with a-p as digits,
    /// which is exactly how Chrome derives real ids from a key's hash.
    /// </summary>
    private static string Id(int n)
    {
        var chars = new char[InventoryChromeExtension.MaxExtensionId];
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            chars[i] = (char)('a' + n % 16);
            n /= 16;
        }

        return new string(chars);
    }

    private static DiscoveredChromeInstallation Install(
        string? version = "131.0.6778.86",
        string scope = "Machine",
        string? user = null,
        string? executablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        string? architecture = "x64",
        string? channel = "stable",
        string? updaterVersion = "1.3.36.372",
        DateTimeOffset? lastUpdateCheck = null) =>
        new(version, executablePath, architecture, channel, scope, user, updaterVersion, lastUpdateCheck);

    private static DiscoveredChromeProfile Profile(
        string sid = AliceSid,
        string key = "Default",
        string? account = Alice,
        string? name = "Alice",
        string? path = null,
        bool? isManaged = false,
        DateTimeOffset? lastActiveAt = null,
        IReadOnlyList<ChromeExtensionEntry>? secure = null,
        IReadOnlyList<ChromeExtensionEntry>? preferences = null) =>
        new(
            sid,
            account,
            new ChromeProfileInfo(key, name, isManaged, lastActiveAt),
            path ?? $@"C:\Users\alice\AppData\Local\Google\Chrome\User Data\{key}",
            secure ?? [],
            preferences ?? []);

    private static ChromeExtensionEntry Ext(
        string? id = null,
        string? name = "Contoso Helper",
        string? version = "2.4.1",
        int? manifestVersion = 3,
        bool? enabled = true,
        int? location = 1,
        bool? fromWebStore = true,
        string? updateUrl = WebStoreUpdateUrl,
        DateTimeOffset? installedAt = null,
        DateTimeOffset? updatedAt = null) =>
        new(id ?? Id(0), name, version, manifestVersion, enabled, location, fromWebStore, updateUrl, installedAt, updatedAt);

    // ---- Status -------------------------------------------------------------

    [Fact]
    public void Nothing_found_is_reported_as_not_installed()
    {
        var result = ChromeInventoryNormalizer.Normalize(null, [], enumerationFailed: false);

        result.Status.ShouldBe("NotInstalled");
        result.Installation.ShouldBeNull();
        result.Profiles.ShouldBeEmpty();
    }

    [Fact]
    public void An_installation_with_profiles_is_available()
    {
        var result = ChromeInventoryNormalizer.Normalize(Install(), [Profile()], enumerationFailed: false);

        result.Status.ShouldBe("Available");
        result.Installation.ShouldNotBeNull();
        result.Profiles.ShouldHaveSingleItem();
    }

    [Fact]
    public void An_installation_with_no_profiles_is_available()
    {
        // Freshly installed, never launched: no User Data yet.
        var result = ChromeInventoryNormalizer.Normalize(Install(), [], enumerationFailed: false);

        result.Status.ShouldBe("Available");
        result.Profiles.ShouldBeEmpty();
    }

    /// <summary>
    /// Chrome's uninstaller leaves User Data behind unless the person ticks
    /// "also delete your browsing data" (unticked by default), so this is the
    /// state of every machine Chrome was ever removed from. The status is about
    /// the browser, which is gone; the profiles and the extensions recorded in
    /// them are still facts an administrator wants to know about, so they are
    /// carried under "NotInstalled" rather than making the machine "Available".
    /// </summary>
    [Fact]
    public void Profiles_without_an_installation_are_not_installed_but_still_carried()
    {
        var result = ChromeInventoryNormalizer.Normalize(null, [Profile(secure: [Ext()])], enumerationFailed: false);

        result.Status.ShouldBe("NotInstalled");
        result.Installation.ShouldBeNull();
        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem();
    }

    /// <summary>
    /// The status says whether the collector found Chrome, not whether every
    /// detail survived normalization: an installation whose version could not
    /// be read is still an installation. "NotInstalled" would be a false
    /// statement; "Available" with an empty body is merely an incomplete one.
    /// </summary>
    [Fact]
    public void An_installation_without_a_readable_version_is_still_available()
    {
        var result = ChromeInventoryNormalizer.Normalize(Install(version: null), [], enumerationFailed: false);

        result.Status.ShouldBe("Available");
        result.Installation.ShouldBeNull();
    }

    /// <summary>
    /// Error is a statement about the enumeration, not about the data: what was
    /// read before the failure is real and is carried, because a partial section
    /// the server can flag beats an empty one it cannot tell from a clean machine.
    /// </summary>
    [Fact]
    public void An_enumeration_failure_is_an_error_that_still_carries_what_was_found()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(secure: [Ext()])], enumerationFailed: true);

        result.Status.ShouldBe("Error");
        result.Installation.ShouldNotBeNull().Version.ShouldBe("131.0.6778.86");
        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem();
    }

    [Fact]
    public void An_enumeration_failure_with_nothing_found_is_an_error_rather_than_not_installed()
    {
        var result = ChromeInventoryNormalizer.Normalize(null, [], enumerationFailed: true);

        result.Status.ShouldBe("Error");
    }

    [Fact]
    public void Every_status_the_normalizer_emits_is_one_the_contract_allows()
    {
        var statuses = new[]
        {
            ChromeInventoryNormalizer.Normalize(null, [], enumerationFailed: false).Status,
            ChromeInventoryNormalizer.Normalize(Install(), [], enumerationFailed: false).Status,
            ChromeInventoryNormalizer.Normalize(null, [], enumerationFailed: true).Status,
        };

        statuses.ShouldAllBe(s => InventoryChrome.Statuses.Contains(s));
        statuses.Distinct().Count().ShouldBe(3, "the three cases are the three statuses");
    }

    // ---- Installation ------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_installation_without_a_version_is_not_reported(string? version)
    {
        // The contract requires a version; the profiles are unaffected.
        var result = ChromeInventoryNormalizer.Normalize(Install(version: version), [Profile()], enumerationFailed: false);

        result.Installation.ShouldBeNull();
        result.Status.ShouldBe("Available");
        result.Profiles.ShouldHaveSingleItem();
    }

    /// <summary>
    /// The Agent API rejects an entire inventory report if one field of a
    /// section it validates is over length; it does so for the software rows
    /// today and will for this section once the server ingests it. Clamping
    /// here means an odd installation costs its own detail, not the machine's
    /// whole report.
    /// </summary>
    [Fact]
    public void Installation_strings_are_clamped_to_the_contract()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(
                version: new string('v', 100),
                executablePath: @"C:\" + new string('p', 600),
                architecture: new string('a', 40),
                channel: new string('c', 40),
                scope: "User",
                user: new string('u', 300),
                updaterVersion: new string('w', 100)),
            [],
            enumerationFailed: false);

        var installation = result.Installation.ShouldNotBeNull();
        installation.Version.Length.ShouldBe(InventoryChromeInstallation.MaxVersion);
        installation.ExecutablePath!.Length.ShouldBe(InventoryChromeInstallation.MaxExecutablePath);
        installation.Architecture!.Length.ShouldBe(InventoryChromeInstallation.MaxArchitecture);
        installation.Channel!.Length.ShouldBe(InventoryChromeInstallation.MaxChannel);
        installation.InstalledForUser!.Length.ShouldBe(InventoryChromeInstallation.MaxInstalledForUser);
        installation.UpdaterVersion!.Length.ShouldBe(InventoryChromeInstallation.MaxUpdaterVersion);
    }

    [Fact]
    public void Blank_installation_metadata_is_absent_rather_than_whitespace()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(version: "  131.0.6778.86 ", executablePath: "  ", architecture: "", channel: "\t", updaterVersion: " "),
            [],
            enumerationFailed: false);

        var installation = result.Installation.ShouldNotBeNull();
        installation.Version.ShouldBe("131.0.6778.86");
        installation.ExecutablePath.ShouldBeNull();
        installation.Architecture.ShouldBeNull();
        installation.Channel.ShouldBeNull();
        installation.UpdaterVersion.ShouldBeNull();
    }

    /// <summary>
    /// The collector is the only producer of the scope, so anything outside the
    /// contract's set is our own bug, and "Machine" is the safer reading: a
    /// machine-wide install reaches every user, so understating it is the worse
    /// mistake. The set is ordinal, so a mis-cased value is outside it too.
    /// </summary>
    [Theory]
    [InlineData("Machine", "Machine")]
    [InlineData("User", "User")]
    [InlineData("  User  ", "User")]
    [InlineData("Everyone", "Machine")]
    [InlineData("user", "Machine")]
    [InlineData("", "Machine")]
    public void A_scope_outside_the_contract_is_read_as_machine_wide(string scope, string expected)
    {
        var result = ChromeInventoryNormalizer.Normalize(Install(scope: scope), [], enumerationFailed: false);

        result.Installation.ShouldNotBeNull().InstallationScope.ShouldBe(expected);
        InventoryChromeInstallation.InstallationScopes.ShouldContain(expected);
    }

    [Fact]
    public void A_per_user_installation_carries_its_user_and_a_machine_wide_one_never_does()
    {
        var perUser = ChromeInventoryNormalizer.Normalize(
            Install(scope: "User", user: Alice, executablePath: @"C:\Users\alice\AppData\Local\Google\Chrome\Application\chrome.exe"),
            [],
            enumerationFailed: false);
        var machine = ChromeInventoryNormalizer.Normalize(Install(scope: "Machine", user: Alice), [], enumerationFailed: false);

        perUser.Installation.ShouldNotBeNull().InstalledForUser.ShouldBe(Alice);
        // Attribution would be a lie: an all-users install belongs to nobody.
        machine.Installation.ShouldNotBeNull().InstalledForUser.ShouldBeNull();
    }

    [Fact]
    public void The_last_update_check_is_carried_through()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 20, 6, 30, 0, TimeSpan.Zero);

        var result = ChromeInventoryNormalizer.Normalize(Install(lastUpdateCheck: checkedAt), [], enumerationFailed: false);

        result.Installation.ShouldNotBeNull().LastUpdateCheck.ShouldBe(checkedAt);
        ChromeInventoryNormalizer.Normalize(Install(), [], enumerationFailed: false)
            .Installation.ShouldNotBeNull().LastUpdateCheck.ShouldBeNull();
    }

    // ---- Profiles ----------------------------------------------------------

    [Fact]
    public void The_same_profile_seen_twice_is_one_row_and_the_first_wins()
    {
        // NTFS is case-insensitive, so two spellings of one directory are one profile.
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [
                Profile(sid: AliceSid, key: "Default", name: "Alice"),
                Profile(sid: AliceSid.ToLowerInvariant(), key: "default", name: "Alice (again)"),
            ],
            enumerationFailed: false);

        var profile = result.Profiles.ShouldHaveSingleItem();
        profile.UserSid.ShouldBe(AliceSid);
        profile.ProfileKey.ShouldBe("Default");
        profile.ProfileName.ShouldBe("Alice");
    }

    [Fact]
    public void The_same_profile_key_under_two_users_is_two_profiles()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [Profile(sid: AliceSid, account: Alice), Profile(sid: BobSid, account: Bob, path: @"C:\Users\bob\AppData\Local\Google\Chrome\User Data\Default")],
            enumerationFailed: false);

        result.Profiles.Count.ShouldBe(2);
        result.Profiles.Select(p => p.UserSid).ShouldBe([AliceSid, BobSid]);
        result.Profiles.ShouldAllBe(p => p.ProfileKey == "Default");
    }

    [Fact]
    public void Two_profiles_of_one_user_are_two_rows()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(key: "Default"), Profile(key: "Profile 1", name: "Work")], enumerationFailed: false);

        result.Profiles.Select(p => p.ProfileKey).ShouldBe(["Default", "Profile 1"]);
    }

    [Theory]
    [InlineData("", "Default")]
    [InlineData("   ", "Default")]
    [InlineData(AliceSid, "")]
    [InlineData(AliceSid, " ")]
    public void A_profile_without_a_user_or_a_key_is_dropped(string sid, string key)
    {
        // Nothing to identify it by; the other profiles are unaffected.
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(sid: sid, key: key), Profile(sid: BobSid, account: Bob)], enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().UserSid.ShouldBe(BobSid);
    }

    [Fact]
    public void A_profile_without_a_path_is_dropped()
    {
        // The path is required on the wire; a blank one would cost the whole report.
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(key: "Default", path: "  "), Profile(key: "Profile 1")], enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().ProfileKey.ShouldBe("Profile 1");
    }

    [Fact]
    public void Profiles_keep_their_discovery_order()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [Profile(key: "Profile 9"), Profile(key: "Default"), Profile(key: "Profile 2")],
            enumerationFailed: false);

        result.Profiles.Select(p => p.ProfileKey).ShouldBe(["Profile 9", "Default", "Profile 2"]);
    }

    [Fact]
    public void The_profile_list_is_capped_and_keeps_the_first()
    {
        var many = Enumerable.Range(0, InventoryChrome.MaxProfiles + 10)
            .Select(i => Profile(key: $"Profile {i}"))
            .ToArray();

        var result = ChromeInventoryNormalizer.Normalize(Install(), many, enumerationFailed: false);

        result.Profiles.Count.ShouldBe(InventoryChrome.MaxProfiles);
        result.Profiles.Select(p => p.ProfileKey)
            .ShouldBe(Enumerable.Range(0, InventoryChrome.MaxProfiles).Select(i => $"Profile {i}"));
    }

    [Fact]
    public void Profile_strings_are_clamped_to_the_contract()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            null,
            [
                Profile(
                    sid: "S-1-5-21-" + new string('1', 300),
                    account: new string('u', 300),
                    key: new string('k', 100),
                    name: new string('n', 300),
                    path: @"C:\" + new string('p', 600)),
            ],
            enumerationFailed: false);

        var profile = result.Profiles.ShouldHaveSingleItem();
        profile.UserSid.Length.ShouldBe(InventoryChromeProfile.MaxUserSid);
        profile.UserAccount!.Length.ShouldBe(InventoryChromeProfile.MaxUserAccount);
        profile.ProfileKey.Length.ShouldBe(InventoryChromeProfile.MaxProfileKey);
        profile.ProfileName!.Length.ShouldBe(InventoryChromeProfile.MaxProfileName);
        profile.ProfilePath.Length.ShouldBe(InventoryChromeProfile.MaxProfilePath);
    }

    [Fact]
    public void Profile_facts_are_carried_through()
    {
        var lastActive = new DateTimeOffset(2026, 9, 18, 17, 45, 12, TimeSpan.Zero);

        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [Profile(key: "Profile 3", name: "  Work  ", isManaged: true, lastActiveAt: lastActive)],
            enumerationFailed: false);

        var profile = result.Profiles.ShouldHaveSingleItem();
        profile.UserSid.ShouldBe(AliceSid);
        profile.UserAccount.ShouldBe(Alice);
        profile.ProfileKey.ShouldBe("Profile 3");
        profile.ProfileName.ShouldBe("Work");
        profile.ProfilePath.ShouldBe(@"C:\Users\alice\AppData\Local\Google\Chrome\User Data\Profile 3");
        profile.IsManaged.ShouldBe(true);
        profile.LastActiveAt.ShouldBe(lastActive);
    }

    [Fact]
    public void Unrecorded_profile_facts_stay_null_rather_than_defaulting()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(name: null, account: null, isManaged: null, lastActiveAt: null)], enumerationFailed: false);

        var profile = result.Profiles.ShouldHaveSingleItem();
        profile.ProfileName.ShouldBeNull();
        profile.UserAccount.ShouldBeNull();
        profile.IsManaged.ShouldBeNull();
        profile.LastActiveAt.ShouldBeNull();
    }

    [Fact]
    public void A_profile_whose_preference_files_were_unreadable_still_appears_with_no_extensions()
    {
        var result = ChromeInventoryNormalizer.Normalize(Install(), [Profile(secure: [], preferences: [])], enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldBeEmpty();
    }

    // ---- Extensions --------------------------------------------------------

    /// <summary>
    /// Secure Preferences is the file Chrome protects against tampering and
    /// trusts for install state, so where the two files disagree its record is
    /// the one reported. An extension only Preferences knows about is still
    /// reported: it is a fact, just a less trusted one.
    /// </summary>
    [Fact]
    public void Secure_preferences_wins_over_preferences_for_the_same_extension()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [
                Profile(
                    secure: [Ext(Id(1), version: "2.0.0", enabled: true)],
                    preferences: [Ext(Id(1), version: "1.0.0", enabled: false), Ext(Id(2), name: "Preferences Only")]),
            ],
            enumerationFailed: false);

        var extensions = result.Profiles.ShouldHaveSingleItem().Extensions;
        extensions.Count.ShouldBe(2);
        extensions[0].ExtensionId.ShouldBe(Id(1));
        extensions[0].Version.ShouldBe("2.0.0");
        extensions[0].Enabled.ShouldBe(true);
        extensions[1].ExtensionId.ShouldBe(Id(2));
        extensions[1].Name.ShouldBe("Preferences Only");
    }

    [Fact]
    public void The_same_extension_seen_twice_in_one_file_is_one_row_and_the_first_wins()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [Profile(secure: [Ext(Id(1), name: "First"), Ext(Id(1), name: "Second")])],
            enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem().Name.ShouldBe("First");
    }

    [Theory]
    [InlineData("abcdefghijklmnopabcdefghijklmno")]     // 31 characters
    [InlineData("abcdefghijklmnopabcdefghijklmnopa")]   // 33 characters
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")]    // upper case
    [InlineData("abcdefghijklmnopabcdefghijklmnoq")]    // a letter past p
    [InlineData(" abcdefghijklmnopabcdefghijklmno")]    // whitespace is not a letter
    [InlineData("")]
    public void An_entry_whose_id_is_not_shaped_like_one_is_dropped(string id)
    {
        // The Agent API will apply the same check to the whole report once it
        // ingests the section, as it does to software rows today; letting one
        // through would then cost every other section.
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(secure: [Ext(id), Ext(Id(1))])], enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem().ExtensionId.ShouldBe(Id(1));
    }

    /// <summary>
    /// Chromium's ManifestLocation numbers, pinned one by one. Zero is Chrome's
    /// own "invalid", null is a record with no number, and anything past the
    /// table is a renumbering to investigate -- all three are Unknown.
    /// </summary>
    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(0, "Unknown")]
    [InlineData(1, "Internal")]
    [InlineData(2, "ExternalPref")]
    [InlineData(3, "ExternalRegistry")]
    [InlineData(4, "Unpacked")]
    [InlineData(5, "Component")]
    [InlineData(6, "ExternalPrefDownload")]
    [InlineData(7, "ExternalPolicyDownload")]
    [InlineData(8, "CommandLine")]
    [InlineData(9, "ExternalPolicy")]
    [InlineData(10, "ExternalComponent")]
    [InlineData(11, "Unknown")]
    public void Every_chrome_location_number_maps_to_its_wire_name(int? location, string expected)
    {
        ChromeInventoryNormalizer.InstallTypeOf(location).ShouldBe(expected);

        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(secure: [Ext(location: location)])], enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem().InstallType.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void Only_the_two_policy_locations_are_managed(int? location)
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(secure: [Ext(location: location)])], enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem().IsManaged
            .ShouldBe(location is 7 or 9, $"location {location?.ToString() ?? "null"}");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(100, null)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(99, 99)]
    public void A_manifest_version_is_kept_only_when_plausible(int? given, int? expected)
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(secure: [Ext(manifestVersion: given)])], enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem().ManifestVersion.ShouldBe(expected);
    }

    [Fact]
    public void Extension_strings_are_clamped_to_the_contract()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [Profile(secure: [Ext(name: new string('n', 300), version: new string('v', 100), updateUrl: "https://" + new string('u', 600))])],
            enumerationFailed: false);

        var extension = result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem();
        extension.Name!.Length.ShouldBe(InventoryChromeExtension.MaxName);
        extension.Version!.Length.ShouldBe(InventoryChromeExtension.MaxVersion);
        extension.UpdateUrl!.Length.ShouldBe(InventoryChromeExtension.MaxUpdateUrl);
        extension.InstallType.Length.ShouldBeLessThanOrEqualTo(InventoryChromeExtension.MaxInstallType);
    }

    [Fact]
    public void Blank_extension_metadata_is_absent_rather_than_whitespace()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(secure: [Ext(name: "  ", version: "", updateUrl: "\t")])], enumerationFailed: false);

        var extension = result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem();
        extension.Name.ShouldBeNull();
        extension.Version.ShouldBeNull();
        extension.UpdateUrl.ShouldBeNull();
    }

    /// <summary>
    /// Some records have no manifest block at all (an extension mid-install, or
    /// one Chrome has blocklisted). It is still an extension Chrome knows about.
    /// </summary>
    [Fact]
    public void An_entry_without_a_manifest_is_still_an_entry()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [Profile(secure: [Ext(Id(5), name: null, version: null, manifestVersion: null, updateUrl: null, enabled: null, fromWebStore: null)])],
            enumerationFailed: false);

        var extension = result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem();
        extension.ExtensionId.ShouldBe(Id(5));
        extension.Name.ShouldBeNull();
        extension.Version.ShouldBeNull();
        extension.ManifestVersion.ShouldBeNull();
        extension.Enabled.ShouldBeNull();
        extension.FromWebStore.ShouldBeNull();
        extension.UpdateUrl.ShouldBeNull();
        extension.InstallType.ShouldBe("Internal");
    }

    [Fact]
    public void Extension_facts_are_carried_through_unchanged()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [
                Profile(secure:
                [
                    Ext(Id(7), name: "Contoso Policy Extension", version: "5.1.0", manifestVersion: 3, enabled: false,
                        location: 9, fromWebStore: false, updateUrl: "https://updates.example.com/crx",
                        installedAt: Installed, updatedAt: Updated),
                ]),
            ],
            enumerationFailed: false);

        var extension = result.Profiles.ShouldHaveSingleItem().Extensions.ShouldHaveSingleItem();
        extension.ShouldBe(new InventoryChromeExtension(
            Id(7), "Contoso Policy Extension", "5.1.0", 3, false, "ExternalPolicy", true, false,
            "https://updates.example.com/crx", Installed, Updated));
    }

    [Fact]
    public void Extensions_keep_their_discovery_order_with_secure_preferences_first()
    {
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [Profile(secure: [Ext(Id(3)), Ext(Id(1))], preferences: [Ext(Id(2)), Ext(Id(0))])],
            enumerationFailed: false);

        result.Profiles.ShouldHaveSingleItem().Extensions.Select(e => e.ExtensionId)
            .ShouldBe([Id(3), Id(1), Id(2), Id(0)]);
    }

    [Fact]
    public void The_extension_list_is_capped_and_keeps_the_first()
    {
        // Enough in Secure Preferences to nearly fill the list, and enough more in
        // Preferences to overflow it: the cap applies to the merged list.
        var secure = Enumerable.Range(0, InventoryChromeProfile.MaxExtensions - 10).Select(i => Ext(Id(i))).ToArray();
        var preferences = Enumerable.Range(InventoryChromeProfile.MaxExtensions - 10, 30).Select(i => Ext(Id(i))).ToArray();

        var result = ChromeInventoryNormalizer.Normalize(
            Install(), [Profile(secure: secure, preferences: preferences)], enumerationFailed: false);

        var extensions = result.Profiles.ShouldHaveSingleItem().Extensions;
        extensions.Count.ShouldBe(InventoryChromeProfile.MaxExtensions);
        extensions.Select(e => e.ExtensionId).ShouldBe(Enumerable.Range(0, InventoryChromeProfile.MaxExtensions).Select(Id));
    }

    [Fact]
    public void Each_profile_has_its_own_extension_list()
    {
        // The same extension in two profiles is two installations; de-duplication
        // is per profile, never across them.
        var result = ChromeInventoryNormalizer.Normalize(
            Install(),
            [
                Profile(key: "Default", secure: [Ext(Id(1)), Ext(Id(2))]),
                Profile(key: "Profile 1", secure: [Ext(Id(1))]),
            ],
            enumerationFailed: false);

        result.Profiles.Count.ShouldBe(2);
        result.Profiles[0].Extensions.Select(e => e.ExtensionId).ShouldBe([Id(1), Id(2)]);
        result.Profiles[1].Extensions.Select(e => e.ExtensionId).ShouldBe([Id(1)]);
    }

    [Fact]
    public void An_empty_machine_produces_an_empty_section_rather_than_throwing()
    {
        var result = ChromeInventoryNormalizer.Normalize(null, [], enumerationFailed: false);

        result.Profiles.ShouldBeEmpty();
        result.Installation.ShouldBeNull();
    }
}
