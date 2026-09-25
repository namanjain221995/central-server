using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Chrome;

/// <summary>
/// One Chrome profile on a managed endpoint: a browsing identity inside one
/// Windows user's Chrome, as last reported. Replaced wholesale per complete
/// inventory upload.
/// </summary>
/// <remarks>
/// <para>
/// Identity is (device, Windows SID, profile directory name). The SID because
/// account names are renameable; the directory name because the display name is
/// whatever the person typed, and Chrome itself keys the profile by directory.
/// </para>
/// <para>
/// No Google account e-mail address is carried, by construction: the wire contract
/// has no field for one, so there is nothing here to store and nothing to redact.
/// </para>
/// <para>
/// Hangs off <see cref="ChromeInstallation"/> as well as the device so the whole
/// Chrome section of a device is one graph. A profile can exist under an
/// installation whose status is <see cref="ChromeReportStatus.NotInstalled"/>:
/// Chrome's uninstaller leaves <c>User Data</c> behind, and what it recorded there
/// is still fact.
/// </para>
/// </remarks>
public sealed class ChromeProfile : AuditableEntity
{
    private ChromeProfile()
    {
        UserSid = null!;
        ProfileKey = null!;
        ProfilePath = null!;
    }

    public ChromeProfile(
        Guid deviceId,
        Guid chromeInstallationId,
        string userSid,
        string? userAccount,
        string profileKey,
        string? profileName,
        string profilePath,
        bool? isManaged,
        DateTimeOffset? lastActiveAt,
        DateTimeOffset collectedAt,
        // Last and optional: added after the first rows existed, so every
        // caller and fixture written before it stays valid.
        string? accountEmail = null)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        ChromeInstallationId = Guard.NotEmpty(chromeInstallationId);
        UserSid = Guard.NotNullOrWhiteSpace(userSid, nameof(userSid), maxLength: 184);
        UserAccount = Guard.OptionalMaxLength(userAccount, 256);
        ProfileKey = Guard.NotNullOrWhiteSpace(profileKey, nameof(profileKey), maxLength: 64);
        ProfileName = Guard.OptionalMaxLength(profileName, 256);
        ProfilePath = Guard.NotNullOrWhiteSpace(profilePath, nameof(profilePath), maxLength: 512);
        AccountEmail = Guard.OptionalMaxLength(accountEmail, 256);
        IsManaged = isManaged;
        LastActiveAt = lastActiveAt;
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    public Guid ChromeInstallationId { get; private set; }

    /// <summary>The Windows account the profile belongs to.</summary>
    public string UserSid { get; private set; }

    /// <summary>That account's name (<c>DOMAIN\name</c>), or the SID when it could not be resolved.</summary>
    public string? UserAccount { get; private set; }

    /// <summary>
    /// The Google account signed in to the profile, as Chrome records it; null
    /// when the profile is not signed in or the agent predates the field.
    /// </summary>
    public string? AccountEmail { get; private set; }

    /// <summary>The profile directory name under <c>User Data</c>: Chrome's stable identity for it.</summary>
    public string ProfileKey { get; private set; }

    /// <summary>The display name Chrome shows for the profile, when recorded.</summary>
    public string? ProfileName { get; private set; }

    /// <summary>The profile directory, absolute and local to the endpoint.</summary>
    public string ProfilePath { get; private set; }

    /// <summary>Whether Chrome marks the profile as enterprise-managed. Null when unrecorded.</summary>
    public bool? IsManaged { get; private set; }

    /// <summary>When the profile was last used, as Chrome records it. Null when unrecorded.</summary>
    public DateTimeOffset? LastActiveAt { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }
}
