using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Tasks;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Restart Management over real HTTP: the contract of the routes, who may call
/// them, and what a schedule looks like from the console's side.
/// </summary>
/// <remarks>
/// Dispatch itself is the sweeper's work and is tested at the service level
/// with a clock the test controls. Here every schedule is far enough away that
/// the host's live sweeper never reaches it, so a test only ever sees what it
/// created.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class RestartScheduleEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly GroupTestSupport _support = new(fixture);

    private static readonly Uri Schedules = new("/admin/v1/restart-schedules", UriKind.Relative);

    private static Uri ForGroup(Guid groupId) => new($"/admin/v1/restart-schedules/groups/{groupId}", UriKind.Relative);

    private static Uri Schedule(Guid id) => new($"/admin/v1/restart-schedules/{id}", UriKind.Relative);

    private static Uri Cancel(Guid id) => new($"/admin/v1/restart-schedules/{id}/cancel", UriKind.Relative);

    private const int FarAway = 1800;

    private Task<HttpClient> ItAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<(HttpClient Client, Guid Group, Guid Online, Guid Offline)> FleetAsync(string name)
    {
        var client = await ItAdminAsync();
        var online = await _support.SeedDeviceAsync(agentVersion: "1.14.0");
        var offline = await _support.SeedDeviceAsync(online: false);
        var group = await _support.CreateGroupAsync(client, UniqueName(name), online, offline);
        return (client, group, online, offline);
    }

    // ---------------------------------------------------------------- create

    [Fact]
    public async Task Scheduling_returns_the_plan_and_sends_nothing()
    {
        var (client, group, online, offline) = await FleetAsync("Plan");
        using var _ = client;

        var response = await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await BodyAsync(response);
        body.GetProperty("status").GetString().ShouldBe("Pending");
        body.GetProperty("groupId").GetGuid().ShouldBe(group);
        body.GetProperty("requestedDelaySeconds").GetInt32().ShouldBe(FarAway);
        body.GetProperty("warningSeconds").GetInt32().ShouldBe(300);
        body.GetProperty("canCancelCleanly").GetBoolean().ShouldBeTrue();
        body.GetProperty("createdByDisplay").GetString().ShouldBe(AdminApiPostgresFixture.ItAdminEmail);
        (body.GetProperty("restartAt").GetDateTimeOffset() - body.GetProperty("dispatchAt").GetDateTimeOffset())
            .ShouldBe(TimeSpan.FromSeconds(300));

        var devices = ByDevice(body.GetProperty("devices"));
        devices[online].GetProperty("state").GetString().ShouldBe("WillRestart");
        devices[online].GetProperty("supportsCancel").GetBoolean().ShouldBeTrue();
        devices[offline].GetProperty("state").GetString().ShouldBe("OfflineNow");
        devices[offline].GetProperty("supportsCancel").GetBoolean().ShouldBeFalse("seeded at 1.9.0");

        (await _support.TasksAsync(online, DeviceTaskType.RestartDevice)).ShouldBe(0);
        response.Headers.Location!.ToString().ShouldEndWith(body.GetProperty("id").GetGuid().ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(59)]
    [InlineData(-600)]
    [InlineData(7 * 24 * 3600 + 1)]
    public async Task A_delay_outside_the_rules_is_refused_with_400(int delaySeconds)
    {
        var (client, group, _, _) = await FleetAsync("Delay");
        using var _ = client;

        var response = await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_missing_group_or_delay_is_a_400_and_an_unknown_group_a_404()
    {
        using var client = await ItAdminAsync();

        (await client.PostAsync(Schedules, Json(new { delaySeconds = FarAway }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsync(Schedules, Json(new { groupId = Guid.CreateVersion7() }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsync(Schedules, Json(new { groupId = Guid.CreateVersion7(), delaySeconds = FarAway }))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(ForGroup(Guid.CreateVersion7()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(Schedule(Guid.CreateVersion7()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_second_schedule_for_the_same_department_is_a_409_naming_the_first()
    {
        var (client, group, _, _) = await FleetAsync("Twice");
        using var _ = client;
        var first = await BodyAsync(await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway })));

        var response = await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = 7200 }));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyAsync(response)).GetProperty("scheduleId").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
    }

    // ------------------------------------------------------------------ read

    [Fact]
    public async Task The_department_view_shows_the_active_schedule_and_the_group_counts()
    {
        var (client, group, _, _) = await FleetAsync("Overview");
        using var _ = client;
        var created = await BodyAsync(await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway })));

        var overview = await client.GetFromJsonAsync<JsonElement>(ForGroup(group));

        overview.GetProperty("group").GetProperty("id").GetGuid().ShouldBe(group);
        overview.GetProperty("group").GetProperty("deviceCount").GetInt32().ShouldBe(2);
        overview.GetProperty("group").GetProperty("onlineCount").GetInt32().ShouldBe(1);
        overview.GetProperty("active").GetProperty("id").GetGuid().ShouldBe(created.GetProperty("id").GetGuid());
        overview.GetProperty("recent").GetArrayLength().ShouldBe(0);

        var one = await client.GetFromJsonAsync<JsonElement>(Schedule(created.GetProperty("id").GetGuid()));
        one.GetProperty("groupName").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public async Task Cancelling_for_the_department_ends_the_schedule_and_moves_it_to_history()
    {
        var (client, group, online, _) = await FleetAsync("CancelAll");
        using var _ = client;
        var id = (await BodyAsync(await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway })))).GetProperty("id").GetGuid();

        var response = await client.PostAsync(Cancel(id), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("schedule").GetProperty("status").GetString().ShouldBe("Cancelled");
        body.GetProperty("schedule").GetProperty("cancelledByDisplay").GetString().ShouldBe(AdminApiPostgresFixture.ItAdminEmail);
        body.GetProperty("devices").GetArrayLength().ShouldBe(0);

        var overview = await client.GetFromJsonAsync<JsonElement>(ForGroup(group));
        overview.GetProperty("active").ValueKind.ShouldBe(JsonValueKind.Null);
        overview.GetProperty("recent")[0].GetProperty("id").GetGuid().ShouldBe(id);

        (await client.PostAsync(Cancel(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "nothing left to cancel");
        (await _support.TasksAsync(online, DeviceTaskType.RestartDevice)).ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_for_selected_devices_keeps_the_schedule_and_excludes_them()
    {
        var (client, group, online, offline) = await FleetAsync("CancelSome");
        using var _ = client;
        var id = (await BodyAsync(await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway })))).GetProperty("id").GetGuid();

        var response = await client.PostAsync(Cancel(id), Json(new { deviceIds = new[] { online } }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("schedule").GetProperty("status").GetString().ShouldBe("Pending");
        var results = ByDevice(body.GetProperty("devices"));
        results[online].GetProperty("outcome").GetString().ShouldBe("Excluded");
        var devices = ByDevice(body.GetProperty("schedule").GetProperty("devices"));
        devices[online].GetProperty("state").GetString().ShouldBe("Excluded");
        devices[offline].GetProperty("state").GetString().ShouldBe("OfflineNow");

        (await client.PostAsync(Cancel(id), Json(new { deviceIds = Array.Empty<Guid>() }))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest, "an empty list is neither 'these' nor 'all'");
    }

    /// <summary>
    /// "Select all" on a large department sends every id at once. The route's
    /// body limit has to admit the largest list the handler accepts, or the
    /// request dies as a 413 before the handler can say anything.
    /// </summary>
    [Fact]
    public async Task Cancelling_for_the_largest_allowed_device_list_reaches_the_handler()
    {
        var (client, group, _, _) = await FleetAsync("CancelMany");
        using var _ = client;
        var id = (await BodyAsync(await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway })))).GetProperty("id").GetGuid();
        var strangers = Enumerable.Range(0, 500).Select(_ => Guid.CreateVersion7()).ToArray();

        var response = await client.PostAsync(Cancel(id), Json(new { deviceIds = strangers }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("devices").GetArrayLength().ShouldBe(500);
        body.GetProperty("devices")[0].GetProperty("outcome").GetString().ShouldBe("NotInSchedule");

        (await client.PostAsync(Cancel(id), Json(new { deviceIds = strangers.Append(Guid.CreateVersion7()).ToArray() }))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest, "one over the limit is refused with a reason, not a 413");
    }

    // ------------------------------------------------------------ authority

    [Fact]
    public async Task Only_holders_of_the_restart_permission_may_schedule_or_cancel()
    {
        var (client, group, _, _) = await FleetAsync("Rbac");
        using var _ = client;
        var id = (await BodyAsync(await client.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway })))).GetProperty("id").GetGuid();

        using var auditor = await _support.ClientAsAsync(AdminApiPostgresFixture.AuditorEmail);
        (await auditor.GetAsync(ForGroup(group))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditor.PostAsync(Schedules, Json(new { groupId = group, delaySeconds = FarAway }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditor.PostAsync(Cancel(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Helpdesk may restart a device, so it may schedule one too.
        using var helpdesk = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        (await helpdesk.GetAsync(ForGroup(group))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var anonymous = fixture.Factory.CreateClient();
        (await anonymous.GetAsync(ForGroup(group))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_scoped_administrator_sees_only_their_own_departments()
    {
        var (owner, theirs, _, _) = await FleetAsync("ScopedA");
        using var _ = owner;
        var other = await _support.SeedDeviceAsync();
        var elsewhere = await _support.CreateGroupAsync(owner, UniqueName("ScopedB"), other);

        using var scoped = await _support.ScopedAdminAsync(theirs);
        (await scoped.GetAsync(ForGroup(theirs))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scoped.GetAsync(ForGroup(elsewhere))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.PostAsync(Schedules, Json(new { groupId = elsewhere, delaySeconds = FarAway }))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound, "a group outside scope does not exist for them");

        var created = await scoped.PostAsync(Schedules, Json(new { groupId = theirs, delaySeconds = FarAway }));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await BodyAsync(created)).GetProperty("id").GetGuid();

        // The schedule belongs to the group: the owner sees it, another scoped admin does not.
        (await owner.GetAsync(Schedule(id))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var outsider = await _support.ScopedAdminAsync(elsewhere);
        (await outsider.GetAsync(Schedule(id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.PostAsync(Cancel(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
