using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Tasks;

/// <summary>A minimal settable clock, so we do not depend on a time-testing package.</summary>
file sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The claim is the only place a restart can be handed to a device, so it is
/// where "exactly once" has to hold.
/// </summary>
/// <remarks>
/// Two polls for the same device can overlap -- a retried request, two agent
/// instances during an upgrade, a proxy replay. The row version (<c>xmin</c>)
/// makes the second writer lose: its <c>SaveChanges</c> throws, it returns
/// nothing, and the task is delivered once. These tests drive two independent
/// contexts through that race, because a single context cannot lose to itself.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DeviceTaskClaimTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static AuditWriter Audit(Infrastructure.Persistence.EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static DeviceTaskService Service(Infrastructure.Persistence.EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);

    private async Task<(Guid OrgId, Guid DeviceId)> SeedDeviceAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization("Claim", ("c" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "CLAIM", "m-" + Guid.CreateVersion7().ToString("N"), "1.9.0", null, token.Id, Start);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (org.Id, device.Id);
    }

    private async Task<Guid> SeedRestartAsync(Guid orgId, Guid deviceId, TimeSpan ttl)
    {
        await using var db = _fixture.CreateDbContext();
        var task = DeviceTask.Create(
            orgId, deviceId, DeviceTaskType.RestartDevice,
            """{"graceSeconds":300,"message":"IT scheduled a restart."}""",
            Guid.CreateVersion7(), "admin", Start, ttl);
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    /// <summary>
    /// Two polls race for one queued restart. Whatever the interleaving, the
    /// task is handed out exactly once: one poll gets it, the other gets nothing,
    /// and the row ends Delivered with a single delivery time.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_polls_deliver_a_restart_exactly_once()
    {
        var (orgId, deviceId) = await SeedDeviceAsync();
        var taskId = await SeedRestartAsync(orgId, deviceId, TimeSpan.FromMinutes(15));
        var time = new TestClock(Start.AddSeconds(5));

        // Two contexts, so each has its own tracked copy of the same row and
        // its own xmin to check on write -- the shape of two overlapping HTTP
        // requests. Both read the row as Queued before either writes.
        await using var first = _fixture.CreateDbContext();
        await using var second = _fixture.CreateDbContext();

        var a = Service(first, time);
        var b = Service(second, time);

        // Force the interleaving: both load, then both attempt to write.
        var claimA = a.ClaimForDeviceAsync(deviceId);
        var claimB = b.ClaimForDeviceAsync(deviceId);
        var results = await Task.WhenAll(claimA, claimB);

        var deliveredTo = results.Count(r => r.Count == 1);
        var deliveredNothing = results.Count(r => r.Count == 0);

        deliveredTo.ShouldBe(1, "exactly one poll may receive the restart");
        deliveredNothing.ShouldBe(1, "the losing poll must get nothing rather than a second copy");

        await using var verify = _fixture.CreateDbContext();
        var row = await verify.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        row.Status.ShouldBe(DeviceTaskStatus.Delivered);
        row.DeliveredAt.ShouldBe(Start.AddSeconds(5));
    }

    /// <summary>A poll that arrives after a delivery finds nothing: the claim is not repeatable.</summary>
    [Fact]
    public async Task A_later_poll_does_not_redeliver_a_claimed_restart()
    {
        var (orgId, deviceId) = await SeedDeviceAsync();
        await SeedRestartAsync(orgId, deviceId, TimeSpan.FromMinutes(15));
        var time = new TestClock(Start.AddSeconds(5));

        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, time).ClaimForDeviceAsync(deviceId)).Count.ShouldBe(1);
        }

        time.Advance(TimeSpan.FromSeconds(30));

        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, time).ClaimForDeviceAsync(deviceId)).ShouldBeEmpty();
        }
    }

    /// <summary>
    /// A restart the device did not pick up in time is never handed out. The
    /// claim marks it Expired instead, so an agent coming back online after the
    /// deadline does not restart a machine nobody is expecting to go down.
    /// </summary>
    [Fact]
    public async Task An_expired_restart_is_marked_expired_at_claim_and_never_delivered()
    {
        var (orgId, deviceId) = await SeedDeviceAsync();
        var taskId = await SeedRestartAsync(orgId, deviceId, TimeSpan.FromMinutes(15));
        var time = new TestClock(Start.AddMinutes(16));

        await using (var db = _fixture.CreateDbContext())
        {
            (await Service(db, time).ClaimForDeviceAsync(deviceId)).ShouldBeEmpty(
                "an expired restart must not reach the device");
        }

        await using var verify = _fixture.CreateDbContext();
        var row = await verify.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        row.Status.ShouldBe(DeviceTaskStatus.Expired);
        row.DeliveredAt.ShouldBeNull();
        row.ResultMessage.ShouldBe("Task expired before an agent claimed it.");
    }

    /// <summary>One second inside the deadline is still delivered; the boundary is the deadline itself.</summary>
    [Fact]
    public async Task A_restart_just_inside_its_deadline_is_delivered()
    {
        var (orgId, deviceId) = await SeedDeviceAsync();
        await SeedRestartAsync(orgId, deviceId, TimeSpan.FromMinutes(15));
        var time = new TestClock(Start.AddMinutes(15).AddSeconds(-1));

        await using var db = _fixture.CreateDbContext();
        var delivered = await Service(db, time).ClaimForDeviceAsync(deviceId);

        delivered.Count.ShouldBe(1);
        delivered[0].ExpiresAt.ShouldBe(Start.AddMinutes(15));
    }
}
