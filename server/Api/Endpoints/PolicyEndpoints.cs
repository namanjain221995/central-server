using System.Text.Json;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Policies;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Policy management: create, version, assign, view compliance.</summary>
/// <remarks>
/// Policies themselves are organization-wide definitions, but assigning one is a
/// change to the devices it lands on, and compliance results describe individual
/// devices. So assignment is device-scoped (to a device the caller may act on, or a
/// group the caller is scoped to) and compliance reads are narrowed to the caller's
/// devices. Out of scope answers 404, indistinguishable from a target that does not
/// exist, matching every other device route.
/// </remarks>
public static class PolicyEndpoints
{
    public static IEndpointRouteBuilder MapPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/policies");

        group.MapGet("/", ListAsync).WithName("ListPolicies").RequirePermission(Permissions.Policy.View);
        group.MapPost("/", CreateAsync).WithName("CreatePolicy").RequirePermission(Permissions.Policy.Create);
        group.MapPost("/{policyId:guid}/assign", AssignAsync).WithName("AssignPolicy").RequirePermission(Permissions.Policy.Assign);
        group.MapPost("/{policyId:guid}/assign-group", AssignGroupAsync).WithName("AssignPolicyToGroup").RequirePermission(Permissions.Policy.Assign);
        group.MapGet("/{policyId:guid}/compliance", ComplianceAsync).WithName("PolicyCompliance").RequirePermission(Permissions.Policy.View);

        return endpoints;
    }

    public sealed record CreatePolicyRequest(string Type, string Name, string Description, int MaxTimeoutSeconds);
    public sealed record AssignRequest(Guid DeviceId);

    private static async Task<IResult> ListAsync(
        EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var orgId = actor.OrganizationId;

        var policies = await dbContext.Policies.AsNoTracking()
            .Where(p => p.OrganizationId == orgId)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, Type = p.Type.ToString(), p.Name, p.Description, p.IsEnabled, p.CurrentVersionNumber })
            .ToListAsync(cancellationToken);

        // Attach compliance rollups, counted over the caller's devices only: the
        // counts are per-device results summed, and a scoped administrator's
        // totals must not describe machines they cannot see.
        var results = await ScopedComplianceResultsAsync(dbContext, scope, actor, cancellationToken);
        var rollups = await results
            .GroupBy(r => new { r.PolicyId, r.State })
            .Select(g => new { g.Key.PolicyId, g.Key.State, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var result = policies.Select(p => new
        {
            p.Id, p.Type, p.Name, p.Description, p.IsEnabled, p.CurrentVersionNumber,
            Compliant = rollups.Where(r => r.PolicyId == p.Id && r.State == PolicyComplianceState.Compliant).Sum(r => r.Count),
            NonCompliant = rollups.Where(r => r.PolicyId == p.Id && r.State == PolicyComplianceState.NonCompliant).Sum(r => r.Count),
            Unknown = rollups.Where(r => r.PolicyId == p.Id && r.State == PolicyComplianceState.Unknown).Sum(r => r.Count),
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreatePolicyRequest request, HttpContext httpContext,
        PolicyService policyService, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PolicyType>(request.Type, out var type))
        {
            return Results.Problem(title: "Unknown policy type.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200
            || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 512)
        {
            return Results.Problem(title: "Invalid policy name or description.", statusCode: StatusCodes.Status400BadRequest);
        }

        // v1: only ScreenLockTimeout, whose desired state is a max timeout.
        if (type != PolicyType.ScreenLockTimeout || request.MaxTimeoutSeconds is < 30 or > 86400)
        {
            return Results.Problem(title: "Invalid desired state for this policy type.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var desiredState = JsonSerializer.Serialize(new { maxTimeoutSeconds = request.MaxTimeoutSeconds });

        var policy = await policyService.CreateAsync(
            actor.OrganizationId, type, request.Name.Trim(), request.Description.Trim(), desiredState,
            actor.UserId, actor.Email, cancellationToken);

        return Results.Created($"/admin/v1/policies/{policy.Id}", new { policy.Id });
    }

    private static async Task<IResult> AssignAsync(
        Guid policyId, [FromBody] AssignRequest request, HttpContext httpContext,
        PolicyService policyService, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        // The target comes from the body rather than the route, but it is still one
        // device: an administrator scoped to a group must not push a policy onto a
        // machine outside it by naming its id. Covers existence in the organization.
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, request.DeviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var ok = await policyService.AssignToDeviceAsync(
            actor.OrganizationId, policyId, request.DeviceId, actor.UserId, actor.Email, cancellationToken);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    public sealed record AssignGroupRequest(Guid GroupId);

    private static async Task<IResult> AssignGroupAsync(
        Guid policyId, [FromBody] AssignGroupRequest request, HttpContext httpContext,
        EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var policy = await dbContext.Policies
            .SingleOrDefaultAsync(p => p.Id == policyId && p.OrganizationId == actor.OrganizationId, cancellationToken);

        // Authority over a group means authority over every member, which is what
        // CanActOnGroupAsync answers; it also answers false for a group that does
        // not exist in this organization, so unknown and out of scope are one 404.
        var groupInScope = await scope.CanActOnGroupAsync(
            actor.UserId, actor.OrganizationId, request.GroupId, cancellationToken);
        if (policy is null || !groupInScope)
        {
            return Results.NotFound();
        }

        var already = await dbContext.PolicyAssignments.AnyAsync(
            x => x.PolicyId == policyId && x.TargetType == PolicyAssignmentTarget.Group && x.TargetId == request.GroupId,
            cancellationToken);
        if (!already)
        {
            dbContext.PolicyAssignments.Add(new PolicyAssignment(
                actor.OrganizationId, policyId, PolicyAssignmentTarget.Group, request.GroupId));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ComplianceAsync(
        Guid policyId, EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        // One row per device: narrowed to the caller's devices, so a scoped
        // administrator never sees the hostname or state of a machine elsewhere.
        var results = await ScopedComplianceResultsAsync(dbContext, scope, actor, cancellationToken);

        var rows = await (
            from r in results
            join d in dbContext.Devices.AsNoTracking() on r.DeviceId equals d.Id
            where r.PolicyId == policyId
            orderby d.Hostname
            select new
            {
                r.DeviceId, d.Hostname, State = r.State.ToString(), r.PolicyVersionNumber, r.EvaluatedAt,
                Deviations = r.DeviationsJson,
            }).ToListAsync(cancellationToken);

        return Results.Ok(rows.Select(x => new
        {
            x.DeviceId, x.Hostname, x.State, x.PolicyVersionNumber, x.EvaluatedAt,
            Deviations = x.Deviations is null ? null : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(x.Deviations),
        }));
    }

    /// <summary>
    /// The organization's compliance results, narrowed to the devices the caller may
    /// see. Null scope means unrestricted, exactly as before.
    /// </summary>
    private static async Task<IQueryable<PolicyComplianceResult>> ScopedComplianceResultsAsync(
        EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope, AdminActor actor,
        CancellationToken cancellationToken)
    {
        var orgId = actor.OrganizationId;
        var query = dbContext.PolicyComplianceResults.AsNoTracking()
            .Where(r => r.OrganizationId == orgId);

        var visible = (await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, orgId, cancellationToken))?.ToList();
        if (visible is not null)
        {
            query = query.Where(r => visible.Contains(r.DeviceId));
        }

        return query;
    }
}
