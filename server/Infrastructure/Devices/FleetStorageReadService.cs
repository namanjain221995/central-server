using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Reads the fleet-wide storage overview: how full each active device's fixed
/// volumes are, so an operator can spot a machine about to run out of disk before
/// it starts failing updates, backups or inventory.
/// </summary>
/// <remarks>
/// <para>
/// Read-only over the hardware inventory the agent already uploads
/// (<see cref="DeviceHardware.DisksJson"/>); nothing here reaches a device and no
/// new agent capability is needed. Only ACTIVE devices count — a retired machine's
/// last inventory must not appear as a live low-disk alert.
/// </para>
/// <para>
/// The read is scoped exactly as the fleet-account and driver-update overviews are:
/// a scoped administrator is handed the device ids they may see (null means "all"),
/// and both the rows and the active-device total are computed over those devices
/// only, so the page can never name a machine outside the caller's scope.
/// </para>
/// <para>
/// Severity is derived from free space as a percentage, the one signal that is
/// comparable across a 128 GB laptop and a 2 TB workstation: a volume under
/// <see cref="CriticalFreePercent"/> free is Critical, under
/// <see cref="LowFreePercent"/> is Low, otherwise Ok. A device's severity is the
/// worst of its volumes. The thresholds are returned in the payload so the console
/// labels exactly what the server decided, rather than re-deriving it.
/// </para>
/// </remarks>
public sealed class FleetStorageReadService(
    EndpointPlatformDbContext dbContext,
    ILogger<FleetStorageReadService> logger)
{
    /// <summary>A fixed volume with less than this percent free is Critical.</summary>
    public const double CriticalFreePercent = 5.0;

    /// <summary>A fixed volume with less than this percent free is Low (but not yet Critical).</summary>
    public const double LowFreePercent = 10.0;

    /// <summary>At most this many device rows are returned.</summary>
    private const int MaxDevices = 5000;

    /// <summary>At most this many volumes are kept per device (matches the inventory cap).</summary>
    private const int MaxVolumesPerDevice = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ILogger<FleetStorageReadService> _logger = logger;

    /// <summary>
    /// Every active, in-scope device in the organization with the fixed volumes it
    /// last reported, each volume's free space and severity, and the device's worst
    /// severity — fullest (worst) device first, so the machines that need attention
    /// are at the top.
    /// </summary>
    public async Task<FleetStorageSummary> GetSummaryAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var visible = scopedDeviceIds?.ToList();

        var devicesQuery = _dbContext.Devices.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Status == DeviceStatus.Active);
        if (visible is not null)
        {
            devicesQuery = devicesQuery.Where(d => visible.Contains(d.Id));
        }

        var activeDevices = await devicesQuery.CountAsync(cancellationToken);

        // Left join so a device that has not uploaded hardware yet still appears, as a
        // row with no volumes and Unknown severity, rather than silently vanishing.
        // The hardware side is projected through an explicit null guard: CollectedAt is
        // a non-nullable DateTimeOffset on the entity, and reading it straight from the
        // optional side of the join throws "Nullable object must have a value" the
        // moment a device has no hardware row, so it is lifted to DateTimeOffset? here.
        var rows = await (
            from d in devicesQuery
            join h in _dbContext.DeviceHardware.AsNoTracking() on d.Id equals h.DeviceId into hj
            from h in hj.DefaultIfEmpty()
            select new
            {
                d.Id,
                d.Hostname,
                DisksJson = h != null ? h.DisksJson : null,
                CollectedAt = h != null ? (DateTimeOffset?)h.CollectedAt : null,
            })
            .ToListAsync(cancellationToken);

        var truncated = false;
        if (rows.Count > MaxDevices)
        {
            truncated = true;
            rows = rows.Take(MaxDevices).ToList();
        }

        var devices = new List<DeviceStorageRow>(rows.Count);
        foreach (var row in rows)
        {
            var volumes = ParseVolumes(row.DisksJson, row.Hostname);
            var severity = volumes.Count == 0
                ? StorageSeverity.Unknown
                : volumes.Max(v => v.Severity);

            devices.Add(new DeviceStorageRow(
                row.Id,
                row.Hostname,
                row.DisksJson is null ? null : row.CollectedAt,
                severity,
                volumes));
        }

        // Worst first: Critical, then Low, then Ok, then Unknown; within a severity the
        // tightest volume (lowest free percent) leads, so the most urgent machine is top.
        devices = devices
            .OrderByDescending(d => SeverityRank(d.Severity))
            .ThenBy(d => d.Volumes.Count == 0 ? double.MaxValue : d.Volumes.Min(v => v.FreePercent))
            .ThenBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DeviceId)
            .ToList();

        var lowCount = devices.Count(d => d.Severity == StorageSeverity.Low);
        var criticalCount = devices.Count(d => d.Severity == StorageSeverity.Critical);
        var reporting = devices.Count(d => d.Volumes.Count > 0);

        if (truncated)
        {
            _logger.LogWarning(
                "Fleet storage summary for organization {OrganizationId} was truncated at {MaxDevices} devices.",
                organizationId, MaxDevices);
        }

        return new FleetStorageSummary(
            activeDevices,
            reporting,
            lowCount,
            criticalCount,
            CriticalFreePercent,
            LowFreePercent,
            truncated,
            devices);
    }

    /// <summary>
    /// Parses the stored volume document into typed rows, each with its own severity.
    /// A malformed or absent document yields no volumes rather than throwing — a
    /// single device's bad inventory must not fail the whole overview.
    /// </summary>
    private List<VolumeStorageRow> ParseVolumes(string? disksJson, string hostname)
    {
        if (string.IsNullOrWhiteSpace(disksJson))
        {
            return [];
        }

        List<DiskJson>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<DiskJson>>(disksJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Unreadable disk inventory for device {Hostname}; treating as no volumes.", hostname);
            return [];
        }

        if (parsed is null || parsed.Count == 0)
        {
            return [];
        }

        var volumes = new List<VolumeStorageRow>(Math.Min(parsed.Count, MaxVolumesPerDevice));
        foreach (var disk in parsed.Take(MaxVolumesPerDevice))
        {
            var size = Math.Max(0, disk.SizeBytes);
            var free = Math.Clamp(disk.FreeBytes, 0, size == 0 ? disk.FreeBytes : size);

            // A zero-size volume (never happens for a real fixed disk, but the inventory
            // is agent-reported display data) carries no usable percentage, so it is
            // Unknown rather than a false 0%-free Critical.
            var percent = size == 0 ? 0 : Math.Round(free * 100.0 / size, 1);
            var severity = size == 0
                ? StorageSeverity.Unknown
                : percent < CriticalFreePercent
                    ? StorageSeverity.Critical
                    : percent < LowFreePercent
                        ? StorageSeverity.Low
                        : StorageSeverity.Ok;

            volumes.Add(new VolumeStorageRow(
                string.IsNullOrWhiteSpace(disk.Name) ? "?" : disk.Name,
                disk.FileSystem,
                size,
                free,
                percent,
                severity));
        }

        return volumes
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Orders severities so the worst sorts first; Unknown sinks below Ok.</summary>
    private static int SeverityRank(StorageSeverity severity) => severity switch
    {
        StorageSeverity.Critical => 3,
        StorageSeverity.Low => 2,
        StorageSeverity.Ok => 1,
        _ => 0,
    };

    private sealed record DiskJson(string? Name, string? FileSystem, long SizeBytes, long FreeBytes);
}

/// <summary>How urgently a volume or device needs disk attention.</summary>
/// <remarks>
/// Serialized by name. The admin API has no global string-enum converter — endpoints
/// otherwise stringify enums by hand — so this carries its own converter to reach the
/// dashboard as "Ok"/"Low"/"Critical"/"Unknown" rather than a bare ordinal.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageSeverity
{
    /// <summary>No usable free-space reading (device never reported hardware, or a zero-size volume).</summary>
    Unknown = 0,

    /// <summary>Comfortable free space.</summary>
    Ok = 1,

    /// <summary>Running low — worth watching.</summary>
    Low = 2,

    /// <summary>Nearly full — likely to start failing operations.</summary>
    Critical = 3,
}

/// <summary>The fleet-wide storage overview.</summary>
/// <param name="ActiveDevices">Active devices in the caller's scope.</param>
/// <param name="DevicesReporting">Of those, how many have reported at least one volume.</param>
/// <param name="LowDiskDeviceCount">Devices whose worst volume is Low.</param>
/// <param name="CriticalDiskDeviceCount">Devices whose worst volume is Critical.</param>
/// <param name="CriticalFreePercent">The threshold below which a volume is Critical.</param>
/// <param name="LowFreePercent">The threshold below which a volume is Low.</param>
/// <param name="Truncated">True when the device cap cut the listing short.</param>
/// <param name="Devices">Device rows, worst (fullest) first.</param>
public sealed record FleetStorageSummary(
    int ActiveDevices,
    int DevicesReporting,
    int LowDiskDeviceCount,
    int CriticalDiskDeviceCount,
    double CriticalFreePercent,
    double LowFreePercent,
    bool Truncated,
    IReadOnlyList<DeviceStorageRow> Devices);

/// <summary>One device's storage, per its last inventory.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Hostname">Its hostname.</param>
/// <param name="CollectedAt">When the inventory behind these volumes was collected; null if none yet.</param>
/// <param name="Severity">The worst severity across the device's volumes.</param>
/// <param name="Volumes">The device's fixed volumes.</param>
public sealed record DeviceStorageRow(
    Guid DeviceId,
    string Hostname,
    DateTimeOffset? CollectedAt,
    StorageSeverity Severity,
    IReadOnlyList<VolumeStorageRow> Volumes);

/// <summary>One fixed volume on a device.</summary>
/// <param name="Name">The volume name/mount point (e.g. <c>C:</c>).</param>
/// <param name="FileSystem">Its file system (e.g. NTFS), if reported.</param>
/// <param name="SizeBytes">Total size in bytes.</param>
/// <param name="FreeBytes">Free space in bytes.</param>
/// <param name="FreePercent">Free space as a percentage of size, one decimal.</param>
/// <param name="Severity">This volume's severity.</param>
public sealed record VolumeStorageRow(
    string Name,
    string? FileSystem,
    long SizeBytes,
    long FreeBytes,
    double FreePercent,
    StorageSeverity Severity);
