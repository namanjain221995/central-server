using System.Security.Cryptography;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.RemoteSupport;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.BitLocker;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.RemoteSupport;

public enum RemoteSupportOutcome
{
    Success = 0,
    DeviceNotFound = 1,
    NotEnrolled = 2,

    /// <summary>Enrolled, but the device has not reported a RustDesk ID yet, so there is nothing to dial.</summary>
    NotReady = 3,

    /// <summary>The ephemeral secret store was unreachable, so the password could not be handed over safely.</summary>
    SecretUnavailable = 4,

    /// <summary>The device's agent is too old to carry the remote-support configure executor.</summary>
    NotEligible = 5,

    RateLimited = 6,
    StepUpFailed = 7,

    /// <summary>The sealed password could not be unsealed (likely sealed under a different escrow key).</summary>
    UnsealFailed = 8,
}

public sealed record RemoteSupportConfigureResult(RemoteSupportOutcome Outcome, Guid? TaskId, string? Error);

/// <param name="Password">
/// The unattended password, present only on a successful reveal and only in memory
/// on the way to the HTTP response. Never persisted, logged or audited.
/// </param>
public sealed record RemoteSupportRevealResult(
    RemoteSupportOutcome Outcome, string? RustDeskId, string? Password, string? Error, int RetryAfterSeconds);

/// <summary>One device as the remote-support list shows it. No secret.</summary>
public sealed record RemoteSupportDeviceRow(
    Guid DeviceId, string Hostname, string? DisplayName, bool IsOnline,
    DateTimeOffset? LastSeenAt, string? RustDeskId, string State);

/// <summary>
/// Enrols a device for IT remote support (seals a platform-managed RustDesk
/// unattended password and queues the configure task) and reveals that password to
/// an authorized administrator so they can start a session.
/// </summary>
/// <remarks>
/// The sealed password is handled exactly like a BitLocker recovery password
/// (<see cref="RecoveryEscrowService"/>): sealed with the symmetric escrow key,
/// which lives only in the Admin API, and revealed only after a rate-limit check and
/// a step-up re-authentication, with every reveal -- and every refusal -- audited
/// through <see cref="AuditStateRedactor"/>. The plaintext never touches a log, an
/// exception, an audit row or the persisted task payload: it reaches the agent as a
/// one-time ephemeral-secret reference the agent redeems out of band.
/// </remarks>
public sealed class RemoteSupportService(
    EndpointPlatformDbContext dbContext,
    IRecoveryKeyProtector protector,
    EphemeralSecretStore secretStore,
    DeviceTaskService taskService,
    RevealRateLimiter rateLimiter,
    AdminAuthService authService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions,
    ILogger<RemoteSupportService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly IRecoveryKeyProtector _protector = protector;
    private readonly EphemeralSecretStore _secretStore = secretStore;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly RevealRateLimiter _rateLimiter = rateLimiter;
    private readonly AdminAuthService _authService = authService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);
    private readonly ILogger<RemoteSupportService> _logger = logger;

    // --------------------------------------------------------------- configure

    /// <summary>
    /// Enrols a device: generates and seals a fresh unattended password, queues the
    /// configure task that sets it on the device's RustDesk, and records the
    /// enrolment. One transaction under the execution strategy so a mid-way failure
    /// never leaves a configure task queued with no sealed password behind it.
    /// </summary>
    public Task<RemoteSupportConfigureResult> ConfigureAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() =>
            ConfigureInTransactionAsync(organizationId, deviceId, actorId, actorDisplay, cancellationToken));
    }

    private async Task<RemoteSupportConfigureResult> ConfigureInTransactionAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay, CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var device = await _dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == organizationId && d.Status == DeviceStatus.Active,
                cancellationToken);

        if (device is null)
        {
            return new RemoteSupportConfigureResult(RemoteSupportOutcome.DeviceNotFound, null, "No such device.");
        }

        // Platform-managed: the admin never chooses it. Strong (~116 bits), so with
        // RustDesk reachable only behind this password it is the gate that public
        // relay servers do not otherwise provide.
        var password = GeneratedPassword.Create();
        var sealedPassword = _protector.Protect(password);
        var keyVersion = _protector.CurrentKeyVersion;

        // The plaintext goes to the ephemeral store; only the one-time reference is
        // persisted in the task. If the store is down we refuse rather than fall back
        // to carrying the password in the task payload.
        var secretRef = await _secretStore.StoreAsync(deviceId, password, cancellationToken);
        if (secretRef is null)
        {
            return new RemoteSupportConfigureResult(RemoteSupportOutcome.SecretUnavailable, null,
                "The secure secret store is unavailable, so the password could not be handed over safely. "
                + "No change was made; try again once it recovers.");
        }

        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.ConfigureRemoteSupport,
            new TaskPayloads.ConfigureRemoteSupport(secretRef),
            actorId, actorDisplay, cancellationToken);

        if (task is null)
        {
            // The device is in the org and active, so the only refusal is the
            // catalogue's agent-version gate.
            return new RemoteSupportConfigureResult(RemoteSupportOutcome.NotEligible, null,
                "This device's agent is too old for remote support. Update the agent to 1.20.0 or later.");
        }

        var existing = await _dbContext.RemoteSupportEnrollments
            .SingleOrDefaultAsync(
                e => e.DeviceId == deviceId && e.OrganizationId == organizationId, cancellationToken);

        var reconfigured = existing is not null;
        if (existing is not null)
        {
            existing.Reseal(sealedPassword, keyVersion, actorId, actorDisplay, _timeProvider.GetUtcNow());
        }
        else
        {
            _dbContext.RemoteSupportEnrollments.Add(new RemoteSupportEnrollment(
                organizationId, deviceId, sealedPassword, keyVersion, actorId, actorDisplay,
                _timeProvider.GetUtcNow()));
        }

        StageAudit(device, actorId, actorDisplay, "remotesupport.configured", AuditResult.Success,
            next: new Dictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["reconfigured"] = reconfigured,
                ["taskId"] = task.Id,
            });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Remote support configured for device {DeviceId} by {Actor} (reconfigured: {Reconfigured}, task {TaskId}).",
            deviceId, actorDisplay, reconfigured, task.Id);

        return new RemoteSupportConfigureResult(RemoteSupportOutcome.Success, task.Id, null);
    }

    // ------------------------------------------------------------------ reveal

    /// <summary>
    /// Reveals a device's unattended password so an administrator can start a
    /// session, after a rate-limit check and step-up re-authentication. The RustDesk
    /// ID must have been reported, or there is nothing to dial.
    /// </summary>
    public async Task<RemoteSupportRevealResult> RevealAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        string currentPassword, string justification, CancellationToken cancellationToken = default)
    {
        var enrollment = await _dbContext.RemoteSupportEnrollments
            .SingleOrDefaultAsync(
                e => e.DeviceId == deviceId && e.OrganizationId == organizationId, cancellationToken);

        var device = await _dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (enrollment is null || device is null)
        {
            return new RemoteSupportRevealResult(RemoteSupportOutcome.NotEnrolled, null, null,
                "This device is not enrolled for remote support.", 0);
        }

        if (string.IsNullOrEmpty(device.RemoteSupportId))
        {
            return new RemoteSupportRevealResult(RemoteSupportOutcome.NotReady, null, null,
                "RustDesk has not registered on this device yet. Try again shortly.", 0);
        }

        var limit = await _rateLimiter.TryConsumeAsync(actorId, deviceId, cancellationToken);
        if (!limit.Allowed)
        {
            StageAudit(device, actorId, actorDisplay, "remotesupport.reveal_denied", AuditResult.Failure,
                next: new Dictionary<string, object?>
                {
                    ["deviceId"] = deviceId,
                    ["reason"] = "rate limited",
                    ["scope"] = limit.Scope,
                },
                failureReason: $"Rate limited ({limit.Scope}).");
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new RemoteSupportRevealResult(RemoteSupportOutcome.RateLimited, null, null,
                "Too many remote-support reveals. Try again later.", limit.RetryAfterSeconds);
        }

        // Step-up: holding the permission is not enough; prove it is still the person
        // who signed in. Reuses the sign-in verifier, so a wrong password counts
        // towards the same lockout.
        if (!await _authService.VerifyCurrentPasswordAsync(actorId, currentPassword, cancellationToken))
        {
            StageAudit(device, actorId, actorDisplay, "remotesupport.reveal_denied", AuditResult.Failure,
                next: new Dictionary<string, object?>
                {
                    ["deviceId"] = deviceId,
                    ["reason"] = "step-up authentication failed",
                },
                failureReason: "Step-up authentication failed.");
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("Remote-support reveal refused for {UserId}: step-up failed.", actorId);
            return new RemoteSupportRevealResult(RemoteSupportOutcome.StepUpFailed, null, null,
                "That password is not correct.", 0);
        }

        string password;
        try
        {
            password = _protector.Unprotect(enrollment.SealedPassword);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex,
                "Remote-support password for device {DeviceId} could not be unsealed (key version {Version}).",
                deviceId, enrollment.KeyVersion);
            return new RemoteSupportRevealResult(RemoteSupportOutcome.UnsealFailed, null, null,
                "The stored password could not be unsealed. It may have been sealed with a different escrow key.", 0);
        }

        enrollment.RecordReveal(actorId, _timeProvider.GetUtcNow());

        StageAudit(device, actorId, actorDisplay, "remotesupport.revealed", AuditResult.Success,
            next: new Dictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["rustDeskId"] = device.RemoteSupportId,
                ["justification"] = justification,
                ["revealedCount"] = enrollment.RevealedCount,
            });
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Remote-support password for device {DeviceId} revealed by {Actor} (reveal #{Count}).",
            deviceId, actorDisplay, enrollment.RevealedCount);

        return new RemoteSupportRevealResult(RemoteSupportOutcome.Success, device.RemoteSupportId, password, null, 0);
    }

    // -------------------------------------------------------------------- list

    /// <summary>
    /// Every active device in the caller's scope, with its remote-support state and
    /// RustDesk ID. No secret.
    /// </summary>
    public async Task<IReadOnlyList<RemoteSupportDeviceRow>> ListDevicesAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds, CancellationToken cancellationToken = default)
    {
        var enrolledIds = (await _dbContext.RemoteSupportEnrollments
                .AsNoTracking()
                .Where(e => e.OrganizationId == organizationId)
                .Select(e => e.DeviceId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var devices = await _dbContext.Devices
            .AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Status == DeviceStatus.Active)
            .OrderBy(d => d.Hostname)
            .Select(d => new { d.Id, d.Hostname, d.DisplayName, d.LastSeenAt, d.RemoteSupportId })
            .ToListAsync(cancellationToken);

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            devices = devices.Where(d => visible.Contains(d.Id)).ToList();
        }

        var now = _timeProvider.GetUtcNow();
        return devices.Select(d =>
        {
            var enrolled = enrolledIds.Contains(d.Id);
            var hasId = !string.IsNullOrEmpty(d.RemoteSupportId);
            var state = enrolled
                ? (hasId ? "Ready" : "Enrolling")
                : (hasId ? "NotEnrolled" : "NotInstalled");
            var online = d.LastSeenAt is { } lastSeen && now - lastSeen <= _staleAfter;
            return new RemoteSupportDeviceRow(
                d.Id, d.Hostname, d.DisplayName, online, d.LastSeenAt, d.RemoteSupportId, state);
        }).ToList();
    }

    // ----------------------------------------------------------------- audit

    private void StageAudit(
        Device device, Guid actorId, string actorDisplay, string action, AuditResult result,
        IDictionary<string, object?> next, string? failureReason = null) =>
        _auditWriter.Stage(
            device.OrganizationId, AuditActorType.PlatformUser, actorId, actorDisplay, action, result,
            audit =>
            {
                audit.OnDevice(device.Id, device.Hostname);
                audit.OnTarget("remote_support_enrollment", device.Id.ToString(), device.Hostname)
                    .Requiring(Permissions.RemoteSupport.Connect)
                    .WithStateChange(null, AuditStateRedactor.Redact(next));
                if (failureReason is not null)
                {
                    audit.WithFailureReason(failureReason);
                }
            });
}
