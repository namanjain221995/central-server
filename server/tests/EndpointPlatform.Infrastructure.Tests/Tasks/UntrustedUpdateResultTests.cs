using System.Text.Json;
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
    private readonly DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// Windows Update scan and install results -- driver and security alike -- are
/// agent-supplied and untrusted. Two shapes that System.Text.Json and PostgreSQL do
/// not stop on their own must still never fail the completion that carries them:
/// a <c>null</c> element in a list (nullable annotations are not enforced on
/// deserialization) and a NUL character in a string (PostgreSQL text and jsonb
/// cannot store one). Either used to turn the result into a 500, which the agent's
/// spool then re-posted on every heartbeat until it gave up -- and the install's
/// true outcome was lost.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UntrustedUpdateResultTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string A = "0c000000-0000-0000-0000-000000000001";
    private const string B = "0c000000-0000-0000-0000-000000000002";

    private static AuditWriter Audit(EndpointPlatformDbContext db, TimeProvider time) =>
        new(db, time, new CorrelationIdAccessor(), new HttpContextAccessor());

    private async Task<Device> SeedDeviceAsync(string tag)
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization(tag, ("o" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, tag, "m-" + Guid.CreateVersion7().ToString("N"), "1.27.0", null, token.Id, Start);
        db.Devices.Add(device);
        db.DeviceDriverUpdates.Add(new DeviceDriverUpdate(device.Id, A, 1, "Driver A", "Display", "Vendor", null, false, 1, Start));
        db.DeviceSecurityUpdates.Add(new DeviceSecurityUpdate(device.Id, A, 1, "Security A", "KB5044284", "Critical", false, 1, Start));
        await db.SaveChangesAsync();
        return device;
    }

    /// <summary>Delivers a task of <paramref name="type"/> and posts <paramref name="resultJson"/> for it.</summary>
    private async Task<(bool Accepted, Guid TaskId)> CompleteAsync(
        Device device, DeviceTaskType type, string resultJson, string message = "done", bool succeeded = true)
    {
        Guid taskId;
        await using (var db = _fixture.CreateDbContext())
        {
            var task = DeviceTask.Create(device.OrganizationId, device.Id, type,
                type is DeviceTaskType.InstallDriverUpdate or DeviceTaskType.InstallSecurityUpdate
                    ? JsonSerializer.Serialize(new { updateIds = new[] { A, B }, issuedAt = Start }, Web)
                    : null,
                Guid.CreateVersion7(), "operator@test", Start, TimeSpan.FromHours(1));
            task.TryDeliver(Start).ShouldBeTrue();
            db.DeviceTasks.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        var clock = new TestClock(Start.AddMinutes(5));
        await using (var db = _fixture.CreateDbContext())
        {
            var service = new DeviceTaskService(db, Audit(db, clock), clock, NullLogger<DeviceTaskService>.Instance);
            return (await service.CompleteAsync(device.Id, taskId, succeeded, message, resultJson), taskId);
        }
    }

    private async Task<DeviceTask> TaskAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private static object DriverItem(string id, string title) =>
        new { updateId = id, revision = 1, title, driverClass = "Display", driverProvider = "Vendor", isPreview = false, sizeBytes = 1 };

    private static object SecurityItem(string id, string title) =>
        new { updateId = id, revision = 1, title, kbIds = "KB5044284", severity = "Critical", isPreview = false, sizeBytes = 1 };

    // ---- null elements -----------------------------------------------------------

    [Fact]
    public async Task Null_items_in_a_driver_scan_are_skipped_and_the_rest_ingested()
    {
        var device = await SeedDeviceAsync("UR1");

        var (accepted, _) = await CompleteAsync(device, DeviceTaskType.ScanDriverUpdates,
            JsonSerializer.Serialize(new { scannedAt = Start, updates = new object?[] { null, DriverItem(B, "Driver B"), null } }));

        accepted.ShouldBeTrue("a null element must not fail the completion");
        await using var db = _fixture.CreateDbContext();
        (await db.DeviceDriverUpdates.Where(u => u.DeviceId == device.Id).Select(u => u.UpdateId).ToListAsync())
            .ShouldBe(new[] { B });
    }

    [Fact]
    public async Task Null_items_in_a_security_scan_are_skipped_and_the_rest_ingested()
    {
        var device = await SeedDeviceAsync("UR2");

        var (accepted, _) = await CompleteAsync(device, DeviceTaskType.ScanSecurityUpdates,
            JsonSerializer.Serialize(new { scannedAt = Start, updates = new object?[] { null, SecurityItem(B, "Security B"), null } }));

        accepted.ShouldBeTrue("a null element must not fail the completion");
        await using var db = _fixture.CreateDbContext();
        (await db.DeviceSecurityUpdates.Where(u => u.DeviceId == device.Id).Select(u => u.UpdateId).ToListAsync())
            .ShouldBe(new[] { B });
    }

    [Theory]
    [InlineData(DeviceTaskType.InstallDriverUpdate)]
    [InlineData(DeviceTaskType.InstallSecurityUpdate)]
    public async Task Null_items_in_a_legacy_install_result_are_skipped_and_the_rest_remembered(DeviceTaskType type)
    {
        var device = await SeedDeviceAsync($"UR3-{(int)type}");

        var (accepted, taskId) = await CompleteAsync(device, type, JsonSerializer.Serialize(new
        {
            rebootRequired = true,
            installed = new object?[] { null, new { updateId = A, title = "A", resultCode = 2, hresult = 0 } },
            failed = new object?[] { null },
        }));

        accepted.ShouldBeTrue("a null element must not fail the completion");
        (await TaskAsync(taskId)).Status.ShouldBe(DeviceTaskStatus.Succeeded);

        await using var db = _fixture.CreateDbContext();
        if (type == DeviceTaskType.InstallDriverUpdate)
        {
            (await db.DeviceDriverUpdateInstallRecords.SingleAsync(r => r.DeviceId == device.Id)).Status
                .ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        }
        else
        {
            (await db.DeviceSecurityUpdateInstallRecords.SingleAsync(r => r.DeviceId == device.Id)).Status
                .ShouldBe(SecurityUpdateInstallStatus.PendingReboot);
        }
    }

    [Theory]
    [InlineData(DeviceTaskType.InstallDriverUpdate)]
    [InlineData(DeviceTaskType.InstallSecurityUpdate)]
    public async Task Null_items_in_classified_outcomes_and_in_the_post_scan_are_skipped(DeviceTaskType type)
    {
        var device = await SeedDeviceAsync($"UR4-{(int)type}");
        var item = type == DeviceTaskType.InstallDriverUpdate ? DriverItem(B, "Update B") : SecurityItem(B, "Update B");

        var (accepted, _) = await CompleteAsync(device, type, JsonSerializer.Serialize(new
        {
            rebootRequired = false,
            installed = Array.Empty<object>(),
            failed = Array.Empty<object>(),
            outcomes = new object?[]
            {
                null,
                new { updateId = A, title = "A", resultCode = 2, hresult = 0, rebootRequired = false, status = "Installed" },
            },
            verified = true,
            postScan = new { scannedAt = Start, updates = new object?[] { null, item } },
        }));

        accepted.ShouldBeTrue("a null element must not fail the completion");

        await using var db = _fixture.CreateDbContext();
        if (type == DeviceTaskType.InstallDriverUpdate)
        {
            var record = await db.DeviceDriverUpdateInstallRecords.SingleAsync(r => r.DeviceId == device.Id);
            record.UpdateId.ShouldBe(A);
            record.Status.ShouldBe(DriverUpdateInstallStatus.Installed, "gone from the post-scan: confirmed");
            (await db.DeviceDriverUpdates.Where(u => u.DeviceId == device.Id).Select(u => u.UpdateId).ToListAsync())
                .ShouldBe(new[] { B });
        }
        else
        {
            var record = await db.DeviceSecurityUpdateInstallRecords.SingleAsync(r => r.DeviceId == device.Id);
            record.UpdateId.ShouldBe(A);
            record.Status.ShouldBe(SecurityUpdateInstallStatus.Installed, "gone from the post-scan: confirmed");
            (await db.DeviceSecurityUpdates.Where(u => u.DeviceId == device.Id).Select(u => u.UpdateId).ToListAsync())
                .ShouldBe(new[] { B });
        }
    }

    // ---- NUL characters ----------------------------------------------------------

    [Fact]
    public async Task Nul_characters_in_a_driver_scan_are_stripped_before_storage()
    {
        var device = await SeedDeviceAsync("UR5");

        // System.Text.Json writes the NUL as the \u0000 escape -- what an agent sends.
        var json = JsonSerializer.Serialize(new
        {
            scannedAt = Start,
            updates = new[]
            {
                new { updateId = B + "\0", revision = 1, title = "Vendor\0 - Display", driverClass = "Disp\0lay", driverProvider = "\0", isPreview = false, sizeBytes = 1 },
            },
        });
        json.ShouldContain("\\u0000");

        var (accepted, taskId) = await CompleteAsync(device, DeviceTaskType.ScanDriverUpdates, json);

        accepted.ShouldBeTrue("a NUL character must not fail the completion");
        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceDriverUpdates.SingleAsync(u => u.DeviceId == device.Id);
        row.UpdateId.ShouldBe(B);
        row.Title.ShouldBe("Vendor - Display");
        row.DriverClass.ShouldBe("Display");
        row.DriverProvider.ShouldBeNull("nothing left once the NUL is gone");
        (await TaskAsync(taskId)).ResultJson!.ShouldNotContain("\\u0000");
    }

    [Fact]
    public async Task Nul_characters_in_a_security_scan_are_stripped_before_storage()
    {
        var device = await SeedDeviceAsync("UR6");

        var json = JsonSerializer.Serialize(new
        {
            scannedAt = Start,
            updates = new[]
            {
                new { updateId = B + "\0", revision = 1, title = "2026-10\0 Cumulative Update", kbIds = "KB\05044284", severity = "Crit\0ical", isPreview = false, sizeBytes = 1 },
            },
        });

        var (accepted, _) = await CompleteAsync(device, DeviceTaskType.ScanSecurityUpdates, json);

        accepted.ShouldBeTrue("a NUL character must not fail the completion");
        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceSecurityUpdates.SingleAsync(u => u.DeviceId == device.Id);
        row.UpdateId.ShouldBe(B);
        row.Title.ShouldBe("2026-10 Cumulative Update");
        row.KbIds.ShouldBe("KB5044284");
        row.Severity.ShouldBe("Critical");
    }

    [Theory]
    [InlineData(DeviceTaskType.InstallDriverUpdate)]
    [InlineData(DeviceTaskType.InstallSecurityUpdate)]
    public async Task Nul_characters_in_an_install_result_and_its_message_are_stripped_before_storage(DeviceTaskType type)
    {
        var device = await SeedDeviceAsync($"UR7-{(int)type}");

        var json = JsonSerializer.Serialize(new
        {
            rebootRequired = false,
            installed = Array.Empty<object>(),
            failed = new[] { new { updateId = B, title = "B\0", resultCode = 4, hresult = -2145124329 } },
            outcomes = new object[]
            {
                new { updateId = A + "\0", title = "A\0", resultCode = 2, hresult = 0, rebootRequired = false, status = "InstalledUnverified" },
                // Nothing pending for B, so the record takes the agent's title.
                new { updateId = B, title = "B\0 title", resultCode = 4, hresult = -2145124329, rebootRequired = false, status = "Failed" },
            },
            verified = false,
            postScan = (object?)null,
        });

        var (accepted, taskId) = await CompleteAsync(device, type, json, message: "Installed 1\0 update(s); 1 failed", succeeded: true);

        accepted.ShouldBeTrue("a NUL character must not fail the completion");
        var task = await TaskAsync(taskId);
        task.Status.ShouldBe(DeviceTaskStatus.Succeeded);
        task.ResultMessage.ShouldBe("Installed 1 update(s); 1 failed");
        task.ResultJson!.ShouldNotContain("\\u0000");

        await using var db = _fixture.CreateDbContext();
        if (type == DeviceTaskType.InstallDriverUpdate)
        {
            var records = await db.DeviceDriverUpdateInstallRecords.Where(r => r.DeviceId == device.Id).ToDictionaryAsync(r => r.UpdateId);
            records[A].Status.ShouldBe(DriverUpdateInstallStatus.InstalledUnverified);
            records[B].Title.ShouldBe("B title");
            records[B].Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        }
        else
        {
            var records = await db.DeviceSecurityUpdateInstallRecords.Where(r => r.DeviceId == device.Id).ToDictionaryAsync(r => r.UpdateId);
            records[A].Status.ShouldBe(SecurityUpdateInstallStatus.InstalledUnverified);
            records[B].Title.ShouldBe("B title");
            records[B].Status.ShouldBe(SecurityUpdateInstallStatus.Failed);
        }
    }
}
