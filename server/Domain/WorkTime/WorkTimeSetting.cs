using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.WorkTime;

/// <summary>
/// One organization's work-time configuration: the shift window, how long without input
/// counts as idle, and the expected break. One row per organization, created on first
/// set and updated in place.
/// </summary>
/// <remarks>
/// Shift times are minutes from midnight (local), so a night shift whose end is at or
/// before its start simply crosses midnight. The idle threshold is applied at report
/// time, so changing it re-reads existing samples with the new rule.
/// </remarks>
public sealed class WorkTimeSetting : AuditableEntity
{
    public const int DefaultShiftStartMinutes = (19 * 60) + 30; // 19:30
    public const int DefaultShiftEndMinutes = (4 * 60) + 30;    // 04:30 (next day)
    public const int DefaultIdleThresholdMinutes = 5;
    public const int DefaultBreakMinutes = 60;
    public const int DefaultUtcOffsetMinutes = 330;             // IST (UTC+5:30)

    private const int MinutesPerDay = 24 * 60;

    private WorkTimeSetting()
    {
    }

    public WorkTimeSetting(Guid organizationId)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        ShiftStartMinutes = DefaultShiftStartMinutes;
        ShiftEndMinutes = DefaultShiftEndMinutes;
        IdleThresholdMinutes = DefaultIdleThresholdMinutes;
        BreakMinutes = DefaultBreakMinutes;
        UtcOffsetMinutes = DefaultUtcOffsetMinutes;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// The organization's UTC offset in minutes (e.g. 330 for IST). Samples are stored
    /// in UTC; this converts them to local time to place each one in the right shift day.
    /// </summary>
    public int UtcOffsetMinutes { get; private set; }

    /// <summary>Shift start, minutes from local midnight (0–1439).</summary>
    public int ShiftStartMinutes { get; private set; }

    /// <summary>Shift end, minutes from local midnight (0–1439); at or before the start means it crosses midnight.</summary>
    public int ShiftEndMinutes { get; private set; }

    /// <summary>Minutes without any input before a minute is counted idle (1–120).</summary>
    public int IdleThresholdMinutes { get; private set; }

    /// <summary>The expected break per shift, in minutes (0–480) — a target shown on the report.</summary>
    public int BreakMinutes { get; private set; }

    /// <summary>True when the shift runs past midnight.</summary>
    public bool CrossesMidnight => ShiftEndMinutes <= ShiftStartMinutes;

    /// <summary>The shift's length in minutes, accounting for a midnight crossing.</summary>
    public int ShiftLengthMinutes =>
        CrossesMidnight ? (MinutesPerDay - ShiftStartMinutes) + ShiftEndMinutes : ShiftEndMinutes - ShiftStartMinutes;

    public void Update(int shiftStartMinutes, int shiftEndMinutes, int idleThresholdMinutes, int breakMinutes, int utcOffsetMinutes)
    {
        ShiftStartMinutes = ValidateMinuteOfDay(shiftStartMinutes, nameof(shiftStartMinutes));
        ShiftEndMinutes = ValidateMinuteOfDay(shiftEndMinutes, nameof(shiftEndMinutes));
        IdleThresholdMinutes = ValidateRange(idleThresholdMinutes, 1, 120, nameof(idleThresholdMinutes));
        BreakMinutes = ValidateRange(breakMinutes, 0, 480, nameof(breakMinutes));
        UtcOffsetMinutes = ValidateRange(utcOffsetMinutes, -720, 840, nameof(utcOffsetMinutes));

        // Equal start and end would otherwise read as a 24-hour shift; refuse it as the
        // almost-certain mistake it is.
        if (ShiftStartMinutes == ShiftEndMinutes)
        {
            throw new ArgumentException("The shift start and end cannot be the same time.", nameof(shiftEndMinutes));
        }
    }

    private static int ValidateMinuteOfDay(int value, string paramName) =>
        value is >= 0 and < MinutesPerDay
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, "Must be a minute of the day (0–1439).");

    private static int ValidateRange(int value, int min, int max, string paramName) =>
        value >= min && value <= max
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, $"Must be between {min} and {max}.");
}
