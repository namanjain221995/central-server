using EndpointPlatform.Domain.Authorization;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// What each built-in access level can and cannot do.
/// </summary>
/// <remarks>
/// <para>
/// Served from the compiled catalogue in <see cref="Permissions"/> and
/// <see cref="SystemRoles"/>, not from the database. The roles table reconciles
/// permission GRANTS on every deployment but never updates a role's display name or
/// description after it is first created, so database text can silently disagree
/// with the code while the ticks beside it are current — a mismatch nobody would
/// notice because both halves look authoritative.
/// </para>
/// <para>
/// Authenticated, but NOT gated on <see cref="Permissions.Platform.UserView"/>.
/// That permission is held by Super Administrator and Auditor only, so gating this
/// on it would stop an IT Administrator — the most senior operational role — from
/// reading what their own access level grants them. This endpoint discloses no
/// tenant data: it is the product's own permission model, which is the same for
/// every deployment and is published in the repository.
/// </para>
/// <para>
/// The response is fully ordered and pre-grouped by the server. Neither frozen
/// collection documents an enumeration order, and the browser must not invent its
/// own grouping — grouping on a key prefix rather than the declared category would,
/// for one real example, split <c>localuser.elevate</c> away from the seven
/// <c>user.*</c> permissions it belongs with, hiding the single most policy-laden
/// row in that section.
/// </para>
/// </remarks>
public static class AccessLevelEndpoints
{
    public static IEndpointRouteBuilder MapAccessLevelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/access-levels", Get)
            .WithName("GetAccessLevels")
            .RequireAuthorization();

        return endpoints;
    }

    /// <param name="Key">The stable identifier, used when creating an administrator.</param>
    /// <param name="HoldsEveryPermission">
    /// True for Super Administrator, whose permissions are computed as "the whole
    /// catalogue" rather than listed. The console states this rather than rendering
    /// a list of ticks that would silently become incomplete when a permission is
    /// added.
    /// </param>
    public sealed record AccessLevel(
        string Key,
        string DisplayName,
        string Description,
        bool HoldsEveryPermission,
        int GrantedCount,
        int DeniedCount,
        IReadOnlyList<string> PermissionKeys);

    public sealed record PermissionEntry(string Key, string Description, bool HighRisk);

    public sealed record PermissionCategory(string Name, IReadOnlyList<PermissionEntry> Permissions);

    public sealed record AccessLevelsResponse(
        IReadOnlyList<AccessLevel> AccessLevels,
        IReadOnlyList<PermissionCategory> Categories,
        int TotalPermissions);

    /// <summary>
    /// Privilege order, most to least. The only order in which the checklist reads
    /// as a story rather than as an arbitrary table.
    /// </summary>
    private static readonly string[] RoleOrder =
    [
        SystemRoles.SuperAdministrator,
        SystemRoles.ItAdministrator,
        SystemRoles.Helpdesk,
        SystemRoles.Auditor,
    ];

    /// <summary>
    /// Category order, roughly "what an operator touches most often" first, so the
    /// table does not open on platform administration.
    /// </summary>
    private static readonly string[] CategoryOrder =
    [
        "Devices",
        "Local accounts",
        "Groups",
        "Software",
        "Chrome",
        "Policies",
        "Peripherals",
        "Drivers",
        "BitLocker",
        "Tasks",
        "Audit",
        "Platform",
    ];

    private static IResult Get()
    {
        var total = Permissions.All.Count;

        var levels = RoleOrder
            .Where(SystemRoles.All.ContainsKey)
            .Select(key => SystemRoles.All[key])
            .Select(role =>
            {
                var granted = role.PermissionKeys.Count;

                return new AccessLevel(
                    role.Key,
                    role.DisplayName,
                    role.Description,
                    HoldsEveryPermission: granted == total,
                    GrantedCount: granted,
                    DeniedCount: total - granted,
                    // Ordinal, so the browser can build a set from it deterministically.
                    PermissionKeys: role.PermissionKeys.Order(StringComparer.Ordinal).ToArray());
            })
            .ToArray();

        var categories = Permissions.All
            .GroupBy(p => p.Category, StringComparer.Ordinal)
            .Select(group => new PermissionCategory(
                group.Key,
                group
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new PermissionEntry(p.Key, p.Description, p.HighRisk))
                    .ToArray()))
            // A category the catalogue gains but CategoryOrder has not learned about
            // sorts last rather than disappearing.
            .OrderBy(c => IndexOrLast(CategoryOrder, c.Name))
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToArray();

        return Results.Ok(new AccessLevelsResponse(levels, categories, total));
    }

    private static int IndexOrLast(string[] order, string value)
    {
        var index = Array.IndexOf(order, value);
        return index < 0 ? order.Length : index;
    }
}
