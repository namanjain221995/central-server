using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Seeding and reading helpers shared by the group tests.
/// </summary>
/// <remarks>
/// The fixture's database is shared by every API test class, so every device
/// created anywhere lands in the default organization's "All Devices". Tests
/// here therefore act on groups they create themselves and never assume a count
/// for "All Devices".
/// </remarks>
internal sealed class GroupTestSupport(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    public static Uri Groups() => new("/admin/v1/groups", UriKind.Relative);

    public static Uri Group(Guid id) => new($"/admin/v1/groups/{id}", UriKind.Relative);

    public static Uri Devices(Guid id) => new($"/admin/v1/groups/{id}/devices", UriKind.Relative);

    public static Uri RemoveDevices(Guid id) => new($"/admin/v1/groups/{id}/devices/remove", UriKind.Relative);

    public static Uri Candidates(Guid id) => new($"/admin/v1/groups/{id}/candidates", UriKind.Relative);

    public static Uri Action(Guid id, string action) => new($"/admin/v1/groups/{id}/actions/{action}", UriKind.Relative);

    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    public static StringContent Json(object value) => Json(JsonSerializer.Serialize(value));

    public static string UniqueName(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..Math.Min(40, prefix.Length + 33)];

    /// <summary>A device in the default organization. Online unless told otherwise.</summary>
    public async Task<Guid> SeedDeviceAsync(bool online = true, string agentVersion = "1.9.0", string? hostname = null)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        var token = new EnrollmentToken(
            organizationId,
            $"group-test-{Guid.CreateVersion7():N}",
            secretHash: Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            createdByUserId: await db.PlatformUsers.Select(u => u.Id).FirstAsync(),
            createdByDisplay: "group-test",
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            maxUses: 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, hostname ?? $"GRP-{Guid.CreateVersion7():N}"[..12], $"smbios-{Guid.CreateVersion7()}",
            agentVersion, "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        if (!online)
        {
            await MakeOfflineAsync(device.Id);
        }

        return device.Id;
    }

    /// <summary>Last heard from an hour ago -- well past the 180-second threshold.</summary>
    public async Task MakeOfflineAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow.AddHours(-1)));
    }

    public async Task<Guid> GroupOfAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.Devices.Where(d => d.Id == deviceId).Select(d => d.DeviceGroupId).SingleAsync();
    }

    public async Task<Guid> AllDevicesIdAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        return await db.DeviceGroups.Where(g => g.OrganizationId == organizationId && g.IsBuiltIn).Select(g => g.Id).SingleAsync();
    }

    public async Task<int> TasksAsync(Guid deviceId, DeviceTaskType type)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == type);
    }

    public async Task<int> ActiveRestartsAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.CountAsync(t =>
            t.DeviceId == deviceId && t.Type == DeviceTaskType.RestartDevice
            && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered));
    }

    public async Task<HttpClient> ClientAsAsync(string email) =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(email));

    /// <summary>Creates a group through the API as the IT administrator and returns its id.</summary>
    public async Task<Guid> CreateGroupAsync(HttpClient client, string name, params Guid[] deviceIds)
    {
        var response = await client.PostAsync(Groups(), Json(new { name, deviceIds }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// An IT administrator without all-device scope, scoped to exactly the given
    /// groups -- the shape deny-by-default applies to.
    /// </summary>
    public async Task<HttpClient> ScopedAdminAsync(params Guid[] groupIds)
    {
        var email = $"scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == SystemRoles.ItAdministrator);

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            foreach (var groupId in groupIds)
            {
                db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, groupId));
            }

            await db.SaveChangesAsync();
        }

        return await ClientAsAsync(email);
    }

    /// <summary>A queued restart for a device, created directly, as an individual restart would.</summary>
    public async Task<Guid> SeedQueuedRestartAsync(Guid deviceId, bool delivered = false)
    {
        await using var db = _fixture.CreateDbContext();
        var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
        var actor = await db.PlatformUsers.Select(u => u.Id).FirstAsync();

        var task = DeviceTask.Create(
            device.OrganizationId, deviceId, DeviceTaskType.RestartDevice,
            payloadJson: """{"graceSeconds":600,"message":"Your IT administrator scheduled a restart in 10 minutes."}""",
            createdByUserId: actor, createdByDisplay: "seed", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));
        if (delivered)
        {
            task.TryDeliver(DateTimeOffset.UtcNow).ShouldBeTrue();
        }

        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    public static IReadOnlyDictionary<Guid, JsonElement> ByDevice(JsonElement devices) =>
        devices.EnumerateArray().ToDictionary(d => d.GetProperty("deviceId").GetGuid());
}
