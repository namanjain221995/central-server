using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Driver package approval and deployment over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are the point. A package with no signer pin cannot be approved, a
/// role without <c>driver.manage</c> cannot approve or deploy one, a device outside
/// the caller's scope is invisible, and an endpoint whose agent predates the executor
/// is told so rather than being handed a task nothing will run.
/// </para>
/// <para>
/// The queued task's payload is asserted directly, because it is what the endpoint
/// will act on: the pins it carries are the only thing standing between an approved
/// package and an arbitrary one.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DriverPackageEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string HardwareId = @"PCI\VEN_8086&DEV_1234";
    private const string Signer = "Contoso Corporation";

    private static readonly Uri Packages = new("/admin/v1/driver-packages", UriKind.Relative);

    private static Uri Deploy(Guid packageId) =>
        new($"/admin/v1/driver-packages/{packageId}/deploy", UriKind.Relative);

    private static Uri Withdraw(Guid packageId) =>
        new($"/admin/v1/driver-packages/{packageId}/withdraw", UriKind.Relative);

    private static readonly Dictionary<string, string> SessionTokens = [];
    private static readonly SemaphoreSlim SignInGate = new(1, 1);

    private async Task<HttpClient> ClientAsync(string email)
    {
        await SignInGate.WaitAsync();
        try
        {
            if (!SessionTokens.TryGetValue(email, out var token))
            {
                token = await _fixture.SignInAsync(email);
                SessionTokens[email] = token;
            }

            return _fixture.CreateClientFor(token);
        }
        finally
        {
            SignInGate.Release();
        }
    }

    /// <summary>A distinct archive per call, so each test gets a distinct content hash.</summary>
    private static (byte[] Bytes, string Sha) Archive() =>
        Archive(Guid.NewGuid().ToString());

    private static (byte[] Bytes, string Sha) Archive(string seed)
    {
        var bytes = Encoding.UTF8.GetBytes($"pretend-driver-archive-{seed}");
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static MultipartFormDataContent Upload(
        byte[] bytes,
        string sha,
        string? name = "Contoso NIC",
        string? version = "2.0",
        string? infFileName = "contoso.inf",
        string? hardwareId = HardwareId,
        string? signer = Signer,
        string? driverVersion = "2.0.0.0")
    {
        var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "contoso-nic.zip");

        void Add(string key, string? value)
        {
            if (value is not null)
            {
                content.Add(new StringContent(value), key);
            }
        }

        Add("name", name);
        Add("version", version);
        Add("sha256", sha);
        Add("infFileName", infFileName);
        Add("hardwareId", hardwareId);
        Add("requiredSignerSubject", signer);
        Add("driverVersion", driverVersion);
        Add("provider", "Contoso");

        return content;
    }

    private async Task<Guid> SeedDeviceAsync(string agentVersion = "1.3.0")
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"drvpkg-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "DRVPKG-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            agentVersion, null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> ApprovePackageAsync(HttpClient client)
    {
        var (bytes, sha) = Archive();
        var response = await client.PostAsync(Packages, Upload(bytes, sha));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_cannot_approve_or_deploy()
    {
        using var client = _fixture.Factory.CreateClient();
        var (bytes, sha) = Archive();

        (await client.PostAsync(Packages, Upload(bytes, sha)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync(Deploy(Guid.CreateVersion7()), new { deviceId = Guid.CreateVersion7() }))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Helpdesk and Auditor can see the catalogue -- knowing which driver is approved
    /// is part of diagnosing a fault -- and can do nothing else with it.
    /// </summary>
    [Theory]
    [InlineData("helpdesk")]
    [InlineData("auditor")]
    public async Task A_role_without_driver_manage_can_read_but_not_approve_withdraw_or_deploy(string which)
    {
        var email = which == "helpdesk"
            ? AdminApiPostgresFixture.HelpdeskEmail
            : AdminApiPostgresFixture.AuditorEmail;

        using var client = await ClientAsync(email);
        var (bytes, sha) = Archive();

        (await client.GetAsync(Packages)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsync(Packages, Upload(bytes, sha)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsync(Withdraw(Guid.CreateVersion7()), null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync(Deploy(Guid.CreateVersion7()), new { deviceId = Guid.CreateVersion7() }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_outside_the_callers_scope_is_invisible_to_deployment()
    {
        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(admin);

        var inScope = await SeedDeviceAsync();
        var outOfScope = await SeedDeviceAsync();

        var email = $"drvpkg-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"DrvPkg-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            (await db.Devices.SingleAsync(candidate => candidate.Id == inScope)).MoveToGroup(group.Id);

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
                DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));

        (await client.PostAsJsonAsync(Deploy(packageId), new { deviceId = inScope }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsJsonAsync(Deploy(packageId), new { deviceId = outOfScope }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- approval ----------------------------------------------------------

    [Fact]
    public async Task An_approved_package_is_stored_with_its_pins()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var (bytes, sha) = Archive();

        var response = await client.PostAsync(Packages, Upload(bytes, sha));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var db = _fixture.CreateDbContext();
        var package = await db.DriverPackages.SingleAsync(p => p.Id == id);

        package.Sha256.ShouldBe(sha);
        package.HardwareId.ShouldBe(HardwareId);
        package.RequiredSignerSubject.ShouldBe(Signer);
        package.InfFileName.ShouldBe("contoso.inf");
        package.IsWithdrawn.ShouldBeFalse();
    }

    /// <summary>
    /// The rule that separates driver approval from software approval, enforced at
    /// the boundary as well as in the domain.
    /// </summary>
    [Fact]
    public async Task A_package_without_a_signer_pin_is_refused()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var (bytes, sha) = Archive();

        var response = await client.PostAsync(Packages, Upload(bytes, sha, signer: null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var db = _fixture.CreateDbContext();
        (await db.DriverPackages.AnyAsync(p => p.Sha256 == sha)).ShouldBeFalse();
    }

    /// <summary>
    /// The content store recomputes the hash as it writes, so a declared hash that
    /// disagrees with the bytes is caught before a row exists.
    /// </summary>
    [Fact]
    public async Task Content_that_does_not_match_the_declared_hash_is_refused()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var (bytes, _) = Archive();
        var (_, otherSha) = Archive("something-else");

        var response = await client.PostAsync(Packages, Upload(bytes, otherSha));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var db = _fixture.CreateDbContext();
        (await db.DriverPackages.AnyAsync(p => p.Sha256 == otherSha)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("infFileName")]
    [InlineData("hardwareId")]
    [InlineData("name")]
    [InlineData("version")]
    public async Task A_package_missing_a_required_field_is_refused(string missing)
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var (bytes, sha) = Archive();

        var content = Upload(
            bytes, sha,
            name: missing == "name" ? null : "Contoso NIC",
            version: missing == "version" ? null : "2.0",
            infFileName: missing == "infFileName" ? null : "contoso.inf",
            hardwareId: missing == "hardwareId" ? null : HardwareId);

        (await client.PostAsync(Packages, content)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_inf_name_carrying_a_path_is_refused()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var (bytes, sha) = Archive();

        (await client.PostAsync(Packages, Upload(bytes, sha, infFileName: @"..\..\windows\inf\usbstor.inf")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Re_uploading_identical_content_is_a_duplicate()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var (bytes, sha) = Archive();

        (await client.PostAsync(Packages, Upload(bytes, sha))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(Packages, Upload(bytes, sha))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- deployment --------------------------------------------------------

    [Fact]
    public async Task Deploying_queues_a_task_carrying_every_pin_the_endpoint_needs()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);
        var deviceId = await SeedDeviceAsync();

        var response = await client.PostAsJsonAsync(Deploy(packageId), new { deviceId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleAsync(t => t.Id == taskId);

        task.Type.ShouldBe(DeviceTaskType.InstallDriverPackage);
        task.DeviceId.ShouldBe(deviceId);

        var payload = JsonDocument.Parse(task.PayloadJson!).RootElement;

        payload.GetProperty("packageId").GetGuid().ShouldBe(packageId);
        payload.GetProperty("sha256").GetString().ShouldNotBeNullOrWhiteSpace();
        payload.GetProperty("hardwareId").GetString().ShouldBe(HardwareId);
        payload.GetProperty("requiredSignerSubject").GetString().ShouldBe(Signer);
        payload.GetProperty("infFileName").GetString().ShouldBe("contoso.inf");
        payload.GetProperty("issuedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);

        // Refused by default, and the payload says so explicitly rather than relying
        // on the endpoint to assume it.
        payload.GetProperty("allowDowngrade").GetBoolean().ShouldBeFalse();

        // There is no URL anywhere in what the endpoint receives.
        task.PayloadJson!.ShouldNotContain("http");
    }

    [Fact]
    public async Task A_downgrade_must_be_asked_for_explicitly_and_is_carried_in_the_payload()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);
        var deviceId = await SeedDeviceAsync();

        var response = await client.PostAsJsonAsync(
            Deploy(packageId), new { deviceId, allowDowngrade = true });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleAsync(t => t.Id == taskId);

        JsonDocument.Parse(task.PayloadJson!).RootElement
            .GetProperty("allowDowngrade").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// An endpoint whose agent predates the executor is told what is wrong, rather
    /// than being given a task that would come back as an unknown type.
    /// </summary>
    [Fact]
    public async Task An_endpoint_running_an_older_agent_is_refused_with_a_reason()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);
        var deviceId = await SeedDeviceAsync(agentVersion: "1.2.0");

        var response = await client.PostAsJsonAsync(Deploy(packageId), new { deviceId });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("1.3.0");

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.AnyAsync(t => t.DeviceId == deviceId)).ShouldBeFalse(
            "no task should exist for an endpoint that cannot run it");
    }

    [Fact]
    public async Task A_withdrawn_package_can_no_longer_be_deployed()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);
        var deviceId = await SeedDeviceAsync();

        (await client.PostAsync(Withdraw(packageId), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync(Deploy(packageId), new { deviceId }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deploying_an_unknown_package_or_device_is_not_found()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);

        (await client.PostAsJsonAsync(Deploy(Guid.CreateVersion7()), new { deviceId = await SeedDeviceAsync() }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await client.PostAsJsonAsync(Deploy(packageId), new { deviceId = Guid.CreateVersion7() }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The withdrawal race: approved when the task was queued, withdrawn before the
    /// endpoint acted.
    /// </summary>
    /// <remarks>
    /// The queued task deliberately survives -- cancelling it is a separate decision
    /// -- but it becomes unusable, because the archive it names is no longer served.
    /// The endpoint's download is refused and no driver store is touched. Withdrawal
    /// therefore does not need to race the task queue to be effective.
    /// </remarks>
    [Fact]
    public async Task A_package_withdrawn_after_a_task_is_queued_can_no_longer_be_fetched()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);
        var deviceId = await SeedDeviceAsync();

        var deploy = await client.PostAsJsonAsync(Deploy(packageId), new { deviceId });
        deploy.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskId = (await deploy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();

        (await client.PostAsync(Withdraw(packageId), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var db = _fixture.CreateDbContext();

        // The task still exists and still names the package...
        var task = await db.DeviceTasks.SingleAsync(t => t.Id == taskId);
        task.Status.ShouldBe(DeviceTaskStatus.Queued);

        // ...but the package is no longer deployable, which is what the agent
        // download route checks before streaming a single byte.
        (await db.DriverPackages.SingleAsync(p => p.Id == packageId)).IsWithdrawn.ShouldBeTrue();

        var service = _fixture.Factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<Infrastructure.Drivers.DriverPackageService>();

        var org = (await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync()).Id;

        (await service.GetDeployableAsync(org, packageId)).ShouldBeNull(
            "a withdrawn package must not be servable to an endpoint holding an older task");
    }

    // ---- audit -------------------------------------------------------------

    [Fact]
    public async Task Approval_and_withdrawal_are_audited_and_carry_no_secrets()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);

        await client.PostAsync(Withdraw(packageId), null);

        await using var db = _fixture.CreateDbContext();
        var entries = await db.AuditLogEntries
            .Where(a => a.TargetId == packageId.ToString())
            .OrderBy(a => a.OccurredAt)
            .ToListAsync();

        entries.Select(e => e.Action).ShouldBe(["driver.package.created", "driver.package.withdrawn"]);

        foreach (var entry in entries)
        {
            var payload = (entry.PreviousState ?? "") + (entry.NewState ?? "");

            payload.ShouldNotContain(AdminApiPostgresFixture.Password);
            payload.ShouldNotContain("password");
            payload.ShouldNotContain("secret");
        }
    }

    /// <summary>
    /// Queueing is audited by the existing task pipeline rather than a driver-specific
    /// event, which is why no <c>driver.update.requested</c> exists.
    /// </summary>
    [Fact]
    public async Task Deployment_is_audited_through_the_existing_task_event()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var packageId = await ApprovePackageAsync(client);
        var deviceId = await SeedDeviceAsync();

        await client.PostAsJsonAsync(Deploy(packageId), new { deviceId });

        await using var db = _fixture.CreateDbContext();

        (await db.AuditLogEntries.AnyAsync(
                a => a.DeviceId == deviceId && a.Action == "task.queue.installdriverpackage"))
            .ShouldBeTrue();
    }
}
