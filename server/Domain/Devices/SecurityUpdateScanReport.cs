namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// The result an agent reports for a <see cref="Tasks.DeviceTaskType.ScanSecurityUpdates"/>
/// task: when it scanned and the security/quality updates Windows Update offered but
/// had not installed. Deserialized from the task's result JSON (camelCase, Web defaults).
/// </summary>
/// <remarks>
/// This is a wire contract carried in an agent-supplied result, so every field is
/// nullable and nothing here is trusted: the ingestion drops an item with no update
/// id or title, bounds the string lengths the store allows, and keeps the first of
/// any duplicate update id. A report with an empty or absent <see cref="Updates"/>
/// list is valid and meaningful -- it means the machine has nothing pending -- and
/// clears the device's rows.
/// </remarks>
public sealed record SecurityUpdateScanReport(
    DateTimeOffset? ScannedAt,
    IReadOnlyList<SecurityUpdateScanItem>? Updates);

/// <summary>One security update in a <see cref="SecurityUpdateScanReport"/>.</summary>
/// <param name="UpdateId">The Windows Update update id (a GUID). Required; an item without one is dropped.</param>
/// <param name="Revision">The update's revision number.</param>
/// <param name="Title">The update's display title. Required; an item without one is dropped.</param>
/// <param name="KbIds">The KB numbers, comma-joined, e.g. "KB5044284", when known.</param>
/// <param name="Severity">The WUA MsrcSeverity string, e.g. "Critical", "Important", when known.</param>
/// <param name="IsPreview">Whether Windows Update marks this a preview update.</param>
/// <param name="SizeBytes">The download size in bytes, when known.</param>
public sealed record SecurityUpdateScanItem(
    string? UpdateId,
    int Revision,
    string? Title,
    string? KbIds,
    string? Severity,
    bool IsPreview,
    long? SizeBytes);
