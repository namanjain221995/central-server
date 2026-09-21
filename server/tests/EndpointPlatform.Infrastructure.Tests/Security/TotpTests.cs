using System.Text;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Infrastructure.Tests.Security;

/// <summary>
/// The TOTP implementation, pinned against the published specification rather
/// than against itself.
/// </summary>
/// <remarks>
/// <para>
/// The first test uses the official RFC 6238 Appendix B vectors. That is the
/// reason this algorithm could reasonably be written here instead of taken from a
/// package: its correctness is checkable against an external authority, so
/// "it matches what I expected" is not the standard being applied.
/// </para>
/// <para>
/// The RFC's vectors are eight digits and this implementation emits six, which is
/// what authenticator apps use. The vector test therefore compares the last six
/// digits - dynamic truncation produces one integer and the digit count is a
/// modulus applied to it, so the six-digit code is exactly the low six digits of
/// the eight-digit one.
/// </para>
/// </remarks>
public sealed class TotpTests
{
    /// <summary>The RFC 6238 SHA-1 seed: the ASCII string "12345678901234567890".</summary>
    private static byte[] RfcSeed => Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>
    /// RFC 6238 Appendix B, the SHA-1 rows.
    /// </summary>
    /// <remarks>
    /// Unix time, then the eight-digit code the RFC states. Reproduced verbatim;
    /// if any of these disagree, the implementation is wrong, not the vectors.
    /// </remarks>
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_the_RFC_6238_test_vectors(long unixTime, string expectedEightDigits)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixTime);
        var counter = Totp.CounterFor(at);

        var actual = Totp.ComputeCode(RfcSeed, counter);

        actual.ShouldBe(expectedEightDigits[^Totp.Digits..]);
    }

    /// <summary>A code is accepted inside its own step.</summary>
    [Fact]
    public void A_current_code_verifies()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.ComputeCode(Totp.FromBase32(secret), Totp.CounterFor(now));

        Totp.Verify(secret, code, now).ShouldBe(Totp.CounterFor(now));
    }

    /// <summary>
    /// Codes one step either side are accepted; two steps away are not.
    /// </summary>
    /// <remarks>
    /// Pins the drift window explicitly. Widening it is a real security decision -
    /// it multiplies an attacker's guessing surface - and should not happen by
    /// accident.
    /// </remarks>
    [Theory]
    [InlineData(-2, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Drift_is_accepted_one_step_either_way(int offsetSteps, bool expected)
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.ComputeCode(Totp.FromBase32(secret), Totp.CounterFor(now) + offsetSteps);

        (Totp.Verify(secret, code, now) is not null).ShouldBe(expected);
    }

    /// <summary>
    /// Verification returns the matched counter, which is what makes replay
    /// prevention possible.
    /// </summary>
    /// <remarks>
    /// Without this the caller cannot tell a fresh code from one already used
    /// inside the same ninety-second window, and a code read over somebody's
    /// shoulder stays usable.
    /// </remarks>
    [Fact]
    public void Verification_reports_which_counter_matched()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var previous = Totp.CounterFor(now) - 1;
        var code = Totp.ComputeCode(Totp.FromBase32(secret), previous);

        Totp.Verify(secret, code, now).ShouldBe(previous);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]      // too short
    [InlineData("1234567")]    // too long
    [InlineData("12345a")]     // not digits
    [InlineData("abcdef")]
    public void Malformed_input_is_refused_without_throwing(string? code)
    {
        var secret = Totp.GenerateSecret();

        Totp.Verify(secret, code, DateTimeOffset.UtcNow).ShouldBeNull();
    }

    /// <summary>Spaces are tolerated: authenticator apps display codes as "123 456".</summary>
    [Fact]
    public void A_code_with_spaces_is_accepted()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.ComputeCode(Totp.FromBase32(secret), Totp.CounterFor(now));

        Totp.Verify(secret, $"{code[..3]} {code[3..]}", now).ShouldNotBeNull();
    }

    // ------------------------------------------------------------------ base32

    /// <summary>RFC 4648 section 10 Base32 vectors.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_the_RFC_4648_vectors(string plain, string encoded)
    {
        Totp.ToBase32(Encoding.ASCII.GetBytes(plain)).ShouldBe(encoded);
        Encoding.ASCII.GetString(Totp.FromBase32(encoded)).ShouldBe(plain);
    }

    /// <summary>Hand-retyped secrets survive the shapes people type them in.</summary>
    [Theory]
    [InlineData("mzxw6ytboi")]
    [InlineData("MZXW 6YTB OI")]
    [InlineData("MZXW-6YTB-OI")]
    [InlineData("MZXW6YTBOI======")]
    public void Base32_decoding_tolerates_how_people_retype_it(string encoded)
    {
        Encoding.ASCII.GetString(Totp.FromBase32(encoded)).ShouldBe("foobar");
    }

    [Fact]
    public void Base32_rejects_a_character_outside_the_alphabet()
    {
        Should.Throw<FormatException>(() => Totp.FromBase32("MZXW6YTB0I"));  // zero is not in the alphabet
    }

    [Fact]
    public void A_generated_secret_is_the_expected_size_and_is_not_repeated()
    {
        var first = Totp.GenerateSecret();
        var second = Totp.GenerateSecret();

        Totp.FromBase32(first).Length.ShouldBe(Totp.SecretBytes);
        first.ShouldNotBe(second);
    }

    // --------------------------------------------------------------------- uri

    /// <summary>
    /// The enrolment URI carries everything an authenticator app needs.
    /// </summary>
    /// <remarks>
    /// The issuer appears both as a label prefix and as a parameter because
    /// different apps read different ones.
    /// </remarks>
    [Fact]
    public void The_enrolment_uri_is_well_formed()
    {
        var uri = Totp.BuildUri("Endpoint Platform", "naman.jain@techsarasolutions.com", "MZXW6YTBOI");

        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain("Endpoint%20Platform:naman.jain%40techsarasolutions.com");
        uri.ShouldContain("secret=MZXW6YTBOI");
        uri.ShouldContain("issuer=Endpoint%20Platform");
        uri.ShouldContain("algorithm=SHA1");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
    }
}
