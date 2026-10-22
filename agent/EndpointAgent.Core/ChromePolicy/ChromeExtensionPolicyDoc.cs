using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.ChromePolicy;

/// <summary>
/// What the platform wants Chrome to do with one extension on this device.
/// </summary>
/// <remarks>
/// The four values mirror the wire contract's mode names exactly; each maps onto
/// one Chrome <c>installation_mode</c> (see <see cref="ChromeExtensionPolicyDoc"/>).
/// There is deliberately no "unmanaged" member: an extension the policy does not
/// name is simply absent from the document, so "leave it alone" is the absence of
/// a rule, not a fifth mode a payload could smuggle in.
/// </remarks>
public enum ChromeExtensionPolicyMode
{
    /// <summary>Chrome installs the extension from the Web Store and the user cannot remove or disable it.</summary>
    ForceInstalled,

    /// <summary>Chrome prevents the extension from being installed; an installed copy is disabled.</summary>
    Blocked,

    /// <summary>The user may install or remove the extension; the policy explicitly permits it.</summary>
    Allowed,

    /// <summary>Chrome uninstalls the extension and prevents it from being installed again.</summary>
    Removed,
}

/// <summary>One managed extension: which one, and what Chrome is to do with it.</summary>
/// <param name="ExtensionId">The Chrome extension id: exactly 32 characters, each a-p.</param>
/// <param name="Mode">The enforcement Chrome applies.</param>
public sealed record ChromeExtensionPolicyRule(string ExtensionId, ChromeExtensionPolicyMode Mode);

/// <summary>
/// Builds the Chrome <c>ExtensionSettings</c> policy document from the rules the
/// server issued.
/// </summary>
/// <remarks>
/// <para>
/// Pure: rules in, one JSON string out, nothing else touched -- the registry
/// write lives behind <see cref="Abstractions.IChromeExtensionPolicyWriter"/>, so
/// this document (the contract Chrome itself parses) is unit-testable on any OS.
/// The shape is Chrome's own: an object keyed by extension id, each value an
/// object whose <c>installation_mode</c> is <c>force_installed</c>,
/// <c>blocked</c>, <c>allowed</c> or <c>removed</c>; a force-installed extension
/// additionally carries the Web Store <c>update_url</c>, without which Chrome has
/// nowhere to install it from.
/// </para>
/// <para>
/// <b>Deterministic.</b> Rules are sorted by extension id (ordinal) before they
/// are written, so the same rule set always produces byte-identical output
/// whatever order the payload carried it in. The registry value therefore only
/// changes when the policy actually changed, which keeps re-applies invisible to
/// anything diffing or watching the key.
/// </para>
/// <para>
/// <b>An invalid rule is a refusal, not a skip.</b> The ids become keys in a
/// machine-wide enforcement document, so an id that is not shaped like a Chrome
/// extension id (exactly 32 characters a-p -- the shared contract's definition,
/// reused so this check and the inventory's cannot drift), a duplicate id, or a
/// mode value outside the enum refuses the whole document. Dropping the bad entry
/// and writing the rest would enforce a policy the server never issued.
/// </para>
/// </remarks>
public static class ChromeExtensionPolicyDoc
{
    /// <summary>
    /// The Chrome Web Store update URL a force-installed extension is installed
    /// from. The one fixed value the platform ever writes: the server names
    /// extensions, never where code comes from.
    /// </summary>
    public const string ForceInstallUpdateUrl = "https://clients2.google.com/service/update2/crx";

    /// <summary>
    /// Builds the <c>ExtensionSettings</c> JSON document for the given rules.
    /// An empty rule set builds the empty object <c>{}</c> (callers that mean
    /// "no managed policy" clear the value instead of writing that).
    /// </summary>
    /// <returns>
    /// True with the document, or false with a human-readable refusal when any
    /// rule is invalid -- in which case no document exists and nothing may be
    /// written.
    /// </returns>
    public static bool TryBuild(
        IReadOnlyList<ChromeExtensionPolicyRule> rules,
        [NotNullWhen(true)] out string? settingsJson,
        [NotNullWhen(false)] out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(rules);

        settingsJson = null;
        refusal = null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                refusal = "The Chrome extension policy contained a null rule.";
                return false;
            }

            // The contract's one definition of an extension id, shared with the
            // inventory reader and the server, so the three cannot drift apart.
            if (!InventoryChromeExtension.IsValidExtensionId(rule.ExtensionId))
            {
                refusal = "The Chrome extension policy named an invalid extension id; "
                    + "an id is exactly 32 characters, each a-p.";
                return false;
            }

            // Not Enum.IsDefined-by-name leniency: a value cast from an arbitrary
            // number must not reach the switch below and become a mode by accident.
            if (!Enum.IsDefined(rule.Mode))
            {
                refusal = "The Chrome extension policy carried a mode outside the supported set.";
                return false;
            }

            // Two rules for one extension is an ambiguity, and whole-state means
            // never guessing which the server meant.
            if (!seen.Add(rule.ExtensionId))
            {
                refusal = "The Chrome extension policy named the same extension more than once.";
                return false;
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var rule in rules.OrderBy(static r => r.ExtensionId, StringComparer.Ordinal))
            {
                writer.WriteStartObject(rule.ExtensionId);
                writer.WriteString("installation_mode", InstallationMode(rule.Mode));

                if (rule.Mode == ChromeExtensionPolicyMode.ForceInstalled)
                {
                    writer.WriteString("update_url", ForceInstallUpdateUrl);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        settingsJson = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return true;
    }

    /// <summary>The Chrome policy name for a mode. Validated above, so every defined member maps.</summary>
    private static string InstallationMode(ChromeExtensionPolicyMode mode) => mode switch
    {
        ChromeExtensionPolicyMode.ForceInstalled => "force_installed",
        ChromeExtensionPolicyMode.Blocked => "blocked",
        ChromeExtensionPolicyMode.Allowed => "allowed",
        ChromeExtensionPolicyMode.Removed => "removed",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unreachable: TryBuild validates the mode."),
    };
}
