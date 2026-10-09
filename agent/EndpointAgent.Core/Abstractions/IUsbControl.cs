namespace EndpointAgent.Core.Abstractions;

/// <summary>How the platform classifies a USB device. Mirrors the server enum by name.</summary>
public enum UsbClass
{
    Unknown = 0,
    Storage = 1,
    Keyboard = 2,
    Mouse = 3,
    NetworkAdapter = 4,
    Hub = 5,
    Other = 6,

    /// <summary>
    /// A phone, tablet, camera or media player reached through MTP or PTP — the
    /// Windows Portable Devices stack — or an Android device exposing its ADB
    /// debugging interface.
    /// </summary>
    /// <remarks>
    /// Subject to access policy exactly like <see cref="Storage"/>, because a
    /// phone in "File transfer" mode is a writable disk for every practical
    /// purpose even though Windows never gives it a drive letter: Explorer
    /// shows it under This PC and copies files both ways. It is a class of its
    /// own rather than being folded into Storage because enforcement differs in
    /// one way an administrator has to know about — there is no read-only mode.
    /// </remarks>
    PortableDevice = 7,
}

public static class UsbClassExtensions
{
    /// <summary>True for the classes access policy applies to.</summary>
    public static bool IsRestrictable(this UsbClass usbClass) =>
        usbClass is UsbClass.Storage or UsbClass.PortableDevice;

    /// <summary>
    /// True when a read-only grant can actually be enforced on the class.
    /// </summary>
    /// <remarks>
    /// Only mass storage: read-only is a disk attribute, and an MTP/PTP device
    /// has no disk to carry one. A portable device is either restricted or
    /// enabled, nothing in between.
    /// </remarks>
    public static bool SupportsReadOnly(this UsbClass usbClass) => usbClass == UsbClass.Storage;
}

/// <summary>Wording shared by the enforcer and the policy manager, so the console sees one message.</summary>
public static class UsbEnforcementMessages
{
    /// <summary>
    /// Why a read-only grant on a portable device ends in Restricted rather than
    /// in access. Reported verbatim so the console can show the reason beside
    /// the device.
    /// </summary>
    public const string ReadOnlyUnavailableForPortableDevices =
        "Read-only is not available for a phone, camera or other portable device: MTP and PTP have no "
        + "read-only mode. The device has been kept restricted; grant read/write access if the user needs it.";
}

/// <summary>What the agent is enforcing on a restrictable device.</summary>
public enum UsbEnforcedState
{
    /// <summary>Device instance disabled: no volume, no drive letter, no MTP session, no access.</summary>
    Restricted = 0,

    /// <summary>Device enabled with the disk marked read-only by Windows. Storage only.</summary>
    ReadOnly = 1,

    /// <summary>
    /// Device enabled and writable: ordinary Windows behaviour, for as long as
    /// the grant lasts.
    /// </summary>
    /// <remarks>
    /// The widest state this agent can be put into, and the only one that
    /// permits writing. It still requires a live, in-date, administrator-issued
    /// grant naming this exact device — it is not a way to mark a device
    /// permanently trusted, and the device returns to <see cref="Restricted"/>
    /// the moment the grant lapses, with or without contact from the server.
    /// </remarks>
    Enabled = 2,
}

/// <summary>One USB device as seen on the local machine.</summary>
/// <param name="InstanceId">
/// Windows device instance ID, e.g. <c>USB\VID_0781&amp;PID_5581\ABC123</c>. The
/// only identity used for policy decisions.
/// </param>
/// <param name="SerialNumber">
/// The serial from the instance ID's last segment when the device genuinely has
/// one, otherwise null. Devices without a serial get a Windows-generated
/// instance segment containing <c>&amp;</c>, which is per-port rather than
/// per-device; the enumerator reports null instead of passing that off as a
/// serial, because a grant keyed to a port would follow the port, not the stick.
/// </param>
/// <param name="IsEnabled">Whether Windows currently has the device started.</param>
public sealed record UsbDeviceInfo(
    string InstanceId,
    UsbClass Class,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    string? Manufacturer,
    string? Product,
    string? HardwareIds,
    bool IsEnabled);

/// <summary>
/// How far an enforcement attempt got — beyond "the API call returned".
/// </summary>
/// <remarks>
/// A successful SetupAPI call is not proof that a device is blocked: Windows
/// can accept a disable and defer it to the next restart, and a status read can
/// itself fail. The console must be able to tell those apart from a device it
/// has actually confirmed is in the requested state, so the distinction is
/// carried from the enforcer all the way to the report.
/// </remarks>
public enum UsbEnforcementStatus
{
    /// <summary>The call succeeded but the resulting device state could not be read back.</summary>
    Unverified = 0,

    /// <summary>Windows reports the device in the requested state.</summary>
    Verified = 1,

    /// <summary>Windows accepted the change but applies it only after the endpoint restarts.</summary>
    RequiresRestart = 2,

    /// <summary>The state could not be applied.</summary>
    Failed = 3,
}

/// <summary>Outcome of one enforcement attempt.</summary>
/// <param name="Succeeded">
/// True only when the state was actually applied. A failure is reported to the
/// server rather than swallowed, so the console can show the device as
/// unenforced instead of implying a control that is not in place.
/// </param>
/// <param name="Error">
/// Why it failed, why it needs a restart, or — for <see cref="UsbEnforcementStatus.Unverified"/>
/// — why the result could not be confirmed. Null when the state was applied and
/// read back.
/// </param>
public sealed record UsbEnforcementResult(bool Succeeded, string? Error, UsbEnforcementStatus Status)
{
    /// <summary>Applied, and Windows reports the device in the requested state.</summary>
    public static readonly UsbEnforcementResult Ok = new(true, null, UsbEnforcementStatus.Verified);

    public static UsbEnforcementResult Failed(string error) =>
        new(false, error, UsbEnforcementStatus.Failed);

    /// <summary>
    /// Windows accepted the change but it is not in force until the endpoint
    /// restarts. Not a success: the control is not in place yet.
    /// </summary>
    public static UsbEnforcementResult RestartRequired(string reason) =>
        new(false, reason, UsbEnforcementStatus.RequiresRestart);

    /// <summary>The call succeeded but the device state could not be read back.</summary>
    public static UsbEnforcementResult Unverified(string reason) =>
        new(true, reason, UsbEnforcementStatus.Unverified);
}

/// <summary>Enumerates the USB devices attached to this machine.</summary>
public interface IUsbDeviceEnumerator
{
    IReadOnlyList<UsbDeviceInfo> Enumerate();
}

/// <summary>
/// Applies USB access state on this machine, for storage and for portable
/// devices (phones, cameras) alike.
/// </summary>
/// <remarks>
/// <para>
/// The Windows implementation uses SetupAPI (<c>DIF_PROPERTYCHANGE</c> with
/// <c>DICS_DISABLE</c>/<c>DICS_ENABLE</c>) and the disk IOCTL
/// <c>IOCTL_DISK_SET_DISK_ATTRIBUTES</c>. No shell, no PowerShell, no registry
/// edits through free-form commands, no kernel driver (ADR-0005).
/// </para>
/// <para>
/// Write access is expressible, through <see cref="AllowReadWrite"/> alone, and
/// only ever as the enforcement of a live administrator-issued grant. Every
/// path that is not such a grant — no policy, an expired one, a malformed one,
/// an unreachable server, an unreadable cache — resolves to
/// <see cref="Restrict"/> rather than to access.
/// </para>
/// <para>
/// Every method reads the device state back after acting and reports what it
/// found; a result is <see cref="UsbEnforcementStatus.Verified"/> only when
/// Windows itself says the device is in the requested state.
/// </para>
/// </remarks>
public interface IUsbPolicyEnforcer
{
    /// <summary>
    /// Disables the device instance so nothing mounts and no MTP/PTP session can
    /// be opened. Idempotent.
    /// </summary>
    UsbEnforcementResult Restrict(string instanceId);

    /// <summary>
    /// Enables the device and marks its disks read-only. Idempotent. Storage
    /// only: a portable device has no disk to mark, and the Windows
    /// implementation restricts it again and reports failure rather than leave
    /// it open.
    /// </summary>
    UsbEnforcementResult AllowReadOnly(string instanceId);

    /// <summary>
    /// Enables the device and clears any read-only marking, giving ordinary
    /// read/write access. Idempotent.
    /// </summary>
    /// <remarks>
    /// Mechanically the same operations as <see cref="Release"/>, and
    /// deliberately kept as a separate method anyway, because the two mean
    /// opposite things. This is enforcement of a live grant that the agent will
    /// withdraw when the deadline passes; Release is the agent standing down
    /// altogether. Collapsing them would make the ledger — which exists to
    /// record what still needs undoing — unable to tell the two apart.
    /// </remarks>
    UsbEnforcementResult AllowReadWrite(string instanceId);

    /// <summary>
    /// Undoes everything this agent applied to a device, leaving it as Windows
    /// would have it with no agent installed. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because both enforcement mechanisms outlive the process that
    /// applied them. Disabling a devnode writes <c>CONFIGFLAG_DISABLED</c> into
    /// the device's registry key, which Windows honours forever — across reboots,
    /// across the service being stopped, and across the product being uninstalled.
    /// Without an explicit release, stopping the agent would leave the machine
    /// permanently altered, and uninstalling it would leave sticks disabled with
    /// no remaining mechanism to re-enable them short of Device Manager by hand.
    /// </para>
    /// <para>
    /// Release is therefore not the same as <see cref="AllowReadOnly"/>. Read-only
    /// is a state this product enforces; release is the absence of enforcement.
    /// The device comes back enabled <em>and</em> writable, because that is what
    /// an unmanaged Windows machine does with a USB stick.
    /// </para>
    /// </remarks>
    UsbEnforcementResult Release(string instanceId);
}

/// <summary>
/// Raises an event when USB devices arrive or are removed.
/// </summary>
/// <remarks>
/// Notification is an optimisation for latency, not the mechanism policy depends
/// on: the agent also reconciles on a timer, so a watcher that fails to start
/// degrades the response time from seconds to the reconcile interval rather than
/// leaving a device unmanaged.
/// </remarks>
public interface IUsbDeviceWatcher : IDisposable
{
    event EventHandler<UsbChangeKind>? Changed;

    /// <summary>Begins watching. Returns false if notifications are unavailable.</summary>
    bool TryStart();
}

public enum UsbChangeKind
{
    Arrived = 0,
    Removed = 1,
}
