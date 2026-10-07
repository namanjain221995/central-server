using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Milestone 9 additions over real HTTP against real PostgreSQL: cancelling a
/// queued task, and the fleet-wide task list.
/// </summary>
/// <remarks>
/// Cancellation follows one rule — you may cancel exactly the tasks you are
/// permitted to queue — and refuses anything already handed to an agent, because
/// pretending to stop work that is mid-flight on a Windows machine would make
/// "Cancelled" a lie.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class TaskManagementEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri CancelOf(Guid deviceId, Guid taskId) =>
        new($"/admin/v1/devices/{deviceId}/tasks/{taskId}/cancel", UriKind.Relative);

    private async Task<Guid> SeedDeviceAsync(string hostname, string agentVersion = "1.0.0")
    {
        await using var db = _fixture.CreateDbContext();

        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var token = new EnrollmentToken(
            organizationId,
            $"task-mgmt-test-{Guid.CreateVersion7():N}",
            secretHash: Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            createdByUserId: await db.PlatformUsers.Select(u => u.Id).FirstAsync(),
            createdByDisplay: "task-mgmt-test",
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            maxUses: 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        await db.SaveChangesAsync();
        return device.Id;
    }

    /// <summary>Seeds a task directly in the given state, bypassing the HTTP queue path.</summary>
    private async Task<Guid> SeedTaskAsync(Guid deviceId, DeviceTaskType type, bool delivered = false)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var task = DeviceTask.Create(
            organizationId, deviceId, type, payloadJson: null,
            createdByUserId: await db.PlatformUsers.Select(u => u.Id).FirstAsync(),
            createdByDisplay: "task-mgmt-test",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));

        if (delivered)
        {
            task.TryDeliver(DateTimeOffset.UtcNow).ShouldBeTrue();
        }

        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<DeviceTaskStatus> StatusOfAsync(Guid taskId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.Id == taskId).Select(t => t.Status).SingleAsync();
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public async Task A_queued_task_can_be_cancelled_by_a_role_that_could_have_queued_it()
    {
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-1");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.RestartDevice);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusOfAsync(taskId)).ShouldBe(DeviceTaskStatus.Cancelled);
    }

    [Fact]
    public async Task A_delivered_task_cannot_be_cancelled()
    {
        // The agent may already be acting on it; the server refuses rather than
        // recording a cancellation that changes nothing on the machine. For a
        // restart on an agent that cannot abort (1.0.0 here), the same answer.
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-2");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.RestartDevice, delivered: true);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StatusOfAsync(taskId)).ShouldBe(DeviceTaskStatus.Delivered);

        var lock_ = await SeedTaskAsync(deviceId, DeviceTaskType.LockDevice, delivered: true);
        (await client.PostAsync(CancelOf(deviceId, lock_), content: null)).StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "only a restart can be taken back after delivery");
    }

    /// <summary>
    /// A restart the device already has can still be taken back on an agent
    /// that can abort it: the server queues a CancelRestart task and answers
    /// 202 with it. The restart itself changes only when the device confirms.
    /// </summary>
    [Fact]
    public async Task A_delivered_restart_on_a_current_agent_is_cancelled_through_the_device()
    {
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-3", agentVersion: "1.14.0");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.RestartDevice, delivered: true);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().ShouldBe("CancelRequested");
        var cancelTaskId = body.GetProperty("cancelTaskId").GetGuid();
        (await StatusOfAsync(taskId)).ShouldBe(DeviceTaskStatus.Delivered, "requested is not done");

        await using var db = _fixture.CreateDbContext();
        var cancel = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == cancelTaskId);
        cancel.Type.ShouldBe(DeviceTaskType.CancelRestart);
        cancel.DeviceId.ShouldBe(deviceId);
        JsonDocument.Parse(cancel.PayloadJson!).RootElement.GetProperty("restartTaskId").GetGuid().ShouldBe(taskId);

        // A second press reports the cancellation already on its way, not a second one.
        (await client.PostAsync(CancelOf(deviceId, taskId), content: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.CancelRestart)).ShouldBe(1);
    }

    [Fact]
    public async Task Helpdesk_can_cancel_a_restart_it_could_have_queued()
    {
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-3");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.RestartDevice);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Helpdesk_cannot_cancel_a_shutdown_it_could_never_have_queued()
    {
        // The same role, the same endpoint — the answer differs because the
        // authorization follows the task's own type.
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-4");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.ShutdownDevice);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await StatusOfAsync(taskId)).ShouldBe(DeviceTaskStatus.Queued);
    }

    [Fact]
    public async Task Auditor_cannot_cancel_anything()
    {
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-5");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.RefreshInventory);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await StatusOfAsync(taskId)).ShouldBe(DeviceTaskStatus.Queued);
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_cancel()
    {
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-6");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.RestartDevice);
        using var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cancelling_an_unknown_task_is_a_not_found()
    {
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-7");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(CancelOf(deviceId, Guid.CreateVersion7()), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancellation_is_audited_with_the_task_type()
    {
        var deviceId = await SeedDeviceAsync("TASK-CANCEL-8");
        var taskId = await SeedTaskAsync(deviceId, DeviceTaskType.RestartDevice);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        await client.PostAsync(CancelOf(deviceId, taskId), content: null);

        await using var db = _fixture.CreateDbContext();
        var audited = await db.AuditLogEntries.AsNoTracking()
            .AnyAsync(e => e.Action == "task.cancel.restartdevice" && e.DeviceId == deviceId);
        audited.ShouldBeTrue();
    }

    // ------------------------------------------------------------- fleet list

    [Fact]
    public async Task The_fleet_task_list_returns_recent_tasks_with_device_names()
    {
        var deviceId = await SeedDeviceAsync("TASK-LIST-1");
        await SeedTaskAsync(deviceId, DeviceTaskType.LockDevice);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var payload = await client.GetStringAsync(new Uri("/admin/v1/tasks?pageSize=100", UriKind.Relative));

        payload.ShouldContain("TASK-LIST-1");
        payload.ShouldContain("LockDevice");
        payload.ShouldContain("totalCount");
    }

    [Fact]
    public async Task The_fleet_task_list_never_carries_payloads()
    {
        var deviceId = await SeedDeviceAsync("TASK-LIST-2");
        await using (var db = _fixture.CreateDbContext())
        {
            var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
            var task = DeviceTask.Create(
                organizationId, deviceId, DeviceTaskType.ControlService,
                payloadJson: """{"serviceName":"PAYLOAD-MARKER-SVC","action":"Stop"}""",
                createdByUserId: await db.PlatformUsers.Select(u => u.Id).FirstAsync(),
                createdByDisplay: "task-mgmt-test",
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));
            db.DeviceTasks.Add(task);
            await db.SaveChangesAsync();
        }

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var payload = await client.GetStringAsync(new Uri("/admin/v1/tasks?pageSize=100", UriKind.Relative));

        payload.ShouldContain("TASK-LIST-2");
        // The task row appears; the payload it was queued with does not.
        payload.ShouldNotContain("PAYLOAD-MARKER-SVC");
    }

    [Fact]
    public async Task Auditor_can_read_the_fleet_task_list()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(new Uri("/admin/v1/tasks", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_read_the_fleet_task_list()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync(new Uri("/admin/v1/tasks", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_status_filter_is_a_bad_request_not_an_empty_page()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(new Uri("/admin/v1/tasks?status=Sideways", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_status_filter_returns_only_that_state()
    {
        var deviceId = await SeedDeviceAsync("TASK-LIST-3");
        await SeedTaskAsync(deviceId, DeviceTaskType.LockDevice);
        var cancelled = await SeedTaskAsync(deviceId, DeviceTaskType.RestartDevice);
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        await client.PostAsync(CancelOf(deviceId, cancelled), content: null);

        var payload = await client.GetStringAsync(
            new Uri("/admin/v1/tasks?status=Cancelled&pageSize=100", UriKind.Relative));

        payload.ShouldContain("Cancelled");
        payload.ShouldNotContain("\"Queued\"");
    }
}
