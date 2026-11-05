namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// Wire shapes for file collection, shared by the agent and the agent API. The agent
/// zips a Windows user's chosen folders in-process, encrypts the archive to the
/// browser's one-time public key, and moves it through the server as a header plus a
/// run of ciphertext frames, ending with a result. The server never stores the bytes;
/// it relays them, bounded, to the browser that started the transfer.
/// </summary>
public static class FileCollectionProtocol
{
    /// <summary>
    /// The largest single ciphertext frame the agent uploads and the server relays.
    /// Kept well under the agent route's 16 MB body limit, and small enough that only
    /// a few frames are ever buffered server-side at once.
    /// </summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Folder keys the agent accepts; nothing else resolves to a path.</summary>
    public static readonly string[] AllowedRootKeys = ["Desktop", "Downloads", "Documents"];
}

/// <summary>
/// The agent's key material for a transfer, posted once before any frame. The browser
/// derives the shared archive key from its own private key and this public key; the
/// salt binds the key derivation to this transfer.
/// </summary>
/// <param name="EphemeralPublicKey">Agent's one-time ECDH P-256 public key, base64 of the raw point.</param>
/// <param name="Salt">Random HKDF salt for this transfer, base64.</param>
public sealed record AgentFileCollectionHeader(string EphemeralPublicKey, string Salt);

/// <summary>
/// The agent's final word on a transfer, posted after the last frame. A failed or
/// truncated collection still ends here so the operator reads a real outcome rather
/// than a stalled download.
/// </summary>
/// <param name="Succeeded">True when the archive was produced and fully uploaded.</param>
/// <param name="Message">Short human-readable detail, especially on failure; length-bounded server-side.</param>
/// <param name="TotalBytes">Ciphertext bytes uploaded across all frames.</param>
/// <param name="FrameCount">How many frames were uploaded.</param>
/// <param name="Truncated">True when the archive hit the size ceiling and is incomplete.</param>
public sealed record AgentFileCollectionResult(
    bool Succeeded,
    string? Message,
    long TotalBytes,
    int FrameCount,
    bool Truncated);
