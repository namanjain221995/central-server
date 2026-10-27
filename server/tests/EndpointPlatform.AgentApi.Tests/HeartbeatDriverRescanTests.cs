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
/// The heartbeat asks for a driver-update scan whenever the device holds a question
/// only a scan can answer and nothing has answered it -- a restart its reboot-pending
/// installs were waiting for, or an install that went silent -- level-triggered, so a
/// missed moment is caught on the next heartbeat, and bounded, so a device whose
/// scans keep failing is not asked on every heartbeat. Ordinary boot-time jitter, and
/// restarts of devices with nothing waiting, queue nothing; a failing probe never
/// fails the heartbeat.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class HeartbeatDriverRescanTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static readonly Guid Requester = Guid.CreateVersion7();
    private const string RequesterDisplay = "operator@test";
    private const string AgentVersion = "1.26.0";

    private async Task<(Guid DeviceId, Guid OrgId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(
            org.Id, $"hbr-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var enroll = Req(AgentProtocol.Routes.Enroll, new EnrollRequest(
            secret, "HBR-PC", $"machine-{Guid.CreateVersion7():N}", AgentVersion, null));
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

    /// <summary>
    /// Stores a boot time, and optionally an install waiting for a restart (attempted
    /// two hours ago unless <paramref name="attemptedAt"/> says otherwise).
    /// </summary>
    private async Task SeedAsync(
        Guid deviceId, Guid orgId, DateTimeOffset bootedAt, bool withRebootPendingInstall, DateTimeOffset? attemptedAt = null)
    {
        await using var db = _fixture.CreateDbContext();
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBootAt, bootedAt));

        if (!withRebootPendingInstall)
        {
            return;
        }

        var attempted = attemptedAt ?? DateTimeOffset.UtcNow.AddHours(-2);
        var install = DeviceTask.Create(orgId, deviceId, DeviceTaskType.InstallDriverUpdate,
            """{"updateIds":["eeeeeeee-0000-0000-0000-000000000001"]}""", Requester, RequesterDisplay,
            attempted, TimeSpan.FromHours(1));
        install.TryDeliver(attempted).ShouldBeTrue();
        install.TryComplete(true, "Installed 1 driver update(s); a restart is required.", null, attempted.AddMinutes(3)).ShouldBeTrue();
        db.DeviceTasks.Add(install);

        var record = new DeviceDriverUpdateInstallRecord(deviceId, "eeeeeeee-0000-0000-0000-000000000001", 1, "Update", attempted);
        record.RecordAttempt(DriverUpdateInstallStatus.PendingReboot, 2, 0, install.Id, null, 1, attempted);
        db.DeviceDriverUpdateInstallRecords.Add(record);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds a driver-update scan created at <paramref name="createdAt"/>, optionally
    /// claimed at <paramref name="deliveredAt"/> and settled.
    /// </summary>
    private async Task<Guid> AddScanAsync(
        Guid deviceId, Guid orgId, DateTimeOffset createdAt, DateTimeOffset? deliveredAt = null, bool? succeeded = null)
    {
        await using var db = _fixture.CreateDbContext();
        var scan = DeviceTask.Create(orgId, deviceId, DeviceTaskType.ScanDriverUpdates, null,
            Guid.CreateVersion7(), "someone@test", createdAt, TimeSpan.FromMinutes(30));
        if (deliveredAt is { } claimed)
        {
            scan.TryDeliver(claimed).ShouldBeTrue();
            if (succeeded is { } ok)
            {
                scan.TryComplete(ok, ok ? "Found 0 driver update(s)." : "Windows Update failed.", ok ? """{"updates":[]}""" : null,
                    claimed.AddMinutes(1)).ShouldBeTrue();
            }
        }

        db.DeviceTasks.Add(scan);
        await db.SaveChangesAsync();

        // The audit interceptor stamps "now"; put the row where the scenario has it.
        await db.DeviceTasks.Where(t => t.Id == scan.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, createdAt));
        return scan.Id;
    }

    private async Task<HeartbeatResponse> HeartbeatAsync(string credential, TimeSpan uptime)
    {
        using var client = _fixture.Factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var response = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("HBR-PC", AgentVersion, null, now, now - uptime), credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>())!;
    }

    private async Task<List<DeviceTask>> ScansAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ScanDriverUpdates)
            .ToListAsync();
    }

    [Fact]
    public async Task A_restart_with_reboot_pending_driver_installs_queues_exactly_one_scan_for_the_requester()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: true);

        // Up for one minute: the machine restarted since the stored boot time.
        var first = await HeartbeatAsync(credential, TimeSpan.FromMinutes(1));

        var scan = (await ScansAsync(deviceId)).ShouldHaveSingleItem();
        scan.Status.ShouldBe(DeviceTaskStatus.Queued);
        scan.CreatedByUserId.ShouldBe(Requester);
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);
        first.TasksPending.ShouldBeTrue("queued before the pending check, so this very tick picks it up");

        // The next heartbeats from the same boot find that scan still pending.
        await HeartbeatAsync(credential, TimeSpan.FromMinutes(2));
        await HeartbeatAsync(credential, TimeSpan.FromMinutes(3));

        (await ScansAsync(deviceId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Boot_time_jitter_is_not_a_restart()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        var bootedAt = DateTimeOffset.UtcNow.AddHours(-5);
        await SeedAsync(deviceId, orgId, bootedAt, withRebootPendingInstall: true);

        // The recomputed boot time lands 30 seconds after the stored one: clock
        // noise in "receive time minus uptime", and still long before the attempt.
        await HeartbeatAsync(credential, DateTimeOffset.UtcNow - bootedAt.AddSeconds(30));

        (await ScansAsync(deviceId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_restart_with_nothing_waiting_for_it_queues_nothing()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: false);

        await HeartbeatAsync(credential, TimeSpan.FromMinutes(1));

        (await ScansAsync(deviceId)).ShouldBeEmpty();
    }

    // The probe compares the boot time with the ATTEMPT, not with the previous boot
    // time: a first-reported boot time from before the attempt is no restart since it.
    [Fact]
    public async Task A_first_reported_boot_time_from_before_the_attempt_is_not_a_restart()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: true);
        await using (var db = _fixture.CreateDbContext())
        {
            await db.Devices.Where(d => d.Id == deviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBootAt, (DateTimeOffset?)null));
        }

        // Up for five hours: booted well before the install was attempted two hours ago.
        await HeartbeatAsync(credential, TimeSpan.FromHours(5));

        (await ScansAsync(deviceId)).ShouldBeEmpty("the machine has not restarted since the attempt");
    }

    // F1(b): level-triggered. The heartbeat that carried the restart has come and
    // gone (the stored boot time already is the new one) -- e.g. the reboot-pending
    // result was spooled through the restart and landed afterwards, or the probe
    // failed on that one heartbeat. The next heartbeat still asks.
    [Fact]
    public async Task A_restart_the_server_already_knows_about_still_gets_its_scan_on_the_next_heartbeat()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        var bootedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedAsync(deviceId, orgId, bootedAt, withRebootPendingInstall: true);

        // Same boot as stored: no boot-time jump on this heartbeat at all.
        var response = await HeartbeatAsync(credential, DateTimeOffset.UtcNow - bootedAt);

        var scan = (await ScansAsync(deviceId)).ShouldHaveSingleItem(
            "a reboot-pending install attempted before the current boot, with no scan since, is an open question");
        scan.Status.ShouldBe(DeviceTaskStatus.Queued);
        scan.CreatedByUserId.ShouldBe(Requester);
        scan.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);
        response.TasksPending.ShouldBeTrue();
    }

    // F1(b) bound: at most one follow-up per half hour for the same open question.
    [Fact]
    public async Task A_follow_up_scan_that_keeps_failing_is_asked_again_at_most_once_every_half_hour()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        var bootedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedAsync(deviceId, orgId, bootedAt, withRebootPendingInstall: true);
        var uptime = DateTimeOffset.UtcNow - bootedAt;

        await HeartbeatAsync(credential, uptime);
        var first = (await ScansAsync(deviceId)).ShouldHaveSingleItem();

        // The device runs it and it fails (Windows Update broken).
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = await db.DeviceTasks.SingleAsync(t => t.Id == first.Id);
            scan.TryDeliver(DateTimeOffset.UtcNow).ShouldBeTrue();
            scan.TryComplete(false, "Windows Update failed.", null, DateTimeOffset.UtcNow).ShouldBeTrue();
            await db.SaveChangesAsync();
        }

        await HeartbeatAsync(credential, uptime);
        await HeartbeatAsync(credential, uptime);
        (await ScansAsync(deviceId)).Count.ShouldBe(1, "not re-asked on every heartbeat while its scans fail");

        // Half an hour after it ended (asked for, run and failed, all after the
        // restart), it is asked again -- once. The hold runs from the scan's last
        // activity, so all three moments move back.
        await using (var db = _fixture.CreateDbContext())
        {
            var halfAnHourAgo = DateTimeOffset.UtcNow.AddMinutes(-31);
            await db.DeviceTasks.Where(t => t.Id == first.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.CreatedAt, halfAnHourAgo)
                    .SetProperty(t => t.DeliveredAt, halfAnHourAgo)
                    .SetProperty(t => t.CompletedAt, halfAnHourAgo));
        }

        await HeartbeatAsync(credential, uptime);
        await HeartbeatAsync(credential, uptime);

        var scans = await ScansAsync(deviceId);
        scans.Count.ShouldBe(2);
        scans.Single(s => s.Id != first.Id).Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    [Fact]
    public async Task A_restart_a_scan_has_already_answered_queues_nothing()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        var bootedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedAsync(deviceId, orgId, bootedAt, withRebootPendingInstall: true);
        await AddScanAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddMinutes(-6),
            deliveredAt: DateTimeOffset.UtcNow.AddMinutes(-5), succeeded: true);

        await HeartbeatAsync(credential, DateTimeOffset.UtcNow - bootedAt);

        (await ScansAsync(deviceId)).Count.ShouldBe(1, "a successful scan claimed after the restart already observed it");
    }

    // F3 on the reboot path: a scan claimed before the restart (and killed by it)
    // stays "Delivered" until its lifetime runs out, and must not hold back the
    // scan that can actually see the restart's outcome.
    [Fact]
    public async Task A_scan_the_restart_killed_does_not_hold_back_the_one_after_it()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: true);
        var killed = await AddScanAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddMinutes(-25),
            deliveredAt: DateTimeOffset.UtcNow.AddMinutes(-20));

        // Restarted a minute ago.
        await HeartbeatAsync(credential, TimeSpan.FromMinutes(1));

        var scans = await ScansAsync(deviceId);
        scans.Count.ShouldBe(2);
        scans.Single(s => s.Id != killed).Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    // The bound never delays a restart's follow-up because of a scan from BEFORE the
    // restart (e.g. the install's own auto-rescan minutes earlier): that scan could
    // not have seen it.
    [Fact]
    public async Task A_scan_from_before_the_restart_never_holds_back_the_one_after_it()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: true);
        var before = await AddScanAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddMinutes(-10),
            deliveredAt: DateTimeOffset.UtcNow.AddMinutes(-9), succeeded: true);

        await HeartbeatAsync(credential, TimeSpan.FromMinutes(1));

        var scans = await ScansAsync(deviceId);
        scans.Count.ShouldBe(2);
        scans.Single(s => s.Id != before).Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    /// <summary>A driver install the device claimed and then went silent on, which the sweeper expired.</summary>
    private async Task<Guid> AddSilentInstallAsync(Guid deviceId, Guid orgId, DateTimeOffset deliveredAt)
    {
        await using var db = _fixture.CreateDbContext();
        var install = DeviceTask.Create(orgId, deviceId, DeviceTaskType.InstallDriverUpdate,
            """{"updateIds":["eeeeeeee-0000-0000-0000-000000000002"]}""", Requester, RequesterDisplay,
            deliveredAt.AddMinutes(-1), TimeSpan.FromHours(1));
        install.TryDeliver(deliveredAt).ShouldBeTrue();
        install.TryExpire(deliveredAt.AddHours(1)).ShouldBeTrue();
        db.DeviceTasks.Add(install);
        await db.SaveChangesAsync();
        return install.Id;
    }

    // F5(b): the scan the expiry queued often expires unused while the device is
    // offline longer than a scan's half-hour lifetime; on reconnect it gets one.
    [Fact]
    public async Task A_device_back_from_a_silent_install_gets_one_verification_scan_on_reconnect()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: false);
        await AddSilentInstallAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-3));

        // The expiry's own scan, queued two hours ago, expired with nobody to claim it.
        var expiredScan = await AddScanAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-2));
        await using (var db = _fixture.CreateDbContext())
        {
            var scan = await db.DeviceTasks.SingleAsync(t => t.Id == expiredScan);
            scan.TryExpire(DateTimeOffset.UtcNow.AddHours(-1)).ShouldBeTrue();
            await db.SaveChangesAsync();
        }

        var response = await HeartbeatAsync(credential, TimeSpan.FromHours(5));
        await HeartbeatAsync(credential, TimeSpan.FromHours(5));

        var scans = await ScansAsync(deviceId);
        scans.Count.ShouldBe(2, "one fresh scan on reconnect, not one per heartbeat");
        var fresh = scans.Single(s => s.Id != expiredScan);
        fresh.Status.ShouldBe(DeviceTaskStatus.Queued);
        fresh.CreatedByUserId.ShouldBe(Requester);
        fresh.CreatedByDisplay.ShouldBe(RequesterDisplay + DeviceTaskService.AutoRescanSuffix);
        response.TasksPending.ShouldBeTrue();
    }

    [Fact]
    public async Task A_silent_install_a_later_scan_already_answered_queues_nothing()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: false);
        await AddSilentInstallAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-3));
        await AddScanAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-1),
            deliveredAt: DateTimeOffset.UtcNow.AddMinutes(-59), succeeded: true);

        await HeartbeatAsync(credential, TimeSpan.FromHours(5));

        (await ScansAsync(deviceId)).Count.ShouldBe(1);
    }

    /// <summary>Fails every database command that touches the install memory -- the follow-up probe's table.</summary>
    private sealed class FailInstallMemoryQueries : DbCommandInterceptor
    {
        private static void ThrowIfProbe(DbCommand command)
        {
            if (command.CommandText.Contains("device_driver_update_install_records", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated database failure in the follow-up probe.");
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

    // F7: the probe runs after the heartbeat is saved; its failure must not turn a
    // successful heartbeat into a 500 -- and, level-triggered, the next heartbeat
    // asks again even though the restart itself is already recorded.
    [Fact]
    public async Task A_failing_follow_up_probe_never_fails_the_heartbeat_and_the_next_heartbeat_retries_it()
    {
        var (deviceId, orgId, credential) = await EnrollAsync();
        await SeedAsync(deviceId, orgId, DateTimeOffset.UtcNow.AddHours(-5), withRebootPendingInstall: true);

        await using (var broken = _fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<EndpointPlatformDbContext>((_, options) =>
                options.AddInterceptors(new FailInstallMemoryQueries())))))
        {
            using var client = broken.CreateClient();
            var now = DateTimeOffset.UtcNow;
            var response = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
                new HeartbeatRequest("HBR-PC", AgentVersion, null, now, now - TimeSpan.FromMinutes(1)), credential));

            response.StatusCode.ShouldBe(HttpStatusCode.OK, "the heartbeat itself succeeded and was saved");
        }

        (await ScansAsync(deviceId)).ShouldBeEmpty("the probe failed on that heartbeat");
        await using (var db = _fixture.CreateDbContext())
        {
            (await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId)).LastBootAt!.Value
                .ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5), "the restart is recorded: no boot-time jump will come again");
        }

        await HeartbeatAsync(credential, TimeSpan.FromMinutes(2));

        (await ScansAsync(deviceId)).ShouldHaveSingleItem("the next heartbeat asks again").Status.ShouldBe(DeviceTaskStatus.Queued);
    }
}
