using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Local account management through the Admin API: permission gating, device scope,
/// the safety rules, and that every mutation becomes a typed task rather than a
/// direct write.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class LocalAccountEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string AdminSid = "S-1-5-21-1-2-3-1001";
    private const string StandardSid = "S-1-5-21-1-2-3-1002";

    /// <summary>Seeds a device with one admin and one standard local account.</summary>
    private async Task<Guid> SeedDeviceWithAccountsAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var token = new EnrollmentToken(org.Id, $"la-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(org.Id, "LA-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var now = DateTimeOffset.UtcNow;
        db.DeviceLocalUsers.Add(new DeviceLocalUser(
            device.Id, AdminSid, "LocalAdmin", "Local Admin", null, true, true, true, now, true, now));
        db.DeviceLocalUsers.Add(new DeviceLocalUser(
            device.Id, StandardSid, "Standard", "Standard User", null, true, true, true, now, false, now));
        var membersJson =
            "[{\"name\":\"LocalAdmin\",\"sid\":\"" + AdminSid + "\",\"memberType\":\"User\"}]";
        db.DeviceLocalGroups.Add(new DeviceLocalGroup(
            device.Id, DeviceLocalGroup.AdministratorsSid, "Administrators", null, membersJson, 1, now));

        await db.SaveChangesAsync();
        return device.Id;
    }

    private static Uri ChangeType(Guid deviceId, string sid) =>
        new($"/admin/v1/devices/{deviceId}/local-users/{sid}/change-account-type", UriKind.Relative);

    // ------------------------------------------------------------- happy path

    [Fact]
    public async Task Promoting_a_standard_user_queues_a_typed_task()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(
            ChangeType(deviceId, StandardSid), new { accountType = "Administrator" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleOrDefaultAsync(
            t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ChangeLocalUserType);

        task.ShouldNotBeNull("the mutation must go through the typed task pipeline");
        task!.PayloadJson.ShouldNotBeNull();
        task.PayloadJson!.ShouldContain(StandardSid, Case.Insensitive);
    }

    [Fact]
    public async Task Demoting_an_administrator_when_another_exists_is_allowed()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();

        // Add a second enabled administrator so the last-admin rule does not trip.
        await using (var db = _fixture.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.DeviceLocalUsers.Add(new DeviceLocalUser(
                deviceId, "S-1-5-21-1-2-3-1003", "SecondAdmin", null, null, true, true, true, now, true, now));
            await db.SaveChangesAsync();
        }

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(
            ChangeType(deviceId, AdminSid), new { accountType = "StandardUser" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // ------------------------------------------------------------ safety rules

    [Fact]
    public async Task Demoting_the_last_administrator_is_refused_before_any_task_is_queued()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(
            ChangeType(deviceId, AdminSid), new { accountType = "StandardUser" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ChangeLocalUserType))
            .ShouldBe(0, "a refused operation must not reach the device");
    }

    [Fact]
    public async Task Deleting_the_last_administrator_is_refused()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.DeleteAsync(
            new Uri($"/admin/v1/devices/{deviceId}/local-users/{AdminSid}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // -------------------------------------------------------------------- RBAC

    [Fact]
    public async Task An_auditor_cannot_change_an_account_type()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(
            ChangeType(deviceId, StandardSid), new { accountType = "Administrator" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_helpdesk_operator_cannot_change_an_account_type_but_can_disable()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsJsonAsync(ChangeType(deviceId, StandardSid), new { accountType = "Administrator" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden, "helpdesk deliberately lacks user.change_type");

        (await client.PostAsync(
                new Uri($"/admin/v1/devices/{deviceId}/local-users/{StandardSid}/disable", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted, "helpdesk holds user.disable");
    }

    [Fact]
    public async Task An_auditor_can_read_local_users()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(
            new Uri($"/admin/v1/devices/{deviceId}/local-users", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain(AdminSid);
    }

    [Fact]
    public async Task An_anonymous_caller_is_rejected()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        using var client = _fixture.CreateClientFor("not-a-session");

        (await client.GetAsync(new Uri($"/admin/v1/devices/{deviceId}/local-users", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------ device scope

    [Fact]
    public async Task An_administrator_without_device_scope_cannot_act_on_the_device()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();

        // A freshly created administrator holds every permission but no scope, so its
        // authority must reach no device at all - deny by default.
        var email = $"scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);
            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            // Deliberately NOT calling GrantAllDeviceScope().
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var token = await _fixture.SignInAsync(email);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsJsonAsync(ChangeType(deviceId, StandardSid), new { accountType = "Administrator" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden, "permission without scope must reach nothing");

        (await client.GetAsync(new Uri($"/admin/v1/devices/{deviceId}/local-users", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden, "scope narrows reads too");
    }

    [Fact]
    public async Task Group_scope_grants_access_to_member_devices_only()
    {
        var inScopeDevice = await SeedDeviceWithAccountsAsync();
        var outOfScopeDevice = await SeedDeviceWithAccountsAsync();

        var email = $"grouped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"Scope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            (await db.Devices.SingleAsync(candidate => candidate.Id == inScopeDevice)).MoveToGroup(group.Id);

            var user = new PlatformUser(org.Id, email, "Grouped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        var token = await _fixture.SignInAsync(email);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(new Uri($"/admin/v1/devices/{inScopeDevice}/local-users", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "the device is in a group this admin is scoped to");

        (await client.GetAsync(new Uri($"/admin/v1/devices/{outOfScopeDevice}/local-users", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a device outside the scoped group stays invisible");
    }

    // -------------------------------------------------------- input validation

    [Fact]
    public async Task An_invalid_account_type_is_rejected()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsJsonAsync(ChangeType(deviceId, StandardSid), new { accountType = "Root" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_reserved_username_cannot_be_created()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(
            new Uri($"/admin/v1/devices/{deviceId}/local-users", UriKind.Relative),
            new { username = "Administrator", password = "LongEnoughPassword1!", enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_short_password_is_rejected_before_a_secret_is_stored()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsJsonAsync(
            new Uri($"/admin/v1/devices/{deviceId}/local-users", UriKind.Relative),
            new { username = "ShortPw", password = "abc", enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------- password secrecy

    [Fact]
    public async Task A_created_users_password_never_appears_in_the_task_or_the_audit_trail()
    {
        var deviceId = await SeedDeviceWithAccountsAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        const string password = "Sup3rSecret-DoNotPersist!";
        var response = await client.PostAsJsonAsync(
            new Uri($"/admin/v1/devices/{deviceId}/local-users", UriKind.Relative),
            new { username = "PwTestUser", password, enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using var db = _fixture.CreateDbContext();

        var payloads = await db.DeviceTasks
            .Where(t => t.DeviceId == deviceId)
            .Select(t => t.PayloadJson)
            .ToListAsync();
        payloads.ShouldNotBeEmpty();
        payloads.ShouldAllBe(p => p == null || !p.Contains(password));

        // Project the jsonb columns as-is and inspect them client-side; concatenating
        // them in SQL would ask Postgres to add jsonb to text.
        var audits = await db.AuditLogEntries
            .Where(a => a.DeviceId == deviceId)
            .Select(a => new { a.Action, a.PreviousState, a.NewState })
            .ToListAsync();

        audits.ShouldNotBeEmpty("queuing the task must be audited");
        audits.ShouldAllBe(a =>
            !a.Action.Contains(password)
            && (a.PreviousState == null || !a.PreviousState.Contains(password))
            && (a.NewState == null || !a.NewState.Contains(password)));
    }
}
