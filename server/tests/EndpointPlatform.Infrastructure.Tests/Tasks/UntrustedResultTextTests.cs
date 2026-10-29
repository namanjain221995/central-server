using System.Text.Json;
using EndpointPlatform.Infrastructure.Tasks;

namespace EndpointPlatform.Infrastructure.Tests.Tasks;

/// <summary>
/// The NUL stripping applied to every agent-posted result: it removes exactly the
/// NULs, keeps everything else -- including an escaped backslash that happens to be
/// followed by the text "u0000" -- and leaves NUL-free text untouched.
/// </summary>
public sealed class UntrustedResultTextTests
{
    [Fact]
    public void Text_without_a_nul_is_returned_unchanged()
    {
        const string json = """{"title":"Vendor - Display","path":"C:\\Windows"}""";

        UntrustedResultText.JsonWithoutNul(json).ShouldBeSameAs(json);
        UntrustedResultText.WithoutNul("plain").ShouldBeSameAs("plain");
        UntrustedResultText.JsonWithoutNul(null).ShouldBeNull();
        UntrustedResultText.WithoutNul(null).ShouldBeNull();
    }

    [Fact]
    public void A_nul_escape_is_removed_and_the_document_still_parses_to_the_same_text_minus_the_nul()
    {
        var json = JsonSerializer.Serialize(new { title = "Up\0date\0", id = "\0a" });
        json.ShouldContain(@"\u0000");

        var cleaned = UntrustedResultText.JsonWithoutNul(json)!;

        cleaned.ShouldNotContain(@"\u0000");
        using var doc = JsonDocument.Parse(cleaned);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("Update");
        doc.RootElement.GetProperty("id").GetString().ShouldBe("a");
    }

    [Theory]
    // An escaped backslash then the literal text u0000: not a NUL, kept as is.
    [InlineData(@"{""p"":""a\\u0000b""}", @"{""p"":""a\\u0000b""}")]
    // An escaped backslash, then a real NUL escape: the backslash stays, the NUL goes.
    [InlineData(@"{""p"":""a\\\u0000b""}", @"{""p"":""a\\b""}")]
    // Two escaped backslashes then the literal text: kept.
    [InlineData(@"{""p"":""\\\\u0000""}", @"{""p"":""\\\\u0000""}")]
    public void Only_a_real_nul_escape_is_removed(string json, string expected)
    {
        UntrustedResultText.JsonWithoutNul(json).ShouldBe(expected);

        // Whatever is left is still the JSON it claims to be.
        using var _ = JsonDocument.Parse(UntrustedResultText.JsonWithoutNul(json)!);
    }

    [Fact]
    public void A_raw_nul_character_is_removed_from_text_and_json()
    {
        UntrustedResultText.WithoutNul("a\0b\0").ShouldBe("ab");
        UntrustedResultText.JsonWithoutNul("{\"a\":1}\0").ShouldBe("{\"a\":1}");
    }
}
