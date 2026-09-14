using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// Registers approved software packages, serves their metadata, and turns a
/// deploy request into audited <see cref="DeviceTaskType.InstallPackage"/> tasks.
/// The privileged install itself happens on the agent; this service only ever
/// stores content and queues intent, both audited.
/// </summary>
public sealed class SoftwarePackageService(
    EndpointPlatformDbContext dbContext,
    IPackageContentStore contentStore,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly IPackageContentStore _contentStore = contentStore;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Registers a package: stores the content (hash-verified on write) then the
    /// metadata row. Content is stored before the row so a committed row always has
    /// its bytes. A duplicate (same org + hash) is rejected.
    /// </summary>
    public async Task<PackageCreateResult> CreateAsync(
        Guid organizationId,
        string name,
        string version,
        string? publisher,
        string declaredSha256,
        string fileName,
        string msiProductCode,
        string? requiredSignerSubject,
        Stream content,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var normalizedHash = declaredSha256.Trim().ToLowerInvariant();

        var duplicate = await _dbContext.SoftwarePackages.AnyAsync(
            p => p.OrganizationId == organizationId && p.Sha256 == normalizedHash, cancellationToken);
        if (duplicate)
        {
            return PackageCreateResult.Duplicate();
        }

        long sizeBytes;
        try
        {
            sizeBytes = await _contentStore.SaveAsync(normalizedHash, content, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Hash mismatch: the declared hash and the uploaded bytes disagree.
            return PackageCreateResult.HashMismatch();
        }

        SoftwarePackage package;
        try
        {
            package = new SoftwarePackage(
                organizationId, name, version, publisher, SoftwarePackageType.WindowsInstaller,
                normalizedHash, fileName, sizeBytes, msiProductCode, requiredSignerSubject,
                actorId, actorDisplay);
        }
        catch (ArgumentException ex)
        {
            return PackageCreateResult.Invalid(ex.Message);
        }

        _dbContext.SoftwarePackages.Add(package);

        var stateJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            sha256 = normalizedHash,
            productCode = package.MsiProductCode,
            fileName = package.FileName,
            requiredSigner = package.RequiredSignerSubject,
        });

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.package.create", AuditResult.Success,
            a => a.OnTarget("software_package", package.Id.ToString(), $"{name} {version}")
                  .Requiring(Permissions.Software.Deploy)
                  .WithStateChange(null, stateJson));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return PackageCreateResult.Created(package);
    }

    public async Task<IReadOnlyList<SoftwarePackage>> ListAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        await _dbContext.SoftwarePackages
            .AsNoTracking()
            .Where(p => p.OrganizationId == organizationId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<SoftwarePackage?> GetDeployableAsync(
        Guid organizationId, Guid packageId, CancellationToken cancellationToken = default) =>
        await _dbContext.SoftwarePackages.AsNoTracking().SingleOrDefaultAsync(
            p => p.Id == packageId && p.OrganizationId == organizationId && !p.IsWithdrawn, cancellationToken);

    /// <summary>
    /// Returns a withdrawn package to the catalogue.
    /// </summary>
    /// <remarks>
    /// Audited under its own action so the trail reads as a pair: a package that
    /// was withdrawn and later restored shows both, rather than one event whose
    /// meaning depends on the row's current state.
    /// </remarks>
    public async Task<bool> RestoreAsync(
        Guid organizationId, Guid packageId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var package = await _dbContext.SoftwarePackages.SingleOrDefaultAsync(
            p => p.Id == packageId && p.OrganizationId == organizationId, cancellationToken);
        if (package is null)
        {
            return false;
        }

        package.Restore();

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.package.restore", AuditResult.Success,
            a => a.OnTarget("software_package", package.Id.ToString(), $"{package.Name} {package.Version}")
                  .Requiring(Permissions.Software.Deploy));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> WithdrawAsync(
        Guid organizationId, Guid packageId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var package = await _dbContext.SoftwarePackages.SingleOrDefaultAsync(
            p => p.Id == packageId && p.OrganizationId == organizationId, cancellationToken);
        if (package is null)
        {
            return false;
        }

        package.Withdraw(_timeProvider.GetUtcNow());

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.package.withdraw", AuditResult.Success,
            a => a.OnTarget("software_package", package.Id.ToString(), $"{package.Name} {package.Version}")
                  .Requiring(Permissions.Software.Deploy));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Queues an InstallPackage task for a single device. Null if the package or device is not deployable.</summary>
    public async Task<DeviceTask?> DeployToDeviceAsync(
        Guid organizationId, Guid packageId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var package = await GetDeployableAsync(organizationId, packageId, cancellationToken);
        if (package is null)
        {
            return null;
        }

        return await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.InstallPackage,
            PayloadFor(package), actorId, actorDisplay, cancellationToken);
    }

    /// <summary>Queues an InstallPackage task for every active member of a group.</summary>
    public async Task<GroupDeployResult?> DeployToGroupAsync(
        Guid organizationId, Guid packageId, Guid groupId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var package = await GetDeployableAsync(organizationId, packageId, cancellationToken);
        if (package is null)
        {
            return null;
        }

        var groupExists = await _dbContext.DeviceGroups.AnyAsync(
            g => g.Id == groupId && g.OrganizationId == organizationId, cancellationToken);
        if (!groupExists)
        {
            return null;
        }

        var memberIds = await _dbContext.Devices
            .Where(d => d.DeviceGroupId == groupId && d.OrganizationId == organizationId)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var payload = PayloadFor(package);
        var queued = 0;
        foreach (var deviceId in memberIds)
        {
            var task = await _taskService.QueueAsync(
                organizationId, deviceId, DeviceTaskType.InstallPackage, payload, actorId, actorDisplay,
                cancellationToken);
            if (task is not null)
            {
                queued++;
            }
        }

        return new GroupDeployResult(memberIds.Count, queued);
    }

    private static TaskPayloads.InstallPackage PayloadFor(SoftwarePackage package) =>
        new(package.Id, package.Sha256, package.MsiProductCode, package.RequiredSignerSubject,
            package.Name, package.Version);
}

public sealed record PackageCreateResult(
    PackageCreateStatus Status, SoftwarePackage? Package, string? Error)
{
    public static PackageCreateResult Created(SoftwarePackage p) => new(PackageCreateStatus.Created, p, null);
    public static PackageCreateResult Duplicate() => new(PackageCreateStatus.Duplicate, null, null);
    public static PackageCreateResult HashMismatch() => new(PackageCreateStatus.HashMismatch, null, null);
    public static PackageCreateResult Invalid(string error) => new(PackageCreateStatus.Invalid, null, error);
}

public enum PackageCreateStatus
{
    Created = 0,
    Duplicate = 1,
    HashMismatch = 2,
    Invalid = 3,
}

public sealed record GroupDeployResult(int MemberCount, int QueuedCount);
