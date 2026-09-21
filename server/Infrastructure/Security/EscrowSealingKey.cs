using System.Security.Cryptography;
using EndpointPlatform.Domain.BitLocker;
using Microsoft.Extensions.Configuration;

namespace EndpointPlatform.Infrastructure.Security;

public sealed class EscrowSealingKeyOptions
{
    public const string SectionName = "RecoveryEscrow";

    /// <summary>
    /// Base64 SPKI of the RSA-3072 public key that endpoints seal to. Public
    /// material: it encrypts and cannot decrypt, so both APIs may hold it.
    /// </summary>
    public string? SealingPublicKey { get; init; }

    /// <summary>
    /// Base64 PKCS#8 of the matching private key. <b>Admin API only.</b> Anything
    /// holding this can read every automatically escrowed recovery password.
    /// </summary>
    public string? SealingPrivateKey { get; init; }
}

/// <summary>
/// The public half of the escrow sealing keypair, and the fingerprint agents pin.
/// </summary>
/// <remarks>
/// Deliberately public-only. The Agent API needs to know which key endpoints seal
/// to -- to pin it at enrollment and to reject an envelope sealed to something else
/// -- and needs no ability to open what it stores. Keeping the type incapable of
/// decryption means the boundary survives a careless registration as well as a
/// careful one.
/// </remarks>
public interface IEscrowSealingKeyProvider
{
    /// <summary>Null when no sealing key is configured; the estate is then ineligible.</summary>
    string? PublicKeySpki { get; }

    /// <summary>Hex SHA-256 over the SPKI, or null when unconfigured.</summary>
    string? Fingerprint { get; }

    bool IsConfigured { get; }
}

public sealed class EscrowSealingKeyProvider : IEscrowSealingKeyProvider
{
    public EscrowSealingKeyProvider(string? publicKeySpkiBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64))
        {
            return;
        }

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(publicKeySpkiBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{EscrowSealingKeyOptions.SectionName}:SealingPublicKey must be base64-encoded "
                + "SubjectPublicKeyInfo.", ex);
        }

        using var rsa = RSA.Create();

        try
        {
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"{EscrowSealingKeyOptions.SectionName}:SealingPublicKey is not a valid RSA public key.",
                ex);
        }

        if (rsa.KeySize < 3072)
        {
            throw new InvalidOperationException(
                $"{EscrowSealingKeyOptions.SectionName}:SealingPublicKey is {rsa.KeySize} bits; "
                + "at least 3072 are required.");
        }

        PublicKeySpki = publicKeySpkiBase64;
        Fingerprint = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    public string? PublicKeySpki { get; }

    public string? Fingerprint { get; }

    public bool IsConfigured => Fingerprint is not null;
}

/// <summary>
/// Refuses to let the Agent API start while it is configured with anything that
/// could decrypt an escrowed recovery password.
/// </summary>
/// <remarks>
/// <para>
/// The key boundary was, until now, a matter of which services happened to be
/// registered in which host. Both APIs compile against the same infrastructure
/// assembly, so nothing structural stopped a future edit -- or a copied
/// environment file -- from handing the endpoint-facing process the means to read
/// the estate's recovery passwords.
/// </para>
/// <para>
/// This turns that convention into a startup assertion. It is called from the
/// Agent API's composition root, and it fails the host rather than logging a
/// warning, because a warning about this would be read after the fact.
/// </para>
/// </remarks>
public static class AgentApiKeyBoundaryGuard
{
    /// <summary>Settings the Agent API must never have.</summary>
    private static readonly string[] Forbidden =
    [
        "RecoveryEscrow:Key",
        "RecoveryEscrow:SealingPrivateKey",

        // Multi-factor secrets. Not escrow material, but the same boundary
        // applies: holding this key would let the endpoint-facing process derive
        // a valid second factor for any administrator, which is precisely what
        // the second factor exists to prevent. Listed here rather than trusted to
        // gen-env.sh, so a hand-edited environment file fails at startup instead
        // of silently widening the blast radius of an agent compromise.
        "Mfa:TotpKey",
    ];

    public static void AssertNoDecryptionKeys(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var present = Forbidden
            .Where(key => !string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();

        if (present.Length == 0)
        {
            return;
        }

        // Names the setting, never its value.
        throw new InvalidOperationException(
            "The Agent API is configured with escrow decryption key material ("
            + string.Join(", ", present)
            + "). This process is reachable by every managed endpoint and must never be able to "
            + "decrypt an escrowed recovery password. Remove the setting from the Agent API's "
            + "configuration; it belongs to the Admin API alone.");
    }
}

/// <summary>
/// Refuses to let the Admin API start configured so that automatic escrow could
/// succeed while reveal never can.
/// </summary>
/// <remarks>
/// <para>
/// The failure this prevents is silent and delayed, which is what makes it worth a
/// startup check. Endpoints seal to the public key; if the Admin API has no
/// matching private half, every escrow still <em>succeeds</em> -- the agent seals,
/// the Agent API stores, the dashboard reports a filed key -- and the fact that
/// nobody can ever open it is discovered on the day a disk will not boot. An estate
/// could fill up with unreadable credentials and look perfectly healthy the whole
/// time.
/// </para>
/// <para>
/// So: a configured public key requires a private key, and the two must be the same
/// keypair. Checked by deriving the public half from the private one and comparing
/// fingerprints, rather than trusting that whoever pasted them in pasted a matching
/// pair.
/// </para>
/// <para>
/// Configuring neither is fine and stays fine -- automatic escrow is simply not
/// available, which is an ordinary state and not one worth refusing to boot over.
/// </para>
/// </remarks>
public static class AdminApiSealingKeyGuard
{
    public static void AssertRevealRemainsPossible(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var publicKey = configuration["RecoveryEscrow:SealingPublicKey"];
        var privateKey = configuration["RecoveryEscrow:SealingPrivateKey"];

        var hasPublic = !string.IsNullOrWhiteSpace(publicKey);
        var hasPrivate = !string.IsNullOrWhiteSpace(privateKey);

        if (!hasPublic && !hasPrivate)
        {
            return;
        }

        if (hasPublic && !hasPrivate)
        {
            throw new InvalidOperationException(
                "RecoveryEscrow:SealingPublicKey is configured but RecoveryEscrow:SealingPrivateKey is "
                + "not. Endpoints would seal recovery passwords this server can never open, and every "
                + "escrow would appear to succeed. Configure the matching private key, or remove the "
                + "public key to leave automatic escrow switched off.");
        }

        // A private key alone is harmless -- nothing seals to it -- but it is
        // almost certainly a half-finished deployment, and the pairing check below
        // is the thing worth running either way.
        using var priv = RSA.Create();

        try
        {
            priv.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey!), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "RecoveryEscrow:SealingPrivateKey is not a valid base64 PKCS#8 RSA private key.", ex);
        }

        if (priv.KeySize < 3072)
        {
            throw new InvalidOperationException(
                $"RecoveryEscrow:SealingPrivateKey is {priv.KeySize} bits; at least 3072 are required.");
        }

        if (!hasPublic)
        {
            return;
        }

        var derived = Convert.ToHexString(
            SHA256.HashData(priv.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var configured = new EscrowSealingKeyProvider(publicKey).Fingerprint;

        if (!string.Equals(derived, configured, StringComparison.OrdinalIgnoreCase))
        {
            // Names neither key, only that they disagree.
            throw new InvalidOperationException(
                "RecoveryEscrow:SealingPublicKey and RecoveryEscrow:SealingPrivateKey are not the same "
                + "keypair. Endpoints would seal to a key this server cannot unwrap, so every "
                + "automatically escrowed recovery password would be unreadable.");
        }
    }
}

/// <summary>
/// Opens an endpoint-sealed envelope. <b>Admin API only.</b>
/// </summary>
public interface IHybridEnvelopeUnsealer
{
    bool IsConfigured { get; }

    /// <summary>
    /// Unwraps the data key with the RSA private half and decrypts the password.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The envelope was sealed to a different key, or has been tampered with.
    /// </exception>
    string Unseal(string envelopeJson);
}

/// <summary>
/// The one place an automatically escrowed recovery password becomes readable.
/// </summary>
/// <remarks>
/// <para>
/// Registered only in the Admin API, and reached only from the existing reveal
/// path -- permission, device scope, step-up password, rate limit, audit. There is
/// deliberately no second, lighter route for automatic escrows: an operator
/// retrieving one passes exactly the checks they would for a manually filed key,
/// because the credential is the same kind of thing either way.
/// </para>
/// <para>
/// Nothing here logs, returns or embeds plaintext in an exception. A failure says
/// unsealing failed, never what was being unsealed.
/// </para>
/// </remarks>
public sealed class RsaHybridEnvelopeUnsealer : IHybridEnvelopeUnsealer, IDisposable
{
    private readonly RSA? _privateKey;

    public RsaHybridEnvelopeUnsealer(string? privateKeyPkcs8Base64)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64))
        {
            // Left unconfigured, hybrid reveal is unavailable and says so. It is not
            // a startup failure: an estate with no automatic escrows yet is a
            // perfectly valid state, and refusing to boot over it would take the
            // console down for a capability nothing is using.
            return;
        }

        byte[] pkcs8;
        try
        {
            pkcs8 = Convert.FromBase64String(privateKeyPkcs8Base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{EscrowSealingKeyOptions.SectionName}:SealingPrivateKey must be base64-encoded PKCS#8.",
                ex);
        }

        var rsa = RSA.Create();

        try
        {
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        catch (CryptographicException ex)
        {
            rsa.Dispose();
            throw new InvalidOperationException(
                $"{EscrowSealingKeyOptions.SectionName}:SealingPrivateKey is not a valid RSA private key.",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        _privateKey = rsa;
    }

    public bool IsConfigured => _privateKey is not null;

    public string Unseal(string envelopeJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);

        if (_privateKey is null)
        {
            throw new InvalidOperationException(
                $"{EscrowSealingKeyOptions.SectionName}:SealingPrivateKey is not configured, so "
                + "automatically escrowed recovery passwords cannot be revealed.");
        }

        if (SealedRecoveryEnvelope.Validate(envelopeJson, out var envelope) != SealedEnvelopeError.None
            || envelope is null)
        {
            throw new CryptographicException("The sealed recovery envelope is malformed.");
        }

        var dataKey = _privateKey.Decrypt(
            Convert.FromBase64String(envelope.WrappedKey!), RSAEncryptionPadding.OaepSHA256);

        var ciphertext = Convert.FromBase64String(envelope.Ciphertext!);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(dataKey, SealedRecoveryEnvelope.TagBytes);

            aes.Decrypt(
                Convert.FromBase64String(envelope.Nonce!),
                ciphertext,
                Convert.FromBase64String(envelope.Tag!),
                plaintext);

            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose() => _privateKey?.Dispose();
}
