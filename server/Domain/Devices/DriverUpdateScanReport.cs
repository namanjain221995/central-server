namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// The result an agent reports for a <see cref="Tasks.DeviceTaskType.ScanDriverUpdates"/>
/// task: when it scanned and the driver updates Windows Update offered but had not
/// installed. Deserialized from the task's result JSON (camelCase, Web defaults).
/// </summary>
/// <remarks>
/// This is a wire contract carried in an agent-supplied result, so every field is
/// nullable and nothing here is trusted: the ingestion drops an item with no update
/// id or title, bounds the string lengths the store allows, and keeps the first of
/// any duplicate update id. A report with an empty or absent <see cref="Updates"/>
/// list is valid and meaningful -- it means the machine has nothing pending -- and
/// clears the device's rows.
/// </remarks>
public sealed record DriverUpdateScanReport(
    DateTimeOffset? ScannedAt,
    IReadOnlyList<DriverUpdateScanItem>? Updates);

/// <summary>One driver update in a <see cref="DriverUpdateScanReport"/>.</summary>
/// <param name="UpdateId">The Windows Update update id (a GUID). Required; an item without one is dropped.</param>
/// <param name="Revision">The update's revision number.</param>
/// <param name="Title">The update's display title. Required; an item without one is dropped.</param>
/// <param name="DriverClass">The driver class, e.g. "Display", "Net", when known.</param>
/// <param name="DriverProvider">Who publishes the driver, when known.</param>
/// <param name="DriverVerDate">The driver's version date, when known.</param>
/// <param name="IsPreview">Whether Windows Update marks this a preview (beta) driver.</param>
/// <param name="SizeBytes">The download size in bytes, when known.</param>
public sealed record DriverUpdateScanItem(
    string? UpdateId,
    int Revision,
    string? Title,
    string? DriverClass,
    string? DriverProvider,
    DateTimeOffset? DriverVerDate,
    bool IsPreview,
    long? SizeBytes);
