using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// What the platform last learned about an attempt to install one Windows Update
/// security update on one device. Stored as text, like the task type.
/// </summary>
/// <remarks>
/// A closed set on purpose: each value is a distinct thing to tell the operator,
/// and every transition between them is decided either by an install result or by
/// the reconcile that runs after each scan. "Installing" is deliberately not here --
/// an install in flight is read from its task, which is the only thing that knows.
/// The same values, with the same meaning, as <see cref="DriverUpdateInstallStatus"/>;
/// a separate type so the two features can never drift into each other's tables.
/// </remarks>
public enum SecurityUpdateInstallStatus
{
    /// <summary>Windows Update no longer offers it: confirmed by a scan after the attempt.</summary>
    Installed = 0,

    /// <summary>Windows Update reported success but nothing has yet checked whether it is still offered.</summary>
    InstalledUnverified = 1,

    /// <summary>Windows Update reported success and asked for a restart to finish it.</summary>
    PendingReboot = 2,

    /// <summary>Windows Update reported success and then offered it again -- including after a restart.</summary>
    ReOffered = 3,

    /// <summary>Windows Update reported the install failed (or refused it, e.g. an unaccepted licence).</summary>
    Failed = 4,
}

/// <summary>
/// The platform's memory of installing one Windows Update security update on one
/// device: how many times it was tried, what Windows said, and what the scans after
/// it showed. One row per device and update id.
/// </summary>
/// <remarks>
/// <para>
/// A separate table, not columns on <see cref="DeviceSecurityUpdate"/>, because the
/// pending rows are deleted and recreated by every scan by design. The memory of an
/// attempt has to survive exactly that, or the update that "installs" and comes back
/// on the next scan reads as a fresh, never-tried offer forever.
/// </para>
/// <para>
/// Security updates almost always want a restart to finish -- a cumulative update is
/// staged by the install and committed by the reboot -- so PendingReboot is the
/// common outcome here, not the exception it is for drivers.
/// </para>
/// <para>
/// <see cref="Revision"/> is the revision that was pending when the attempt was
/// made, when the server knew it. A later scan offering the same update id at a
/// higher revision is a new offer that superseded the attempted one, not the
/// attempted one coming back, and the reconcile must not brand it re-offered.
/// </para>
/// </remarks>
public sealed class DeviceSecurityUpdateInstallRecord : AuditableEntity
{
    private DeviceSecurityUpdateInstallRecord()
    {
        UpdateId = null!;
        Title = null!;
    }

    /// <summary>
    /// Starts the memory of a first attempt. The caller records the attempt itself
    /// with <see cref="RecordAttempt"/> straight away, so the first and every later
    /// attempt go through one path.
    /// </summary>
    public DeviceSecurityUpdateInstallRecord(
        Guid deviceId,
        string updateId,
        int? revision,
        string title,
        DateTimeOffset firstAttemptAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        UpdateId = Guard.NotNullOrWhiteSpace(updateId, nameof(updateId), maxLength: 64);
        Revision = revision;
        Title = Guard.NotNullOrWhiteSpace(title, nameof(title), maxLength: 512);
        FirstAttemptAt = firstAttemptAt;
        LastAttemptAt = firstAttemptAt;
        Status = SecurityUpdateInstallStatus.InstalledUnverified;
    }

    public Guid DeviceId { get; private set; }

    /// <summary>The Windows Update update id (a GUID), as the scan reported it.</summary>
    public string UpdateId { get; private set; }

    /// <summary>The revision pending when the last attempt was made, or null when not known.</summary>
    public int? Revision { get; private set; }

    /// <summary>The update's display title, as last reported.</summary>
    public string Title { get; private set; }

    public SecurityUpdateInstallStatus Status { get; private set; }

    /// <summary>How many install results have named this update on this device.</summary>
    public int Attempts { get; private set; }

    /// <summary>The last Windows Update <c>OperationResultCode</c> (2 succeeded, 3 with errors, 4 failed, 5 aborted).</summary>
    public int? LastResultCode { get; private set; }

    /// <summary>The last HRESULT Windows Update reported for this update, when one was.</summary>
    public int? LastHresult { get; private set; }

    /// <summary>The install task that produced the last attempt.</summary>
    public Guid? LastTaskId { get; private set; }

    public DateTimeOffset FirstAttemptAt { get; private set; }

    /// <summary>
    /// When the last attempt ran, on the server's clock -- the install task's
    /// delivery time. Compared with <see cref="Device.LastBootAt"/> to tell whether
    /// a restart has happened since.
    /// </summary>
    public DateTimeOffset LastAttemptAt { get; private set; }

    /// <summary>When a scan first showed the update gone after the last attempt; null until then.</summary>
    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>
    /// Records one install result for this update. Every attempt counts, and a new
    /// attempt clears any earlier confirmation: whatever was true before, the
    /// question "did it stick" is open again until the next scan answers it.
    /// </summary>
    public void RecordAttempt(
        SecurityUpdateInstallStatus status,
        int? resultCode,
        int? hresult,
        Guid taskId,
        string? title,
        int? revision,
        DateTimeOffset attemptedAt)
    {
        Status = status;
        LastResultCode = resultCode;
        LastHresult = hresult;
        LastTaskId = Guard.NotEmpty(taskId);
        LastAttemptAt = attemptedAt;
        ConfirmedAt = null;
        Attempts++;

        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = Guard.NotNullOrWhiteSpace(title, nameof(title), maxLength: 512);
        }

        if (revision is not null)
        {
            Revision = revision;
        }
    }

    /// <summary>
    /// A scan after the attempt no longer lists the update: it is installed. The
    /// first confirmation is kept; re-confirming on every later scan would make the
    /// "installed recently" list forget when it actually happened.
    /// </summary>
    public void ConfirmInstalled(DateTimeOffset now)
    {
        if (Status == SecurityUpdateInstallStatus.Installed && ConfirmedAt is not null)
        {
            return;
        }

        Status = SecurityUpdateInstallStatus.Installed;
        ConfirmedAt = now;
    }

    /// <summary>
    /// Windows Update offers the update again after reporting it installed -- with
    /// no restart outstanding, or after one. Retrying the same channel cannot help.
    /// </summary>
    public void MarkReOffered()
    {
        Status = SecurityUpdateInstallStatus.ReOffered;
        ConfirmedAt = null;
    }
}
