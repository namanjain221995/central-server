using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Chrome;

/// <summary>
/// One administrator-declared rule for one Chrome extension on one device: force
/// it on, block it, allow it or remove it.
/// </summary>
/// <remarks>
/// <para>
/// This is the decision, not the observation. <see cref="ChromeExtension"/> rows
/// say what Chrome reported is in each profile; a rule says what an administrator
/// wants, whether or not the extension is present yet -- a force-install names an
/// extension no profile has, and a block may outlive the install it was written
/// against. The two are deliberately unlinked: inventory is replaced wholesale on
/// every upload, and a rule must not vanish because a scan did.
/// </para>
/// <para>
/// Rules are per device and whole-state on the wire: every task carries every rule
/// the device has, and an extension absent from the set has NO rule -- Chrome's
/// default applies to it. The unique (device, extension) pair is the identity an
/// upsert and the enforcement key on the endpoint both match on.
/// </para>
/// <para>
/// <see cref="DisplayName"/> is a label the console remembers -- typically the
/// extension's name as inventory knew it when the rule was written -- so a rule
/// for an extension no longer installed anywhere still reads as something other
/// than 32 letters. Display only: enforcement matches the id, never the name,
/// because an extension chooses its own name.
/// </para>
/// </remarks>
public sealed class ChromeExtensionRule : AuditableEntity
{
    public const int MaxDisplayNameLength = 256;

    private ChromeExtensionRule()
    {
        ExtensionId = null!;
    }

    public ChromeExtensionRule(
        Guid deviceId,
        string extensionId,
        ChromeExtensionRuleMode mode,
        string? displayName)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        ExtensionId = ValidateExtensionId(extensionId);
        Mode = ValidateMode(mode);
        DisplayName = Guard.OptionalMaxLength(displayName, MaxDisplayNameLength);
    }

    public Guid DeviceId { get; private set; }

    /// <summary>
    /// Chrome's 32-character identifier (letters a-p). The enforcement key; the
    /// same rule <see cref="ChromeExtension"/> states, restated by the shared
    /// check so the two columns can never hold differently-shaped ids.
    /// </summary>
    public string ExtensionId { get; private set; }

    public ChromeExtensionRuleMode Mode { get; private set; }

    /// <summary>A label for the console. Never used to match or enforce.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// Re-decides the rule: the upsert half of "one rule per device per extension".
    /// </summary>
    /// <param name="displayName">
    /// A fresh label, or null to keep the one already remembered -- a mode change
    /// from a caller that does not know the extension's name must not erase the
    /// label the console has.
    /// </param>
    public void Apply(ChromeExtensionRuleMode mode, string? displayName)
    {
        Mode = ValidateMode(mode);

        if (Guard.OptionalMaxLength(displayName, MaxDisplayNameLength) is { } label)
        {
            DisplayName = label;
        }
    }

    private static string ValidateExtensionId(string? value)
    {
        if (!ChromeExtension.IsValidExtensionId(value))
        {
            throw new ArgumentException(
                $"A Chrome extension id is exactly {ChromeExtension.ExtensionIdLength} characters in a-p.",
                nameof(value));
        }

        return value!;
    }

    private static ChromeExtensionRuleMode ValidateMode(ChromeExtensionRuleMode mode)
    {
        // Refuses a cast of an arbitrary number before it can be stored or put on
        // the wire, where the agent would drop the rule as malformed.
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "Not a Chrome extension rule mode this platform defines.");
        }

        return mode;
    }
}
