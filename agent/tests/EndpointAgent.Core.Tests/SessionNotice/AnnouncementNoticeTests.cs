using System.Text;
using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.Core.Tests.SessionNotice;

/// <summary>
/// The announcement's wire format and what the window shows for it.
/// </summary>
/// <remarks>
/// The one line of the protocol that carries text, so its parser is tested as
/// the attack surface it is: the bounds the administrator's console promises are
/// enforced again where the bytes arrive, and everything that is not exactly an
/// announcement must be refused.
/// </remarks>
public sealed class AnnouncementNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("0199c0d2-3b1e-7c8a-9f00-00000000000a");
    private static readonly Guid OtherId = Guid.Parse("0199c0d2-3b1e-7c8a-9f00-00000000000b");

    private static AnnouncementNotice Notice(string title = "T", string message = "M", DateTimeOffset? at = null, Guid? id = null) =>
        new(id ?? Id, title, message, at ?? Now);

    private static bool Decode(string line, out AnnouncementNotice? notice)
    {
        var decoded = SessionNoticeProtocol.TryDecode(Encoding.UTF8.GetBytes(line), out var restart, out notice);
        restart.ShouldBeNull("no line in these tests may ever decode as a restart");
        return decoded;
    }

    // ------------------------------------------------------------- protocol

    [Fact]
    public void An_announcement_round_trips_exactly()
    {
        var notice = Notice("Maintenance tonight", "The office network is down from 22:00 to 23:00.");

        var line = SessionNoticeProtocol.Encode(notice);

        line[^1].ShouldBe((byte)'\n');
        SessionNoticeProtocol.TryDecode(line.AsSpan(0, line.Length - 1), out var restart, out var decoded).ShouldBeTrue();
        restart.ShouldBeNull();
        decoded.ShouldBe(notice);
    }

    [Fact]
    public void The_encoded_line_carries_exactly_its_kind_the_id_the_moment_and_the_two_texts()
    {
        var text = Encoding.UTF8.GetString(SessionNoticeProtocol.Encode(Notice("Title", "Message")));

        text.ShouldContain("\"v\":3");
        text.ShouldContain("\"kind\":\"announcement\"");
        text.ShouldContain($"\"id\":\"{Id:D}\"");
        text.ShouldContain("\"at\"");
        text.ShouldContain("\"title\":\"Title\"");
        text.ShouldContain("\"message\":\"Message\"");
        text.ShouldNotContain("restartAt");
        text.ShouldNotContain("graceSeconds");
    }

    /// <summary>
    /// The character bounds and the byte bound must hold together: the largest
    /// permitted announcement, written entirely in three-byte UTF-8 characters,
    /// still fits one line. This is why the encoder leaves non-ASCII unescaped.
    /// </summary>
    [Fact]
    public void The_largest_permitted_announcement_fits_the_line_limit()
    {
        var notice = Notice(
            new string('あ', SessionNoticeProtocol.MaxTitleChars),
            new string('あ', SessionNoticeProtocol.MaxMessageChars));

        var line = SessionNoticeProtocol.Encode(notice);

        line.Length.ShouldBeLessThanOrEqualTo(SessionNoticeProtocol.MaxLineBytes);
        SessionNoticeProtocol.TryDecode(line.AsSpan(0, line.Length - 1), out _, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(notice);
    }

    /// <summary>Quotes and backslashes escape to two bytes each; the worst escaping case still fits.</summary>
    [Fact]
    public void A_message_of_nothing_but_escaped_characters_still_fits_and_round_trips()
    {
        var notice = Notice(
            new string('"', SessionNoticeProtocol.MaxTitleChars),
            new string('\\', SessionNoticeProtocol.MaxMessageChars));

        var line = SessionNoticeProtocol.Encode(notice);

        line.Length.ShouldBeLessThanOrEqualTo(SessionNoticeProtocol.MaxLineBytes);
        SessionNoticeProtocol.TryDecode(line.AsSpan(0, line.Length - 1), out _, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(notice);
    }

    [Fact]
    public void A_multi_line_message_is_permitted_and_round_trips()
    {
        var notice = Notice("Two things", "First thing.\nSecond thing.\tIndented.");

        var line = SessionNoticeProtocol.Encode(notice);

        SessionNoticeProtocol.TryDecode(line.AsSpan(0, line.Length - 1), out _, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(notice);
    }

    [Theory]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":2,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":4,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"not a guid","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d23b1e7c8a9f0000000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"{0199c0d2-3b1e-7c8a-9f00-00000000000a}","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":7,"at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":null,"at":"2026-10-16T10:00:00Z","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":""}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":null,"message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":7,"message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":["M"]}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"not a date","title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":12345,"title":"T","message":"M"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"M","extra":1}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"M","command":"calc.exe"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"a\u0000b"}""")]
    [InlineData("""{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"a\u001bb","message":"M"}""")]
    public void Anything_that_is_not_exactly_an_announcement_is_refused(string line)
    {
        Decode(line, out var notice).ShouldBeFalse($"'{line}' must be refused");
        notice.ShouldBeNull();
    }

    [Fact]
    public void A_title_over_the_bound_is_refused()
    {
        var line = $$"""{"v":3,"kind":"announcement","id":"{{Id:D}}","at":"2026-10-16T10:00:00Z","title":"{{new string('t', SessionNoticeProtocol.MaxTitleChars + 1)}}","message":"M"}""";

        Decode(line, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_message_over_the_bound_is_refused()
    {
        var line = $$"""{"v":3,"kind":"announcement","id":"{{Id:D}}","at":"2026-10-16T10:00:00Z","title":"T","message":"{{new string('m', SessionNoticeProtocol.MaxMessageChars + 1)}}"}""";

        Decode(line, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_line_longer_than_the_protocol_allows_is_refused_unread()
    {
        var padded = """{"v":3,"kind":"announcement","id":"0199c0d2-3b1e-7c8a-9f00-00000000000a","at":"2026-10-16T10:00:00Z","title":"T","message":"M"}"""
            .PadRight(SessionNoticeProtocol.MaxLineBytes + 1);

        Decode(padded, out _).ShouldBeFalse();
    }

    /// <summary>
    /// The encoder refuses what the decoder would: the service must never emit a
    /// line its own reader throws away.
    /// </summary>
    [Theory]
    [InlineData("", "M")]
    [InlineData("T", "")]
    [InlineData("a\u0007b", "M")]
    [InlineData("T", "a\u0000b")]
    public void Encoding_out_of_bounds_text_throws_instead_of_sending_it(string title, string message)
    {
        Should.Throw<ArgumentException>(
            () => SessionNoticeProtocol.Encode(Notice(title, message)));
    }

    [Fact]
    public void Encoding_overlong_text_throws_instead_of_sending_it()
    {
        Should.Throw<ArgumentException>(() => SessionNoticeProtocol.Encode(
            Notice(new string('t', SessionNoticeProtocol.MaxTitleChars + 1), "M")));
        Should.Throw<ArgumentException>(() => SessionNoticeProtocol.Encode(
            Notice("T", new string('m', SessionNoticeProtocol.MaxMessageChars + 1))));
    }

    // ----------------------------------------------------------------- view

    [Fact]
    public void No_announcement_shows_nothing()
    {
        AnnouncementView.For(null, Now).Visible.ShouldBeFalse();
    }

    [Fact]
    public void An_announcement_is_shown_from_its_moment_with_the_administrators_words()
    {
        var view = AnnouncementView.For(Notice("Heads up", "Scheduled maintenance at noon."), Now);

        view.Visible.ShouldBeTrue();
        view.Title.ShouldBe("Heads up");
        view.Message.ShouldBe("Scheduled maintenance at noon.");
    }

    [Fact]
    public void An_announcement_lingers_for_thirty_minutes_and_then_goes_away()
    {
        var notice = Notice();

        AnnouncementView.For(notice, Now + AnnouncementView.Linger - TimeSpan.FromSeconds(1)).Visible.ShouldBeTrue();
        AnnouncementView.For(notice, Now + AnnouncementView.Linger).Visible.ShouldBeFalse();
    }

    /// <summary>An announcement stamped in the future is a clock problem, not something to show early.</summary>
    [Fact]
    public void An_announcement_from_the_future_is_not_shown()
    {
        AnnouncementView.For(Notice(at: Now.AddMinutes(5)), Now).Visible.ShouldBeFalse();
    }

    [Fact]
    public void A_dismissed_announcement_stays_hidden()
    {
        AnnouncementView.For(Notice(), Now.AddMinutes(1), dismissedIds: new HashSet<Guid> { Id }).Visible.ShouldBeFalse();
    }

    /// <summary>
    /// Dismissal keys on the server's id, because the same announcement can be
    /// delivered again -- a replay to a reconnecting notifier, a re-queued task --
    /// and every delivery is stamped with a fresh moment. Same id, whatever the
    /// timestamp: it stays closed.
    /// </summary>
    [Fact]
    public void A_redelivery_of_a_dismissed_announcement_stays_hidden_despite_its_fresh_timestamp()
    {
        var dismissed = new HashSet<Guid> { Id };
        var redelivered = Notice(at: Now.AddMinutes(10));

        AnnouncementView.For(redelivered, Now.AddMinutes(11), dismissed).Visible.ShouldBeFalse();
    }

    /// <summary>Only a different id is a new message; it shows past any earlier dismissals.</summary>
    [Fact]
    public void An_announcement_with_a_new_id_shows_even_after_earlier_ones_were_dismissed()
    {
        var dismissed = new HashSet<Guid> { Id };

        AnnouncementView.For(Notice("T", "Different words", id: OtherId), Now.AddMinutes(1), dismissed).Visible.ShouldBeTrue();
        AnnouncementView.For(Notice("T", "M", at: Now, id: OtherId), Now.AddMinutes(1), dismissed).Visible.ShouldBeTrue();
    }

    [Fact]
    public void Several_dismissed_ids_all_stay_hidden()
    {
        var dismissed = new HashSet<Guid> { Id, OtherId };

        AnnouncementView.For(Notice(id: Id), Now.AddMinutes(1), dismissed).Visible.ShouldBeFalse();
        AnnouncementView.For(Notice(id: OtherId), Now.AddMinutes(1), dismissed).Visible.ShouldBeFalse();
    }

    [Fact]
    public void While_hidden_the_view_carries_no_words_at_all()
    {
        var view = AnnouncementView.For(Notice(), Now + AnnouncementView.Linger);

        view.Visible.ShouldBeFalse();
        view.Title.ShouldBeEmpty();
        view.Message.ShouldBeEmpty();
    }

    /// <summary>
    /// The chrome around the administrator's words is fixed: the window itself
    /// still says, in constants, where the text came from.
    /// </summary>
    [Fact]
    public void The_words_framing_an_announcement_are_fixed_and_say_it_came_from_IT()
    {
        AnnouncementView.WindowTitle.ShouldBe("Announcement");
        AnnouncementView.Headline.ShouldBe("Message from your IT administrator");
        AnnouncementView.Footer.ShouldBe("You can close this message.");

        typeof(AnnouncementView).GetField(nameof(AnnouncementView.WindowTitle))!.IsLiteral.ShouldBeTrue();
        typeof(AnnouncementView).GetField(nameof(AnnouncementView.Headline))!.IsLiteral.ShouldBeTrue();
        typeof(AnnouncementView).GetField(nameof(AnnouncementView.Footer))!.IsLiteral.ShouldBeTrue();
    }
}
