using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Milestone 1.5.0: deploying an approved package to devices and groups.
/// </summary>
/// <remarks>
/// The point of these tests is what does <em>not</em> happen. A deployment that
/// queues a task for every target reinstalls software that is already correct,
/// and on a fleet that is hundreds of avoidable MSI executions against working
/// installations — so the assertions are mostly about tasks that must not exist.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeploymentEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Deployments = new("/admin/v1/deployments", UriKind.Relative);
    private static readonly Uri Preview = new("/admin/v1/deployments/preview", UriKind.Relative);

    private const string ProductCode = "{2C4E1D0B-AAAA-BBBB-CCCC-444455556666}";

    private sealed record CreateResponse(Guid DeploymentId, int Targeted, int Queued, int Skipped);

    private sealed record PreviewResponse(
        Guid PackageId, string PackageName, string PackageVersion, int Targeted, int NeedsInstall,
        int AlreadyInstalled, int NewerInstalled, int Retired, int NotComparable);

    private sealed record Tally(
        int Total, int Pending, int Installing, int Succeeded, int Failed, int Expired, int Skipped);

    private sealed record DeviceResult(
        Guid DeviceId, string Hostname, string? DisplayName, string DeviceStatus, DateTimeOffset? LastSeenAt,
        string Status, string Reason, string? ObservedVersion, Guid? TaskId, string? ResultMessage,
        DateTimeOffset? CompletedAt);

    private sealed record Detail(
        Guid Id, Guid PackageId, string PackageName, string PackageVersion, string TargetType,
        string CreatedByDisplay, DateTimeOffset CreatedAt, Tally Tally, IReadOnlyList<DeviceResult> Targets);

    private async Task<HttpClient> AdminAsync()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        return _fixture.CreateClientFor(token);
    }

    /// <summary>Registers a deployable package through the real upload endpoint.</summary>
    private async Task<Guid> RegisterPackageAsync(HttpClient client, string name, string version)
    {
        var bytes = Encoding.UTF8.GetBytes("msi-" + Guid.CreateVersion7().ToString("N") + new string('z', 300));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var form = new MultipartFormDataContent
        {
            { file, "file", "app.msi" },
            { new StringContent(name), "name" },
            { new StringContent(version), "version" },
            { new StringContent("Contoso"), "publisher" },
            { new StringContent(sha), "sha256" },
            { new StringContent(ProductCode), "msiProductCode" },
        };

        var response = await client.PostAsync(new Uri("/admin/v1/packages/", UriKind.Relative), form);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        await using var db = _fixture.CreateDbContext();
        return await db.SoftwarePackages.Where(p => p.Sha256 == sha).Select(p => p.Id).SingleAsync();
    }

    /// <summary>
    /// A device, optionally with an installed version of the package's product.
    /// </summary>
    private async Task<Guid> SeedDeviceAsync(
        string hostname,
        string? installedVersion = null,
        DeviceStatus status = DeviceStatus.Active,
        string packageName = "Contoso App",
        string? productCode = ProductCode)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId, $"deploy-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "deploy-tests", now.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", "1.5.0",
            "Microsoft Windows 11 Pro", token.Id, now);

        if (status == DeviceStatus.Retired)
        {
            device.Retire();
        }

        db.Devices.Add(device);

        if (installedVersion is not null)
        {
            db.DeviceSoftware.Add(new DeviceSoftware(
                device.Id, packageName, installedVersion, "Contoso", null, null, "x64", now,
                "Machine", null, productCode));
        }

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> SeedGroupAsync(params Guid[] deviceIds)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var group = new DeviceGroup(organizationId, $"Group {Guid.CreateVersion7():N}"[..20], "deployment tests", DeviceGroupType.Static);
        db.DeviceGroups.Add(group);

        foreach (var deviceId in deviceIds)
        {
            (await db.Devices.SingleAsync(candidate => candidate.Id == deviceId)).MoveToGroup(group.Id);
        }

        await db.SaveChangesAsync();
        return group.Id;
    }

    private static async Task<int> InstallTaskCountAsync(
        AdminApiPostgresFixture fixture, Guid deviceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.DeviceTasks.CountAsync(
            t => t.DeviceId == deviceId && t.Type == DeviceTaskType.InstallPackage);
    }

    // ------------------------------------------------------------------ core

    /// <summary>
    /// The behaviour the whole eligibility engine exists for: a device that
    /// already has the requested version is targeted, recorded, and sent nothing.
    /// </summary>
    [Fact]
    public async Task A_device_that_already_has_the_version_is_skipped_and_receives_no_task()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");

        var current = await SeedDeviceAsync("DEP-CURRENT", installedVersion: "2.0.0");
        var stale = await SeedDeviceAsync("DEP-STALE", installedVersion: "1.0.0");
        var missing = await SeedDeviceAsync("DEP-MISSING");

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId,
            deviceIds = new[] { current, stale, missing },
            groupIds = Array.Empty<Guid>(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = (await response.Content.ReadFromJsonAsync<CreateResponse>())!;

        result.Targeted.ShouldBe(3);
        result.Queued.ShouldBe(2);   // stale + missing
        result.Skipped.ShouldBe(1);  // current

        (await InstallTaskCountAsync(_fixture, current)).ShouldBe(0);
        (await InstallTaskCountAsync(_fixture, stale)).ShouldBe(1);
        (await InstallTaskCountAsync(_fixture, missing)).ShouldBe(1);
    }

    /// <summary>Installing an older package over a newer install is a downgrade.</summary>
    [Fact]
    public async Task A_device_with_a_newer_version_is_never_downgraded()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");
        var ahead = await SeedDeviceAsync("DEP-AHEAD", installedVersion: "3.1.0");

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = new[] { ahead }, groupIds = Array.Empty<Guid>(),
        });

        var result = (await response.Content.ReadFromJsonAsync<CreateResponse>())!;
        result.Queued.ShouldBe(0);
        (await InstallTaskCountAsync(_fixture, ahead)).ShouldBe(0);
    }

    /// <summary>
    /// Retired devices receive no tasks of any kind, and the exclusion is recorded
    /// rather than the device silently vanishing from the deployment.
    /// </summary>
    [Fact]
    public async Task A_retired_device_is_excluded_and_recorded()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");
        var retired = await SeedDeviceAsync("DEP-RETIRED", status: DeviceStatus.Retired);

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = new[] { retired }, groupIds = Array.Empty<Guid>(),
        });

        var result = (await response.Content.ReadFromJsonAsync<CreateResponse>())!;
        result.Targeted.ShouldBe(1);
        result.Queued.ShouldBe(0);
        (await InstallTaskCountAsync(_fixture, retired)).ShouldBe(0);

        var detail = await client.GetFromJsonAsync<Detail>(
            new Uri($"/admin/v1/deployments/{result.DeploymentId}", UriKind.Relative));

        var target = detail!.Targets.Single();
        target.Status.ShouldBe("Skipped");
        target.Reason.ShouldBe("Retired");
    }

    [Fact]
    public async Task A_group_target_resolves_to_its_members()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");

        var a = await SeedDeviceAsync("DEP-GRP-A");
        var b = await SeedDeviceAsync("DEP-GRP-B");
        var groupId = await SeedGroupAsync(a, b);

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = Array.Empty<Guid>(), groupIds = new[] { groupId },
        });

        var result = (await response.Content.ReadFromJsonAsync<CreateResponse>())!;
        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(2);
    }

    /// <summary>
    /// A device reachable through both a direct target and a group is deployed to
    /// once. Two tasks would mean two installs on one machine.
    /// </summary>
    [Fact]
    public async Task A_device_targeted_twice_receives_one_task()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");

        var device = await SeedDeviceAsync("DEP-DUP");
        var groupId = await SeedGroupAsync(device);

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = new[] { device }, groupIds = new[] { groupId },
        });

        var result = (await response.Content.ReadFromJsonAsync<CreateResponse>())!;
        result.Targeted.ShouldBe(1);
        result.Queued.ShouldBe(1);
        (await InstallTaskCountAsync(_fixture, device)).ShouldBe(1);
    }

    // ------------------------------------------------------------- security

    /// <summary>Withdrawal means "not for new deployments", enforced server-side.</summary>
    [Fact]
    public async Task A_withdrawn_package_cannot_be_deployed()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");
        var device = await SeedDeviceAsync("DEP-WITHDRAWN");

        (await client.PostAsync(
            new Uri($"/admin/v1/packages/{packageId}/withdraw", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = new[] { device }, groupIds = Array.Empty<Guid>(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await InstallTaskCountAsync(_fixture, device)).ShouldBe(0);
    }

    /// <summary>
    /// A device id that does not resolve inside the caller's organization is not
    /// targeted, and nothing about it is disclosed.
    /// </summary>
    [Fact]
    public async Task An_unknown_device_id_is_not_targeted()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = new[] { Guid.CreateVersion7() }, groupIds = Array.Empty<Guid>(),
        });

        // Nothing resolved, so there is no deployment to create.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_group_id_resolves_to_no_devices()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = Array.Empty<Guid>(), groupIds = new[] { Guid.CreateVersion7() },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_request_with_no_targets_is_refused()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");

        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = Array.Empty<Guid>(), groupIds = Array.Empty<Guid>(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_deploy_or_read_deployments()
    {
        using var anonymous = _fixture.Factory.CreateClient();

        (await anonymous.PostAsJsonAsync(Deployments, new
        {
            packageId = Guid.CreateVersion7(),
            deviceIds = new[] { Guid.CreateVersion7() },
            groupIds = Array.Empty<Guid>(),
        })).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        (await anonymous.GetAsync(Deployments))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------------- preview

    /// <summary>The dialog's numbers must not change anything.</summary>
    [Fact]
    public async Task Previewing_reports_the_plan_without_creating_tasks_or_a_deployment()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");

        var current = await SeedDeviceAsync("PRE-CURRENT", installedVersion: "2.0.0");
        var stale = await SeedDeviceAsync("PRE-STALE", installedVersion: "1.0.0");
        var retired = await SeedDeviceAsync("PRE-RETIRED", status: DeviceStatus.Retired);

        await using (var db = _fixture.CreateDbContext())
        {
            var before = await db.SoftwareDeployments.CountAsync();

            var response = await client.PostAsJsonAsync(Preview, new
            {
                packageId,
                deviceIds = new[] { current, stale, retired },
                groupIds = Array.Empty<Guid>(),
            });

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var plan = (await response.Content.ReadFromJsonAsync<PreviewResponse>())!;

            plan.Targeted.ShouldBe(3);
            plan.NeedsInstall.ShouldBe(1);
            plan.AlreadyInstalled.ShouldBe(1);
            plan.Retired.ShouldBe(1);

            (await db.SoftwareDeployments.CountAsync()).ShouldBe(before);
        }

        (await InstallTaskCountAsync(_fixture, stale)).ShouldBe(0);
    }

    // --------------------------------------------------------------- status

    /// <summary>
    /// Per-device status is derived from the task, so a freshly queued install
    /// reads as Pending rather than as anything more optimistic.
    /// </summary>
    [Fact]
    public async Task Per_device_status_is_derived_from_the_task()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");
        var device = await SeedDeviceAsync("DEP-STATUS");

        var created = (await (await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = new[] { device }, groupIds = Array.Empty<Guid>(),
        })).Content.ReadFromJsonAsync<CreateResponse>())!;

        var detail = await client.GetFromJsonAsync<Detail>(
            new Uri($"/admin/v1/deployments/{created.DeploymentId}", UriKind.Relative));

        var target = detail!.Targets.Single();
        target.DeviceId.ShouldBe(device);
        target.Status.ShouldBe("Pending");
        target.TaskId.ShouldNotBeNull();
        detail.Tally.Pending.ShouldBe(1);
        detail.Tally.Total.ShouldBe(1);
        detail.PackageVersion.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task A_deployment_appears_in_the_list_with_its_tally()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "Contoso App", "2.0.0");
        var device = await SeedDeviceAsync("DEP-LIST");

        var created = (await (await client.PostAsJsonAsync(Deployments, new
        {
            packageId, deviceIds = new[] { device }, groupIds = Array.Empty<Guid>(),
        })).Content.ReadFromJsonAsync<CreateResponse>())!;

        var list = await client.GetFromJsonAsync<DeploymentListPage>(Deployments);

        var row = list!.Items.Single(d => d.Id == created.DeploymentId);
        row.PackageName.ShouldBe("Contoso App");
        row.Tally.Total.ShouldBe(1);
        row.Tally.Pending.ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_deployment_is_not_found()
    {
        using var client = await AdminAsync();

        (await client.GetAsync(new Uri($"/admin/v1/deployments/{Guid.CreateVersion7()}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed record DeploymentListRow(
        Guid Id, Guid PackageId, string PackageName, string PackageVersion, string TargetType,
        string CreatedByDisplay, DateTimeOffset CreatedAt, Tally Tally);

    private sealed record DeploymentListPage(
        IReadOnlyList<DeploymentListRow> Items, int TotalCount, int Page, int PageSize);
}
