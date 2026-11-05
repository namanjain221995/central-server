using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.WorkTime;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.WorkTime;

/// <summary>
/// Turns the raw per-minute activity samples into a work-time report: for each shift
/// date, device and user, how long they were present, actively working, and idle.
/// </summary>
/// <remarks>
/// <para>
/// Samples are UTC; the shift is local. Each sample is converted to local time with the
/// organization's offset and attributed to the shift day it falls in — a night shift
/// that crosses midnight puts its early-morning minutes on the previous day's shift.
/// A minute counts as active when its idle time is below the configurable threshold;
/// idle time is the break.
/// </para>
/// <para>
/// Scoped exactly like the other reports: a scoped administrator is handed the device
/// ids they may see, so the report never names a machine outside their reach.
/// </para>
/// </remarks>
public sealed class WorkTimeReportService(EndpointPlatformDbContext dbContext)
{
    /// <summary>Longest date range a single report may cover.</summary>
    public const int MaxRangeDays = 62;

    /// <summary>Upper bound on samples loaded for one report; past it the result is marked truncated.</summary>
    private const int MaxSamples = 1_000_000;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;

    public async Task<WorkTimeReport> GetReportAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        DateOnly fromDate,
        DateOnly toDate,
        Guid? groupId,
        CancellationToken cancellationToken = default)
    {
        var setting = await _dbContext.WorkTimeSettings.AsNoTracking()
                          .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken)
                      ?? new WorkTimeSetting(organizationId);

        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }
        if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxRangeDays)
        {
            toDate = fromDate.AddDays(MaxRangeDays - 1);
        }

        var offset = TimeSpan.FromMinutes(setting.UtcOffsetMinutes);
        var windowStartUtc = LocalShiftStart(fromDate, setting, offset);
        var windowEndUtc = LocalShiftEnd(toDate, setting, offset);

        var devicesQuery = _dbContext.Devices.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Status == DeviceStatus.Active);
        if (groupId is { } group)
        {
            devicesQuery = devicesQuery.Where(d => d.DeviceGroupId == group);
        }
        if (scopedDeviceIds is not null)
        {
            devicesQuery = devicesQuery.Where(d => scopedDeviceIds.Contains(d.Id));
        }

        var devices = await devicesQuery
            .Select(d => new { d.Id, d.Hostname, d.DeviceGroupId })
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return new WorkTimeReport(ToDto(setting), [], false);
        }

        var deviceIds = devices.Select(d => d.Id).ToList();
        var deviceInfo = devices.ToDictionary(d => d.Id);

        var groupNames = await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => g.OrganizationId == organizationId)
            .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        var samplesQuery = _dbContext.DeviceActivitySamples.AsNoTracking()
            .Where(s => deviceIds.Contains(s.DeviceId) && s.SampledAt >= windowStartUtc && s.SampledAt < windowEndUtc);

        var total = await samplesQuery.CountAsync(cancellationToken);
        var truncated = total > MaxSamples;

        var samples = await samplesQuery
            .OrderBy(s => s.SampledAt)
            .Take(MaxSamples)
            .Select(s => new { s.DeviceId, s.UserName, s.SampledAt, s.IdleSeconds })
            .ToListAsync(cancellationToken);

        var idleThresholdSeconds = setting.IdleThresholdMinutes * 60;
        var buckets = new Dictionary<(DateOnly Date, Guid Device, string User), (int Present, int Active)>();

        foreach (var sample in samples)
        {
            var shiftDate = AttributeShiftDate(sample.SampledAt, setting, offset);
            if (shiftDate is null || shiftDate.Value < fromDate || shiftDate.Value > toDate)
            {
                continue;
            }

            var key = (shiftDate.Value, sample.DeviceId, sample.UserName);
            buckets.TryGetValue(key, out var agg);
            agg.Present += 1;
            if (sample.IdleSeconds < idleThresholdSeconds)
            {
                agg.Active += 1;
            }
            buckets[key] = agg;
        }

        var rows = buckets
            .Select(kv =>
            {
                var info = deviceInfo[kv.Key.Device];
                var groupName = groupNames.GetValueOrDefault(info.DeviceGroupId, "—");
                return new WorkTimeReportRow(
                    kv.Key.Date, kv.Key.Device, info.Hostname, groupName, kv.Key.User,
                    PresentMinutes: kv.Value.Present,
                    ActiveMinutes: kv.Value.Active,
                    IdleMinutes: kv.Value.Present - kv.Value.Active);
            })
            .OrderByDescending(r => r.ShiftDate)
            .ThenBy(r => r.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkTimeReport(ToDto(setting), rows, truncated);
    }

    // Returned in UTC (offset 0): the window bounds go into a timestamptz query, and
    // Npgsql only accepts UTC DateTimeOffset there. Absolute-time comparisons are unchanged.
    private static DateTimeOffset LocalShiftStart(DateOnly date, WorkTimeSetting setting, TimeSpan offset) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), offset).AddMinutes(setting.ShiftStartMinutes).ToUniversalTime();

    private static DateTimeOffset LocalShiftEnd(DateOnly date, WorkTimeSetting setting, TimeSpan offset)
    {
        var endDate = setting.CrossesMidnight ? date.AddDays(1) : date;
        return new DateTimeOffset(endDate.ToDateTime(TimeOnly.MinValue), offset).AddMinutes(setting.ShiftEndMinutes).ToUniversalTime();
    }

    /// <summary>Which shift day a UTC sample belongs to, or null if it falls outside any shift.</summary>
    private static DateOnly? AttributeShiftDate(DateTimeOffset sampledUtc, WorkTimeSetting setting, TimeSpan offset)
    {
        var local = sampledUtc.ToOffset(offset);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        var localMinute = (local.Hour * 60) + local.Minute;

        if (!setting.CrossesMidnight)
        {
            return localMinute >= setting.ShiftStartMinutes && localMinute < setting.ShiftEndMinutes ? localDate : null;
        }

        // Crosses midnight: the evening part [start, midnight) is this day's shift; the
        // early-morning part [midnight, end) belongs to the shift that began yesterday.
        if (localMinute >= setting.ShiftStartMinutes)
        {
            return localDate;
        }
        if (localMinute < setting.ShiftEndMinutes)
        {
            return localDate.AddDays(-1);
        }
        return null;
    }

    private static WorkTimeSettingDto ToDto(WorkTimeSetting s) => new(
        s.ShiftStartMinutes, s.ShiftEndMinutes, s.IdleThresholdMinutes, s.BreakMinutes, s.UtcOffsetMinutes, s.ShiftLengthMinutes);
}

/// <summary>The work-time configuration, with the derived shift length for the dashboard.</summary>
public sealed record WorkTimeSettingDto(
    int ShiftStartMinutes,
    int ShiftEndMinutes,
    int IdleThresholdMinutes,
    int BreakMinutes,
    int UtcOffsetMinutes,
    int ShiftLengthMinutes);

/// <summary>One person's work time on one shift day.</summary>
/// <param name="PresentMinutes">Minutes the user was logged in and sampled during the shift (gross).</param>
/// <param name="ActiveMinutes">Of those, minutes with keyboard/mouse activity (working / actual working).</param>
/// <param name="IdleMinutes">Present minutes with no activity (the break).</param>
public sealed record WorkTimeReportRow(
    DateOnly ShiftDate,
    Guid DeviceId,
    string Hostname,
    string GroupName,
    string UserName,
    int PresentMinutes,
    int ActiveMinutes,
    int IdleMinutes);

/// <summary>A work-time report over a date range, with the settings it was computed against.</summary>
public sealed record WorkTimeReport(
    WorkTimeSettingDto Settings,
    IReadOnlyList<WorkTimeReportRow> Rows,
    bool Truncated);
