using System.Text;
using EndpointAgent.Core.ScreenLock;

namespace EndpointAgent.Core.Tests.ScreenLock;

/// <summary>
/// The screen-lock pipe's wire format: a request or response round-trips, and
/// everything unexpected is refused -- the strictness that lets the service treat
/// an ordinary user's process as hostile and the overlay treat a squatter's answer
/// as suspect.
/// </summary>
public sealed class ScreenLockPipeProtocolTests
{
    private static byte[] Line(string json) => Encoding.UTF8.GetBytes(json + "\n");

    private static LockPipeRequest DecodeRequest(byte[] line)
    {
        ScreenLockPipeProtocol.TryDecodeRequest(StripNewline(line), out var request).ShouldBeTrue();
        return request!;
    }

    private static LockPipeResponse DecodeResponse(byte[] line)
    {
        ScreenLockPipeProtocol.TryDecodeResponse(StripNewline(line), out var response).ShouldBeTrue();
        return response!;
    }

    private static ReadOnlySpan<byte> StripNewline(byte[] line) =>
        line.Length > 0 && line[^1] == (byte)'\n' ? line.AsSpan(0, line.Length - 1) : line;

    // ------------------------------------------------------------- round-trips

    [Fact]
    public void A_show_request_round_trips()
    {
        DecodeRequest(ScreenLockPipeProtocol.EncodeRequest(LockPipeRequest.Show)).ShouldBe(LockPipeRequest.Show);
    }

    [Fact]
    public void A_submit_request_round_trips_including_unicode_codes()
    {
        var request = LockPipeRequest.SubmitCode("pâssphrâse-with-ünicode-é");
        DecodeRequest(ScreenLockPipeProtocol.EncodeRequest(request)).ShouldBe(request);
    }

    [Fact]
    public void A_show_answer_round_trips_with_and_without_a_contact_line()
    {
        var withContact = LockPipeResponse.Showing(locked: true, "Locked by IT.", "ext. 4000");
        DecodeResponse(ScreenLockPipeProtocol.EncodeResponse(withContact)).ShouldBe(withContact);

        var noContact = LockPipeResponse.Showing(locked: true, "Locked by IT.", null);
        DecodeResponse(ScreenLockPipeProtocol.EncodeResponse(noContact)).ShouldBe(noContact);

        var notLocked = LockPipeResponse.Showing(locked: false, null, null);
        DecodeResponse(ScreenLockPipeProtocol.EncodeResponse(notLocked)).ShouldBe(notLocked);
    }

    [Fact]
    public void A_verify_answer_and_an_error_round_trip()
    {
        DecodeResponse(ScreenLockPipeProtocol.EncodeResponse(LockPipeResponse.Verified(true))).Unlocked.ShouldBe(true);
        DecodeResponse(ScreenLockPipeProtocol.EncodeResponse(LockPipeResponse.Verified(false))).Unlocked.ShouldBe(false);
        DecodeResponse(ScreenLockPipeProtocol.EncodeResponse(LockPipeResponse.Failed(LockPipeError.TooManyRequests)))
            .Error.ShouldBe(LockPipeError.TooManyRequests);
    }

    // ------------------------------------------------------------ bad requests

    [Theory]
    [InlineData("{\"v\":1,\"op\":\"show\",\"code\":\"x\"}", "a show that carries anything else is not a show")]
    [InlineData("{\"v\":1,\"op\":\"submit\"}", "a submit must carry a code")]
    [InlineData("{\"v\":1,\"op\":\"run\",\"code\":\"x\"}", "an unknown operation")]
    [InlineData("{\"v\":2,\"op\":\"show\"}", "an unknown version")]
    [InlineData("{\"op\":\"show\"}", "no version")]
    [InlineData("{\"v\":1}", "no operation")]
    [InlineData("not json", "not JSON")]
    [InlineData("[]", "not an object")]
    public void A_line_that_is_not_a_request_is_refused(string json, string because)
    {
        ScreenLockPipeProtocol.TryDecodeRequest(StripNewline(Line(json)), out var request).ShouldBeFalse(because);
        request.ShouldBeNull();
    }

    [Fact]
    public void A_submit_with_a_control_character_or_an_overlong_code_is_refused()
    {
        ScreenLockPipeProtocol.TryDecodeRequest(StripNewline(Line("{\"v\":1,\"op\":\"submit\",\"code\":\"a\\u0000b\"}")), out _)
            .ShouldBeFalse("a code with a control character is not plain text");

        var tooLong = new string('x', ScreenLockPipeProtocol.MaxCodeChars + 1);
        ScreenLockPipeProtocol.TryDecodeRequest(StripNewline(Line($"{{\"v\":1,\"op\":\"submit\",\"code\":\"{tooLong}\"}}")), out _)
            .ShouldBeFalse("a code past the limit is refused");
    }

    [Fact]
    public void An_overlong_request_line_is_refused_without_parsing()
    {
        var flood = new byte[ScreenLockPipeProtocol.MaxRequestBytes + 1];
        Array.Fill(flood, (byte)'x');

        ScreenLockPipeProtocol.TryDecodeRequest(flood, out _).ShouldBeFalse();
    }

    // ----------------------------------------------------------- bad responses

    [Theory]
    [InlineData("{\"v\":1,\"locked\":true,\"unlocked\":true}", "a response is one kind, not two")]
    [InlineData("{\"v\":1,\"unlocked\":true,\"message\":\"x\"}", "a verify answer carries no words")]
    [InlineData("{\"v\":1,\"locked\":false,\"message\":\"x\"}", "not locked carries no words")]
    [InlineData("{\"v\":1,\"error\":\"Nope\"}", "an unknown error word")]
    [InlineData("{\"v\":1,\"error\":\"BadRequest\",\"locked\":true}", "an error carries nothing else")]
    [InlineData("{\"v\":2,\"locked\":false}", "an unknown version")]
    [InlineData("{\"v\":1,\"op\":\"show\"}", "an unmapped property refuses the line")]
    [InlineData("{\"v\":1}", "no payload at all")]
    public void A_line_that_is_not_a_response_is_refused(string json, string because)
    {
        ScreenLockPipeProtocol.TryDecodeResponse(StripNewline(Line(json)), out var response).ShouldBeFalse(because);
        response.ShouldBeNull();
    }

    [Fact]
    public void A_show_answer_whose_words_are_not_plain_text_is_refused()
    {
        ScreenLockPipeProtocol.TryDecodeResponse(StripNewline(Line("{\"v\":1,\"locked\":true,\"message\":\"a\\u0007b\"}")), out _)
            .ShouldBeFalse("a bell character is not plain text");
    }
}
