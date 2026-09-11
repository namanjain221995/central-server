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
/// The agent's task poll, end to end over HTTP: what a claimed restart carries,
/// and that claiming is not repeatable.
/// </summary>
/// <remarks>
/// The service-level race is covered in the Infrastructure tests; this proves
/// the same guarantees hold through the real endpoint, authentication and
/// serialisation -- in particular that the deadline reaches the agent, which is
/// what lets it refuse a restart that arrives too late.
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class TaskClaimEndpointTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static HttpRequestMessage Claim(string credential)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Tasks, UriKind.Relative));
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return message;
    }

    private async Task<(Guid OrgId, Guid DeviceId, string Credential)> SeedDeviceCredentialAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"claim-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "task-claim-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Domain.Devices.Device.Enroll(
            org.Id, "CLAIM-PC", $"machine-{Guid.CreateVersion7()}", "1.9.0",
            "Microsoft Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var secret = SecretGenerator.GenerateSecret();
        db.AgentCredentials.Add(new AgentCredential(
            device.Id, SecretGenerator.GenerateKeyId(), SecretGenerator.HashSecret(secret),
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync();

        var credential = await db.AgentCredentials.AsNoTracking().SingleAsync(c => c.DeviceId == device.Id);
        return (org.Id, device.Id, $"{credential.KeyId}.{secret}");
    }

    private async Task<DeviceTask> SeedRestartAsync(Guid orgId, Guid deviceId, TimeSpan ttl, DateTimeOffset? createdAt = null)
    {
        await using var db = _fixture.CreateDbContext();
        var task = DeviceTask.Create(
            orgId, deviceId, DeviceTaskType.RestartDevice,
            """{"graceSeconds":300,"message":"IT scheduled a restart."}""",
            Guid.CreateVersion7(), "task-claim-tests", createdAt ?? DateTimeOffset.UtcNow, ttl);
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    /// <summary>
    /// The deadline travels with the task. Without it the agent has no way to
    /// tell a restart it should refuse from one it should carry out.
    /// </summary>
    [Fact]
    public async Task A_claimed_restart_carries_its_payload_and_its_deadline()
    {
        var (orgId, deviceId, credential) = await SeedDeviceCredentialAsync();
        var task = await SeedRestartAsync(orgId, deviceId, TimeSpan.FromMinutes(15));
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Claim(credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentTaskListResponse>();
        body.ShouldNotBeNull();
        var claimed = body.Tasks.ShouldHaveSingleItem();
        claimed.TaskId.ShouldBe(task.Id);
        claimed.Type.ShouldBe("RestartDevice");
        claimed.PayloadJson.ShouldNotBeNull();
        // Parsed, not string-matched: the payload is stored as jsonb and comes
        // back reformatted by Postgres, so the bytes are not the ones written.
        var payload = System.Text.Json.JsonDocument.Parse(claimed.PayloadJson).RootElement;
        payload.GetProperty("graceSeconds").GetInt32().ShouldBe(300);

        // Compared against the stored row, not the in-memory one: Postgres keeps
        // microseconds and .NET keeps 100ns ticks, so the value the agent sees is
        // the persisted one and that is what has to match.
        await using var db = _fixture.CreateDbContext();
        var stored = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        claimed.ExpiresAt.ShouldBe(stored.ExpiresAt);
    }

    /// <summary>
    /// Polling twice does not hand the restart out twice. The second poll --
    /// a retry, a duplicate agent, a replayed request -- gets an empty list.
    /// </summary>
    [Fact]
    public async Task A_second_poll_does_not_receive_the_same_restart()
    {
        var (orgId, deviceId, credential) = await SeedDeviceCredentialAsync();
        await SeedRestartAsync(orgId, deviceId, TimeSpan.FromMinutes(15));
        using var client = _fixture.Factory.CreateClient();

        var first = await (await client.SendAsync(Claim(credential))).Content.ReadFromJsonAsync<AgentTaskListResponse>();
        var second = await (await client.SendAsync(Claim(credential))).Content.ReadFromJsonAsync<AgentTaskListResponse>();

        first!.Tasks.Count.ShouldBe(1);
        second!.Tasks.ShouldBeEmpty("a delivered task must never be delivered again");

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Status == DeviceTaskStatus.Delivered))
            .ShouldBe(1);
    }

    /// <summary>Two polls in flight at once still produce exactly one delivery.</summary>
    [Fact]
    public async Task Two_simultaneous_polls_produce_exactly_one_delivery()
    {
        var (orgId, deviceId, credential) = await SeedDeviceCredentialAsync();
        await SeedRestartAsync(orgId, deviceId, TimeSpan.FromMinutes(15));
        using var client = _fixture.Factory.CreateClient();

        var responses = await Task.WhenAll(client.SendAsync(Claim(credential)), client.SendAsync(Claim(credential)));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<AgentTaskListResponse>()));

        bodies.Sum(b => b!.Tasks.Count).ShouldBe(1, "the restart must be handed to exactly one poll");

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.DeviceId == deviceId);
        row.Status.ShouldBe(DeviceTaskStatus.Delivered);
    }

    /// <summary>
    /// A restart whose deadline passed before the device polled is marked
    /// Expired by the claim and is never sent, so a machine coming back online
    /// late does not restart on a request nobody is waiting for.
    /// </summary>
    [Fact]
    public async Task An_expired_restart_is_never_delivered()
    {
        var (orgId, deviceId, credential) = await SeedDeviceCredentialAsync();
        var task = await SeedRestartAsync(
            orgId, deviceId, TimeSpan.FromMinutes(15), createdAt: DateTimeOffset.UtcNow.AddMinutes(-16));
        using var client = _fixture.Factory.CreateClient();

        var body = await (await client.SendAsync(Claim(credential))).Content.ReadFromJsonAsync<AgentTaskListResponse>();

        body!.Tasks.ShouldBeEmpty();
        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        row.Status.ShouldBe(DeviceTaskStatus.Expired);
        row.DeliveredAt.ShouldBeNull();
    }

    /// <summary>Only the credentialled device sees its tasks; a bad credential sees nothing and learns nothing.</summary>
    [Fact]
    public async Task A_claim_without_a_valid_credential_is_unauthorized()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Claim("nobody.nothing"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
