using EndpointPlatform.Domain.Chrome;

namespace EndpointPlatform.Domain.Tests.Chrome;

/// <summary>
/// What a Chrome extension rule must be before it can exist, and how re-deciding
/// one behaves.
/// </summary>
/// <remarks>
/// The extension id is the enforcement key the agent writes Chrome policy under,
/// so a malformed one would not merely mislabel a row -- it would put a rule on
/// the wire that Chrome ignores while the console shows it as in force. The
/// constructor is the narrowest place to refuse it, whatever future caller
/// forgets to validate first.
/// </remarks>
public sealed class ChromeExtensionRuleTests
{
    /// <summary>A well-formed id: 32 characters, each in a-p.</summary>
    private const string ValidId = "abcdefghijklmnopabcdefghijklmnop";

    private static ChromeExtensionRule Create(
        string extensionId = ValidId,
        ChromeExtensionRuleMode mode = ChromeExtensionRuleMode.Blocked,
        string? displayName = "Free VPN",
        Guid? deviceId = null) =>
        new(deviceId ?? Guid.CreateVersion7(), extensionId, mode, displayName);

    [Fact]
    public void A_well_formed_rule_is_accepted()
    {
        var deviceId = Guid.CreateVersion7();

        var rule = Create(deviceId: deviceId);

        rule.DeviceId.ShouldBe(deviceId);
        rule.ExtensionId.ShouldBe(ValidId);
        rule.Mode.ShouldBe(ChromeExtensionRuleMode.Blocked);
        rule.DisplayName.ShouldBe("Free VPN");
    }

    [Fact]
    public void The_label_is_optional_and_whitespace_is_no_label()
    {
        Create(displayName: null).DisplayName.ShouldBeNull();
        Create(displayName: "   ").DisplayName.ShouldBeNull();
        Create(displayName: "  Ad Blocker  ").DisplayName.ShouldBe("Ad Blocker", "labels are trimmed");
    }

    [Fact]
    public void A_label_longer_than_the_console_column_is_refused()
    {
        Should.Throw<ArgumentException>(() => Create(displayName: new string('x', 257)));
        Create(displayName: new string('x', 256)).DisplayName!.Length.ShouldBe(256);
    }

    // ---- the extension id --------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcdefghijklmnopabcdefghijklmno")] // 31
    [InlineData("abcdefghijklmnopabcdefghijklmnopq")] // 33
    [InlineData("Abcdefghijklmnopabcdefghijklmnop")] // uppercase
    [InlineData("qbcdefghijklmnopabcdefghijklmnop")] // q is past p
    [InlineData("abcdefghijklmno1abcdefghijklmnop")] // digit
    [InlineData(" bcdefghijklmnopabcdefghijklmnop")] // space
    public void A_malformed_extension_id_cannot_become_a_rule(string? extensionId)
    {
        Should.Throw<ArgumentException>(() => Create(extensionId: extensionId!));
    }

    /// <summary>
    /// The rule restated here is <see cref="ChromeExtension.IsValidExtensionId"/>,
    /// the same check the inventory rows use, so the two columns can never hold
    /// differently-shaped ids.
    /// </summary>
    [Fact]
    public void The_id_rule_is_the_inventory_rows_own()
    {
        ChromeExtension.IsValidExtensionId(ValidId).ShouldBeTrue();
        Create(extensionId: ValidId).ExtensionId.ShouldBe(ValidId);
    }

    // ---- the mode ----------------------------------------------------------

    [Fact]
    public void A_mode_outside_the_enum_cannot_become_a_rule()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Create(mode: (ChromeExtensionRuleMode)99));
    }

    [Theory]
    [InlineData(ChromeExtensionRuleMode.ForceInstalled)]
    [InlineData(ChromeExtensionRuleMode.Blocked)]
    [InlineData(ChromeExtensionRuleMode.Allowed)]
    [InlineData(ChromeExtensionRuleMode.Removed)]
    public void Every_defined_mode_is_accepted(ChromeExtensionRuleMode mode)
    {
        Create(mode: mode).Mode.ShouldBe(mode);
    }

    // ---- re-deciding (the upsert half) --------------------------------------

    [Fact]
    public void Applying_a_new_decision_changes_the_mode()
    {
        var rule = Create(mode: ChromeExtensionRuleMode.Allowed);

        rule.Apply(ChromeExtensionRuleMode.Removed, displayName: "Free VPN");

        rule.Mode.ShouldBe(ChromeExtensionRuleMode.Removed);
    }

    [Fact]
    public void Applying_without_a_label_keeps_the_label_the_console_remembered()
    {
        var rule = Create(displayName: "Free VPN");

        rule.Apply(ChromeExtensionRuleMode.ForceInstalled, displayName: null);
        rule.DisplayName.ShouldBe("Free VPN", "a mode change must not erase the label");

        rule.Apply(ChromeExtensionRuleMode.Blocked, displayName: "  ");
        rule.DisplayName.ShouldBe("Free VPN", "whitespace is no label either");

        rule.Apply(ChromeExtensionRuleMode.Blocked, displayName: "Totally Free VPN");
        rule.DisplayName.ShouldBe("Totally Free VPN", "a fresh label replaces the old one");
    }

    [Fact]
    public void Applying_an_undefined_mode_is_refused_and_changes_nothing()
    {
        var rule = Create(mode: ChromeExtensionRuleMode.Blocked);

        Should.Throw<ArgumentOutOfRangeException>(() => rule.Apply((ChromeExtensionRuleMode)(-1), null));

        rule.Mode.ShouldBe(ChromeExtensionRuleMode.Blocked);
    }
}
