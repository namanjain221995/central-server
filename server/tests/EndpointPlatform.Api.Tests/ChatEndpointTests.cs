using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Chat;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Live IT-support chat over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Chat is first-line support, so unlike remote support Helpdesk may use it -- that
/// grant is the first thing these tests pin. The rest are the boundaries: the device
/// scope, the agent-version floor, one active chat per device, the plain-text body
/// rule, and that starting and ending a chat are audited while the messages are not.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ChatEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Devices = new("/admin/v1/chat/devices", UriKind.Relative);

    private static Uri Start(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/chat", UriKind.Relative);

    private static Uri Messages(Guid sessionId, int? after = null) =>
        new(after is null
            ? $"/admin/v1/chat/{sessionId}/messages"
            : $"/admin/v1/chat/{sessionId}/messages?after={after}", UriKind.Relative);

    private static Uri End(Guid sessionId) => new($"/admin/v1/chat/{sessionId}/end", UriKind.Relative);

    private static JsonContent MessageBody(string body = "hello there") => JsonContent.Create(new { body });

    // ---- clients -----------------------------------------------------------

    private async Task<HttpClient> ClientAsync(string email)
    {
        var token = SecretGenerator.GenerateSecret();

        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);

            db.AdminSessions.Add(new AdminSession(
                user.Id,
                SecretGenerator.HashSecret(token),
                user.SecurityStamp,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1),
                sourceIp: null,
                userAgent: "chat-tests"));

            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    // ---- seeding -----------------------------------------------------------

    /// <summary>A fresh active device. Defaults to an agent new enough for live chat.</summary>
    private async Task<Guid> SeedDeviceAsync(string agentVersion = "1.22.0")
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"chat-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "CHAT-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            agentVersion, null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> StartChatAsync(HttpClient client, Guid deviceId)
    {
        var response = await client.PostAsync(Start(deviceId), content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_can_do_nothing()
    {
        var deviceId = await SeedDeviceAsync();
        var sessionId = Guid.CreateVersion7();
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(Devices))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsync(Start(deviceId), content: null))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.GetAsync(Messages(sessionId)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsync(Messages(sessionId), MessageBody()))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsync(End(sessionId), content: null))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The central RBAC claim for this feature: chat is a support capability, so
    /// Helpdesk holds it -- unlike remote support, which Helpdesk cannot touch.
    /// Auditor, which mutates nothing, cannot.
    /// </summary>
    [Fact]
    public async Task Helpdesk_can_chat_but_auditor_cannot()
    {
        var deviceId = await SeedDeviceAsync();

        using var auditor = await ClientAsync(AdminApiPostgresFixture.AuditorEmail);
        (await auditor.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditor.PostAsync(Start(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var helpdesk = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);
        (await helpdesk.GetAsync(Devices)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Helpdesk can genuinely open a chat, not merely load the list.
        var sessionId = await StartChatAsync(helpdesk, deviceId);
        (await helpdesk.PostAsync(Messages(sessionId), MessageBody())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_device_and_its_session_outside_the_callers_scope_are_not_found()
    {
        var inScope = await SeedDeviceAsync();
        var outOfScope = await SeedDeviceAsync();

        // A full-scope admin opens a chat on the out-of-scope device so there is a
        // real session for the scoped admin to be refused.
        using var fullScope = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var foreignSession = await StartChatAsync(fullScope, outOfScope);

        var email = $"chat-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.ItAdministrator);

            var group = new DeviceGroup(org.Id, $"ChatScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            (await db.Devices.SingleAsync(d => d.Id == inScope)).MoveToGroup(group.Id);

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        using var client = await ClientAsync(email);

        // Starting a chat on the out-of-scope device answers 404, never 403: the
        // admin must not be able to map the estate by probing.
        (await client.PostAsync(Start(outOfScope), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And every session-keyed route on the foreign session is 404 too.
        (await client.GetAsync(Messages(foreignSession))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(Messages(foreignSession), MessageBody())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(End(foreignSession), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- start -------------------------------------------------------------

    [Fact]
    public async Task Starting_a_chat_creates_an_active_session_and_queues_the_open_task()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var sessionId = await StartChatAsync(client, deviceId);

        await using var db = _fixture.CreateDbContext();

        var session = await db.ChatSessions.SingleAsync(s => s.Id == sessionId);
        session.DeviceId.ShouldBe(deviceId);
        session.Status.ShouldBe(ChatSessionStatus.Active);
        session.EndedReason.ShouldBe(ChatEndedReason.None);

        var task = await db.DeviceTasks.SingleAsync(
            t => t.DeviceId == deviceId && t.Type == DeviceTaskType.StartLiveChat);
        task.PayloadJson.ShouldNotBeNull().ShouldContain(sessionId.ToString());

        // The device now shows up on the active-chat list.
        // The picker lists every active device; the one we just started a chat with
        // must carry that session's id, while chat-less devices carry null.
        var listed = await (await client.GetAsync(Devices)).Content.ReadFromJsonAsync<JsonElement>();
        listed.EnumerateArray()
            .Where(d => d.GetProperty("activeSessionId").ValueKind != JsonValueKind.Null)
            .Select(d => d.GetProperty("activeSessionId").GetGuid())
            .ShouldContain(sessionId);
    }

    [Fact]
    public async Task A_second_start_for_a_device_already_in_chat_is_a_conflict()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        await StartChatAsync(client, deviceId);

        (await client.PostAsync(Start(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.ChatSessions.CountAsync(
            s => s.DeviceId == deviceId && s.Status == ChatSessionStatus.Active)).ShouldBe(1);
    }

    [Fact]
    public async Task Starting_a_chat_on_an_old_agent_is_refused()
    {
        var deviceId = await SeedDeviceAsync(agentVersion: "1.21.0");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(Start(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.ChatSessions.AnyAsync(s => s.DeviceId == deviceId)).ShouldBeFalse();
    }

    // ---- messages ----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has a \u0001 control char")]
    public async Task Sending_requires_a_non_empty_plain_text_body(string body)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var sessionId = await StartChatAsync(client, deviceId);

        (await client.PostAsync(Messages(sessionId), JsonContent.Create(new { body })))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_messages_are_sequenced_and_readable_after_a_cursor()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var sessionId = await StartChatAsync(client, deviceId);

        var first = await (await client.PostAsync(Messages(sessionId), MessageBody("first")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await client.PostAsync(Messages(sessionId), MessageBody("second")))
            .Content.ReadFromJsonAsync<JsonElement>();

        first.GetProperty("seq").GetInt32().ShouldBe(1);
        second.GetProperty("seq").GetInt32().ShouldBe(2);

        var all = await (await client.GetAsync(Messages(sessionId, after: 0))).Content.ReadFromJsonAsync<JsonElement>();
        all.GetProperty("ended").GetBoolean().ShouldBeFalse();
        var messages = all.GetProperty("messages").EnumerateArray().ToList();
        messages.Count.ShouldBe(2);
        messages[0].GetProperty("sender").GetString().ShouldBe("Admin");
        messages[0].GetProperty("body").GetString().ShouldBe("first");

        // The cursor pages: everything after seq 1 is just the second message.
        var afterFirst = await (await client.GetAsync(Messages(sessionId, after: 1)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var tail = afterFirst.GetProperty("messages").EnumerateArray().ToList();
        tail.Count.ShouldBe(1);
        tail[0].GetProperty("seq").GetInt32().ShouldBe(2);
    }

    // ---- end ---------------------------------------------------------------

    [Fact]
    public async Task Ending_a_chat_closes_the_session_and_queues_the_close_task()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var sessionId = await StartChatAsync(client, deviceId);

        var ended = await client.PostAsync(End(sessionId), content: null);
        ended.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().ShouldBe("Ended");

        await using var db = _fixture.CreateDbContext();
        var session = await db.ChatSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.ShouldBe(ChatSessionStatus.Ended);
        session.EndedReason.ShouldBe(ChatEndedReason.AdminEnded);
        session.EndedAt.ShouldNotBeNull();

        (await db.DeviceTasks.AnyAsync(
            t => t.DeviceId == deviceId && t.Type == DeviceTaskType.EndLiveChat)).ShouldBeTrue();

        // A message to a closed session is refused, but the history stays readable.
        (await client.PostAsync(Messages(sessionId), MessageBody())).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var read = await (await client.GetAsync(Messages(sessionId))).Content.ReadFromJsonAsync<JsonElement>();
        read.GetProperty("ended").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Ending_an_unknown_session_is_not_found()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(End(Guid.CreateVersion7()), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- audit -------------------------------------------------------------

    /// <summary>
    /// The session's boundaries are audited; the chatter inside it is not, so one
    /// conversation leaves two audit rows rather than one per line.
    /// </summary>
    [Fact]
    public async Task Starting_and_ending_are_audited_but_individual_messages_are_not()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var sessionId = await StartChatAsync(client, deviceId);

        await client.PostAsync(Messages(sessionId), MessageBody("a line"));
        await client.PostAsync(Messages(sessionId), MessageBody("another line"));
        await client.PostAsync(End(sessionId), content: null);

        await using var db = _fixture.CreateDbContext();
        var actions = await db.AuditLogEntries
            .Where(a => a.Action.StartsWith("chat."))
            .Select(a => a.Action)
            .ToListAsync();

        actions.ShouldContain("chat.start");
        actions.ShouldContain("chat.end");

        // No per-message audit: the two message POSTs added nothing to the trail.
        actions.Count(a => a is "chat.start" or "chat.end").ShouldBe(actions.Count);
    }
}
