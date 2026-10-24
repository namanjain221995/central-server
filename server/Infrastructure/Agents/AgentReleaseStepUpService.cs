using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Agents;

public enum ReleaseActionGateOutcome
{
    Allowed = 0,
    RateLimited = 1,
    StepUpFailed = 2,
}

/// <param name="Error">Safe to show a caller. Never names which check inside step-up failed.</param>
public sealed record ReleaseActionGateResult(
    ReleaseActionGateOutcome Outcome, string? Error, int RetryAfterSeconds)
{
    public static readonly ReleaseActionGateResult Allowed =
        new(ReleaseActionGateOutcome.Allowed, null, 0);
}

/// <summary>
/// The step-up gate in front of the three dangerous agent-release actions:
/// publish, artifact replacement, and queueing a device self-update.
/// </summary>
/// <remarks>
/// <para>
/// Publishing an agent release -- or pointing a device at one -- is SYSTEM-level
/// code headed for the fleet, gated until now by the <c>software.deploy</c>
/// permission alone. The motivation on record is the 1.16.0 incident: a broken
/// MSI built against localhost was published and wiped the agent off every
/// machine it updated. Re-typing the password plus a recorded justification makes
/// that class of accident a deliberate act, and a stolen <c>software.deploy</c>
/// session doing the same thing maliciously is the High-adjacent chain the
/// denial audits exist to pin.
/// </para>
/// <para>
/// Mirrors <see cref="BitLocker.RecoveryEscrowService"/>'s reveal gate exactly,
/// in the same order: the rate limit is consumed FIRST, so this endpoint cannot
/// be used to brute-force an administrator's password without bound; then the
/// caller's own password is re-verified through the sign-in verifier, so a wrong
/// password counts towards the same account lockout. Every refusal is audited
/// immediately, with its reason and never the password.
/// </para>
/// </remarks>
public sealed class AgentReleaseStepUpService(
    EndpointPlatformDbContext dbContext,
    ReleaseActionRateLimiter rateLimiter,
    AdminAuthService authService,
    AuditWriter auditWriter,
    ILogger<AgentReleaseStepUpService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ReleaseActionRateLimiter _rateLimiter = rateLimiter
        ?? throw new ArgumentNullException(nameof(rateLimiter));

    private readonly AdminAuthService _authService = authService
        ?? throw new ArgumentNullException(nameof(authService));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly ILogger<AgentReleaseStepUpService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Rate-limits and step-up-verifies the caller before a gated release action.
    /// </summary>
    /// <param name="denialAction">
    /// The audit action a refusal is recorded under:
    /// <c>agent_release.publish_denied</c>, <c>agent_release.artifact_replace_denied</c>
    /// or <c>agent_release.update_agent_denied</c>.
    /// </param>
    /// <param name="releaseId">The release the caller named, when the route names one.</param>
    /// <param name="targetDisplay">What the audit row shows for the target.</param>
    /// <param name="device">The target device, for update-agent; null for the release-only routes.</param>
    public async Task<ReleaseActionGateResult> AuthorizeAsync(
        string denialAction,
        Guid? releaseId,
        string targetDisplay,
        Guid actorId,
        string actorDisplay,
        Guid actorOrganizationId,
        string currentPassword,
        Domain.Devices.Device? device = null,
        CancellationToken cancellationToken = default)
    {
        // Rate limit first, and counted before judged: an attempt that will be
        // refused still consumes budget, and the step-up below can never be
        // driven faster than the window allows.
        var limit = await _rateLimiter.TryConsumeAsync(actorId, cancellationToken);
        if (!limit.Allowed)
        {
            await AuditDeniedAsync(
                denialAction, releaseId, targetDisplay, actorId, actorDisplay, actorOrganizationId,
                device,
                new Dictionary<string, object?>
                {
                    ["releaseId"] = releaseId,
                    ["reason"] = "rate limited",
                    ["scope"] = "user",
                },
                failureReason: "Rate limited (user).",
                cancellationToken);

            return new ReleaseActionGateResult(
                ReleaseActionGateOutcome.RateLimited,
                "Too many agent-release actions. Try again later.",
                limit.RetryAfterSeconds);
        }

        // Step-up: holding the permission is not enough, the caller must prove they
        // are still the person who signed in. Reuses the sign-in verifier, so a
        // wrong password counts towards the same lockout.
        if (!await _authService.VerifyCurrentPasswordAsync(actorId, currentPassword, cancellationToken))
        {
            await AuditDeniedAsync(
                denialAction, releaseId, targetDisplay, actorId, actorDisplay, actorOrganizationId,
                device,
                new Dictionary<string, object?>
                {
                    ["releaseId"] = releaseId,
                    ["reason"] = "step-up authentication failed",
                },
                failureReason: "Step-up authentication failed.",
                cancellationToken);

            _logger.LogWarning(
                "Agent-release action refused for {UserId}: step-up authentication failed.", actorId);

            return new ReleaseActionGateResult(
                ReleaseActionGateOutcome.StepUpFailed, "That password is not correct.", 0);
        }

        return ReleaseActionGateResult.Allowed;
    }

    /// <summary>
    /// Stages and saves a denial immediately: the request is about to be refused,
    /// so nothing downstream will save it later.
    /// </summary>
    private async Task AuditDeniedAsync(
        string action,
        Guid? releaseId,
        string targetDisplay,
        Guid actorId,
        string actorDisplay,
        Guid actorOrganizationId,
        Domain.Devices.Device? device,
        IDictionary<string, object?> next,
        string failureReason,
        CancellationToken cancellationToken)
    {
        _auditWriter.Stage(
            actorOrganizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action, AuditResult.Failure,
            audit =>
            {
                if (device is not null)
                {
                    audit.OnDevice(device.Id, device.Hostname);
                }

                audit.OnTarget("agent_release", releaseId?.ToString(), targetDisplay)
                    .Requiring(Permissions.Software.Deploy)
                    .WithStateChange(null, AuditStateRedactor.Redact(next))
                    .WithFailureReason(failureReason);
            });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
