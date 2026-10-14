using System.Text;
using EndpointAgent.Core.CompanyPortal;

namespace EndpointAgent.Core.Tests.CompanyPortal;

/// <summary>
/// The portal pipe's wire format, both directions.
/// </summary>
/// <remarks>
/// The request parser runs in the LocalSystem service on bytes from an ordinary
/// user's process, and the response parser runs in the user's process on bytes
/// from a server it trusts by session alone. Each is tested as the attack surface
/// it is: everything that is not exactly a request, or exactly a response, must
/// be refused.
/// </remarks>
public sealed class CompanyPortalProtocolTests
{
    private static readonly Guid AppId = Guid.Parse("7d2f9c1e-4b6a-4c3d-9e8f-0a1b2c3d4e5f");
    private static readonly DateTimeOffset Updated = new(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);

    private static bool DecodeRequest(string line, out PortalRequest? request) =>
        CompanyPortalProtocol.TryDecodeRequest(Encoding.UTF8.GetBytes(line), out request);

    private static bool DecodeResponse(string line, out PortalResponse? response) =>
        CompanyPortalProtocol.TryDecodeResponse(Encoding.UTF8.GetBytes(line), out response);

    private static ReadOnlySpan<byte> WithoutNewline(byte[] line)
    {
        line[^1].ShouldBe((byte)'\n');
        return line.AsSpan(0, line.Length - 1);
    }

    private static PortalApp App(string? description = "Does things", string? category = "Tools", string? publisher = "Contoso") =>
        new(AppId, "Contoso App", description, category, true, "2.1.0", publisher, Updated, "Available");

    // ------------------------------------------------------------- requests

    [Fact]
    public void A_list_request_round_trips_and_carries_nothing_but_the_version_and_the_operation()
    {
        var line = CompanyPortalProtocol.EncodeRequest(PortalRequest.ListApps);

        Encoding.UTF8.GetString(line).ShouldBe("{\"v\":1,\"op\":\"list\"}\n");
        CompanyPortalProtocol.TryDecodeRequest(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(PortalRequest.ListApps);
    }

    [Fact]
    public void An_icon_request_round_trips_exactly()
    {
        var line = CompanyPortalProtocol.EncodeRequest(PortalRequest.IconOf(AppId));

        CompanyPortalProtocol.TryDecodeRequest(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(new PortalRequest(PortalOperation.Icon, AppId));
    }

    [Fact]
    public void An_install_request_round_trips_exactly()
    {
        var line = CompanyPortalProtocol.EncodeRequest(PortalRequest.InstallOf(AppId));

        CompanyPortalProtocol.TryDecodeRequest(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(new PortalRequest(PortalOperation.Install, AppId));
        line.Length.ShouldBeLessThanOrEqualTo(CompanyPortalProtocol.MaxRequestBytes);
    }

    [Theory]
    [InlineData("{\"v\":1,\"op\":\"list\",\"path\":\"C:\\\\x\"}", "an extra property")]
    [InlineData("{\"v\":1}", "no operation")]
    [InlineData("{\"op\":\"list\"}", "no version")]
    [InlineData("{\"v\":2,\"op\":\"list\"}", "a version this build does not speak")]
    [InlineData("{\"v\":\"1\",\"op\":\"list\"}", "a version that is not a number")]
    [InlineData("{\"v\":1,\"op\":\"run\"}", "an operation that does not exist")]
    [InlineData("{\"v\":1,\"op\":\"List\"}", "an operation in the wrong case")]
    [InlineData("{\"v\":1,\"op\":1}", "an operation that is not a string")]
    [InlineData("{\"v\":1,\"op\":\"list\",\"id\":\"7d2f9c1e-4b6a-4c3d-9e8f-0a1b2c3d4e5f\"}", "a list request with an id")]
    [InlineData("{\"v\":1,\"op\":\"icon\"}", "an icon request without an id")]
    [InlineData("{\"v\":1,\"op\":\"install\"}", "an install request without an id")]
    [InlineData("{\"v\":1,\"op\":\"install\",\"id\":\"not-a-guid\"}", "an id that is not a GUID")]
    [InlineData("{\"v\":1,\"op\":\"install\",\"id\":\"{7d2f9c1e-4b6a-4c3d-9e8f-0a1b2c3d4e5f}\"}", "a GUID in a format other than D")]
    [InlineData("{\"v\":1,\"op\":\"install\",\"id\":\"00000000-0000-0000-0000-000000000000\"}", "the empty GUID")]
    [InlineData("{\"v\":1,\"op\":\"install\",\"id\":7}", "an id that is not a string")]
    [InlineData("{\"v\":1,\"op\":\"install\",\"id\":\"7d2f9c1e-4b6a-4c3d-9e8f-0a1b2c3d4e5f\",\"x\":1}", "an install request with an extra property")]
    [InlineData("{\"v\":{\"n\":1},\"op\":\"list\"}", "a nested object where a number belongs")]
    [InlineData("{\"v\":1,\"op\":{\"a\":{\"b\":{\"c\":1}}}}", "nesting past the parser's depth")]
    [InlineData("[1,2]", "an array")]
    [InlineData("\"list\"", "a bare string")]
    [InlineData("42", "a bare number")]
    [InlineData("null", "null")]
    [InlineData("", "an empty line")]
    [InlineData("{\"v\":1,\"op\":\"list\"", "truncated JSON")]
    [InlineData("garbage", "not JSON at all")]
    public void A_line_that_is_not_exactly_a_request_is_refused(string line, string because)
    {
        DecodeRequest(line, out var request).ShouldBeFalse(because);
        request.ShouldBeNull();
    }

    [Fact]
    public void A_request_line_longer_than_the_bound_is_refused_before_it_is_parsed()
    {
        // Valid JSON, valid request, padded with whitespace past the limit: the
        // bound is on bytes, not on meaning.
        var padded = "{\"v\":1,\"op\":\"list\"}" + new string(' ', CompanyPortalProtocol.MaxRequestBytes);

        DecodeRequest(padded, out _).ShouldBeFalse();
        DecodeRequest("{\"v\":1,\"op\":\"list\"}" + new string(' ', CompanyPortalProtocol.MaxRequestBytes - 20), out _)
            .ShouldBeTrue("the same request within the bound is fine");
    }

    // ------------------------------------------------------------ responses

    [Fact]
    public void A_catalog_round_trips_with_every_field_including_the_nulls()
    {
        var apps = new[] { App(), App(description: null, category: null, publisher: null) with { Id = Guid.NewGuid(), State = "Installed" } };
        var line = CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: apps));

        CompanyPortalProtocol.TryDecodeResponse(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded.ShouldNotBeNull();
        decoded.Ok.ShouldBeTrue();
        decoded.Error.ShouldBeNull();
        decoded.Icon.ShouldBeNull();
        decoded.Outcome.ShouldBeNull();
        decoded.Apps.ShouldNotBeNull();
        decoded.Apps.ShouldBe(apps);
    }

    [Fact]
    public void An_empty_catalog_round_trips_as_a_successful_answer_with_no_apps()
    {
        var line = CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: []));

        CompanyPortalProtocol.TryDecodeResponse(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded!.Ok.ShouldBeTrue();
        decoded.Apps.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    public void An_icon_round_trips_byte_for_byte()
    {
        var icon = new byte[CompanyPortalProtocol.MaxIconBytes];
        Random.Shared.NextBytes(icon);
        var line = CompanyPortalProtocol.EncodeResponse(new PortalResponse(Icon: icon));

        line.Length.ShouldBeLessThanOrEqualTo(CompanyPortalProtocol.MaxResponseBytes, "the largest icon must fit the response bound");
        CompanyPortalProtocol.TryDecodeResponse(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded!.Icon.ShouldBe(icon);
        decoded.Apps.ShouldBeNull();
        decoded.Outcome.ShouldBeNull();
    }

    [Fact]
    public void An_outcome_round_trips_exactly()
    {
        var line = CompanyPortalProtocol.EncodeResponse(new PortalResponse(Outcome: "Queued"));

        CompanyPortalProtocol.TryDecodeResponse(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded!.Outcome.ShouldBe("Queued");
        decoded.Ok.ShouldBeTrue();
    }

    [Theory]
    [InlineData(PortalError.BadRequest)]
    [InlineData(PortalError.NotEnrolled)]
    [InlineData(PortalError.ServerUnavailable)]
    [InlineData(PortalError.TooManyRequests)]
    public void Every_error_round_trips_as_its_word(PortalError error)
    {
        var line = CompanyPortalProtocol.EncodeResponse(PortalResponse.Failed(error));

        Encoding.UTF8.GetString(line).ShouldContain($"\"error\":\"{error}\"");
        CompanyPortalProtocol.TryDecodeResponse(WithoutNewline(line), out var decoded).ShouldBeTrue();
        decoded!.Ok.ShouldBeFalse();
        decoded.Error.ShouldBe(error);
        decoded.Apps.ShouldBeNull();
        decoded.Icon.ShouldBeNull();
        decoded.Outcome.ShouldBeNull();
    }

    [Theory]
    [InlineData("{\"v\":1,\"ok\":true,\"apps\":[],\"outcome\":\"Queued\"}", "two payloads")]
    [InlineData("{\"v\":1,\"ok\":true,\"icon\":\"AAAA\",\"outcome\":\"Queued\"}", "two payloads, icon and outcome")]
    [InlineData("{\"v\":1,\"ok\":true}", "a success with no payload")]
    [InlineData("{\"v\":1,\"ok\":true,\"outcome\":\"Queued\",\"error\":\"BadRequest\"}", "a success that also names an error")]
    [InlineData("{\"v\":1,\"ok\":false,\"error\":\"Teapot\"}", "an error word this build does not know")]
    [InlineData("{\"v\":1,\"ok\":false,\"error\":\"badRequest\"}", "an error word in the wrong case")]
    [InlineData("{\"v\":1,\"ok\":false,\"error\":\"3\"}", "an error given as a number in a string")]
    [InlineData("{\"v\":1,\"ok\":false}", "a failure with no error word")]
    [InlineData("{\"v\":1,\"ok\":true,\"outcome\":\"Queued\",\"hint\":\"x\"}", "a property the reader has no member for")]
    [InlineData("{\"v\":2,\"ok\":true,\"outcome\":\"Queued\"}", "a version this build does not speak")]
    [InlineData("{\"ok\":true,\"outcome\":\"Queued\"}", "no version")]
    [InlineData("{\"v\":1,\"ok\":true,\"icon\":\"\"}", "an empty icon")]
    [InlineData("{\"v\":1,\"ok\":true,\"icon\":\"!!!!\"}", "an icon that is not base64")]
    [InlineData("{\"v\":1,\"ok\":true,\"outcome\":\"ThisOutcomeWordIsFarTooLongToBeOneOfOurs\"}", "an overlong outcome")]
    [InlineData("[]", "an array")]
    [InlineData("\"ok\"", "a bare string")]
    [InlineData("", "an empty line")]
    [InlineData("{\"v\":1,\"ok\":true,\"outcome\":\"Queued\"", "truncated JSON")]
    public void A_line_that_is_not_exactly_a_response_is_refused(string line, string because)
    {
        DecodeResponse(line, out var response).ShouldBeFalse(because);
        response.ShouldBeNull();
    }

    [Fact]
    public void An_icon_larger_than_the_ceiling_is_refused()
    {
        var oversized = Convert.ToBase64String(new byte[CompanyPortalProtocol.MaxIconBytes + 1]);

        DecodeResponse($"{{\"v\":1,\"ok\":true,\"icon\":\"{oversized}\"}}", out _).ShouldBeFalse();
    }

    private static string AppJson(string displayName = "Contoso App", string updatedAt = "2026-10-01T12:30:00.0000000+00:00") =>
        $"{{\"id\":\"{AppId}\",\"displayName\":\"{displayName}\",\"hasIcon\":true,\"version\":\"1.0\"," +
        $"\"updatedAt\":\"{updatedAt}\",\"state\":\"Available\"}}";

    [Fact]
    public void A_well_formed_app_written_by_hand_is_accepted_so_the_refusals_below_mean_something()
    {
        DecodeResponse($"{{\"v\":1,\"ok\":true,\"apps\":[{AppJson()}]}}", out var response).ShouldBeTrue();
        var app = response!.Apps.ShouldNotBeNull().ShouldHaveSingleItem();
        app.DisplayName.ShouldBe("Contoso App");
        app.UpdatedAt.ShouldBe(Updated);
    }

    [Theory]
    [InlineData("yesterday", "Contoso App", "a time that is not a time")]
    [InlineData("", "Contoso App", "an empty time")]
    [InlineData("2026-10-01T12:30:00.0000000+00:00", "", "an empty name")]
    [InlineData("2026-10-01T12:30:00.0000000+00:00", "   ", "a blank name")]
    public void An_app_with_a_bad_time_or_name_refuses_the_whole_catalog(string updatedAt, string displayName, string because)
    {
        DecodeResponse($"{{\"v\":1,\"ok\":true,\"apps\":[{AppJson(displayName, updatedAt)}]}}", out _).ShouldBeFalse(because);
    }

    [Fact]
    public void An_app_with_an_overlong_name_refuses_the_whole_catalog()
    {
        var name = new string('n', 129);

        DecodeResponse($"{{\"v\":1,\"ok\":true,\"apps\":[{AppJson(name)}]}}", out _).ShouldBeFalse();
        DecodeResponse($"{{\"v\":1,\"ok\":true,\"apps\":[{AppJson(name[..128])}]}}", out _).ShouldBeTrue("128 is the limit, not 127");
    }

    [Fact]
    public void An_app_with_a_property_the_reader_has_no_member_for_refuses_the_whole_catalog()
    {
        var app = AppJson()[..^1] + ",\"installerPath\":\"C:\\\\x.msi\"}";

        DecodeResponse($"{{\"v\":1,\"ok\":true,\"apps\":[{app}]}}", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_response_line_longer_than_the_bound_is_refused_before_it_is_parsed()
    {
        var padded = "{\"v\":1,\"ok\":true,\"outcome\":\"Queued\"}" + new string(' ', CompanyPortalProtocol.MaxResponseBytes);

        DecodeResponse(padded, out _).ShouldBeFalse();
    }
}
