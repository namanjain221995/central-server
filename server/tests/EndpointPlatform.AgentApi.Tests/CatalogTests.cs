using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The device's side of the Company Portal over real HTTP + PostgreSQL: what a
/// machine may see, and what happens when its user asks for something.
/// </summary>
/// <remarks>
/// The device is the only identity. It authenticated with its credential, its
/// group decides what it may see, and a request from it can only name a catalog
/// app id; the reported user name is kept for the record and decides nothing.
/// Refusals are uniform on purpose, so the assertions about them are about what
/// was <em>not</em> created as much as about the answer.
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class CatalogTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    /// <summary>Mirrors <c>SelfServiceCatalogService.MaxRequestsPerMinute</c>, which is internal.</summary>
    private const int MaxRequestsPerMinute = 10;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static string CatalogRoute => AgentProtocol.RoutePrefix + AgentProtocol.Routes.Catalog;

    private static string IconRoute(Guid appId) =>
        $"{CatalogRoute}/{appId}{AgentProtocol.Routes.CatalogIconSuffix}";

    private static string InstallRoute(Guid appId) =>
        $"{CatalogRoute}/{appId}{AgentProtocol.Routes.CatalogInstallSuffix}";

    private static string Unique(string prefix) => $"{prefix} {Guid.CreateVersion7():N}"[..Math.Min(40, prefix.Length + 33)];

    private static byte[] TinyPng() => [.. PngSignature, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    // ------------------------------------------------------------ wire

    private static HttpRequestMessage Request(
        HttpMethod method, string route, object? body = null, string? credential = null, int? protocolVersion = null)
    {
        var message = new HttpRequestMessage(method, new Uri(route, UriKind.Relative));

        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        message.Headers.Add(
            AgentProtocol.Headers.ProtocolVersion,
            (protocolVersion ?? AgentProtocol.Version).ToString());

        if (credential is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        }

        return message;
    }

    private async Task<(Guid DeviceId, string Credential, Guid OrgId)> EnrollAsync(string hostname)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();

        db.EnrollmentTokens.Add(new EnrollmentToken(
            org.Id, $"cat-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(Request(
            HttpMethod.Post, AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, hostname, $"machine-{Guid.CreateVersion7():N}", "1.16.1", "Windows 11 Pro")));

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}", org.Id);
    }

    private async Task<CatalogListResponse> ListAsync(string credential)
    {
        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(Request(HttpMethod.Get, CatalogRoute, credential: credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CatalogListResponse>())!;
    }

    private async Task<CatalogInstallResponse> RequestInstallAsync(string credential, Guid appId, string? requestedBy)
    {
        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(Request(
            HttpMethod.Post, InstallRoute(appId), new CatalogInstallCommand(requestedBy), credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "every decision, including a refusal, is a normal answer");
        return (await response.Content.ReadFromJsonAsync<CatalogInstallResponse>())!;
    }

    // ------------------------------------------------------------ seed

    private async Task<SoftwarePackage> SeedPackageAsync(
        Guid orgId, string name, string version = "1.0.0", bool withdrawn = false)
    {
        await using var db = _fixture.CreateDbContext();
        var sha = Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray()));
        var package = new SoftwarePackage(
            orgId, name, version, "Contoso", SoftwarePackageType.WindowsInstaller, sha, "app.msi", 4096,
            $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}", "CN=Contoso", Guid.CreateVersion7(), "admin@test");

        if (withdrawn)
        {
            package.Withdraw(DateTimeOffset.UtcNow);
        }

        db.SoftwarePackages.Add(package);
        await db.SaveChangesAsync();
        return package;
    }

    private async Task<CatalogApp> SeedAppAsync(
        Guid orgId,
        Guid packageId,
        string displayName,
        bool published = true,
        CatalogVisibility visibility = CatalogVisibility.AllDevices,
        IReadOnlyCollection<Guid>? groupIds = null,
        byte[]? icon = null)
    {
        await using var db = _fixture.CreateDbContext();
        var app = new CatalogApp(
            orgId, packageId, displayName, "Seeded by CatalogTests", "Tools", visibility,
            Guid.CreateVersion7(), "creator@test");

        if (published)
        {
            app.Publish(Guid.CreateVersion7(), "publisher@test");
        }

        if (icon is not null)
        {
            app.SetIcon(icon);
        }

        db.CatalogApps.Add(app);
        foreach (var groupId in groupIds ?? [])
        {
            db.CatalogAppGroups.Add(new CatalogAppGroup(app.Id, groupId));
        }

        await db.SaveChangesAsync();
        return app;
    }

    /// <summary>A published, everyone-visible app over a fresh package.</summary>
    private async Task<(SoftwarePackage Package, CatalogApp App)> SeedOfferedAsync(Guid orgId, string name)
    {
        var package = await SeedPackageAsync(orgId, name);
        var app = await SeedAppAsync(orgId, package.Id, name);
        return (package, app);
    }

    private async Task<Guid> SeedGroupAsync(Guid orgId, string prefix)
    {
        await using var db = _fixture.CreateDbContext();
        var group = new DeviceGroup(orgId, $"{prefix}-{Guid.CreateVersion7():N}"[..Math.Min(40, prefix.Length + 33)], null, DeviceGroupType.Static);
        db.DeviceGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task MoveToGroupAsync(Guid deviceId, Guid groupId)
    {
        await using var db = _fixture.CreateDbContext();
        var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
        device.MoveToGroup(groupId);
        await db.SaveChangesAsync();
    }

    /// <summary>The device reports the package's product as installed, by product code.</summary>
    private async Task ReportInstalledAsync(Guid deviceId, SoftwarePackage package, string? version = null)
    {
        await using var db = _fixture.CreateDbContext();
        db.DeviceSoftware.Add(new DeviceSoftware(
            deviceId, package.Name, version ?? package.Version, package.Publisher, null, null, "x64",
            DateTimeOffset.UtcNow, "Machine", null, package.MsiProductCode));
        await db.SaveChangesAsync();
    }

    private async Task<List<CatalogInstallRequest>> RequestsOfAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CatalogInstallRequests.AsNoTracking()
            .Where(r => r.DeviceId == deviceId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
    }

    private async Task<int> InstallTasksOfAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.InstallPackage);
    }

    // ------------------------------------------------------------ list

    [Fact]
    public async Task The_catalog_lists_only_published_apps_the_device_may_see_with_their_state_on_it()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-LIST-1");
        var (otherDeviceId, otherCredential, _) = await EnrollAsync("CAT-LIST-2");
        var sales = await SeedGroupAsync(orgId, "Sales");
        await MoveToGroupAsync(deviceId, sales);

        var (everyonePackage, everyone) = await SeedOfferedAsync(orgId, Unique("Everyone"));
        var departmental = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Departmental"))).Id, Unique("Departmental"),
            visibility: CatalogVisibility.SelectedGroups, groupIds: [sales]);
        var draft = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Draft"))).Id, Unique("Draft"), published: false);
        var withdrawn = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Withdrawn"), withdrawn: true)).Id, Unique("Withdrawn"));

        var (installedPackage, installed) = await SeedOfferedAsync(orgId, Unique("Installed"));
        await ReportInstalledAsync(deviceId, installedPackage);

        var outdatedPackage = await SeedPackageAsync(orgId, Unique("Outdated"), version: "2.0.0");
        var outdated = await SeedAppAsync(orgId, outdatedPackage.Id, Unique("Outdated"));
        await ReportInstalledAsync(deviceId, outdatedPackage, version: "1.0.0");

        var mine = (await ListAsync(credential)).Apps.ToDictionary(a => a.Id);

        mine.ShouldContainKey(everyone.Id);
        var tile = mine[everyone.Id];
        tile.DisplayName.ShouldBe(everyone.DisplayName);
        tile.Description.ShouldBe("Seeded by CatalogTests");
        tile.Category.ShouldBe("Tools");
        tile.HasIcon.ShouldBeFalse();
        tile.Version.ShouldBe(everyonePackage.Version);
        tile.Publisher.ShouldBe("Contoso");
        tile.State.ShouldBe("Available");

        mine.ShouldContainKey(departmental.Id, "the device is in the one group the app is offered to");
        mine[departmental.Id].State.ShouldBe("Available");
        mine.ShouldNotContainKey(draft.Id, "an unpublished app is not offered");
        mine.ShouldNotContainKey(withdrawn.Id, "a withdrawn package is never offered, whatever the catalog says");
        mine[installed.Id].State.ShouldBe("Installed");
        mine[outdated.Id].State.ShouldBe("UpdateAvailable");

        // A device outside the group sees the same catalog minus the departmental
        // app, and its own state: nothing is installed on it.
        var theirs = (await ListAsync(otherCredential)).Apps.ToDictionary(a => a.Id);
        theirs.ShouldContainKey(everyone.Id);
        theirs.ShouldNotContainKey(departmental.Id);
        theirs.ShouldNotContainKey(draft.Id);
        theirs[installed.Id].State.ShouldBe("Available");
        theirs[outdated.Id].State.ShouldBe("Available");

        // Nothing about the package crosses the wire beyond version and publisher.
        otherDeviceId.ShouldNotBe(deviceId);
    }

    [Fact]
    public async Task The_catalog_is_sorted_by_name_regardless_of_case()
    {
        var (_, credential, orgId) = await EnrollAsync("CAT-SORT");
        var stamp = Guid.CreateVersion7().ToString("N")[..8];
        await SeedOfferedAsync(orgId, $"zeta {stamp}");
        await SeedOfferedAsync(orgId, $"Alpha {stamp}");
        await SeedOfferedAsync(orgId, $"beta {stamp}");

        var names = (await ListAsync(credential)).Apps.Select(a => a.DisplayName).ToList();

        names.ShouldBe(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ------------------------------------------------------------ icon

    [Fact]
    public async Task An_icon_is_served_only_for_an_app_the_device_may_see()
    {
        var (_, credential, orgId) = await EnrollAsync("CAT-ICON");
        var png = TinyPng();
        var visible = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Iconic"))).Id, Unique("Iconic"), icon: png);
        var draft = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Draft Icon"))).Id, Unique("Draft Icon"),
            published: false, icon: png);
        var elsewhere = await SeedGroupAsync(orgId, "Elsewhere");
        var hidden = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Hidden Icon"))).Id, Unique("Hidden Icon"),
            visibility: CatalogVisibility.SelectedGroups, groupIds: [elsewhere], icon: png);
        var bare = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Bare"))).Id, Unique("Bare"));

        var apps = (await ListAsync(credential)).Apps.ToDictionary(a => a.Id);
        apps[visible.Id].HasIcon.ShouldBeTrue();
        apps[bare.Id].HasIcon.ShouldBeFalse();

        using var client = _fixture.Factory.CreateClient();

        var ok = await client.SendAsync(Request(HttpMethod.Get, IconRoute(visible.Id), credential: credential));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        ok.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await ok.Content.ReadAsByteArrayAsync()).ShouldBe(png);

        // Invisible is indistinguishable from non-existent.
        foreach (var appId in new[] { draft.Id, hidden.Id, bare.Id, Guid.CreateVersion7() })
        {
            (await client.SendAsync(Request(HttpMethod.Get, IconRoute(appId), credential: credential)))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound, $"app {appId} must not serve an icon to this device");
        }
    }

    // --------------------------------------------------------- install

    [Fact]
    public async Task A_request_for_a_visible_app_queues_the_install_once()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-INSTALL");
        var (package, app) = await SeedOfferedAsync(orgId, Unique("Zoom"));

        var first = await RequestInstallAsync(credential, app.Id, @"CORP\jdoe");

        first.Outcome.ShouldBe("Queued");
        first.TaskId.ShouldNotBeNull();

        await using (var db = _fixture.CreateDbContext())
        {
            var task = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == first.TaskId);
            task.DeviceId.ShouldBe(deviceId);
            task.Type.ShouldBe(DeviceTaskType.InstallPackage);
            task.Status.ShouldBe(DeviceTaskStatus.Queued);
            // Queued in the publisher's name, because publishing is the approval
            // the install rests on; the requesting user is recorded alongside.
            task.CreatedByUserId.ShouldBe(app.PublishedByUserId);
            task.CreatedByDisplay.ShouldContain(@"Company Portal (CORP\jdoe)");
            task.PayloadJson.ShouldNotBeNull();
            task.PayloadJson.ShouldContain(package.Id.ToString());
            task.PayloadJson.ShouldContain(package.Sha256);

            // The same deployment record an administrator's install would leave.
            var target = await db.SoftwareDeploymentTargets.AsNoTracking().SingleAsync(t => t.TaskId == first.TaskId);
            target.DeviceId.ShouldBe(deviceId);
            var deployment = await db.SoftwareDeployments.AsNoTracking().SingleAsync(d => d.Id == target.DeploymentId);
            deployment.PackageId.ShouldBe(package.Id);
        }

        var request = (await RequestsOfAsync(deviceId)).ShouldHaveSingleItem();
        request.OrganizationId.ShouldBe(orgId);
        request.CatalogAppId.ShouldBe(app.Id);
        request.AppName.ShouldBe(app.DisplayName);
        request.RequestedBy.ShouldBe(@"CORP\jdoe");
        request.Outcome.ShouldBe(CatalogInstallOutcome.Queued);
        request.TaskId.ShouldBe(first.TaskId);

        // The tile now says so.
        (await ListAsync(credential)).Apps.Single(a => a.Id == app.Id).State.ShouldBe("Installing");

        // A second click, a retry, a replay: the device does not get a second task.
        var second = await RequestInstallAsync(credential, app.Id, @"CORP\jdoe");

        second.Outcome.ShouldBe("AlreadyInProgress");
        second.TaskId.ShouldBeNull();
        (await InstallTasksOfAsync(deviceId)).ShouldBe(1);
        var requests = await RequestsOfAsync(deviceId);
        requests.Count.ShouldBe(2);
        requests[1].Outcome.ShouldBe(CatalogInstallOutcome.AlreadyInProgress);
        requests[1].TaskId.ShouldBeNull();
        requests[1].AppName.ShouldBe(app.DisplayName);
    }

    [Fact]
    public async Task Without_a_user_name_the_install_is_attributed_to_the_machine()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-HOST");
        var (_, app) = await SeedOfferedAsync(orgId, Unique("Anonymous"));

        var decision = await RequestInstallAsync(credential, app.Id, requestedBy: "   ");

        decision.Outcome.ShouldBe("Queued");
        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == decision.TaskId);
        task.CreatedByDisplay.ShouldBe("Company Portal (CAT-HOST)");
        var request = (await RequestsOfAsync(deviceId)).ShouldHaveSingleItem();
        request.RequestedBy.ShouldBeNull("a blank user name is stored as absent, not as whitespace");
        request.Outcome.ShouldBe(CatalogInstallOutcome.Queued);
    }

    [Fact]
    public async Task An_app_the_device_may_not_have_is_refused_without_saying_why()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-REFUSE");
        var draft = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Draft"))).Id, Unique("Draft"), published: false);
        var elsewhere = await SeedGroupAsync(orgId, "Elsewhere");
        var hidden = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Hidden"))).Id, Unique("Hidden"),
            visibility: CatalogVisibility.SelectedGroups, groupIds: [elsewhere]);
        var withdrawn = await SeedAppAsync(
            orgId, (await SeedPackageAsync(orgId, Unique("Withdrawn"), withdrawn: true)).Id, Unique("Withdrawn"));
        var unknown = Guid.CreateVersion7();

        var asked = new[] { draft.Id, hidden.Id, withdrawn.Id, unknown };
        foreach (var appId in asked)
        {
            var decision = await RequestInstallAsync(credential, appId, @"CORP\curious");
            decision.Outcome.ShouldBe("Refused", $"app {appId} is not offered to this device");
            decision.TaskId.ShouldBeNull();
        }

        (await InstallTasksOfAsync(deviceId)).ShouldBe(0);

        var requests = await RequestsOfAsync(deviceId);
        requests.Count.ShouldBe(asked.Length, "a refusal is recorded; a run of them from one machine is worth noticing");
        requests.Select(r => r.CatalogAppId).ShouldBe(asked);
        foreach (var request in requests)
        {
            request.Outcome.ShouldBe(CatalogInstallOutcome.Refused);
            request.TaskId.ShouldBeNull();
            request.RequestedBy.ShouldBe(@"CORP\curious");
            // No name, because the name would say whether the id is real.
            request.AppName.ShouldBe("(not offered)");
        }
    }

    /// <summary>
    /// The product is there, in a version the server cannot order against the
    /// package's. Installing over it could be a downgrade, so to the user it is
    /// installed: the tile says so, and asking for it changes nothing.
    /// </summary>
    [Fact]
    public async Task A_product_in_a_version_the_server_cannot_read_counts_as_installed()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-ODDVER");
        var (package, app) = await SeedOfferedAsync(orgId, Unique("Odd Version"));
        // Same product code, but a version with no numeric head: not comparable.
        await ReportInstalledAsync(deviceId, package, version: "build-2024");

        (await ListAsync(credential)).Apps.Single(a => a.Id == app.Id).State.ShouldBe("Installed");

        var decision = await RequestInstallAsync(credential, app.Id, @"CORP\jdoe");

        decision.Outcome.ShouldBe("AlreadyInstalled");
        decision.TaskId.ShouldBeNull();
        (await InstallTasksOfAsync(deviceId)).ShouldBe(0);
        var request = (await RequestsOfAsync(deviceId)).ShouldHaveSingleItem();
        request.Outcome.ShouldBe(CatalogInstallOutcome.AlreadyInstalled);
        request.AppName.ShouldBe(app.DisplayName);
    }

    /// <summary>
    /// The task and the request row are one decision, written in one
    /// transaction. A failure after the task is queued must take the task with
    /// it; otherwise the device would run an install that nothing on record
    /// asked for, and the next request would be refused as already in progress.
    /// </summary>
    /// <remarks>
    /// The failure is injected underneath the feature, not inside it: a trigger
    /// on the request table that raises for this one device. It fires at the
    /// insert of the request row, which comes after the task has been saved,
    /// so it is exactly the "something went wrong after queueing" case.
    /// </remarks>
    [Fact]
    public async Task A_failure_after_queueing_rolls_the_task_back_too()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-ROLLBACK");
        var (_, app) = await SeedOfferedAsync(orgId, Unique("Fragile"));
        var trigger = $"cattest_reject_{Guid.CreateVersion7():N}";

        await using (var db = _fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION endpoint_platform.{trigger}() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'CatalogTests: simulated failure after queueing';
                END
                $$;
                CREATE TRIGGER {trigger}
                    BEFORE INSERT ON endpoint_platform.catalog_install_requests
                    FOR EACH ROW WHEN (NEW.device_id = '{deviceId}')
                    EXECUTE FUNCTION endpoint_platform.{trigger}();
                """);
        }

        try
        {
            using var client = _fixture.Factory.CreateClient();
            var failed = await client.SendAsync(Request(
                HttpMethod.Post, InstallRoute(app.Id), new CatalogInstallCommand(@"CORP\jdoe"), credential));

            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

            await using var db = _fixture.CreateDbContext();
            (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId)).ShouldBe(0, "the queued task must roll back");
            (await db.SoftwareDeploymentTargets.CountAsync(t => t.DeviceId == deviceId)).ShouldBe(0);
            (await db.CatalogInstallRequests.CountAsync(r => r.DeviceId == deviceId)).ShouldBe(0);
        }
        finally
        {
            await using var db = _fixture.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync($"""
                DROP TRIGGER IF EXISTS {trigger} ON endpoint_platform.catalog_install_requests;
                DROP FUNCTION IF EXISTS endpoint_platform.{trigger}();
                """);
        }

        // Nothing leaked: the same request now succeeds, and leaves exactly one
        // row and one task -- not "already in progress" from a ghost of the
        // failed attempt.
        var decision = await RequestInstallAsync(credential, app.Id, @"CORP\jdoe");

        decision.Outcome.ShouldBe("Queued");
        decision.TaskId.ShouldNotBeNull();
        (await InstallTasksOfAsync(deviceId)).ShouldBe(1);
        var request = (await RequestsOfAsync(deviceId)).ShouldHaveSingleItem();
        request.Outcome.ShouldBe(CatalogInstallOutcome.Queued);
        request.TaskId.ShouldBe(decision.TaskId);
    }

    [Fact]
    public async Task An_app_already_on_the_device_is_not_installed_again()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-PRESENT");
        var (package, app) = await SeedOfferedAsync(orgId, Unique("Present"));
        await ReportInstalledAsync(deviceId, package);

        var decision = await RequestInstallAsync(credential, app.Id, @"CORP\jdoe");

        decision.Outcome.ShouldBe("AlreadyInstalled");
        decision.TaskId.ShouldBeNull();
        (await InstallTasksOfAsync(deviceId)).ShouldBe(0);
        var request = (await RequestsOfAsync(deviceId)).ShouldHaveSingleItem();
        request.Outcome.ShouldBe(CatalogInstallOutcome.AlreadyInstalled);
        request.AppName.ShouldBe(app.DisplayName);
        request.TaskId.ShouldBeNull();
    }

    // ------------------------------------------------------- validation

    [Fact]
    public async Task A_malformed_or_unauthenticated_request_is_rejected_before_anything_is_recorded()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-REJECT");
        var (_, app) = await SeedOfferedAsync(orgId, Unique("Guarded"));
        using var client = _fixture.Factory.CreateClient();

        // A user name longer than the record can hold is a 400, not a truncation.
        (await client.SendAsync(Request(
                HttpMethod.Post, InstallRoute(app.Id), new CatalogInstallCommand(new string('u', 257)), credential)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Wrong protocol version, on every catalog route.
        (await client.SendAsync(Request(
                HttpMethod.Post, InstallRoute(app.Id), new CatalogInstallCommand("x"), credential, protocolVersion: 99)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(Request(HttpMethod.Get, CatalogRoute, credential: credential, protocolVersion: 99)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(Request(HttpMethod.Get, IconRoute(app.Id), credential: credential, protocolVersion: 99)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // No credential, and a credential that is not a device's.
        (await client.SendAsync(Request(HttpMethod.Post, InstallRoute(app.Id), new CatalogInstallCommand("x"))))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Get, CatalogRoute)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Get, IconRoute(app.Id))))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(
                HttpMethod.Post, InstallRoute(app.Id), new CatalogInstallCommand("x"), credential: "nobody.nothing")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await RequestsOfAsync(deviceId)).ShouldBeEmpty();
        (await InstallTasksOfAsync(deviceId)).ShouldBe(0);
    }

    // ------------------------------------------------------- rate limit

    [Fact]
    public async Task More_than_ten_requests_a_minute_are_refused_and_not_recorded()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("CAT-LIMIT");
        var (_, app) = await SeedOfferedAsync(orgId, Unique("Limited"));

        // A loop or a probe: ten refused requests for ids that are not offered.
        for (var i = 0; i < MaxRequestsPerMinute; i++)
        {
            (await RequestInstallAsync(credential, Guid.CreateVersion7(), @"CORP\loop")).Outcome.ShouldBe("Refused");
        }

        (await RequestsOfAsync(deviceId)).Count.ShouldBe(MaxRequestsPerMinute);

        // The eleventh is refused even for an app the device may have, and it
        // leaves no row: beyond the limit nothing deserves a record per attempt.
        var over = await RequestInstallAsync(credential, app.Id, @"CORP\loop");

        over.Outcome.ShouldBe("Refused");
        over.TaskId.ShouldBeNull();
        (await RequestsOfAsync(deviceId)).Count.ShouldBe(MaxRequestsPerMinute);
        (await InstallTasksOfAsync(deviceId)).ShouldBe(0);
    }
}
