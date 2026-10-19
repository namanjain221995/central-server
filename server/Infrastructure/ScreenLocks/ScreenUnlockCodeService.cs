using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Security;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.ScreenLocks;

/// <summary>
/// Sets and reads the organization's IT unlock code -- the recovery code that
/// lifts a persistent screen lock on the device itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The plaintext is never stored, returned or logged.</b> Setting a code turns it
/// into a PBKDF2-SHA256 salt+hash and keeps only that; reading the status says
/// whether one exists and when it changed, never what it is. The verifier is the
/// one thing that leaves this service -- copied into an <c>EngageScreenLock</c>
/// payload -- so the agent can check a code typed on a locked machine offline
/// without the code ever crossing the wire.
/// </para>
/// <para>
/// PBKDF2-HMAC-SHA256, the same primitive as <c>PasswordHasher</c>, but its own
/// type because this verifier travels in a task payload to the agent, which does
/// the comparison. The iteration count is carried in the stored row and in the
/// payload so it can be raised later without stranding codes set under the old one.
/// </para>
/// </remarks>
public sealed class ScreenUnlockCodeService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter)
{
    /// <summary>The shortest code accepted. A recovery code is typed by a person under stress; shorter is a footgun.</summary>
    public const int MinCodeLength = 6;

    /// <summary>The verifier construction. Carried explicitly so it can be raised without stranding old codes.</summary>
    public const string Algorithm = "pbkdf2-sha256";

    // OWASP's floor for PBKDF2-SHA256 is 210k; the task requires at least 100k. The
    // agent derives this once per unlock attempt on a desktop CPU, so it is cheap
    // there and costly to anyone brute-forcing a captured verifier.
    private const int Iterations = 210_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;

    /// <summary>
    /// Sets (or replaces) the organization's unlock code. Returns false, writing
    /// nothing, when the code is shorter than <see cref="MinCodeLength"/>.
    /// </summary>
    public async Task<bool> SetAsync(
        Guid organizationId,
        string code,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        // Not trimmed: a recovery code is matched byte for byte on the device, and
        // trimming here would silently accept a code the agent would then reject.
        if (code is null || code.Length < MinCodeLength)
        {
            return false;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(code), salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

        var saltBase64 = Convert.ToBase64String(salt);
        var hashBase64 = Convert.ToBase64String(hash);

        var setting = await _dbContext.OrganizationSecuritySettings
            .SingleOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);

        if (setting is null)
        {
            setting = new OrganizationSecuritySetting(organizationId, Algorithm, Iterations, saltBase64, hashBase64);
            _dbContext.OrganizationSecuritySettings.Add(setting);
        }
        else
        {
            setting.SetScreenUnlockVerifier(Algorithm, Iterations, saltBase64, hashBase64);
        }

        // The audit records that a code was set and by whom -- never the code, and
        // never the verifier: nothing that helps anyone recover the plaintext.
        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "security.screen_unlock_code.set", AuditResult.Success,
            a => a.OnTarget("organization_security_setting", organizationId.ToString(), "screen unlock code")
                  .Requiring(Domain.Authorization.Permissions.Device.ScreenLock));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Whether a code is set, and when it last changed. Never the code itself.</summary>
    public async Task<ScreenUnlockCodeStatus> GetStatusAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        var updatedAt = await _dbContext.OrganizationSecuritySettings
            .AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => (DateTimeOffset?)s.UpdatedAt)
            .SingleOrDefaultAsync(cancellationToken);

        return new ScreenUnlockCodeStatus(updatedAt is not null, updatedAt);
    }

    /// <summary>
    /// The stored verifier to put in an engage payload, or null when no code is set
    /// -- which is the signal to refuse to lock a device with no recovery path.
    /// </summary>
    public async Task<TaskPayloads.ScreenLockVerifier?> GetVerifierAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.OrganizationSecuritySettings
            .AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => new TaskPayloads.ScreenLockVerifier(
                s.ScreenUnlockAlgo, s.ScreenUnlockIterations, s.ScreenUnlockSalt, s.ScreenUnlockHash))
            .SingleOrDefaultAsync(cancellationToken);
    }
}

/// <summary>Whether an organization has an unlock code set, and when it last changed. Never the code.</summary>
public sealed record ScreenUnlockCodeStatus(bool IsSet, DateTimeOffset? UpdatedAt);
