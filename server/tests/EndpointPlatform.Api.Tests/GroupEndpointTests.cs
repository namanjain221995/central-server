using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Groups as partitions: exactly one group per device, "All Devices" as the
/// fixed fallback, and every membership change authorized at both ends.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class GroupEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    private Task<HttpClient> ItAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);

    // ------------------------------------------------------------- the model

    [Fact]
    public async Task All_Devices_exists_is_built_in_and_is_listed_first()
    {
        using var client = await ItAdminAsync();

        var groups = await client.GetFromJsonAsync<JsonElement>(Groups());
        var first = groups.EnumerateArray().First();

        first.GetProperty("name").GetString().ShouldBe(DeviceGroup.AllDevicesName);
        first.GetProperty("isBuiltIn").GetBoolean().ShouldBeTrue();
        groups.EnumerateArray().Count(g => g.GetProperty("isBuiltIn").GetBoolean())
            .ShouldBe(1, "an organization has exactly one built-in group");
    }

    [Fact]
    public async Task A_newly_enrolled_device_lands_in_All_Devices()
    {
        var device = await _support.SeedDeviceAsync();

        (await _support.GroupOfAsync(device)).ShouldBe(await _support.AllDevicesIdAsync());
    }

    [Fact]
    public async Task All_Devices_cannot_be_renamed()
    {
        using var client = await ItAdminAsync();
        var allDevices = await _support.AllDevicesIdAsync();

        var response = await client.PatchAsync(Group(allDevices), Json(new { name = "Renamed" }));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<JsonElement>(Group(allDevices))).GetProperty("name").GetString()
            .ShouldBe(DeviceGroup.AllDevicesName);
    }

    [Fact]
    public async Task All_Devices_cannot_be_deleted()
    {
        using var client = await ItAdminAsync();
        var allDevices = await _support.AllDevicesIdAsync();

        (await client.DeleteAsync(Group(allDevices))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.GetAsync(Group(allDevices))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Devices_cannot_be_removed_from_All_Devices_because_there_is_nowhere_to_return_them()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var allDevices = await _support.AllDevicesIdAsync();

        (await client.PostAsync(RemoveDevices(allDevices), Json(new { deviceIds = new[] { device } })))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _support.GroupOfAsync(device)).ShouldBe(allDevices);
    }

    /// <summary>Creating with devices moves them in, and out of wherever they were.</summary>
    [Fact]
    public async Task Creating_a_group_with_devices_moves_them_out_of_their_previous_group()
    {
        using var client = await ItAdminAsync();
        var a = await _support.SeedDeviceAsync();
        var b = await _support.SeedDeviceAsync();
        var first = await _support.CreateGroupAsync(client, UniqueName("First"), a);

        var second = await _support.CreateGroupAsync(client, UniqueName("Second"), a, b);

        (await _support.GroupOfAsync(a)).ShouldBe(second, "a device is in exactly one group, so joining one leaves the other");
        (await _support.GroupOfAsync(b)).ShouldBe(second);
        (await client.GetFromJsonAsync<JsonElement>(Group(first))).GetProperty("deviceCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Adding_a_device_moves_it_and_reports_where_it_came_from()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var from = await _support.CreateGroupAsync(client, UniqueName("From"), device);
        var to = await _support.CreateGroupAsync(client, UniqueName("To"));

        var response = await client.PostAsync(Devices(to), Json(new { deviceIds = new[] { device } }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices")[0];
        result.GetProperty("outcome").GetString().ShouldBe("Moved");
        result.GetProperty("previousGroupId").GetGuid().ShouldBe(from);
        (await _support.GroupOfAsync(device)).ShouldBe(to);
    }

    [Fact]
    public async Task Adding_a_device_already_in_the_group_is_reported_not_repeated()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Same"), device);

        var response = await client.PostAsync(Devices(group), Json(new { deviceIds = new[] { device, device } }));

        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices");
        results.GetArrayLength().ShouldBe(1, "a device listed twice is one device");
        results[0].GetProperty("outcome").GetString().ShouldBe("AlreadyInGroup");
    }

    [Fact]
    public async Task Removing_a_device_returns_it_to_All_Devices_and_keeps_it_active()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Leaving"), device);

        var response = await client.PostAsync(RemoveDevices(group), Json(new { deviceIds = new[] { device } }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _support.GroupOfAsync(device)).ShouldBe(await _support.AllDevicesIdAsync());

        await using var db = _fixture.CreateDbContext();
        (await db.Devices.SingleAsync(d => d.Id == device)).Status.ShouldBe(DeviceStatus.Active,
            "removing a device from a group must not retire, offboard or delete it");
    }

    /// <summary>
    /// "Remove from this group" may only remove members of this group. Otherwise
    /// it is a way to move a device out of any other group the caller controls.
    /// </summary>
    [Fact]
    public async Task Remove_leaves_devices_that_are_not_in_the_group_where_they_are()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var elsewhere = await _support.CreateGroupAsync(client, UniqueName("Elsewhere"), device);
        var target = await _support.CreateGroupAsync(client, UniqueName("Target"));

        var response = await client.PostAsync(RemoveDevices(target), Json(new { deviceIds = new[] { device } }));

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices")[0]
            .GetProperty("outcome").GetString().ShouldBe("NotInGroup");
        (await _support.GroupOfAsync(device)).ShouldBe(elsewhere);
    }

    [Fact]
    public async Task Deleting_a_group_moves_every_device_to_All_Devices_and_deletes_none()
    {
        using var client = await ItAdminAsync();
        var devices = new[] { await _support.SeedDeviceAsync(), await _support.SeedDeviceAsync(), await _support.SeedDeviceAsync() };
        var group = await _support.CreateGroupAsync(client, UniqueName("Doomed"), devices);

        var response = await client.DeleteAsync(Group(group));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devicesMoved").GetInt32().ShouldBe(3);
        (await client.GetAsync(Group(group))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var allDevices = await _support.AllDevicesIdAsync();
        await using var db = _fixture.CreateDbContext();
        foreach (var id in devices)
        {
            var device = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == id);
            device.DeviceGroupId.ShouldBe(allDevices);
            device.Status.ShouldBe(DeviceStatus.Active, "deleting a group must never delete or retire a device");
        }
    }

    [Fact]
    public async Task A_retired_device_is_moved_too_when_its_group_is_deleted()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("RetiredMember"), device);

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.Devices.SingleAsync(d => d.Id == device)).Retire();
            await db.SaveChangesAsync();
        }

        (await client.DeleteAsync(Group(group))).StatusCode.ShouldBe(HttpStatusCode.OK,
            "a retired device still points at its group; deletion must move it rather than fail on the foreign key");
        (await _support.GroupOfAsync(device)).ShouldBe(await _support.AllDevicesIdAsync());
    }

    [Fact]
    public async Task Renaming_a_custom_group_changes_its_name()
    {
        using var client = await ItAdminAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Before"));
        var renamed = UniqueName("After");

        (await client.PatchAsync(Group(group), Json(new { name = renamed }))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonElement>(Group(group))).GetProperty("name").GetString().ShouldBe(renamed);
    }

    // ------------------------------------------------------------- validation

    [Fact]
    public async Task A_duplicate_name_is_refused_in_any_casing_and_nothing_is_overwritten()
    {
        using var client = await ItAdminAsync();
        var name = UniqueName("Dup");
        var device = await _support.SeedDeviceAsync();
        var original = await _support.CreateGroupAsync(client, name);

        var exact = await client.PostAsync(Groups(), Json(new { name, deviceIds = new[] { device } }));
        var shouted = await client.PostAsync(Groups(), Json(new { name = name.ToUpperInvariant() }));

        exact.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        shouted.StatusCode.ShouldBe(HttpStatusCode.Conflict, "names are unique case-insensitively");
        (await _support.GroupOfAsync(device)).ShouldNotBe(original,
            "a refused create must not have moved the device into the existing group");
        (await _support.GroupOfAsync(device)).ShouldBe(await _support.AllDevicesIdAsync(),
            "and must not have moved it anywhere: the whole create rolled back");
    }

    [Fact]
    public async Task Renaming_onto_an_existing_name_is_refused()
    {
        using var client = await ItAdminAsync();
        var taken = UniqueName("Taken");
        await _support.CreateGroupAsync(client, taken);
        var other = await _support.CreateGroupAsync(client, UniqueName("Other"));

        (await client.PatchAsync(Group(other), Json(new { name = taken.ToLowerInvariant() })))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("All Devices")]
    [InlineData("all devices")]
    [InlineData("Tab\tInside")]
    [InlineData("Line\nBreak")]
    public async Task Invalid_or_reserved_names_are_refused(string name)
    {
        using var client = await ItAdminAsync();

        (await client.PostAsync(Groups(), Json(new { name }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_overlong_name_is_refused()
    {
        using var client = await ItAdminAsync();

        (await client.PostAsync(Groups(), Json(new { name = new string('x', DeviceGroup.MaxNameLength + 1) })))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_nonexistent_group_is_not_found_everywhere()
    {
        using var client = await ItAdminAsync();
        var missing = Guid.CreateVersion7();

        (await client.GetAsync(Group(missing))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PatchAsync(Group(missing), Json(new { name = "x" }))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync(Group(missing))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(Devices(missing), Json(new { deviceIds = new[] { Guid.CreateVersion7() } })))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(Action(missing, "restart"), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_nonexistent_device_is_reported_per_device_and_does_not_block_the_others()
    {
        using var client = await ItAdminAsync();
        var real = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Mixed"));
        var ghost = Guid.CreateVersion7();

        var response = await client.PostAsync(Devices(group), Json(new { deviceIds = new[] { ghost, real } }));

        var byDevice = ByDevice((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices"));
        byDevice[ghost].GetProperty("outcome").GetString().ShouldBe("NotFound");
        byDevice[real].GetProperty("outcome").GetString().ShouldBe("Moved");
    }

    [Fact]
    public async Task An_empty_device_list_is_refused()
    {
        using var client = await ItAdminAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Empty"));

        (await client.PostAsync(Devices(group), Json(new { deviceIds = Array.Empty<Guid>() })))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task More_devices_than_one_request_allows_are_refused()
    {
        using var client = await ItAdminAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("TooMany"));
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.CreateVersion7()).ToArray();

        (await client.PostAsync(Devices(group), Json(new { deviceIds = ids })))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// An oversized body is refused by the server before it is read.
    /// </summary>
    /// <remarks>
    /// Against real Kestrel, deliberately. The in-process test server does not
    /// enforce request-body limits at all, so the same request there is read in
    /// full and answered 400 -- a test on that host would pass whether or not
    /// the limit existed. This is the trap the fixture's Kestrel host was built
    /// for.
    /// </remarks>
    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_read()
    {
        var factory = await _fixture.GetKestrelFactoryAsync();
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email = AdminApiPostgresFixture.ItAdminEmail, password = AdminApiPostgresFixture.Password });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionToken").GetString());
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        var group = await _support.CreateGroupAsync(client, UniqueName("Huge"));
        var padding = new string('x', 200 * 1024);

        var response = await client.PostAsync(Devices(group), Json($$"""{"deviceIds":[],"pad":"{{padding}}"}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Malformed_json_is_refused()
    {
        using var client = await ItAdminAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("Malformed"));

        (await client.PostAsync(Devices(group), Json("not json"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsync(Groups(), Json("""{"name":"""))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // --------------------------------------------------- authentication, permission

    [Fact]
    public async Task Every_group_route_requires_authentication()
    {
        using var anonymous = _fixture.Factory.CreateClient();
        var any = Guid.CreateVersion7();

        (await anonymous.GetAsync(Groups())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(Group(any))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(Groups(), Json(new { name = "x" }))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(Action(any, "restart"), content: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>Auditors may look at groups; they may not change them.</summary>
    [Fact]
    public async Task Viewing_needs_group_view_and_changing_needs_group_manage()
    {
        using var itAdmin = await ItAdminAsync();
        using var auditor = await _support.ClientAsAsync(AdminApiPostgresFixture.AuditorEmail);
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(itAdmin, UniqueName("AuditorView"));

        (await auditor.GetAsync(Groups())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await auditor.GetAsync(Group(group))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await auditor.PostAsync(Groups(), Json(new { name = UniqueName("Nope") }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditor.PatchAsync(Group(group), Json(new { name = "x" }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditor.DeleteAsync(Group(group))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditor.PostAsync(Devices(group), Json(new { deviceIds = new[] { device } }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await _support.GroupOfAsync(device)).ShouldBe(await _support.AllDevicesIdAsync());
    }

    // ------------------------------------------------------------ device scope

    /// <summary>
    /// The escalation this rewrite closes. Group membership is an administrator's
    /// device scope, so a scoped administrator who could add any device to their
    /// own group could grant themselves authority over any device. The previous
    /// implementation checked organization only, and allowed exactly that.
    /// </summary>
    [Fact]
    public async Task A_scoped_administrator_cannot_pull_an_out_of_scope_device_into_their_group()
    {
        using var itAdmin = await ItAdminAsync();
        var mine = await _support.CreateGroupAsync(itAdmin, UniqueName("Mine"));
        var theirs = await _support.SeedDeviceAsync();
        var theirGroup = await _support.CreateGroupAsync(itAdmin, UniqueName("Theirs"), theirs);
        using var scoped = await _support.ScopedAdminAsync(mine);

        var response = await scoped.PostAsync(Devices(mine), Json(new { deviceIds = new[] { theirs } }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices")[0]
            .GetProperty("outcome").GetString().ShouldBe("NotFound", "an out-of-scope device must be invisible, not 'forbidden'");
        (await _support.GroupOfAsync(theirs)).ShouldBe(theirGroup, "the device must not have moved");

        // And it confers nothing afterwards: the device is still unreachable.
        (await scoped.PostAsync(new Uri($"/admin/v1/devices/{theirs}/actions/restart", UriKind.Relative), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_scoped_administrator_cannot_see_or_touch_a_group_outside_their_scope()
    {
        using var itAdmin = await ItAdminAsync();
        var mine = await _support.CreateGroupAsync(itAdmin, UniqueName("ScopeMine"));
        var device = await _support.SeedDeviceAsync();
        var other = await _support.CreateGroupAsync(itAdmin, UniqueName("ScopeOther"), device);
        using var scoped = await _support.ScopedAdminAsync(mine);

        var visible = (await scoped.GetFromJsonAsync<JsonElement>(Groups())).EnumerateArray()
            .Select(g => g.GetProperty("id").GetGuid()).ToList();
        visible.ShouldContain(mine);
        visible.ShouldNotContain(other);
        visible.ShouldNotContain(await _support.AllDevicesIdAsync(), "not scoped to All Devices, so it is not listed");

        (await scoped.GetAsync(Group(other))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.PatchAsync(Group(other), Json(new { name = "x" }))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.DeleteAsync(Group(other))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.PostAsync(Action(other, "restart"), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _support.TasksAsync(device, Domain.Tasks.DeviceTaskType.RestartDevice)).ShouldBe(0);
    }

    [Fact]
    public async Task A_scoped_administrator_does_not_see_out_of_scope_devices_as_candidates()
    {
        using var itAdmin = await ItAdminAsync();
        var mine = await _support.CreateGroupAsync(itAdmin, UniqueName("CandMine"));
        var hidden = await _support.SeedDeviceAsync();
        await _support.CreateGroupAsync(itAdmin, UniqueName("CandHidden"), hidden);
        using var scoped = await _support.ScopedAdminAsync(mine);

        var candidates = await scoped.GetFromJsonAsync<JsonElement>(Candidates(mine));

        candidates.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldNotContain(hidden);
    }

    /// <summary>
    /// Scope is granted per group, and nothing grants a creator scope over a
    /// group that did not exist -- so a scoped administrator may not create one.
    /// </summary>
    [Fact]
    public async Task A_scoped_administrator_cannot_create_a_group()
    {
        using var itAdmin = await ItAdminAsync();
        var mine = await _support.CreateGroupAsync(itAdmin, UniqueName("NoCreate"));
        using var scoped = await _support.ScopedAdminAsync(mine);

        (await scoped.PostAsync(Groups(), Json(new { name = UniqueName("Orphan") }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Removing returns a device to All Devices, which is a move -- so it needs
    /// authority over All Devices too. Otherwise a scoped administrator could
    /// hand devices to whoever does control it.
    /// </summary>
    [Fact]
    public async Task Removal_is_refused_when_All_Devices_is_outside_the_callers_scope()
    {
        using var itAdmin = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var mine = await _support.CreateGroupAsync(itAdmin, UniqueName("RemoveScope"), device);
        using var scoped = await _support.ScopedAdminAsync(mine);

        (await scoped.PostAsync(RemoveDevices(mine), Json(new { deviceIds = new[] { device } })))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await scoped.DeleteAsync(Group(mine))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await _support.GroupOfAsync(device)).ShouldBe(mine);
    }

    /// <summary>With authority over both ends, a scoped administrator may move devices between their groups.</summary>
    [Fact]
    public async Task A_scoped_administrator_may_move_devices_between_groups_they_control()
    {
        using var itAdmin = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var from = await _support.CreateGroupAsync(itAdmin, UniqueName("BothFrom"), device);
        var to = await _support.CreateGroupAsync(itAdmin, UniqueName("BothTo"));
        using var scoped = await _support.ScopedAdminAsync(from, to);

        var response = await scoped.PostAsync(Devices(to), Json(new { deviceIds = new[] { device } }));

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices")[0]
            .GetProperty("outcome").GetString().ShouldBe("Moved");
        (await _support.GroupOfAsync(device)).ShouldBe(to);
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task Group_lifecycle_is_audited_without_sensitive_detail()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var name = UniqueName("Audited");
        var group = await _support.CreateGroupAsync(client, name, device);
        await client.PatchAsync(Group(group), Json(new { name = name + "2" }));
        await client.DeleteAsync(Group(group));

        await using var db = _fixture.CreateDbContext();
        var entries = await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.TargetId == group.ToString() || e.DeviceId == device)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        entries.Select(e => e.Action).ShouldContain("group.create");
        entries.Select(e => e.Action).ShouldContain("group.move_device");
        entries.Select(e => e.Action).ShouldContain("group.rename");
        entries.Select(e => e.Action).ShouldContain("group.delete");

        foreach (var entry in entries.Where(e => e.Action.StartsWith("group.", StringComparison.Ordinal)))
        {
            var state = (entry.NewState ?? "") + (entry.PreviousState ?? "");
            state.ShouldNotContain("password", Case.Insensitive);
            state.ShouldNotContain("token", Case.Insensitive);
            state.ShouldNotContain("secret", Case.Insensitive);
        }
    }
}
