using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Policies;

/// <summary>
/// Manages policies and their compliance results. Admin operations are audited;
/// agent-facing reads resolve a device's effective policy set and ingest
/// compliance reports.
/// </summary>
public sealed class PolicyService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<Policy> CreateAsync(
        Guid organizationId, PolicyType type, string name, string description, string desiredStateJson,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var policy = new Policy(organizationId, type, name, description);
        policy.AddVersion(desiredStateJson, now);
        _dbContext.Policies.Add(policy);

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "policy.create", AuditResult.Success,
            a => a.OnTarget("policy", policy.Id.ToString(), name)
                  .Requiring(Domain.Authorization.Permissions.Policy.Create)
                  .WithStateChange(null, desiredStateJson));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return policy;
    }

    public async Task<bool> AddVersionAsync(
        Guid organizationId, Guid policyId, string desiredStateJson,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var policy = await _dbContext.Policies
            .SingleOrDefaultAsync(p => p.Id == policyId && p.OrganizationId == organizationId, cancellationToken);
        if (policy is null)
        {
            return false;
        }

        policy.AddVersion(desiredStateJson, _timeProvider.GetUtcNow());

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "policy.version", AuditResult.Success,
            a => a.OnTarget("policy", policy.Id.ToString(), policy.Name)
                  .Requiring(Domain.Authorization.Permissions.Policy.Create)
                  .WithStateChange(null, desiredStateJson));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AssignToDeviceAsync(
        Guid organizationId, Guid policyId, Guid deviceId,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var policy = await _dbContext.Policies
            .SingleOrDefaultAsync(p => p.Id == policyId && p.OrganizationId == organizationId, cancellationToken);
        var deviceExists = await _dbContext.Devices
            .AnyAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (policy is null || !deviceExists)
        {
            return false;
        }

        var already = await _dbContext.PolicyAssignments.AnyAsync(
            x => x.PolicyId == policyId && x.TargetType == PolicyAssignmentTarget.Device && x.TargetId == deviceId,
            cancellationToken);
        if (already)
        {
            return true;
        }

        _dbContext.PolicyAssignments.Add(
            new PolicyAssignment(organizationId, policyId, PolicyAssignmentTarget.Device, deviceId));

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "policy.assign", AuditResult.Success,
            a => a.OnDevice(deviceId, deviceId.ToString())
                  .OnTarget("policy", policyId.ToString(), policy.Name)
                  .Requiring(Domain.Authorization.Permissions.Policy.Assign));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Effective, enabled policies for a device, each with its current version.</summary>
    public async Task<IReadOnlyList<EffectivePolicy>> GetEffectivePoliciesAsync(
        Guid deviceId, CancellationToken cancellationToken = default)
    {
        // Direct device assignments plus assignments to the device's group. A
        // device is in exactly one group, so there is at most one; kept as a list
        // so the query below is unchanged.
        var groupIds = await _dbContext.Devices
            .Where(d => d.Id == deviceId)
            .Select(d => d.DeviceGroupId)
            .ToListAsync(cancellationToken);

        var policyIds = await _dbContext.PolicyAssignments
            .Where(a =>
                (a.TargetType == PolicyAssignmentTarget.Device && a.TargetId == deviceId)
                || (a.TargetType == PolicyAssignmentTarget.Group && groupIds.Contains(a.TargetId)))
            .Select(a => a.PolicyId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (policyIds.Count == 0)
        {
            return [];
        }

        var policies = await _dbContext.Policies
            .Include(p => p.Versions)
            .Where(p => policyIds.Contains(p.Id) && p.IsEnabled)
            .ToListAsync(cancellationToken);

        var result = new List<EffectivePolicy>();
        foreach (var policy in policies)
        {
            var version = policy.Versions.SingleOrDefault(v => v.VersionNumber == policy.CurrentVersionNumber);
            if (version is not null)
            {
                result.Add(new EffectivePolicy(policy, version));
            }
        }

        return result;
    }

    /// <summary>True when the device has an effective policy with no up-to-date compliance result.</summary>
    public async Task<bool> HasPendingComplianceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectivePoliciesAsync(deviceId, cancellationToken);
        if (effective.Count == 0)
        {
            return false;
        }

        var results = await _dbContext.PolicyComplianceResults
            .Where(r => r.DeviceId == deviceId)
            .ToDictionaryAsync(r => r.PolicyId, r => r.PolicyVersionNumber, cancellationToken);

        return effective.Any(e =>
            !results.TryGetValue(e.Policy.Id, out var reportedVersion)
            || reportedVersion != e.Policy.CurrentVersionNumber);
    }

    public async Task RecordComplianceAsync(
        Guid organizationId,
        Guid deviceId,
        IReadOnlyList<ComplianceInput> items,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // Only accept results for policies actually assigned to this device.
        var assignedPolicyIds = (await GetEffectivePoliciesAsync(deviceId, cancellationToken))
            .Select(e => e.Policy.Id).ToHashSet();

        var existing = await _dbContext.PolicyComplianceResults
            .Where(r => r.DeviceId == deviceId)
            .ToDictionaryAsync(r => r.PolicyId, cancellationToken);

        foreach (var item in items)
        {
            if (!assignedPolicyIds.Contains(item.PolicyId))
            {
                continue;
            }

            if (!existing.TryGetValue(item.PolicyId, out var row))
            {
                row = new PolicyComplianceResult(organizationId, deviceId, item.PolicyId);
                _dbContext.PolicyComplianceResults.Add(row);
                existing[item.PolicyId] = row;
            }

            var deviationsJson = item.Deviations.Count == 0
                ? null
                : JsonSerializer.Serialize(item.Deviations, JsonOptions);

            row.Record(item.VersionId, item.VersionNumber, item.State, deviationsJson, now);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

public sealed record EffectivePolicy(Policy Policy, PolicyVersion Version);

public sealed record ComplianceInput(
    Guid PolicyId, Guid VersionId, int VersionNumber, PolicyComplianceState State, IReadOnlyList<string> Deviations);
