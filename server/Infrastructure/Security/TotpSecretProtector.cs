using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Seals and unseals the shared secret behind an administrator's authenticator app.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="ISecretProtector"/>, and the difference would be
/// a silent disaster.</b> That one protects ephemeral values in Redis and falls
/// back to a random process-local key when none is configured - correct there,
/// because a lost key invalidates in-flight secrets that fail safely. A TOTP
/// secret is durable. Sealed with a process-local key it becomes unreadable at the
/// next restart, with no error at any point, and the loss is discovered only when
/// every administrator is simultaneously unable to complete their second factor.
/// </para>
/// <para>
/// <b>It also needs its own key rather than reusing the escrow key.</b>
/// <c>SECRET_PROTECTION_KEY</c> is issued to both API processes, so anything
/// sealed with it is readable by the Agent API - the process every managed
/// endpoint can reach. <c>Mfa:TotpKey</c> is listed in
/// <c>AgentApiKeyBoundaryGuard</c> so that boundary is enforced at startup rather
/// than remembered.
/// </para>
/// </remarks>
public interface ITotpSecretProtector
{
    /// <summary>Seals a Base32 TOTP secret for storage.</summary>
    string Protect(string base32Secret);

    /// <summary>Reverses <see cref="Protect"/>. Throws on tampering or a wrong key.</summary>
    string Unprotect(string sealedValue);
}

public sealed class MfaOptions
{
    public const string SectionName = "Mfa";

    /// <summary>
    /// Base64 32-byte key sealing TOTP secrets at rest. Mandatory on the Admin API.
    /// </summary>
    /// <remarks>
    /// No default and no generated fallback, for the reason given on
    /// <see cref="ITotpSecretProtector"/>: a fallback key would silently destroy
    /// every enrolment at the next restart.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string? TotpKey { get; init; }

    /// <summary>
    /// The name authenticator apps show beside the code.
    /// </summary>
    /// <remarks>
    /// Shown to the person, so it should be recognisable rather than technical. A
    /// deployment serving several organisations may want its own here.
    /// </remarks>
    public string Issuer { get; init; } = "Endpoint Platform";
}

/// <summary>AES-256-GCM, with the nonce and tag carried in the envelope.</summary>
/// <remarks>
/// The envelope layout matches <c>AesGcmRecoveryKeyProtector</c> deliberately:
/// nonce, then tag, then ciphertext, base64 encoded. One shape to reason about
/// across everything this platform seals at rest.
/// </remarks>
public sealed class AesGcmTotpSecretProtector : ITotpSecretProtector
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] _key;

    public AesGcmTotpSecretProtector(IOptions<MfaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.TotpKey;

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{MfaOptions.SectionName}:TotpKey is required. Multi-factor secrets are sealed at rest, "
                + "so there is deliberately no generated fallback: a process-local key would make every "
                + "enrolment unreadable after a restart, and the loss would only be discovered when "
                + "nobody could complete a sign-in.");
        }

        try
        {
            _key = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{MfaOptions.SectionName}:TotpKey must be a base64-encoded 32-byte key.", ex);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{MfaOptions.SectionName}:TotpKey must decode to exactly 32 bytes (got {_key.Length}).");
        }
    }

    public string Protect(string base32Secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base32Secret);

        var plaintextBytes = Encoding.UTF8.GetBytes(base32Secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        try
        {
            using var aes = new AesGcm(_key, TagLength);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        var envelope = new byte[NonceLength + TagLength + ciphertext.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, NonceLength);
        ciphertext.CopyTo(envelope, NonceLength + TagLength);

        return Convert.ToBase64String(envelope);
    }

    public string Unprotect(string sealedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sealedValue);

        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(sealedValue);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The sealed multi-factor secret is malformed.", ex);
        }

        if (envelope.Length < NonceLength + TagLength)
        {
            throw new CryptographicException("The sealed multi-factor secret is truncated.");
        }

        var nonce = envelope.AsSpan(0, NonceLength);
        var tag = envelope.AsSpan(NonceLength, TagLength);
        var ciphertext = envelope.AsSpan(NonceLength + TagLength);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, TagLength);

            // Throws if the tag does not verify, which covers both tampering and
            // the wrong key. There is no path that returns unauthenticated data.
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
