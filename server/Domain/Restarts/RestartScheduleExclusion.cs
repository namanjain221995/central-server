using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Restarts;

/// <summary>
/// One device an administrator took out of a pending department restart. When
/// the schedule is dispatched the device is skipped, whatever group it is in
/// by then.
/// </summary>
/// <remarks>
/// A row per device rather than a list on the schedule: excluding a device
/// never rewrites what another administrator excluded, and the exclusion of a
/// device that has since left the group is a harmless leftover rather than a
/// reason to reject anything. Each exclusion also bumps the schedule's row
/// version, so an exclusion and the dispatch can never both win; the service
/// re-reads and retries when it loses to another exclusion.
/// </remarks>
public sealed class RestartScheduleExclusion : Entity
{
    private RestartScheduleExclusion()
    {
        Hostname = null!;
        ExcludedByDisplay = null!;
    }

    public RestartScheduleExclusion(
        Guid scheduleId, Guid deviceId, string hostname, Guid excludedByUserId, string excludedByDisplay, DateTimeOffset now)
    {
        ScheduleId = Guard.NotEmpty(scheduleId);
        DeviceId = Guard.NotEmpty(deviceId);
        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), 255);
        ExcludedByUserId = Guard.NotEmpty(excludedByUserId);
        ExcludedByDisplay = Guard.NotNullOrWhiteSpace(excludedByDisplay, nameof(excludedByDisplay), RestartSchedule.MaxDisplayLength);
        ExcludedAt = now;
    }

    public Guid ScheduleId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>The hostname when excluded, for the record; the live one is on the device.</summary>
    public string Hostname { get; private set; }

    public Guid ExcludedByUserId { get; private set; }

    public string ExcludedByDisplay { get; private set; }

    public DateTimeOffset ExcludedAt { get; private set; }
}
