using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.WorkTime;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using EndpointPlatform.Infrastructure.WorkTime;

namespace EndpointPlatform.Infrastructure.Tests.WorkTime;

/// <summary>
/// The work-time report: a night shift's early-morning minutes are attributed to the
/// shift's start date, minutes are classified active/idle by the threshold, and only
/// in-shift samples count.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkTimeReportServiceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(Guid OrgId, Device Device)> SeedDeviceAsync(EndpointPlatformDbContext db, string tag)
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
        return (org.Id, device);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static DeviceActivitySample Sample(Guid deviceId, string user, DateTimeOffset at, int idleSeconds) =>
        new(deviceId, user, at, idleSeconds);

    [Fact]
    public async Task Night_shift_minutes_are_attributed_to_the_start_date_and_classified_by_idle()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "WT1");

        // Default shift: 19:30–04:30 IST (UTC+5:30), 5-minute idle threshold.
        // Shift of 2026-01-01 in UTC is 14:00 → 23:00 that same UTC day.
        db.DeviceActivitySamples.AddRange(
            Sample(device.Id, "D\\paras", Utc(2026, 1, 1, 14, 5), 10),   // local 19:35 Jan 1 — active
            Sample(device.Id, "D\\paras", Utc(2026, 1, 1, 20, 0), 10),   // local 01:30 Jan 2 → shift Jan 1 — active
            Sample(device.Id, "D\\paras", Utc(2026, 1, 1, 20, 5), 400),  // local 01:35 Jan 2 → shift Jan 1 — idle
            Sample(device.Id, "D\\paras", Utc(2026, 1, 1, 10, 0), 10));  // local 15:30 Jan 1 — before the shift
        await db.SaveChangesAsync();

        var report = await new WorkTimeReportService(db).GetReportAsync(
            orgId, scopedDeviceIds: null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), groupId: null);

        var row = report.Rows.ShouldHaveSingleItem();
        row.ShiftDate.ShouldBe(new DateOnly(2026, 1, 1));
        row.UserName.ShouldBe("D\\paras");
        row.PresentMinutes.ShouldBe(3); // the three in-shift minutes; the 15:30 one is excluded
        row.ActiveMinutes.ShouldBe(2);  // two under the 5-minute idle threshold
        row.IdleMinutes.ShouldBe(1);    // the 400-second one
        report.Settings.IdleThresholdMinutes.ShouldBe(5);
        report.Settings.ShiftLengthMinutes.ShouldBe(9 * 60);
        report.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_lowered_idle_threshold_reclassifies_the_same_samples()
    {
        await using var db = _fixture.CreateDbContext();
        var (orgId, device) = await SeedDeviceAsync(db, "WT2");

        // Persist a 2-minute threshold for this org.
        var setting = new WorkTimeSetting(orgId);
        setting.Update(enabled: true, shiftStartMinutes: (19 * 60) + 30, shiftEndMinutes: (4 * 60) + 30,
            idleThresholdMinutes: 2, breakMinutes: 60, utcOffsetMinutes: 330);
        db.WorkTimeSettings.Add(setting);

        db.DeviceActivitySamples.AddRange(
            Sample(device.Id, "D\\greg", Utc(2026, 1, 1, 14, 5), 90),    // 1.5 min idle — active at 2-min threshold
            Sample(device.Id, "D\\greg", Utc(2026, 1, 1, 14, 6), 150));  // 2.5 min idle — idle at 2-min threshold
        await db.SaveChangesAsync();

        var report = await new WorkTimeReportService(db).GetReportAsync(
            orgId, scopedDeviceIds: null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), groupId: null);

        var row = report.Rows.ShouldHaveSingleItem();
        row.PresentMinutes.ShouldBe(2);
        row.ActiveMinutes.ShouldBe(1);
        row.IdleMinutes.ShouldBe(1);
        report.Settings.IdleThresholdMinutes.ShouldBe(2);
    }
}
