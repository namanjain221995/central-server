using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One driver update Windows Update offers a managed endpoint but has not yet
/// installed, as last reported by a <c>ScanDriverUpdates</c> scan. Replaced
/// wholesale per scan, like a driver or a piece of software inventory.
/// </summary>
/// <remarks>
/// <para>
/// This is the pending list the console shows -- "which machines have driver
/// updates waiting, and which ones" -- not a history. A scan that finds nothing
/// clears the device's rows, so the absence of a row means the machine reported it
/// has nothing pending, never that it was never asked. The live state of an install
/// in flight is read from its <see cref="Tasks.DeviceTaskType.InstallDriverUpdate"/>
/// task, not from a column here; once the install completes the next scan reflects
/// what actually got installed. What the platform remembers about installing an
/// update -- attempts, outcome, whether it came back -- lives in
/// <see cref="DeviceDriverUpdateInstallRecord"/>, and an operator's decision to hide
/// one in <see cref="DeviceDriverUpdateSuppression"/>, because these rows are
/// replaced by every scan and that memory must survive it.
/// </para>
/// <para>
/// <see cref="UpdateId"/> and <see cref="Revision"/> together are the Windows Update
/// identity of the update. The id alone is carried to the agent to install, because
/// that is what Windows Update matches on; the revision travels with it so the
/// console can tell a superseded offer from a fresh one.
/// </para>
/// </remarks>
public sealed class DeviceDriverUpdate : AuditableEntity
{
    private DeviceDriverUpdate()
    {
        UpdateId = null!;
        Title = null!;
    }

    public DeviceDriverUpdate(
        Guid deviceId,
        string updateId,
        int revision,
        string title,
        string? driverClass,
        string? driverProvider,
        DateTimeOffset? driverVersionDate,
        bool isPreview,
        long? sizeBytes,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        UpdateId = Guard.NotNullOrWhiteSpace(updateId, nameof(updateId), maxLength: 64);
        Revision = revision;
        Title = Guard.NotNullOrWhiteSpace(title, nameof(title), maxLength: 512);
        DriverClass = Guard.OptionalMaxLength(driverClass, 128);
        DriverProvider = Guard.OptionalMaxLength(driverProvider, 256);
        DriverVersionDate = driverVersionDate;
        IsPreview = isPreview;

        // A negative size is not a real figure; Windows Update reports a
        // non-negative byte count or nothing. Treat anything else as unknown
        // rather than storing a nonsense number.
        SizeBytes = sizeBytes is >= 0 ? sizeBytes : null;
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    /// <summary>The Windows Update update id (a GUID). The key the agent installs by.</summary>
    public string UpdateId { get; private set; }

    /// <summary>The update's revision number. With <see cref="UpdateId"/>, its stable identity.</summary>
    public int Revision { get; private set; }

    /// <summary>The update's display title, e.g. "Intel - Display - 31.0.101.4502".</summary>
    public string Title { get; private set; }

    /// <summary>The driver class Windows Update reports, e.g. "Display", "Net", when known.</summary>
    public string? DriverClass { get; private set; }

    /// <summary>Who publishes the driver, when known. Not necessarily the hardware vendor.</summary>
    public string? DriverProvider { get; private set; }

    /// <summary>The driver's version date Windows Update reports, when known.</summary>
    public DateTimeOffset? DriverVersionDate { get; private set; }

    /// <summary>
    /// Whether Windows Update marks this a preview (beta) driver. The console never
    /// auto-selects these, and surfaces the flag so an operator who selects one is
    /// doing it deliberately.
    /// </summary>
    public bool IsPreview { get; private set; }

    /// <summary>The download size in bytes, when Windows Update reports it.</summary>
    public long? SizeBytes { get; private set; }

    /// <summary>When the scan that reported this update ran.</summary>
    public DateTimeOffset CollectedAt { get; private set; }
}
