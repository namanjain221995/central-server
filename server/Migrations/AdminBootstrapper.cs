using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Migrations;

/// <summary>
/// Creates the first Super Administrator account.
/// </summary>
/// <remarks>
/// <para>
/// An explicit operator step, not part of seeding: a bootstrap account requires a
/// credential, and credentials never live in source, images or default seed data.
/// Invoked as <c>dotnet run --project server/Migrations -- bootstrap-admin</c>
/// with <c>ENDPOINTPLATFORM_Bootstrap__AdminEmail</c> and
/// <c>ENDPOINTPLATFORM_Bootstrap__AdminPassword</c> in the environment (the
/// password is never a command-line argument — process arguments are visible to
/// every user on the machine).
/// </para>
/// <para>
/// Refuses to run when any Super Administrator already exists: this is a
/// first-run tool, not an account-management path. Later administrators are
/// created through the platform, audited, by someone holding
/// <c>platform.user.manage</c>.
/// </para>
/// </remarks>
public sealed class AdminBootstrapper(EndpointPlatformDbContext dbContext, ILogger<AdminBootstrapper> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ILogger<AdminBootstrapper> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<int> RunAsync(string? email, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            _logger.LogError(
                "Set ENDPOINTPLATFORM_Bootstrap__AdminEmail to the administrator's email address.");
            return 1;
        }

        // Kept as its own check rather than folded into PasswordPolicy below.
        // PasswordPolicy does reject a null, but the compiler cannot infer that
        // from a string? return value, and the operator needs "you did not set
        // the variable" phrased differently from "what you set is too weak".
        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError(
                "Set ENDPOINTPLATFORM_Bootstrap__AdminPassword to the administrator's password. "
                + "(The value is read from the environment only; never pass it as a command-line argument.)");
            return 1;
        }

        // Delegated to PasswordPolicy rather than repeating a length check here.
        // The two had drifted apart in kind if not in number: this path enforced
        // only a floor, so the very first account on the platform - a Super
        // Administrator - was the one account allowed a guessable password.
        if (PasswordPolicy.Validate(password, PasswordContext.For(email, null)) is { } policyFailure)
        {
            _logger.LogError(
                "ENDPOINTPLATFORM_Bootstrap__AdminPassword is not acceptable: {Reason}", policyFailure);
            return 1;
        }

        var superAdminRole = await _dbContext.Roles
            .SingleOrDefaultAsync(r => r.IsBuiltIn && r.Key == SystemRoles.SuperAdministrator, cancellationToken);

        if (superAdminRole is null)
        {
            _logger.LogError("Built-in roles are not seeded. Run the migration job first.");
            return 1;
        }

        var superAdminExists = await _dbContext.PlatformUserRoles
            .AnyAsync(ur => ur.RoleId == superAdminRole.Id, cancellationToken);

        if (superAdminExists)
        {
            _logger.LogError(
                "A Super Administrator already exists; bootstrap refuses to run. Create further "
                + "administrators through the platform, where the action is audited.");
            return 1;
        }

        var organization = await _dbContext.Organizations
            .OrderBy(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (organization is null)
        {
            _logger.LogError("No organization exists. Run the migration job first.");
            return 1;
        }

        var user = new PlatformUser(organization.Id, email.Trim(), "Bootstrap Administrator");
        user.MarkAsSystemAccount();
        user.SetPasswordHash(PasswordHasher.Hash(password), DateTimeOffset.UtcNow);
        user.AssignRole(superAdminRole.Id);

        // Device scope is deny-by-default, so an administrator with no scope reaches no
        // device at all. That is right for accounts created later through the platform,
        // where an operator chooses their scope - but the bootstrap account is the one
        // that has to be able to grant everyone else's, so it starts organization-wide.
        user.GrantAllDeviceScope();

        _dbContext.PlatformUsers.Add(user);

        _dbContext.AuditLogEntries.Add(Domain.Auditing.AuditLogEntry.For(
                organization.Id,
                DateTimeOffset.UtcNow,
                Domain.Auditing.AuditActorType.System,
                actorId: null,
                actorDisplay: "bootstrap-admin",
                action: "platform.user.bootstrap",
                Domain.Auditing.AuditResult.Success)
            .OnTarget("platform_user", user.Id.ToString(), user.Email)
            .Build());

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Super Administrator {Email} created (user id {UserId}). Sign in through the dashboard "
            + "and change the password if it was generated by a deployment pipeline.",
            user.Email,
            user.Id);

        return 0;
    }
}
