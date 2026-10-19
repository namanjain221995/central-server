using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Security;

/// <summary>
/// Per-organization security settings. Today it holds exactly one thing: the
/// verifier of the IT unlock code that lifts a persistent screen lock on the
/// device itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the verifier is stored, never the code.</b> The plaintext is turned into
/// a PBKDF2-SHA256 salt+hash when it is set and discarded; nothing here, and nothing
/// the service exposes, can recover it. That is what lets the verifier be copied
/// into an <c>EngageScreenLock</c> task so the agent can check a code entered on a
/// locked machine offline, without the code ever leaving the server.
/// </para>
/// <para>
/// One row per organization (a unique index enforces it), created the first time a
/// code is set and updated in place thereafter.
/// </para>
/// </remarks>
public sealed class OrganizationSecuritySetting : AuditableEntity
{
    public const int MaxAlgoLength = 32;

    /// <summary>Base64 of a 16-byte salt is 24 chars and of a 32-byte hash 44; 128 is ample headroom.</summary>
    public const int MaxVerifierPartLength = 128;

    private OrganizationSecuritySetting()
    {
        ScreenUnlockAlgo = null!;
        ScreenUnlockSalt = null!;
        ScreenUnlockHash = null!;
    }

    public OrganizationSecuritySetting(Guid organizationId, string algo, int iterations, string salt, string hash)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        ScreenUnlockAlgo = null!;
        ScreenUnlockSalt = null!;
        ScreenUnlockHash = null!;
        SetScreenUnlockVerifier(algo, iterations, salt, hash);
    }

    public Guid OrganizationId { get; private set; }

    public string ScreenUnlockAlgo { get; private set; }

    public int ScreenUnlockIterations { get; private set; }

    /// <summary>Base64 of the PBKDF2 salt.</summary>
    public string ScreenUnlockSalt { get; private set; }

    /// <summary>Base64 of the PBKDF2 output. A verifier, not a key: it proves a code, it does not store one.</summary>
    public string ScreenUnlockHash { get; private set; }

    /// <summary>Replaces the stored unlock-code verifier. The caller derives it; this only persists it.</summary>
    public void SetScreenUnlockVerifier(string algo, int iterations, string salt, string hash)
    {
        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "PBKDF2 iterations must be positive.");
        }

        ScreenUnlockAlgo = Guard.NotNullOrWhiteSpace(algo, nameof(algo), MaxAlgoLength);
        ScreenUnlockIterations = iterations;
        ScreenUnlockSalt = Guard.NotNullOrWhiteSpace(salt, nameof(salt), MaxVerifierPartLength);
        ScreenUnlockHash = Guard.NotNullOrWhiteSpace(hash, nameof(hash), MaxVerifierPartLength);
    }
}
