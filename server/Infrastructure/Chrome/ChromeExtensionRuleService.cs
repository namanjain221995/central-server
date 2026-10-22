using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Chrome;

public enum ChromeExtensionRuleOutcome
{
    Queued = 0,

    /// <summary>No such device in the caller's organization, or it is retired.</summary>
    DeviceNotFound = 1,

    /// <summary>The device's agent is too old to carry the extension-policy executor.</summary>
    AgentTooOld = 2,

    /// <summary>Removing a rule the device does not have.</summary>
    RuleNotFound = 3,
}

/// <summary>The outcome of setting or removing one rule on one device.</summary>
public sealed record ChromeExtensionRuleResult(
    ChromeExtensionRuleOutcome Outcome, DeviceTask? Task, string? Error);

/// <summary>One rule as the console reads it.</summary>
/// <param name="Mode">The <see cref="ChromeExtensionRuleMode"/> name, exactly as the task payload carries it.</param>
/// <param name="UpdatedAt">When the rule was last set -- the row's persistence stamp.</param>
public sealed record ChromeExtensionRuleRow(
    string ExtensionId, string Mode, string? DisplayName, DateTimeOffset UpdatedAt);

/// <summary>
/// Keeps each device's Chrome extension rules and, on every change, sends the
/// device its complete policy as one <c>ApplyChromeExtensionPolicy</c> task.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every mutation re-sends the whole state.</b> Setting a rule, re-deciding one
/// and lifting one all end the same way: one task carrying every rule the device
/// now has, built from the rows this same transaction commits. There is no
/// "remove this extension's rule" message to lose -- removal is the absence of an
/// entry in the next policy -- and lifting the last rule still queues the task,
/// because an empty policy is the instruction that clears the device.
/// </para>
/// <para>
/// <b>Rule change and task queue commit together.</b> Both happen in one
/// transaction under the execution strategy, the way <see cref="Chat.ChatService"/>
/// pairs a session with its window-open task: a policy row the fleet never heard
/// about, or a task describing rows that rolled back, would each make the console
/// and the device disagree about what the policy is.
/// </para>
/// <para>
/// <b>Refusals are told apart.</b> A missing or retired device and an agent below
/// the executor's floor are different answers (the endpoint turns them into 404
/// and 409), exactly as <see cref="Devices.DriverUpdateService"/> distinguishes
/// them -- the whole fleet is below the required agent until the 1.23.0 rollout
/// reaches it, and "no such device" would be a lie the operator can see through.
/// </para>
/// <para>
/// <b>Audited twice, on purpose.</b> <see cref="DeviceTaskService.QueueAsync"/>
/// records the queued task with its full payload, as it does for every task; this
/// service additionally records the decision itself -- <c>chrome.extension.rule.set</c>
/// or <c>chrome.extension.rule.removed</c>, with the rule's before and after mode --
/// so the audit trail answers "who changed what about this extension" without
/// diffing whole-state payloads.
/// </para>
/// </remarks>
public sealed class ChromeExtensionRuleService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<ChromeExtensionRuleService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChromeExtensionRuleService> _logger = logger;

    // -------------------------------------------------------------------- read

    /// <summary>
    /// One device's rules, ordered by extension id. The caller has already
    /// scope-checked the device; a device with no rules is an empty list.
    /// </summary>
    public async Task<IReadOnlyList<ChromeExtensionRuleRow>> ListRulesAsync(
        Guid deviceId, CancellationToken cancellationToken = default)
    {
        var rules = await _dbContext.ChromeExtensionRules.AsNoTracking()
            .Where(r => r.DeviceId == deviceId)
            .OrderBy(r => r.ExtensionId)
            .ToListAsync(cancellationToken);

        return rules
            .Select(r => new ChromeExtensionRuleRow(r.ExtensionId, r.Mode.ToString(), r.DisplayName, r.UpdatedAt))
            .ToList();
    }

    // --------------------------------------------------------------------- set

    /// <summary>
    /// Sets (or re-decides) one rule on one device the caller has already
    /// scope-checked, and queues the device's complete policy.
    /// </summary>
    public Task<ChromeExtensionRuleResult> SetRuleAsync(
        Guid organizationId, Guid deviceId, string extensionId, ChromeExtensionRuleMode mode,
        string? displayName, Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => SetRuleInTransactionAsync(
            organizationId, deviceId, extensionId, mode, displayName, actorId, actorDisplay, cancellationToken));
    }

    private async Task<ChromeExtensionRuleResult> SetRuleInTransactionAsync(
        Guid organizationId, Guid deviceId, string extensionId, ChromeExtensionRuleMode mode,
        string? displayName, Guid actorId, string actorDisplay, CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var (device, refusal) = await AgentGateAsync(organizationId, deviceId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var rules = await LoadRulesAsync(deviceId, cancellationToken);
        var existing = rules.SingleOrDefault(r => string.Equals(r.ExtensionId, extensionId, StringComparison.Ordinal));
        var previousMode = existing?.Mode;

        if (existing is null)
        {
            // The constructor restates the endpoint's validation (32 chars, a-p;
            // label length), so an invalid rule cannot reach the table or the wire
            // through any future caller either.
            var rule = new ChromeExtensionRule(deviceId, extensionId, mode, displayName);
            _dbContext.ChromeExtensionRules.Add(rule);
            rules.Add(rule);
            existing = rule;
        }
        else
        {
            existing.Apply(mode, displayName);
        }

        var task = await QueuePolicyAsync(organizationId, deviceId, rules, actorId, actorDisplay, cancellationToken);
        if (task is null)
        {
            // The gate above passed, so this is the losing side of a race with a
            // retirement or an agent downgrade. Not committed: disposing the
            // transaction rolls the rule back too.
            return new ChromeExtensionRuleResult(
                ChromeExtensionRuleOutcome.DeviceNotFound, null, "The device could not accept the task.");
        }

        _auditWriter.Stage(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "chrome.extension.rule.set", AuditResult.Success,
            a => a.OnDevice(device!.Id, device.Hostname)
                  .OnTarget("chrome_extension_rule", extensionId, existing.DisplayName ?? extensionId)
                  .Requiring(Permissions.Chrome.Manage)
                  .WithStateChange(
                      previousMode is { } before ? JsonSerializer.Serialize(new { mode = before.ToString() }) : null,
                      JsonSerializer.Serialize(new
                      {
                          mode = existing.Mode.ToString(),
                          displayName = existing.DisplayName,
                          taskId = task.Id,
                      })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Chrome extension rule {ExtensionId} on device {DeviceId} set to {Mode} by {Actor} "
            + "(was {Previous}); policy of {Count} rule(s) queued as task {TaskId}.",
            extensionId, deviceId, existing.Mode, actorDisplay, previousMode?.ToString() ?? "unset",
            rules.Count, task.Id);

        return new ChromeExtensionRuleResult(ChromeExtensionRuleOutcome.Queued, task, null);
    }

    // ------------------------------------------------------------------ remove

    /// <summary>
    /// Lifts one rule on one device the caller has already scope-checked, and
    /// queues the device's complete remaining policy -- still queued when nothing
    /// remains, because the empty policy is what clears the device.
    /// </summary>
    public Task<ChromeExtensionRuleResult> RemoveRuleAsync(
        Guid organizationId, Guid deviceId, string extensionId,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => RemoveRuleInTransactionAsync(
            organizationId, deviceId, extensionId, actorId, actorDisplay, cancellationToken));
    }

    private async Task<ChromeExtensionRuleResult> RemoveRuleInTransactionAsync(
        Guid organizationId, Guid deviceId, string extensionId,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var (device, refusal) = await AgentGateAsync(organizationId, deviceId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var rules = await LoadRulesAsync(deviceId, cancellationToken);
        var existing = rules.SingleOrDefault(r => string.Equals(r.ExtensionId, extensionId, StringComparison.Ordinal));
        if (existing is null)
        {
            return new ChromeExtensionRuleResult(
                ChromeExtensionRuleOutcome.RuleNotFound, null, "This device has no rule for that extension.");
        }

        _dbContext.ChromeExtensionRules.Remove(existing);
        rules.Remove(existing);

        var task = await QueuePolicyAsync(organizationId, deviceId, rules, actorId, actorDisplay, cancellationToken);
        if (task is null)
        {
            return new ChromeExtensionRuleResult(
                ChromeExtensionRuleOutcome.DeviceNotFound, null, "The device could not accept the task.");
        }

        _auditWriter.Stage(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "chrome.extension.rule.removed", AuditResult.Success,
            a => a.OnDevice(device!.Id, device.Hostname)
                  .OnTarget("chrome_extension_rule", extensionId, existing.DisplayName ?? extensionId)
                  .Requiring(Permissions.Chrome.Manage)
                  .WithStateChange(
                      JsonSerializer.Serialize(new { mode = existing.Mode.ToString() }),
                      JsonSerializer.Serialize(new { mode = (string?)null, taskId = task.Id })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Chrome extension rule {ExtensionId} on device {DeviceId} (was {Previous}) removed by {Actor}; "
            + "policy of {Count} rule(s) queued as task {TaskId}.",
            extensionId, deviceId, existing.Mode, actorDisplay, rules.Count, task.Id);

        return new ChromeExtensionRuleResult(ChromeExtensionRuleOutcome.Queued, task, null);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The device, or the refusal: missing/retired and agent-too-old told apart,
    /// as <see cref="Devices.DriverUpdateService"/> tells them. Exactly one of the
    /// pair is non-null.
    /// </summary>
    private async Task<(Device? Device, ChromeExtensionRuleResult? Refusal)> AgentGateAsync(
        Guid organizationId, Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return (null, new ChromeExtensionRuleResult(
                ChromeExtensionRuleOutcome.DeviceNotFound, null, "No such device."));
        }

        var definition = DeviceTaskCatalog.Require(DeviceTaskType.ApplyChromeExtensionPolicy);
        if (!DeviceTaskCatalog.IsSupportedBy(definition, device.AgentVersion))
        {
            return (null, new ChromeExtensionRuleResult(
                ChromeExtensionRuleOutcome.AgentTooOld, null,
                $"This device runs agent {device.AgentVersion}; Chrome extension control needs "
                + $"{definition.MinimumAgentVersion} or newer. Update the agent first."));
        }

        return (device, null);
    }

    /// <summary>Tracked, so the mutation and the payload describe the same rows.</summary>
    private Task<List<ChromeExtensionRule>> LoadRulesAsync(Guid deviceId, CancellationToken cancellationToken) =>
        _dbContext.ChromeExtensionRules
            .Where(r => r.DeviceId == deviceId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Queues the device's complete policy. The task service applies the
    /// minimum-agent gate again and writes the task's own audit record; its
    /// <c>SaveChanges</c> also flushes the rule rows this transaction holds.
    /// </summary>
    private Task<DeviceTask?> QueuePolicyAsync(
        Guid organizationId, Guid deviceId, IReadOnlyCollection<ChromeExtensionRule> rules,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken)
    {
        var payload = new TaskPayloads.ApplyChromeExtensionPolicy(
            rules
                .OrderBy(r => r.ExtensionId, StringComparer.Ordinal)
                .Select(r => new TaskPayloads.ChromeExtensionRulePayload(r.ExtensionId, r.Mode.ToString()))
                .ToList(),
            _timeProvider.GetUtcNow());

        return _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.ApplyChromeExtensionPolicy, payload,
            actorId, actorDisplay, cancellationToken);
    }
}
