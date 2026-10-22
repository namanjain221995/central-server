using System.Text.Json;
using EndpointAgent.Core.ChromePolicy;

namespace EndpointAgent.Core.Tests.ChromePolicy;

/// <summary>
/// The Chrome <c>ExtensionSettings</c> document builder: the per-mode shape Chrome
/// parses, the deterministic ordering, and the refusals -- an invalid or duplicated
/// id, or a mode outside the enum, refuses the whole document rather than skipping
/// the entry (a skipped entry would be a policy the server never issued).
/// </summary>
public sealed class ChromeExtensionPolicyDocTests
{
    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string IdC = "cccccccccccccccccccccccccccccccc";

    private static string Build(params ChromeExtensionPolicyRule[] rules)
    {
        ChromeExtensionPolicyDoc.TryBuild(rules, out var json, out var refusal).ShouldBeTrue(refusal);
        return json!;
    }

    [Fact]
    public void A_force_installed_extension_carries_the_web_store_update_url()
    {
        var json = Build(new ChromeExtensionPolicyRule(IdA, ChromeExtensionPolicyMode.ForceInstalled));

        var entry = JsonDocument.Parse(json).RootElement.GetProperty(IdA);
        entry.GetProperty("installation_mode").GetString().ShouldBe("force_installed");
        entry.GetProperty("update_url").GetString()
            .ShouldBe("https://clients2.google.com/service/update2/crx");
    }

    [Theory]
    [InlineData(ChromeExtensionPolicyMode.Blocked, "blocked")]
    [InlineData(ChromeExtensionPolicyMode.Allowed, "allowed")]
    [InlineData(ChromeExtensionPolicyMode.Removed, "removed")]
    public void The_non_install_modes_carry_only_the_installation_mode(
        ChromeExtensionPolicyMode mode, string expected)
    {
        var json = Build(new ChromeExtensionPolicyRule(IdA, mode));

        var entry = JsonDocument.Parse(json).RootElement.GetProperty(IdA);
        entry.GetProperty("installation_mode").GetString().ShouldBe(expected);

        // No update_url: only a force-install names a source to install from.
        entry.EnumerateObject().Count().ShouldBe(1);
    }

    [Fact]
    public void The_keys_are_the_extension_ids_sorted_ordinally_whatever_the_input_order()
    {
        var json = Build(
            new ChromeExtensionPolicyRule(IdC, ChromeExtensionPolicyMode.Blocked),
            new ChromeExtensionPolicyRule(IdA, ChromeExtensionPolicyMode.Allowed),
            new ChromeExtensionPolicyRule(IdB, ChromeExtensionPolicyMode.Removed));

        JsonDocument.Parse(json).RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ShouldBe([IdA, IdB, IdC]);
    }

    [Fact]
    public void The_same_rules_produce_byte_identical_output_in_any_order()
    {
        ChromeExtensionPolicyRule[] forward =
        [
            new(IdA, ChromeExtensionPolicyMode.ForceInstalled),
            new(IdB, ChromeExtensionPolicyMode.Blocked),
            new(IdC, ChromeExtensionPolicyMode.Removed),
        ];

        // Determinism is what keeps a re-apply invisible: the registry value only
        // changes when the policy actually changed.
        Build(forward).ShouldBe(Build([.. forward.Reverse()]));
    }

    [Fact]
    public void An_empty_rule_set_builds_the_empty_object()
    {
        Build().ShouldBe("{}");
    }

    [Theory]
    [InlineData("")]                                      // empty
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]       // 31 characters
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]     // 33 characters
    [InlineData("qaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]      // 'q' is outside a-p
    [InlineData("Aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]      // upper case is not an id
    [InlineData("1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]      // digits are not in the alphabet
    public void An_invalid_extension_id_refuses_the_whole_document(string badId)
    {
        ChromeExtensionPolicyRule[] rules =
        [
            new(IdA, ChromeExtensionPolicyMode.Blocked),
            new(badId, ChromeExtensionPolicyMode.Blocked),
        ];

        ChromeExtensionPolicyDoc.TryBuild(rules, out var json, out var refusal).ShouldBeFalse();

        json.ShouldBeNull("a document with a good rule in it must not survive a bad one");
        (refusal ?? "").ShouldContain("extension id");
    }

    [Fact]
    public void A_duplicated_extension_id_is_a_refusal()
    {
        // Two rules for one extension is an ambiguity; whole-state means never
        // guessing which one the server meant.
        ChromeExtensionPolicyRule[] rules =
        [
            new(IdA, ChromeExtensionPolicyMode.ForceInstalled),
            new(IdA, ChromeExtensionPolicyMode.Blocked),
        ];

        ChromeExtensionPolicyDoc.TryBuild(rules, out var json, out var refusal).ShouldBeFalse();

        json.ShouldBeNull();
        (refusal ?? "").ShouldContain("more than once");
    }

    [Fact]
    public void A_mode_outside_the_enum_is_a_refusal()
    {
        ChromeExtensionPolicyRule[] rules = [new(IdA, (ChromeExtensionPolicyMode)42)];

        ChromeExtensionPolicyDoc.TryBuild(rules, out var json, out _).ShouldBeFalse();
        json.ShouldBeNull();
    }
}
