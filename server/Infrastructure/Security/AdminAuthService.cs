using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Sign-in, sign-out and per-request session validation for the Admin API.
/// </summary>
/// <remarks>
/// <para>
/// Sign-in failure behaviour: the caller always receives the same generic
/// failure whether the account is unknown, the password wrong, the account
/// disabled or locked — the distinctions live in the audit trail. A dummy
/// PBKDF2 verification runs for unknown accounts so response timing does not
/// reveal which addresses exist.
/// </para>
/// <para>
/// Every sign-in attempt, success or failure, is audited. Failures are written
/// immediately (nothing else commits); successes commit atomically with the
/// session row.
/// </para>
/// </remarks>

/// <summary>Why a password change was refused.</summary>
public enum ChangePasswordError
{
    /// <summary>The supplied current password did not verify, or the account is locked.</summary>
    CurrentPasswordIncorrect = 0,

    /// <summary>The new password does not meet <see cref="PasswordPolicy"/>.</summary>
    WeakPassword = 1,

    /// <summary>The new password is the one already in use.</summary>
    SameAsCurrent = 2,

    /// <summary>No such user, or the account has no password to change.</summary>
    NotPermitted = 3,
}

/// <param name="SessionsRevoked">
/// How many sessions the change invalidated, including the caller's own. Reported
/// so the console can say plainly that the caller must sign in again, rather than
/// leaving them to discover it on their next request.
/// </param>
public sealed record ChangePasswordOutcome(
    bool Success, ChangePasswordError? Error, string? Message, int SessionsRevoked)
{
    public static ChangePasswordOutcome Succeeded(int sessionsRevoked) =>
        new(true, null, null, sessionsRevoked);

    public static ChangePasswordOutcome Failed(ChangePasswordError error, string? message = null) =>
        new(false, error, message, 0);
}

public sealed class AdminAuthService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AdminAuthOptions> options,
    SignInAddressThrottle addressThrottle,
    ILogger<AdminAuthService> logger)
{
    /// <summary>A real hash of an unguessable value, used to equalise timing for unknown accounts.</summary>
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// How long somebody has to produce a code after their password is accepted.
    /// </summary>
    /// <remarks>
    /// Five minutes: long enough to find a phone, unlock it and read a code that
    /// may roll once while they type, and short enough that an abandoned challenge
    /// is not left lying around. The challenge is also bounded by an attempt
    /// counter, so this is not the only limit on it.
    /// </remarks>
    public static readonly TimeSpan MfaChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly AdminAuthOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly SignInAddressThrottle _addressThrottle = addressThrottle
        ?? throw new ArgumentNullException(nameof(addressThrottle));

    private readonly ILogger<AdminAuthService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<SignInOutcome> SignInAsync(
        string email,
        string password,
        string? sourceIp,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var normalized = email.Trim().ToUpperInvariant();

        // Checked BEFORE the account is loaded, and the refusal returns without
        // ever calling RecordFailedSignIn. That ordering is the denial-of-service
        // fix: an address that has spent its failure budget can no longer drive
        // any account's lockout counter, so an attacker who knows an
        // administrator's e-mail address cannot keep that person locked out.
        // Without this, per-account lockout is a weapon pointed at the victim.
        var address = await _addressThrottle.InspectAsync(sourceIp, cancellationToken);
        if (address.Blocked)
        {
            // Same generic failure as every other refusal - telling a caller they
            // are throttled tells them their guessing is having an effect. The
            // distinction is in the audit trail, which is where it belongs.
            PasswordHasher.Verify(password, DummyHash);
            await AuditSignInFailureAsync(null, email, "Source address throttled.", cancellationToken);
            return SignInOutcome.Failed();
        }

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (user is null)
        {
            // Equalise timing with the real-verification path.
            PasswordHasher.Verify(password, DummyHash);
            await _addressThrottle.RecordFailureAsync(sourceIp, cancellationToken);
            await AuditSignInFailureAsync(null, email, "Unknown account.", cancellationToken);
            return SignInOutcome.Failed();
        }

        if (user.Status == PlatformUserStatus.Disabled)
        {
            PasswordHasher.Verify(password, DummyHash);
            await _addressThrottle.RecordFailureAsync(sourceIp, cancellationToken);
            await AuditSignInFailureAsync(user, email, "Account is disabled.", cancellationToken);
            return SignInOutcome.Failed();
        }

        if (user.IsLockedOut(now))
        {
            PasswordHasher.Verify(password, DummyHash);
            await _addressThrottle.RecordFailureAsync(sourceIp, cancellationToken);
            await AuditSignInFailureAsync(user, email, "Account is locked out.", cancellationToken);
            return SignInOutcome.Failed();
        }

        if (user.PasswordHash is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            user.RecordFailedSignIn(
                now,
                _options.LockoutThreshold,
                TimeSpan.FromMinutes(_options.LockoutMinutes),
                TimeSpan.FromMinutes(_options.FailureDecayMinutes));
            await _addressThrottle.RecordFailureAsync(sourceIp, cancellationToken);
            await AuditSignInFailureAsync(user, email, "Wrong password.", cancellationToken);
            return SignInOutcome.Failed();
        }

        // The password is correct. Opportunistically upgrade the stored hash if
        // policy has moved on - this is safe before the second factor, because it
        // changes only how the same credential is stored.
        if (PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            user.SetPasswordHash(PasswordHasher.Hash(password), now);
        }

        // A second factor is owed. Stop here, and in particular do NOT call
        // RecordSuccessfulSignIn: it clears the failed-attempt counter and the
        // lockout, so calling it on a correct password alone would let anyone
        // holding a leaked password reset the lockout indefinitely while grinding
        // the second factor. The counter is cleared when a session is actually
        // issued, in CreateSessionAsync.
        if (user.HasConfirmedMfa)
        {
            var challengeToken = SecretGenerator.GenerateSecret();
            var challenge = new AdminMfaChallenge(
                user.Id,
                SecretGenerator.HashSecret(challengeToken),
                user.SecurityStamp,
                now,
                now.Add(MfaChallengeLifetime),
                sourceIp,
                userAgent);

            _dbContext.AdminMfaChallenges.Add(challenge);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return SignInOutcome.MfaChallenge(challengeToken, challenge.ExpiresAt);
        }

        // No confirmed second factor. A session is issued so the account can reach
        // the enrolment screen and nothing else - PlatformUser.HasConfirmedMfa is
        // false, so MfaEnrolmentRequiredMiddleware confines it. Enrolment is
        // mandatory but has to be completed by a signed-in person; there is no
        // earlier point at which it could be enforced.
        return await CreateSessionAsync(user, sourceIp, userAgent, now, cancellationToken);
    }

    /// <summary>Validates a presented session token; null when it is not acceptable.</summary>
    public async Task<AuthenticatedAdmin?> ValidateSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        var tokenHash = SecretGenerator.HashSecret(token);

        var session = await _dbContext.AdminSessions
            .Include(s => s.PlatformUser)
            .SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

        if (session?.PlatformUser is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var user = session.PlatformUser;

        if (user.Status != PlatformUserStatus.Active || !session.IsUsable(now, user.SecurityStamp))
        {
            return null;
        }

        session.Touch(now);
        // Persisted alongside whatever the request itself saves; a read-only
        // request skipping the touch write is acceptable.

        var permissions = await ResolvePermissionsAsync(user.Id, cancellationToken);

        return new AuthenticatedAdmin(
            user.Id, user.OrganizationId, user.Email, user.DisplayName, permissions, user.MustChangePassword);
    }

    /// <summary>
    /// Changes the signed-in administrator's own password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current password is verified here rather than trusted from the
    /// session. A live session proves who the caller was at sign-in; it does not
    /// prove the person at the keyboard now is the same one. Requiring the
    /// current password is what stops a borrowed or stolen session from locking
    /// the real owner out of their own account.
    /// </para>
    /// <para>
    /// <b>Every existing session dies as a side effect, including the caller's.</b>
    /// <see cref="PlatformUser.SetPasswordHash"/> rotates the security stamp, and
    /// <see cref="AdminSession.IsUsable"/> compares each session's snapshot of
    /// that stamp against the user's current one. This is deliberate and is the
    /// property that makes a password change meaningful: if the reason for
    /// changing it is that the old one leaked, sessions minted with it must not
    /// survive. The caller signs in again like everyone else.
    /// </para>
    /// <para>
    /// A wrong current password counts towards the same lockout the sign-in path
    /// uses, so an authenticated attacker cannot brute-force it any more cheaply
    /// than an unauthenticated one. It is deliberately NOT behind the per-address
    /// login rate limiter: that limiter exists to blunt credential stuffing
    /// against an anonymous endpoint, and applying it here would let one noisy
    /// client block a legitimate administrator from securing their account.
    /// </para>
    /// </remarks>
    public async Task<ChangePasswordOutcome> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || user.PasswordHash is null)
        {
            return ChangePasswordOutcome.Failed(ChangePasswordError.NotPermitted);
        }

        if (user.IsLockedOut(now))
        {
            // Same answer as a wrong password, and for the same reason the
            // sign-in path gives it: distinguishing "locked" from "wrong" tells
            // an attacker whether their guessing is having an effect.
            await AuditPasswordChangeFailureAsync(user, "Account is locked out.", cancellationToken);
            return ChangePasswordOutcome.Failed(ChangePasswordError.CurrentPasswordIncorrect);
        }

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            user.RecordFailedSignIn(
                now,
                _options.LockoutThreshold,
                TimeSpan.FromMinutes(_options.LockoutMinutes),
                TimeSpan.FromMinutes(_options.FailureDecayMinutes));
            await AuditPasswordChangeFailureAsync(user, "Current password incorrect.", cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Password change refused for {UserId}: the current password did not verify.", user.Id);

            return ChangePasswordOutcome.Failed(ChangePasswordError.CurrentPasswordIncorrect);
        }

        // Passed WITH the account's identity, so the screening rule that refuses a
        // password built from the person's own name or e-mail can fire. On an
        // internet-facing console that is the rule worth most: the e-mail address
        // is the username, so an attacker at the login form already has it.
        var context = PasswordContext.For(user.Email, user.DisplayName);

        if (PasswordPolicy.Validate(newPassword, context) is { } policyFailure)
        {
            // Not audited as a security failure and not counted towards lockout:
            // the caller has already proved who they are, and a weak-password
            // attempt is a usability event, not an attack.
            return ChangePasswordOutcome.Failed(ChangePasswordError.WeakPassword, policyFailure);
        }

        // Refused rather than silently accepted. Re-setting the same password
        // would rotate the stamp and destroy every session for no security gain,
        // which looks exactly like the platform malfunctioning.
        if (PasswordHasher.Verify(newPassword, user.PasswordHash))
        {
            return ChangePasswordOutcome.Failed(ChangePasswordError.SameAsCurrent);
        }

        // Rotates the security stamp, which is what invalidates every session.
        user.SetPasswordHash(PasswordHasher.Hash(newPassword), now);

        // Cleared HERE rather than inside SetPasswordHash. That method also runs on a
        // successful sign-in when the stored hash needs rehashing, so clearing it
        // there would let a plain sign-in with the generated password satisfy the
        // requirement - leaving a new administrator using a credential that was
        // displayed on a screen. This is the one code path that proves the owner
        // chose the password: it verified the current one above.
        user.CompleteRequiredPasswordChange();

        // Revoked explicitly as well as invalidated by the stamp. The stamp check
        // already makes them unusable; marking them revoked makes the reason
        // visible to anyone auditing the session table later, rather than leaving
        // rows that merely stopped working.
        var sessions = await _dbContext.AdminSessions
            .Where(x => x.PlatformUserId == user.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(now);
        }

        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "platform.user.password_changed",
            AuditResult.Success,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                // No password material of any kind: not the old value, not the
                // new one, not a hash, not a length, not a prefix. The audit
                // records that a change happened, by whom, and what it cost the
                // caller in sessions -- nothing that helps anyone guess it.
                .WithStateChange(
                    System.Text.Json.JsonSerializer.Serialize(new { sessionsRevoked = 0 }),
                    System.Text.Json.JsonSerializer.Serialize(new { sessionsRevoked = sessions.Count })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Password changed for {UserId}; {SessionCount} session(s) revoked.", user.Id, sessions.Count);

        return ChangePasswordOutcome.Succeeded(sessions.Count);
    }

    private async Task AuditPasswordChangeFailureAsync(
        PlatformUser user, string reason, CancellationToken cancellationToken)
    {
        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "platform.user.password_change_failed",
            AuditResult.Failure,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                .WithFailureReason(reason));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Verifies an already-signed-in administrator's current password, without
    /// changing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step-up authentication for operations where holding the permission is not
    /// enough -- revealing an escrowed BitLocker recovery key is the first. It
    /// answers the question "is this still the person who signed in", which a
    /// session cookie alone cannot.
    /// </para>
    /// <para>
    /// A wrong password counts towards the same lockout as a failed sign-in, and a
    /// locked-out account gets the same answer as a wrong password. Both match the
    /// sign-in and change-password paths, and for the same reason: telling a caller
    /// which of the two happened tells an attacker whether their guessing is
    /// working.
    /// </para>
    /// </remarks>
    public async Task<bool> VerifyCurrentPasswordAsync(
        Guid userId, string currentPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || user.PasswordHash is null || user.IsLockedOut(now))
        {
            return false;
        }

        if (PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return true;
        }

        user.RecordFailedSignIn(
                now,
                _options.LockoutThreshold,
                TimeSpan.FromMinutes(_options.LockoutMinutes),
                TimeSpan.FromMinutes(_options.FailureDecayMinutes));
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Step-up password verification failed for {UserId}.", userId);
        return false;
    }

    public async Task SignOutAsync(string token, CancellationToken cancellationToken = default)
    {
        var tokenHash = SecretGenerator.HashSecret(token);

        var session = await _dbContext.AdminSessions
            .Include(s => s.PlatformUser)
            .SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

        if (session is null)
        {
            return; // Signing out an unknown token is a no-op, not an error.
        }

        session.Revoke(_timeProvider.GetUtcNow());

        if (session.PlatformUser is { } user)
        {
            _auditWriter.Stage(
                user.OrganizationId,
                AuditActorType.PlatformUser,
                user.Id,
                user.Email,
                action: "auth.sign_out",
                AuditResult.Success);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Effective permission keys: user → roles → role permissions.</summary>
    private async Task<IReadOnlyList<string>> ResolvePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.PlatformUserRoles
            .Where(ur => ur.PlatformUserId == userId)
            .Join(_dbContext.RolePermissions, ur => ur.RoleId, rp => rp.RoleId, (_, rp) => rp.PermissionId)
            .Join(_dbContext.Permissions, id => id, p => p.Id, (_, p) => p.Key)
            .Distinct()
            .OrderBy(key => key)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Issues a session. The single place a session comes into existence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the no-second-factor path and by
    /// <see cref="CompleteMfaSignInAsync"/>, so the two cannot drift. In
    /// particular <see cref="PlatformUser.RecordSuccessfulSignIn"/> is called
    /// HERE and nowhere else: clearing the lockout counter is a consequence of
    /// actually signing in, not of getting the password right, and moving it
    /// earlier would hand a password-only attacker an unlimited supply of
    /// attempts against the second factor.
    /// </para>
    /// <para>
    /// The caller is responsible for having established that the account may sign
    /// in at all. This method does not re-check status or lockout.
    /// </para>
    /// </remarks>
    private async Task<SignInOutcome> CreateSessionAsync(
        PlatformUser user,
        string? sourceIp,
        string? userAgent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        user.RecordSuccessfulSignIn(now);

        var token = SecretGenerator.GenerateSecret();

        var session = new AdminSession(
            user.Id,
            SecretGenerator.HashSecret(token),
            user.SecurityStamp,
            now,
            now.AddHours(_options.SessionLifetimeHours),
            sourceIp,
            userAgent);

        _dbContext.AdminSessions.Add(session);

        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "auth.sign_in",
            AuditResult.Success);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var permissions = await ResolvePermissionsAsync(user.Id, cancellationToken);

        return SignInOutcome.Succeeded(user, token, session.ExpiresAt, permissions);
    }

    /// <summary>
    /// Finishes a sign-in that was waiting on a second factor.
    /// </summary>
    /// <remarks>
    /// The account's status and lockout are re-checked here rather than trusted
    /// from the password step. A challenge lasts five minutes, and an account can
    /// be disabled or locked inside that window - by another administrator
    /// responding to exactly the incident that makes it matter.
    /// </remarks>
    public async Task<SignInOutcome> CompleteMfaSignInAsync(
        string? challengeToken,
        string? code,
        string? sourceIp,
        string? userAgent,
        MfaService mfaService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mfaService);

        var now = _timeProvider.GetUtcNow();

        var address = await _addressThrottle.InspectAsync(sourceIp, cancellationToken);
        if (address.Blocked)
        {
            return SignInOutcome.Failed();
        }

        var verification = await mfaService.VerifyChallengeAsync(challengeToken, code, cancellationToken);

        if (!verification.Success)
        {
            await _addressThrottle.RecordFailureAsync(sourceIp, cancellationToken);
            return SignInOutcome.Failed();
        }

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == verification.UserId, cancellationToken);

        if (user is null || user.Status == PlatformUserStatus.Disabled || user.IsLockedOut(now))
        {
            await AuditSignInFailureAsync(
                user, user?.Email ?? "unknown", "Account not usable at second factor.", cancellationToken);
            return SignInOutcome.Failed();
        }

        return await CreateSessionAsync(user, sourceIp, userAgent, now, cancellationToken);
    }

    private async Task AuditSignInFailureAsync(
        PlatformUser? user,
        string attemptedEmail,
        string reason,
        CancellationToken cancellationToken)
    {
        var organizationId = user?.OrganizationId
            ?? await _dbContext.Organizations
                .OrderBy(o => o.CreatedAt)
                .Select(o => (Guid?)o.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (organizationId is null)
        {
            _logger.LogWarning("Sign-in failed ({Reason}) and no organization exists to audit against.", reason);
            return;
        }

        // WriteImmediately: the failed-attempt counter on the user (if any) must
        // persist even though the sign-in itself produced nothing else to save.
        await _auditWriter.WriteImmediatelyAsync(
            organizationId.Value,
            user is null ? AuditActorType.Anonymous : AuditActorType.PlatformUser,
            user?.Id,
            attemptedEmail.Trim(),
            action: "auth.sign_in",
            AuditResult.Failure,
            audit => audit.WithFailureReason(reason),
            cancellationToken);

        _logger.LogWarning("Admin sign-in failed for {Email}: {Reason}", attemptedEmail, reason);
    }
}

/// <param name="MfaChallengeToken">
/// Set when the password was correct but a second factor is still owed. It is NOT
/// a session token: it authenticates nothing and may only be presented to the
/// verify endpoint.
/// </param>
public sealed record SignInOutcome(
    bool Success,
    PlatformUser? User,
    string? Token,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Permissions,
    string? MfaChallengeToken = null)
{
    /// <summary>The password was accepted and a second factor is required.</summary>
    /// <remarks>
    /// Distinguished from <see cref="Success"/> on purpose. A challenge is not a
    /// partial success to be treated leniently - nothing is authenticated until
    /// the code is verified, and the caller must not act on <see cref="User"/>.
    /// </remarks>
    public bool MfaRequired => MfaChallengeToken is not null;

    public static SignInOutcome Failed() => new(false, null, null, default, []);

    public static SignInOutcome Succeeded(
        PlatformUser user, string token, DateTimeOffset expiresAt, IReadOnlyList<string> permissions) =>
        new(true, user, token, expiresAt, permissions);

    /// <summary>
    /// The password was right; the second factor is owed.
    /// </summary>
    /// <remarks>
    /// Carries no user and no permissions. Handing either back here would invite a
    /// caller to render a signed-in console before the second factor had been
    /// supplied.
    /// </remarks>
    public static SignInOutcome MfaChallenge(string challengeToken, DateTimeOffset expiresAt) =>
        new(false, null, null, expiresAt, [], challengeToken);
}

/// <summary>The authenticated principal attached to a validated request.</summary>
/// <param name="MustChangePassword">
/// True while this administrator still holds a server-generated password. The
/// API refuses everything except reading their own identity and changing that
/// password until it is false.
/// </param>
public sealed record AuthenticatedAdmin(
    Guid UserId,
    Guid OrganizationId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Permissions,
    bool MustChangePassword);
