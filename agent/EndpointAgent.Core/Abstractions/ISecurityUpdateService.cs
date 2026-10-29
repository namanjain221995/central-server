namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// One pending Windows Update security/quality update discovered by an online scan.
/// </summary>
/// <remarks>
/// A flat, secret-free projection of the handful of <c>IUpdate</c> properties the
/// console shows. <see cref="UpdateId"/> (the WUA <c>Identity.UpdateID</c> GUID) is
/// the stable key an install request names; <see cref="Revision"/> is carried
/// alongside for display only.
/// </remarks>
/// <param name="UpdateId">The WUA update identity GUID (lower-case, no braces). The stable key.</param>
/// <param name="Revision">The update's revision number, for display.</param>
/// <param name="Title">The update's title.</param>
/// <param name="KbIds">The KB article ids, comma-joined with a "KB" prefix each (e.g. "KB5044284"), or null when none.</param>
/// <param name="Severity">The MSRC severity (e.g. "Critical", "Important"), or null when Microsoft assigned none.</param>
/// <param name="IsPreview">Whether this update is a beta/preview that the platform excludes.</param>
/// <param name="SizeBytes">The maximum download size in bytes.</param>
public sealed record SecurityUpdateInfo(
    string UpdateId,
    int Revision,
    string Title,
    string? KbIds,
    string? Severity,
    bool IsPreview,
    long SizeBytes);

/// <summary>The outcome of an online security-update scan.</summary>
/// <remarks>
/// An empty but successful scan (<see cref="Succeeded"/> true, no
/// <see cref="Updates"/>) is the normal "nothing pending" case and is never an
/// error. A scan that could not run at all -- the update service stopped, a
/// rejected search, any COM failure -- is <see cref="Succeeded"/> false with a
/// human-readable, secret-free <see cref="Message"/>.
/// </remarks>
public sealed record SecurityUpdateScanResult(
    bool Succeeded,
    string? Message,
    IReadOnlyList<SecurityUpdateInfo> Updates)
{
    public static SecurityUpdateScanResult Ok(IReadOnlyList<SecurityUpdateInfo> updates) => new(true, null, updates);

    public static SecurityUpdateScanResult Failure(string message) => new(false, message, []);
}

/// <summary>One update's per-update install result.</summary>
/// <param name="UpdateId">The update identity GUID this result is for.</param>
/// <param name="Title">The update's title.</param>
/// <param name="ResultCode">The WUA <c>OperationResultCode</c> (2 succeeded, 3 succeeded-with-errors, 4 failed).</param>
/// <param name="HResult">The per-update HRESULT (0 on success).</param>
public sealed record SecurityUpdateInstallItem(
    string UpdateId,
    string Title,
    int ResultCode,
    int HResult);

/// <summary>The outcome of installing a set of requested security updates.</summary>
/// <remarks>
/// <para>
/// <see cref="Error"/> is non-null only when the operation could not run at all
/// (the update service is stopped, another operation is in progress, a reboot is
/// pending, nothing could be staged or downloaded, or any COM failure). In that case
/// <see cref="Installed"/> and <see cref="Failed"/> are empty -- exactly as before
/// agent 1.28.0 -- while <see cref="Outcomes"/> still itemizes whatever is known
/// about each requested update.
/// </para>
/// <para>
/// When <see cref="Error"/> is null the operation ran and the per-update split is
/// authoritative: an update is in <see cref="Installed"/> when WUA reported success
/// (or success-with-errors) for it, and in <see cref="Failed"/> otherwise.
/// <see cref="RebootRequired"/> follows the install result and the system-wide
/// reboot flag. These two lists keep their pre-1.28.0 meaning on purpose: they are
/// the legacy keys of the result document.
/// </para>
/// <para>
/// <see cref="Outcomes"/> (1.28.0+) is the full accounting -- one entry per requested
/// update, classified against a verification search run after the install, by the
/// same <see cref="EndpointAgent.Core.Updates.UpdateOutcomeClassifier"/> the driver
/// install uses.
/// <see cref="PostScan"/> is that verification search's offered set (previews
/// excluded), or null when it could not run; <see cref="Verified"/> says which.
/// A service that predates outcomes leaves <see cref="Outcomes"/> null and the
/// executor derives a conservative accounting from the legacy lists.
/// </para>
/// </remarks>
public sealed record SecurityUpdateInstallResult(
    bool RebootRequired,
    IReadOnlyList<SecurityUpdateInstallItem> Installed,
    IReadOnlyList<SecurityUpdateInstallItem> Failed,
    string? Error = null,
    IReadOnlyList<UpdateOutcome>? Outcomes = null,
    IReadOnlyList<SecurityUpdateInfo>? PostScan = null)
{
    /// <summary>Whether the post-install verification search ran (and so <see cref="PostScan"/> is the live offered set).</summary>
    public bool Verified => PostScan is not null;

    public static SecurityUpdateInstallResult ForError(string message) => new(false, [], [], message);

    /// <summary>An operation that could not run, still itemizing what is known about each requested update.</summary>
    public static SecurityUpdateInstallResult ForError(string message, IReadOnlyList<UpdateOutcome> outcomes) =>
        new(false, [], [], message, outcomes);
}

/// <summary>
/// Scans for, and installs, pending Windows Update security/quality updates through
/// the Windows Update Agent (WUA).
/// </summary>
/// <remarks>
/// <para>
/// Read (<see cref="ScanAsync"/>) and write (<see cref="InstallAsync"/>) are one
/// interface here, exactly like the driver-update service, because an install must
/// re-run the very same online search the scan did to resolve the requested
/// identities back to live update objects; the two halves share one search
/// definition and belong together.
/// </para>
/// <para>
/// Both calls block on synchronous COM internally and are run off the caller's
/// thread by the implementation. Neither throws for an expected Windows failure:
/// those come back as an unsuccessful <see cref="SecurityUpdateScanResult"/> or a
/// <see cref="SecurityUpdateInstallResult"/> with <see cref="SecurityUpdateInstallResult.Error"/>
/// set, so the executors can report them cleanly.
/// </para>
/// </remarks>
public interface ISecurityUpdateService
{
    /// <summary>
    /// Runs an online scan for pending, non-hidden, not-yet-installed
    /// security/quality (software) updates.
    /// </summary>
    Task<SecurityUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and installs the subset of the requested updates that are present
    /// and not a preview, then re-runs the same search to verify which left the
    /// offered set, and classifies every requested update.
    /// </summary>
    /// <param name="updateIds">The requested update identity GUIDs (as normalised strings).</param>
    Task<SecurityUpdateInstallResult> InstallAsync(
        IReadOnlyCollection<string> updateIds, CancellationToken cancellationToken = default);
}
