using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Identity;

/// <summary>Why a platform-user operation was refused, or that it succeeded.</summary>
public enum PlatformUserChangeStatus
{
    Success,

    /// <summary>No such administrator in the caller's organization.</summary>
    NotFound,

    /// <summary>The e-mail address is already in use.</summary>
    EmailInUse,

    /// <summary>The requested role is not one of the built-in roles.</summary>
    UnknownRole,

    /// <summary>
    /// The caller tried to grant a role carrying permissions they do not hold
    /// themselves.
    /// </summary>
    RoleExceedsCallerAuthority,

    /// <summary>The caller aimed the operation at their own account.</summary>
    CannotTargetSelf,

    /// <summary>
    /// The operation would leave the platform with no usable Super Administrator.
    /// </summary>
    WouldRemoveLastSuperAdministrator,

    /// <summary>A built-in account created by seeding cannot be changed this way.</summary>
    SystemAccount,
}

/// <summary>An administrator as the console lists them.</summary>
public sealed record PlatformUserSummary(
    Guid Id,
    string Email,
    string DisplayName,
    string Status,
    string? RoleKey,
    string? RoleDisplayName,
    bool HasAllDeviceScope,
    bool MustChangePassword,
    bool IsSystemAccount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>
/// The outcome of creating an administrator or resetting one's password.
/// </summary>
/// <param name="GeneratedPassword">
/// The plaintext, present ONLY on success and returned to the caller exactly once.
/// It is never stored, never logged and never audited.
/// </param>
public sealed record PlatformUserCredentialResult(
    PlatformUserChangeStatus Status,
    Guid? UserId = null,
    string? GeneratedPassword = null);

/// <summary>
/// Creating and administering the platform's own administrator accounts.
/// </summary>
/// <remarks>
/// <para>
/// This type handles ONE secret: the initial password it generates for a new or
/// reset account. That value may travel to exactly one place — the HTTP response of
/// the request that caused it — and must never reach a log line, an audit record, a
/// problem-details body or a database column. Only its hash is stored. Every method
/// below that produces one says so at the point it is produced.
/// </para>
/// <para>
/// Authorization is the endpoint's job; this type assumes the caller already holds
/// <see cref="Permissions.Platform.UserManage"/>. What it does enforce are the
/// invariants that permission alone cannot express: that the platform never loses
/// its last usable Super Administrator, that an administrator cannot disarm their
/// own account, and that nobody can grant authority they do not themselves hold.
/// </para>
/// </remarks>
public sealed class PlatformUserService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<PlatformUserService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<PlatformUserService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Every administrator in the caller's organization.</summary>
    public async Task<IReadOnlyList<PlatformUserSummary>> ListAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        // Include, not just AsNoTracking: without it the Roles navigation comes back
        // empty and every administrator renders as having no access level at all.
        var users = await _dbContext.PlatformUsers
            .AsNoTracking()
            .Include(u => u.Roles)
            .Where(u => u.OrganizationId == organizationId)
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);

        var roleIds = users.SelectMany(u => u.Roles.Select(r => r.RoleId)).Distinct().ToArray();

        var roles = await _dbContext.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        return users.Select(user =>
        {
            // An account holds at most one role by construction (see AssignOnlyRole),
            // but the model permits several, so the first is taken rather than
            // asserted - a list page must render whatever is actually there.
            var role = user.Roles
                .Select(r => roles.TryGetValue(r.RoleId, out var found) ? found : null)
                .FirstOrDefault(r => r is not null);

            return new PlatformUserSummary(
                user.Id,
                user.Email,
                user.DisplayName,
                user.Status.ToString(),
                role?.Key,
                role?.DisplayName,
                user.HasAllDeviceScope,
                user.MustChangePassword,
                user.IsSystemAccount,
                user.CreatedAt,
                user.LastLoginAt);
        }).ToList();
    }

    /// <summary>
    /// Creates an administrator with a server-generated password they must replace.
    /// </summary>
    /// <remarks>
    /// The returned plaintext is the only copy that will ever exist outside the
    /// hash. The caller must hand it to the response and then forget it.
    /// </remarks>
    public async Task<PlatformUserCredentialResult> CreateAsync(
        Guid organizationId,
        Guid actorUserId,
        string actorDisplay,
        string email,
        string displayName,
        string roleKey,
        CancellationToken cancellationToken = default)
    {
        if (!SystemRoles.All.TryGetValue(roleKey, out var roleDefinition))
        {
            return new PlatformUserCredentialResult(PlatformUserChangeStatus.UnknownRole);
        }

        if (!await CallerMayGrantAsync(actorUserId, roleDefinition, cancellationToken))
        {
            return new PlatformUserCredentialResult(PlatformUserChangeStatus.RoleExceedsCallerAuthority);
        }

        var normalized = email.Trim().ToUpperInvariant();

        // Checked GLOBALLY, though the unique index is per (OrganizationId,
        // NormalizedEmail). Sign-in resolves an account by e-mail alone
        // (AdminAuthService.SignInAsync), so a second account sharing an address in
        // another organization would make BOTH unable to sign in. Do not narrow
        // this to the index without fixing that lookup first.
        var addressTaken = await _dbContext.PlatformUsers
            .AsNoTracking()
            .AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (addressTaken)
        {
            return new PlatformUserCredentialResult(PlatformUserChangeStatus.EmailInUse);
        }

        var role = await _dbContext.Roles
            .SingleOrDefaultAsync(r => r.Key == roleKey && r.OrganizationId == null, cancellationToken);

        if (role is null)
        {
            // The built-in roles are seeded on every deployment, so this means
            // seeding has not run rather than that the caller asked for nonsense.
            _logger.LogError(
                "Built-in role {RoleKey} is not seeded; an administrator cannot be created until it is.", roleKey);

            return new PlatformUserCredentialResult(PlatformUserChangeStatus.UnknownRole);
        }

        var now = _timeProvider.GetUtcNow();
        var user = new PlatformUser(organizationId, email, displayName);

        var password = GeneratedPassword.Create();
        user.SetPasswordHash(PasswordHasher.Hash(password), now);

        // Set AFTER the hash, never inside SetPasswordHash: that method also runs on
        // sign-in when a stored hash needs rehashing, and folding the flag into it
        // would let a plain sign-in with this very password clear the requirement.
        user.RequirePasswordChange();

        user.AssignRole(role.Id);

        // Deny-by-default everywhere else, but a Super Administrator with no device
        // scope is powerless over the estate while appearing omnipotent, which is the
        // more dangerous confusion. AdminBootstrapper grants the same for the same
        // reason when it creates the first one.
        if (string.Equals(roleKey, SystemRoles.SuperAdministrator, StringComparison.Ordinal))
        {
            user.GrantAllDeviceScope();
        }

        _dbContext.PlatformUsers.Add(user);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorUserId,
            actorDisplay,
            "platform.user.created",
            AuditResult.Success,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                .Requiring(Permissions.Platform.UserManage)
                // Metadata only. The generated password and even its hash stay out of
                // the trail: the trail is append-only by database trigger, so a secret
                // written here could never be removed.
                .WithStateChange(null, AuditStateRedactor.Redact(new Dictionary<string, object?>
                {
                    ["email"] = user.Email,
                    ["displayName"] = user.DisplayName,
                    ["role"] = roleDefinition.Key,
                    ["hasAllDeviceScope"] = user.HasAllDeviceScope,
                    ["mustChangePassword"] = true,
                })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Administrator {Email} created with role {Role} by {Actor}.", user.Email, roleKey, actorDisplay);

        return new PlatformUserCredentialResult(PlatformUserChangeStatus.Success, user.Id, password);
    }

    /// <summary>
    /// Replaces an administrator's password with a generated one they must change.
    /// </summary>
    public async Task<PlatformUserCredentialResult> ResetPasswordAsync(
        Guid organizationId,
        Guid actorUserId,
        string actorDisplay,
        Guid targetUserId,
        CancellationToken cancellationToken = default)
    {
        if (targetUserId == actorUserId)
        {
            // Self-service password change is a different route with a different
            // contract: it proves knowledge of the current password. Allowing an
            // administrator to reset their own here would bypass that proof.
            return new PlatformUserCredentialResult(PlatformUserChangeStatus.CannotTargetSelf);
        }

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == targetUserId && u.OrganizationId == organizationId, cancellationToken);

        if (user is null)
        {
            return new PlatformUserCredentialResult(PlatformUserChangeStatus.NotFound);
        }

        var now = _timeProvider.GetUtcNow();
        var password = GeneratedPassword.Create();

        user.SetPasswordHash(PasswordHasher.Hash(password), now);
        user.RequirePasswordChange();

        // SetPasswordHash clears the lockout counters but leaves Status alone, so a
        // Locked account would still be refused by session validation on every
        // request after a successful sign-in - a reset that appears to work and then
        // does not. Enabling here is what makes the reset actually restore access.
        if (user.Status == PlatformUserStatus.Locked)
        {
            user.Enable();
        }

        await RevokeSessionsAsync(user.Id, now, cancellationToken);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorUserId,
            actorDisplay,
            "platform.user.password_reset",
            AuditResult.Success,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                .Requiring(Permissions.Platform.UserManage)
                .WithStateChange(null, AuditStateRedactor.Redact(new Dictionary<string, object?>
                {
                    ["email"] = user.Email,
                    ["mustChangePassword"] = true,
                    ["sessionsRevoked"] = true,
                })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Password reset for administrator {Email} by {Actor}.", user.Email, actorDisplay);

        return new PlatformUserCredentialResult(PlatformUserChangeStatus.Success, user.Id, password);
    }

    /// <summary>
    /// Clears another administrator's second factor, returning them to enrolment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer to "I lost my phone and I am out of recovery codes". Without it
    /// that account is unreachable forever, because enrolment is mandatory and
    /// there is nothing left to authenticate the second factor with.
    /// </para>
    /// <para>
    /// <b>Self-reset is refused.</b> An administrator who could clear their own
    /// second factor from a live session would reduce multi-factor to
    /// single-factor: anyone who borrowed an unlocked browser could drop the
    /// requirement and re-enrol their own authenticator. The password-reset path
    /// refuses self-targeting for the same reason.
    /// </para>
    /// <para>
    /// <see cref="PlatformUser.ResetMfa"/> rotates the security stamp, so every
    /// session the account holds dies at once. If the reset is happening because
    /// the account may be compromised, leaving its sessions alive would defeat
    /// the point - and recovery codes are replaced rather than left usable.
    /// </para>
    /// </remarks>
    public async Task<PlatformUserChangeStatus> ResetMfaAsync(
        Guid organizationId,
        Guid actorUserId,
        string actorDisplay,
        Guid targetUserId,
        CancellationToken cancellationToken = default)
    {
        if (targetUserId == actorUserId)
        {
            return PlatformUserChangeStatus.CannotTargetSelf;
        }

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == targetUserId && u.OrganizationId == organizationId, cancellationToken);

        if (user is null)
        {
            return PlatformUserChangeStatus.NotFound;
        }

        var now = _timeProvider.GetUtcNow();
        var hadMfa = user.HasConfirmedMfa;

        user.ResetMfa();

        // Unused codes would otherwise survive the reset and remain valid against
        // whatever the account enrols next, which is a bypass nobody would expect.
        var codes = await _dbContext.MfaRecoveryCodes
            .Where(c => c.UserId == user.Id)
            .ToListAsync(cancellationToken);
        _dbContext.MfaRecoveryCodes.RemoveRange(codes);

        // Any half-finished sign-in for this account must die with the reset.
        var challenges = await _dbContext.AdminMfaChallenges
            .Where(c => c.UserId == user.Id && c.ConsumedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var challenge in challenges)
        {
            challenge.Consume(now);
        }

        await RevokeSessionsAsync(user.Id, now, cancellationToken);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorUserId,
            actorDisplay,
            "platform.user.mfa_reset",
            AuditResult.Success,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                .Requiring(Permissions.Platform.UserManage)
                .WithStateChange(
                    AuditStateRedactor.Redact(new Dictionary<string, object?> { ["mfaEnrolled"] = hadMfa }),
                    AuditStateRedactor.Redact(new Dictionary<string, object?>
                    {
                        ["mfaEnrolled"] = false,
                        ["recoveryCodesCleared"] = codes.Count,
                        ["sessionsRevoked"] = true,
                    })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Multi-factor authentication reset for administrator {Email} by {Actor}.", user.Email, actorDisplay);

        return PlatformUserChangeStatus.Success;
    }

    /// <summary>Disables an administrator, ending every session they hold.</summary>
    public Task<PlatformUserChangeStatus> DisableAsync(
        Guid organizationId, Guid actorUserId, string actorDisplay, Guid targetUserId,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(organizationId, actorUserId, actorDisplay, targetUserId, enabled: false, cancellationToken);

    /// <summary>Returns a disabled administrator to service.</summary>
    public Task<PlatformUserChangeStatus> EnableAsync(
        Guid organizationId, Guid actorUserId, string actorDisplay, Guid targetUserId,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(organizationId, actorUserId, actorDisplay, targetUserId, enabled: true, cancellationToken);

    private async Task<PlatformUserChangeStatus> SetEnabledAsync(
        Guid organizationId,
        Guid actorUserId,
        string actorDisplay,
        Guid targetUserId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (targetUserId == actorUserId)
        {
            // Disabling yourself rotates your own security stamp and kills the
            // session executing the request. It is never what someone meant to do.
            return PlatformUserChangeStatus.CannotTargetSelf;
        }

        // One transaction around the guard and the mutation. Two concurrent disables
        // could otherwise each see two Super Administrators, each proceed, and leave
        // the platform with none - which is exactly what the guard exists to prevent
        // and is only ever discovered as an outage.
        //
        // Run through the context's retrying execution strategy rather than calling
        // BeginTransaction directly: the strategy refuses a user-initiated
        // transaction outright, because silently retrying half a transaction would
        // be worse than the transient failure being retried. Same shape as
        // DeviceGroupService.InTransactionAsync.
        var strategy = _dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(() => SetEnabledCoreAsync(
            organizationId, actorUserId, actorDisplay, targetUserId, enabled, cancellationToken));
    }

    private async Task<PlatformUserChangeStatus> SetEnabledCoreAsync(
        Guid organizationId,
        Guid actorUserId,
        string actorDisplay,
        Guid targetUserId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Include is load-bearing, not tidiness: IsLastUsableSuperAdministratorAsync
        // reads user.Roles, and without this that collection is empty, so the guard
        // would decide nobody is a Super Administrator and never refuse anything.
        var user = await _dbContext.PlatformUsers
            .Include(u => u.Roles)
            .SingleOrDefaultAsync(u => u.Id == targetUserId && u.OrganizationId == organizationId, cancellationToken);

        if (user is null)
        {
            return PlatformUserChangeStatus.NotFound;
        }

        if (!enabled && user.IsSystemAccount)
        {
            return PlatformUserChangeStatus.SystemAccount;
        }

        if (!enabled && await IsLastUsableSuperAdministratorAsync(user, cancellationToken))
        {
            _logger.LogWarning(
                "Refused to disable {Email}: it is the last usable Super Administrator.", user.Email);

            return PlatformUserChangeStatus.WouldRemoveLastSuperAdministrator;
        }

        var previousStatus = user.Status.ToString();
        var now = _timeProvider.GetUtcNow();

        if (enabled)
        {
            user.Enable();
        }
        else
        {
            user.Disable();
            await RevokeSessionsAsync(user.Id, now, cancellationToken);
        }

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorUserId,
            actorDisplay,
            enabled ? "platform.user.enabled" : "platform.user.disabled",
            AuditResult.Success,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                .Requiring(Permissions.Platform.UserManage)
                .WithStateChange(
                    AuditStateRedactor.Redact(new Dictionary<string, object?> { ["status"] = previousStatus }),
                    AuditStateRedactor.Redact(new Dictionary<string, object?>
                    {
                        ["status"] = user.Status.ToString(),
                        ["sessionsRevoked"] = !enabled,
                    })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Administrator {Email} {Action} by {Actor}.", user.Email, enabled ? "enabled" : "disabled", actorDisplay);

        return PlatformUserChangeStatus.Success;
    }

    /// <summary>
    /// Whether disabling this account would leave no usable Super Administrator.
    /// </summary>
    /// <remarks>
    /// "Usable" means not disabled. A Disabled last Super Administrator cannot be
    /// rescued by the bootstrap command: AdminBootstrapper refuses whenever ANY
    /// account holds the role, without asking whether that account can actually sign
    /// in. Recovery would be direct database surgery, so the refusal lives here.
    /// </remarks>
    private async Task<bool> IsLastUsableSuperAdministratorAsync(
        PlatformUser user, CancellationToken cancellationToken)
    {
        var superAdministratorRoleId = await _dbContext.Roles
            .Where(r => r.Key == SystemRoles.SuperAdministrator && r.OrganizationId == null)
            .Select(r => (Guid?)r.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (superAdministratorRoleId is null || !user.Roles.Any(r => r.RoleId == superAdministratorRoleId))
        {
            return false;
        }

        var otherUsableHolders = await _dbContext.PlatformUsers
            .CountAsync(
                u => u.Id != user.Id
                     && u.Status != PlatformUserStatus.Disabled
                     && u.Roles.Any(r => r.RoleId == superAdministratorRoleId),
                cancellationToken);

        return otherUsableHolders == 0;
    }

    /// <summary>
    /// Whether the caller may grant this role, by comparing PERMISSION SETS.
    /// </summary>
    /// <remarks>
    /// Never by role name. No authorization decision in this codebase inspects a
    /// role, and one here would silently become wrong the moment SystemRoles.cs
    /// changed. Comparing the sets gives the intended rule — only a Super
    /// Administrator can mint another — as a consequence rather than a special case.
    /// </remarks>
    private async Task<bool> CallerMayGrantAsync(
        Guid actorUserId, SystemRoleDefinition roleDefinition, CancellationToken cancellationToken)
    {
        var callerPermissions = await _dbContext.PlatformUsers
            .AsNoTracking()
            .Where(u => u.Id == actorUserId)
            .SelectMany(u => u.Roles)
            .Join(_dbContext.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r)
            .SelectMany(r => r.Permissions)
            .Join(_dbContext.Permissions, rp => rp.PermissionId, p => p.Id, (_, p) => p.Key)
            .Distinct()
            .ToListAsync(cancellationToken);

        var held = callerPermissions.ToHashSet(StringComparer.Ordinal);

        return roleDefinition.PermissionKeys.All(held.Contains);
    }

    /// <summary>
    /// Marks every live session revoked.
    /// </summary>
    /// <remarks>
    /// The security stamp rotation already makes them unusable; revoking them as well
    /// makes the reason visible to anyone auditing the session table later, rather
    /// than leaving rows that merely stopped working. This mirrors what the
    /// change-password path does.
    /// </remarks>
    private async Task RevokeSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await _dbContext.AdminSessions
            .Where(s => s.PlatformUserId == userId && s.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(now);
        }
    }
}
