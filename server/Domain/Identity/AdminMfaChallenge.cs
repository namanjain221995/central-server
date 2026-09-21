using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// The short-lived ticket between "the password was correct" and "a session
/// exists".
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate row, deliberately not a session with a flag on it.</b> The
/// obvious alternative is to reuse the forced-password-change shape: issue a real
/// session and narrow what it can reach with middleware. That is wrong here. A
/// session token is a bearer credential accepted by the authentication handler,
/// so an attacker holding only the password would hold one - and its uselessness
/// would depend on one middleware staying correct forever. This type is not a
/// credential for anything except attempting the second factor.
/// </para>
/// <para>
/// <b>Stored as a hash, like a session token.</b> The platform never keeps a
/// bearer value it hands out; a database copy would be directly replayable.
/// </para>
/// <para>
/// <b>Bound to the security stamp.</b> If the account's password or roles change
/// between the password step and the code step, the challenge stops validating.
/// Without that binding, a password reset prompted by a suspected compromise
/// would leave the attacker's half-finished sign-in still live.
/// </para>
/// </remarks>
public sealed class AdminMfaChallenge : AuditableEntity
{
    private AdminMfaChallenge()
    {
        TokenHash = null!;
        SecurityStamp = null!;
    }

    public AdminMfaChallenge(
        Guid userId,
        string tokenHash,
        string securityStamp,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string? sourceIp,
        string? userAgent)
        : base(Guid.CreateVersion7())
    {
        UserId = Guard.NotEmpty(userId);
        TokenHash = Guard.NotNullOrWhiteSpace(tokenHash, nameof(tokenHash), maxLength: 128);
        SecurityStamp = Guard.NotNullOrWhiteSpace(securityStamp, nameof(securityStamp), maxLength: 64);
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        SourceIp = sourceIp;
        UserAgent = userAgent;
    }

    public Guid UserId { get; private set; }

    public PlatformUser? User { get; private set; }

    /// <summary>Hash of the ticket handed to the caller. Never the ticket itself.</summary>
    public string TokenHash { get; private set; }

    /// <summary>The account's security stamp when the password was accepted.</summary>
    public string SecurityStamp { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the challenge was answered or abandoned, or null while it is live.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    public string? SourceIp { get; private set; }

    public string? UserAgent { get; private set; }

    /// <summary>
    /// How many codes have been tried against this challenge.
    /// </summary>
    /// <remarks>
    /// A six-digit code is a one-in-a-million guess, which is only strong while
    /// the number of guesses is bounded. Without this counter the challenge window
    /// is an unmetered oracle: an attacker holding a valid password could try
    /// codes until one worked.
    /// </remarks>
    public int AttemptCount { get; private set; }

    /// <summary>Guesses allowed before the challenge is burned.</summary>
    /// <remarks>
    /// Five, matching the account lockout threshold. Generous for somebody
    /// mistyping or using a phone whose clock has drifted, and far below what
    /// guessing needs.
    /// </remarks>
    public const int MaxAttempts = 5;

    public bool IsUsable(DateTimeOffset now) =>
        ConsumedAt is null && ExpiresAt > now && AttemptCount < MaxAttempts;

    /// <summary>Counts a wrong code against the challenge.</summary>
    public void RecordFailedAttempt() => AttemptCount++;

    /// <summary>
    /// Marks the challenge finished, whether it succeeded or was given up on.
    /// </summary>
    /// <remarks>
    /// Consumed on SUCCESS as well as on abandonment, which is what makes the
    /// ticket single-use. A challenge that stayed live after producing a session
    /// could produce a second one.
    /// </remarks>
    public void Consume(DateTimeOffset now) => ConsumedAt ??= now;
}
