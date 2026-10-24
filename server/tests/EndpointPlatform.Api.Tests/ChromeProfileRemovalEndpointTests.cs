using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Devices are seeded at agent 1.25.0 except where the test is about the gate;
/// the fleet's real versions are below it until the rollout, and the route must
/// say so with a 409 rather than queue work nothing will run.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ChromeProfileRemovalEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    /// <summary>The oldest agent that carries the profile removal executor.</summary>
    private const string CapableAgent = "1.25.0";

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

    // -------------------------------------------------------------- agent gate

    [Fact]
    public async Task A_device_on_an_agent_below_the_floor_is_a_conflict_not_a_queued_task()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: "1.24.0");
        var profileId = await SeedProfileAsync(device);

        var response = await client.PostAsync(Remove(profileId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "the profile is right there; pretending otherwise would send the operator hunting a ghost");
        (await response.Content.ReadAsStringAsync()).ShouldContain("1.25.0",
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
