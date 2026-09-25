using EndpointPlatform.Domain.Chrome;

namespace EndpointPlatform.Domain.Tests.Chrome;

/// <summary>
/// What a Chrome extension row must be before it can exist, and which rows count
/// as extensions at all.
/// </summary>
/// <remarks>
/// The extension id is the key of the fleet-wide "which devices have extension X"
/// index, so a malformed one would make an extension unfindable rather than merely
/// mislabelled. The constructor is the narrowest place to refuse it.
/// </remarks>
public sealed class ChromeExtensionTests
{
    /// <summary>A well-formed id: 32 characters, each in a-p.</summary>
    private const string ValidId = "abcdefghijklmnopabcdefghijklmnop";

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static ChromeExtension Create(
        string extensionId = ValidId,
        string? name = "Example Extension",
        string? version = "2.4.1",
        int? manifestVersion = 3,
        bool? enabled = true,
        ChromeExtensionInstallType installType = ChromeExtensionInstallType.Internal,
        bool isManaged = false,
        bool? fromWebStore = true,
        string? updateUrl = "https://clients2.google.com/service/update2/crx",
        Guid? deviceId = null,
        Guid? profileId = null) =>
        new(
            deviceId ?? Guid.CreateVersion7(),
            profileId ?? Guid.CreateVersion7(),
            extensionId, name, version, manifestVersion, enabled, installType, isManaged, fromWebStore, updateUrl,
            installedAt: Now.AddDays(-30), updatedAt: Now.AddDays(-2), collectedAt: Now);

    [Fact]
    public void A_well_formed_extension_is_accepted()
    {
        var deviceId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();

        var extension = Create(deviceId: deviceId, profileId: profileId);

        extension.DeviceId.ShouldBe(deviceId);
        extension.ChromeProfileId.ShouldBe(profileId);
        extension.ExtensionId.ShouldBe(ValidId);
        extension.Name.ShouldBe("Example Extension");
        extension.Version.ShouldBe("2.4.1");
        extension.ManifestVersion.ShouldBe(3);
        extension.Enabled.ShouldBe(true);
        extension.InstallType.ShouldBe(ChromeExtensionInstallType.Internal);
        extension.IsManaged.ShouldBeFalse();
        extension.FromWebStore.ShouldBe(true);
        extension.UpdateUrl.ShouldBe("https://clients2.google.com/service/update2/crx");
        extension.InstalledAt.ShouldBe(Now.AddDays(-30));
        extension.ExtensionUpdatedAt.ShouldBe(Now.AddDays(-2));
        extension.CollectedAt.ShouldBe(Now);
    }

    [Fact]
    public void Unrecorded_optional_facts_stay_null_rather_than_defaulting()
    {
        var extension = Create(
            name: null, version: "  ", manifestVersion: null, enabled: null, fromWebStore: null, updateUrl: null);

        extension.Name.ShouldBeNull();
        extension.Version.ShouldBeNull("whitespace is not a version");
        extension.ManifestVersion.ShouldBeNull();
        extension.Enabled.ShouldBeNull("unknown is not disabled");
        extension.FromWebStore.ShouldBeNull();
        extension.UpdateUrl.ShouldBeNull();
    }

    // ---- the extension id --------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcdefghijklmnopabcdefghijklmno")] // 31
    [InlineData("abcdefghijklmnopabcdefghijklmnopa")] // 33
    [InlineData("abcdefghijklmnopabcdefghijklmnoq")] // q is outside a-p
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")] // Chrome ids are lower case
    [InlineData("abcdefghijklmnopabcdefghijklmno1")] // digits never appear
    [InlineData("abcdefghijklmnopabcdefghijklmno ")] // no trimming: 32 with a space is not an id
    public void An_invalid_extension_id_is_refused(string? extensionId)
    {
        ChromeExtension.IsValidExtensionId(extensionId).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => Create(extensionId: extensionId!));
    }

    [Theory]
    [InlineData(ValidId)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("pppppppppppppppppppppppppppppppp")]
    public void A_valid_extension_id_is_kept_exactly(string extensionId)
    {
        ChromeExtension.IsValidExtensionId(extensionId).ShouldBeTrue();
        Create(extensionId: extensionId).ExtensionId.ShouldBe(extensionId);
    }

    // ---- what counts as an extension ----------------------------------------

    /// <summary>
    /// Chrome's own built-ins are extensions to Chrome but not to anyone counting
    /// what was installed on a machine. Only the two component types are excluded;
    /// everything else, including unknown, is something the console should show.
    /// </summary>
    [Theory]
    [InlineData(ChromeExtensionInstallType.Component, true)]
    [InlineData(ChromeExtensionInstallType.ExternalComponent, true)]
    [InlineData(ChromeExtensionInstallType.Internal, false)]
    [InlineData(ChromeExtensionInstallType.ExternalPref, false)]
    [InlineData(ChromeExtensionInstallType.ExternalRegistry, false)]
    [InlineData(ChromeExtensionInstallType.Unpacked, false)]
    [InlineData(ChromeExtensionInstallType.ExternalPrefDownload, false)]
    [InlineData(ChromeExtensionInstallType.ExternalPolicyDownload, false)]
    [InlineData(ChromeExtensionInstallType.CommandLine, false)]
    [InlineData(ChromeExtensionInstallType.ExternalPolicy, false)]
    [InlineData(ChromeExtensionInstallType.Unknown, false)]
    public void Only_the_component_install_types_are_components(ChromeExtensionInstallType installType, bool expected)
    {
        Create(installType: installType).IsComponent.ShouldBe(expected);
    }

    [Fact]
    public void Every_install_type_is_covered_by_the_component_rule()
    {
        // Guards the theory above against a member added to the enum without a
        // decision on whether it is Chrome's own.
        Enum.GetValues<ChromeExtensionInstallType>().Length.ShouldBe(11);
    }

    // ---- guards ------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void A_manifest_version_outside_the_plausible_range_is_refused(int manifestVersion)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Create(manifestVersion: manifestVersion));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(99)]
    public void A_plausible_manifest_version_is_kept(int manifestVersion)
    {
        Create(manifestVersion: manifestVersion).ManifestVersion.ShouldBe(manifestVersion);
    }

    [Fact]
    public void Empty_owner_identifiers_are_refused()
    {
        Should.Throw<ArgumentException>(() => Create(deviceId: Guid.Empty));
        Should.Throw<ArgumentException>(() => Create(profileId: Guid.Empty));
    }

    /// <summary>
    /// The limits are the wire contract's <c>InventoryChromeExtension.Max*</c>
    /// constants, refused here again so no route but the Agent API can widen them.
    /// </summary>
    [Theory]
    [InlineData("name", 256)]
    [InlineData("version", 64)]
    [InlineData("updateUrl", 512)]
    public void A_value_over_the_contract_limit_is_refused_and_one_at_the_limit_is_kept(string field, int limit)
    {
        var atLimit = new string('a', limit);
        var overLimit = new string('a', limit + 1);

        Should.NotThrow(() => CreateWith(field, atLimit));
        Should.Throw<ArgumentException>(() => CreateWith(field, overLimit));
    }

    private static ChromeExtension CreateWith(string field, string value) => Create(
        name: field == "name" ? value : null,
        version: field == "version" ? value : null,
        updateUrl: field == "updateUrl" ? value : null);
}
