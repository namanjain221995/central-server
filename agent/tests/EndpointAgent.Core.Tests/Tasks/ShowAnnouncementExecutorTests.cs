using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.SessionNotice;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// Showing an announcement: what is validated before anything reaches a screen,
/// what is handed to the notifier, and what is reported back.
/// </summary>
/// <remarks>
/// Nothing here opens a window. <see cref="RecordingNotifier"/> stands in for the
/// pipe server and records what it was given; the executor's job is the strict
/// gate in front of it and the honest count behind it.
/// </remarks>
public sealed class ShowAnnouncementExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid AnnouncementId = Guid.Parse("0199c0d2-3b1e-7c8a-9f00-00000000000a");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingNotifier : IAnnouncementNotifier
    {
        public List<AnnouncementNotice> Received { get; } = [];
        public int ConnectedSessionCount { get; set; } = 1;
        public Exception? Throw { get; set; }

        public void AnnouncementReceived(AnnouncementNotice notice)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Received.Add(notice);
        }
    }

    private static ShowAnnouncementExecutor Executor(RecordingNotifier notifier) =>
        new(notifier, NullLogger<ShowAnnouncementExecutor>.Instance, new FixedClock(Now));

    private static AgentTask Task_(string? payloadJson, DateTimeOffset? expiresAt = null) =>
        new(Guid.CreateVersion7(), "ShowAnnouncement", payloadJson, expiresAt);

    private static string Payload(string title = "Maintenance", string message = "Back at noon.") =>
        JsonSerializer.Serialize(new { announcementId = AnnouncementId, title, message });

    private static JsonElement Result(AgentTaskResult result) => JsonDocument.Parse(result.ResultJson!).RootElement;

    [Fact]
    public async Task A_valid_announcement_is_handed_to_the_notifier_stamped_now()
    {
        var notifier = new RecordingNotifier { ConnectedSessionCount = 2 };

        var result = await Executor(notifier).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("Announcement shown to 2 session(s).");
        var notice = notifier.Received.ShouldHaveSingleItem();
        notice.Id.ShouldBe(AnnouncementId, "the server's id rides along, so a re-delivery stays dismissed");
        notice.Title.ShouldBe("Maintenance");
        notice.Message.ShouldBe("Back at noon.");
        notice.At.ShouldBe(Now, "the linger window starts when the device sends it");

        var json = Result(result);
        json.GetProperty("announcementId").GetGuid().ShouldBe(AnnouncementId);
        json.GetProperty("sessions").GetInt32().ShouldBe(2);
    }

    /// <summary>
    /// Nobody signed in is not a failure: the announcement was sent, is kept for
    /// replay, and the message says exactly what that means.
    /// </summary>
    [Fact]
    public async Task Zero_connected_sessions_is_success_and_says_the_user_may_be_signed_out()
    {
        var notifier = new RecordingNotifier { ConnectedSessionCount = 0 };

        var result = await Executor(notifier).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldStartWith("Announcement shown to 0 sessions: nobody is signed in on this device right now.");
        result.Message.ShouldContain($"within the next {AnnouncementView.LingerMinutes} minutes");
        notifier.Received.Count.ShouldBe(1, "it must still be sent, for the replay");
        Result(result).GetProperty("sessions").GetInt32().ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"title":"T","message":"M"}""")]
    [InlineData("""{"announcementId":"not-a-guid","title":"T","message":"M"}""")]
    [InlineData("""{"announcementId":"0199c0d2-3b1e-7c8a-9f00-00000000000a","message":"M"}""")]
    [InlineData("""{"announcementId":"0199c0d2-3b1e-7c8a-9f00-00000000000a","title":"","message":"M"}""")]
    [InlineData("""{"announcementId":"0199c0d2-3b1e-7c8a-9f00-00000000000a","title":7,"message":"M"}""")]
    [InlineData("""{"announcementId":"0199c0d2-3b1e-7c8a-9f00-00000000000a","title":"T"}""")]
    [InlineData("""{"announcementId":"0199c0d2-3b1e-7c8a-9f00-00000000000a","title":"T","message":""}""")]
    [InlineData("""{"announcementId":"0199c0d2-3b1e-7c8a-9f00-00000000000a","title":"T","message":"a\u0000b"}""")]
    public async Task A_missing_or_malformed_payload_is_a_failure_and_nothing_is_shown(string? payloadJson)
    {
        var notifier = new RecordingNotifier();

        var result = await Executor(notifier).ExecuteAsync(Task_(payloadJson));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldStartWith("Announcement refused:");
        result.Message.ShouldEndWith("Nothing was shown.");
        notifier.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_overlong_title_is_a_failure_and_nothing_is_shown()
    {
        var notifier = new RecordingNotifier();
        var payload = Payload(title: new string('t', SessionNoticeProtocol.MaxTitleChars + 1));

        var result = await Executor(notifier).ExecuteAsync(Task_(payload));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldStartWith("Announcement refused:");
        result.Message.ShouldContain($"1 to {SessionNoticeProtocol.MaxTitleChars} characters");
        notifier.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_overlong_message_is_a_failure_and_nothing_is_shown()
    {
        var notifier = new RecordingNotifier();
        var payload = Payload(message: new string('m', SessionNoticeProtocol.MaxMessageChars + 1));

        var result = await Executor(notifier).ExecuteAsync(Task_(payload));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldStartWith("Announcement refused:");
        result.Message.ShouldContain($"1 to {SessionNoticeProtocol.MaxMessageChars} characters");
        notifier.Received.ShouldBeEmpty();
    }

    /// <summary>The bounds here are the wire's bounds: the largest payload the server may queue goes through.</summary>
    [Fact]
    public async Task The_largest_permitted_title_and_message_are_accepted()
    {
        var notifier = new RecordingNotifier();
        var payload = Payload(
            title: new string('t', SessionNoticeProtocol.MaxTitleChars),
            message: new string('m', SessionNoticeProtocol.MaxMessageChars));

        var result = await Executor(notifier).ExecuteAsync(Task_(payload));

        result.Succeeded.ShouldBeTrue();
        notifier.Received.ShouldHaveSingleItem();
    }

    /// <summary>
    /// The payload crosses the authenticated task channel, so an unknown extra
    /// property is tolerated here -- the strictness that defends the pipe lives in
    /// the protocol, which these words are validated against either way.
    /// </summary>
    [Fact]
    public async Task An_extra_payload_property_is_tolerated()
    {
        var notifier = new RecordingNotifier();
        var payload = JsonSerializer.Serialize(new
        {
            announcementId = AnnouncementId,
            title = "T",
            message = "M",
            requestedBy = "admin@example.invalid",
        });

        var result = await Executor(notifier).ExecuteAsync(Task_(payload));

        result.Succeeded.ShouldBeTrue();
        notifier.Received.ShouldHaveSingleItem();
    }

    /// <summary>Stale news misleads; an announcement past its server TTL is refused like a late restart.</summary>
    [Fact]
    public async Task An_expired_task_shows_nothing()
    {
        var notifier = new RecordingNotifier();

        var result = await Executor(notifier).ExecuteAsync(Task_(Payload(), expiresAt: Now.AddMinutes(-1)));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldStartWith("Announcement refused: the task expired");
        notifier.Received.ShouldBeEmpty();
    }

    /// <summary>Unlike a restart's courtesy notice, showing the message IS the task.</summary>
    [Fact]
    public async Task A_notifier_that_cannot_send_fails_the_task()
    {
        var notifier = new RecordingNotifier { Throw = new IOException("pipe closed") };

        var result = await Executor(notifier).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldStartWith("Announcement failed: the session notice could not be sent");
    }

    [Fact]
    public void The_executor_answers_to_its_own_task_type() =>
        Executor(new RecordingNotifier()).TaskType.ShouldBe("ShowAnnouncement");
}
