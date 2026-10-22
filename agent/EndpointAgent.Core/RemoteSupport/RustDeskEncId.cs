using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace EndpointAgent.Core.RemoteSupport;

/// <summary>
/// Recovers a RustDesk device ID from the <c>enc_id</c> field of its configuration.
/// </summary>
/// <remarks>
/// <para>
/// Current RustDesk (1.4.7+, the fix for CVE-2026-30785) no longer stores the ID in
/// clear: <c>Config.store()</c> writes <c>enc_id</c> and blanks the plaintext
/// <c>id</c>. The <c>enc_id</c> is a NaCl secretbox (XSalsa20-Poly1305) of the ID
/// string, base64 behind a two-character <c>"00"</c> version prefix, keyed by the
/// machine's own UUID. This is a reversible, machine-local OBFUSCATION of a value the
/// device owner already reads off the RustDesk window -- a public dial identifier,
/// not a secret -- so recovering it on the same machine from that machine's own
/// MachineGuid only recreates what RustDesk itself shows.
/// </para>
/// <para>
/// Two on-disk framings exist and both are handled, mirroring RustDesk's own
/// <c>decrypt_str_or_original</c>:
/// </para>
/// <list type="bullet">
///   <item><b>FORMAT_V1</b> (1.4.7+): payload = <c>0x01</c> ‖ 24-byte random nonce ‖
///   secretbox(MAC ‖ ciphertext).</item>
///   <item><b>Legacy</b> (≤1.4.6): payload = secretbox(MAC ‖ ciphertext) under an
///   all-zero nonce, no format byte.</item>
/// </list>
/// <para>
/// The key is the MachineGuid STRING (as stored in the registry: lowercase,
/// hyphenated, no braces) as UTF-8 bytes, resized to 32 -- truncated here, since a
/// GUID string is 36 bytes. A wrong key simply fails the Poly1305 tag and yields
/// null, which the heartbeat treats as "no ID yet": an unreadable ID must never be a
/// fault.
/// </para>
/// </remarks>
internal static class RustDeskEncId
{
    private const byte FormatV1 = 0x01;
    private const int KeyBytes = 32;    // secretbox KEYBYTES
    private const int NonceBytes = 24;  // secretbox NONCEBYTES
    private const int MacBytes = 16;    // secretbox MACBYTES (Poly1305 tag)

    /// <summary>
    /// The plaintext ID from a base64 <c>enc_id</c> value (the <c>"00"</c> prefix
    /// already stripped by the caller), or null when it cannot be read with this
    /// machine's key.
    /// </summary>
    public static string? Decrypt(string? base64Payload, string? machineGuid)
    {
        if (string.IsNullOrEmpty(base64Payload) || string.IsNullOrEmpty(machineGuid))
        {
            return null;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64Payload);
        }
        catch (FormatException)
        {
            return null;
        }

        var key = DeriveKey(machineGuid);

        // FORMAT_V1: a format byte, then the random nonce used for this value.
        if (blob.Length >= 1 + NonceBytes + MacBytes && blob[0] == FormatV1)
        {
            var nonce = blob[1..(1 + NonceBytes)];
            var sealedBytes = blob[(1 + NonceBytes)..];
            var id = TryOpenToId(sealedBytes, nonce, key);
            if (id is not null)
            {
                return id;
            }
        }

        // Legacy: the whole blob is the secretbox output under an all-zero nonce.
        if (blob.Length >= MacBytes)
        {
            var id = TryOpenToId(blob, new byte[NonceBytes], key);
            if (id is not null)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Seals an ID the way RustDesk writes <c>enc_id</c>, returning the base64 payload
    /// (no <c>"00"</c> prefix). Internal and used only to build round-trip test
    /// vectors without a real device's identifiers; the agent itself never encrypts.
    /// </summary>
    internal static string Encrypt(string id, string machineGuid, byte[]? nonce = null, bool legacy = false)
    {
        var key = DeriveKey(machineGuid);
        nonce ??= legacy ? new byte[NonceBytes] : RandomNumberGenerator.GetBytes(NonceBytes);
        var message = Encoding.UTF8.GetBytes(id);

        var engine = new XSalsa20Engine();
        engine.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(key), nonce));

        var polyKey = new byte[32];
        engine.ProcessBytes(new byte[32], 0, 32, polyKey, 0);

        var ciphertext = new byte[message.Length];
        engine.ProcessBytes(message, 0, message.Length, ciphertext, 0);

        var poly = new Poly1305();
        poly.Init(new KeyParameter(polyKey));
        poly.BlockUpdate(ciphertext, 0, ciphertext.Length);
        var mac = new byte[MacBytes];
        poly.DoFinal(mac, 0);

        // Legacy: just the seal (MAC ‖ ciphertext) under a zero nonce, no framing.
        if (legacy)
        {
            return Convert.ToBase64String([.. mac, .. ciphertext]);
        }

        // FORMAT_V1: format byte, the nonce, then the seal.
        byte[] payload = [FormatV1, .. nonce, .. mac, .. ciphertext];
        return Convert.ToBase64String(payload);
    }

    /// <summary>
    /// <c>get_uuid()</c> is the MachineGuid string's UTF-8 bytes resized to 32. A GUID
    /// string is 36 bytes, so this truncates; the zero-pad branch only matters for a
    /// hypothetically shorter uuid.
    /// </summary>
    private static byte[] DeriveKey(string machineGuid)
    {
        var bytes = Encoding.UTF8.GetBytes(machineGuid);
        var key = new byte[KeyBytes];
        Array.Copy(bytes, key, Math.Min(bytes.Length, KeyBytes));
        return key;
    }

    /// <summary>
    /// Opens a secretbox (<c>MAC ‖ ciphertext</c>, the libsodium <c>_easy</c> layout)
    /// and returns the decrypted ID string, or null if the Poly1305 tag does not
    /// verify (wrong key or not this framing).
    /// </summary>
    private static string? TryOpenToId(byte[] sealedBytes, byte[] nonce, byte[] key)
    {
        if (sealedBytes.Length < MacBytes)
        {
            return null;
        }

        var mac = sealedBytes[..MacBytes];
        var ciphertext = sealedBytes[MacBytes..];

        try
        {
            var engine = new XSalsa20Engine();
            engine.Init(forEncryption: false, new ParametersWithIV(new KeyParameter(key), nonce));

            // The first 32 bytes of the XSalsa20 keystream are the one-time Poly1305
            // key; the message keystream continues from byte 32. Processing 32 zero
            // bytes yields that subkey and advances the engine to the message offset.
            var polyKey = new byte[32];
            engine.ProcessBytes(new byte[32], 0, 32, polyKey, 0);

            var poly = new Poly1305();
            poly.Init(new KeyParameter(polyKey));
            poly.BlockUpdate(ciphertext, 0, ciphertext.Length);
            var computed = new byte[MacBytes];
            poly.DoFinal(computed, 0);

            if (!CryptographicOperations.FixedTimeEquals(computed, mac))
            {
                return null;
            }

            var plaintext = new byte[ciphertext.Length];
            engine.ProcessBytes(ciphertext, 0, ciphertext.Length, plaintext, 0);

            var id = Encoding.UTF8.GetString(plaintext).Trim();
            return id.Length == 0 ? null : id;
        }
        catch (Exception)
        {
            // Any crypto-layer failure reads as "no ID this heartbeat", never a throw.
            return null;
        }
    }
}
