using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EndpointAgent.Core.FileCollection;

namespace EndpointAgent.Core.Tests.FileCollection;

/// <summary>
/// The agent's end-to-end encryption, verified against an independent "browser" that
/// derives the same key and decrypts — the real contract the dashboard will fulfil.
/// </summary>
public sealed class FileCollectionCryptoSessionTests
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("epp-file-collection");

    /// <summary>Stands in for the browser: a P-256 keypair whose public point is handed to the agent.</summary>
    private static (ECDiffieHellman Key, string PublicKeyBase64) NewRecipient()
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1);
        p.Q.Y!.CopyTo(point, 33);
        return (key, Convert.ToBase64String(point));
    }

    /// <summary>Derives the shared AES key the way the browser will, from the agent's header.</summary>
    private static byte[] DeriveRecipientKey(ECDiffieHellman recipient, FileCollectionCryptoSession session)
    {
        var agentPoint = Convert.FromBase64String(session.EphemeralPublicKeyBase64);
        using var agentPublic = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = agentPoint.AsSpan(1, 32).ToArray(),
                Y = agentPoint.AsSpan(33, 32).ToArray(),
            },
        });

        var shared = recipient.DeriveRawSecretAgreement(agentPublic.PublicKey);
        var salt = Convert.FromBase64String(session.SaltBase64);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Info);
    }

    private static byte[] Decrypt(byte[] key, long seq, byte[] frame)
    {
        var nonce = frame.AsSpan(0, 12);
        var tag = frame.AsSpan(frame.Length - 16, 16);
        var ciphertext = frame.AsSpan(12, frame.Length - 28);

        Span<byte> aad = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(aad, seq);

        var plaintext = new byte[ciphertext.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    [Fact]
    public void A_frame_the_agent_encrypts_is_what_the_recipient_decrypts()
    {
        var (recipient, recipientPublic) = NewRecipient();
        using (recipient)
        {
            using var session = FileCollectionCryptoSession.Create(recipientPublic);
            var key = DeriveRecipientKey(recipient, session);

            var plaintext = Encoding.UTF8.GetBytes("the contents of a very important document.zip chunk");
            var frame = session.EncryptFrame(0, plaintext);

            // nonce(12) + ciphertext(=plaintext length) + tag(16)
            frame.Length.ShouldBe(12 + plaintext.Length + 16);
            Decrypt(key, 0, frame).ShouldBe(plaintext);
        }
    }

    [Fact]
    public void Each_frame_uses_a_fresh_nonce()
    {
        var (recipient, recipientPublic) = NewRecipient();
        using (recipient)
        {
            using var session = FileCollectionCryptoSession.Create(recipientPublic);
            var a = session.EncryptFrame(0, [1, 2, 3]);
            var b = session.EncryptFrame(1, [1, 2, 3]);
            a.AsSpan(0, 12).ToArray().ShouldNotBe(b.AsSpan(0, 12).ToArray());
        }
    }

    [Fact]
    public void Decrypting_with_the_wrong_frame_index_fails()
    {
        var (recipient, recipientPublic) = NewRecipient();
        using (recipient)
        {
            using var session = FileCollectionCryptoSession.Create(recipientPublic);
            var key = DeriveRecipientKey(recipient, session);
            var frame = session.EncryptFrame(3, Encoding.UTF8.GetBytes("payload"));

            // The frame index is authenticated data, so claiming a different index
            // must fail the tag check — a reordered or replayed frame cannot pass.
            Should.Throw<AuthenticationTagMismatchException>(() => Decrypt(key, 4, frame));
        }
    }

    [Fact]
    public void A_recipient_key_that_is_not_a_p256_point_is_refused()
    {
        Should.Throw<CryptographicException>(() => FileCollectionCryptoSession.Create("not-base64-$$$"));
        Should.Throw<CryptographicException>(() =>
            FileCollectionCryptoSession.Create(Convert.ToBase64String(new byte[10])));
    }
}
