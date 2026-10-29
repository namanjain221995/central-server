using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The heartbeat asks for a security-update scan whenever the device holds a
/// question only a scan can answer -- above all, the restart a cumulative update was
/// waiting for -- level-triggered and bounded exactly like the driver probe, and in
/// its own try/catch: one probe failing never skips the other, and neither ever
/// fails the heartbeat.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class HeartbeatSecurityRescanTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static readonly Guid Requester = Guid.CreateVersion7();
    private const string RequesterDisplay = "operator@test";
    private const string AgentVersion = "1.27.0";
    private const string SecurityUpdateId = "5e000006-0000-0000-0000-000000000001";
    private const string DriverUpdateId = "5e000006-0000-0000-0000-000000000002";

    private async Task<(Guid DeviceId, Guid OrgId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(
            org.Id, $"hsr-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var enroll = Req(AgentProtocol.Routes.Enroll, new EnrollRequest(
            secret, "HSR-PC", $"machine-{Guid.CreateVersion7():N}", AgentVersion, null));
        var response = await client.SendAsync(enroll);
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, org.Id, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage Req(string route, object body, string? credential = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        }

        return message;
    }

    private async Task SetBootAsync(Guid deviceId, DateTimeOffset bootedAt)
    {
        await using var db = _fixture.CreateDbContext();
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBootAt, bootedAt));
    }

    /// <summary>A security install attempted two hours ago that is waiting for a restart.</summary>
    private async Task AddRebootPendingSecurityInstallAsync(Guid deviceId, Guid orgId)
    {
        var attempted = DateTimeOffset.UtcNow.AddHours(-2);
        await using var db = _fixture.CreateDbContext();
        var install = DeviceTask.Create(orgId, deviceId, DeviceTaskType.InstallSecurityUpdate,
            $$"""{"updateIds":["{{SecurityUpdateId}}"]}""", Requester, RequesterDisplay, attempted, TimeSpan.FromHours(2));
        install.TryDeliver(attempted).ShouldBeTrue();
        install.TryComplete(true, "Installed 1 security update(s); a restart is required.", null, attempted.AddMinutes(20)).ShouldBeTrue();
        db.DeviceTasks.Add(install);

        var record = new DeviceSecurityUpdateInstallRecord(deviceId, SecurityUpdateId, 200, "2026-10 Cumulative Update", attempted);
        record.RecordAttempt(SecurityUpdateInstallStatus.PendingReboot, 2, 0, install.Id, null, 200, attempted);
        db.DeviceSecurityUpdateInstallRecords.Add(record);

        await db.SaveChangesAsync();
    }

    /// <summary>A driver install attempted two hours ago that is waiting for a restart.</summary>
    private async Task AddRebootPendingDriverInstallAsync(Guid deviceId, Guid orgId)
    {
        var attempted = DateTimeOffset.UtcNow.AddHours(-2);
        await using var db = _fixture.CreateDbContext();
        var install = DeviceTask.Create(orgId, deviceId, DeviceTaskType.InstallDriverUpdate,
            $$"""{"updateIds":["{{DriverUpdateId}}"]}""", Requester, RequesterDisplay, attempted, TimeSpan.FromHours(1));
        install.TryDeliver(attempted).ShouldBeTrue();
        install.TryComplete(true, "Installed 1 driver update(s); a restart is required.", null, attempted.AddMinutes(3)).ShouldBeTrue();
        db.DeviceTasks.Add(install);

        var record = new DeviceDriverUpdateInstallRecord(deviceId, DriverUpdateId, 1, "Update", attempted);
        record.RecordAttempt(DriverUpdateInstallStatus.PendingReboot, 2, 0, install.Id, null, 1, attempted);
        db.DeviceDriverUpdateInstallRecords.Add(record);

        await db.SaveChangesAsync();
    }

    private async Task<HeartbeatResponse> HeartbeatAsync(string credential, TimeSpan uptime, HttpClient? client = null)
    {
        using var owned = client is null ? _fixture.Factory.CreateClient() : null;
        var now = DateTimeOffset.UtcNow;
        var response = await (client ?? owned!).SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("HSR-PC", AgentVersion, null, now, now - uptime), credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the heartbeat itself succeeded and was saved");
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>())!;
    }

    private async Task<List<DeviceTask>> ScansAsync(Guid deviceId, DeviceTaskType type)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == type)
            .ToListAsync();
    }

    [Fact]
    public async Task A_restart_with_reboot_pending_security_installs_queues_exactly_one_security_scan_for_the_requester()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SetBootAsync(deviceId, DateTimeOffset.UtcNow.AddHours(-5));
        await AddRebootPendingSecurityInstallAsync(deviceId, orgId);

        // Up for one minute: the machine restarted since the stored boot time.
        var first = await HeartbeatAsync(credential, TimeSpan.FromMinutes(1));

        var scan = (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldHaveSingleItem();
        scan.Status.ShouldBe(DeviceTaskStatus.Queued);
        scan.CreatedByUserId.ShouldBe(Requester);
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);
        first.TasksPending.ShouldBeTrue("queued before the pending check, so this very tick picks it up");
        (await ScansAsync(deviceId, DeviceTaskType.ScanDriverUpdates)).ShouldBeEmpty("no driver install is waiting");

        await HeartbeatAsync(credential, TimeSpan.FromMinutes(2));
        await HeartbeatAsync(credential, TimeSpan.FromMinutes(3));

        (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).Count.ShouldBe(1);
    }

    // Level-triggered: the heartbeat that carried the restart has come and gone.
    [Fact]
    public async Task A_restart_the_server_already_knows_about_still_gets_its_security_scan_on_the_next_heartbeat()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        var bootedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SetBootAsync(deviceId, bootedAt);
        await AddRebootPendingSecurityInstallAsync(deviceId, orgId);

        var response = await HeartbeatAsync(credential, DateTimeOffset.UtcNow - bootedAt);

        (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldHaveSingleItem().Status.ShouldBe(DeviceTaskStatus.Queued);
        response.TasksPending.ShouldBeTrue();
    }

    [Fact]
    public async Task A_device_that_has_not_restarted_since_its_security_install_is_not_asked()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        var bootedAt = DateTimeOffset.UtcNow.AddHours(-5);
        await SetBootAsync(deviceId, bootedAt);
        await AddRebootPendingSecurityInstallAsync(deviceId, orgId);

        await HeartbeatAsync(credential, DateTimeOffset.UtcNow - bootedAt);

        (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldBeEmpty("the restart is still owed");
    }

    [Fact]
    public async Task A_device_back_from_a_silent_security_install_gets_one_verification_scan_on_reconnect()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SetBootAsync(deviceId, DateTimeOffset.UtcNow.AddHours(-5));

        await using (var db = _fixture.CreateDbContext())
        {
            var deliveredAt = DateTimeOffset.UtcNow.AddHours(-4);
            var install = DeviceTask.Create(orgId, deviceId, DeviceTaskType.InstallSecurityUpdate,
                $$"""{"updateIds":["{{SecurityUpdateId}}"]}""", Requester, RequesterDisplay,
                deliveredAt.AddMinutes(-1), TimeSpan.FromHours(2));
            install.TryDeliver(deliveredAt).ShouldBeTrue();
            install.TryExpire(deliveredAt.AddHours(3)).ShouldBeTrue();
            db.DeviceTasks.Add(install);
            await db.SaveChangesAsync();
        }

        var response = await HeartbeatAsync(credential, TimeSpan.FromHours(5));
        await HeartbeatAsync(credential, TimeSpan.FromHours(5));

        var fresh = (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldHaveSingleItem("one scan, not one per heartbeat");
        fresh.CreatedByUserId.ShouldBe(Requester);
        response.TasksPending.ShouldBeTrue();
    }

    [Fact]
    public async Task Open_driver_and_security_questions_each_get_their_own_scan_on_the_same_heartbeat()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SetBootAsync(deviceId, DateTimeOffset.UtcNow.AddHours(-5));
        await AddRebootPendingSecurityInstallAsync(deviceId, orgId);
        await AddRebootPendingDriverInstallAsync(deviceId, orgId);

        await HeartbeatAsync(credential, TimeSpan.FromMinutes(1));

        (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldHaveSingleItem();
        (await ScansAsync(deviceId, DeviceTaskType.ScanDriverUpdates)).ShouldHaveSingleItem();
    }

    /// <summary>Fails every database command that touches the named install-memory table.</summary>
    private sealed class FailQueriesTouching(string table) : DbCommandInterceptor
    {
        private void ThrowIfProbe(DbCommand command)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Simulated database failure in the {table} probe.");
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfProbe(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfProbe(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfProbe(command);
            return ValueTask.FromResult(result);
        }
    }

    // The driver probe runs first; its failure must not skip the security probe.
    [Fact]
    public async Task A_failing_driver_probe_does_not_skip_the_security_probe_or_fail_the_heartbeat()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SetBootAsync(deviceId, DateTimeOffset.UtcNow.AddHours(-5));
        await AddRebootPendingSecurityInstallAsync(deviceId, orgId);
        await AddRebootPendingDriverInstallAsync(deviceId, orgId);

        await using (var broken = _fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<EndpointPlatformDbContext>((_, options) =>
                options.AddInterceptors(new FailQueriesTouching("device_driver_update_install_records"))))))
        {
            using var client = broken.CreateClient();
            await HeartbeatAsync(credential, TimeSpan.FromMinutes(1), client);
        }

        (await ScansAsync(deviceId, DeviceTaskType.ScanDriverUpdates)).ShouldBeEmpty("the driver probe failed on that heartbeat");
        (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldHaveSingleItem("the security probe ran regardless")
            .Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    // And the other way round: a failing security probe neither fails the heartbeat
    // nor costs the driver probe, and the next heartbeat retries it.
    [Fact]
    public async Task A_failing_security_probe_never_fails_the_heartbeat_and_the_next_heartbeat_retries_it()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SetBootAsync(deviceId, DateTimeOffset.UtcNow.AddHours(-5));
        await AddRebootPendingSecurityInstallAsync(deviceId, orgId);
        await AddRebootPendingDriverInstallAsync(deviceId, orgId);

        await using (var broken = _fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<EndpointPlatformDbContext>((_, options) =>
                options.AddInterceptors(new FailQueriesTouching("device_security_update_install_records"))))))
        {
            using var client = broken.CreateClient();
            await HeartbeatAsync(credential, TimeSpan.FromMinutes(1), client);
        }

        (await ScansAsync(deviceId, DeviceTaskType.ScanDriverUpdates)).ShouldHaveSingleItem("the driver probe is unaffected");
        (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldBeEmpty("the security probe failed on that heartbeat");

        await HeartbeatAsync(credential, TimeSpan.FromMinutes(2));

        (await ScansAsync(deviceId, DeviceTaskType.ScanSecurityUpdates)).ShouldHaveSingleItem("the next heartbeat asks again")
            .Status.ShouldBe(DeviceTaskStatus.Queued);
    }
}
