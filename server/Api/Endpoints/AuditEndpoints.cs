using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Read access to the audit log (<see cref="Permissions.Audit.View"/>), newest first.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by construction: the log is append-only in the database (ADR-0004), and
/// nothing here could change that even if it tried. Paging is by time rather than by
/// offset — <c>before</c> is the <c>occurredAt</c> of the last row already shown — so a
/// page stays stable while new events arrive at the top.
/// </para>
/// <para>
/// Before and after states, source address and user agent stay out of the list: the
/// list says who did what to what, and when. They remain in the table for an
/// investigation that queries it directly.
/// </para>
/// </remarks>
public static class AuditEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/audit-log", ListAsync)
            .WithName("ListAuditLog")
            .RequirePermission(Permissions.Audit.View);

        return endpoints;
    }

    /// <remarks>
    /// Scoped for an administrator without all-device scope: rows about a device
    /// outside their groups are left out (before the page is cut, so a scoped page
    /// is still a full page of their own), while rows naming no device -- sign-ins,
    /// account and catalog changes -- remain, as they describe the console rather
    /// than the estate. Asking for an out-of-scope device's history is 404, the
    /// same answer as for a device that does not exist. An all-device
    /// administrator sees exactly what they saw before.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        EndpointPlatformDbContext dbContext,
        DeviceScopeAuthorizer scope,
        CancellationToken cancellationToken,
        int limit = 50,
        DateTimeOffset? before = null,
        Guid? deviceId = null)
    {
        var actor = AdminActor.Required(httpContext.User);
        var organizationId = actor.OrganizationId;

        // Materialised first: EF translates Contains on a List, not on every
        // IReadOnlyCollection the authorizer might hand over.
        var visible = (await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, organizationId, cancellationToken))
            ?.ToList();

        if (deviceId is { } requested && visible is not null && !visible.Contains(requested))
        {
            return Results.NotFound();
        }

        var query = dbContext.AuditLogEntries
            .AsNoTracking()
            .Where(a => a.OrganizationId == organizationId);

        if (visible is not null)
        {
            query = query.Where(a => a.DeviceId == null || visible.Contains(a.DeviceId.Value));
        }

        if (before is { } cursor)
        {
            query = query.Where(a => a.OccurredAt < cursor);
        }

        if (deviceId is { } device)
        {
            query = query.Where(a => a.DeviceId == device);
        }

        var rows = await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .Select(a => new AuditLogEntryView(
                a.Id,
                a.OccurredAt,
                a.ActorType.ToString(),
                a.ActorDisplay,
                a.Action,
                a.Result.ToString(),
                a.DeviceId,
                a.DeviceDisplay,
                a.TargetType,
                a.TargetDisplay,
                a.FailureReason))
            .ToListAsync(cancellationToken);

        return Results.Ok(rows);
    }
}

public sealed record AuditLogEntryView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string ActorType,
    string ActorDisplay,
    string Action,
    string Result,
    Guid? DeviceId,
    string? DeviceDisplay,
    string? TargetType,
    string? TargetDisplay,
    string? FailureReason);
