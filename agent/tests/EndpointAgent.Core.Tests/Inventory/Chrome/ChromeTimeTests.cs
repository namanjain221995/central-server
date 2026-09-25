using EndpointAgent.Core.Inventory.Chrome;

namespace EndpointAgent.Core.Tests.Inventory.Chrome;

/// <summary>
/// Chrome's two clocks. A wrong date is worse than no date, so anything
/// malformed or implausible must come back null, and nothing may throw: these
/// converters sit on the inventory path.
/// </summary>
public sealed class ChromeTimeTests
{
    // ---- microseconds since 1601 -------------------------------------------------------

    /// <summary>13434562575879741 microseconds after 1601-01-01 is 2026-09-22T14:56:15.879741Z.</summary>
    [Fact]
    public void Microseconds_since_1601_become_the_instant_chrome_meant()
    {
        ChromeTime.FromWindowsMicroseconds("13434562575879741")
            .ShouldBe(new DateTimeOffset(2026, 9, 22, 14, 56, 15, TimeSpan.Zero).AddTicks(8_797_410));
    }

    [Fact]
    public void Microsecond_precision_is_kept()
    {
        // One microsecond apart must not collapse into the same instant.
        var a = ChromeTime.FromWindowsMicroseconds("13434562575879741").ShouldNotBeNull();
        var b = ChromeTime.FromWindowsMicroseconds("13434562575879742").ShouldNotBeNull();

        (b - a).ShouldBe(TimeSpan.FromTicks(10));
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        ChromeTime.FromWindowsMicroseconds("  13434562575879741 \n")
            .ShouldBe(ChromeTime.FromWindowsMicroseconds("13434562575879741"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("13434562575879741.5")]
    [InlineData("1.3434562575879741E16")]
    [InlineData("0x2FBA3F6B3A9D5D")]
    public void A_value_that_is_not_a_positive_whole_number_is_null(string? value)
    {
        ChromeTime.FromWindowsMicroseconds(value).ShouldBeNull();
    }

    [Fact]
    public void The_plausible_range_starts_at_2008()
    {
        ChromeTime.FromWindowsMicroseconds("12843619200000000")
            .ShouldBe(new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("12843619199999999")] // one microsecond before 2008-01-01
    [InlineData("12000000000000000")] // 1981
    [InlineData("1")]                 // 1601
    [InlineData("15746918400000000")] // 2100-01-01 exactly, which is out
    [InlineData("16000000000000000")] // 2108
    public void An_instant_before_2008_or_from_2100_on_is_null(string value)
    {
        ChromeTime.FromWindowsMicroseconds(value).ShouldBeNull();
    }

    /// <summary>
    /// A count too large for any calendar is still just an implausible value. It
    /// must come back null like the rest, not surface as an exception from the
    /// date arithmetic.
    /// </summary>
    [Theory]
    [InlineData("300000000000000000")]  // ~year 11100: past what a DateTimeOffset can hold
    [InlineData("1000000000000000000")] // ten times that
    [InlineData("9223372036854775807")] // long.MaxValue
    public void A_count_beyond_any_calendar_is_null_rather_than_an_exception(string value)
    {
        Should.NotThrow(() => ChromeTime.FromWindowsMicroseconds(value)).ShouldBeNull();
    }

    [Fact]
    public void A_count_that_does_not_fit_a_long_is_null()
    {
        ChromeTime.FromWindowsMicroseconds("9223372036854775808").ShouldBeNull();
    }

    // ---- seconds since 1970 ------------------------------------------------------------

    [Fact]
    public void Unix_seconds_are_floored_to_the_second()
    {
        ChromeTime.FromUnixSeconds(1790357157.504665)
            .ShouldBe(new DateTimeOffset(2026, 9, 25, 17, 25, 57, TimeSpan.Zero));
    }

    [Fact]
    public void A_whole_number_of_unix_seconds_is_exact()
    {
        ChromeTime.FromUnixSeconds(1790357157)
            .ShouldBe(new DateTimeOffset(2026, 9, 25, 17, 25, 57, TimeSpan.Zero));
    }

    [Fact]
    public void A_null_unix_value_is_null()
    {
        ChromeTime.FromUnixSeconds(null).ShouldBeNull();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    [InlineData(-1790357157.0)]
    [InlineData(0.0)]
    [InlineData(double.MaxValue)]
    public void A_unix_value_that_is_not_a_positive_finite_number_is_null(double value)
    {
        ChromeTime.FromUnixSeconds(value).ShouldBeNull();
    }

    [Fact]
    public void The_plausible_unix_range_starts_at_2008()
    {
        ChromeTime.FromUnixSeconds(1199145600)
            .ShouldBe(new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(1199145599.999)] // floors to one second before 2008-01-01
    [InlineData(946684800.0)]    // 2000
    [InlineData(1.0)]            // 1970
    [InlineData(4102444800.0)]   // 2100-01-01 exactly, which is out
    [InlineData(5000000000.0)]   // 2128
    public void A_unix_instant_before_2008_or_from_2100_on_is_null(double value)
    {
        ChromeTime.FromUnixSeconds(value).ShouldBeNull();
    }

    /// <summary>
    /// The same rule as for the Windows clock: a value past the end of the
    /// calendar is implausible, so it is null and never an exception.
    /// </summary>
    [Theory]
    [InlineData(1e12)] // ~year 33658: past what a DateTimeOffset can hold
    [InlineData(1e15)]
    [InlineData(1e18)]
    public void A_unix_value_beyond_any_calendar_is_null_rather_than_an_exception(double value)
    {
        Should.NotThrow(() => ChromeTime.FromUnixSeconds(value)).ShouldBeNull();
    }
}
