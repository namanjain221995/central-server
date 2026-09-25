using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Chrome;

/// <summary>
/// The Chrome installation on one managed endpoint, as last reported. One row per
/// device, upserted on every inventory upload.
/// </summary>
/// <remarks>
/// <para>
/// A row exists once a device has reported the Chrome section at all, whatever it
/// said; a device with no row has never reported it (an agent older than the
/// section). That distinction is what lets the console tell "no Chrome" from "no
/// data", so the row is never deleted when Chrome is uninstalled -- it is applied
/// with <see cref="ChromeReportStatus.NotInstalled"/> instead.
/// </para>
/// <para>
/// Stores only what the endpoint reported. Whether the version is current is not a
/// column: it will be derived on read against a reference that does not exist yet,
/// and until it does the answer is unknown rather than a fabricated zero.
/// </para>
/// <para>
/// Every describing field is nullable and null means unrecorded. The limits are the
/// wire contract's, mirrored here so a row that reaches the database has already
/// satisfied them however it got there.
/// </para>
/// </remarks>
public sealed class ChromeInstallation : AuditableEntity
{
    private ChromeInstallation()
    {
    }

    public ChromeInstallation(Guid deviceId)
    {
        DeviceId = Guard.NotEmpty(deviceId);
    }

    public Guid DeviceId { get; private set; }

    /// <summary>Whether the agent found Chrome, and whether it could read it fully.</summary>
    public ChromeReportStatus Status { get; private set; }

    /// <summary>The installed version as Windows records it, e.g. <c>131.0.6778.86</c>.</summary>
    public string? Version { get; private set; }

    /// <summary>Path to <c>chrome.exe</c>, when known and local.</summary>
    public string? ExecutablePath { get; private set; }

    /// <summary><c>x64</c>, <c>x86</c> or <c>arm64</c> as the updater records it.</summary>
    public string? Architecture { get; private set; }

    /// <summary><c>stable</c>, <c>beta</c>, <c>dev</c> or <c>canary</c> as the updater records it.</summary>
    public string? Channel { get; private set; }

    /// <summary><c>Machine</c> for an all-users install, <c>User</c> for a per-user one.</summary>
    public string? InstallationScope { get; private set; }

    /// <summary>For a per-user install, the account it belongs to.</summary>
    public string? InstalledForUser { get; private set; }

    /// <summary>The Google Update client's own version, when present.</summary>
    public string? UpdaterVersion { get; private set; }

    /// <summary>When Google Update last checked for an update, when it records one.</summary>
    public DateTimeOffset? LastUpdateCheck { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>
    /// Whether Chrome is present: the agent found it and could name its version.
    /// </summary>
    /// <remarks>
    /// Requires the version as well as the status, because an installation the
    /// agent could see but not read is not one the console can say anything about,
    /// and a non-empty profile list must never be read as "Chrome is present".
    /// </remarks>
    public bool IsInstalled => Status == ChromeReportStatus.Available && Version is not null;

    /// <summary>
    /// Replaces every reported field with the latest upload. Fields the new report
    /// omits become null: the upload is a whole snapshot, and keeping a stale value
    /// beside a fresh status would describe an installation that no longer exists.
    /// </summary>
    public void Apply(
        ChromeReportStatus status,
        string? version,
        string? executablePath,
        string? architecture,
        string? channel,
        string? installationScope,
        string? installedForUser,
        string? updaterVersion,
        DateTimeOffset? lastUpdateCheck,
        DateTimeOffset collectedAt)
    {
        Status = status;
        Version = Guard.OptionalMaxLength(version, 64);
        ExecutablePath = Guard.OptionalMaxLength(executablePath, 512);
        Architecture = Guard.OptionalMaxLength(architecture, 16);
        Channel = Guard.OptionalMaxLength(channel, 16);
        InstallationScope = Guard.OptionalMaxLength(installationScope, 16);
        InstalledForUser = Guard.OptionalMaxLength(installedForUser, 256);
        UpdaterVersion = Guard.OptionalMaxLength(updaterVersion, 64);
        LastUpdateCheck = lastUpdateCheck;
        CollectedAt = collectedAt;
    }
}
