using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// Turns "deploy this package to these devices and groups" into a durable
/// deployment record and exactly the InstallPackage tasks that are actually
/// needed.
/// </summary>
/// <remarks>
/// <para>
/// The existing single-device and single-group deploy paths queue a task for
/// every target unconditionally. That is fine for one machine and wrong for a
/// fleet: it reinstalls software that is already correct, which at 350 devices is
/// hundreds of avoidable MSI executions on working installations. This service
/// resolves targets, asks <see cref="SoftwareEligibilityEvaluator"/> what each
/// device actually needs, and records the ones it deliberately skipped.
/// </para>
/// <para>
/// <b>Every target is authorized server-side.</b> Device ids and group ids arrive
/// from an untrusted client, so both are re-resolved against the caller's
/// organization and device scope. A device the caller may not see is dropped
/// during resolution and never appears in the deployment -- it is not reported as
/// refused either, because saying "you may not target this device" would confirm
/// that it exists.
/// </para>
/// </remarks>
public sealed class SoftwareDeploymentService(
    EndpointPlatformDbContext dbContext,
    SoftwarePackageService packageService,
    DeviceTaskService taskService,
    AuditWriter auditWriter)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly SoftwarePackageService _packageService = packageService;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;

    /// <summary>
    /// Works out what a deployment would do, without creating anything.
    /// </summary>
    /// <remarks>
    /// Backs the confirmation dialog. It runs exactly the same resolution and
    /// eligibility code as <see cref="CreateAsync"/> -- a preview computed a
    /// different way would eventually disagree with what deploying actually does,
    /// and the operator would stop trusting the numbers.
    /// </remarks>
    public async Task<DeploymentPlan?> PlanAsync(
        Guid organizationId,
        Guid packageId,
        IReadOnlyCollection<Guid> deviceIds,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var package = await _packageService.GetDeployableAsync(organizationId, packageId, cancellationToken);
        if (package is null)
        {
            return null;
        }

        var decisions = await DecideAsync(
            organizationId, package, deviceIds, groupIds, scopedDeviceIds, cancellationToken);

        return new DeploymentPlan(
            package.Id, package.Name, package.Version, decisions);
    }

    /// <summary>
    /// Creates the deployment and queues an InstallPackage task per device that
    /// needs one.
    /// </summary>
    public async Task<DeploymentResult?> CreateAsync(
        Guid organizationId,
        Guid packageId,
        IReadOnlyCollection<Guid> deviceIds,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var package = await _packageService.GetDeployableAsync(organizationId, packageId, cancellationToken);
        if (package is null)
        {
            // Missing, another organization's, or withdrawn. A withdrawn package
            // is refused here rather than at the endpoint, so every caller gets
            // the rule -- withdrawal means "not for new deployments".
            return null;
        }

        var decisions = await DecideAsync(
            organizationId, package, deviceIds, groupIds, scopedDeviceIds, cancellationToken);

        if (decisions.Count == 0)
        {
            return null;
        }

        var targetType = (deviceIds.Count > 0, groupIds.Count > 0) switch
        {
            (true, true) => DeploymentTargetType.Mixed,
            (false, true) => DeploymentTargetType.Groups,
            _ => DeploymentTargetType.Devices,
        };

        var deployment = new SoftwareDeployment(
            organizationId, package.Id, package.Name, package.Version, targetType, actorId, actorDisplay);
        _dbContext.SoftwareDeployments.Add(deployment);

        var (queued, skipped) = await WriteTargetsAsync(
            deployment, package, decisions, attempt: 1, actorId, actorDisplay, cancellationToken);

        var summary = JsonSerializer.Serialize(new
        {
            package = $"{package.Name} {package.Version}",
            targeted = decisions.Count,
            queued,
            skipped,
        });

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.deployment.create", AuditResult.Success,
            a => a.OnTarget("software_deployment", deployment.Id.ToString(), $"{package.Name} {package.Version}")
                  .Requiring(Permissions.Software.Deploy)
                  .WithStateChange(null, summary));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new DeploymentResult(deployment.Id, decisions.Count, queued, skipped);
    }

    /// <summary>
    /// Re-runs the devices that did not succeed, as a new attempt on the same
    /// deployment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately re-runs the <em>whole</em> decision, not just the queueing:
    /// authorization, package lifecycle, retired state and eligibility are all
    /// evaluated again through the same <see cref="DecideAsync"/> path the
    /// original deployment used. A retry is a fresh decision about the world as
    /// it is now, not a replay of an old one -- a device that has since been
    /// retired, fallen out of scope, or acquired the software by other means must
    /// not be sent an install because it failed an hour ago.
    /// </para>
    /// <para>
    /// A withdrawn package stops retries too: <see cref="SoftwarePackageService.GetDeployableAsync"/>
    /// returns null, so there is nothing to retry with. That is the point of
    /// withdrawal.
    /// </para>
    /// <para>
    /// History is preserved. The previous attempt's rows are untouched and the
    /// new decisions are written as attempt N+1.
    /// </para>
    /// </remarks>
    public async Task<DeploymentResult?> RetryAsync(
        Guid organizationId,
        Guid deploymentId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var deployment = await _dbContext.SoftwareDeployments
            .SingleOrDefaultAsync(
                d => d.Id == deploymentId && d.OrganizationId == organizationId, cancellationToken);

        if (deployment is null)
        {
            return null;
        }

        var package = await _packageService.GetDeployableAsync(
            organizationId, deployment.PackageId, cancellationToken);
        if (package is null)
        {
            return null;
        }

        // The devices worth retrying: those whose most recent attempt ended
        // badly. A target that succeeded, or is still running, is not retried --
        // that is the difference between a retry and a redeploy.
        var rows = await (
            from target in _dbContext.SoftwareDeploymentTargets.AsNoTracking()
            join task in _dbContext.DeviceTasks.AsNoTracking() on target.TaskId equals task.Id into taskJoin
            from task in taskJoin.DefaultIfEmpty()
            where target.DeploymentId == deploymentId
            select new
            {
                target.DeviceId,
                target.Attempt,
                target.State,
                TaskStatus = (DeviceTaskStatus?)task.Status,
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return null;
        }

        var nextAttempt = rows.Max(r => r.Attempt) + 1;

        var retryable = rows
            .GroupBy(r => r.DeviceId)
            .Select(g => g.OrderByDescending(r => r.Attempt).First())
            .Where(r => r.State == DeploymentTargetState.Queued
                && r.TaskStatus is DeviceTaskStatus.Failed
                    or DeviceTaskStatus.Expired
                    or DeviceTaskStatus.Cancelled)
            .Select(r => r.DeviceId)
            .ToList();

        if (retryable.Count == 0)
        {
            return new DeploymentResult(deployment.Id, 0, 0, 0);
        }

        var decisions = await DecideAsync(
            organizationId, package, retryable, [], scopedDeviceIds, cancellationToken);

        var (queued, skipped) = await WriteTargetsAsync(
            deployment, package, decisions, nextAttempt, actorId, actorDisplay, cancellationToken);

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.deployment.retry", AuditResult.Success,
            a => a.OnTarget("software_deployment", deployment.Id.ToString(), $"{package.Name} {package.Version}")
                  .Requiring(Permissions.Software.Deploy)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      attempt = nextAttempt,
                      retried = decisions.Count,
                      queued,
                      skipped,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new DeploymentResult(deployment.Id, decisions.Count, queued, skipped);
    }

    /// <summary>
    /// Cancels the work in a deployment that has not reached an agent yet.
    /// </summary>
    /// <remarks>
    /// Only Queued tasks are cancellable, and that limit comes from
    /// <see cref="DeviceTaskService.CancelAsync"/> rather than being re-decided
    /// here: a task already delivered is running on a Windows machine, and
    /// reporting it as cancelled would be a lie the console then repeats.
    /// Delivered and finished targets are left exactly as they are.
    /// </remarks>
    public async Task<DeploymentCancelResult?> CancelPendingAsync(
        Guid organizationId,
        Guid deploymentId,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var deployment = await _dbContext.SoftwareDeployments.AsNoTracking()
            .SingleOrDefaultAsync(
                d => d.Id == deploymentId && d.OrganizationId == organizationId, cancellationToken);

        if (deployment is null)
        {
            return null;
        }

        var pending = await (
            from target in _dbContext.SoftwareDeploymentTargets.AsNoTracking()
            join task in _dbContext.DeviceTasks.AsNoTracking() on target.TaskId equals task.Id
            where target.DeploymentId == deploymentId && task.Status == DeviceTaskStatus.Queued
            select new { target.DeviceId, TaskId = task.Id })
            .ToListAsync(cancellationToken);

        var cancelled = 0;
        foreach (var item in pending)
        {
            var result = await _taskService.CancelAsync(
                organizationId, item.DeviceId, item.TaskId, actorId, actorDisplay, cancellationToken);

            // NotCancellable means the agent claimed it between the query and
            // now. That is a race the design expects, and losing it is correct:
            // the install is genuinely under way.
            if (result == TaskCancelResult.Success)
            {
                cancelled++;
            }
        }

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.deployment.cancel", AuditResult.Success,
            a => a.OnTarget("software_deployment", deployment.Id.ToString(),
                    $"{deployment.PackageName} {deployment.PackageVersion}")
                  .Requiring(Permissions.Software.Deploy)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      considered = pending.Count,
                      cancelled,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new DeploymentCancelResult(deployment.Id, pending.Count, cancelled);
    }

    /// <summary>
    /// Writes one attempt's decisions and queues the tasks they call for.
    /// </summary>
    /// <remarks>
    /// Shared by the first deployment and every retry so the two can never drift
    /// -- the rule about what gets a task, and what gets recorded as skipped, is
    /// written once.
    /// </remarks>
    private async Task<(int Queued, int Skipped)> WriteTargetsAsync(
        SoftwareDeployment deployment,
        SoftwarePackage package,
        IReadOnlyList<DeploymentDecision> decisions,
        int attempt,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken)
    {
        var payload = new TaskPayloads.InstallPackage(
            package.Id, package.Sha256, package.MsiProductCode, package.RequiredSignerSubject,
            package.Name, package.Version);

        var queued = 0;
        var skipped = 0;

        foreach (var decision in decisions)
        {
            Guid? taskId = null;

            if (decision.Eligibility.NeedsInstall())
            {
                // Still queued through the task service, never by writing a task
                // row here: it is what enforces the retired-device rule, the
                // minimum agent version and the catalog definition, and a second
                // path that skipped those would be a hole in all three.
                var task = await _taskService.QueueAsync(
                    deployment.OrganizationId, decision.DeviceId, DeviceTaskType.InstallPackage,
                    payload, actorId, actorDisplay, cancellationToken);

                taskId = task?.Id;
            }

            var state = taskId is null ? DeploymentTargetState.Skipped : DeploymentTargetState.Queued;

            // A device that needed the package but whose task was refused is not
            // silently dropped: it is recorded as skipped, and the reason it was
            // refused (retired, or an agent too old for InstallPackage) is what
            // the operator sees.
            var reason = state == DeploymentTargetState.Skipped && decision.Eligibility.NeedsInstall()
                ? SoftwareEligibility.Retired
                : decision.Eligibility;

            _dbContext.SoftwareDeploymentTargets.Add(new SoftwareDeploymentTarget(
                deployment.Id, decision.DeviceId, state, reason, taskId, decision.ObservedVersion, attempt));

            if (state == DeploymentTargetState.Queued)
            {
                queued++;
            }
            else
            {
                skipped++;
            }
        }

        return (queued, skipped);
    }

    /// <summary>
    /// Resolves the targets and decides what each one needs.
    /// </summary>
    /// <remarks>
    /// Three queries regardless of fleet size: the devices, their group
    /// memberships, and all of their installed software at once. Asking per device
    /// would be an N+1 that only shows itself once a real group is targeted.
    /// </remarks>
    private async Task<IReadOnlyList<DeploymentDecision>> DecideAsync(
        Guid organizationId,
        SoftwarePackage package,
        IReadOnlyCollection<Guid> deviceIds,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken)
    {
        var targeted = new HashSet<Guid>(deviceIds);

        if (groupIds.Count > 0)
        {
            // Groups are re-resolved against the organization: a group id from
            // another tenant resolves to no members rather than to its members.
            var fromGroups = await (
                from device in _dbContext.Devices
                join grp in _dbContext.DeviceGroups on device.DeviceGroupId equals grp.Id
                where groupIds.Contains(device.DeviceGroupId) && grp.OrganizationId == organizationId
                select device.Id)
                .Distinct()
                .ToListAsync(cancellationToken);

            targeted.UnionWith(fromGroups);
        }

        if (targeted.Count == 0)
        {
            return [];
        }

        // Tenancy is enforced here, so a device id from another organization
        // simply does not come back.
        var devices = await _dbContext.Devices
            .AsNoTracking()
            .Where(d => targeted.Contains(d.Id) && d.OrganizationId == organizationId)
            .Select(d => new { d.Id, d.Status })
            .ToListAsync(cancellationToken);

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            devices = devices.Where(d => visible.Contains(d.Id)).ToList();
        }

        if (devices.Count == 0)
        {
            return [];
        }

        var deviceIdSet = devices.Select(d => d.Id).ToHashSet();

        // One query for every targeted device's software, then grouped in memory.
        var installed = await _dbContext.DeviceSoftware
            .AsNoTracking()
            .Where(s => deviceIdSet.Contains(s.DeviceId))
            .Select(s => new { s.DeviceId, s.Name, s.Version, s.Publisher, s.ProductCode })
            .ToListAsync(cancellationToken);

        var byDevice = installed
            .GroupBy(s => s.DeviceId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<InstalledApplication>)g
                    .Select(s => new InstalledApplication(s.Name, s.Version, s.Publisher, s.ProductCode))
                    .ToList());

        // Devices already carrying an outstanding install of this package. One
        // query, not one per device. This is what makes a double-clicked Deploy,
        // a browser retry or a client retry after a timeout safe: the second
        // request resolves the same devices and finds the work already queued.
        var inFlight = await (
            from target in _dbContext.SoftwareDeploymentTargets.AsNoTracking()
            join deployment in _dbContext.SoftwareDeployments.AsNoTracking()
                on target.DeploymentId equals deployment.Id
            join task in _dbContext.DeviceTasks.AsNoTracking() on target.TaskId equals task.Id
            where deployment.PackageId == package.Id
                && deviceIdSet.Contains(target.DeviceId)
                && (task.Status == DeviceTaskStatus.Queued || task.Status == DeviceTaskStatus.Delivered)
            select target.DeviceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var outstanding = inFlight.ToHashSet();

        var deployable = new DeployableSoftware(
            package.Name, package.Version, package.Publisher, package.MsiProductCode);

        var decisions = new List<DeploymentDecision>(devices.Count);

        foreach (var device in devices)
        {
            if (device.Status == DeviceStatus.Retired)
            {
                // Recorded, not dropped: "this device was targeted and excluded
                // because it is retired" is a useful answer, and silently omitting
                // it would look like the device was never targeted.
                decisions.Add(new DeploymentDecision(device.Id, SoftwareEligibility.Retired, null));
                continue;
            }

            var apps = byDevice.TryGetValue(device.Id, out var found) ? found : [];

            if (outstanding.Contains(device.Id))
            {
                decisions.Add(new DeploymentDecision(
                    device.Id, SoftwareEligibility.AlreadyInProgress, ObservedVersionFor(deployable, apps)));
                continue;
            }

            var eligibility = SoftwareEligibilityEvaluator.Evaluate(deployable, apps);

            decisions.Add(new DeploymentDecision(
                device.Id, eligibility, ObservedVersionFor(deployable, apps)));
        }

        return decisions;
    }

    /// <summary>The version actually seen on the device, for the audit trail.</summary>
    private static string? ObservedVersionFor(
        DeployableSoftware package, IReadOnlyList<InstalledApplication> installed)
    {
        foreach (var app in installed)
        {
            var codeMatch = !string.IsNullOrWhiteSpace(app.ProductCode)
                && string.Equals(app.ProductCode.Trim(), package.MsiProductCode?.Trim(), StringComparison.OrdinalIgnoreCase);

            var nameMatch = string.Equals(app.Name?.Trim(), package.Name.Trim(), StringComparison.OrdinalIgnoreCase);

            if (codeMatch || nameMatch)
            {
                return app.Version;
            }
        }

        return null;
    }
}

/// <summary>What a deployment decided for one device, before it was written.</summary>
public sealed record DeploymentDecision(
    Guid DeviceId, SoftwareEligibility Eligibility, string? ObservedVersion);

/// <summary>A dry run: what deploying would do.</summary>
public sealed record DeploymentPlan(
    Guid PackageId,
    string PackageName,
    string PackageVersion,
    IReadOnlyList<DeploymentDecision> Decisions)
{
    public int Targeted => Decisions.Count;

    public int NeedsInstall => Decisions.Count(d => d.Eligibility.NeedsInstall());

    public int AlreadyInstalled => Decisions.Count(d => d.Eligibility == SoftwareEligibility.AlreadyInstalled);

    public int NewerInstalled => Decisions.Count(d => d.Eligibility == SoftwareEligibility.NewerInstalled);

    public int Retired => Decisions.Count(d => d.Eligibility == SoftwareEligibility.Retired);

    public int NotComparable => Decisions.Count(d => d.Eligibility == SoftwareEligibility.VersionNotComparable);
}

/// <summary>The outcome of creating a deployment, or of one retry attempt.</summary>
public sealed record DeploymentResult(Guid DeploymentId, int Targeted, int Queued, int Skipped);

/// <summary>
/// The outcome of cancelling pending work.
/// </summary>
/// <param name="Considered">Targets that were still Queued when the sweep began.</param>
/// <param name="Cancelled">
/// Those actually cancelled. Lower than <paramref name="Considered"/> when an
/// agent claimed a task mid-sweep -- that install is genuinely running and is
/// correctly left alone.
/// </param>
public sealed record DeploymentCancelResult(Guid DeploymentId, int Considered, int Cancelled);
