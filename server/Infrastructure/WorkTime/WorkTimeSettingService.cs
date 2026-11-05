using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.WorkTime;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.WorkTime;

/// <summary>
/// Reads and writes one organization's work-time configuration (shift window, idle
/// threshold, break, UTC offset). One row per organization, created on first save.
/// </summary>
public sealed class WorkTimeSettingService(EndpointPlatformDbContext dbContext, AuditWriter auditWriter)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;

    /// <summary>
    /// The organization's setting, or a transient default (not persisted) if none has
    /// been saved — so a report always has a shift to compute against.
    /// </summary>
    public async Task<WorkTimeSetting> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.WorkTimeSettings.AsNoTracking()
                   .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken)
               ?? new WorkTimeSetting(organizationId);
    }

    public async Task SetAsync(
        Guid organizationId,
        Guid actorId,
        string actorDisplay,
        bool enabled,
        int shiftStartMinutes,
        int shiftEndMinutes,
        int idleThresholdMinutes,
        int breakMinutes,
        int utcOffsetMinutes,
        CancellationToken cancellationToken = default)
    {
        var setting = await _dbContext.WorkTimeSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);

        var previous = setting is null ? null : Snapshot(setting);

        if (setting is null)
        {
            setting = new WorkTimeSetting(organizationId);
            _dbContext.WorkTimeSettings.Add(setting);
        }

        // Validates ranges and the non-zero shift length; throws on bad input.
        setting.Update(enabled, shiftStartMinutes, shiftEndMinutes, idleThresholdMinutes, breakMinutes, utcOffsetMinutes);

        _auditWriter.Stage(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            "worktime.settings.update", AuditResult.Success,
            a => a.Requiring(Permissions.Activity.Configure).WithStateChange(previous, Snapshot(setting)));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Snapshot(WorkTimeSetting s) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            s.Enabled,
            s.ShiftStartMinutes,
            s.ShiftEndMinutes,
            s.IdleThresholdMinutes,
            s.BreakMinutes,
            s.UtcOffsetMinutes,
        });
}
