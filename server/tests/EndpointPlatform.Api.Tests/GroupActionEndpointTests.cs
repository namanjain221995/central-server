using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Group actions: the server resolves the group's online members and queues the
/// ordinary per-device task for each. Nothing here is a group task.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class GroupActionEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    private Task<HttpClient> ItAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);

    /// <summary>A group with three online devices and one offline one.</summary>
    private async Task<(HttpClient Client, Guid Group, Guid[] Online, Guid Offline)> FleetAsync(string name)
    {
        var client = await ItAdminAsync();
        var online = new[] { await _support.SeedDeviceAsync(), await _support.SeedDeviceAsync(), await _support.SeedDeviceAsync() };
        var offline = await _support.SeedDeviceAsync(online: false);
        var group = await _support.CreateGroupAsync(client, UniqueName(name), [.. online, offline]);
        return (client, group, online, offline);
    }

    private async Task<DeviceTask> OnlyRestartAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.RestartDevice);
    }

    private static int GraceOf(DeviceTask task) =>
        JsonDocument.Parse(task.PayloadJson!).RootElement.GetProperty("graceSeconds").GetInt32();

    private static string MessageOf(DeviceTask task) =>
        JsonDocument.Parse(task.PayloadJson!).RootElement.GetProperty("message").GetString()!;

    // --------------------------------------------------------- online only

    /// <summary>The example from the requirement: four devices, one offline, three restart tasks.</summary>
    [Fact]
    public async Task Restart_targets_only_online_devices_and_queues_nothing_for_the_offline_one()
    {
        var (client, group, online, offline) = await FleetAsync("OnlineOnly");
        using var _ = client;

        var response = await client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = 60 }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var byDevice = ByDevice(body.GetProperty("devices"));

        foreach (var id in online)
        {
            byDevice[id].GetProperty("outcome").GetString().ShouldBe("Queued");
            (await _support.TasksAsync(id, DeviceTaskType.RestartDevice)).ShouldBe(1);
        }

        byDevice[offline].GetProperty("outcome").GetString().ShouldBe("Offline");
        (await _support.TasksAsync(offline, DeviceTaskType.RestartDevice))
            .ShouldBe(0, "an offline device must not receive a restart task");

        body.GetProperty("status").GetString().ShouldBe("QueuedWithIssues",
            "an offline device is an issue, never a success");
    }

    /// <summary>
    /// Online is decided on the server at queue time. A device that went offline
    /// after the page rendered it as online is not targeted.
    /// </summary>
    [Fact]
    public async Task A_device_that_went_offline_after_the_page_loaded_is_not_targeted()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Stale"), device);

        (await client.GetFromJsonAsync<JsonElement>(Group(group))).GetProperty("devices")[0]
            .GetProperty("isOnline").GetBoolean().ShouldBeTrue("the console saw it online");

        await _support.MakeOfflineAsync(device);

        var body = await (await client.PostAsync(Action(group, "restart"), content: null)).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("devices")[0].GetProperty("outcome").GetString().ShouldBe("Offline");
        (await _support.TasksAsync(device, DeviceTaskType.RestartDevice)).ShouldBe(0);
    }

    [Fact]
    public async Task A_group_with_no_online_devices_reports_that_and_queues_nothing()
    {
        using var client = await ItAdminAsync();
        var offline = await _support.SeedDeviceAsync(online: false);
        var group = await _support.CreateGroupAsync(client, UniqueName("AllDark"), offline);

        var body = await (await client.PostAsync(Action(group, "restart"), content: null)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("status").GetString().ShouldBe("NoEligibleDevices");
        (await _support.TasksAsync(offline, DeviceTaskType.RestartDevice)).ShouldBe(0);
    }

    [Fact]
    public async Task An_empty_group_reports_no_eligible_devices()
    {
        using var client = await ItAdminAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Hollow"));

        var body = await (await client.PostAsync(Action(group, "restart"), content: null)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("status").GetString().ShouldBe("NoEligibleDevices");
        body.GetProperty("devices").GetArrayLength().ShouldBe(0);
    }

    // ------------------------------------------------------- the timer contract

    /// <summary>Every online device gets the same grace period, and the same payload a single restart would.</summary>
    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 30)]
    [InlineData(600, 600)]
    [InlineData(3600, 3600)]
    public async Task Every_online_device_gets_the_same_grace_period_as_a_single_restart_would(int delay, int expectedGrace)
    {
        var (client, group, online, _) = await FleetAsync("Grace");
        using var __ = client;

        var response = await client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = delay }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("graceSeconds").GetInt32().ShouldBe(expectedGrace);

        foreach (var id in online)
        {
            var task = await OnlyRestartAsync(id);
            GraceOf(task).ShouldBe(expectedGrace);
            MessageOf(task).ShouldBe(RestartGrace.MessageFor(expectedGrace),
                "a group restart must deliver exactly the single-device payload");
            task.Type.ShouldBe(DeviceTaskType.RestartDevice, "there is no group task type");
        }
    }

    /// <summary>The group route and the single-device route produce byte-for-byte the same payload.</summary>
    [Fact]
    public async Task A_group_restart_payload_is_identical_to_a_single_device_restart_payload()
    {
        using var client = await ItAdminAsync();
        var single = await _support.SeedDeviceAsync();
        var member = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Identical"), member);

        await client.PostAsync(new Uri($"/admin/v1/devices/{single}/actions/restart", UriKind.Relative), Json(new { delaySeconds = 300 }));
        await client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = 300 }));

        (await OnlyRestartAsync(member)).PayloadJson.ShouldBe((await OnlyRestartAsync(single)).PayloadJson);
    }

    [Theory]
    [InlineData("""{"delaySeconds":1}""")]
    [InlineData("""{"delaySeconds":29}""")]
    [InlineData("""{"delaySeconds":3601}""")]
    [InlineData("""{"delaySeconds":-1}""")]
    [InlineData("""{"delaySeconds":1.5}""")]
    [InlineData("""{"delaySeconds":99999999999999999999}""")]
    [InlineData("not json")]
    public async Task An_invalid_timer_is_refused_and_queues_nothing_for_any_device(string body)
    {
        var (client, group, online, _) = await FleetAsync("BadTimer");
        using var __ = client;

        var response = await client.PostAsync(Action(group, "restart"), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        foreach (var id in online)
        {
            (await _support.TasksAsync(id, DeviceTaskType.RestartDevice)).ShouldBe(0, $"'{body}' must queue nothing");
        }
    }

    /// <summary>
    /// A quoted number is read as the number it contains -- ASP.NET's web JSON
    /// defaults allow it -- and then validated exactly as a bare number is.
    /// </summary>
    /// <remarks>
    /// The adversarial question is whether quoting is a way past the range or
    /// overflow checks. It is not: every quoted value lands on the same answer as
    /// its unquoted twin, and the group and single-device routes agree on every
    /// one, which is the requirement that a group restart has no timer contract of
    /// its own.
    /// </remarks>
    [Theory]
    [InlineData("\"60\"", HttpStatusCode.Accepted)]
    [InlineData("\"0\"", HttpStatusCode.Accepted)]
    [InlineData("\"7200\"", HttpStatusCode.BadRequest)]
    [InlineData("\"15\"", HttpStatusCode.BadRequest)]
    [InlineData("\"-1\"", HttpStatusCode.BadRequest)]
    [InlineData("\"99999999999999999999\"", HttpStatusCode.BadRequest)]
    [InlineData("\"1e3\"", HttpStatusCode.BadRequest)]
    [InlineData("\"abc\"", HttpStatusCode.BadRequest)]
    [InlineData("\" 60 \"", HttpStatusCode.BadRequest)]
    public async Task A_quoted_timer_gets_the_same_answer_on_the_group_and_single_device_routes(string quoted, HttpStatusCode expected)
    {
        using var client = await ItAdminAsync();
        var single = await _support.SeedDeviceAsync();
        var member = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Quoted"), member);
        var body = $$"""{"delaySeconds":{{quoted}}}""";

        var singleResponse = await client.PostAsync(new Uri($"/admin/v1/devices/{single}/actions/restart", UriKind.Relative), Json(body));
        var groupResponse = await client.PostAsync(Action(group, "restart"), Json(body));

        singleResponse.StatusCode.ShouldBe(expected, $"single-device route, {quoted}");
        groupResponse.StatusCode.ShouldBe(expected, $"group route, {quoted}");

        if (expected == HttpStatusCode.BadRequest)
        {
            (await _support.TasksAsync(member, DeviceTaskType.RestartDevice)).ShouldBe(0);
            (await _support.TasksAsync(single, DeviceTaskType.RestartDevice)).ShouldBe(0);
        }
    }

    /// <summary>
    /// The chunked-framing bypass the single-device route once had must not
    /// exist here: the body is bound by the framework, so framing changes nothing.
    /// </summary>
    [Theory]
    [InlineData("""{"delaySeconds":600}""", HttpStatusCode.Accepted)]
    [InlineData("""{"delaySeconds":7200}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"delaySeconds":15}""", HttpStatusCode.BadRequest)]
    [InlineData("not json", HttpStatusCode.BadRequest)]
    public async Task A_chunked_body_is_treated_exactly_like_a_content_length_body(string body, HttpStatusCode expected)
    {
        var (client, group, online, _) = await FleetAsync("Chunked");
        using var __ = client;

        var content = new UnknownLengthContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, Action(group, "restart")) { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(expected);
        var queued = 0;
        foreach (var id in online)
        {
            queued += await _support.TasksAsync(id, DeviceTaskType.RestartDevice);
        }

        if (expected == HttpStatusCode.Accepted)
        {
            GraceOf(await OnlyRestartAsync(online[0])).ShouldBe(600, "a chunked body must not fall back to an immediate restart");
        }
        else
        {
            queued.ShouldBe(0, "a refused chunked body must queue nothing");
        }
    }

    // --------------------------------------------- the client cannot pick devices

    /// <summary>
    /// The request names a group. Device ids smuggled into the body are not read:
    /// the server resolves membership itself.
    /// </summary>
    [Fact]
    public async Task Device_ids_in_the_request_body_are_ignored()
    {
        using var client = await ItAdminAsync();
        var member = await _support.SeedDeviceAsync();
        var outsider = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Inject"), member);

        var response = await client.PostAsync(Action(group, "restart"),
            Json(new { delaySeconds = 60, deviceIds = new[] { outsider }, devices = new[] { outsider } }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await _support.TasksAsync(member, DeviceTaskType.RestartDevice)).ShouldBe(1);
        (await _support.TasksAsync(outsider, DeviceTaskType.RestartDevice)).ShouldBe(0,
            "a device named in the body but not in the group must not be restarted");
    }

    // ------------------------------------------------------------ conflicts

    /// <summary>
    /// A device that already has a restart in flight is reported, and the rest of
    /// the group still restarts. One device must not cost the whole group.
    /// </summary>
    [Fact]
    public async Task An_individual_restart_in_flight_is_reported_and_the_rest_of_the_group_proceeds()
    {
        var (client, group, online, _) = await FleetAsync("Busy");
        using var __ = client;

        (await client.PostAsync(new Uri($"/admin/v1/devices/{online[1]}/actions/restart", UriKind.Relative), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = await (await client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = 120 })))
            .Content.ReadFromJsonAsync<JsonElement>();
        var byDevice = ByDevice(body.GetProperty("devices"));

        byDevice[online[0]].GetProperty("outcome").GetString().ShouldBe("Queued");
        byDevice[online[1]].GetProperty("outcome").GetString().ShouldBe("AlreadyInProgress");
        byDevice[online[2]].GetProperty("outcome").GetString().ShouldBe("Queued");

        (await _support.ActiveRestartsAsync(online[1])).ShouldBe(1, "the busy device must not get a second active restart");
        GraceOf(await OnlyRestartAsync(online[1])).ShouldBe(30, "and its existing restart is left exactly as it was");
    }

    /// <summary>
    /// Two group restarts racing each other: every device ends with exactly one
    /// active restart. The pre-check is a read-then-write; the unique index is
    /// what holds, and the losing insert must not poison the rest of the group.
    /// </summary>
    [Fact]
    public async Task Concurrent_group_restarts_leave_every_device_with_exactly_one_active_restart()
    {
        var (client, group, online, offline) = await FleetAsync("Race");
        using var __ = client;

        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = 300 }))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Accepted);

        foreach (var id in online)
        {
            (await _support.ActiveRestartsAsync(id)).ShouldBe(1, "no duplicate active restart rows may exist");
        }

        (await _support.TasksAsync(offline, DeviceTaskType.RestartDevice)).ShouldBe(0);

        // Across all six responses each device was queued exactly once; every
        // other attempt said it was already in progress rather than failing.
        var outcomes = new List<(Guid Device, string Outcome)>();
        foreach (var response in responses)
        {
            foreach (var d in (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices").EnumerateArray())
            {
                outcomes.Add((d.GetProperty("deviceId").GetGuid(), d.GetProperty("outcome").GetString()!));
            }
        }

        foreach (var id in online)
        {
            outcomes.Count(o => o.Device == id && o.Outcome == "Queued").ShouldBe(1);
            outcomes.Where(o => o.Device == id && o.Outcome != "Queued").ShouldAllBe(o => o.Outcome == "AlreadyInProgress");
        }
    }

    [Fact]
    public async Task A_group_restart_racing_an_individual_restart_leaves_one_active_restart()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("GroupVsOne"), device);

        await Task.WhenAll(
            client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = 60 })),
            client.PostAsync(new Uri($"/admin/v1/devices/{device}/actions/restart", UriKind.Relative), Json(new { delaySeconds = 60 })),
            client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = 60 })));

        (await _support.ActiveRestartsAsync(device)).ShouldBe(1);
    }

    // ---------------------------------------------------------- other actions

    [Theory]
    [InlineData("shutdown", DeviceTaskType.ShutdownDevice)]
    [InlineData("lock", DeviceTaskType.LockDevice)]
    [InlineData("signout", DeviceTaskType.SignOutUser)]
    public async Task Other_power_and_session_actions_fan_out_to_online_devices(string action, DeviceTaskType type)
    {
        var (client, group, online, offline) = await FleetAsync(action);
        using var __ = client;

        (await client.PostAsync(Action(group, action), content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        foreach (var id in online)
        {
            (await _support.TasksAsync(id, type)).ShouldBe(1);
        }

        (await _support.TasksAsync(offline, type)).ShouldBe(0);
    }

    [Fact]
    public async Task Shutdown_uses_the_single_device_payload()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("ShutdownPayload"), device);

        await client.PostAsync(Action(group, "shutdown"), content: null);

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.DeviceId == device && t.Type == DeviceTaskType.ShutdownDevice);
        GraceOf(task).ShouldBe(RestartGrace.ShutdownGraceSeconds);
        MessageOf(task).ShouldBe(RestartGrace.ShutdownMessage);
    }

    /// <summary>Sleep is not an action this platform has, so no group route pretends otherwise.</summary>
    [Fact]
    public async Task There_is_no_sleep_action()
    {
        using var client = await ItAdminAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("NoSleep"));

        (await client.PostAsync(Action(group, "sleep"), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Group_force_stop_goes_through_the_existing_force_stop_service_for_online_devices()
    {
        var (client, group, online, offline) = await FleetAsync("ForceStop");
        using var __ = client;

        var response = await client.PostAsync(Action(group, "force-stop"),
            Json(new { applicationName = "Nonexistent Application", publisher = (string?)null }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var byDevice = ByDevice((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices"));
        foreach (var id in online)
        {
            byDevice[id].GetProperty("outcome").GetString().ShouldBe("NotInstalled",
                "the existing service decides per device, from inventory");
        }

        byDevice[offline].GetProperty("outcome").GetString().ShouldBe("Offline");
    }

    [Fact]
    public async Task Group_force_stop_requires_an_application_name()
    {
        using var client = await ItAdminAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("ForceStopName"));

        (await client.PostAsync(Action(group, "force-stop"), Json(new { applicationName = "" })))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------ permission

    /// <summary>
    /// A group action takes exactly the permission its single-device equivalent
    /// takes. Helpdesk may restart and lock devices, so may restart and lock a
    /// group; it may not shut down or sign out, and may not do those to a group.
    /// </summary>
    [Fact]
    public async Task Each_group_action_requires_the_same_permission_as_the_device_action()
    {
        using var itAdmin = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(itAdmin, UniqueName("Perms"), device);
        using var helpdesk = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var auditor = await _support.ClientAsAsync(AdminApiPostgresFixture.AuditorEmail);

        (await helpdesk.PostAsync(Action(group, "lock"), content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await helpdesk.PostAsync(Action(group, "shutdown"), content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await helpdesk.PostAsync(Action(group, "signout"), content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        foreach (var action in new[] { "restart", "shutdown", "lock", "signout", "cancel-restart" })
        {
            (await auditor.PostAsync(Action(group, action), content: null)).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden, $"an auditor must not be able to {action} a group");
        }

        (await _support.TasksAsync(device, DeviceTaskType.ShutdownDevice)).ShouldBe(0);
        (await _support.TasksAsync(device, DeviceTaskType.SignOutUser)).ShouldBe(0);
    }

    /// <summary>
    /// A scoped administrator can act on their own group, and a group is never a
    /// way to reach a device they have no authority over.
    /// </summary>
    [Fact]
    public async Task A_group_is_not_an_authorization_bypass()
    {
        using var itAdmin = await ItAdminAsync();
        var mine = await _support.SeedDeviceAsync();
        var theirs = await _support.SeedDeviceAsync();
        var myGroup = await _support.CreateGroupAsync(itAdmin, UniqueName("Bypass-Mine"), mine);
        var theirGroup = await _support.CreateGroupAsync(itAdmin, UniqueName("Bypass-Theirs"), theirs);
        using var scoped = await _support.ScopedAdminAsync(myGroup);

        (await scoped.PostAsync(Action(myGroup, "restart"), content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await scoped.PostAsync(Action(theirGroup, "restart"), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await _support.TasksAsync(mine, DeviceTaskType.RestartDevice)).ShouldBe(1);
        (await _support.TasksAsync(theirs, DeviceTaskType.RestartDevice)).ShouldBe(0);
    }

    // ---------------------------------------------------------- cancellation

    /// <summary>
    /// A restart is cancellable only until it is delivered. After that the device
    /// may already have handed the countdown to Windows, so it is reported as too
    /// late -- never as cancelled.
    /// </summary>
    [Fact]
    public async Task Cancelling_cancels_queued_restarts_and_reports_delivered_ones_as_too_late()
    {
        using var client = await ItAdminAsync();
        var queued = await _support.SeedDeviceAsync();
        var delivered = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Cancel"), queued, delivered);
        var queuedTask = await _support.SeedQueuedRestartAsync(queued);
        var deliveredTask = await _support.SeedQueuedRestartAsync(delivered, delivered: true);

        var response = await client.PostAsync(Action(group, "cancel-restart"), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var byDevice = ByDevice((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices"));
        byDevice[queued].GetProperty("outcome").GetString().ShouldBe("Cancelled");
        byDevice[delivered].GetProperty("outcome").GetString().ShouldBe("TooLateToCancel",
            "a delivered restart must not be shown as cancelled");

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == queuedTask)).Status.ShouldBe(DeviceTaskStatus.Cancelled);
        (await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == deliveredTask)).Status.ShouldBe(DeviceTaskStatus.Delivered,
            "the delivered restart's state must be left exactly as it was");
    }

    [Fact]
    public async Task A_scoped_administrator_cannot_cancel_a_restart_outside_their_scope()
    {
        using var itAdmin = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var theirGroup = await _support.CreateGroupAsync(itAdmin, UniqueName("CancelTheirs"), device);
        var myGroup = await _support.CreateGroupAsync(itAdmin, UniqueName("CancelMine"));
        var task = await _support.SeedQueuedRestartAsync(device);
        using var scoped = await _support.ScopedAdminAsync(myGroup);

        (await scoped.PostAsync(Action(theirGroup, "cancel-restart"), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.PostAsync(new Uri($"/admin/v1/devices/{device}/tasks/{task}/cancel", UriKind.Relative), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "single-device cancellation is device-scoped too");

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == task)).Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task A_group_action_is_audited_with_its_timer_counts_and_per_device_outcomes()
    {
        var (client, group, online, offline) = await FleetAsync("AuditAction");
        using var __ = client;

        await client.PostAsync(Action(group, "restart"), Json(new { delaySeconds = 90 }));

        await using var db = _fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.Action == "group.action.restart" && e.TargetId == group.ToString())
            .SingleAsync();

        using var state = JsonDocument.Parse(entry.NewState!);
        state.RootElement.GetProperty("graceSeconds").GetInt32().ShouldBe(90);
        state.RootElement.GetProperty("targeted").GetInt32().ShouldBe(online.Length);
        state.RootElement.GetProperty("offline").GetInt32().ShouldBe(1);
        state.RootElement.GetProperty("results").GetArrayLength().ShouldBe(online.Length + 1);

        // Each queued task still has its own task.queue audit entry.
        foreach (var id in online)
        {
            (await db.AuditLogEntries.AsNoTracking().CountAsync(e => e.Action == "task.queue.restartdevice" && e.DeviceId == id))
                .ShouldBe(1);
        }

        (await db.AuditLogEntries.AsNoTracking().CountAsync(e => e.Action == "task.queue.restartdevice" && e.DeviceId == offline))
            .ShouldBe(0);
    }

    /// <summary>A body whose length cannot be computed, so the request is sent without Content-Length.</summary>
    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
