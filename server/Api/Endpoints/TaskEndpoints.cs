using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Fleet-wide task history: what has been queued across every device, what state
/// each task is in, and what came back.
/// </summary>
/// <remarks>
/// <para>
/// The per-device list under <c>/devices/{id}/tasks</c> answers "what happened
/// to this machine"; this one answers "what is the platform doing right now" —
/// the view an administrator needs when they queued work against several
/// machines, or when an auditor wants recent remote actions without walking
/// device by device.
/// </para>
/// <para>
/// Read-only and payload-free: the projection carries type, state, actor and
/// result message, never <c>PayloadJson</c>. Payloads are administrator input
/// (service names, SIDs, secret references) that the history view does not need,
/// and the cheapest data to keep out of a broad read surface is data it never
/// returns.
/// </para>
/// </remarks>
public static class TaskEndpoints
{
    /// <summary>Page-size ceiling. The sort index makes this a bounded, cheap query at 200 endpoints.</summary>
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/admin/v1/tasks")
            .MapGet("/", ListAsync)
            .WithName("ListTasks")
            .RequirePermission(Domain.Authorization.Permissions.Task.View);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50,
        string? status = null)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = dbContext.DeviceTasks
            .AsNoTracking()
            .Where(t => t.OrganizationId == organizationId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            // An unrecognised filter is a 400, not an empty page: silence here
            // would read as "no tasks in that state", which is a claim.
            if (!Enum.TryParse<DeviceTaskStatus>(status, ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    title: $"Unknown task status '{status}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            query = query.Where(t => t.Status == parsed);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Join(
                dbContext.Devices.AsNoTracking(),
                t => t.DeviceId,
                d => d.Id,
                (t, d) => new
                {
                    t.Id,
                    t.DeviceId,
                    // Both names, same contract as the device list: the label
                    // leads, the hostname still identifies the machine.
                    DeviceHostname = d.Hostname,
                    DeviceDisplayName = d.DisplayName,
                    Type = t.Type.ToString(),
                    Status = t.Status.ToString(),
                    t.CreatedByDisplay,
                    t.CreatedAt,
                    t.DeliveredAt,
                    t.CompletedAt,
                    t.ResultMessage,
                    // The agent's structured result, never the payload: for a
                    // restart it says when Windows acts, which is what decides
                    // whether the console still offers Cancel.
                    t.ResultJson,
                })
            // Re-asserted after the join: SQL only promises the pre-join Skip/Take
            // picked the right page, not that the joined rows come back in order.
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(new { items, totalCount, page, pageSize });
    }
}
