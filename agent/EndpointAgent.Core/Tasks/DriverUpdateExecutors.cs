using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Scans for pending optional Windows Update <b>driver</b> updates for a
/// <c>ScanDriverUpdates</c> task and reports the ones the platform offers.
/// </summary>
/// <remarks>
/// <para>
/// Read-only: an online scan changes nothing on the device. The scan itself lives
/// behind <see cref="IDriverUpdateService"/> so the Windows COM is isolated and this
/// executor -- the JSON contract the server and dashboard parse -- is unit-testable
/// with a fake scanner.
/// </para>
/// <para>
/// Preview and beta driver updates are excluded here as well as in the scanner, so a
/// preview can never reach the reported list however the scan was produced. An empty
/// scan is a success with an empty list, never a failure; a scan that could not run
/// (the update service stopped, a COM error) is the honest failure the service
/// reported.
/// </para>
/// </remarks>
public sealed class ScanDriverUpdatesExecutor(
    IDriverUpdateService service,
    ILogger<ScanDriverUpdatesExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly IDriverUpdateService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ILogger<ScanDriverUpdatesExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "ScanDriverUpdates";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var scan = await _service.ScanAsync(cancellationToken);

        if (!scan.Succeeded)
        {
            _logger.LogWarning("Driver update scan did not complete: {Message}", scan.Message);
            return new AgentTaskResult(
                false, scan.Message ?? "The driver update scan could not be completed.", null);
        }

        // Belt and braces: the scanner already drops previews, but the exclusion is
        // enforced here too so the reported list can never carry one.
        var offered = scan.Updates.Where(u => !u.IsPreview).ToList();

        var resultJson = BuildResultJson(_time.GetUtcNow(), offered);

        var message = offered.Count == 1
            ? "Found 1 driver update."
            : $"Found {offered.Count} driver update(s).";

        _logger.LogInformation("{Message}", message);
        return new AgentTaskResult(true, message, resultJson);
    }

    /// <summary>
    /// The scan result document: when it ran and the updates offered. Matches the
    /// shape the server and dashboard parse exactly.
    /// </summary>
    internal static string BuildResultJson(DateTimeOffset scannedAt, IReadOnlyList<DriverUpdateInfo> updates) =>
        JsonSerializer.Serialize(new
        {
            scannedAt,
            updates = updates.Select(u => new
            {
                updateId = u.UpdateId,
                revision = u.Revision,
                title = u.Title,
                driverClass = u.DriverClass,
                driverProvider = u.DriverProvider,
                driverVerDate = u.DriverVerDate,
                isPreview = u.IsPreview,
                sizeBytes = u.SizeBytes,
            }),
        });
}

/// <summary>
/// Installs the selected optional driver updates for an <c>InstallDriverUpdate</c>
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
/// message that says a restart is needed -- the InstallDriverPackage / InstallPackage
/// convention. The task fails only when nothing installed and at least one update
/// failed, or when the operation could not run at all.
/// </para>
/// </remarks>
public sealed class InstallDriverUpdateExecutor(
    IDriverUpdateService service,
    ILogger<InstallDriverUpdateExecutor> logger) : ITaskExecutor
{
    private readonly IDriverUpdateService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ILogger<InstallDriverUpdateExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string TaskType => "InstallDriverUpdate";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var updateIds, out var refusal))
        {
            _logger.LogWarning("Install-driver-update task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return new AgentTaskResult(false, refusal, null);
        }

        var report = await _service.InstallAsync(updateIds, cancellationToken);

        var resultJson = BuildResultJson(report);

        if (report.Error is not null)
        {
            _logger.LogWarning("Driver update install did not run: {Message}", report.Error);
            return new AgentTaskResult(false, report.Error, resultJson);
        }

        // Success when at least one update installed, or when nothing failed. A
        // required restart is still a success -- the driver is staged, it simply is
        // not active yet.
        var succeeded = report.Failed.Count == 0 || report.Installed.Count > 0;

        var message = $"Installed {report.Installed.Count} driver update(s)";
        if (report.Failed.Count > 0)
        {
            message += $"; {report.Failed.Count} failed";
        }

        if (report.RebootRequired)
        {
            message += "; a restart is required before they take effect";
        }

        message += ".";

        _logger.LogInformation("{Message}", message);
        return new AgentTaskResult(succeeded, message, resultJson);
    }

    /// <summary>
    /// The install result document: the reboot flag and the per-update split. Matches
    /// the shape the server and dashboard parse exactly.
    /// </summary>
    internal static string BuildResultJson(DriverUpdateInstallResult report) =>
        JsonSerializer.Serialize(new
        {
            rebootRequired = report.RebootRequired,
            installed = report.Installed.Select(ToJson),
            failed = report.Failed.Select(ToJson),
        });

    private static object ToJson(DriverUpdateInstallItem item) => new
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
            refusal = "The install-driver-update task carried no payload.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            refusal = "The install-driver-update payload was not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("updateIds", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                refusal = "The install-driver-update payload named no update ids.";
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
                refusal = "The install-driver-update payload named no valid update ids.";
                return false;
            }

            updateIds = set;
            refusal = string.Empty;
            return true;
        }
    }
}
