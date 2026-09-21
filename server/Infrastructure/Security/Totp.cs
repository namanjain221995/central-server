using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Time-based one-time passwords, RFC 6238, as every authenticator app implements
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written rather than taken from a package.</b> The algorithm is HMAC,
/// a counter and a truncation - about forty lines - and it is pinned by official
/// test vectors, so correctness is demonstrable rather than trusted. The server
/// has a deliberately small dependency surface and this did not justify adding to
/// it.
/// </para>
/// <para>
/// <b>SHA-1, six digits, thirty seconds.</b> Not a considered cryptographic
/// preference - it is what Google Authenticator, Microsoft Authenticator, 1Password
/// and the rest actually implement. An <c>otpauth://</c> URI carrying anything else
/// is silently ignored or mis-read by several of them. HMAC-SHA-1 is not affected
/// by the collision attacks that retired SHA-1 for signatures.
/// </para>
/// <para>
/// <b>Verification must be paired with replay prevention by the caller.</b> This
/// type returns the counter it matched; the caller stores it and refuses anything
/// not strictly greater. Without that, a code observed on someone's screen stays
/// usable for the rest of its window.
/// </para>
/// </remarks>
public static class Totp
{
    /// <summary>Digits in a generated code.</summary>
    public const int Digits = 6;

    /// <summary>Bytes of entropy in a new secret.</summary>
    /// <remarks>
    /// Twenty, matching the HMAC-SHA-1 block-derived length RFC 4226 recommends and
    /// what authenticator apps expect. Encodes to 32 Base32 characters.
    /// </remarks>
    public const int SecretBytes = 20;

    /// <summary>How long one code is valid.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Steps either side of the current one that are also accepted.
    /// </summary>
    /// <remarks>
    /// One step each way: a total window of about ninety seconds. Enough for a
    /// phone whose clock has drifted a little and for somebody typing slowly;
    /// small enough that an observed code expires quickly. Raising this multiplies
    /// an attacker's guessing surface linearly.
    /// </remarks>
    public const int DriftSteps = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Creates a new secret, Base32-encoded as authenticator apps expect.</summary>
    public static string GenerateSecret() => ToBase32(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>
    /// Checks a code, returning the counter it matched so the caller can refuse a replay.
    /// </summary>
    /// <returns>
    /// The matched counter, or null when the code does not match any accepted step.
    /// </returns>
    /// <remarks>
    /// Compares every candidate step before returning, rather than short-circuiting
    /// on the first match. The work is three HMACs either way, and a loop that exits
    /// early leaks which step matched through timing.
    /// </remarks>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base32Secret);

        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (trimmed.Length != Digits || !trimmed.All(char.IsAsciiDigit))
        {
            return null;
        }

        byte[] secret;
        try
        {
            secret = FromBase32(base32Secret);
        }
        catch (FormatException)
        {
            return null;
        }

        var current = CounterFor(now);
        long? matched = null;

        for (var offset = -DriftSteps; offset <= DriftSteps; offset++)
        {
            var counter = current + offset;
            var expected = ComputeCode(secret, counter);

            // Fixed-time comparison, and no early exit from the loop.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(trimmed)))
            {
                matched = counter;
            }
        }

        CryptographicOperations.ZeroMemory(secret);
        return matched;
    }

    /// <summary>The time step a moment falls in.</summary>
    public static long CounterFor(DateTimeOffset now) =>
        now.ToUnixTimeSeconds() / (long)Period.TotalSeconds;

    /// <summary>Computes the code for one counter value. RFC 4226 section 5.3.</summary>
    public static string ComputeCode(byte[] secret, long counter)
    {
        ArgumentNullException.ThrowIfNull(secret);

        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, message, hash);

        // Dynamic truncation: the low nibble of the last byte selects the offset.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var modulus = (int)Math.Pow(10, Digits);
        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    /// <summary>
    /// Builds the <c>otpauth://</c> URI an authenticator app reads from a QR code.
    /// </summary>
    /// <remarks>
    /// The issuer appears twice - as a path prefix and as a parameter - because
    /// different apps read different ones, and older readers show the account
    /// unlabelled without the prefix.
    /// </remarks>
    public static string BuildUri(string issuer, string account, string base32Secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(base32Secret);

        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);

        return $"otpauth://totp/{label}"
            + $"?secret={base32Secret}"
            + $"&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={(int)Period.TotalSeconds}";
    }

    // ------------------------------------------------------------------ base32

    /// <summary>RFC 4648 Base32, unpadded, as authenticator apps expect.</summary>
    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Decodes RFC 4648 Base32.
    /// </summary>
    /// <remarks>
    /// Tolerates padding, spaces and lower case, because people retype these by
    /// hand when a camera will not read the QR code.
    /// </remarks>
    public static byte[] FromBase32(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        var bytes = new List<byte>(encoded.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;

        foreach (var raw in encoded)
        {
            if (raw is '=' or ' ' or '-')
            {
                continue;
            }

            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException($"'{raw}' is not a Base32 character.");
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return [.. bytes];
    }
}
