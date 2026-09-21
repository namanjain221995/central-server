using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Who may queue an agent update, and onto which devices.
/// </summary>
/// <remarks>
/// <para>
/// An agent update runs an installer as SYSTEM on the target machine, so the
/// targeting rules matter as much as the permission. This route filtered on
/// OrganizationId alone until now -- weaker than every other device-targeted
/// endpoint -- which let an administrator scoped to one group push an installer
/// onto any machine in the tenant.
/// </para>
/// <para>
/// These cover the refusals rather than the happy path, which
/// <c>AgentReleaseEndpointTests</c> already owns. Each asserts that no task was
/// created, not merely that the response was unsuccessful: a refusal that still
/// queued work would be the worst of both.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class AgentUpdateAuthorizationTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri UpdateAgent(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/actions/update-agent", UriKind.Relative);

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// A fresh Super Administrator, with or without device scope.
    /// </summary>
    /// <remarks>
    /// The session is minted directly rather than by signing in: the login
    /// endpoint is rate limited and that budget is shared by every test in this
    /// assembly.
    /// </remarks>
    private async Task<HttpClient> AdminAsync(bool allDeviceScope = true)
    {
        var email = $"upd-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == SystemRoles.SuperAdministrator);

            var user = new PlatformUser(org.Id, email, "Update Admin");
            user.SetPasswordHash(
                PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);

            if (allDeviceScope)
            {
                user.GrantAllDeviceScope();
            }

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var token = SecretGenerator.GenerateSecret();

        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);

            db.AdminSessions.Add(new AdminSession(
                user.Id,
                SecretGenerator.HashSecret(token),
                user.SecurityStamp,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1),
                sourceIp: null,
                userAgent: "agent-update-tests"));

            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    private async Task<Guid> SeedDeviceAsync(string agentVersion, Guid? organizationId = null)
    {
        await using var db = _fixture.CreateDbContext();

        var orgId = organizationId
            ?? await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();

        var token = new EnrollmentToken(
            orgId, $"upd-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            Guid.CreateVersion7(), "update-tests", DateTimeOffset.UtcNow.AddHours(1), 1);

        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            orgId, "UPD-PC", $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);

        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return device.Id;
    }

    /// <summary>Uploads a draft release. Mirrors AgentReleaseEndpointTests exactly.</summary>
    private async Task<Guid> CreateDraftAsync(HttpClient client, string version)
    {
        // Signed by the fixture authority: most callers go on to publish, and the
        // publish gate refuses anything else.
        var bytes = TestArtifacts.SignedMsi(
            AdminApiPostgresFixture.SigningAuthority, seed: $"{version}-{Guid.CreateVersion7():N}", productVersion: version);

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var form = new MultipartFormDataContent
        {
            { file, "file", $"EndpointPlatformAgent-{version}-x64.msi" },
            { new StringContent(version), "version" },
            { new StringContent(Convert.ToHexStringLower(SHA256.HashData(bytes))), "sha256" },
        };

        var response = await client.PostAsync(new Uri("/admin/v1/agent-releases", UriKind.Relative), form);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync();

        return Guid.Parse(
            System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("releaseId").GetString()!);
    }

    /// <summary>A draft that has been published.</summary>
    private async Task<Guid> PublishedReleaseAsync(HttpClient client, string version)
    {
        var releaseId = await CreateDraftAsync(client, version);

        (await client.PostAsync(
                new Uri($"/admin/v1/agent-releases/{releaseId}/publish", UriKind.Relative), null))
            .EnsureSuccessStatusCode();

        return releaseId;
    }

    private async Task AssertNothingQueuedAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();

        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId))
            .ShouldBe(0, "a refused update must not leave a task behind");
    }

    // ---- device scope ------------------------------------------------------

    /// <summary>
    /// The gap this class was written for.
    /// </summary>
    /// <remarks>
    /// Scope is deny-by-default, so an account with no scope rows reaches
    /// nothing. Answered 404 rather than 403, matching the other device routes: a
    /// caller who may not act on a device is not told whether it exists.
    /// </remarks>
    [Fact]
    public async Task An_administrator_without_device_scope_cannot_queue_an_update()
    {
        using var owner = await AdminAsync();
        var releaseId = await PublishedReleaseAsync(owner, "9.20.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        using var unscoped = await AdminAsync(allDeviceScope: false);

        (await unscoped.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await AssertNothingQueuedAsync(deviceId);
    }

    /// <summary>The same account, once scoped, may proceed.</summary>
    [Fact]
    public async Task A_scoped_administrator_can_queue_an_update()
    {
        using var owner = await AdminAsync();
        var releaseId = await PublishedReleaseAsync(owner, "9.21.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        using var scoped = await AdminAsync(allDeviceScope: true);

        (await scoped.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.DeviceId == deviceId);
        task.Type.ShouldBe(Domain.Tasks.DeviceTaskType.UpdateAgent);
    }

    /// <summary>Organization isolation survives the scope check being added.</summary>
    [Fact]
    public async Task A_device_in_another_organization_is_not_found()
    {
        using var client = await AdminAsync();
        var releaseId = await PublishedReleaseAsync(client, "9.22.0");

        Guid foreignOrgId;
        await using (var db = _fixture.CreateDbContext())
        {
            var org = new Organization("Other Org", ("o" + Guid.CreateVersion7().ToString("N"))[..20]);
            db.Organizations.Add(org);
            await db.SaveChangesAsync();
            foreignOrgId = org.Id;
        }

        var foreignDeviceId = await SeedDeviceAsync("1.0.0", foreignOrgId);

        (await client.PostAsJsonAsync(UpdateAgent(foreignDeviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await AssertNothingQueuedAsync(foreignDeviceId);
    }

    // ---- targeting rules ---------------------------------------------------

    /// <summary>
    /// A retired device is not a target. An offboarded machine should not be
    /// handed an installer if it ever reappears.
    /// </summary>
    /// <remarks>
    /// Enforced one layer down, in <c>DeviceTaskService.QueueAsync</c>, which
    /// returns null for a retired device and leaves this handler answering 404.
    /// Asserted as that exact code rather than merely "not Accepted": the refusal
    /// is meant to be indistinguishable from the device not existing, and a
    /// looser assertion would still pass if the gate moved or began answering
    /// 409 instead.
    /// </remarks>
    [Fact]
    public async Task A_retired_device_cannot_be_targeted()
    {
        using var client = await AdminAsync();
        var releaseId = await PublishedReleaseAsync(client, "9.23.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        await using (var db = _fixture.CreateDbContext())
        {
            var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
            device.Retire();
            await db.SaveChangesAsync();
        }

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await AssertNothingQueuedAsync(deviceId);
    }

    /// <summary>
    /// A release that was published and then revoked is withdrawn, and must not
    /// be deployable afterwards.
    /// </summary>
    [Fact]
    public async Task A_revoked_release_cannot_be_queued()
    {
        using var client = await AdminAsync();
        var releaseId = await PublishedReleaseAsync(client, "9.24.0");

        (await client.PostAsync(
                new Uri($"/admin/v1/agent-releases/{releaseId}/revoke", UriKind.Relative), null))
            .EnsureSuccessStatusCode();

        var deviceId = await SeedDeviceAsync("1.0.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldNotBe(HttpStatusCode.Accepted);

        await AssertNothingQueuedAsync(deviceId);
    }

    [Fact]
    public async Task A_same_version_update_is_refused()
    {
        using var client = await AdminAsync();
        var releaseId = await PublishedReleaseAsync(client, "9.25.0");
        var deviceId = await SeedDeviceAsync("9.25.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await AssertNothingQueuedAsync(deviceId);
    }

    [Fact]
    public async Task A_downgrade_is_refused()
    {
        using var client = await AdminAsync();
        var releaseId = await PublishedReleaseAsync(client, "9.26.0");
        var deviceId = await SeedDeviceAsync("9.27.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await AssertNothingQueuedAsync(deviceId);
    }

    [Fact]
    public async Task An_unpublished_release_cannot_be_queued()
    {
        using var client = await AdminAsync();

        var draftId = await CreateDraftAsync(client, "9.28.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId = draftId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await AssertNothingQueuedAsync(deviceId);
    }
}
