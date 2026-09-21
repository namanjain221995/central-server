using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>Why a second-factor step was refused.</summary>
public enum MfaError
{
    /// <summary>The challenge is unknown, expired, already used or out of attempts.</summary>
    ChallengeInvalid = 0,

    /// <summary>The code did not verify.</summary>
    CodeIncorrect = 1,

    /// <summary>The code was correct but has already been used.</summary>
    CodeAlreadyUsed = 2,

    /// <summary>The account has no confirmed second factor.</summary>
    NotEnrolled = 3,

    /// <summary>There is no enrolment in progress to confirm.</summary>
    NoEnrolmentInProgress = 4,
}

/// <param name="Secret">The Base32 secret, for hand entry when a camera will not read the QR.</param>
/// <param name="Uri">The otpauth:// URI the QR code encodes.</param>
public sealed record MfaEnrolmentStart(string Secret, string Uri);

/// <param name="RecoveryCodes">Shown exactly once. The server keeps only hashes.</param>
public sealed record MfaConfirmation(bool Success, MfaError? Error, IReadOnlyList<string> RecoveryCodes)
{
    public static MfaConfirmation Succeeded(IReadOnlyList<string> codes) => new(true, null, codes);

    public static MfaConfirmation Failed(MfaError error) => new(false, error, []);
}

/// <param name="Counter">The matched time step, or null when a recovery code was used.</param>
public sealed record MfaVerification(bool Success, MfaError? Error, Guid UserId, long? Counter)
{
    public static MfaVerification Succeeded(Guid userId, long? counter) => new(true, null, userId, counter);

    public static MfaVerification Failed(MfaError error) => new(false, error, Guid.Empty, null);
}

/// <summary>
/// Enrolment, confirmation and verification of the second factor.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <c>AdminAuthService</c>. That type already carries
/// sign-in, sign-out, session validation and password changes; folding the
/// second factor into it would make the one file that decides who is
/// authenticated harder to read than it already is.
/// </para>
/// <para>
/// <b>Recovery codes are hashed exactly like passwords</b>, with the same hasher
/// and the same cost. They are bearer credentials that bypass the second factor,
/// so treating them as anything weaker than a password would make the weakest
/// link the one nobody looks at.
/// </para>
/// </remarks>
public sealed class MfaService(
    EndpointPlatformDbContext dbContext,
    ITotpSecretProtector protector,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<MfaOptions> options,
    ILogger<MfaService> logger)
{
    /// <summary>How many recovery codes are issued at once.</summary>
    /// <remarks>
    /// Ten is the common convention and it is a reasonable balance: enough that
    /// losing a phone is survivable several times over, few enough to print on a
    /// card and store somewhere safe.
    /// </remarks>
    public const int RecoveryCodeCount = 10;

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ITotpSecretProtector _protector = protector
        ?? throw new ArgumentNullException(nameof(protector));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly MfaOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<MfaService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    // ------------------------------------------------------------- enrolment

    /// <summary>
    /// Issues a new secret and returns what the enrolment screen needs.
    /// </summary>
    /// <remarks>
    /// Overwrites any unconfirmed enrolment already in progress, so somebody who
    /// abandoned a half-finished setup - or scanned the QR on a phone they no
    /// longer have - can simply start again. A CONFIRMED enrolment is not
    /// replaced here; that requires a reset by an administrator.
    /// </remarks>
    public async Task<MfaEnrolmentStart?> BeginEnrolmentAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || user.HasConfirmedMfa)
        {
            return null;
        }

        var secret = Totp.GenerateSecret();
        user.BeginMfaEnrolment(_protector.Protect(secret));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new MfaEnrolmentStart(secret, Totp.BuildUri(_options.Issuer, user.Email, secret));
    }

    /// <summary>
    /// Confirms an enrolment by checking a code, and issues the recovery codes.
    /// </summary>
    /// <remarks>
    /// The recovery codes are returned here and never again. Generating them at
    /// confirmation rather than at <see cref="BeginEnrolmentAsync"/> means an
    /// abandoned enrolment never leaves usable bypass codes behind.
    /// </remarks>
    public async Task<MfaConfirmation> ConfirmEnrolmentAsync(
        Guid userId, string? code, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user?.TotpSealedSecret is null || user.TotpConfirmedAt is not null)
        {
            return MfaConfirmation.Failed(MfaError.NoEnrolmentInProgress);
        }

        var counter = Totp.Verify(_protector.Unprotect(user.TotpSealedSecret), code, now);
        if (counter is null)
        {
            return MfaConfirmation.Failed(MfaError.CodeIncorrect);
        }

        user.ConfirmMfaEnrolment(now, counter.Value);
        var codes = await ReplaceRecoveryCodesAsync(user.Id, cancellationToken);

        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "auth.mfa_enrolled",
            AuditResult.Success);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Administrator {UserId} completed multi-factor enrolment.", user.Id);
        return MfaConfirmation.Succeeded(codes);
    }

    // ---------------------------------------------------------- verification

    /// <summary>
    /// Checks a code or recovery code against a live challenge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every outcome consumes something. A wrong code increments the challenge's
    /// attempt counter; a right one consumes the challenge entirely. A six-digit
    /// code is only strong while guesses are bounded, and an unbounded challenge
    /// window would be an oracle for anyone holding a valid password.
    /// </para>
    /// <para>
    /// The TOTP path refuses a counter it has already seen. Without that a code
    /// stays usable for the rest of its ninety-second window, which is long enough
    /// to read one off a screen and type it somewhere else.
    /// </para>
    /// </remarks>
    public async Task<MfaVerification> VerifyChallengeAsync(
        string? challengeToken, string? code, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(challengeToken))
        {
            return MfaVerification.Failed(MfaError.ChallengeInvalid);
        }

        var hash = SecretGenerator.HashSecret(challengeToken);

        var challenge = await _dbContext.AdminMfaChallenges
            .Include(c => c.User)
            .SingleOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);

        // The security-stamp check is what makes a password change mid-flow
        // invalidate the challenge: if the account was reset because of a
        // suspected compromise, the attacker's half-finished sign-in must die too.
        if (challenge?.User is null
            || !challenge.IsUsable(now)
            || challenge.SecurityStamp != challenge.User.SecurityStamp)
        {
            return MfaVerification.Failed(MfaError.ChallengeInvalid);
        }

        var user = challenge.User;
        if (!user.HasConfirmedMfa || user.TotpSealedSecret is null)
        {
            return MfaVerification.Failed(MfaError.NotEnrolled);
        }

        // Recovery codes are tried first only when the input does not look like a
        // TOTP code, so a mistyped six-digit code never burns one.
        var looksLikeTotp = code?.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            is { Length: 6 } candidate && candidate.All(char.IsAsciiDigit);

        if (!looksLikeTotp)
        {
            return await VerifyRecoveryCodeAsync(challenge, user, code, now, cancellationToken);
        }

        var counter = Totp.Verify(_protector.Unprotect(user.TotpSealedSecret), code, now);

        if (counter is null)
        {
            challenge.RecordFailedAttempt();
            await AuditMfaFailureAsync(user, "Incorrect code.", cancellationToken);
            return MfaVerification.Failed(MfaError.CodeIncorrect);
        }

        if (user.TotpLastCounter is { } last && counter.Value <= last)
        {
            challenge.RecordFailedAttempt();
            await AuditMfaFailureAsync(user, "Code already used.", cancellationToken);
            return MfaVerification.Failed(MfaError.CodeAlreadyUsed);
        }

        user.RecordMfaCounter(counter.Value);
        challenge.Consume(now);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MfaVerification.Succeeded(user.Id, counter.Value);
    }

    private async Task<MfaVerification> VerifyRecoveryCodeAsync(
        AdminMfaChallenge challenge,
        PlatformUser user,
        string? code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalised = NormaliseRecoveryCode(code);

        if (normalised is null)
        {
            challenge.RecordFailedAttempt();
            await AuditMfaFailureAsync(user, "Malformed code.", cancellationToken);
            return MfaVerification.Failed(MfaError.CodeIncorrect);
        }

        var candidates = await _dbContext.MfaRecoveryCodes
            .Where(c => c.UserId == user.Id && c.UsedAt == null)
            .ToListAsync(cancellationToken);

        // Verified against the hash of every unused code. PasswordHasher.Verify is
        // deliberately expensive, and there are at most ten - the cost is the
        // point, and it is bounded.
        var match = candidates.FirstOrDefault(c => PasswordHasher.Verify(normalised, c.CodeHash));

        if (match is null)
        {
            challenge.RecordFailedAttempt();
            await AuditMfaFailureAsync(user, "Incorrect recovery code.", cancellationToken);
            return MfaVerification.Failed(MfaError.CodeIncorrect);
        }

        match.Redeem(now);
        challenge.Consume(now);

        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "auth.mfa_recovery_code_used",
            AuditResult.Success);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var remaining = candidates.Count - 1;
        _logger.LogWarning(
            "Administrator {UserId} signed in with a recovery code; {Remaining} remain.",
            user.Id, remaining);

        return MfaVerification.Succeeded(user.Id, null);
    }

    // ------------------------------------------------------------- recovery

    /// <summary>How many unused recovery codes an account has left.</summary>
    public Task<int> CountUnusedRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.MfaRecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, cancellationToken);

    /// <summary>
    /// Replaces every recovery code with a fresh set, returning the plaintext once.
    /// </summary>
    /// <remarks>
    /// Replaces rather than tops up: a set an administrator has partially used, or
    /// suspects was seen, should stop working entirely when they ask for new ones.
    /// The old rows are deleted rather than marked used, because they were never
    /// redeemed and recording them as such would be a lie in the audit trail.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.MfaRecoveryCodes
            .Where(c => c.UserId == userId)
            .ToListAsync(cancellationToken);

        _dbContext.MfaRecoveryCodes.RemoveRange(existing);

        var plaintext = new List<string>(RecoveryCodeCount);

        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var code = GeneratedPassword.CreateRecoveryCode();
            plaintext.Add(code);
            _dbContext.MfaRecoveryCodes.Add(
                new MfaRecoveryCode(userId, PasswordHasher.Hash(NormaliseRecoveryCode(code)!)));
        }

        return plaintext;
    }

    /// <summary>
    /// Folds a recovery code to the form it is hashed in.
    /// </summary>
    /// <remarks>
    /// Case and separators are presentation. Somebody reading a code off a printed
    /// card will type it in whatever shape they see, and refusing "abcd-efgh"
    /// because it was stored as "ABCDEFGH" would be a support call, not security.
    /// </remarks>
    private static string? NormaliseRecoveryCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var cleaned = new string([.. code.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Records a failed second-factor attempt, committing the challenge's attempt
    /// counter with it.
    /// </summary>
    /// <remarks>
    /// WriteImmediately, matching the sign-in failure path: the incremented
    /// attempt count on the challenge must persist even though a failed
    /// verification produces nothing else to save. Without the commit the counter
    /// would roll back and the attempt limit would never be reached.
    /// </remarks>
    private async Task AuditMfaFailureAsync(
        PlatformUser user, string reason, CancellationToken cancellationToken) =>
        await _auditWriter.WriteImmediatelyAsync(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "auth.mfa_verify",
            AuditResult.Failure,
            audit => audit.WithFailureReason(reason),
            cancellationToken);
}
