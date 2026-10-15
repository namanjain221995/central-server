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

    private static bool Decode(string line, out RestartNotice? notice)
    {
        var decoded = SessionNoticeProtocol.TryDecode(Encoding.UTF8.GetBytes(line), out notice, out var announcement);
        announcement.ShouldBeNull("no line in these tests may ever decode as an announcement");
        return decoded;
    }

    // ------------------------------------------------------------- protocol

    [Fact]
    public void A_notice_round_trips_exactly()
    {
        var notice = new RestartNotice(Now.AddMinutes(10), 600);

        var line = SessionNoticeProtocol.Encode(notice);

        line[^1].ShouldBe((byte)'\n');
        SessionNoticeProtocol.TryDecode(line.AsSpan(0, line.Length - 1), out var decoded, out var announcement).ShouldBeTrue();
        announcement.ShouldBeNull();
        decoded.ShouldBe(notice);
        decoded!.Cancelled.ShouldBeFalse();
    }

    [Fact]
    public void A_cancellation_round_trips_exactly()
    {
        var notice = RestartNotice.CancelledAt(Now);

        var line = SessionNoticeProtocol.Encode(notice);

        SessionNoticeProtocol.TryDecode(line.AsSpan(0, line.Length - 1), out var decoded, out _).ShouldBeTrue();
        decoded.ShouldBe(notice);
        decoded!.Cancelled.ShouldBeTrue();
        decoded.GraceSeconds.ShouldBe(0);
    }

    [Fact]
    public void The_encoded_line_carries_only_its_kind_the_time_the_grace_period_and_the_flag()
    {
        var text = Encoding.UTF8.GetString(SessionNoticeProtocol.Encode(new RestartNotice(Now, 60)));

        text.ShouldContain("\"v\":3");
        text.ShouldContain("\"kind\":\"restart\"");
        text.ShouldContain("\"restartAt\"");
        text.ShouldContain("\"graceSeconds\":60");
        text.ShouldContain("\"cancelled\":false");
        text.ShouldNotContain("message", Case.Insensitive);
        text.ShouldNotContain("title", Case.Insensitive);
        text.Length.ShouldBeLessThan(SessionNoticeProtocol.MaxLineBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60}""")]
    [InlineData("""{"v":3,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":1,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":4,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":"3","kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"Restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"shutdown","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":3,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":-1,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":3601,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":1.5,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":"60","cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"not a date","graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":12345,"graceSeconds":60,"cancelled":false}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":"false"}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":0}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":null}""")]
    public void Anything_that_is_not_exactly_a_notice_is_refused(string line)
    {
        Decode(line, out var notice).ShouldBeFalse($"'{line}' must be refused");
        notice.ShouldBeNull();
    }

    /// <summary>
    /// The service and the notifier ship in the same installer, so there is no
    /// mixed-version case to tolerate: yesterday's well-formed version 2 line is
    /// refused like any other unexpected one.
    /// </summary>
    [Fact]
    public void A_version_2_line_is_refused()
    {
        Decode("""{"v":2,"restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}""", out _).ShouldBeFalse();
    }

    /// <summary>
    /// An extra property is not tolerated. A sender that adds a "message" to a
    /// RESTART line is not the sender this reader was written for: text travels
    /// only as an announcement, with its own kind and its own bounds.
    /// </summary>
    [Theory]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false,"message":"Click here to keep working"}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false,"command":"calc.exe"}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false,"extra":null}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":true,"reason":"because"}""")]
    [InlineData("""{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false,"title":"x"}""")]
    public void An_extra_property_is_refused_whatever_it_is(string line)
    {
        Decode(line, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_line_longer_than_the_protocol_allows_is_refused_unread()
    {
        var padded = """{"v":3,"kind":"restart","restartAt":"2026-09-15T10:10:00Z","graceSeconds":60,"cancelled":false}"""
            .PadRight(SessionNoticeProtocol.MaxLineBytes + 1);

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
        view.Cancelled.ShouldBeFalse();
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
    /// A cancellation is shown from the moment it was issued, for long enough to
    /// be read, and never with a countdown or "restarting now".
    /// </summary>
    [Fact]
    public void A_cancellation_is_shown_briefly_and_then_goes_away()
    {
        var notice = RestartNotice.CancelledAt(Now);

        var shown = RestartNoticeView.For(notice, Now.AddSeconds(5));
        shown.Visible.ShouldBeTrue();
        shown.Cancelled.ShouldBeTrue();
        shown.Restarting.ShouldBeFalse();
        shown.Countdown.ShouldBeNull();

        RestartNoticeView.For(notice, Now + RestartNoticeView.LingerAfterCancel - TimeSpan.FromSeconds(1)).Visible.ShouldBeTrue();
        RestartNoticeView.For(notice, Now + RestartNoticeView.LingerAfterCancel).Visible.ShouldBeFalse();
    }

    /// <summary>A cancellation stamped in the future is a clock problem, not something to show early.</summary>
    [Fact]
    public void A_cancellation_from_the_future_is_not_shown()
    {
        RestartNoticeView.For(RestartNotice.CancelledAt(Now.AddMinutes(5)), Now).Visible.ShouldBeFalse();
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
        RestartNoticeView.CancelledTitle.ShouldBe("Restart Cancelled");
        RestartNoticeView.CancelledHeadline.ShouldBe("Your IT administrator has cancelled the scheduled restart.");

        typeof(RestartNoticeView).GetField(nameof(RestartNoticeView.Headline))!.IsLiteral.ShouldBeTrue();
        typeof(RestartNoticeView).GetField(nameof(RestartNoticeView.CancelledHeadline))!.IsLiteral.ShouldBeTrue();
        typeof(RestartNotice).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .ShouldNotContain(typeof(string), "a notice carries no text at all");
    }
}
