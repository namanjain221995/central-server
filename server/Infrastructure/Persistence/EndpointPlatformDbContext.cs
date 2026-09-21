using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform.
/// </summary>
/// <remarks>
/// <para>
/// One context, not one per module. The platform is a modular monolith sharing a
/// single PostgreSQL database, and splitting the context would give up
/// cross-module foreign keys and single-transaction consistency for no benefit at
/// this scale.
/// </para>
/// <para>
/// Migrations live in the separate <c>EndpointPlatform.Migrations</c> assembly
/// (see <see cref="MigrationsAssemblyName"/>) so that neither API host ships the
/// migration history and schema changes can run under a more privileged database
/// role than the runtime application role.
/// </para>
/// </remarks>
public sealed class EndpointPlatformDbContext(DbContextOptions<EndpointPlatformDbContext> options)
    : DbContext(options)
{
    public const string MigrationsAssemblyName = "EndpointPlatform.Migrations";

    /// <summary>Schema holding every platform table.</summary>
    public const string Schema = "endpoint_platform";

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<PlatformUserRole> PlatformUserRoles => Set<PlatformUserRole>();

    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();

    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    public DbSet<AdminMfaChallenge> AdminMfaChallenges => Set<AdminMfaChallenge>();

    public DbSet<LocalAdminElevation> LocalAdminElevations => Set<LocalAdminElevation>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceHardware> DeviceHardware => Set<DeviceHardware>();

    public DbSet<DeviceNetworkInterface> DeviceNetworkInterfaces => Set<DeviceNetworkInterface>();

    public DbSet<DeviceLocalUser> DeviceLocalUsers => Set<DeviceLocalUser>();

    public DbSet<DeviceLocalGroup> DeviceLocalGroups => Set<DeviceLocalGroup>();

    public DbSet<DeviceSoftware> DeviceSoftware => Set<DeviceSoftware>();

    public DbSet<DeviceSoftwareEvidence> DeviceSoftwareEvidence => Set<DeviceSoftwareEvidence>();

    public DbSet<DeviceDriver> DeviceDrivers => Set<DeviceDriver>();

    public DbSet<DeviceBitLockerStatus> DeviceBitLockerStatus => Set<DeviceBitLockerStatus>();

    public DbSet<DeviceBitLockerVolume> DeviceBitLockerVolumes => Set<DeviceBitLockerVolume>();

    public DbSet<Domain.BitLocker.BitLockerRecoveryEscrow> BitLockerRecoveryEscrows =>
        Set<Domain.BitLocker.BitLockerRecoveryEscrow>();

    public DbSet<Domain.BitLocker.BitLockerEscrowAttempt> BitLockerEscrowAttempts =>
        Set<Domain.BitLocker.BitLockerEscrowAttempt>();

    public DbSet<DeviceSecurityPosture> DeviceSecurityPosture => Set<DeviceSecurityPosture>();

    public DbSet<DeviceUpdateStatus> DeviceUpdateStatus => Set<DeviceUpdateStatus>();

    public DbSet<DeviceUpdateHistoryEntry> DeviceUpdateHistory => Set<DeviceUpdateHistoryEntry>();

    public DbSet<DeviceServiceEntry> DeviceServices => Set<DeviceServiceEntry>();

    public DbSet<DeviceProcessEntry> DeviceProcesses => Set<DeviceProcessEntry>();

    public DbSet<Domain.Peripherals.UsbDevice> UsbDevices => Set<Domain.Peripherals.UsbDevice>();

    public DbSet<Domain.Peripherals.UsbAccessRequest> UsbAccessRequests =>
        Set<Domain.Peripherals.UsbAccessRequest>();

    public DbSet<SoftwarePackage> SoftwarePackages => Set<SoftwarePackage>();

    public DbSet<SoftwareDeployment> SoftwareDeployments => Set<SoftwareDeployment>();

    public DbSet<SoftwareDeploymentTarget> SoftwareDeploymentTargets => Set<SoftwareDeploymentTarget>();

    public DbSet<Domain.Drivers.DriverPackage> DriverPackages => Set<Domain.Drivers.DriverPackage>();

    public DbSet<Domain.Agents.AgentRelease> AgentReleases => Set<Domain.Agents.AgentRelease>();

    public DbSet<AdminDeviceScope> AdminDeviceScopes => Set<AdminDeviceScope>();

    public DbSet<Policy> Policies => Set<Policy>();

    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();

    public DbSet<PolicyAssignment> PolicyAssignments => Set<PolicyAssignment>();

    public DbSet<PolicyComplianceResult> PolicyComplianceResults => Set<PolicyComplianceResult>();

    /// <remarks>
    /// There is no membership set: a device's group is <c>Device.DeviceGroupId</c>.
    /// </remarks>
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();

    public DbSet<DeviceTask> DeviceTasks => Set<DeviceTask>();

    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();

    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();

    /// <summary>
    /// Append-only. The runtime database role holds INSERT and SELECT here and
    /// nothing else; a database trigger rejects UPDATE and DELETE regardless of
    /// role. <c>AuditImmutabilityInterceptor</c> additionally fails fast in-process
    /// so a bug surfaces as a clear exception rather than a database error.
    /// </summary>
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        // Picks up every IEntityTypeConfiguration in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EndpointPlatformDbContext).Assembly);

        // Must run last: it rewrites names produced by the configurations above.
        SnakeCaseNamingConvention.ApplyTo(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }
}
