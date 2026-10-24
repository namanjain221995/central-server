using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The fleet-wide Chrome account removal over real HTTP: one account named by
/// e-mail, every matching profile row on every resolved device its own
/// <c>RemoveChromeProfile</c> task, the tallies that explain the rest, and the
/// optional restart fan-out that reuses the restart action's task whole.
/// </summary>
/// <remarks>
/// <para>
/// The fixture database is shared, so each test matches on an e-mail address of its
/// own: the account is the selector, which makes every count exact even on a
/// database other tests have seeded -- except for all-devices targeting, where only
/// lower bounds and this test's own devices are asserted.
/// </para>
/// <para>
/// The weight is on what the single-profile route already pinned travelling
/// unchanged through the fan-out: payloads from the stored rows (never the
/// request), the never-removable Default profile, the 1.26.0 agent floor -- here a
/// tally rather than a refusal, because a fleet action reports stragglers instead
/// of failing the fleet.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ChromeAccountBulkRemovalEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    /// <summary>The oldest agent whose executor defers the removal while Chrome is open.</summary>
    private const string CapableAgent = "1.26.0";

    private static readonly Uri RemoveAccount =
        new("/admin/v1/chrome/accounts/actions/remove", UriKind.Relative);

    private sealed record Tallies(
        int TargetedDevices, int QueuedRemovals, int QueuedRestarts,
        int SkippedNoProfile, int SkippedDefault, int RejectedAgentTooOld, int Offline);

    private Task<HttpClient> ItAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);

    /// <summary>An address no other test's inventory rows can collide with.</summary>
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}@example.com";

    private static async Task<Tallies> OkAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Tallies>())!;
    }

    private static object Target(string kind, Guid? groupId = null, Guid[]? deviceIds = null) =>
        new { kind, groupId, deviceIds };

    /// <summary>
    /// Seeds one Chrome profile row on the device, under its (created-on-demand)
    /// installation, and returns the row's id. Distinct SIDs let one device hold
    /// the same account for several Windows users.
    /// </summary>
    private async Task<Guid> SeedProfileAsync(
        Guid deviceId, string? email, string key = "Profile 1",
        string sid = "S-1-5-21-4000-5000-6000-1001")
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
            deviceId, installation.Id, sid, @"WORKGROUP\user", key, "Person",
            @"C:\Users\user\AppData\Local\Google\Chrome\User Data\" + key,
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

    // --------------------------------------------------------------- targeting

    [Fact]
    public async Task Removing_for_a_group_queues_one_task_per_matching_row_from_the_stored_rows()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("grp");
        var a = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var b = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeAcct"), a, b);
        await SeedProfileAsync(a, email, key: "Profile 1", sid: "S-1-5-21-1-1-1-1001");
        await SeedProfileAsync(b, email, key: "Profile 7", sid: "S-1-5-21-2-2-2-1002");

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("group", groupId: group),
            restartDevices = false,
        }));

        result.ShouldBe(new Tallies(2, 2, 0, 0, 0, 0, 0));

        // The payload is each device's stored row, exactly as the single route
        // builds it -- the request contributed nothing but the account.
        var task = (await RemovalTasksAsync(b)).ShouldHaveSingleItem();
        using var payload = JsonDocument.Parse(task.PayloadJson!);
        payload.RootElement.EnumerateObject().Count().ShouldBe(4);
        payload.RootElement.GetProperty("userSid").GetString().ShouldBe("S-1-5-21-2-2-2-1002");
        payload.RootElement.GetProperty("profileKey").GetString().ShouldBe("Profile 7");
        payload.RootElement.GetProperty("expectedEmail").GetString().ShouldBe(email);
        payload.RootElement.GetProperty("profilePath").GetString()
            .ShouldBe(@"C:\Users\user\AppData\Local\Google\Chrome\User Data\Profile 7");

        (await RemovalTasksAsync(a)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_for_all_devices_reaches_each_of_my_devices()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("all");
        var a = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var b = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(a, email);
        await SeedProfileAsync(b, email);

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("all"),
            restartDevices = false,
        }));

        // The database is shared, so there may be more targets; mine must be among
        // them, and the unique address keeps the queued count exact.
        result.TargetedDevices.ShouldBeGreaterThanOrEqualTo(2);
        result.QueuedRemovals.ShouldBe(2);
        (await RemovalTasksAsync(a)).Count.ShouldBe(1);
        (await RemovalTasksAsync(b)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_for_a_selection_touches_only_the_selection()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("sel");
        var chosen = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var bystander = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(chosen, email);
        await SeedProfileAsync(bystander, email, sid: "S-1-5-21-9-9-9-1003");

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [chosen]),
            restartDevices = false,
        }));

        result.ShouldBe(new Tallies(1, 1, 0, 0, 0, 0, 0));
        (await RemovalTasksAsync(bystander)).ShouldBeEmpty(
            "a device holding the account is still not a target unless named");
    }

    // ---------------------------------------------------------------- matching

    [Fact]
    public async Task The_account_matches_case_insensitively_and_the_payload_keeps_the_stored_casing()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("case");
        var stored = "Mixed.Case-" + email;
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(device, stored);

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = stored.ToUpperInvariant(),
            target = Target("devices", deviceIds: [device]),
            restartDevices = false,
        }));

        result.QueuedRemovals.ShouldBe(1);

        var task = (await RemovalTasksAsync(device)).ShouldHaveSingleItem();
        using var payload = JsonDocument.Parse(task.PayloadJson!);
        payload.RootElement.GetProperty("expectedEmail").GetString().ShouldBe(stored,
            "the expected e-mail is the inventory row's, which is what the agent compares against Chrome's records");
    }

    [Fact]
    public async Task A_device_holding_the_account_in_several_profiles_gets_one_task_per_profile()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("multi");
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        // The same account signed in twice: another profile of the same Windows
        // user, and a profile of a second Windows user. Each is its own directory,
        // so each is its own task.
        await SeedProfileAsync(device, email, key: "Profile 1", sid: "S-1-5-21-1-1-1-1001");
        await SeedProfileAsync(device, email, key: "Profile 2", sid: "S-1-5-21-1-1-1-1001");
        await SeedProfileAsync(device, email, key: "Profile 1", sid: "S-1-5-21-3-3-3-1002");

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [device]),
            restartDevices = false,
        }));

        result.ShouldBe(new Tallies(1, 3, 0, 0, 0, 0, 0));
        (await RemovalTasksAsync(device)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_default_profile_is_counted_and_never_queued()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("dflt");
        var mixed = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var defaultOnly = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(mixed, email, key: "Default", sid: "S-1-5-21-1-1-1-1001");
        await SeedProfileAsync(mixed, email, key: "Profile 2", sid: "S-1-5-21-1-1-1-1001");
        await SeedProfileAsync(defaultOnly, email, key: "Default", sid: "S-1-5-21-5-5-5-1002");

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [mixed, defaultOnly]),
            restartDevices = false,
        }));

        // Two Default matches counted, one task queued; the device that holds the
        // account ONLY in Default is explained by skippedDefault, not read as
        // having no profile.
        result.ShouldBe(new Tallies(2, 1, 0, 0, 2, 0, 0));

        var task = (await RemovalTasksAsync(mixed)).ShouldHaveSingleItem();
        using var payload = JsonDocument.Parse(task.PayloadJson!);
        payload.RootElement.GetProperty("profileKey").GetString().ShouldBe("Profile 2");
        (await RemovalTasksAsync(defaultOnly)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_device_without_the_account_is_tallied_not_tasked()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("none");
        var holds = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var without = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(holds, email);
        await SeedProfileAsync(without, UniqueEmail("other"), sid: "S-1-5-21-6-6-6-1003");

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [holds, without]),
            restartDevices = false,
        }));

        result.ShouldBe(new Tallies(2, 1, 0, 1, 0, 0, 0));
        (await RemovalTasksAsync(without)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- agent gate

    /// <summary>
    /// 1.25.0 deliberately, as on the single route: it has an executor, but one that
    /// fails fast while Chrome is open. Fleet-wide, a too-old device is a tally the
    /// operator reads -- never a failed request, and never a queued task whose
    /// failure would read like the removal breaking.
    /// </summary>
    [Fact]
    public async Task A_device_below_the_agent_floor_is_counted_not_failed()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("old");
        var capable = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var tooOld = await _support.SeedDeviceAsync(agentVersion: "1.25.0");
        await SeedProfileAsync(capable, email);
        await SeedProfileAsync(tooOld, email, sid: "S-1-5-21-7-7-7-1004");

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [capable, tooOld]),
            restartDevices = false,
        }));

        result.ShouldBe(new Tallies(2, 1, 0, 0, 0, 1, 0));
        (await RemovalTasksAsync(tooOld)).ShouldBeEmpty();
        (await RemovalTasksAsync(capable)).Count.ShouldBe(1);
    }

    // -------------------------------------------------------------------- scope

    [Fact]
    public async Task A_scoped_administrator_reaches_only_devices_inside_their_scope()
    {
        using var admin = await ItAdminAsync();
        var email = UniqueEmail("scope");
        var inScope = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var elsewhere = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(inScope, email);
        await SeedProfileAsync(elsewhere, email, sid: "S-1-5-21-8-8-8-1005");
        var mine = await _support.CreateGroupAsync(admin, UniqueName("ChromeAcctScope"), inScope);

        using var scoped = await _support.ScopedAdminAsync(mine);

        var result = await OkAsync(await scoped.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            // Both named, out of band knowledge or not: the out-of-scope device is
            // dropped during resolution and never counted, let alone touched.
            target = Target("devices", deviceIds: [inScope, elsewhere]),
            restartDevices = false,
        }));

        result.ShouldBe(new Tallies(1, 1, 0, 0, 0, 0, 0));
        (await RemovalTasksAsync(elsewhere)).ShouldBeEmpty(
            "scope is enforced during resolution, so an excluded device is not even a tally");
    }

    [Fact]
    public async Task A_target_that_resolves_to_nothing_is_not_found()
    {
        using var client = await ItAdminAsync();

        (await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = UniqueEmail("ghost"),
            target = Target("group", groupId: Guid.CreateVersion7()),
            restartDevices = false,
        })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = UniqueEmail("ghost"),
            target = Target("devices", deviceIds: [Guid.CreateVersion7()]),
            restartDevices = false,
        })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------- restarts

    [Fact]
    public async Task Restart_fan_out_is_one_restart_per_device_that_got_a_removal()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("boot");
        var twoProfiles = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var oneProfile = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var noProfile = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(twoProfiles, email, key: "Profile 1", sid: "S-1-5-21-1-1-1-1001");
        await SeedProfileAsync(twoProfiles, email, key: "Profile 2", sid: "S-1-5-21-2-2-2-1002");
        await SeedProfileAsync(oneProfile, email, sid: "S-1-5-21-3-3-3-1003");

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [twoProfiles, oneProfile, noProfile]),
            restartDevices = true,
        }));

        result.ShouldBe(new Tallies(3, 3, 2, 1, 0, 0, 0));

        // One restart however many profiles fell, none for the device that had
        // nothing to remove -- and each is exactly the restart route's task.
        (await _support.TasksAsync(twoProfiles, DeviceTaskType.RestartDevice)).ShouldBe(1);
        (await _support.TasksAsync(oneProfile, DeviceTaskType.RestartDevice)).ShouldBe(1);
        (await _support.TasksAsync(noProfile, DeviceTaskType.RestartDevice)).ShouldBe(0);

        await using var db = _fixture.CreateDbContext();
        var restart = await db.DeviceTasks.AsNoTracking().SingleAsync(
            t => t.DeviceId == oneProfile && t.Type == DeviceTaskType.RestartDevice);
        using var payload = JsonDocument.Parse(restart.PayloadJson!);
        payload.RootElement.GetProperty("graceSeconds").GetInt32().ShouldBe(RestartGrace.ImmediateSeconds);
        payload.RootElement.GetProperty("message").GetString()
            .ShouldBe(RestartGrace.MessageFor(RestartGrace.ImmediateSeconds));
    }

    [Fact]
    public async Task A_restart_already_in_flight_is_kept_not_doubled()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("inflight");
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(device, email);
        await _support.SeedQueuedRestartAsync(device);

        var result = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [device]),
            restartDevices = true,
        }));

        result.QueuedRemovals.ShouldBe(1);
        result.QueuedRestarts.ShouldBe(0, "the reboot is already coming; a second would only be refused");
        (await _support.ActiveRestartsAsync(device)).ShouldBe(1);
    }

    /// <summary>
    /// The same split the single route enforces: chrome.manage queues removals,
    /// and only device.restart -- the restart action's own permission -- lets the
    /// request also take machines down. Refused whole, up front.
    /// </summary>
    [Fact]
    public async Task Restarting_demands_the_restart_permission_on_top_of_chrome_manage()
    {
        using var client = await ChromeManagerWithoutRestartAsync();
        var email = UniqueEmail("perm");
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(device, email);

        var refused = await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [device]),
            restartDevices = true,
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).ShouldContain(Permissions.Device.Restart,
            customMessage: "the refusal names the permission the restart action itself requires");
        (await RemovalTasksAsync(device)).ShouldBeEmpty("a half-granted request queues nothing at all");
        (await _support.TasksAsync(device, DeviceTaskType.RestartDevice)).ShouldBe(0);

        // The same caller removes fleet-wide without the restart.
        var allowed = await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [device]),
            restartDevices = false,
        }));
        allowed.QueuedRemovals.ShouldBe(1);
    }

    // -------------------------------------------------------------------- audit

    [Fact]
    public async Task The_bulk_decision_is_audited_once_with_its_tallies()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("audit");
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var without = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(device, email);

        await OkAsync(await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = email,
            target = Target("devices", deviceIds: [device, without]),
            restartDevices = false,
        }));

        await using var db = _fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.AsNoTracking().SingleAsync(
            a => a.Action == "chrome.account.bulk_remove" && a.TargetId == email);

        entry.RequiredPermission.ShouldBe(Permissions.Chrome.Manage);
        using var state = JsonDocument.Parse(entry.NewState!);
        state.RootElement.GetProperty("accountEmail").GetString().ShouldBe(email);
        state.RootElement.GetProperty("targetedDevices").GetInt32().ShouldBe(2);
        state.RootElement.GetProperty("queuedRemovals").GetInt32().ShouldBe(1);
        state.RootElement.GetProperty("queuedRestarts").GetInt32().ShouldBe(0);
        state.RootElement.GetProperty("skippedNoProfile").GetInt32().ShouldBe(1);
        state.RootElement.GetProperty("skippedDefault").GetInt32().ShouldBe(0);
        state.RootElement.GetProperty("rejectedAgentTooOld").GetInt32().ShouldBe(0);

        // The per-task audit the task service has always written is still there.
        (await db.AuditLogEntries.CountAsync(
            a => a.DeviceId == device && a.Action == "task.queue.removechromeprofile")).ShouldBe(1);
    }

    // -------------------------------------------------------------------- shape

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("two@signs@example.com")]
    [InlineData("@example.com")]
    [InlineData("someone@")]
    [InlineData("some one@example.com")]
    public async Task An_account_that_does_not_look_like_an_email_is_refused(string? accountEmail)
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);

        var response = await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail,
            target = Target("devices", deviceIds: [device]),
            restartDevices = false,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RemovalTasksAsync(device)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_address_over_320_characters_is_refused()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);

        var response = await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = new string('a', 310) + "@example.com",
            target = Target("devices", deviceIds: [device]),
            restartDevices = false,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_malformed_target_is_refused()
    {
        using var client = await ItAdminAsync();
        var email = UniqueEmail("shape");
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(device, email);

        foreach (var target in new object?[]
                 {
                     null,
                     Target("everything"),
                     Target("all", deviceIds: [device]),
                     Target("group"),
                     Target("group", groupId: Guid.Empty),
                     Target("devices"),
                     Target("devices", groupId: Guid.CreateVersion7(), deviceIds: [device]),
                 })
        {
            var response = await client.PostAsJsonAsync(RemoveAccount, new
            {
                accountEmail = email,
                target,
                restartDevices = false,
            });

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
                $"'{System.Text.Json.JsonSerializer.Serialize(target)}' is not a well-formed target");
        }

        (await RemovalTasksAsync(device)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------- permissions

    [Theory]
    [InlineData(AdminApiPostgresFixture.HelpdeskEmail)]
    [InlineData(AdminApiPostgresFixture.AuditorEmail)]
    public async Task A_chrome_view_only_caller_is_forbidden(string email)
    {
        var account = UniqueEmail("viewer");
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await SeedProfileAsync(device, account);

        using var client = await _support.ClientAsAsync(email);

        (await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = account,
            target = Target("devices", deviceIds: [device]),
            restartDevices = false,
        })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RemovalTasksAsync(device)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected()
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsJsonAsync(RemoveAccount, new
        {
            accountEmail = "someone@example.com",
            target = Target("all"),
            restartDevices = false,
        })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// An administrator holding chrome.manage but not device.restart -- no built-in
    /// role is shaped like this, which is exactly why the guard must be its own check.
    /// </summary>
    private async Task<HttpClient> ChromeManagerWithoutRestartAsync()
    {
        var email = $"chrome-bulk-only-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = Role.CreateCustom(
                org.Id, $"chrome_bulk_only_{Guid.CreateVersion7():N}", "Chrome Bulk Only",
                "chrome.manage without device.restart");
            var manage = await db.Permissions.SingleAsync(p => p.Key == Permissions.Chrome.Manage);
            role.GrantPermission(manage.Id);
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            var user = new PlatformUser(org.Id, email, "Chrome Bulk Only");
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
}
