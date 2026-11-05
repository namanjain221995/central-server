using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EndpointAgent.Core.FileCollection;

/// <summary>
/// The agent half of the end-to-end encryption for one collection. It derives a
/// per-transfer AES key from the browser's public key and its own ephemeral key, and
/// encrypts the archive frame by frame. Only the browser, holding the matching private
/// key, can derive the same key and decrypt — the server relays ciphertext it cannot
/// read.
/// </summary>
/// <remarks>
/// ECDH on P-256 gives a shared secret; HKDF-SHA256 over it, bound to a random
/// per-transfer salt, gives the AES-256 key; each frame is AES-GCM with a fresh random
/// nonce and the frame index as associated data, so frames cannot be dropped or
/// reordered without detection. The wire frame is <c>nonce(12) || ciphertext || tag(16)</c>,
/// which is exactly what the browser's WebCrypto AES-GCM decrypt expects.
/// </remarks>
public sealed class FileCollectionCryptoSession : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] Info = Encoding.UTF8.GetBytes("epp-file-collection");

    private readonly AesGcm _gcm;

    private FileCollectionCryptoSession(AesGcm gcm, string ephemeralPublicKeyBase64, string saltBase64)
    {
        _gcm = gcm;
        EphemeralPublicKeyBase64 = ephemeralPublicKeyBase64;
        SaltBase64 = saltBase64;
    }

    /// <summary>The agent's one-time public key for this transfer, base64 of the raw P-256 point.</summary>
    public string EphemeralPublicKeyBase64 { get; }

    /// <summary>The HKDF salt for this transfer, base64.</summary>
    public string SaltBase64 { get; }

    /// <summary>
    /// Begins a session against the browser's public key. Throws
    /// <see cref="CryptographicException"/> if that key is not a valid uncompressed
    /// P-256 point.
    /// </summary>
    public static FileCollectionCryptoSession Create(string recipientPublicKeyBase64)
    {
        byte[] point;
        try
        {
            point = Convert.FromBase64String(recipientPublicKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Recipient public key is not valid base64.", ex);
        }

        if (point.Length != 65 || point[0] != 0x04)
        {
            throw new CryptographicException("Recipient public key must be a 65-byte uncompressed P-256 point.");
        }

        using var recipient = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = point.AsSpan(1, 32).ToArray(),
                Y = point.AsSpan(33, 32).ToArray(),
            },
        });

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var shared = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Info);
        CryptographicOperations.ZeroMemory(shared);

        var parameters = ephemeral.ExportParameters(false);
        var publicPoint = new byte[65];
        publicPoint[0] = 0x04;
        LeftPad(parameters.Q.X!, publicPoint.AsSpan(1, 32));
        LeftPad(parameters.Q.Y!, publicPoint.AsSpan(33, 32));

        return new FileCollectionCryptoSession(
            new AesGcm(key, TagSize),
            Convert.ToBase64String(publicPoint),
            Convert.ToBase64String(salt));
    }

    /// <summary>
    /// Encrypts one plaintext chunk into a wire frame:
    /// <c>nonce(12) || ciphertext || tag(16)</c>, with the frame index as associated data.
    /// </summary>
    public byte[] EncryptFrame(long seq, ReadOnlySpan<byte> plaintext)
    {
        var frame = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = frame.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = frame.AsSpan(NonceSize, plaintext.Length);
        var tag = frame.AsSpan(NonceSize + plaintext.Length, TagSize);

        Span<byte> aad = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(aad, seq);

        _gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return frame;
    }

    public void Dispose() => _gcm.Dispose();

    /// <summary>Right-aligns a coordinate into a fixed 32-byte field (export is already padded; this is belt-and-braces).</summary>
    private static void LeftPad(byte[] value, Span<byte> destination)
    {
        destination.Clear();
        value.AsSpan().CopyTo(destination[(destination.Length - value.Length)..]);
    }
}
