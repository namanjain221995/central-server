namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// What became of ONE requested Windows Update update -- a driver update or a
/// security update alike -- as the agent can honestly tell it at the moment the
/// install task finishes. A closed set: every requested update lands in exactly one
/// of these, so nothing the operator selected silently vanishes from the result.
/// </summary>
/// <remarks>
/// <para>
/// One set for both update kinds (driver since agent 1.27.0, security since 1.28.0):
/// the update-truth contracts give both the same eight outcomes with the same wire
/// strings, so sharing the type is what keeps them from drifting apart.
/// </para>
/// <para>
/// The wire strings are pinned by <see cref="UpdateOutcomeStatusNames"/>, not by
/// these member names, so a rename here can never change the contract.
/// </para>
/// </remarks>
public enum UpdateOutcomeStatus
{
    /// <summary>WUA reported success, no restart is needed, and a fresh search no longer offers it.</summary>
    Installed,

    /// <summary>WUA reported success, no restart is needed, but the verification search could not run -- so it is not confirmed.</summary>
    InstalledUnverified,

    /// <summary>
    /// WUA reported success and a restart is required (the update's own flag or the
    /// machine's). Whether it is still offered is NOT judged: a reboot-pending update
    /// may legitimately stay offered until the restart, and that judgement needs a
    /// post-reboot scan, which the server owns.
    /// </summary>
    PendingReboot,

    /// <summary>WUA reported success, no restart is needed, and a fresh search STILL offers it: the "claims success, does not stick" class.</summary>
    ReOffered,

    /// <summary>WUA reported failure for it, or it never reached the installer because the operation stopped (download failure, installer busy).</summary>
    Failed,

    /// <summary>The server asked for it, but Windows Update did not offer it to this device at install time (already installed meanwhile, or no longer applicable).</summary>
    NotOffered,

    /// <summary>It is a preview/beta update, which the platform never installs.</summary>
    SkippedPreview,

    /// <summary>Its licence could not be accepted, so it was not staged.</summary>
    EulaDeclined,
}

/// <summary>The exact wire strings for <see cref="UpdateOutcomeStatus"/> (the update-truth contracts, section A).</summary>
public static class UpdateOutcomeStatusNames
{
    /// <summary>The contract string for <paramref name="status"/>.</summary>
    public static string ToWire(UpdateOutcomeStatus status) => status switch
    {
        UpdateOutcomeStatus.Installed => "Installed",
        UpdateOutcomeStatus.InstalledUnverified => "InstalledUnverified",
        UpdateOutcomeStatus.PendingReboot => "PendingReboot",
        UpdateOutcomeStatus.ReOffered => "ReOffered",
        UpdateOutcomeStatus.Failed => "Failed",
        UpdateOutcomeStatus.NotOffered => "NotOffered",
        UpdateOutcomeStatus.SkippedPreview => "SkippedPreview",
        UpdateOutcomeStatus.EulaDeclined => "EulaDeclined",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not an update outcome."),
    };
}

/// <summary>One requested update's classified outcome.</summary>
/// <param name="UpdateId">The update identity GUID (lower-case, no braces).</param>
/// <param name="Title">
/// The update's title. For <see cref="UpdateOutcomeStatus.NotOffered"/> the agent
/// never saw the update, so this is a fixed placeholder, never empty; the server
/// already holds the real title on its pending row.
/// </param>
/// <param name="ResultCode">
/// The WUA <c>OperationResultCode</c> of the operation that decided the outcome (the
/// install, or for an update that failed to download, the download). Null when the
/// update never reached an operation that produces one (NotOffered, SkippedPreview,
/// EulaDeclined, or a Failed update stopped before the installer ran).
/// </param>
/// <param name="HResult">The matching HRESULT, null under the same conditions as <paramref name="ResultCode"/>.</param>
/// <param name="RebootRequired">
/// The update's OWN reboot flag (<c>IUpdateInstallationResult.RebootRequired</c>),
/// falling back to the install's aggregate flag when the per-update read fails --
/// never to false. False for updates that never reached the installer.
/// </param>
/// <param name="Status">The classification; see <see cref="UpdateOutcomeStatus"/>.</param>
public sealed record UpdateOutcome(
    string UpdateId,
    string Title,
    int? ResultCode,
    int? HResult,
    bool RebootRequired,
    UpdateOutcomeStatus Status);
