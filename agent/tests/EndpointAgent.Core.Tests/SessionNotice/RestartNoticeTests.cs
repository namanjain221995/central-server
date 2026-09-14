using System.Reflection;
using System.Text;
using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.Core.Tests.SessionNotice;

/// <summary>
/// The notice's wire format and what the window shows for it.
/// </summary>
/// <remarks>
/// The reader runs as an ordinary user and parses bytes from a pipe, so the
/// parser is tested as the attack surface it is: everything that is not exactly
/// a notice must be refused.
/// </remarks>
public sealed class RestartNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static bool Decode(string line, out RestartNotice? notice) =>
        RestartNoticeProtocol.TryDecode(Encoding.UTF8.GetBytes(line), out notice);

    // ------------------------------------------------------------- protocol

    [Fact]
    public void A_notice_round_trips_exactly()
    {
        var notice = new RestartNotice(Now.AddMinutes(10), 600);

        var line = RestartNoticeProtocol.Encode(notice);

        line[^1].ShouldBe((byte)'\n');
        RestartNoticeProtocol.TryDecode(line.AsSpan(0, line.Length - 1), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(notice);
    }

    [Fact]
    public void The_encoded_line_carries_only_the_time_and_the_grace_period()
    {
        var text = Encoding.UTF8.GetString(RestartNoticeProtocol.Encode(new RestartNotice(Now, 60)));

        text.ShouldContain("\"v\":1");
        text.ShouldContain("\"restartAt\"");
        text.ShouldContain("\"graceSeconds\":60");
        text.ShouldNotContain("message", Case.Insensitive);
        text.Length.ShouldBeLessThan(RestartNoticeProtocol.MaxLineBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z"}""")]
    [InlineData("""{"v":1,"graceSeconds":60}""")]
    [InlineData("""{"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60}""")]
    [InlineData("""{"v":2,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60}""")]
    [InlineData("""{"v":"1","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60}""")]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":-1}""")]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":3601}""")]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":1.5}""")]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":"60"}""")]
    [InlineData("""{"v":1,"restartAt":"not a date","graceSeconds":60}""")]
    [InlineData("""{"v":1,"restartAt":12345,"graceSeconds":60}""")]
    public void Anything_that_is_not_exactly_a_notice_is_refused(string line)
    {
        Decode(line, out var notice).ShouldBeFalse($"'{line}' must be refused");
        notice.ShouldBeNull();
    }

    /// <summary>
    /// An extra property is not tolerated. A sender that adds a "message" is not
    /// the sender this reader was written for, and the reader must never become a
    /// way to put words in front of a user.
    /// </summary>
    [Theory]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"message":"Click here to keep working"}""")]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"command":"calc.exe"}""")]
    [InlineData("""{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"extra":null}""")]
    public void An_extra_property_is_refused_whatever_it_is(string line)
    {
        Decode(line, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_line_longer_than_the_protocol_allows_is_refused_unread()
    {
        var padded = """{"v":1,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60}""".PadRight(RestartNoticeProtocol.MaxLineBytes + 1);

        Decode(padded, out _).ShouldBeFalse();
    }

    // ----------------------------------------------------------------- view

    [Fact]
    public void No_notice_shows_nothing()
    {
        RestartNoticeView.For(null, Now).Visible.ShouldBeFalse();
    }

    [Theory]
    [InlineData(47, "00:00:47")]
    [InlineData(600, "00:10:00")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3599, "00:59:59")]
    public void A_pending_restart_counts_down_in_hours_minutes_and_seconds(int secondsLeft, string expected)
    {
        var view = RestartNoticeView.For(new RestartNotice(Now.AddSeconds(secondsLeft), secondsLeft), Now);

        view.Visible.ShouldBeTrue();
        view.Restarting.ShouldBeFalse("it must not claim to be restarting while it is still counting down");
        view.Countdown.ShouldBe(expected);
    }

    /// <summary>With a fraction of a second left, the countdown must not already read zero.</summary>
    [Fact]
    public void The_countdown_rounds_up()
    {
        RestartNoticeView.For(new RestartNotice(Now.AddMilliseconds(400), 30), Now).Countdown.ShouldBe("00:00:01");
    }

    [Fact]
    public void Once_the_moment_passes_it_says_restarting_now_and_shows_no_countdown()
    {
        var view = RestartNoticeView.For(new RestartNotice(Now.AddSeconds(-5), 30), Now);

        view.Visible.ShouldBeTrue();
        view.Restarting.ShouldBeTrue();
        view.Countdown.ShouldBeNull();
    }

    /// <summary>
    /// A machine still running well after its restart time did not restart --
    /// aborted, or the clock was wrong. The notice goes away rather than saying
    /// "restarting now" indefinitely.
    /// </summary>
    [Fact]
    public void A_notice_long_past_its_moment_hides_instead_of_claiming_a_restart_forever()
    {
        var notice = new RestartNotice(Now, 30);

        RestartNoticeView.For(notice, Now + RestartNoticeView.LingerAfterRestart - TimeSpan.FromSeconds(1)).Visible.ShouldBeTrue();
        RestartNoticeView.For(notice, Now + RestartNoticeView.LingerAfterRestart).Visible.ShouldBeFalse();
    }

    /// <summary>
    /// Every word the user sees is a constant. Nothing about a notice -- no field,
    /// no value -- can change what the window says, only the number counting down.
    /// </summary>
    [Fact]
    public void The_words_the_user_sees_are_fixed_and_say_it_came_from_IT()
    {
        RestartNoticeView.Headline.ShouldBe("Your IT administrator has scheduled this device to restart.");
        RestartNoticeView.Title.ShouldBe("Restart Scheduled");
        RestartNoticeView.Footer.ShouldBe("Please save your work.");

        typeof(RestartNoticeView).GetField(nameof(RestartNoticeView.Headline))!.IsLiteral.ShouldBeTrue();
        typeof(RestartNotice).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .ShouldNotContain(typeof(string), "a notice carries no text at all");
    }
}
