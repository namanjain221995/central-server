using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Announcements over real HTTP: who may broadcast, which devices actually get
/// the task, and what the history page says afterwards.
/// </summary>
/// <remarks>
/// Delivery rides the existing task channel, so most assertions are about the
/// <c>device_tasks</c> rows: a current agent gets exactly one ShowAnnouncement
/// task with the camelCase payload the agent reads, and a retired device or an
/// agent that predates the display executor gets nothing but a recorded reason.
/// The fixture database is shared by every API test class, so each test acts
/// only on devices and announcements it created.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class AnnouncementEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    private static readonly Uri Announcements = new("/admin/v1/announcements", UriKind.Relative);

    /// <summary>The oldest agent whose catalogue entry admits ShowAnnouncement.</summary>
    private const string CurrentAgent = "1.18.0";

    private sealed record SendResponse(Guid AnnouncementId, int Targeted, int Queued, int Offline, int Skipped);

    private Task<HttpClient> SuperAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.SuperAdminEmail);

    private static StringContent Send(object body) => Json(body);

    private async Task<SendResponse> SentAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SendResponse>())!;
    }

    private async Task RetireAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        (await db.Devices.SingleAsync(d => d.Id == deviceId)).Retire();
        await db.SaveChangesAsync();
    }

    private async Task<string?> SkipReasonAsync(Guid announcementId, Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AnnouncementDevices
            .Where(r => r.AnnouncementId == announcementId && r.DeviceId == deviceId)
            .Select(r => r.SkipReason)
            .SingleAsync();
    }

    /// <summary>A device in a second organization, with that organization's own built-in group.</summary>
    private async Task<(Guid DeviceId, Guid AllDevicesId)> SeedForeignDeviceAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization("Other Org", ("a" + Guid.CreateVersion7().ToString("N"))[..20]);
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var token = new EnrollmentToken(
            org.Id, $"announce-foreign-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "seed", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "ANN-FOREIGN", "m-" + Guid.CreateVersion7().ToString("N"),
            CurrentAgent, "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return (device.Id, device.DeviceGroupId);
    }

    // ------------------------------------------------------------------ groups

    [Fact]
    public async Task Sending_to_a_group_reaches_current_agents_and_records_why_old_ones_were_skipped()
    {
        using var client = await SuperAdminAsync();
        var current = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var newer = await _support.SeedDeviceAsync(agentVersion: "1.18.2");
        var old = await _support.SeedDeviceAsync(agentVersion: "1.16.0");
        var group = await _support.CreateGroupAsync(client, UniqueName("Announce"), current, newer, old);

        var result = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "Maintenance tonight",
            message = "Save your work by 18:00.",
            groupId = group,
        })));

        result.Targeted.ShouldBe(3);
        result.Queued.ShouldBe(2);
        result.Offline.ShouldBe(0, "every targeted device was seeded with a fresh heartbeat");
        result.Skipped.ShouldBe(1);

        (await _support.TasksAsync(current, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
        (await _support.TasksAsync(newer, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
        (await _support.TasksAsync(old, DeviceTaskType.ShowAnnouncement)).ShouldBe(0,
            "an agent without the display executor must never be queued the task");

        (await SkipReasonAsync(result.AnnouncementId, old)).ShouldBe("AgentTooOld");
        (await SkipReasonAsync(result.AnnouncementId, current)).ShouldBeNull();
    }

    /// <summary>
    /// The task is the wire contract: camelCase keys the agent reads verbatim,
    /// and a day of lifetime so an offline device still shows the notice when
    /// it reconnects.
    /// </summary>
    [Fact]
    public async Task The_queued_task_carries_the_camel_case_payload_and_lives_a_day()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var before = DateTimeOffset.UtcNow;

        var result = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "Printer room",
            message = "The third-floor printer is back.",
            deviceIds = new[] { device },
        })));

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleAsync(
            t => t.DeviceId == device && t.Type == DeviceTaskType.ShowAnnouncement);

        using var payload = JsonDocument.Parse(task.PayloadJson!);
        payload.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["announcementId", "title", "message"], ignoreOrder: true);
        payload.RootElement.GetProperty("announcementId").GetGuid().ShouldBe(result.AnnouncementId);
        payload.RootElement.GetProperty("title").GetString().ShouldBe("Printer room");
        payload.RootElement.GetProperty("message").GetString().ShouldBe("The third-floor printer is back.");

        task.ExpiresAt.ShouldBeGreaterThan(before.AddHours(23));
        task.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddHours(25));

        var audit = await db.AuditLogEntries
            .SingleOrDefaultAsync(a => a.Action == "announcement.send"
                                       && a.TargetId == result.AnnouncementId.ToString());
        audit.ShouldNotBeNull("a broadcast to screens must leave its own audit entry");
        audit.RequiredPermission.ShouldBe(Permissions.Announcement.Send);

        // Parsed, not substring-matched: the column is jsonb, and PostgreSQL
        // normalizes key order and spacing on the way back out.
        using var state = JsonDocument.Parse(audit.NewState!);
        state.RootElement.GetProperty("title").GetString().ShouldBe("Printer room");
        state.RootElement.GetProperty("targeted").GetInt32().ShouldBe(1);
        state.RootElement.GetProperty("queued").GetInt32().ShouldBe(1);
        state.RootElement.GetProperty("skipped").GetInt32().ShouldBe(0);
    }

    // ----------------------------------------------------------------- devices

    [Fact]
    public async Task A_multi_select_queues_one_task_per_device_and_a_duplicate_id_counts_once()
    {
        using var client = await SuperAdminAsync();
        var a = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var b = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);

        var result = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "Two screens",
            message = "Hello to exactly these machines.",
            deviceIds = new[] { a, a, b },
        })));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(2);
        result.Offline.ShouldBe(0);
        result.Skipped.ShouldBe(0);
        (await _support.TasksAsync(a, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
        (await _support.TasksAsync(b, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
    }

    /// <summary>
    /// An offline device still gets the task -- it lives a day exactly so the
    /// notice shows on reconnect -- but the response says so, with the same
    /// staleness rule as the console's Offline badge, so the banner can be
    /// honest about how many screens have actually seen anything yet.
    /// </summary>
    [Fact]
    public async Task An_offline_device_is_queued_but_counted_as_offline()
    {
        using var client = await SuperAdminAsync();
        var online = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var stale = await _support.SeedDeviceAsync(online: false, agentVersion: CurrentAgent);

        var result = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "Reconnect news",
            message = "You will read this when you are back.",
            deviceIds = new[] { online, stale },
        })));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(2, "offline is not skipped: the task waits a day for the device");
        result.Offline.ShouldBe(1);
        result.Skipped.ShouldBe(0);
        (await _support.TasksAsync(online, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
        (await _support.TasksAsync(stale, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
    }

    /// <summary>
    /// Retired devices receive no tasks of any kind, and the exclusion is
    /// recorded rather than the device silently vanishing from the announcement.
    /// </summary>
    [Fact]
    public async Task A_retired_device_is_recorded_as_skipped_and_gets_no_task()
    {
        using var client = await SuperAdminAsync();
        var active = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var retired = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        await RetireAsync(retired);

        var result = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "Still here",
            message = "Only live machines hear this.",
            deviceIds = new[] { active, retired },
        })));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(1);
        result.Offline.ShouldBe(0, "skipped is not offline: nothing was queued for the retired device");
        result.Skipped.ShouldBe(1);
        (await _support.TasksAsync(retired, DeviceTaskType.ShowAnnouncement)).ShouldBe(0);
        (await SkipReasonAsync(result.AnnouncementId, retired)).ShouldBe("Retired");
    }

    // ---------------------------------------------------------------- security

    [Fact]
    public async Task A_foreign_organizations_group_is_not_found()
    {
        using var client = await SuperAdminAsync();
        var (_, foreignGroup) = await SeedForeignDeviceAsync();

        var body = new { title = "Hi", message = "There", groupId = foreignGroup };
        (await client.PostAsync(Announcements, Send(body))).StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "a group the caller may not use must be indistinguishable from one that does not exist");

        (await client.PostAsync(Announcements, Send(new { title = "Hi", message = "There", groupId = Guid.CreateVersion7() })))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_foreign_device_id_is_silently_dropped()
    {
        using var client = await SuperAdminAsync();
        var mine = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var (foreign, _) = await SeedForeignDeviceAsync();

        var result = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "Scope check",
            message = "Only this organization's machines.",
            deviceIds = new[] { mine, foreign },
        })));

        result.Targeted.ShouldBe(1, "the foreign id must neither be targeted nor acknowledged");
        (await _support.TasksAsync(foreign, DeviceTaskType.ShowAnnouncement)).ShouldBe(0);
    }

    [Fact]
    public async Task The_target_shape_and_the_text_limits_are_enforced_with_400()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var group = await _support.CreateGroupAsync(client, UniqueName("AnnShape"), device);

        async Task<HttpStatusCode> PostAsync(object body) =>
            (await client.PostAsync(Announcements, Send(body))).StatusCode;

        // Neither target, and both at once: an announcement aims at exactly one.
        (await PostAsync(new { title = "Hi", message = "There" })).ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(new { title = "Hi", message = "There", groupId = group, deviceIds = new[] { device } }))
            .ShouldBe(HttpStatusCode.BadRequest);

        (await PostAsync(new { title = "", message = "There", deviceIds = new[] { device } }))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(new { title = "   ", message = "There", deviceIds = new[] { device } }))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(new { title = new string('t', 81), message = "There", deviceIds = new[] { device } }))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(new { title = "Hi", message = "", deviceIds = new[] { device } }))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(new { title = "Hi", message = new string('m', 501), deviceIds = new[] { device } }))
            .ShouldBe(HttpStatusCode.BadRequest);

        (await _support.TasksAsync(device, DeviceTaskType.ShowAnnouncement)).ShouldBe(0,
            "a refused request must queue nothing");
    }

    /// <summary>
    /// The agent's display executor refuses control characters other than line
    /// breaks and tabs, so the server must too -- accepting one here would queue
    /// a broadcast fleet-wide that then fails on every device.
    /// </summary>
    [Fact]
    public async Task Control_characters_are_refused_but_line_breaks_travel()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);

        (await client.PostAsync(Announcements, Send(new
        {
            title = "Hi",
            message = "Ding \u0007 dong.",
            deviceIds = new[] { device },
        }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsync(Announcements, Send(new
        {
            title = "Bell \u0007 title",
            message = "Fine message.",
            deviceIds = new[] { device },
        }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _support.TasksAsync(device, DeviceTaskType.ShowAnnouncement)).ShouldBe(0);

        var accepted = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "Two lines",
            message = "First line.\nSecond line.",
            deviceIds = new[] { device },
        })));

        accepted.Queued.ShouldBe(1, "a line break is ordinary announcement text, agent and server agree");
        (await _support.TasksAsync(device, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
    }

    /// <summary>
    /// The tasks, the announcement, its device rows and the audit entry are one
    /// decision, written in one transaction. A failure after some tasks are
    /// queued must take them all back; otherwise screens would show a broadcast
    /// that no history and no audit entry says was ever sent.
    /// </summary>
    /// <remarks>
    /// The failure is injected underneath the feature, as CatalogTests injects
    /// it: a trigger on the device-row table that raises for one device. It
    /// fires at the final save, after every task has been queued, so it is
    /// exactly the "something went wrong mid-fleet" case.
    /// </remarks>
    [Fact]
    public async Task A_failure_after_queueing_rolls_every_task_back()
    {
        using var client = await SuperAdminAsync();
        var first = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var poisoned = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var title = $"Fragile {Guid.CreateVersion7():N}"[..20];
        var trigger = $"anntest_reject_{Guid.CreateVersion7():N}";

        await using (var db = _fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION endpoint_platform.{trigger}() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'AnnouncementEndpointTests: simulated failure after queueing';
                END
                $$;
                CREATE TRIGGER {trigger}
                    BEFORE INSERT ON endpoint_platform.announcement_devices
                    FOR EACH ROW WHEN (NEW.device_id = '{poisoned}')
                    EXECUTE FUNCTION endpoint_platform.{trigger}();
                """);
        }

        try
        {
            var failed = await client.PostAsync(Announcements, Send(new
            {
                title,
                message = "This send dies between the queue and the record.",
                deviceIds = new[] { first, poisoned },
            }));

            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

            await using var db = _fixture.CreateDbContext();
            (await _support.TasksAsync(first, DeviceTaskType.ShowAnnouncement)).ShouldBe(0,
                "the task queued before the failure must roll back with it");
            (await _support.TasksAsync(poisoned, DeviceTaskType.ShowAnnouncement)).ShouldBe(0);
            (await db.Announcements.CountAsync(a => a.Title == title)).ShouldBe(0);
            (await db.AnnouncementDevices.CountAsync(r => r.DeviceId == first || r.DeviceId == poisoned)).ShouldBe(0);
        }
        finally
        {
            await using var db = _fixture.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync($"""
                DROP TRIGGER IF EXISTS {trigger} ON endpoint_platform.announcement_devices;
                DROP FUNCTION IF EXISTS endpoint_platform.{trigger}();
                """);
        }

        // Nothing leaked: the same send now lands whole -- one task per device,
        // one announcement, not a ghost of the failed attempt.
        var result = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title,
            message = "This send dies between the queue and the record.",
            deviceIds = new[] { first, poisoned },
        })));

        result.Queued.ShouldBe(2);
        (await _support.TasksAsync(first, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
        (await _support.TasksAsync(poisoned, DeviceTaskType.ShowAnnouncement)).ShouldBe(1);
    }

    /// <summary>
    /// Broadcasting is its own authority: reading software or supporting users
    /// does not imply speaking to every screen.
    /// </summary>
    [Fact]
    public async Task Only_holders_of_announcement_send_may_broadcast_or_read_the_history()
    {
        using var admin = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);

        // An administrator holding software.view and nothing else.
        var email = $"announce-viewer-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = Role.CreateCustom(
                org.Id, $"announce_viewer_{Guid.CreateVersion7():N}", "Announce Viewer", "software.view only");
            var view = await db.Permissions.SingleAsync(p => p.Key == Permissions.Software.View);
            role.GrantPermission(view.Id);
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            var user = new PlatformUser(org.Id, email, "Announce Viewer");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var body = new { title = "Nope", message = "Not yours to say.", deviceIds = new[] { device } };

        using var viewer = _fixture.CreateClientFor(await _fixture.SignInAsync(email));
        (await viewer.PostAsync(Announcements, Send(body))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.GetAsync(Announcements)).StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "the history shows what was said to the whole estate; it belongs to those allowed to say it");

        using var helpdesk = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        (await helpdesk.PostAsync(Announcements, Send(body))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var anonymous = _fixture.Factory.CreateClient();
        (await anonymous.PostAsync(Announcements, Send(body))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(Announcements)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await _support.TasksAsync(device, DeviceTaskType.ShowAnnouncement)).ShouldBe(0);
    }

    // ------------------------------------------------------------------- list

    [Fact]
    public async Task The_history_lists_the_send_with_its_counts()
    {
        using var client = await SuperAdminAsync();
        var current = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var old = await _support.SeedDeviceAsync(agentVersion: "1.16.0");
        var groupName = UniqueName("AnnList");
        var group = await _support.CreateGroupAsync(client, groupName, current, old);

        var sent = await SentAsync(await client.PostAsync(Announcements, Send(new
        {
            title = "History check",
            message = "This send shows up with its numbers.",
            groupId = group,
        })));

        var response = await client.GetAsync(Announcements);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();

        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == sent.AnnouncementId);
        row.GetProperty("title").GetString().ShouldBe("History check");
        row.GetProperty("message").GetString().ShouldBe("This send shows up with its numbers.");
        row.GetProperty("targetType").GetString().ShouldBe("Group");
        row.GetProperty("groupName").GetString().ShouldBe(groupName);
        row.GetProperty("createdByDisplay").GetString().ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
        row.GetProperty("targeted").GetInt32().ShouldBe(2);
        row.GetProperty("queued").GetInt32().ShouldBe(1);
        row.GetProperty("skipped").GetInt32().ShouldBe(1);
        row.GetProperty("pending").GetInt32().ShouldBe(1, "the task is still Queued; nothing has polled");
        row.GetProperty("succeeded").GetInt32().ShouldBe(0);
        row.GetProperty("failed").GetInt32().ShouldBe(0);
    }
}
