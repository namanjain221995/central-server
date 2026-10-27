using EndpointAgent.Core.Abstractions;

namespace EndpointAgent.Core.Drivers;

/// <summary>
/// The single rule for what an installed driver update's outcome IS, given what
/// WUA reported and what a verification search found afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Pure and platform-neutral, like <see cref="DriverUpdatePreview"/>: the Windows
/// service gathers the facts over COM and hands them here, so the classification
/// table is pinned by unit tests rather than by a device.
/// </para>
/// <para>
/// The rules (driver-update-truth contract, section A), applied in this order:
/// </para>
/// <list type="number">
///   <item>Result code other than 2 (succeeded) or 3 (succeeded with errors) is
///   <see cref="DriverUpdateOutcomeStatus.Failed"/>.</item>
///   <item>Succeeded and a restart is required -- the update's own flag OR the
///   machine's -- is <see cref="DriverUpdateOutcomeStatus.PendingReboot"/>, whatever
///   the verification search said and even if it could not run. A reboot-pending
///   update may legitimately still be offered until the restart, so the agent never
///   brands it re-offered; that judgement needs a post-reboot scan, which the server
///   owns.</item>
///   <item>Succeeded, no restart, verification unavailable is
///   <see cref="DriverUpdateOutcomeStatus.InstalledUnverified"/> -- never a guess.</item>
///   <item>Succeeded, no restart, still offered is
///   <see cref="DriverUpdateOutcomeStatus.ReOffered"/>.</item>
///   <item>Succeeded, no restart, no longer offered is
///   <see cref="DriverUpdateOutcomeStatus.Installed"/>.</item>
/// </list>
/// </remarks>
public static class DriverUpdateOutcomeClassifier
{
    /// <summary>WUA <c>orcSucceeded</c>.</summary>
    public const int ResultSucceeded = 2;

    /// <summary>WUA <c>orcSucceededWithErrors</c>.</summary>
    public const int ResultSucceededWithErrors = 3;

    /// <summary>
    /// The title reported for an update Windows Update did not offer: the agent never
    /// saw it, so it has no title to give. Non-empty so a strict reader never trips on it.
    /// </summary>
    public const string NotOfferedTitle = "(not offered by Windows Update)";

    /// <summary>Whether a WUA result code means the installer reported success.</summary>
    public static bool IsSuccess(int resultCode) =>
        resultCode is ResultSucceeded or ResultSucceededWithErrors;

    /// <summary>Classifies one update that reached the installer.</summary>
    /// <param name="resultCode">The update's WUA <c>OperationResultCode</c>.</param>
    /// <param name="rebootRequired">
    /// The update's own reboot flag (aggregate fallback when unreadable) OR the
    /// machine's reboot-required flag.
    /// </param>
    /// <param name="stillOffered">
    /// Whether the post-install verification search still offers it; null when that
    /// search could not run.
    /// </param>
    public static DriverUpdateOutcomeStatus Classify(int resultCode, bool rebootRequired, bool? stillOffered)
    {
        if (!IsSuccess(resultCode))
        {
            return DriverUpdateOutcomeStatus.Failed;
        }

        if (rebootRequired)
        {
            return DriverUpdateOutcomeStatus.PendingReboot;
        }

        return stillOffered switch
        {
            null => DriverUpdateOutcomeStatus.InstalledUnverified,
            true => DriverUpdateOutcomeStatus.ReOffered,
            false => DriverUpdateOutcomeStatus.Installed,
        };
    }

    /// <summary>
    /// One <see cref="DriverUpdateOutcomeStatus.NotOffered"/> outcome per requested id
    /// the pre-install search did not match, in request order. Before 1.27.0 these
    /// simply vanished from the result whenever any other update proceeded.
    /// </summary>
    /// <param name="requested">The requested ids (normalised).</param>
    /// <param name="matched">The requested ids the pre-install search found (normalised, case-insensitive).</param>
    public static IReadOnlyList<DriverUpdateOutcome> NotOffered(
        IEnumerable<string> requested, IReadOnlySet<string> matched)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(matched);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<DriverUpdateOutcome>();
        foreach (var id in requested)
        {
            if (!matched.Contains(id) && seen.Add(id))
            {
                outcomes.Add(new DriverUpdateOutcome(
                    id, NotOfferedTitle, ResultCode: null, HResult: null, RebootRequired: false,
                    DriverUpdateOutcomeStatus.NotOffered));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// The conservative accounting for a service result that carries no
    /// <see cref="DriverUpdateInstallResult.Outcomes"/> (a pre-1.27.0 shape): what WUA
    /// said installed is <see cref="DriverUpdateOutcomeStatus.PendingReboot"/> when a
    /// restart is required and <see cref="DriverUpdateOutcomeStatus.InstalledUnverified"/>
    /// otherwise -- never Installed or ReOffered, because nothing verified it -- and
    /// what failed is <see cref="DriverUpdateOutcomeStatus.Failed"/>. The same mapping
    /// the server applies to a legacy result.
    /// </summary>
    public static IReadOnlyList<DriverUpdateOutcome> FromLegacy(DriverUpdateInstallResult report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var outcomes = new List<DriverUpdateOutcome>(report.Installed.Count + report.Failed.Count);
        foreach (var item in report.Installed)
        {
            outcomes.Add(new DriverUpdateOutcome(
                item.UpdateId, item.Title, item.ResultCode, item.HResult, report.RebootRequired,
                report.RebootRequired ? DriverUpdateOutcomeStatus.PendingReboot : DriverUpdateOutcomeStatus.InstalledUnverified));
        }

        foreach (var item in report.Failed)
        {
            outcomes.Add(new DriverUpdateOutcome(
                item.UpdateId, item.Title, item.ResultCode, item.HResult, false, DriverUpdateOutcomeStatus.Failed));
        }

        return outcomes;
    }
}
