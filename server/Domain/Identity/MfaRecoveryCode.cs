using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// One single-use code that stands in for an authenticator app.
/// </summary>
/// <remarks>
/// <para>
/// Issued as a set when an administrator confirms enrolment, displayed exactly
/// once, and stored only as a hash - the same shape as a password, and for the
/// same reason. A readable column here would let anyone with database access
/// bypass the second factor of every account, which would make the whole
/// mechanism decorative.
/// </para>
/// <para>
/// <b>Single use is enforced by <see cref="UsedAt"/>, not by deletion.</b> A
/// spent code is kept so the audit trail can say which one was used and when. It
/// also means a set that has been entirely consumed is visibly exhausted rather
/// than indistinguishable from one that was never issued.
/// </para>
/// </remarks>
public sealed class MfaRecoveryCode : AuditableEntity
{
    private MfaRecoveryCode()
    {
        CodeHash = null!;
    }

    public MfaRecoveryCode(Guid userId, string codeHash)
        : base(Guid.CreateVersion7())
    {
        UserId = Guard.NotEmpty(userId);
        CodeHash = Guard.NotNullOrWhiteSpace(codeHash, nameof(codeHash), maxLength: 128);
    }

    public Guid UserId { get; private set; }

    public PlatformUser? User { get; private set; }

    /// <summary>Hash of the code. Never the code itself.</summary>
    public string CodeHash { get; private set; }

    /// <summary>When this code was redeemed, or null while it is still usable.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    public bool IsUsable => UsedAt is null;

    /// <summary>
    /// Marks the code spent.
    /// </summary>
    /// <remarks>
    /// Refuses a second redemption rather than silently allowing it. Two threads
    /// racing the same code is exactly the case single-use exists to stop, and a
    /// no-op would let both through.
    /// </remarks>
    public void Redeem(DateTimeOffset now)
    {
        if (UsedAt is not null)
        {
            throw new InvalidOperationException("This recovery code has already been used.");
        }

        UsedAt = now;
    }
}
