using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The Chrome profile removal route over real HTTP against real PostgreSQL: the
/// 202 that queues a <c>RemoveChromeProfile</c> task, and every refusal around it.
/// </summary>
/// <remarks>
/// <para>
/// The weight here is on the payload, because it is the part most easily got
/// wrong: the task must carry the STORED row's SID, key, path and account --
/// there is no request body to echo -- under the exact camelCase names the
/// deployed executor parses, with a signed-out profile travelling as a null
/// <c>expectedEmail</c> rather than an empty string the agent would refuse.
/// </para>
/// <para>
/// Devices are seeded at agent 1.26.0 except where the test is about the gate;
/// the fleet's real versions are below it until the rollout, and the route must
/// say so with a 409 rather than queue work that will fail or confuse -- which
/// since the gate moved includes 1.25.0, whose executor fails fast while Chrome
/// is open instead of scheduling the removal.
/// </para>
/// <para>
/// The optional <c>restartDevice</c> body is covered here too: absent and false
/// change nothing; true queues the restart route's exact task after the removal,
/// demands the restart action's own permission, and never stacks a second restart
/// on a device that already has one coming.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ChromeProfileRemovalEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    /// <summary>The oldest agent whose executor defers the removal while Chrome is open.</summary>
    private const string CapableAgent = "1.26.0";

    private const string Sid = "S-1-5-21-4000-5000-6000-1001";
    private const string Email = "someone@example.com";

    private static Uri Remove(Guid profileId) =>
        new($"/admin/v1/chrome/profiles/{profileId}/actions/remove", UriKind.Relative);

    private Task<HttpClient> ItAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);

    private static string PathOf(string key) =>
        @"C:\Users\user\AppData\Local\Google\Chrome\User Data\" + key;

    /// <summary>
    /// Seeds the device's Chrome installation with one profile and returns the
    /// profile row's id -- the one the device Chrome view hands the console.
    /// </summary>
    private async Task<Guid> SeedProfileAsync(Guid deviceId, string key = "Profile 1", string? email = Email)
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;

        var installation = await db.ChromeInstallations.SingleOrDefaultAsync(i => i.DeviceId == deviceId);
        if (installation is null)
        {
            installation = new ChromeInstallation(deviceId);
            installation.Apply(
                ChromeReportStatus.Available, "131.0.6778.86",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe", "x64", "stable", "Machine",
                null, null, null, now);
            db.ChromeInstallations.Add(installation);
        }

        var profile = new ChromeProfile(
            deviceId, installation.Id, Sid, @"WORKGROUP\user", key, "Person 1", PathOf(key),
            isManaged: false, now.AddMinutes(-30), now, email, signedIn: email is null ? null : true);
        db.ChromeProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private async Task<List<DeviceTask>> RemovalTasksAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.RemoveChromeProfile)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
    }

    private async Task<int> AuditCountAsync(Guid deviceId, string action)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AuditLogEntries.CountAsync(a => a.DeviceId == deviceId && a.Action == action);
    }

    /// <summary>A capable device with one profile in a second organization.</summary>
    private async Task<Guid> SeedForeignProfileAsync()
    {
        Guid deviceId;
        await using (var db = _fixture.CreateDbContext())
        {
            var org = new Organization("Other Org", ("p" + Guid.CreateVersion7().ToString("N"))[..20]);
            db.Organizations.Add(org);
            await db.SaveChangesAsync();

            var token = new EnrollmentToken(
                org.Id, $"chrprof-foreign-{Guid.CreateVersion7():N}",
                Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
                Guid.CreateVersion7(), "seed", DateTimeOffset.UtcNow.AddHours(1), 1);
            db.EnrollmentTokens.Add(token);

            var device = Device.Enroll(
                org.Id, "CHRPROF-FOREIGN", "m-" + Guid.CreateVersion7().ToString("N"),
                CapableAgent, "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            deviceId = device.Id;
        }

        return await SeedProfileAsync(deviceId);
    }

    // -------------------------------------------------------------- happy path

    [Fact]
    public async Task Removing_a_profile_queues_a_task_built_from_the_stored_row()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device, key: "Profile 1", email: Email);

        var response = await client.PostAsync(Remove(profileId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted,
            "the server accepted and queued the work; whether the endpoint removes the profile is the task result's to tell");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = body.GetProperty("taskId").GetGuid();
        taskId.ShouldNotBe(Guid.Empty);
        body.GetProperty("restartTaskId").ValueKind.ShouldBe(JsonValueKind.Null,
            "no body means what it always has: remove, and nothing else");

        var tasks = await RemovalTasksAsync(device);
        tasks.Count.ShouldBe(1);
        tasks[0].Id.ShouldBe(taskId);
        tasks[0].Status.ShouldBe(DeviceTaskStatus.Queued);

        // The payload is the stored row under the exact camelCase names the
        // deployed executor parses -- and nothing else travels.
        using var payload = JsonDocument.Parse(tasks[0].PayloadJson!);
        payload.RootElement.EnumerateObject().Count().ShouldBe(4);
        payload.RootElement.GetProperty("userSid").GetString().ShouldBe(Sid);
        payload.RootElement.GetProperty("profileKey").GetString().ShouldBe("Profile 1");
        payload.RootElement.GetProperty("expectedEmail").GetString().ShouldBe(Email);
        payload.RootElement.GetProperty("profilePath").GetString().ShouldBe(PathOf("Profile 1"));

        (await AuditCountAsync(device, "chrome.profile.remove")).ShouldBe(1,
            "the decision is audited besides the task the task service records");
        (await AuditCountAsync(device, "task.queue.removechromeprofile")).ShouldBe(1);
    }

    [Fact]
    public async Task A_signed_out_profile_travels_with_a_null_expected_email()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device, key: "Profile 2", email: null);

        (await client.PostAsync(Remove(profileId), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var tasks = await RemovalTasksAsync(device);
        using var payload = JsonDocument.Parse(tasks.Single().PayloadJson!);

        payload.RootElement.GetProperty("expectedEmail").ValueKind.ShouldBe(JsonValueKind.Null,
            "null means 'the console saw no account'; an empty string would make the agent refuse the task");
    }

    // --------------------------------------------------------- optional restart

    [Fact]
    public async Task Removing_with_restart_queues_the_restart_routes_exact_task_after_the_removal()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device, key: "Profile 3");

        var response = await client.PostAsync(Remove(profileId), Json(new { restartDevice = true }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = body.GetProperty("taskId").GetGuid();
        var restartTaskId = body.GetProperty("restartTaskId").GetGuid();
        restartTaskId.ShouldNotBe(Guid.Empty);

        var removal = (await RemovalTasksAsync(device)).ShouldHaveSingleItem();
        removal.Id.ShouldBe(taskId);

        await using var db = _fixture.CreateDbContext();
        var restart = await db.DeviceTasks.AsNoTracking().SingleAsync(
            t => t.DeviceId == device && t.Type == DeviceTaskType.RestartDevice);
        restart.Id.ShouldBe(restartTaskId);
        restart.Status.ShouldBe(DeviceTaskStatus.Queued);
        restart.CreatedAt.ShouldBeGreaterThanOrEqualTo(removal.CreatedAt,
            "the device claims by creation order, and a scheduled removal finishes at the boot the restart causes");

        // Exactly the restart route's payload: the immediate grace period and the
        // system-defined notice -- nothing bespoke to this feature.
        using var payload = JsonDocument.Parse(restart.PayloadJson!);
        payload.RootElement.GetProperty("graceSeconds").GetInt32().ShouldBe(RestartGrace.ImmediateSeconds);
        payload.RootElement.GetProperty("message").GetString()
            .ShouldBe(RestartGrace.MessageFor(RestartGrace.ImmediateSeconds));

        (await AuditCountAsync(device, "chrome.profile.remove")).ShouldBe(1);
        (await AuditCountAsync(device, "task.queue.restartdevice")).ShouldBe(1,
            "the restart keeps the audit the restart action has always written");
    }

    [Fact]
    public async Task An_explicit_restartDevice_false_is_the_bodiless_request()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device, key: "Profile 4");

        var response = await client.PostAsync(Remove(profileId), Json(new { restartDevice = false }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("restartTaskId").ValueKind.ShouldBe(JsonValueKind.Null);

        (await RemovalTasksAsync(device)).Count.ShouldBe(1);
        (await _support.TasksAsync(device, DeviceTaskType.RestartDevice)).ShouldBe(0);
    }

    /// <summary>
    /// chrome.manage decides what Chrome holds; whether a machine goes down is
    /// device.restart's -- the same permission the restart action requires -- so a
    /// caller holding only the former may remove, but a removal-with-restart is
    /// refused whole, before anything is queued.
    /// </summary>
    [Fact]
    public async Task Restarting_demands_the_restart_permission_on_top_of_chrome_manage()
    {
        using var client = await ChromeManagerWithoutRestartAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device, key: "Profile 5");

        var refused = await client.PostAsync(Remove(profileId), Json(new { restartDevice = true }));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).ShouldContain(Permissions.Device.Restart,
            customMessage: "the refusal names the permission the restart action itself requires");
        (await RemovalTasksAsync(device)).ShouldBeEmpty("a half-granted request queues nothing at all");
        (await _support.TasksAsync(device, DeviceTaskType.RestartDevice)).ShouldBe(0);

        // The same caller may still remove without the restart.
        (await client.PostAsync(Remove(profileId), Json(new { restartDevice = false })))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await RemovalTasksAsync(device)).Count.ShouldBe(1);
    }

    /// <summary>
    /// A device that already has a restart queued or delivered keeps it: the reboot
    /// is already coming (and the database allows only one in flight), so the
    /// removal is queued, no second restart is, and the response says so with a
    /// null restartTaskId.
    /// </summary>
    [Fact]
    public async Task A_restart_already_in_flight_is_kept_not_doubled()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device, key: "Profile 6");
        await _support.SeedQueuedRestartAsync(device);

        var response = await client.PostAsync(Remove(profileId), Json(new { restartDevice = true }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted,
            "the removal is the request; the restart it asked for is already happening");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("restartTaskId").ValueKind.ShouldBe(JsonValueKind.Null);

        (await RemovalTasksAsync(device)).Count.ShouldBe(1);
        (await _support.ActiveRestartsAsync(device)).ShouldBe(1, "the in-flight restart, and only it");
    }

    /// <summary>
    /// An administrator holding chrome.manage but not device.restart. No built-in
    /// role is shaped like this, which is exactly why the guard must be its own
    /// check rather than inherited from whoever happens to hold chrome.manage today.
    /// </summary>
    private async Task<HttpClient> ChromeManagerWithoutRestartAsync()
    {
        var email = $"chrome-only-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = Role.CreateCustom(
                org.Id, $"chrome_only_{Guid.CreateVersion7():N}", "Chrome Only", "chrome.manage without device.restart");
            var manage = await db.Permissions.SingleAsync(p => p.Key == Permissions.Chrome.Manage);
            role.GrantPermission(manage.Id);
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            var user = new PlatformUser(org.Id, email, "Chrome Only");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        return await _support.ClientAsAsync(email);
    }

    // -------------------------------------------------------------- agent gate

    /// <summary>
    /// 1.25.0 deliberately: it HAS a removal executor, but one that fails fast while
    /// Chrome is open instead of scheduling the removal -- so since the deferred
    /// semantics shipped in 1.26.0 it gets the same clean "update the agent" 409 as
    /// anything older, never a task whose failure would read like the feature breaking.
    /// </summary>
    [Theory]
    [InlineData("1.25.0")]
    [InlineData("1.24.0")]
    public async Task A_device_on_an_agent_below_the_floor_is_a_conflict_not_a_queued_task(string agentVersion)
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: agentVersion);
        var profileId = await SeedProfileAsync(device);

        var response = await client.PostAsync(Remove(profileId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "the profile is right there; pretending otherwise would send the operator hunting a ghost");
        (await response.Content.ReadAsStringAsync()).ShouldContain("1.26.0",
            customMessage: "the refusal names the version to update to");

        (await RemovalTasksAsync(device)).ShouldBeEmpty();
        (await AuditCountAsync(device, "chrome.profile.remove")).ShouldBe(0);
    }

    // --------------------------------------------------------------- not found

    [Fact]
    public async Task A_profile_that_does_not_exist_is_not_found()
    {
        using var client = await ItAdminAsync();

        (await client.PostAsync(Remove(Guid.CreateVersion7()), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_profile_of_another_organization_is_not_found()
    {
        using var client = await ItAdminAsync();
        var foreignProfile = await SeedForeignProfileAsync();

        (await client.PostAsync(Remove(foreignProfile), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_profile_on_a_retired_device_is_not_found()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device);

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.Devices.SingleAsync(d => d.Id == device);
            stored.Retire();
            await db.SaveChangesAsync();
        }

        (await client.PostAsync(Remove(profileId), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound,
                "a retired device takes no work; its leftover inventory rows are history, not targets");
        (await RemovalTasksAsync(device)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------- scope

    [Fact]
    public async Task An_out_of_scope_profile_is_not_found_for_a_scoped_administrator()
    {
        using var admin = await ItAdminAsync();
        var inScope = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var elsewhere = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var inScopeProfile = await SeedProfileAsync(inScope);
        var elsewhereProfile = await SeedProfileAsync(elsewhere);
        var mine = await _support.CreateGroupAsync(admin, UniqueName("ChromeProfScope"), inScope);

        using var scoped = await _support.ScopedAdminAsync(mine);

        (await scoped.PostAsync(Remove(elsewhereProfile), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound,
                "404, never 403: a profile id must not confirm that an unreachable device exists");
        (await RemovalTasksAsync(elsewhere)).ShouldBeEmpty();

        (await scoped.PostAsync(Remove(inScopeProfile), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted, "the same caller may act inside their scope");
    }

    // ------------------------------------------------------------- permissions

    /// <summary>
    /// The read/write split the role catalogue pins: Helpdesk and Auditor hold
    /// <c>chrome.view</c> and can read the profile rows, but removing one is
    /// <c>chrome.manage</c>, which neither holds.
    /// </summary>
    [Theory]
    [InlineData(AdminApiPostgresFixture.HelpdeskEmail)]
    [InlineData(AdminApiPostgresFixture.AuditorEmail)]
    public async Task A_chrome_view_only_caller_is_forbidden(string email)
    {
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var profileId = await SeedProfileAsync(device);

        using var client = await _support.ClientAsAsync(email);

        (await client.PostAsync(Remove(profileId), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{email} must not delete what Chrome holds");
        (await RemovalTasksAsync(device)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected()
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsync(Remove(Guid.CreateVersion7()), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
