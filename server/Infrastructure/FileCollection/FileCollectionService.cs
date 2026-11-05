using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.FileCollection;

/// <summary>What starting a file collection did.</summary>
public enum FileCollectionStartStatus
{
    Started,
    BadRequest,
    StepUpFailed,
    NotFound,
    AgentUnavailable,
}

/// <summary>The outcome of <see cref="FileCollectionService.StartAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="TransferId">The transfer to stream, set only when <see cref="FileCollectionStartStatus.Started"/>.</param>
/// <param name="Error">A human-readable reason when it did not start.</param>
public sealed record FileCollectionStartResult(FileCollectionStartStatus Status, string? TransferId, string? Error);

/// <summary>
/// Starts a file-collection transfer: re-confirms the operator with a password
/// step-up, registers the relay, queues the device task, and records the download in
/// the audit log. The archive itself never touches this service — it flows,
/// encrypted, through <see cref="FileCollectionRelay"/>.
/// </summary>
/// <remarks>
/// The audit entry names the operator, the device, the Windows user and the chosen
/// folders, and nothing else: individual file names are never sent to the server, so
/// they can never reach the audit trail.
/// </remarks>
public sealed class FileCollectionService(
    AdminAuthService adminAuth,
    DeviceScopeAuthorizer scope,
    DeviceTaskService taskService,
    FileCollectionRelay relay,
    AuditWriter auditWriter,
    EndpointPlatformDbContext dbContext,
    ILogger<FileCollectionService> logger)
{
    /// <summary>Hard ceiling on one collection's archive — 10 GiB.</summary>
    public const long MaxArchiveBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>A base64 raw P-256 point is ~88 chars; this bounds a malformed or hostile value.</summary>
    private const int MaxRecipientKeyLength = 512;

    private readonly AdminAuthService _adminAuth = adminAuth;
    private readonly DeviceScopeAuthorizer _scope = scope;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly FileCollectionRelay _relay = relay;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly ILogger<FileCollectionService> _logger = logger;

    public async Task<FileCollectionStartResult> StartAsync(
        Guid organizationId,
        Guid deviceId,
        Guid actorId,
        string actorDisplay,
        string? recipientPublicKey,
        string? userSid,
        IReadOnlyList<string>? rootKeys,
        string? currentPassword,
        string? justification,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientPublicKey) || recipientPublicKey.Length > MaxRecipientKeyLength)
        {
            return Bad("A valid recipient public key is required.");
        }

        if (string.IsNullOrWhiteSpace(userSid))
        {
            return Bad("A Windows user must be chosen.");
        }

        var roots = NormalizeRoots(rootKeys);
        if (roots.Count == 0)
        {
            return Bad("Choose at least one of Desktop, Downloads or Documents.");
        }

        var trimmedJustification = justification?.Trim() ?? string.Empty;
        if (trimmedJustification.Length < 3)
        {
            return Bad("A justification is required.");
        }

        if (trimmedJustification.Length > 500)
        {
            return Bad("That justification is too long.");
        }

        // Step-up: the same current-password re-confirmation the BitLocker reveal and
        // agent-release actions use. A wrong password counts toward account lockout.
        var stepUpOk = await _adminAuth.VerifyCurrentPasswordAsync(actorId, currentPassword ?? string.Empty, cancellationToken);
        if (!stepUpOk)
        {
            await _auditWriter.WriteImmediatelyAsync(
                organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
                "device.files.collect_denied", AuditResult.Failure,
                a => a.OnDevice(deviceId, null)
                      .OnTarget("windows_user", userSid, userSid)
                      .Requiring(Permissions.Files.Collect)
                      .WithFailureReason("Step-up password incorrect."),
                cancellationToken);

            return new(FileCollectionStartStatus.StepUpFailed, null, "That password is not correct.");
        }

        var device = await _dbContext.Devices.AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == organizationId && d.Status == DeviceStatus.Active,
                cancellationToken);
        if (device is null
            || !await _scope.CanActOnDeviceAsync(actorId, organizationId, deviceId, cancellationToken))
        {
            return new(FileCollectionStartStatus.NotFound, null, "Device not found.");
        }

        var transferId = Guid.NewGuid().ToString("N");
        await _relay.CreateAsync(transferId, organizationId, deviceId);

        var payload = new TaskPayloads.CollectFiles(transferId, recipientPublicKey, userSid, roots, MaxArchiveBytes);
        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.CollectFiles, payload, actorId, actorDisplay, cancellationToken);
        if (task is null)
        {
            await _relay.AbandonAsync(transferId);
            return new(FileCollectionStartStatus.AgentUnavailable, null,
                "The device is unavailable, or its agent is older than 1.29.0 and cannot collect files.");
        }

        // The compliance record: who collected whose files and which folders. Never
        // the file names (the server never sees them). QueueAsync already wrote the
        // task.queue.collectfiles entry; this is the named, operator-facing one.
        await _auditWriter.WriteImmediatelyAsync(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            "device.files.collect", AuditResult.Success,
            a => a.OnDevice(device.Id, device.Hostname)
                  .OnTarget("windows_user", userSid, userSid)
                  .Requiring(Permissions.Files.Collect)
                  .WithStateChange(null, AuditStateRedactor.Redact(new Dictionary<string, object?>
                  {
                      ["transferId"] = transferId,
                      ["userSid"] = userSid,
                      ["folders"] = roots,
                      ["justification"] = trimmedJustification,
                  })),
            cancellationToken);

        _logger.LogInformation(
            "File collection {TransferId} started for device {DeviceId}, user {UserSid}, folders {Folders}, by {Actor}.",
            transferId, deviceId, userSid, string.Join(",", roots), actorId);

        return new(FileCollectionStartStatus.Started, transferId, null);
    }

    /// <summary>
    /// Keeps only recognised folder keys, matched case-insensitively against the
    /// allowed set and de-duplicated. Anything else is dropped here, so the agent
    /// never receives a key it would have to reject.
    /// </summary>
    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string>? rootKeys)
    {
        if (rootKeys is null)
        {
            return [];
        }

        return rootKeys
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => FileCollectionProtocol.AllowedRootKeys
                .FirstOrDefault(a => string.Equals(a, r.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(r => r is not null)
            .Select(r => r!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static FileCollectionStartResult Bad(string message) =>
        new(FileCollectionStartStatus.BadRequest, null, message);
}
