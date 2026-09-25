using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Chrome;

/// <summary>
/// One extension in one Chrome profile on a managed endpoint, as Chrome recorded
/// it. Replaced wholesale with its profile per complete inventory upload.
/// </summary>
/// <remarks>
/// <para>
/// Facts only. The agent reports what Chrome's own record says; whether the
/// extension is wanted, blocked or out of date is decided on the server, so a
/// change of judgement is a server change rather than a fleet-wide agent rollout.
/// Nothing here was read from the extension's own code directory.
/// </para>
/// <para>
/// The extension id is validated here as well as at the Agent API. It is the one
/// value the fleet-wide "which devices have extension X" index is keyed on, and a
/// malformed id in that column would make an extension unfindable rather than
/// merely mislabelled. The rule is restated rather than shared because the domain
/// does not reference the wire contract; a test holds the two together.
/// </para>
/// </remarks>
public sealed class ChromeExtension : AuditableEntity
{
    /// <summary>
    /// The length of a Chrome extension id. Chrome derives it from the extension's
    /// public key as 32 characters in a-p, so anything else cannot be one.
    /// </summary>
    public const int ExtensionIdLength = 32;

    private ChromeExtension()
    {
        ExtensionId = null!;
    }

    public ChromeExtension(
        Guid deviceId,
        Guid chromeProfileId,
        string extensionId,
        string? name,
        string? version,
        int? manifestVersion,
        bool? enabled,
        ChromeExtensionInstallType installType,
        bool isManaged,
        bool? fromWebStore,
        string? updateUrl,
        DateTimeOffset? installedAt,
        DateTimeOffset? updatedAt,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        ChromeProfileId = Guard.NotEmpty(chromeProfileId);
        ExtensionId = ValidateExtensionId(extensionId);
        Name = Guard.OptionalMaxLength(name, 256);
        Version = Guard.OptionalMaxLength(version, 64);
        ManifestVersion = ValidateManifestVersion(manifestVersion);
        Enabled = enabled;
        InstallType = installType;
        IsManaged = isManaged;
        FromWebStore = fromWebStore;
        UpdateUrl = Guard.OptionalMaxLength(updateUrl, 512);
        InstalledAt = installedAt;
        ExtensionUpdatedAt = updatedAt;
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    public Guid ChromeProfileId { get; private set; }

    /// <summary>Chrome's 32-character identifier (letters a-p), derived from the extension's key.</summary>
    public string ExtensionId { get; private set; }

    /// <summary>The localised display name Chrome recorded, when present.</summary>
    public string? Name { get; private set; }

    public string? Version { get; private set; }

    /// <summary>The manifest format version (2 or 3), when present.</summary>
    public int? ManifestVersion { get; private set; }

    /// <summary>Whether Chrome has the extension enabled. Null when the record does not say.</summary>
    public bool? Enabled { get; private set; }

    /// <summary>Where Chrome says the extension came from.</summary>
    public ChromeExtensionInstallType InstallType { get; private set; }

    /// <summary>Whether enterprise policy installed it.</summary>
    public bool IsManaged { get; private set; }

    /// <summary>Whether Chrome records the Web Store as its origin. Null when unrecorded.</summary>
    public bool? FromWebStore { get; private set; }

    /// <summary>Where Chrome checks for updates to it, when the manifest names one.</summary>
    public string? UpdateUrl { get; private set; }

    /// <summary>When it was first installed, as Chrome records it.</summary>
    public DateTimeOffset? InstalledAt { get; private set; }

    /// <summary>
    /// When Chrome last updated the extension, as Chrome records it. Named apart
    /// from the row's own <see cref="AuditableEntity.UpdatedAt"/>, which the
    /// persistence layer stamps and which says nothing about the extension.
    /// </summary>
    public DateTimeOffset? ExtensionUpdatedAt { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>
    /// Whether this is one of Chrome's own built-ins rather than something anyone
    /// installed. The console counts "extensions" without them: a machine with
    /// nothing installed still carries a dozen components, and counting those
    /// would make every device look loaded.
    /// </summary>
    public bool IsComponent =>
        InstallType is ChromeExtensionInstallType.Component or ChromeExtensionInstallType.ExternalComponent;

    /// <summary>
    /// Whether a string is shaped like a Chrome extension id: exactly
    /// <see cref="ExtensionIdLength"/> characters, each in a-p. The same rule the
    /// wire contract states; the constructor refuses anything else.
    /// </summary>
    public static bool IsValidExtensionId(string? value)
    {
        if (value is null || value.Length != ExtensionIdLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < 'a' or > 'p')
            {
                return false;
            }
        }

        return true;
    }

    private static string ValidateExtensionId(string? value)
    {
        if (!IsValidExtensionId(value))
        {
            throw new ArgumentException(
                $"A Chrome extension id is exactly {ExtensionIdLength} characters in a-p.", nameof(value));
        }

        return value!;
    }

    private static int? ValidateManifestVersion(int? value)
    {
        // Chrome has shipped manifest versions 1 to 3. The ceiling is generous on
        // purpose -- it exists to refuse garbage, not to predict Chrome's roadmap.
        if (value is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "A Chrome manifest version is between 1 and 99.");
        }

        return value;
    }
}
