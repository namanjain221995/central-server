using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Phase 1 heartbeat matrix: authenticated, unauthenticated, malformed
/// credential, malformed body, retired device.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class HeartbeatEndpointTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private async Task<(Guid DeviceId, string HeaderValue)> EnrollDeviceAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var secret = SecretGenerator.GenerateSecret();
        var token = new EnrollmentToken(
            organization.Id,
            $"hb-token-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(),
            "test-admin@company.local",
            DateTimeOffset.UtcNow.AddHours(1),
            maxUses: 1);
        dbContext.EnrollmentTokens.Add(token);
        await dbContext.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var enroll = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll, UriKind.Relative))
        {
            Content = JsonContent.Create(new EnrollRequest(
                secret, "HB-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", "Windows 11 Test")),
        };
        enroll.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());

        var response = await client.SendAsync(enroll);
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage MakeHeartbeat(string? credentialHeader, object? bodyOverride = null)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Heartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(
                bodyOverride ?? new HeartbeatRequest("HB-PC", "1.0.1", "Windows 11 Test", DateTimeOffset.UtcNow)),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());

        if (credentialHeader is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credentialHeader);
        }

        return message;
    }

    [Fact]
    public async Task An_authenticated_heartbeat_succeeds_and_updates_last_seen()
    {
        var (deviceId, credential) = await EnrollDeviceAsync();
        using var client = _fixture.Factory.CreateClient();

        DateTimeOffset? before;
        await using (var dbContext = _fixture.CreateDbContext())
        {
            before = (await dbContext.Devices.SingleAsync(d => d.Id == deviceId)).LastSeenAt;
        }

        await Task.Delay(50);
        var response = await client.SendAsync(MakeHeartbeat(credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>();
        body.ShouldNotBeNull();
        body.HeartbeatIntervalSeconds.ShouldBeGreaterThanOrEqualTo(15);

        await using (var dbContext = _fixture.CreateDbContext())
        {
            var device = await dbContext.Devices.SingleAsync(d => d.Id == deviceId);
            device.LastSeenAt.ShouldNotBeNull();
            device.LastSeenAt.Value.ShouldBeGreaterThan(before!.Value);
            device.AgentVersion.ShouldBe("1.0.1", "heartbeat facts must update the device");
        }
    }

    /// <summary>
    /// The boot time is taken as an uptime -- the agent's two readings of its
    /// own clock -- and placed on the server's clock, so a device whose clock
    /// is hours wrong still reports when it booted correctly.
    /// </summary>
    [Fact]
    public async Task A_heartbeat_with_a_boot_time_records_it_on_the_servers_clock_even_from_a_skewed_device()
    {
        var (deviceId, credential) = await EnrollDeviceAsync();
        using var client = _fixture.Factory.CreateClient();

        var deviceClock = DateTimeOffset.UtcNow.AddHours(3);
        var before = DateTimeOffset.UtcNow;
        var response = await client.SendAsync(MakeHeartbeat(credential,
            new HeartbeatRequest("HB-PC", "1.15.0", "Windows 11 Test", deviceClock, deviceClock.AddMinutes(-10))));
        var after = DateTimeOffset.UtcNow;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var dbContext = _fixture.CreateDbContext();
        var device = await dbContext.Devices.SingleAsync(d => d.Id == deviceId);
        device.LastBootAt.ShouldNotBeNull();
        device.LastBootAt.Value.ShouldBeGreaterThanOrEqualTo(before.AddMinutes(-10).AddSeconds(-1));
        device.LastBootAt.Value.ShouldBeLessThanOrEqualTo(after.AddMinutes(-10).AddSeconds(1));
    }

    [Fact]
    public async Task An_unauthenticated_heartbeat_is_rejected()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(MakeHeartbeat(credentialHeader: null));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("aaaa.bbbb")]
    [InlineData("00000000000000000000000000000000.0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_forged_or_malformed_credential_is_rejected(string credentialHeader)
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(MakeHeartbeat(credentialHeader));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_key_id_with_the_wrong_secret_is_rejected()
    {
        var (_, credential) = await EnrollDeviceAsync();
        var keyId = credential.Split('.')[0];
        var wrongSecret = SecretGenerator.GenerateSecret();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(MakeHeartbeat($"{keyId}.{wrongSecret}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_malformed_heartbeat_body_is_rejected_without_touching_the_device()
    {
        var (deviceId, credential) = await EnrollDeviceAsync();
        using var client = _fixture.Factory.CreateClient();

        DateTimeOffset? before;
        await using (var dbContext = _fixture.CreateDbContext())
        {
            before = (await dbContext.Devices.SingleAsync(d => d.Id == deviceId)).LastSeenAt;
        }

        // Hostname empty and agent version over-long.
        var response = await client.SendAsync(MakeHeartbeat(credential, new
        {
            Hostname = "",
            AgentVersion = new string('x', 100),
            OperatingSystem = (string?)null,
            AgentTimestamp = DateTimeOffset.UtcNow,
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using (var dbContext = _fixture.CreateDbContext())
        {
            (await dbContext.Devices.SingleAsync(d => d.Id == deviceId)).LastSeenAt
                .ShouldBe(before, "a rejected heartbeat must not update last_seen");
        }
    }

    [Fact]
    public async Task A_retired_device_cannot_heartbeat()
    {
        var (deviceId, credential) = await EnrollDeviceAsync();

        await using (var dbContext = _fixture.CreateDbContext())
        {
            var device = await dbContext.Devices.SingleAsync(d => d.Id == deviceId);
            device.Retire();
            await dbContext.SaveChangesAsync();
        }

        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(MakeHeartbeat(credential));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a retired device's credential must stop working even before explicit revocation");
    }
}
