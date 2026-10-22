using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Tasks;

file sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A succeeded <c>ScanDriverUpdates</c> result is ingested by
/// <see cref="DeviceTaskService.CompleteAsync"/> into the device's pending
/// driver-update list, replacing it wholesale and trusting nothing in the
/// agent-supplied body.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DriverUpdateScanIngestionTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static AuditWriter Audit(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static async Task<Device> SeedDeviceAsync(EndpointPlatformDbContext db, string tag)
    {
        var org = new Organization(tag, ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, Start);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    /// <summary>Delivers a scan task and posts a result, as the agent poll would.</summary>
    private async Task CompleteScanAsync(Device device, string? resultJson, bool succeeded = true)
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext();
        var fresh = await db.Devices.SingleAsync(d => d.Id == device.Id);

        var task = DeviceTask.Create(
            fresh.OrganizationId, fresh.Id, DeviceTaskType.ScanDriverUpdates, null,
            Guid.CreateVersion7(), "admin", Start, TimeSpan.FromMinutes(30));
        task.TryDeliver(Start).ShouldBeTrue();
        db.DeviceTasks.Add(task);
        await db.SaveChangesAsync();

        var service = new DeviceTaskService(db, Audit(db, time), time, NullLogger<DeviceTaskService>.Instance);
        (await service.CompleteAsync(fresh.Id, task.Id, succeeded, "done", resultJson)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_scan_result_populates_the_pending_list_with_what_was_reported()
    {
        await using var db = _fixture.CreateDbContext();
        var device = await SeedDeviceAsync(db, "DU1");

        var json = """
        {
          "scannedAt": "2026-01-01T12:00:00+00:00",
          "updates": [
            { "updateId": "11111111-1111-1111-1111-111111111111", "revision": 200, "title": "Intel - Display - 31.0.101.4502", "driverClass": "Display", "driverProvider": "Intel", "driverVerDate": "2025-06-01T00:00:00+00:00", "isPreview": false, "sizeBytes": 123456 },
            { "updateId": "22222222-2222-2222-2222-222222222222", "revision": 1, "title": "Realtek - Net - 1.2.3 (preview)", "driverClass": "Net", "driverProvider": "Realtek", "driverVerDate": null, "isPreview": true, "sizeBytes": null }
          ]
        }
        """;

        await CompleteScanAsync(device, json);

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.DeviceDriverUpdates.Where(u => u.DeviceId == device.Id).ToListAsync();
        rows.Count.ShouldBe(2);

        var display = rows.Single(r => r.UpdateId == "11111111-1111-1111-1111-111111111111");
        display.Revision.ShouldBe(200);
        display.Title.ShouldBe("Intel - Display - 31.0.101.4502");
        display.DriverClass.ShouldBe("Display");
        display.DriverProvider.ShouldBe("Intel");
        display.IsPreview.ShouldBeFalse();
        display.SizeBytes.ShouldBe(123456);
        display.DriverVersionDate.ShouldNotBeNull();

        var net = rows.Single(r => r.UpdateId == "22222222-2222-2222-2222-222222222222");
        net.IsPreview.ShouldBeTrue();
        net.SizeBytes.ShouldBeNull();
        net.DriverVersionDate.ShouldBeNull();
    }

    [Fact]
    public async Task A_later_scan_replaces_the_list_wholesale()
    {
        await using var db = _fixture.CreateDbContext();
        var device = await SeedDeviceAsync(db, "DU2");

        await CompleteScanAsync(device, """
        { "scannedAt": "2026-01-01T12:00:00+00:00", "updates": [
          { "updateId": "aaaaaaaa-0000-0000-0000-000000000001", "revision": 1, "title": "Old A", "isPreview": false },
          { "updateId": "aaaaaaaa-0000-0000-0000-000000000002", "revision": 1, "title": "Old B", "isPreview": false } ] }
        """);

        await CompleteScanAsync(device, """
        { "scannedAt": "2026-01-01T13:00:00+00:00", "updates": [
          { "updateId": "aaaaaaaa-0000-0000-0000-000000000002", "revision": 2, "title": "New B", "isPreview": false } ] }
        """);

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.DeviceDriverUpdates.Where(u => u.DeviceId == device.Id).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].UpdateId.ShouldBe("aaaaaaaa-0000-0000-0000-000000000002");
        rows[0].Revision.ShouldBe(2);
        rows[0].Title.ShouldBe("New B");
    }

    [Fact]
    public async Task An_empty_scan_clears_the_list()
    {
        await using var db = _fixture.CreateDbContext();
        var device = await SeedDeviceAsync(db, "DU3");

        await CompleteScanAsync(device, """
        { "scannedAt": "2026-01-01T12:00:00+00:00", "updates": [
          { "updateId": "bbbbbbbb-0000-0000-0000-000000000001", "revision": 1, "title": "Something", "isPreview": false } ] }
        """);

        await CompleteScanAsync(device, """{ "scannedAt": "2026-01-01T13:00:00+00:00", "updates": [] }""");

        await using var verify = _fixture.CreateDbContext();
        (await verify.DeviceDriverUpdates.CountAsync(u => u.DeviceId == device.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Items_with_no_update_id_or_title_are_dropped_and_duplicates_keep_the_first()
    {
        await using var db = _fixture.CreateDbContext();
        var device = await SeedDeviceAsync(db, "DU4");

        await CompleteScanAsync(device, """
        { "scannedAt": "2026-01-01T12:00:00+00:00", "updates": [
          { "updateId": "cccccccc-0000-0000-0000-000000000001", "revision": 1, "title": "Keep me", "isPreview": false },
          { "updateId": "cccccccc-0000-0000-0000-000000000001", "revision": 9, "title": "Duplicate id", "isPreview": false },
          { "updateId": "", "revision": 1, "title": "No id", "isPreview": false },
          { "updateId": "cccccccc-0000-0000-0000-000000000002", "revision": 1, "title": null, "isPreview": false } ] }
        """);

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.DeviceDriverUpdates.Where(u => u.DeviceId == device.Id).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].UpdateId.ShouldBe("cccccccc-0000-0000-0000-000000000001");
        rows[0].Title.ShouldBe("Keep me");
    }

    [Fact]
    public async Task A_failed_scan_leaves_the_previous_list_untouched()
    {
        await using var db = _fixture.CreateDbContext();
        var device = await SeedDeviceAsync(db, "DU5");

        await CompleteScanAsync(device, """
        { "scannedAt": "2026-01-01T12:00:00+00:00", "updates": [
          { "updateId": "dddddddd-0000-0000-0000-000000000001", "revision": 1, "title": "Still here", "isPreview": false } ] }
        """);

        // A scan that failed to run reports Succeeded=false with a null body. The
        // pending list must not be cleared on a failure.
        await CompleteScanAsync(device, resultJson: null, succeeded: false);

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.DeviceDriverUpdates.Where(u => u.DeviceId == device.Id).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].UpdateId.ShouldBe("dddddddd-0000-0000-0000-000000000001");
    }
}
