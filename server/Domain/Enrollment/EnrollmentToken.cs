using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Enrollment;

/// <summary>
/// A scoped, expiring, limited-use token that authorises agent enrollment.
/// </summary>
/// <remarks>
/// <para>
/// The token secret itself is generated in the application layer, shown to the
/// administrator exactly once, and stored here only as a SHA-256 hash. A database
/// leak therefore yields nothing enrollable. (SHA-256 without salt/stretching is
/// correct here, unlike for passwords: the secret is 256 bits of CSPRNG output,
/// so dictionary and rainbow attacks have nothing to bite on, and lookups must be
/// possible by hash.)
/// </para>
/// <para>
/// Usage accounting is intentionally conservative: <see cref="TryConsume"/> checks
/// expiry, revocation and remaining uses at the domain level, and the persistence
/// layer additionally relies on optimistic concurrency (<c>xmin</c>) so two agents
/// racing for the last use cannot both win.
/// </para>
/// </remarks>
public sealed class EnrollmentToken : AuditableEntity
{
    public const int SecretHashLength = 64; // hex-encoded SHA-256

    private EnrollmentToken()
    {
        Name = null!;
        SecretHash = null!;
        CreatedByDisplay = null!;
    }

    public EnrollmentToken(
        Guid organizationId,
        string name,
        string secretHash,
        Guid createdByUserId,
        string createdByDisplay,
        DateTimeOffset expiresAt,
        int maxUses)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 200);
        SecretHash = ValidateSecretHash(secretHash);
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), maxLength: 320);
        ExpiresAt = expiresAt;

        if (maxUses is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxUses), maxUses, "Max uses must be between 1 and 10,000.");
        }

        MaxUses = maxUses;
        UseCount = 0;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>Administrator-facing label, e.g. "Finance laptops August 2026".</summary>
    public string Name { get; private set; }

    /// <summary>Hex-encoded SHA-256 of the token secret. The secret itself is never stored.</summary>
    public string SecretHash { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>Denormalised for audit lineage; survives account renames.</summary>
    public string CreatedByDisplay { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public int MaxUses { get; private set; }

    public int UseCount { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsExhausted => UseCount >= MaxUses;

    public bool IsUsable(DateTimeOffset now) => !IsRevoked && !IsExpired(now) && !IsExhausted;

    /// <summary>
    /// Answers what <see cref="TryConsume"/> would answer right now, without
    /// spending a use.
    /// </summary>
    /// <remarks>
    /// Exists for decisions that must be made BEFORE committing a use — above all
    /// the re-enrollment approval gate, where a parked agent retries the same
    /// token every few minutes while it waits. Consuming on each retry would burn
    /// a limited-use token down to nothing before anyone approved anything, so
    /// the gate validates here and consumes only on the attempt that actually
    /// enrolls. Checks in the same order as <see cref="TryConsume"/>, so the
    /// audited refusal reason cannot differ between the two.
    /// </remarks>
    public EnrollmentTokenConsumeResult Validate(DateTimeOffset now)
    {
        if (IsRevoked)
        {
            return EnrollmentTokenConsumeResult.Revoked;
        }

        if (IsExpired(now))
        {
            return EnrollmentTokenConsumeResult.Expired;
        }

        if (IsExhausted)
        {
            return EnrollmentTokenConsumeResult.Exhausted;
        }

        return EnrollmentTokenConsumeResult.Consumed;
    }

    /// <summary>
    /// Attempts to consume one use. Returns the reason on refusal rather than
    /// throwing, because a refused enrollment is an expected, auditable outcome —
    /// not an exceptional one.
    /// </summary>
    public EnrollmentTokenConsumeResult TryConsume(DateTimeOffset now)
    {
        var result = Validate(now);
        if (result != EnrollmentTokenConsumeResult.Consumed)
        {
            return result;
        }

        UseCount++;
        return EnrollmentTokenConsumeResult.Consumed;
    }

    public void Revoke(DateTimeOffset now)
    {
        // Idempotent: revoking twice keeps the first timestamp, which is the one
        // that matters for audit.
        RevokedAt ??= now;
    }

    private static string ValidateSecretHash(string secretHash)
    {
        var value = Guard.NotNullOrWhiteSpace(secretHash, nameof(secretHash), maxLength: SecretHashLength);

        if (value.Length != SecretHashLength)
        {
            throw new ArgumentException(
                $"Secret hash must be exactly {SecretHashLength} hex characters (SHA-256).",
                nameof(secretHash));
        }

        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                throw new ArgumentException(
                    "Secret hash must be lowercase hexadecimal.", nameof(secretHash));
            }
        }

        return value;
    }
}

public enum EnrollmentTokenConsumeResult
{
    Consumed = 0,
    Expired = 1,
    Revoked = 2,
    Exhausted = 3,
}
