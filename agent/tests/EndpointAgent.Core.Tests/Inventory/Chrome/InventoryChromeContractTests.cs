using System.Reflection;
using System.Text.Json;
using EndpointAgent.Core.Inventory.Chrome;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Tests.Inventory.Chrome;

/// <summary>
/// The Chrome section on the wire: a full report carrying one serializes and
/// binds back, a report from an agent that predates the section still binds,
/// and the names the normalizer emits are the names the contract allows.
/// </summary>
public sealed class InventoryChromeContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string AliceSid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
    private const string BobSid = "S-1-5-21-1000000000-2000000000-3000000000-1002";

    // Invented ids of the right shape: 32 letters, each a-p.
    private const string HelperId = "abcdefghijklmnopabcdefghijklmnop";
    private const string PolicyId = "ponmlkjihgfedcbaponmlkjihgfedcba";
    private const string ComponentId = "aaaabbbbccccddddeeeeffffgggghhhh";

    /// <summary>
    /// One shared empty list, so two profiles compared after
    /// <c>with { Extensions = NoExtensions }</c> are equal: record equality
    /// compares a list by reference, and the extensions are compared separately.
    /// </summary>
    private static readonly IReadOnlyList<InventoryChromeExtension> NoExtensions = [];

    private static InventoryChrome FullChrome() => new(
        "Available",
        new InventoryChromeInstallation(
            "131.0.6778.86",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "x64",
            "stable",
            "Machine",
            null,
            "1.3.36.372",
            new DateTimeOffset(2026, 9, 20, 6, 30, 0, TimeSpan.Zero)),
        [
            new InventoryChromeProfile(
                AliceSid,
                @"CONTOSO\alice",
                "Default",
                "Alice",
                @"C:\Users\alice\AppData\Local\Google\Chrome\User Data\Default",
                false,
                new DateTimeOffset(2026, 9, 18, 17, 45, 12, TimeSpan.Zero),
                [
                    new InventoryChromeExtension(
                        HelperId, "Contoso Helper", "2.4.1", 3, true, "Internal", false, true,
                        "https://clients2.google.com/service/update2/crx",
                        new DateTimeOffset(2025, 3, 14, 9, 26, 53, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)),
                    new InventoryChromeExtension(
                        PolicyId, "Contoso Policy Extension", "5.1.0", 3, false, "ExternalPolicy", true, false,
                        "https://updates.example.com/crx", null, null),
                    new InventoryChromeExtension(
                        ComponentId, null, null, null, null, "Component", false, null, null, null, null),
                ]),
            new InventoryChromeProfile(
                BobSid,
                BobSid,
                "Profile 1",
                null,
                @"C:\Users\bob\AppData\Local\Google\Chrome\User Data\Profile 1",
                null,
                null,
                []),
        ]);

    private static InventoryReport FullReport() => new(
        new InventoryHardware(
            "SN-0001", "Contoso", "Model 1", "Contoso CPU", 4, 8, 17_179_869_184,
            [new InventoryDisk("C:", "NTFS", 512_000_000_000, 128_000_000_000)]),
        [new InventoryNetworkInterface("Ethernet", "00-11-22-33-44-55", ["192.0.2.10"], true)],
        @"CONTOSO\alice",
        new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
        Chrome: FullChrome());

    [Fact]
    public void A_full_report_with_a_chrome_section_round_trips()
    {
        var report = FullReport();

        var json = JsonSerializer.Serialize(report, Web);
        var back = JsonSerializer.Deserialize<InventoryReport>(json, Web).ShouldNotBeNull();

        var chrome = back.Chrome.ShouldNotBeNull();
        var expected = report.Chrome!;

        chrome.Status.ShouldBe(expected.Status);
        chrome.Installation.ShouldBe(expected.Installation, "every installation field survives");
        chrome.Profiles.Count.ShouldBe(expected.Profiles.Count);

        foreach (var (actualProfile, expectedProfile) in chrome.Profiles.Zip(expected.Profiles))
        {
            (actualProfile with { Extensions = NoExtensions })
                .ShouldBe(expectedProfile with { Extensions = NoExtensions }, "every scalar profile field survives");
            actualProfile.Extensions.Count.ShouldBe(expectedProfile.Extensions.Count);

            foreach (var (actualExtension, expectedExtension) in actualProfile.Extensions.Zip(expectedProfile.Extensions))
            {
                actualExtension.ShouldBe(expectedExtension);
            }
        }

        // The rest of the report is untouched by the new section.
        back.CollectedAt.ShouldBe(report.CollectedAt);
        back.LoggedOnUser.ShouldBe(report.LoggedOnUser);
        back.Hardware.SerialNumber.ShouldBe("SN-0001");
    }

    [Fact]
    public void The_wire_names_are_camel_case()
    {
        var json = JsonSerializer.Serialize(FullReport(), Web);

        foreach (var name in new[]
        {
            "\"chrome\":", "\"status\":", "\"installation\":", "\"profiles\":",
            "\"executablePath\":", "\"installationScope\":", "\"installedForUser\":", "\"updaterVersion\":", "\"lastUpdateCheck\":",
            "\"userSid\":", "\"userAccount\":", "\"profileKey\":", "\"profileName\":", "\"profilePath\":", "\"lastActiveAt\":",
            "\"extensions\":", "\"extensionId\":", "\"manifestVersion\":", "\"enabled\":", "\"installType\":",
            "\"isManaged\":", "\"fromWebStore\":", "\"updateUrl\":", "\"installedAt\":", "\"updatedAt\":",
        })
        {
            json.ShouldContain(name);
        }
    }

    /// <summary>What an agent older than 1.13.0 sends: no chrome property at all. It binds with the section null.</summary>
    [Fact]
    public void A_report_from_an_older_agent_binds_with_chrome_absent()
    {
        const string json = """
            {"hardware":{"serialNumber":"SN-0001","manufacturer":"Contoso","model":"Model 1","cpuName":"Contoso CPU",
                         "cpuPhysicalCores":4,"cpuLogicalProcessors":8,"totalMemoryBytes":17179869184,"disks":[]},
             "networkInterfaces":[],"loggedOnUser":null,"collectedAt":"2026-09-21T08:00:00+00:00",
             "software":[],"bitLocker":{"status":"Available","volumes":[]}}
            """;

        var report = JsonSerializer.Deserialize<InventoryReport>(json, Web).ShouldNotBeNull();

        report.Chrome.ShouldBeNull();
        report.BitLocker.ShouldNotBeNull().Status.ShouldBe("Available");
        report.Hardware.SerialNumber.ShouldBe("SN-0001");
    }

    /// <summary>A machine without Chrome: the section is present, says so, and carries nothing.</summary>
    [Fact]
    public void A_not_installed_section_binds_with_no_installation_and_no_profiles()
    {
        const string json = """{"status":"NotInstalled","installation":null,"profiles":[]}""";

        var chrome = JsonSerializer.Deserialize<InventoryChrome>(json, Web).ShouldNotBeNull();

        chrome.Status.ShouldBe("NotInstalled");
        chrome.Installation.ShouldBeNull();
        chrome.Profiles.ShouldBeEmpty();
    }

    [Fact]
    public void The_statuses_are_exactly_the_documented_ones()
    {
        InventoryChrome.Statuses.SetEquals(["Available", "NotInstalled", "Error"]).ShouldBeTrue();
        InventoryChrome.Statuses.ShouldAllBe(s => s.Length <= InventoryChrome.MaxStatus);
    }

    [Fact]
    public void The_install_types_are_exactly_the_documented_ones()
    {
        InventoryChromeExtension.InstallTypes.SetEquals(
        [
            "Internal", "ExternalPref", "ExternalRegistry", "Unpacked", "Component", "ExternalPrefDownload",
            "ExternalPolicyDownload", "CommandLine", "ExternalPolicy", "ExternalComponent", "Unknown",
        ]).ShouldBeTrue();
        InventoryChromeExtension.InstallTypes.ShouldAllBe(t => t.Length <= InventoryChromeExtension.MaxInstallType);
    }

    [Fact]
    public void The_installation_scopes_are_exactly_machine_and_user()
    {
        InventoryChromeInstallation.InstallationScopes.SetEquals(["Machine", "User"]).ShouldBeTrue();
        InventoryChromeInstallation.InstallationScopes.ShouldAllBe(s => s.Length <= InventoryChromeInstallation.MaxInstallationScope);
    }

    [Theory]
    [InlineData("abcdefghijklmnopabcdefghijklmnop", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("pppppppppppppppppppppppppppppppp", true)]
    [InlineData("abcdefghijklmnopabcdefghijklmno", false)]     // 31 characters
    [InlineData("abcdefghijklmnopabcdefghijklmnopa", false)]   // 33 characters
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP", false)]    // upper case
    [InlineData("abcdefghijklmnopabcdefghijklmnoq", false)]    // a letter past p
    [InlineData("abcdefghijklmnopabcdefghijklmno1", false)]    // a digit
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_extension_id_is_exactly_thirty_two_letters_a_to_p(string? value, bool expected)
    {
        InventoryChromeExtension.IsValidExtensionId(value).ShouldBe(expected);
    }

    /// <summary>
    /// The normalizer names install types; the contract lists what the server
    /// accepts. They must agree in both directions: a name the normalizer emits
    /// but the contract lacks would reject every report from a machine with
    /// such an extension, and a name the contract lists but nothing emits is a
    /// value the console would document for nothing.
    /// </summary>
    [Fact]
    public void Every_install_type_the_normalizer_can_emit_is_one_the_contract_allows()
    {
        var emitted = Enumerable.Range(0, 12)
            .Select(location => ChromeInventoryNormalizer.InstallTypeOf(location))
            .Append(ChromeInventoryNormalizer.InstallTypeOf(null))
            .ToHashSet(StringComparer.Ordinal);

        emitted.ShouldAllBe(t => InventoryChromeExtension.InstallTypes.Contains(t));
        emitted.SetEquals(InventoryChromeExtension.InstallTypes).ShouldBeTrue("every documented name is reachable");
    }

    [Fact]
    public void The_limits_the_normalizer_clamps_to_are_the_contracts()
    {
        var profile = new DiscoveredChromeProfile(
            AliceSid,
            new string('u', 300),
            new ChromeProfileInfo(new string('k', 100), new string('n', 300), null, null),
            @"C:\" + new string('p', 600),
            Enumerable.Range(0, 300).Select(i => new ChromeExtensionEntry(
                new string((char)('a' + i % 16), 32), new string('e', 300), new string('v', 100), 3, true, 1, true,
                "https://" + new string('w', 600), null, null)).ToArray(),
            []);

        var section = ChromeInventoryNormalizer.Normalize(
            new DiscoveredChromeInstallation(new string('v', 100), @"C:\" + new string('x', 600), null, null, "Machine", null, null, null),
            [profile],
            enumerationFailed: false);

        section.Installation!.Version.Length.ShouldBe(InventoryChromeInstallation.MaxVersion);
        section.Installation.ExecutablePath!.Length.ShouldBe(InventoryChromeInstallation.MaxExecutablePath);

        var row = section.Profiles.ShouldHaveSingleItem();
        row.UserAccount!.Length.ShouldBe(InventoryChromeProfile.MaxUserAccount);
        row.ProfileKey.Length.ShouldBe(InventoryChromeProfile.MaxProfileKey);
        row.ProfileName!.Length.ShouldBe(InventoryChromeProfile.MaxProfileName);
        row.ProfilePath.Length.ShouldBe(InventoryChromeProfile.MaxProfilePath);

        // 300 entries but only 16 distinct ids, so the cap is not reached here;
        // what is pinned is that every string on every row is within the limit.
        row.Extensions.Count.ShouldBeLessThanOrEqualTo(InventoryChromeProfile.MaxExtensions);
        row.Extensions.ShouldAllBe(e => e.Name!.Length == InventoryChromeExtension.MaxName);
        row.Extensions.ShouldAllBe(e => e.Version!.Length == InventoryChromeExtension.MaxVersion);
        row.Extensions.ShouldAllBe(e => e.UpdateUrl!.Length == InventoryChromeExtension.MaxUpdateUrl);
    }

    /// <summary>
    /// Local State records the Google account signed in to each profile
    /// (user_name, gaia_*, hosted_domain). None of that has a place on the wire,
    /// by construction: the contract has no field for it, so the agent cannot
    /// carry it by accident and the server has nothing to redact.
    /// </summary>
    [Fact]
    public void The_profile_carries_no_google_account_field()
    {
        var names = typeof(InventoryChromeProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        names.ShouldNotBeEmpty();
        foreach (var forbidden in new[] { "Email", "Gaia", "UserName", "HostedDomain", "GoogleAccount" })
        {
            names.ShouldNotContain(n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
    }
}
