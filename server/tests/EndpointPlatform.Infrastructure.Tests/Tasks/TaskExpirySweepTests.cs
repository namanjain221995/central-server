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
/// Phase 15: the background sweep expires tasks whose deadline passed while the
/// device was offline, without touching still-valid or already-terminal tasks.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TaskExpirySweepTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static AuditWriter Audit(Infrastructure.Persistence.EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    [Fact]
    public async Task The_sweep_expires_only_overdue_non_terminal_tasks()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new TestClock(start);

        await using var db = _fixture.CreateDbContext();
        var org = new Organization("S", ("s" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "SW", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, start);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        // An overdue queued task and a still-valid one.
        var overdue = DeviceTask.Create(org.Id, device.Id, DeviceTaskType.Ping, null, Guid.CreateVersion7(), "admin", start, TimeSpan.FromMinutes(5));
        var fresh = DeviceTask.Create(org.Id, device.Id, DeviceTaskType.Ping, null, Guid.CreateVersion7(), "admin", start, TimeSpan.FromHours(2));
        db.DeviceTasks.AddRange(overdue, fresh);
        await db.SaveChangesAsync();

        // Advance past the overdue task's 5-minute deadline but not the 2-hour one.
        time.Advance(TimeSpan.FromMinutes(10));

        var service = new DeviceTaskService(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);
        var expired = await service.SweepExpiredAsync(batchSize: 500);

        expired.ShouldBe(1);

        await using var verify = _fixture.CreateDbContext();
        (await verify.DeviceTasks.SingleAsync(t => t.Id == overdue.Id)).Status.ShouldBe(DeviceTaskStatus.Expired);
        (await verify.DeviceTasks.SingleAsync(t => t.Id == fresh.Id)).Status.ShouldBe(DeviceTaskStatus.Queued);
    }

    [Fact]
    public async Task A_delivered_task_whose_agent_never_reports_back_is_expired_too()
    {
        // The "agent restarts while holding a task" case: the task was claimed,
        // the agent died before executing or reporting, and nothing will ever
        // post a result. Without this path the task would sit Delivered forever,
        // which reads in the dashboard as "still running on the machine".
        var start = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new TestClock(start);

        await using var db = _fixture.CreateDbContext();
        var org = new Organization("S3", ("u" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "SW3", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, start);
        db.Devices.Add(device);

        var task = DeviceTask.Create(org.Id, device.Id, DeviceTaskType.RestartDevice, null,
            Guid.CreateVersion7(), "admin", start, TimeSpan.FromMinutes(15));
        task.TryDeliver(start.AddMinutes(1)).ShouldBeTrue();
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();

        time.Advance(TimeSpan.FromMinutes(20));

        var service = new DeviceTaskService(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);
        var expired = await service.SweepExpiredAsync(batchSize: 500);

        expired.ShouldBe(1);

        await using (var verify = _fixture.CreateDbContext())
        {
            var reloaded = await verify.DeviceTasks.SingleAsync(t => t.Id == task.Id);
            reloaded.Status.ShouldBe(DeviceTaskStatus.Expired);
            reloaded.ResultMessage.ShouldBe(DeviceTask.ExpiredUnreportedMessage + ".",
                "the device had it, so the expiry must not claim it was never picked up");
        }

        // A restart result that limps in afterwards must not resurrect it: only a
        // driver install takes a late result (see the driver-install variant below).
        (await service.CompleteAsync(device.Id, task.Id, succeeded: true, "late", null)).ShouldBeFalse();

        await using (var verify = _fixture.CreateDbContext())
        {
            var still = await verify.DeviceTasks.SingleAsync(t => t.Id == task.Id);
            still.Status.ShouldBe(DeviceTaskStatus.Expired);
        }
    }

    [Fact]
    public async Task A_delivered_driver_install_takes_its_late_result_and_cannot_be_rewritten_after()
    {
        var start = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var time = new TestClock(start);

        await using var db = _fixture.CreateDbContext();
        var org = new Organization("S3L", ("u" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "SW3L", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, start);
        db.Devices.Add(device);

        var task = DeviceTask.Create(org.Id, device.Id, DeviceTaskType.InstallDriverUpdate, null,
            Guid.CreateVersion7(), "admin", start, TimeSpan.FromMinutes(15));
        task.TryDeliver(start.AddMinutes(1)).ShouldBeTrue();
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();

        time.Advance(TimeSpan.FromMinutes(20));

        var service = new DeviceTaskService(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);
        await service.SweepExpiredAsync(batchSize: 500);

        // The device provably had the install; its late report is the truth.
        (await service.CompleteAsync(device.Id, task.Id, succeeded: true, "late", null)).ShouldBeTrue();

        await using (var verify = _fixture.CreateDbContext())
        {
            var settled = await verify.DeviceTasks.SingleAsync(t => t.Id == task.Id);
            settled.Status.ShouldBe(DeviceTaskStatus.Succeeded);
            settled.ResultMessage.ShouldBe("late" + DeviceTask.LateResultSuffix);
        }

        // And once settled, a replay cannot rewrite it.
        (await service.CompleteAsync(device.Id, task.Id, succeeded: false, "tampered", null)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_sweep_finds_nothing_to_do()
    {
        var start = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new TestClock(start);

        await using var db = _fixture.CreateDbContext();
        var org = new Organization("S2", ("t" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "SW2", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, start);
        db.Devices.Add(device);
        db.DeviceTasks.Add(DeviceTask.Create(org.Id, device.Id, DeviceTaskType.Ping, null, Guid.CreateVersion7(), "admin", start, TimeSpan.FromMinutes(1)));
        await db.SaveChangesAsync();

        time.Advance(TimeSpan.FromMinutes(5));
        var service = new DeviceTaskService(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);

        (await service.SweepExpiredAsync(500)).ShouldBe(1);
        (await service.SweepExpiredAsync(500)).ShouldBe(0, "an already-expired task must not be swept twice");
    }
}
