using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Restart now, or after a delay: what is queued, what is refused, and who may
/// ask.
/// </summary>
/// <remarks>
/// The delay becomes the task's <c>graceSeconds</c> and nothing else -- the same
/// payload every deployed agent reads -- so most of these read the queued row
/// back and check that one number. The boundary is the deployed agent's clamp,
/// and the tests name it as such.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class RestartDeviceEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri RestartOf(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/actions/restart", UriKind.Relative);

    private static Uri ActionOf(Guid deviceId, string action) =>
        new($"/admin/v1/devices/{deviceId}/actions/{action}", UriKind.Relative);

    private async Task<Guid> SeedDeviceAsync(string agentVersion = "1.9.0")
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var token = new EnrollmentToken(
            organizationId,
            $"restart-test-{Guid.CreateVersion7():N}",
            secretHash: Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            createdByUserId: await db.PlatformUsers.Select(u => u.Id).FirstAsync(),
            createdByDisplay: "restart-test",
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            maxUses: 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, $"RST-{Guid.CreateVersion7():N}"[..12], $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<HttpClient> ItAdminAsync() =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<DeviceTask> QueuedRestartAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.RestartDevice)
            .OrderByDescending(t => t.CreatedAt)
            .FirstAsync();
    }

    // Parsed rather than string-matched: payloads are stored as jsonb and come
    // back reformatted by Postgres, so the bytes are never the ones written.
    private static int GraceOf(DeviceTask task) =>
        JsonDocument.Parse(task.PayloadJson!).RootElement.GetProperty("graceSeconds").GetInt32();

    private static string MessageOf(DeviceTask task) =>
        JsonDocument.Parse(task.PayloadJson!).RootElement.GetProperty("message").GetString()!;

    // ---------------------------------------------------------------- queues

    /// <summary>A bare POST is what every existing caller sends, and it still means "now": a thirty-second warning.</summary>
    [Fact]
    public async Task Restart_now_with_no_body_queues_the_standard_warning()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(RestartOf(deviceId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().ShouldBe("Queued");
        body.GetProperty("graceSeconds").GetInt32().ShouldBe(RestartGrace.ImmediateSeconds);
        body.GetProperty("expiresAt").GetDateTimeOffset()
            .ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        var task = await QueuedRestartAsync(deviceId);
        task.Status.ShouldBe(DeviceTaskStatus.Queued);
        GraceOf(task).ShouldBe(30);
        MessageOf(task).ShouldBe("Your IT administrator initiated a restart.");
    }

    [Fact]
    public async Task Restart_now_as_an_explicit_zero_queues_the_standard_warning()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(RestartOf(deviceId), Json("""{"delaySeconds":0}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        GraceOf(await QueuedRestartAsync(deviceId)).ShouldBe(30);
    }

    /// <summary>A timed restart is the same task with the delay as its grace period -- one field, no second clock.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    [InlineData(3600)]
    public async Task A_timed_restart_queues_the_delay_as_the_grace_period(int delay)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(RestartOf(deviceId), Json($$"""{"delaySeconds":{{delay}}}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("graceSeconds").GetInt32().ShouldBe(delay);

        var task = await QueuedRestartAsync(deviceId);
        GraceOf(task).ShouldBe(delay);
        MessageOf(task).ShouldContain("scheduled a restart in");

        // The payload is only ever those two fields: no deadline, no absolute time.
        var keys = JsonDocument.Parse(task.PayloadJson!).RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        keys.ShouldBe(["graceSeconds", "message"]);
    }

    /// <summary>
    /// An explicit 30 and a 0 are the same request, because "now" <em>is</em> a
    /// thirty-second warning. They queue an identical task, so they carry an
    /// identical message: inventing a distinction the device cannot observe
    /// would tell the signed-in user two different things about one behaviour.
    /// </summary>
    [Fact]
    public async Task An_explicit_thirty_is_the_same_request_as_now()
    {
        var zeroDevice = await SeedDeviceAsync();
        var thirtyDevice = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        await client.PostAsync(RestartOf(zeroDevice), Json("""{"delaySeconds":0}"""));
        await client.PostAsync(RestartOf(thirtyDevice), Json("""{"delaySeconds":30}"""));

        var zero = await QueuedRestartAsync(zeroDevice);
        var thirty = await QueuedRestartAsync(thirtyDevice);

        GraceOf(zero).ShouldBe(30);
        GraceOf(thirty).ShouldBe(30);
        MessageOf(thirty).ShouldBe(MessageOf(zero));
    }

    [Fact]
    public async Task Queueing_a_restart_is_audited_with_the_requested_timing()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        (await client.PostAsync(RestartOf(deviceId), Json("""{"delaySeconds":600}"""))).StatusCode
            .ShouldBe(HttpStatusCode.Accepted);

        await using var db = _fixture.CreateDbContext();
        var audit = await db.AuditLogEntries.AsNoTracking()
            .Where(a => a.DeviceId == deviceId && a.Action == "task.queue.restartdevice")
            .OrderByDescending(a => a.OccurredAt)
            .FirstAsync();

        audit.ActorDisplay.ShouldBe(AdminApiPostgresFixture.ItAdminEmail);
        audit.RequiredPermission.ShouldBe(Permissions.Device.Restart);
        audit.NewState.ShouldNotBeNull();
        // Parsed: audit state is stored as jsonb and comes back reformatted.
        JsonDocument.Parse(audit.NewState).RootElement.GetProperty("graceSeconds").GetInt32().ShouldBe(600);
    }

    // --------------------------------------------------------------- refuses

    [Theory]
    [InlineData(-1)]
    [InlineData(-3600)]
    [InlineData(int.MinValue)]
    public async Task A_negative_delay_is_refused(int delay)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(RestartOf(deviceId), Json($$"""{"delaySeconds":{{delay}}}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("delaySeconds must be 0");
        await AssertNothingQueuedAsync(deviceId);
    }

    /// <summary>Below the floor but above zero: refused, never silently rounded up.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(29)]
    public async Task A_delay_below_the_floor_is_refused(int delay)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(RestartOf(deviceId), Json($$"""{"delaySeconds":{{delay}}}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertNothingQueuedAsync(deviceId);
    }

    /// <summary>Over the deployed agent's clamp: refused, because the machine would go down earlier than promised.</summary>
    [Theory]
    [InlineData(3601)]
    [InlineData(86_400)]
    [InlineData(int.MaxValue)]
    public async Task An_excessive_delay_is_refused(int delay)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(RestartOf(deviceId), Json($$"""{"delaySeconds":{{delay}}}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("3600");
        await AssertNothingQueuedAsync(deviceId);
    }

    /// <summary>A value that does not fit the field -- an overflow, a string, garbage -- is a 400, not a 500 and not a restart.</summary>
    [Theory]
    [InlineData("""{"delaySeconds":99999999999999999999}""")]
    [InlineData("""{"delaySeconds":"soon"}""")]
    [InlineData("""{"delaySeconds":1.5}""")]
    [InlineData("not json at all")]
    public async Task A_malformed_body_is_refused(string body)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(RestartOf(deviceId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertNothingQueuedAsync(deviceId);
    }

    /// <summary>
    /// One restart in flight per device. The second is answered 409 naming the
    /// first; it is not queued as a task the operator will later see fail.
    /// </summary>
    [Fact]
    public async Task A_second_restart_while_one_is_queued_is_a_conflict_naming_the_first()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var first = await client.PostAsync(RestartOf(deviceId), Json("""{"delaySeconds":300}"""));
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();

        var second = await client.PostAsync(RestartOf(deviceId), content: null);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("taskId").GetGuid().ShouldBe(firstId);
        problem.GetProperty("status").GetString().ShouldBe("Queued");

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.RestartDevice))
            .ShouldBe(1);
    }

    /// <summary>Once the first restart has settled, a new one may be queued.</summary>
    [Fact]
    public async Task A_new_restart_may_be_queued_once_the_previous_one_has_settled()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        (await client.PostAsync(RestartOf(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using (var db = _fixture.CreateDbContext())
        {
            var task = await db.DeviceTasks.SingleAsync(t => t.DeviceId == deviceId);
            task.TryDeliver(DateTimeOffset.UtcNow).ShouldBeTrue();
            task.TryComplete(true, "Restart accepted by Windows.", null, DateTimeOffset.UtcNow).ShouldBeTrue();
            await db.SaveChangesAsync();
        }

        (await client.PostAsync(RestartOf(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // ---------------------------------------------------------------- scope

    /// <summary>
    /// Permission is not enough. An administrator scoped to a group must not be
    /// able to restart a device outside it by knowing its id -- and is told the
    /// device is not there, the same answer every device-scoped route gives.
    /// </summary>
    [Fact]
    public async Task An_administrator_cannot_restart_a_device_outside_their_scope()
    {
        var inScope = await SeedDeviceAsync();
        var outOfScope = await SeedDeviceAsync();
        var email = $"restart-scoped-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == SystemRoles.ItAdministrator);
            var group = new DeviceGroup(org.Id, $"RestartScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            db.DeviceGroupMemberships.Add(new DeviceGroupMembership(group.Id, inScope));

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
                DateTimeOffset.UtcNow);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));

        (await client.PostAsync(RestartOf(inScope), Json("""{"delaySeconds":60}"""))).StatusCode
            .ShouldBe(HttpStatusCode.Accepted, "the device is in a group this admin is scoped to");

        (await client.PostAsync(RestartOf(outOfScope), Json("""{"delaySeconds":60}"""))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound, "a device outside the scoped group stays invisible");
        await AssertNothingQueuedAsync(outOfScope);

        // The other power and session actions share the same gate.
        foreach (var action in new[] { "shutdown", "lock", "signout" })
        {
            (await client.PostAsync(ActionOf(outOfScope, action), content: null)).StatusCode
                .ShouldBe(HttpStatusCode.NotFound, $"{action} must be scoped exactly as restart is");
        }
    }

    [Fact]
    public async Task An_unknown_device_is_not_found()
    {
        using var client = await ItAdminAsync();

        (await client.PostAsync(RestartOf(Guid.CreateVersion7()), content: null)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_retired_device_cannot_be_restarted()
    {
        var deviceId = await SeedDeviceAsync();
        await using (var db = _fixture.CreateDbContext())
        {
            var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
            device.Retire();
            await db.SaveChangesAsync();
        }

        using var client = await ItAdminAsync();

        (await client.PostAsync(RestartOf(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertNothingQueuedAsync(deviceId);
    }

    // ------------------------------------------------------- no regression

    /// <summary>The three actions that share the helper still queue exactly as before.</summary>
    [Theory]
    [InlineData("shutdown", DeviceTaskType.ShutdownDevice)]
    [InlineData("lock", DeviceTaskType.LockDevice)]
    [InlineData("signout", DeviceTaskType.SignOutUser)]
    public async Task The_other_device_actions_still_queue(string action, DeviceTaskType expected)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();

        var response = await client.PostAsync(ActionOf(deviceId, action), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.SingleAsync(t => t.DeviceId == deviceId)).Type.ShouldBe(expected);
    }

    /// <summary>The device task list now carries the deadline and the structured result the console reads.</summary>
    [Fact]
    public async Task The_device_task_list_exposes_the_deadline_and_the_result()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ItAdminAsync();
        (await client.PostAsync(RestartOf(deviceId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using (var db = _fixture.CreateDbContext())
        {
            var task = await db.DeviceTasks.SingleAsync(t => t.DeviceId == deviceId);
            task.TryDeliver(DateTimeOffset.UtcNow).ShouldBeTrue();
            task.TryComplete(true, "Restart accepted by Windows.",
                """{"graceSeconds":30,"restartAt":"2026-09-12T10:00:30+00:00","outcome":"Scheduled","code":null}""",
                DateTimeOffset.UtcNow).ShouldBeTrue();
            await db.SaveChangesAsync();
        }

        var list = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/admin/v1/devices/{deviceId}/tasks", UriKind.Relative));
        var row = list.EnumerateArray().Single();

        row.GetProperty("expiresAt").ValueKind.ShouldBe(JsonValueKind.String);
        var result = JsonDocument.Parse(row.GetProperty("resultJson").GetString()!).RootElement;
        result.GetProperty("outcome").GetString().ShouldBe("Scheduled");
        result.GetProperty("restartAt").GetDateTimeOffset().ShouldBe(
            new DateTimeOffset(2026, 9, 12, 10, 0, 30, TimeSpan.Zero));
    }

    private async Task AssertNothingQueuedAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.AnyAsync(t => t.DeviceId == deviceId)).ShouldBeFalse("a refused request must queue nothing");
    }
}
