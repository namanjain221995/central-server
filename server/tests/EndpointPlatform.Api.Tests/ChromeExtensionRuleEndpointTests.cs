using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The Chrome extension rule routes over real HTTP against real PostgreSQL: the
/// per-device rules and the whole-state <c>ApplyChromeExtensionPolicy</c> task
/// every mutation queues.
/// </summary>
/// <remarks>
/// <para>
/// The weight here is on the whole-state contract, because it is the part most
/// easily got wrong: every mutation's task must carry EVERY rule the device then
/// has -- the second rule travels with the first, and lifting the last rule still
/// queues a task whose rule list is an empty array, since that is the instruction
/// that clears the device.
/// </para>
/// <para>
/// Devices are seeded at agent 1.23.0 except where the test is about the gate;
/// the fleet's real versions (1.16.2, 1.18.0) are below it until the rollout, and
/// the routes must say so with a 409 rather than queue work nothing will run.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ChromeExtensionRuleEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    /// <summary>The oldest agent that carries the ExtensionSettings executor.</summary>
    private const string CapableAgent = "1.23.0";

    private static Uri Rules(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/chrome/extension-rules", UriKind.Relative);

    private static Uri Rule(Guid deviceId, string extensionId) =>
        new($"/admin/v1/devices/{deviceId}/chrome/extension-rules/{extensionId}", UriKind.Relative);

    private Task<HttpClient> ItAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);

    /// <summary>A Chrome extension id: exactly 32 characters in a-p, distinct per leading letter.</summary>
    private static string ExtensionId(char first) => first + "bcdefghijklmnopabcdefghijklmnop";

    private static Task<HttpResponseMessage> PutRuleAsync(
        HttpClient client, Guid deviceId, string extensionId, string? mode, string? displayName = null) =>
        client.PutAsync(Rule(deviceId, extensionId), Json(new { mode, displayName }));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<JsonElement>> ListedRulesAsync(HttpClient client, Guid deviceId) =>
        (await ReadAsync(await client.GetAsync(Rules(deviceId)))).EnumerateArray().ToList();

    /// <summary>The device's policy tasks, oldest first, with each payload parsed.</summary>
    private async Task<List<JsonDocument>> PolicyPayloadsAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var payloads = await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ApplyChromeExtensionPolicy)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.PayloadJson!)
            .ToListAsync();
        return payloads.Select(p => JsonDocument.Parse(p)).ToList();
    }

    private async Task<int> AuditCountAsync(Guid deviceId, string action)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AuditLogEntries.CountAsync(a => a.DeviceId == deviceId && a.Action == action);
    }

    /// <summary>A capable device in a second organization.</summary>
    private async Task<Guid> SeedForeignDeviceAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization("Other Org", ("x" + Guid.CreateVersion7().ToString("N"))[..20]);
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var token = new EnrollmentToken(
            org.Id, $"chromerule-foreign-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "seed", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "CHRR-FOREIGN", "m-" + Guid.CreateVersion7().ToString("N"),
            CapableAgent, "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    // ------------------------------------------------------------- whole state

    [Fact]
    public async Task Setting_a_rule_stores_it_and_queues_the_devices_complete_policy()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var blocked = ExtensionId('f');
        var forced = ExtensionId('a');

        var first = await ReadAsync(await PutRuleAsync(client, device, blocked, "Blocked", "Free VPN"));
        first.GetProperty("taskId").GetGuid().ShouldNotBe(Guid.Empty);
        first.GetProperty("status").GetString().ShouldBe("Queued");

        var listed = await ListedRulesAsync(client, device);
        listed.Count.ShouldBe(1);
        listed[0].GetProperty("extensionId").GetString().ShouldBe(blocked);
        listed[0].GetProperty("mode").GetString().ShouldBe("Blocked");
        listed[0].GetProperty("displayName").GetString().ShouldBe("Free VPN");
        listed[0].GetProperty("updatedAt").ValueKind.ShouldBe(JsonValueKind.String);

        // The second rule's task carries BOTH rules: whole state, never a delta.
        (await ReadAsync(await PutRuleAsync(client, device, forced, "ForceInstalled")))
            .GetProperty("status").GetString().ShouldBe("Queued");

        var payloads = await PolicyPayloadsAsync(device);
        payloads.Count.ShouldBe(2);

        var firstRules = payloads[0].RootElement.GetProperty("rules");
        firstRules.GetArrayLength().ShouldBe(1);
        firstRules[0].GetProperty("extensionId").GetString().ShouldBe(blocked);
        firstRules[0].GetProperty("mode").GetString().ShouldBe("Blocked");

        var secondRules = payloads[1].RootElement.GetProperty("rules");
        secondRules.GetArrayLength().ShouldBe(2, "every rule the device has travels in every task");
        secondRules.EnumerateArray().Select(r => r.GetProperty("extensionId").GetString())
            .ShouldBe([forced, blocked], "ordered by extension id, so payloads are comparable");
        secondRules[0].GetProperty("mode").GetString().ShouldBe("ForceInstalled");
        secondRules[1].GetProperty("mode").GetString().ShouldBe("Blocked");
        payloads[1].RootElement.GetProperty("issuedAt").ValueKind.ShouldBe(JsonValueKind.String);

        (await AuditCountAsync(device, "chrome.extension.rule.set")).ShouldBe(2,
            "the decision is audited besides the task the task service records");
        payloads.ForEach(p => p.Dispose());
    }

    [Fact]
    public async Task Setting_a_rule_again_re_decides_it_rather_than_duplicating_it()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var extension = ExtensionId('b');

        await ReadAsync(await PutRuleAsync(client, device, extension, "Allowed", "Softphone"));
        await ReadAsync(await PutRuleAsync(client, device, extension, "Removed"));

        var listed = await ListedRulesAsync(client, device);
        listed.Count.ShouldBe(1, "one rule per device per extension; a second PUT is the upsert");
        listed[0].GetProperty("mode").GetString().ShouldBe("Removed");
        listed[0].GetProperty("displayName").GetString().ShouldBe("Softphone",
            "a mode change without a label keeps the label the console remembered");

        var payloads = await PolicyPayloadsAsync(device);
        payloads.Count.ShouldBe(2, "every re-decision re-sends the policy");
        var rules = payloads[1].RootElement.GetProperty("rules");
        rules.GetArrayLength().ShouldBe(1);
        rules[0].GetProperty("mode").GetString().ShouldBe("Removed");
        payloads.ForEach(p => p.Dispose());
    }

    [Fact]
    public async Task Lifting_rules_queues_the_remaining_policy_down_to_the_empty_one()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var first = ExtensionId('c');
        var second = ExtensionId('d');

        await ReadAsync(await PutRuleAsync(client, device, first, "Blocked"));
        await ReadAsync(await PutRuleAsync(client, device, second, "ForceInstalled"));

        var lift = await ReadAsync(await client.DeleteAsync(Rule(device, first)));
        lift.GetProperty("taskId").GetGuid().ShouldNotBe(Guid.Empty);
        lift.GetProperty("status").GetString().ShouldBe("Queued");

        var listed = await ListedRulesAsync(client, device);
        listed.Count.ShouldBe(1);
        listed[0].GetProperty("extensionId").GetString().ShouldBe(second);

        await ReadAsync(await client.DeleteAsync(Rule(device, second)));
        (await ListedRulesAsync(client, device)).ShouldBeEmpty();

        var payloads = await PolicyPayloadsAsync(device);
        payloads.Count.ShouldBe(4, "two sets, two lifts: every mutation queues the policy");

        var afterFirstLift = payloads[2].RootElement.GetProperty("rules");
        afterFirstLift.GetArrayLength().ShouldBe(1, "the lifted rule is simply absent");
        afterFirstLift[0].GetProperty("extensionId").GetString().ShouldBe(second);

        var afterLastLift = payloads[3].RootElement.GetProperty("rules");
        afterLastLift.ValueKind.ShouldBe(JsonValueKind.Array);
        afterLastLift.GetArrayLength().ShouldBe(0,
            "lifting the last rule still queues the task: the empty policy clears the device");

        (await AuditCountAsync(device, "chrome.extension.rule.removed")).ShouldBe(2);
        payloads.ForEach(p => p.Dispose());
    }

    // -------------------------------------------------------------- validation

    [Theory]
    [InlineData("abc")]
    [InlineData("abcdefghijklmnopabcdefghijklmno")] // 31
    [InlineData("abcdefghijklmnopabcdefghijklmnopq")] // 33
    [InlineData("Abcdefghijklmnopabcdefghijklmnop")] // uppercase
    [InlineData("qbcdefghijklmnopabcdefghijklmnop")] // q is past p
    public async Task A_malformed_extension_id_is_a_bad_request_and_queues_nothing(string extensionId)
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);

        (await PutRuleAsync(client, device, extensionId, "Blocked"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _support.TasksAsync(device, DeviceTaskType.ApplyChromeExtensionPolicy)).ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Banish")]
    [InlineData("force_installed")]
    [InlineData("1")]
    public async Task A_mode_outside_the_four_names_is_a_bad_request(string? mode)
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);

        (await PutRuleAsync(client, device, ExtensionId('e'), mode))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _support.TasksAsync(device, DeviceTaskType.ApplyChromeExtensionPolicy)).ShouldBe(0);
    }

    [Fact]
    public async Task A_label_longer_than_the_column_is_a_bad_request()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);

        (await PutRuleAsync(client, device, ExtensionId('g'), "Blocked", new string('x', 257)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _support.TasksAsync(device, DeviceTaskType.ApplyChromeExtensionPolicy)).ShouldBe(0);
    }

    // -------------------------------------------------------------- agent gate

    [Fact]
    public async Task A_device_on_an_agent_below_the_floor_is_a_conflict_not_a_queued_task()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: "1.16.2");

        var response = await PutRuleAsync(client, device, ExtensionId('h'), "Blocked");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "the device is right there; pretending otherwise would send the operator hunting a ghost");
        (await response.Content.ReadAsStringAsync()).ShouldContain("1.23.0",
            customMessage: "the refusal names the version to update to");

        (await ListedRulesAsync(client, device)).ShouldBeEmpty("a refused rule is not stored either");
        (await _support.TasksAsync(device, DeviceTaskType.ApplyChromeExtensionPolicy)).ShouldBe(0);
    }

    [Fact]
    public async Task Lifting_a_rule_is_refused_too_when_the_agent_can_no_longer_take_the_policy()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var extension = ExtensionId('i');
        await ReadAsync(await PutRuleAsync(client, device, extension, "Blocked"));

        // The box was rebuilt onto an older agent after the rule was written.
        await using (var db = _fixture.CreateDbContext())
        {
            await db.Devices.Where(d => d.Id == device)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.AgentVersion, "1.18.0"));
        }

        (await client.DeleteAsync(Rule(device, extension))).StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "a lift that cannot reach the device would leave the console and the machine disagreeing");

        var listed = await ListedRulesAsync(client, device);
        listed.Count.ShouldBe(1, "the refused lift left the rule in place");
    }

    // --------------------------------------------------------------- not found

    [Fact]
    public async Task Lifting_a_rule_the_device_does_not_have_is_not_found()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        await ReadAsync(await PutRuleAsync(client, device, ExtensionId('j'), "Blocked"));

        (await client.DeleteAsync(Rule(device, ExtensionId('k'))))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync(Rule(device, "not-a-chrome-id")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound,
                "a malformed id can never name a stored rule");

        (await ListedRulesAsync(client, device)).Count.ShouldBe(1);
        (await _support.TasksAsync(device, DeviceTaskType.ApplyChromeExtensionPolicy)).ShouldBe(1,
            "only the set queued a task; the refused lifts queued nothing");
    }

    // ------------------------------------------------------------------- scope

    [Fact]
    public async Task An_out_of_scope_device_is_not_found_on_every_rule_route()
    {
        using var admin = await ItAdminAsync();
        var inScope = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var elsewhere = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var mine = await _support.CreateGroupAsync(admin, UniqueName("ChromeRuleScope"), inScope);

        using var scoped = await _support.ScopedAdminAsync(mine);

        (await scoped.GetAsync(Rules(inScope))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scoped.GetAsync(Rules(elsewhere))).StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "404, never 403: an administrator outside a device's scope must not learn that it exists");
        (await PutRuleAsync(scoped, elsewhere, ExtensionId('l'), "Blocked"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.DeleteAsync(Rule(elsewhere, ExtensionId('l'))))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await _support.TasksAsync(elsewhere, DeviceTaskType.ApplyChromeExtensionPolicy)).ShouldBe(0);
    }

    [Fact]
    public async Task A_device_of_another_organization_is_not_found()
    {
        using var client = await ItAdminAsync();
        var foreign = await SeedForeignDeviceAsync();

        (await client.GetAsync(Rules(foreign))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PutRuleAsync(client, foreign, ExtensionId('m'), "Blocked"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync(Rule(foreign, ExtensionId('m'))))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------- permissions

    /// <summary>
    /// The read/write split the role catalogue pins: every operational role reads
    /// the rules with <c>chrome.view</c>, but declaring or lifting one is
    /// <c>chrome.manage</c>, which Helpdesk and Auditor do not hold.
    /// </summary>
    [Fact]
    public async Task Reading_rides_on_chrome_view_and_writing_on_chrome_manage()
    {
        using var admin = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CapableAgent);
        var extension = ExtensionId('n');
        await ReadAsync(await PutRuleAsync(admin, device, extension, "Blocked"));

        foreach (var email in new[] { AdminApiPostgresFixture.HelpdeskEmail, AdminApiPostgresFixture.AuditorEmail })
        {
            using var reader = await _support.ClientAsAsync(email);

            (await reader.GetAsync(Rules(device))).StatusCode.ShouldBe(HttpStatusCode.OK,
                $"{email} holds chrome.view: which extension is ruled what is diagnosis");
            (await PutRuleAsync(reader, device, extension, "Removed"))
                .StatusCode.ShouldBe(HttpStatusCode.Forbidden,
                    $"{email} must not change what Chrome runs");
            (await reader.DeleteAsync(Rule(device, extension)))
                .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await ListedRulesAsync(admin, device))[0].GetProperty("mode").GetString().ShouldBe("Blocked",
            "the forbidden writes changed nothing");
        (await _support.TasksAsync(device, DeviceTaskType.ApplyChromeExtensionPolicy)).ShouldBe(1);
    }
}
