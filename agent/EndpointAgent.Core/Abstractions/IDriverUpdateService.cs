namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// One optional Windows Update driver update discovered by an online scan.
/// </summary>
/// <remarks>
/// A flat, secret-free projection of the handful of <c>IUpdate</c> /
/// <c>IWindowsDriverUpdate</c> properties the console shows. <see cref="UpdateId"/>
/// (the WUA <c>Identity.UpdateID</c> GUID) is the stable key an install request
/// names; <see cref="Revision"/> is carried alongside for display only.
/// </remarks>
/// <param name="UpdateId">The WUA update identity GUID (lower-case, no braces). The stable key.</param>
/// <param name="Revision">The update's revision number, for display.</param>
/// <param name="Title">The update's title.</param>
/// <param name="DriverClass">The driver device class (e.g. "Net", "Display"), or null.</param>
/// <param name="DriverProvider">The driver publisher, or null.</param>
/// <param name="DriverVerDate">The driver version date as an ISO date string, or null.</param>
/// <param name="IsPreview">Whether this update is a beta/preview that the platform excludes.</param>
/// <param name="SizeBytes">The maximum download size in bytes.</param>
public sealed record DriverUpdateInfo(
    string UpdateId,
    int Revision,
    string Title,
    string? DriverClass,
    string? DriverProvider,
    string? DriverVerDate,
    bool IsPreview,
    long SizeBytes);

/// <summary>The outcome of an online driver-update scan.</summary>
/// <remarks>
/// An empty but successful scan (<see cref="Succeeded"/> true, no
/// <see cref="Updates"/>) is the normal "nothing pending" case and is never an
/// error. A scan that could not run at all -- the update service stopped, a
/// rejected search, any COM failure -- is <see cref="Succeeded"/> false with a
/// human-readable, secret-free <see cref="Message"/>.
/// </remarks>
public sealed record DriverUpdateScanResult(
    bool Succeeded,
    string? Message,
    IReadOnlyList<DriverUpdateInfo> Updates)
{
    public static DriverUpdateScanResult Ok(IReadOnlyList<DriverUpdateInfo> updates) => new(true, null, updates);

    public static DriverUpdateScanResult Failure(string message) => new(false, message, []);
}

/// <summary>One update's per-update install result.</summary>
/// <param name="UpdateId">The update identity GUID this result is for.</param>
/// <param name="Title">The update's title.</param>
/// <param name="ResultCode">The WUA <c>OperationResultCode</c> (2 succeeded, 3 succeeded-with-errors, 4 failed).</param>
/// <param name="HResult">The per-update HRESULT (0 on success).</param>
public sealed record DriverUpdateInstallItem(
    string UpdateId,
    string Title,
    int ResultCode,
    int HResult);

/// <summary>The outcome of installing a set of requested driver updates.</summary>
/// <remarks>
/// <para>
/// <see cref="Error"/> is non-null only when the operation could not run at all
/// (the update service is stopped, another operation is in progress, a reboot is
/// pending, or any COM failure). In that case <see cref="Installed"/> and
/// <see cref="Failed"/> are empty.
/// </para>
/// <para>
/// When <see cref="Error"/> is null the operation ran and the per-update split is
/// authoritative: an update is in <see cref="Installed"/> when WUA reported success
/// (or success-with-errors) for it, and in <see cref="Failed"/> otherwise.
/// <see cref="RebootRequired"/> follows the install result and the system-wide
/// reboot flag.
/// </para>
/// </remarks>
public sealed record DriverUpdateInstallResult(
    bool RebootRequired,
    IReadOnlyList<DriverUpdateInstallItem> Installed,
    IReadOnlyList<DriverUpdateInstallItem> Failed,
    string? Error = null)
{
    public static DriverUpdateInstallResult ForError(string message) => new(false, [], [], message);
}

/// <summary>
/// Scans for, and installs, optional Windows Update driver updates through the
/// Windows Update Agent (WUA).
/// </summary>
/// <remarks>
/// <para>
/// Read (<see cref="ScanAsync"/>) and write (<see cref="InstallAsync"/>) are one
/// interface here, unlike the inventory driver collector/installer split, because
/// an install must re-run the very same online search the scan did to resolve the
/// requested identities back to live update objects; the two halves share one
/// search definition and belong together.
/// </para>
/// <para>
/// Both calls block on synchronous COM internally and are run off the caller's
/// thread by the implementation. Neither throws for an expected Windows failure:
/// those come back as an unsuccessful <see cref="DriverUpdateScanResult"/> or a
/// <see cref="DriverUpdateInstallResult"/> with <see cref="DriverUpdateInstallResult.Error"/>
/// set, so the executors can report them cleanly.
/// </para>
/// </remarks>
public interface IDriverUpdateService
{
    /// <summary>
    /// Runs an online scan for pending, non-hidden, not-yet-installed driver updates.
    /// </summary>
    Task<DriverUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and installs the subset of the requested updates that are present,
    /// not a preview, and installable without user input.
    /// </summary>
    /// <param name="updateIds">The requested update identity GUIDs (as normalised strings).</param>
    Task<DriverUpdateInstallResult> InstallAsync(
        IReadOnlyCollection<string> updateIds, CancellationToken cancellationToken = default);
}
