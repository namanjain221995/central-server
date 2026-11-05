using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.WorkTime;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.WorkTime;

/// <summary>
/// Stores the activity-presence samples a device uploads. Idempotent: a re-sent batch
/// (the agent spools and retries) never creates duplicate minutes, so active/idle
/// counts stay honest.
/// </summary>
public sealed class ActivityIngestService(EndpointPlatformDbContext dbContext, ILogger<ActivityIngestService> logger)
{
    /// <summary>Most samples accepted in one upload (~a day's worth per user).</summary>
    public const int MaxBatch = 2000;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly ILogger<ActivityIngestService> _logger = logger;

    /// <summary>Stores the new minutes from the batch for the authenticated device. Returns how many were inserted.</summary>
    public async Task<int> IngestAsync(Guid deviceId, IReadOnlyList<ActivitySampleDto> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        // Respect the toggle: if work-time is off for this device's organization (or was
        // never turned on), discard whatever a device still uploads, so "off" genuinely
        // stops collection. The device's agent keeps spooling harmlessly until it is on.
        var enabled = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Join(_dbContext.WorkTimeSettings, d => d.OrganizationId, s => s.OrganizationId, (_, s) => s.Enabled)
            .FirstOrDefaultAsync(cancellationToken);
        if (!enabled)
        {
            return 0;
        }

        // Normalise: trim/bound the user name, truncate to the minute, clamp idle, and
        // collapse duplicate minutes within the batch (last one wins).
        var normalized = samples
            .Take(MaxBatch)
            .Where(s => !string.IsNullOrWhiteSpace(s.UserName))
            .Select(s => new
            {
                UserName = Bound(s.UserName.Trim(), 256),
                Minute = TruncateToMinute(s.SampledAt),
                Idle = Math.Max(0, s.IdleSeconds),
            })
            .GroupBy(s => (s.UserName, s.Minute))
            .Select(g => g.Last())
            .ToList();

        if (normalized.Count == 0)
        {
            return 0;
        }

        var earliest = normalized.Min(s => s.Minute);
        var latest = normalized.Max(s => s.Minute);

        var existing = await _dbContext.DeviceActivitySamples.AsNoTracking()
            .Where(s => s.DeviceId == deviceId && s.SampledAt >= earliest && s.SampledAt <= latest)
            .Select(s => new { s.UserName, s.SampledAt })
            .ToListAsync(cancellationToken);

        var existingKeys = existing.Select(e => (e.UserName, e.SampledAt)).ToHashSet();

        var toInsert = normalized
            .Where(s => !existingKeys.Contains((s.UserName, s.Minute)))
            .Select(s => new DeviceActivitySample(deviceId, s.UserName, s.Minute, s.Idle))
            .ToList();

        if (toInsert.Count == 0)
        {
            return 0;
        }

        _dbContext.DeviceActivitySamples.AddRange(toInsert);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent upload inserted the same minute first. The unique index did
            // its job; the data is already there, so this is not an error worth failing
            // the (best-effort, retried) upload over.
            _logger.LogDebug(ex, "Activity ingest for device {DeviceId} hit a duplicate-minute race.", deviceId);
            return 0;
        }

        return toInsert.Count;
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
}
