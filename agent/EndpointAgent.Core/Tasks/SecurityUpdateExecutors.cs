using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Updates;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Scans for pending Windows Update <b>security/quality</b> updates for a
/// <c>ScanSecurityUpdates</c> task and reports the ones the platform offers.
/// </summary>
/// <remarks>
/// <para>
/// Read-only: an online scan changes nothing on the device. The scan itself lives
/// behind <see cref="ISecurityUpdateService"/> so the Windows COM is isolated and this
/// executor -- the JSON contract the server and dashboard parse -- is unit-testable
/// with a fake scanner.
/// </para>
/// <para>
/// Preview and beta updates (the optional "Preview" cumulative updates) are excluded
/// here as well as in the scanner, so a preview can never reach the reported list
/// however the scan was produced. An empty scan is a success with an empty list,
/// never a failure; a scan that could not run (the update service stopped, a COM
/// error) is the honest failure the service reported.
/// </para>
/// </remarks>
public sealed class ScanSecurityUpdatesExecutor(
    ISecurityUpdateService service,
    ILogger<ScanSecurityUpdatesExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly ISecurityUpdateService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ILogger<ScanSecurityUpdatesExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "ScanSecurityUpdates";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var scan = await _service.ScanAsync(cancellationToken);

        if (!scan.Succeeded)
        {
            _logger.LogWarning("Security update scan did not complete: {Message}", scan.Message);
            return new AgentTaskResult(
                false, scan.Message ?? "The security update scan could not be completed.", null);
        }

        // Belt and braces: the scanner already drops previews, but the exclusion is
        // enforced here too so the reported list can never carry one.
        var offered = scan.Updates.Where(u => !u.IsPreview).ToList();

        var resultJson = BuildResultJson(_time.GetUtcNow(), offered);

        var message = offered.Count == 1
            ? "Found 1 security update."
            : $"Found {offered.Count} security update(s).";

        _logger.LogInformation("{Message}", message);
        return new AgentTaskResult(true, message, resultJson);
    }

    /// <summary>
    /// Most updates one scan document carries -- the same ceiling the server's scan
    /// ingestion applies, so nothing the agent sends is silently cut there instead.
    /// </summary>
    internal const int MaxReportedUpdates = 500;

    /// <summary>
    /// The scan result document: when it ran and the updates offered. Matches the
    /// shape the server and dashboard parse exactly.
    /// </summary>
    internal static string BuildResultJson(DateTimeOffset scannedAt, IReadOnlyList<SecurityUpdateInfo> updates)
    {
        var document = BuildScanDocument(scannedAt, updates);
        return JsonSerializer.Serialize(document, document.GetType());
    }

    /// <summary>
    /// The scan document as an object: the ONE code path for both a scan's result and
    /// the <c>postScan</c> an install carries (agent 1.28.0+), so the server can feed
    /// either to the same parser. Previews are excluded here (belt and braces) and the
    /// list is capped at <see cref="MaxReportedUpdates"/>.
    /// </summary>
    internal static object BuildScanDocument(DateTimeOffset scannedAt, IEnumerable<SecurityUpdateInfo> updates) =>
        new
        {
            scannedAt,
            updates = updates
                .Where(u => !u.IsPreview)
                .Take(MaxReportedUpdates)
                .Select(u => new
                {
                    updateId = u.UpdateId,
                    revision = u.Revision,
                    title = u.Title,
                    kbIds = u.KbIds,
                    severity = u.Severity,
                    isPreview = u.IsPreview,
                    sizeBytes = u.SizeBytes,
                })
                .ToList(),
        };
}

/// <summary>
/// Installs the selected security updates for an <c>InstallSecurityUpdate</c>
/// task and reports which took, which failed, and whether a restart is required.
/// </summary>
/// <remarks>
/// <para>
/// The payload names update identities only; nothing about the install is a
/// free-form string. The identities are validated as GUIDs and normalised before
/// they reach the service, which re-runs the scan to resolve them to live updates
/// and installs the ones that are present, not a preview, and installable without
/// user input.
/// </para>
/// <para>
/// A required restart is a <b>success</b>, reported with the structured result and a
/// message that says a restart is needed -- the InstallDriverUpdate / InstallPackage
/// convention. The task fails only when nothing installed and at least one update
/// failed, or when the operation could not run at all.
/// </para>
/// <para>
/// The result document is a SUPERSET (agent 1.28.0, security-update-truth contract
/// section A): every pre-1.28.0 key keeps its name and meaning, so an older server
/// reads it unchanged, and three keys are appended -- <c>outcomes</c> (one classified
/// entry per requested update), <c>verified</c> (whether the post-install
/// verification search ran) and <c>postScan</c> (that search's offered set, built by
/// the very code that builds a scan result, or null when it could not run). The
/// message is honest per class: what installed, what needs a restart, what Windows
/// Update re-offered, what failed, and what was never offered. A security update
/// almost always needs a restart, so "needs a restart to finish" is the common, and
/// correct, outcome.
/// </para>
/// </remarks>
public sealed class InstallSecurityUpdateExecutor(
    ISecurityUpdateService service,
    ILogger<InstallSecurityUpdateExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly ISecurityUpdateService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ILogger<InstallSecurityUpdateExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "InstallSecurityUpdate";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var updateIds, out var refusal))
        {
            _logger.LogWarning("Install-security-update task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return new AgentTaskResult(false, refusal, null);
        }

        var report = await _service.InstallAsync(updateIds, cancellationToken);

        // A service that predates outcomes gets the conservative accounting (never
        // Installed or ReOffered without a verification search behind it).
        var outcomes = report.Outcomes ?? UpdateOutcomeClassifier.FromLegacy(report);

        var resultJson = BuildResultJson(report, outcomes, _time.GetUtcNow());

        if (report.Error is not null)
        {
            _logger.LogWarning("Security update install did not run: {Message}", report.Error);
            return new AgentTaskResult(false, report.Error, resultJson);
        }

        // Success when at least one update installed, or when nothing failed. A
        // required restart is still a success -- the update is staged, it simply is
        // not active yet. Decided on the legacy split, exactly as before 1.28.0;
        // the outcomes and the message carry the finer truth.
        var succeeded = report.Failed.Count == 0 || report.Installed.Count > 0;

        var message = DescribeOutcomes(outcomes);

        _logger.LogInformation("{Message}", message);
        return new AgentTaskResult(succeeded, message, resultJson);
    }

    /// <summary>
    /// The install result document. The first three keys are the pre-1.28.0 shape,
    /// unchanged; <c>outcomes</c>, <c>verified</c> and <c>postScan</c> are appended.
    /// </summary>
    /// <param name="report">The service's report.</param>
    /// <param name="outcomes">The per-update accounting (the report's own, or the legacy derivation).</param>
    /// <param name="verifiedAt">The <c>scannedAt</c> stamped on <c>postScan</c>.</param>
    internal static string BuildResultJson(
        SecurityUpdateInstallResult report, IReadOnlyList<UpdateOutcome> outcomes, DateTimeOffset verifiedAt) =>
        JsonSerializer.Serialize(new
        {
            rebootRequired = report.RebootRequired,
            installed = report.Installed.Select(ToJson),
            failed = report.Failed.Select(ToJson),
            outcomes = outcomes.Select(o => new
            {
                updateId = o.UpdateId,
                title = o.Title,
                resultCode = o.ResultCode,
                hresult = o.HResult,
                rebootRequired = o.RebootRequired,
                status = UpdateOutcomeStatusNames.ToWire(o.Status),
            }),
            verified = report.Verified,

            // The SAME object a ScanSecurityUpdates result serializes, so the server can
            // apply it with its existing scan parser. Null when verification failed.
            postScan = report.PostScan is null
                ? null
                : ScanSecurityUpdatesExecutor.BuildScanDocument(verifiedAt, report.PostScan),
        });

    /// <summary>
    /// The operator-facing summary, one clause per outcome class that occurred, e.g.
    /// "Installed 1; 1 needs a restart to finish; 2 were re-offered by Windows Update
    /// and cannot be completed by the platform."
    /// </summary>
    internal static string DescribeOutcomes(IReadOnlyList<UpdateOutcome> outcomes)
    {
        int Count(UpdateOutcomeStatus status) => outcomes.Count(o => o.Status == status);

        var installed = Count(UpdateOutcomeStatus.Installed);
        var unverified = Count(UpdateOutcomeStatus.InstalledUnverified);
        var pendingReboot = Count(UpdateOutcomeStatus.PendingReboot);
        var reOffered = Count(UpdateOutcomeStatus.ReOffered);
        var failed = Count(UpdateOutcomeStatus.Failed);
        var notOffered = Count(UpdateOutcomeStatus.NotOffered);
        var preview = Count(UpdateOutcomeStatus.SkippedPreview);
        var eula = Count(UpdateOutcomeStatus.EulaDeclined);

        var parts = new List<string>();
        if (installed > 0)
        {
            parts.Add($"Installed {installed}");
        }

        if (unverified > 0)
        {
            parts.Add($"{unverified} reported installed but could not be verified");
        }

        if (pendingReboot > 0)
        {
            parts.Add($"{pendingReboot} {(pendingReboot == 1 ? "needs" : "need")} a restart to finish");
        }

        if (reOffered > 0)
        {
            parts.Add($"{reOffered} {(reOffered == 1 ? "was" : "were")} re-offered by Windows Update "
                + "and cannot be completed by the platform");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        if (notOffered > 0)
        {
            parts.Add($"{notOffered} not offered by Windows Update");
        }

        if (preview > 0)
        {
            parts.Add($"{preview} skipped as a preview");
        }

        if (eula > 0)
        {
            parts.Add($"{eula} skipped because the licence could not be accepted");
        }

        return parts.Count == 0
            ? "No security updates were installed."
            : string.Join("; ", parts) + ".";
    }

    private static object ToJson(SecurityUpdateInstallItem item) => new
    {
        updateId = item.UpdateId,
        title = item.Title,
        resultCode = item.ResultCode,
        hresult = item.HResult,
    };

    /// <summary>
    /// Reads the update identities the payload names. Strict: a missing, malformed, or
    /// empty <c>updateIds</c>, or one with no parseable GUID, is a refusal that installs
    /// nothing. Identities are normalised (lower-case, no braces) so they compare cleanly
    /// against the WUA identity the scan returns. <c>issuedAt</c> and any other property
    /// are tolerated and ignored -- the payload arrives over the authenticated channel.
    /// </summary>
    internal static bool TryParse(string? payloadJson, out IReadOnlyCollection<string> updateIds, out string refusal)
    {
        updateIds = [];

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            refusal = "The install-security-update task carried no payload.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            refusal = "The install-security-update payload was not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("updateIds", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                refusal = "The install-security-update payload named no update ids.";
                return false;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in ids.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String
                    && Guid.TryParse(element.GetString(), out var guid))
                {
                    set.Add(guid.ToString());
                }
            }

            if (set.Count == 0)
            {
                refusal = "The install-security-update payload named no valid update ids.";
                return false;
            }

            updateIds = set;
            refusal = string.Empty;
            return true;
        }
    }
}
