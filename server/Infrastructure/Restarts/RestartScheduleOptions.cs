using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Restarts;

/// <summary>Settings for department-wide scheduled restarts.</summary>
/// <remarks>
/// Validated on start, so an out-of-range value fails the host rather than
/// quietly sending restarts with no warning or never sending them at all.
/// </remarks>
public sealed class RestartScheduleOptions
{
    public const string SectionName = "RestartSchedules";

    /// <summary>
    /// How long before the scheduled moment the restart tasks go out, which is
    /// also the warning Windows shows the signed-in user. Bounded by what every
    /// deployed agent accepts as a grace period.
    /// </summary>
    [Range(30, 3600)]
    public int WarningSeconds { get; init; } = 300;

    /// <summary>
    /// How late a dispatch may run and still be sent. A schedule whose moment
    /// passed by more than this while nothing was dispatching -- the server was
    /// down -- is marked Missed instead: a restart hours after it was expected is
    /// a surprise, not a maintenance window.
    /// </summary>
    [Range(60, 86_400)]
    public int MissedAfterSeconds { get; init; } = 300;

    /// <summary>How often the sweeper looks for schedules that are due.</summary>
    [Range(5, 300)]
    public int SweepIntervalSeconds { get; init; } = 15;
}
