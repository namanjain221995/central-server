using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The second device-scope pass: account creation's default scope, and the
/// routes the review of the first pass found still organization-wide -- the
/// security and update overviews, deployment cancel and list, the Company Portal
/// catalog, the audit log, the fleet report and the IT unlock code.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="DeviceScopeEnforcementTests"/>: one device in a group
/// the scoped administrator holds, one outside it, an IT Administrator scoped to
/// that group only, and the fixture's all-device IT Administrator as the baseline.
/// </para>
/// <para>
/// The fixture's own accounts all hold all-device scope, which is exactly why an
/// account created through the product with NO scope went unnoticed: the first
/// tests here create administrators through the real users endpoint instead.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeviceScopeFollowUpTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _groups = new(fixture);

    private sealed record Scenario(Guid InScope, Guid OutOfScope, Guid GroupId, Guid OtherGroupId, HttpClient Scoped, HttpClient Unscoped)
        : IDisposable
    {
        public void Dispose()
        {
            Scoped.Dispose();
            Unscoped.Dispose();
        }
    }

    /// <summary>
    /// An in-scope device in a fresh group, an out-of-scope device in another fresh
    /// group, an administrator scoped to the first group only, and the all-device
    /// IT Administrator.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var inScope = await _groups.SeedDeviceAsync(hostname: $"FIN-{Guid.NewGuid():N}"[..15]);
        var outOfScope = await _groups.SeedDeviceAsync(hostname: $"FOS-{Guid.NewGuid():N}"[..15]);

        var unscoped = await _groups.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);
        var groupId = await _groups.CreateGroupAsync(unscoped, GroupTestSupport.UniqueName("Mine"), inScope);
        var otherGroupId = await _groups.CreateGroupAsync(unscoped, GroupTestSupport.UniqueName("Theirs"), outOfScope);
        var scoped = await _groups.ScopedAdminAsync(groupId);

        return new Scenario(inScope, outOfScope, groupId, otherGroupId, scoped, unscoped);
    }

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HashSet<Guid> Ids(JsonElement array, string property) =>
        array.EnumerateArray()
            .Where(e => e.GetProperty(property).ValueKind != JsonValueKind.Null)
            .Select(e => e.GetProperty(property).GetGuid())
            .ToHashSet();

    private async Task<Guid> OrganizationIdAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
    }

    private static async Task ShouldBeScopeRefusalAsync(HttpResponseMessage response, string because)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, because);
        (await response.Content.ReadAsStringAsync()).ShouldContain("scope", Case.Insensitive, because);
    }

    // ======================================= R1 accounts created through the product

    /// <summary>
    /// Creates an administrator through POST /admin/v1/platform-users, then signs
    /// in with the password it returned.
    /// </summary>
    /// <remarks>
    /// The real path leaves the account owing a password change and an MFA
    /// enrolment, both separate flows tested elsewhere; they are completed in the
    /// database so this exercises the account's SCOPE and nothing else. Scope is
    /// never touched here -- it is whatever creation gave the account.
    /// </remarks>
    private async Task<(HttpClient Client, Guid UserId)> CreateThroughUsersEndpointAsync(string roleKey)
    {
        var email = $"created-{Guid.CreateVersion7():N}@test.local";

        using (var super = await _groups.ClientAsAsync(AdminApiPostgresFixture.SuperAdminEmail))
        {
            var response = await super.PostAsJsonAsync(
                Rel("/admin/v1/platform-users"), new { email, displayName = "Created " + roleKey, roleKey });
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var userId = body.GetProperty("userId").GetGuid();
            var password = body.GetProperty("password").GetString()!;

            await using (var db = _fixture.CreateDbContext())
            {
                var user = await db.PlatformUsers.SingleAsync(u => u.Id == userId);
                user.CompleteRequiredPasswordChange();
                AdminApiPostgresFixture.EnrolMfa(user);
                await db.SaveChangesAsync();
            }

            return (_fixture.CreateClientFor(await _fixture.SignInAsync(email, password)), userId);
        }
    }

    [Theory]
    [InlineData(SystemRoles.ItAdministrator)]
    [InlineData(SystemRoles.Helpdesk)]
    [InlineData(SystemRoles.Auditor)]
    public async Task An_administrator_created_through_the_product_sees_the_fleet(string roleKey)
    {
        var device = await _groups.SeedDeviceAsync(hostname: $"NEW-{Guid.NewGuid():N}"[..15]);

        var (client, userId) = await CreateThroughUsersEndpointAsync(roleKey);
        using (client)
        {
            // Before the fix only a new Super Administrator got any scope, and there is
            // no surface to grant it later: every other new account saw nothing.
            var page = await JsonAsync(await client.GetAsync(Rel("/admin/v1/devices?pageSize=200")));
            page.GetProperty("totalCount").GetInt32().ShouldBeGreaterThan(0, $"a new {roleKey} must not get an empty console");

            (await JsonAsync(await client.GetAsync(Rel($"/admin/v1/devices/{device}"))))
                .GetProperty("id").GetGuid().ShouldBe(device);

            (await JsonAsync(await client.GetAsync(Rel("/admin/v1/devices/counts"))))
                .GetProperty("total").GetInt32().ShouldBeGreaterThan(0);
        }

        await using var db = _fixture.CreateDbContext();
        (await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Id == userId)).HasAllDeviceScope
            .ShouldBeTrue("the role governs what; no where-restriction configured means every device");
        (await db.AdminDeviceScopes.AnyAsync(s => s.PlatformUserId == userId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_deliberately_scoped_administrator_stays_scoped()
    {
        using var s = await ArrangeAsync();

        // Scope rows and no flag: the configured restriction still holds, so the
        // creation default changed what an UNCONFIGURED account means and nothing else.
        var page = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/devices?pageSize=200")));
        Ids(page.GetProperty("items"), "id").ShouldBe(new[] { s.InScope }, ignoreOrder: true);
        (await s.Scoped.GetAsync(Rel($"/admin/v1/devices/{s.OutOfScope}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ================================================ R2 security and update overviews

    private async Task SeedPostureAndUpdatesAsync(params Guid[] deviceIds)
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        foreach (var deviceId in deviceIds)
        {
            var posture = new DeviceSecurityPosture(deviceId);
            posture.Apply(true, true, 1, true, true, true, true, true, true, "2.0", "On", 1, now);
            db.DeviceSecurityPosture.Add(posture);

            var status = new DeviceUpdateStatus(deviceId);
            status.Apply(rebootRequired: true, failedUpdateCount: 2, now);
            db.DeviceUpdateStatus.Add(status);
        }

        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("/admin/v1/security/overview")]
    [InlineData("/admin/v1/updates/overview")]
    public async Task Fleet_overviews_list_and_count_only_in_scope_devices(string path)
    {
        using var s = await ArrangeAsync();
        await SeedPostureAndUpdatesAsync(s.InScope, s.OutOfScope);

        var scoped = await JsonAsync(await s.Scoped.GetAsync(Rel(path)));
        Ids(scoped.GetProperty("devices"), "deviceId").ShouldBe(new[] { s.InScope }, ignoreOrder: true, customMessage: "no other group's id or hostname");
        scoped.GetProperty("summary").GetProperty("devicesReporting").GetInt32()
            .ShouldBe(1, "the summary must not reveal the rest of the estate");

        var all = await JsonAsync(await s.Unscoped.GetAsync(Rel(path)));
        var allIds = Ids(all.GetProperty("devices"), "deviceId");
        allIds.ShouldContain(s.InScope);
        allIds.ShouldContain(s.OutOfScope, "an all-device administrator is unchanged");
    }

    // ======================================================= R3 deployments

    private async Task<Guid> SeedPackageAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var user = await db.PlatformUsers.OrderBy(u => u.CreatedAt).FirstAsync();

        var package = new Domain.Software.SoftwarePackage(
            await OrganizationIdAsync(),
            $"Scope2 App {Guid.CreateVersion7():N}"[..24],
            "1.0.0",
            "Test Publisher",
            Domain.Software.SoftwarePackageType.WindowsInstaller,
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            "app.msi",
            2048,
            $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}",
            requiredSignerSubject: null,
            user.Id,
            user.Email);

        db.SoftwarePackages.Add(package);
        await db.SaveChangesAsync();
        return package.Id;
    }

    private static async Task<Guid> DeployAsync(HttpClient client, Guid packageId, params Guid[] deviceIds)
    {
        var response = await client.PostAsJsonAsync(
            Rel("/admin/v1/deployments"), new { packageId, deviceIds, groupIds = Array.Empty<Guid>() });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deploymentId").GetGuid();
    }

    private async Task<DeviceTaskStatus> InstallTaskStatusAsync(Guid deploymentId, Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await (
            from target in db.SoftwareDeploymentTargets
            join task in db.DeviceTasks on target.TaskId equals task.Id
            where target.DeploymentId == deploymentId && target.DeviceId == deviceId
            select task.Status).SingleAsync();
    }

    [Fact]
    public async Task Cancelling_a_deployment_cancels_only_in_scope_pending_installs()
    {
        using var s = await ArrangeAsync();
        var deploymentId = await DeployAsync(s.Unscoped, await SeedPackageAsync(), s.InScope, s.OutOfScope);

        var response = await s.Scoped.PostAsync(Rel($"/admin/v1/deployments/{deploymentId}/cancel"), null);
        var body = await JsonAsync(response);
        body.GetProperty("considered").GetInt32().ShouldBe(1, "the out-of-scope install is not even considered");
        body.GetProperty("cancelled").GetInt32().ShouldBe(1);

        (await InstallTaskStatusAsync(deploymentId, s.InScope)).ShouldBe(DeviceTaskStatus.Cancelled);
        (await InstallTaskStatusAsync(deploymentId, s.OutOfScope))
            .ShouldBe(DeviceTaskStatus.Queued, "another group's pending install must survive a scoped cancel");

        // An all-device administrator cancels everything still pending, as before.
        (await JsonAsync(await s.Unscoped.PostAsync(Rel($"/admin/v1/deployments/{deploymentId}/cancel"), null)))
            .GetProperty("cancelled").GetInt32().ShouldBe(1);
        (await InstallTaskStatusAsync(deploymentId, s.OutOfScope)).ShouldBe(DeviceTaskStatus.Cancelled);
    }

    [Fact]
    public async Task A_deployment_with_no_in_scope_target_is_not_found_and_not_listed()
    {
        using var s = await ArrangeAsync();
        var mixed = await DeployAsync(s.Unscoped, await SeedPackageAsync(), s.InScope, s.OutOfScope);
        var elsewhere = await DeployAsync(s.Unscoped, await SeedPackageAsync(), s.OutOfScope);

        (await s.Scoped.PostAsync(Rel($"/admin/v1/deployments/{elsewhere}/cancel"), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await InstallTaskStatusAsync(elsewhere, s.OutOfScope)).ShouldBe(DeviceTaskStatus.Queued, "a 404 must change nothing");

        (await s.Scoped.GetAsync(Rel($"/admin/v1/deployments/{elsewhere}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await s.Scoped.PostAsync(Rel($"/admin/v1/deployments/{elsewhere}/retry"), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The list is filtered before counting and paging: this administrator's
        // group holds one device, and only one deployment ever touched it.
        var page = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/deployments?pageSize=100")));
        Ids(page.GetProperty("items"), "id").ShouldBe(new[] { mixed }, ignoreOrder: true);
        page.GetProperty("totalCount").GetInt32().ShouldBe(1, "the total must not count other groups' deployments");

        var all = Ids((await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/deployments?pageSize=100"))))
            .GetProperty("items"), "id");
        all.ShouldContain(mixed);
        all.ShouldContain(elsewhere, "an all-device administrator is unchanged");
    }

    // ======================================================== R4 company portal

    private static object CatalogBody(Guid packageId, string visibility, params Guid[] groupIds) => new
    {
        packageId,
        displayName = "Scope App",
        description = (string?)null,
        category = (string?)null,
        visibility,
        groupIds,
        publish = false,
    };

    private async Task<int> CatalogAppsForAsync(Guid packageId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CatalogApps.CountAsync(a => a.PackageId == packageId);
    }

    private static async Task<Guid> CreateCatalogAppAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Rel("/admin/v1/catalog"), body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_scoped_administrator_can_only_offer_an_app_to_their_own_groups()
    {
        using var s = await ArrangeAsync();

        var fleetWide = await SeedPackageAsync();
        await ShouldBeScopeRefusalAsync(
            await s.Scoped.PostAsJsonAsync(Rel("/admin/v1/catalog"), CatalogBody(fleetWide, "AllDevices")),
            "all devices is every device, which a scoped administrator does not hold");
        (await CatalogAppsForAsync(fleetWide)).ShouldBe(0);

        var otherGroup = await SeedPackageAsync();
        await ShouldBeScopeRefusalAsync(
            await s.Scoped.PostAsJsonAsync(Rel("/admin/v1/catalog"), CatalogBody(otherGroup, "SelectedGroups", s.OtherGroupId)),
            "another group's devices are not theirs to offer an install to");
        await ShouldBeScopeRefusalAsync(
            await s.Scoped.PostAsJsonAsync(Rel("/admin/v1/catalog"), CatalogBody(otherGroup, "SelectedGroups", s.GroupId, s.OtherGroupId)),
            "every named group must be in scope, not just one");
        (await CatalogAppsForAsync(otherGroup)).ShouldBe(0);

        // Their own group works end to end.
        var mine = await CreateCatalogAppAsync(s.Scoped, CatalogBody(await SeedPackageAsync(), "SelectedGroups", s.GroupId));
        (await s.Scoped.PostAsync(Rel($"/admin/v1/catalog/{mine}/publish"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Scoped.PostAsync(Rel($"/admin/v1/catalog/{mine}/unpublish"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Scoped.DeleteAsync(Rel($"/admin/v1/catalog/{mine}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // An all-device administrator is unchanged.
        await CreateCatalogAppAsync(s.Unscoped, CatalogBody(fleetWide, "AllDevices"));
    }

    [Fact]
    public async Task A_scoped_administrator_cannot_change_an_app_that_reaches_beyond_their_groups()
    {
        using var s = await ArrangeAsync();

        var fleetWidePackage = await SeedPackageAsync();
        var fleetWide = await CreateCatalogAppAsync(s.Unscoped, CatalogBody(fleetWidePackage, "AllDevices"));
        var sharedPackage = await SeedPackageAsync();
        var shared = await CreateCatalogAppAsync(s.Unscoped, CatalogBody(sharedPackage, "SelectedGroups", s.GroupId, s.OtherGroupId));

        foreach (var app in new[] { fleetWide, shared })
        {
            await ShouldBeScopeRefusalAsync(
                await s.Scoped.PostAsync(Rel($"/admin/v1/catalog/{app}/publish"), null), "publishing pre-approves installs beyond their scope");
            await ShouldBeScopeRefusalAsync(
                await s.Scoped.PostAsync(Rel($"/admin/v1/catalog/{app}/unpublish"), null), "unpublishing withdraws it from other groups");
            await ShouldBeScopeRefusalAsync(
                await s.Scoped.DeleteAsync(Rel($"/admin/v1/catalog/{app}")), "deleting removes it from other groups");
        }

        // Narrowing the shared entry to their own group would withdraw it from the
        // other group's devices just as surely as unpublishing it.
        await ShouldBeScopeRefusalAsync(
            await s.Scoped.PutAsJsonAsync(Rel($"/admin/v1/catalog/{shared}"), CatalogBody(sharedPackage, "SelectedGroups", s.GroupId)),
            "an update must not reach outside scope at either end");

        await using (var db = _fixture.CreateDbContext())
        {
            var apps = await db.CatalogApps.AsNoTracking().Where(a => a.Id == fleetWide || a.Id == shared).ToListAsync();
            apps.Count.ShouldBe(2, "nothing was deleted");
            apps.ShouldAllBe(a => !a.IsPublished, "nothing was published");
            (await db.CatalogAppGroups.CountAsync(g => g.CatalogAppId == shared)).ShouldBe(2, "nothing was narrowed");
        }

        (await s.Unscoped.PostAsync(Rel($"/admin/v1/catalog/{fleetWide}/publish"), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "an all-device administrator is unchanged");
    }

    // ============================================================= R5 audit log

    [Fact]
    public async Task The_audit_log_leaves_out_other_groups_devices()
    {
        using var s = await ArrangeAsync();
        static JsonContent Name(string value) => JsonContent.Create(new { displayName = value });

        // In scope first, out of scope LAST: the newest row is the one a scoped
        // page of one must skip, which proves the filter runs before the limit.
        (await s.Unscoped.PatchAsync(Rel($"/admin/v1/devices/{s.InScope}/display-name"), Name("Audit-In")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Unscoped.PatchAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/display-name"), Name("Audit-Out")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var page = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/audit-log?limit=200")));
        var deviceIds = Ids(page, "deviceId");
        deviceIds.ShouldNotContain(s.OutOfScope);
        deviceIds.ShouldContain(s.InScope);
        page.EnumerateArray().ShouldContain(
            e => e.GetProperty("deviceId").ValueKind == JsonValueKind.Null,
            "rows naming no device describe the console, not the estate, and stay visible");

        var newest = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/audit-log?limit=1")));
        newest.GetArrayLength().ShouldBe(1, "a scoped page is still a full page of the caller's own rows");
        Ids(newest, "deviceId").ShouldNotContain(s.OutOfScope);

        // A device's own history: out of scope is 404, in scope works.
        (await s.Scoped.GetAsync(Rel($"/admin/v1/audit-log?deviceId={s.OutOfScope}")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await s.Scoped.GetAsync(Rel($"/admin/v1/audit-log?deviceId={Guid.CreateVersion7()}")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "unknown and out of scope are the same answer");
        Ids(await JsonAsync(await s.Scoped.GetAsync(Rel($"/admin/v1/audit-log?deviceId={s.InScope}"))), "deviceId")
            .ShouldBe(new[] { s.InScope }, ignoreOrder: true);

        // An all-device administrator is unchanged.
        Ids(await JsonAsync(await s.Unscoped.GetAsync(Rel($"/admin/v1/audit-log?deviceId={s.OutOfScope}"))), "deviceId")
            .ShouldBe(new[] { s.OutOfScope }, ignoreOrder: true);
    }

    // ============================================================ R6 fleet report

    [Fact]
    public async Task The_fleet_report_counts_only_in_scope_devices()
    {
        using var s = await ArrangeAsync();
        await SeedPostureAndUpdatesAsync(s.InScope, s.OutOfScope);

        (await s.Unscoped.PostAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/actions/lock"), null)).EnsureSuccessStatusCode();
        (await s.Unscoped.PostAsync(Rel($"/admin/v1/devices/{s.InScope}/actions/lock"), null)).EnsureSuccessStatusCode();

        Guid policyId;
        using (var super = await _groups.ClientAsAsync(AdminApiPostgresFixture.SuperAdminEmail))
        {
            policyId = (await JsonAsync(await super.PostAsJsonAsync(Rel("/admin/v1/policies"), new
            {
                type = "ScreenLockTimeout",
                name = GroupTestSupport.UniqueName("Report-Policy"),
                description = "Fleet report scope test.",
                maxTimeoutSeconds = 600,
            }))).GetProperty("id").GetGuid();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var result = new PolicyComplianceResult(await OrganizationIdAsync(), s.OutOfScope, policyId);
            result.Record(
                await db.PolicyVersions.Where(v => v.PolicyId == policyId).Select(v => v.Id).FirstAsync(),
                1, PolicyComplianceState.NonCompliant, null, DateTimeOffset.UtcNow);
            db.PolicyComplianceResults.Add(result);
            await db.SaveChangesAsync();

            (await db.PolicyComplianceResults.CountAsync(r => r.State == PolicyComplianceState.NonCompliant))
                .ShouldBeGreaterThan(0, "the organization does have a NonCompliant result to leak");
        }

        int inScopeTasks;
        await using (var db = _fixture.CreateDbContext())
        {
            inScopeTasks = await db.DeviceTasks.CountAsync(t => t.DeviceId == s.InScope);
        }

        var report = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/reports/summary")));
        report.GetProperty("devices").GetProperty("total").GetInt32().ShouldBe(1);
        report.GetProperty("security").GetProperty("devicesReporting").GetInt32().ShouldBe(1);
        report.GetProperty("updates").GetProperty("devicesReporting").GetInt32().ShouldBe(1);
        report.GetProperty("policies").GetProperty("nonCompliantResults").GetInt32()
            .ShouldBe(0, "the only NonCompliant result is another group's");

        var tasks = report.GetProperty("tasks");
        (tasks.GetProperty("queued").GetInt32() + tasks.GetProperty("delivered").GetInt32()
         + tasks.GetProperty("succeeded").GetInt32() + tasks.GetProperty("failed").GetInt32()
         + tasks.GetProperty("expired").GetInt32() + tasks.GetProperty("cancelled").GetInt32())
            .ShouldBe(inScopeTasks, "task throughput counts only work on the caller's devices");

        var all = await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/reports/summary")));
        all.GetProperty("devices").GetProperty("total").GetInt32()
            .ShouldBeGreaterThanOrEqualTo(2, "an all-device administrator is unchanged");
    }

    // ===================================================== R7 the IT unlock code

    [Fact]
    public async Task Only_an_all_device_administrator_may_change_the_unlock_code()
    {
        using var s = await ArrangeAsync();

        var before = await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/settings/screen-unlock-code")));

        await ShouldBeScopeRefusalAsync(
            await s.Scoped.PutAsJsonAsync(Rel("/admin/v1/settings/screen-unlock-code"), new { code = "scoped-override-1234" }),
            "every later lock in every group captures this code");

        // Unchanged, and still readable by the scoped administrator.
        var after = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/settings/screen-unlock-code")));
        after.GetProperty("isSet").GetBoolean().ShouldBe(before.GetProperty("isSet").GetBoolean());
        after.GetProperty("updatedAt").ToString().ShouldBe(before.GetProperty("updatedAt").ToString());
    }
}
