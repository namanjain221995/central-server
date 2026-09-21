using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Creating a local account: the configuration profile, the account-type default,
/// the extra permission needed to mint an administrator, and the group allow-list.
/// </summary>
/// <remarks>
/// These assert what reaches the <em>task payload</em>, because that is the only
/// thing the endpoint actually produces. Whether Windows then honours it is proven
/// by the agent's own tests and by live verification — the two halves are checked
/// separately on purpose.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class CreateLocalUserEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private async Task<Guid> SeedDeviceAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var token = new EnrollmentToken(org.Id, $"cu-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "CU-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    /// <summary>Records the local groups a device has reported, as inventory would.</summary>
    private async Task SeedDeviceGroupsAsync(Guid deviceId, params string[] groupNames)
    {
        await using var db = _fixture.CreateDbContext();
        var index = 0;
        foreach (var name in groupNames)
        {
            db.DeviceLocalGroups.Add(new DeviceLocalGroup(
                deviceId,
                string.Equals(name, "Administrators", StringComparison.OrdinalIgnoreCase)
                    ? DeviceLocalGroup.AdministratorsSid
                    : $"S-1-5-32-{600 + index++}",
                name, null, "[]", 0, DateTimeOffset.UtcNow));
        }

        await db.SaveChangesAsync();
    }

    private static Uri Profiles(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/local-user-profiles", UriKind.Relative);

    private static Uri Users(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/local-users", UriKind.Relative);

    /// <summary>Reads the queued task's payload, which is what the endpoint really emits.</summary>
    private async Task<JsonElement> QueuedCreatePayloadAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var payload = await db.DeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.CreateLocalUser)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.PayloadJson)
            .FirstAsync();

        return JsonDocument.Parse(payload!).RootElement.Clone();
    }

    // ------------------------------------------------------- account type

    [Fact]
    public async Task A_request_without_an_account_type_creates_a_standard_user()
    {
        // The dangerous default would be administrator; assert the safe one explicitly.
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "PlainUser", password = "LongEnoughPassword1!", enabled = true,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await QueuedCreatePayloadAsync(deviceId)).GetProperty("administrator").GetBoolean()
            .ShouldBeFalse("a request that does not ask for administrator must never get it");
    }

    [Fact]
    public async Task An_unrecognised_account_type_is_rejected_rather_than_defaulted()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "OddType", password = "LongEnoughPassword1!", accountType = "Root",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_operator_with_change_type_can_create_an_administrator()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "AdminUser", password = "LongEnoughPassword1!", accountType = "Administrator",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await QueuedCreatePayloadAsync(deviceId)).GetProperty("administrator").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Creating_an_administrator_requires_the_change_type_permission()
    {
        // user.create alone must not be a route around the change-type gate: creating
        // an administrator grants exactly what promoting one grants.
        var deviceId = await SeedDeviceAsync();
        var email = $"creator-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

            var role = Role.CreateCustom(org.Id, $"creator_{Guid.CreateVersion7():N}", "Creator", "user.create only");
            var permissions = await db.Permissions
                .Where(p => p.Key == Permissions.LocalUser.Create
                         || p.Key == Permissions.LocalUser.View
                         || p.Key == Permissions.Device.View)
                .ToListAsync();
            foreach (var permission in permissions)
            {
                role.GrantPermission(permission.Id);
            }

            db.Roles.Add(role);
            await db.SaveChangesAsync();

            var user = new PlatformUser(org.Id, email, "Creator Only");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var token = await _fixture.SignInAsync(email);
        using var client = _fixture.CreateClientFor(token);

        // Standard user: allowed.
        (await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "OkStandard", password = "LongEnoughPassword1!", accountType = "StandardUser",
        })).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Administrator: refused.
        (await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "NotAllowedAdmin", password = "LongEnoughPassword1!", accountType = "Administrator",
        })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------ groups

    [Fact]
    public async Task Administrators_cannot_be_smuggled_in_as_an_additional_group()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "SneakyAdmin", password = "LongEnoughPassword1!",
            additionalGroups = new[] { "Administrators" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_unlisted_group_is_refused()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "OddGroup", password = "LongEnoughPassword1!",
            additionalGroups = new[] { "Some Random Group" },
        })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_permitted_group_reaches_the_task_payload()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "RdpUser", password = "LongEnoughPassword1!",
            additionalGroups = new[] { "Remote Desktop Users" },
        })).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var groups = (await QueuedCreatePayloadAsync(deviceId)).GetProperty("additionalGroups");
        groups.EnumerateArray().Select(g => g.GetString()).ShouldContain("Remote Desktop Users");
    }

    // ----------------------------------------------------------- profiles

    [Fact]
    public async Task An_unknown_profile_is_rejected()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "GhostProfile", password = "LongEnoughPassword1!", profileKey = "not_a_profile",
        })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_profiles_endpoint_lists_the_baselines()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(Profiles(deviceId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = doc.RootElement.GetProperty("profiles").EnumerateArray()
            .Select(p => p.GetProperty("key").GetString()).ToList();

        keys.ShouldContain(UserConfigurationProfiles.StandardEmployee);
        keys.ShouldContain(UserConfigurationProfiles.ItAdministrator);

        doc.RootElement.GetProperty("permittedAdditionalGroups").EnumerateArray()
            .Select(g => g.GetString()).ShouldNotContain("Administrators");
    }

    [Fact]
    public async Task Only_groups_the_device_reported_are_offered()
    {
        // The allow-list is a policy ceiling, not a claim that a machine has them.
        // Offering a group this device lacks invites a request it can only
        // half-honour.
        var deviceId = await SeedDeviceAsync();
        await SeedDeviceGroupsAsync(deviceId, "Users", "Administrators", "Guests", "IIS_IUSRS");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(Profiles(deviceId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var offered = doc.RootElement.GetProperty("permittedAdditionalGroups")
            .EnumerateArray().Select(g => g.GetString()).ToList();

        offered.ShouldContain("Users");
        offered.ShouldNotContain("Remote Desktop Users", "this device did not report that group");
        offered.ShouldNotContain("Backup Operators", "this device did not report that group");
        offered.ShouldNotContain("Administrators", "administrator rights are an account type, not a group");
        offered.ShouldNotContain("Guests", "reported by the device but not permitted by policy");

        doc.RootElement.GetProperty("deviceGroupsKnown").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_baseline_never_preselects_a_group_this_device_lacks()
    {
        // Regression: the IT Administrator baseline preselected "Remote Desktop
        // Users", so on a Windows edition without it the form opened already asking
        // for something the machine could not provide.
        var deviceId = await SeedDeviceAsync();
        await SeedDeviceGroupsAsync(deviceId, "Users", "Administrators");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync(Profiles(deviceId))).Content.ReadAsStringAsync());

        var offered = doc.RootElement.GetProperty("permittedAdditionalGroups")
            .EnumerateArray().Select(g => g.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in doc.RootElement.GetProperty("profiles").EnumerateArray())
        {
            foreach (var group in profile.GetProperty("additionalGroups").EnumerateArray())
            {
                offered.ShouldContain(group.GetString(),
                    $"profile '{profile.GetProperty("key").GetString()}' preselected a group not offered here");
            }
        }

        // The administrator baseline is still an administrator baseline - the fix
        // removed a group, not the rights.
        var itAdmin = doc.RootElement.GetProperty("profiles").EnumerateArray()
            .Single(p => p.GetProperty("key").GetString() == UserConfigurationProfiles.ItAdministrator);
        itAdmin.GetProperty("grantsAdministrator").GetBoolean().ShouldBeTrue();
        itAdmin.GetProperty("accountType").GetString().ShouldBe("Administrator");
    }

    [Fact]
    public async Task A_device_that_never_reported_groups_still_offers_the_allow_list()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync(Profiles(deviceId))).Content.ReadAsStringAsync());

        doc.RootElement.GetProperty("deviceGroupsKnown").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("permittedAdditionalGroups")
            .EnumerateArray().Select(g => g.GetString())
            .ShouldContain("Users", "no inventory means unknown, not empty");
    }

    [Fact]
    public async Task An_auditor_cannot_create_a_user()
    {
        var deviceId = await SeedDeviceAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsJsonAsync(Users(deviceId), new
        {
            username = "AuditorTried", password = "LongEnoughPassword1!",
        })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
