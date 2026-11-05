using EndpointPlatform.Domain.WorkTime;

namespace EndpointPlatform.Domain.Tests.WorkTime;

/// <summary>
/// The work-time setting's shift arithmetic and validation: a night shift crosses
/// midnight, the shift length accounts for it, and bad input is refused.
/// </summary>
public sealed class WorkTimeSettingTests
{
    [Fact]
    public void The_default_is_the_night_shift_7_30pm_to_4_30am_in_IST()
    {
        var setting = new WorkTimeSetting(Guid.NewGuid());

        setting.ShiftStartMinutes.ShouldBe((19 * 60) + 30);
        setting.ShiftEndMinutes.ShouldBe((4 * 60) + 30);
        setting.IdleThresholdMinutes.ShouldBe(5);
        setting.UtcOffsetMinutes.ShouldBe(330);
        setting.CrossesMidnight.ShouldBeTrue();
        setting.ShiftLengthMinutes.ShouldBe(9 * 60); // 19:30 -> 04:30 is nine hours
    }

    [Fact]
    public void A_day_shift_does_not_cross_midnight_and_measures_its_own_span()
    {
        var setting = new WorkTimeSetting(Guid.NewGuid());
        setting.Update(shiftStartMinutes: 9 * 60, shiftEndMinutes: 17 * 60, idleThresholdMinutes: 10, breakMinutes: 30, utcOffsetMinutes: 330);

        setting.CrossesMidnight.ShouldBeFalse();
        setting.ShiftLengthMinutes.ShouldBe(8 * 60);
    }

    [Theory]
    [InlineData(-1, 4 * 60, 5, 60, 330)]       // start out of range
    [InlineData(19 * 60, 1500, 5, 60, 330)]    // end out of range
    [InlineData(19 * 60, 4 * 60, 0, 60, 330)]  // idle threshold below 1
    [InlineData(19 * 60, 4 * 60, 5, 600, 330)] // break above 480
    [InlineData(19 * 60, 4 * 60, 5, 60, 2000)] // offset out of range
    public void Out_of_range_values_are_refused(int start, int end, int idle, int brk, int offset)
    {
        var setting = new WorkTimeSetting(Guid.NewGuid());
        Should.Throw<Exception>(() => setting.Update(start, end, idle, brk, offset));
    }

    [Fact]
    public void A_zero_length_shift_is_refused()
    {
        var setting = new WorkTimeSetting(Guid.NewGuid());
        Should.Throw<ArgumentException>(() => setting.Update(9 * 60, 9 * 60, 5, 60, 330));
    }
}
