namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// The result an agent reports for a <see cref="Tasks.DeviceTaskType.InstallDriverUpdate"/>
/// task. Deserialized from the task's result JSON (camelCase, Web defaults).
/// </summary>
/// <remarks>
/// <para>
/// A superset wire contract. Every agent since 1.21.0 sends <see cref="RebootRequired"/>,
/// <see cref="Installed"/> and <see cref="Failed"/>; agent 1.27.0 adds
/// <see cref="Outcomes"/> (one classified outcome per requested update),
/// <see cref="Verified"/> and <see cref="PostScan"/> -- the offered list from the
/// verification search it ran after installing, shape-identical to a scan result so
/// the server applies it through the very same code as a scan.
/// </para>
/// <para>
/// Agent-supplied, so every field is nullable and nothing is trusted: the ingestion
/// drops items with no update id, bounds lengths, keeps the first of any duplicate
/// and caps the count, exactly as it does for a scan.
/// </para>
/// <para>
/// <c>hresult</c> is read as a 64-bit number so an agent that writes the HRESULT
/// unsigned (0x80240017 as 2149842967) parses as well as one that writes it signed.
/// </para>
/// </remarks>
public sealed record DriverUpdateInstallReport(
    bool? RebootRequired,
    IReadOnlyList<DriverUpdateInstallReportItem>? Installed,
    IReadOnlyList<DriverUpdateInstallReportItem>? Failed,
    IReadOnlyList<DriverUpdateInstallOutcomeItem>? Outcomes,
    bool? Verified,
    DriverUpdateScanReport? PostScan);

/// <summary>One update in the legacy <c>installed</c> / <c>failed</c> lists.</summary>
public sealed record DriverUpdateInstallReportItem(
    string? UpdateId,
    string? Title,
    int? ResultCode,
    long? Hresult);

/// <summary>One classified per-update outcome (agent 1.27.0+).</summary>
/// <param name="Status">
/// One of <c>Installed</c>, <c>InstalledUnverified</c>, <c>PendingReboot</c>,
/// <c>ReOffered</c>, <c>Failed</c>, <c>NotOffered</c>, <c>SkippedPreview</c>,
/// <c>EulaDeclined</c>.
/// </param>
public sealed record DriverUpdateInstallOutcomeItem(
    string? UpdateId,
    string? Title,
    int? ResultCode,
    long? Hresult,
    bool? RebootRequired,
    string? Status);
