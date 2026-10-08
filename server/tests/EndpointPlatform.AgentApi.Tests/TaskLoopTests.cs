using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The full task loop over real HTTP + PostgreSQL: queue server-side, agent claims
/// via poll, agent reports result, terminal state is immutable and scoped.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class TaskLoopTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private async Task<(Guid DeviceId, string Credential, Guid OrgId)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(
            org.Id, $"tk-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var enroll = Req(AgentProtocol.Routes.Enroll, new EnrollRequest(
            secret, "TASK-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null));
        var resp = await client.SendAsync(enroll);
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}", org.Id);
    }

    private static HttpRequestMessage Req(string route, object? body = null, string? credential = null, HttpMethod? method = null)
    {
        var m = new HttpRequestMessage(method ?? HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative));
        if (body is not null) m.Content = JsonContent.Create(body);
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    private async Task<Guid> QueueTaskAsync(Guid orgId, Guid deviceId, DeviceTaskType type)
    {
        await using var db = _fixture.CreateDbContext();
        var task = DeviceTask.Create(orgId, deviceId, type, null, Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    [Fact]
    public async Task Heartbeat_signals_pending_tasks()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var before = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("TASK-PC", "1.0.0", null, DateTimeOffset.UtcNow), credential));
        (await before.Content.ReadFromJsonAsync<HeartbeatResponse>())!.TasksPending.ShouldBeFalse();

        await QueueTaskAsync(orgId, deviceId, DeviceTaskType.Ping);

        var after = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("TASK-PC", "1.0.0", null, DateTimeOffset.UtcNow), credential));
        (await after.Content.ReadFromJsonAsync<HeartbeatResponse>())!.TasksPending.ShouldBeTrue();
    }

    [Fact]
    public async Task Claim_delivers_the_task_and_result_completes_it()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        var taskId = await QueueTaskAsync(orgId, deviceId, DeviceTaskType.Ping);
        using var client = _fixture.Factory.CreateClient();

        var before = DateTimeOffset.UtcNow;
        var claim = await client.SendAsync(Req(AgentProtocol.Routes.Tasks, credential: credential, method: HttpMethod.Get));
        claim.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tasks = (await claim.Content.ReadFromJsonAsync<AgentTaskListResponse>())!;
        tasks.Tasks.ShouldContain(t => t.TaskId == taskId && t.Type == "Ping");

        // The server's clock rides along, so an agent can place a deadline
        // given in server time on its own clock without trusting either to agree.
        var serverTime = tasks.Tasks.Single(t => t.TaskId == taskId).ServerTime;
        serverTime.ShouldNotBeNull();
        serverTime.Value.ShouldBeInRange(before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.DeviceTasks.SingleAsync(t => t.Id == taskId)).Status.ShouldBe(DeviceTaskStatus.Delivered);
        }

        var result = await client.SendAsync(Req(
            $"{AgentProtocol.Routes.Tasks}/{taskId}{AgentProtocol.Routes.TaskResultSuffix}",
            new AgentTaskResult(true, "pong", null), credential));
        result.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var db = _fixture.CreateDbContext())
        {
            var task = await db.DeviceTasks.SingleAsync(t => t.Id == taskId);
            task.Status.ShouldBe(DeviceTaskStatus.Succeeded);
            task.ResultMessage.ShouldBe("pong");
        }
    }

    [Fact]
    public async Task A_second_claim_does_not_redeliver_an_already_claimed_task()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        await QueueTaskAsync(orgId, deviceId, DeviceTaskType.Ping);
        using var client = _fixture.Factory.CreateClient();

        var first = await (await client.SendAsync(Req(AgentProtocol.Routes.Tasks, credential: credential, method: HttpMethod.Get)))
            .Content.ReadFromJsonAsync<AgentTaskListResponse>();
        first!.Tasks.Count.ShouldBe(1);

        var second = await (await client.SendAsync(Req(AgentProtocol.Routes.Tasks, credential: credential, method: HttpMethod.Get)))
            .Content.ReadFromJsonAsync<AgentTaskListResponse>();
        second!.Tasks.ShouldBeEmpty("a delivered task must not be handed out again");
    }

    [Fact]
    public async Task An_unauthenticated_claim_is_rejected()
    {
        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Tasks, method: HttpMethod.Get));
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_result_for_an_undelivered_task_is_rejected()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        var taskId = await QueueTaskAsync(orgId, deviceId, DeviceTaskType.Ping);
        using var client = _fixture.Factory.CreateClient();

        // Post a result WITHOUT claiming first -> task still Queued -> 404.
        var result = await client.SendAsync(Req(
            $"{AgentProtocol.Routes.Tasks}/{taskId}{AgentProtocol.Routes.TaskResultSuffix}",
            new AgentTaskResult(true, "forged", null), credential));
        result.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_device_cannot_report_results_for_another_devices_task()
    {
        var (_, credentialA, orgId) = await EnrollAsync();
        var (deviceB, _, _) = await EnrollAsync();
        var taskForB = await QueueTaskAsync(orgId, deviceB, DeviceTaskType.Ping);
        using var client = _fixture.Factory.CreateClient();

        // Device A tries to complete device B's task.
        var result = await client.SendAsync(Req(
            $"{AgentProtocol.Routes.Tasks}/{taskForB}{AgentProtocol.Routes.TaskResultSuffix}",
            new AgentTaskResult(true, "cross-device", null), credentialA));
        result.StatusCode.ShouldBe(HttpStatusCode.NotFound, "task ids are scoped to the authenticated device");
    }
}
