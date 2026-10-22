using EndpointAgent.Core.Chat;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Tests.Chat;

/// <summary>
/// The live-chat wire format: what round-trips, and what the strict decoder on each
/// side refuses. The service reads requests from an ordinary user's process, so the
/// request decoder is the one that matters most.
/// </summary>
public sealed class LiveChatPipeProtocolTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Encoding appends a newline; the reader hands the decoder the line without it.</summary>
    private static ReadOnlySpan<byte> Line(byte[] encoded) => encoded.AsSpan(0, encoded.Length - 1);

    // ------------------------------------------------------------- requests

    [Fact]
    public void A_poll_request_round_trips()
    {
        var encoded = LiveChatPipeProtocol.EncodeRequest(ChatPipeRequest.Poll(7));

        LiveChatPipeProtocol.TryDecodeRequest(Line(encoded), out var request).ShouldBeTrue();
        request.ShouldNotBeNull();
        request.Op.ShouldBe(ChatPipeOp.Poll);
        request.AfterSeq.ShouldBe(7);
        request.Body.ShouldBeNull();
    }

    [Fact]
    public void A_send_request_round_trips_including_unicode()
    {
        var encoded = LiveChatPipeProtocol.EncodeRequest(ChatPipeRequest.Send("hello — नमस्ते 🙂"));

        LiveChatPipeProtocol.TryDecodeRequest(Line(encoded), out var request).ShouldBeTrue();
        request.ShouldNotBeNull();
        request.Op.ShouldBe(ChatPipeOp.Send);
        request.Body.ShouldBe("hello — नमस्ते 🙂");
    }

    [Fact]
    public void A_closed_request_round_trips()
    {
        var encoded = LiveChatPipeProtocol.EncodeRequest(ChatPipeRequest.Closed);

        LiveChatPipeProtocol.TryDecodeRequest(Line(encoded), out var request).ShouldBeTrue();
        request.ShouldNotBeNull();
        request.Op.ShouldBe(ChatPipeOp.Closed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"v\":1}")]                                   // no op
    [InlineData("{\"v\":2,\"op\":\"poll\",\"after\":0}")]       // wrong version
    [InlineData("{\"v\":1,\"op\":\"nope\"}")]                   // unknown op
    [InlineData("{\"v\":1,\"op\":\"poll\"}")]                   // poll without after
    [InlineData("{\"v\":1,\"op\":\"poll\",\"after\":-1}")]      // negative cursor
    [InlineData("{\"v\":1,\"op\":\"poll\",\"after\":0,\"x\":1}")] // extra property
    [InlineData("{\"v\":1,\"op\":\"send\"}")]                   // send without body
    [InlineData("{\"v\":1,\"op\":\"closed\",\"x\":1}")]         // closed with extra
    public void A_malformed_request_is_refused(string json)
    {
        LiveChatPipeProtocol.TryDecodeRequest(System.Text.Encoding.UTF8.GetBytes(json), out var request).ShouldBeFalse();
        request.ShouldBeNull();
    }

    [Fact]
    public void A_send_body_that_is_too_long_or_has_control_characters_is_refused()
    {
        var tooLong = LiveChatPipeProtocol.EncodeRequest(ChatPipeRequest.Send(new string('x', ChatText.MaxBodyChars + 1)));
        LiveChatPipeProtocol.TryDecodeRequest(Line(tooLong), out _).ShouldBeFalse();

        var control = System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"send\",\"body\":\"a\\u0007b\"}");
        LiveChatPipeProtocol.TryDecodeRequest(control, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_request_longer_than_the_bound_is_refused_unread()
    {
        var overlong = new byte[LiveChatPipeProtocol.MaxRequestBytes + 1];
        LiveChatPipeProtocol.TryDecodeRequest(overlong, out _).ShouldBeFalse();
    }

    // ------------------------------------------------------------ responses

    [Fact]
    public void A_poll_answer_round_trips_with_messages()
    {
        var messages = new List<ChatMessageDto>
        {
            new(1, "Admin", "Asha (IT)", "Hi, how can I help?", At),
            new(2, "Employee", "WS-01 user", "My printer is offline.", At.AddSeconds(30)),
            new(3, "System", "System", "The chat has started.", At.AddSeconds(-1)),
        };
        var encoded = LiveChatPipeProtocol.EncodeResponse(ChatPipeResponse.Polled(active: true, "Asha (IT)", messages));

        LiveChatPipeProtocol.TryDecodeResponse(Line(encoded), out var response).ShouldBeTrue();
        response.ShouldNotBeNull();
        response.Active.ShouldBe(true);
        response.AdminDisplay.ShouldBe("Asha (IT)");
        response.Messages.ShouldNotBeNull().Count.ShouldBe(3);
        response.Messages![0].Sender.ShouldBe("Admin");
        response.Messages[1].Body.ShouldBe("My printer is offline.");
        response.Messages[2].Sender.ShouldBe("System");
    }

    [Fact]
    public void A_closed_poll_answer_carries_no_name_or_messages()
    {
        var encoded = LiveChatPipeProtocol.EncodeResponse(ChatPipeResponse.Polled(active: false, "Asha (IT)", []));

        LiveChatPipeProtocol.TryDecodeResponse(Line(encoded), out var response).ShouldBeTrue();
        response.ShouldNotBeNull();
        response.Active.ShouldBe(false);
        response.AdminDisplay.ShouldBeNull();
        response.Messages.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_send_answer_round_trips(bool accepted)
    {
        var encoded = LiveChatPipeProtocol.EncodeResponse(ChatPipeResponse.Sent(accepted));

        LiveChatPipeProtocol.TryDecodeResponse(Line(encoded), out var response).ShouldBeTrue();
        response.ShouldNotBeNull();
        response.Accepted.ShouldBe(accepted);
        response.Active.ShouldBeNull();
    }

    [Fact]
    public void An_ack_and_an_error_round_trip()
    {
        var ack = LiveChatPipeProtocol.EncodeResponse(ChatPipeResponse.Acked());
        LiveChatPipeProtocol.TryDecodeResponse(Line(ack), out var ackResponse).ShouldBeTrue();
        ackResponse!.Acknowledged.ShouldBe(true);

        var failed = LiveChatPipeProtocol.EncodeResponse(ChatPipeResponse.Failed(ChatPipeError.TooManyRequests));
        LiveChatPipeProtocol.TryDecodeResponse(Line(failed), out var failedResponse).ShouldBeTrue();
        failedResponse!.Error.ShouldBe(ChatPipeError.TooManyRequests);
    }

    [Theory]
    [InlineData("{\"v\":1,\"active\":true,\"adminDisplay\":\"A\",\"messages\":[{\"seq\":1,\"sender\":\"Nobody\",\"senderDisplay\":\"x\",\"body\":\"hi\",\"sentAt\":\"2026-10-22T12:00:00+00:00\"}]}")] // unknown sender
    [InlineData("{\"v\":1,\"active\":true,\"messages\":[]}")]                 // active with no admin name
    [InlineData("{\"v\":1,\"active\":true,\"adminDisplay\":\"A\"}")]          // active with no messages array
    [InlineData("{\"v\":1,\"accepted\":true,\"active\":true,\"messages\":[]}")] // two modes at once
    [InlineData("{\"v\":1}")]                                                 // nothing set
    public void A_malformed_response_is_refused(string json)
    {
        LiveChatPipeProtocol.TryDecodeResponse(System.Text.Encoding.UTF8.GetBytes(json), out var response).ShouldBeFalse();
        response.ShouldBeNull();
    }
}
