using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>Fleet security overview: per-device compliance scores and a summary.</summary>
public sealed class SecurityReadService(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    /// <param name="scopedDeviceIds">
    /// The devices the caller may see, or null for all of them -- the value
    /// <c>DeviceScopeAuthorizer.ScopedDeviceIdsOrNullAsync</c> returns. Required, not
    /// defaulted: every row names a device, so a caller that forgot to pass scope
    /// would hand a group-scoped administrator the whole estate's hostnames. Applied
    /// in SQL, before anything is materialised or summarised, so the summary counts
    /// describe the caller's devices only.
    /// </param>
    public async Task<SecurityOverview> GetOverviewAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var query =
            from p in _dbContext.DeviceSecurityPosture.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on p.DeviceId equals d.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            select new { d.Id, d.Hostname, Posture = p };

        // Materialised first: EF translates Contains on a List, not on every
        // IReadOnlyCollection a caller might hand over.
        if (scopedDeviceIds?.ToList() is { } visible)
        {
            query = query.Where(x => visible.Contains(x.Id));
        }

        var rows = await query.ToListAsync(cancellationToken);

        var devices = rows
            .Select(r => new DeviceSecuritySummary(
                r.Id,
                r.Hostname,
                r.Posture.ComplianceScore(),
                r.Posture.DefenderAntivirusEnabled,
                FirewallOn(r.Posture),
                r.Posture.SecureBootEnabled,
                r.Posture.TpmEnabled,
                r.Posture.BitLockerSystemDriveStatus,
                r.Posture.LocalAdministratorCount,
                r.Posture.CollectedAt))
            .OrderBy(d => d.ComplianceScore ?? 999)
            .ThenBy(d => d.Hostname)
            .ToList();

        var scored = devices.Where(d => d.ComplianceScore.HasValue).Select(d => d.ComplianceScore!.Value).ToList();

        var summary = new SecuritySummary(
            devices.Count,
            scored.Count == 0 ? null : (int)Math.Round(scored.Average()),
            devices.Count(d => d.ComplianceScore >= 80),
            devices.Count(d => d.ComplianceScore is >= 50 and < 80),
            devices.Count(d => d.ComplianceScore < 50));

        return new SecurityOverview(summary, devices);
    }

    private static bool? FirewallOn(DeviceSecurityPosture p)
    {
        // "All known profiles on" - null if none were readable.
        var known = new[] { p.FirewallDomainEnabled, p.FirewallPrivateEnabled, p.FirewallPublicEnabled }
            .Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return known.Count == 0 ? null : known.All(x => x);
    }
}

public sealed record DeviceSecuritySummary(
    Guid DeviceId, string Hostname, int? ComplianceScore,
    bool? DefenderEnabled, bool? FirewallEnabled, bool? SecureBootEnabled,
    bool? TpmEnabled, string? BitLockerSystemDriveStatus, int? LocalAdministratorCount,
    DateTimeOffset CollectedAt);

public sealed record SecuritySummary(
    int DevicesReporting, int? AverageScore, int Healthy, int NeedsAttention, int Critical);

public sealed record SecurityOverview(SecuritySummary Summary, IReadOnlyList<DeviceSecuritySummary> Devices);
