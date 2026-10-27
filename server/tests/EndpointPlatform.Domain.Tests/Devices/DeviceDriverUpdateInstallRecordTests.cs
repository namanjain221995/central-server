using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// The memory of a driver-update install: every attempt counts, a new attempt
/// re-opens the question of whether it stuck, and the first confirmation is kept.
/// </summary>
public sealed class DeviceDriverUpdateInstallRecordTests
{
    private static readonly Guid Device = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Update = "11111111-1111-1111-1111-111111111111";

    private static DeviceDriverUpdateInstallRecord First(DriverUpdateInstallStatus status)
    {
        var record = new DeviceDriverUpdateInstallRecord(Device, Update, 3, "Vendor - Display - 1.0", Now);
        record.RecordAttempt(status, 2, 0, Guid.CreateVersion7(), null, 3, Now);
        return record;
    }

    [Fact]
    public void A_first_attempt_is_counted_once()
    {
        var record = First(DriverUpdateInstallStatus.PendingReboot);

        record.Attempts.ShouldBe(1);
        record.Status.ShouldBe(DriverUpdateInstallStatus.PendingReboot);
        record.FirstAttemptAt.ShouldBe(Now);
        record.LastAttemptAt.ShouldBe(Now);
        record.LastResultCode.ShouldBe(2);
        record.Revision.ShouldBe(3);
    }

    [Fact]
    public void Another_attempt_counts_and_clears_an_earlier_confirmation()
    {
        var record = First(DriverUpdateInstallStatus.InstalledUnverified);
        record.ConfirmInstalled(Now.AddMinutes(5));
        record.ConfirmedAt.ShouldBe(Now.AddMinutes(5));

        var task = Guid.CreateVersion7();
        record.RecordAttempt(DriverUpdateInstallStatus.Failed, 4, unchecked((int)0x80240017), task, "Renamed", 4, Now.AddDays(1));

        record.Attempts.ShouldBe(2);
        record.Status.ShouldBe(DriverUpdateInstallStatus.Failed);
        record.ConfirmedAt.ShouldBeNull();
        record.LastTaskId.ShouldBe(task);
        record.LastHresult.ShouldBe(unchecked((int)0x80240017));
        record.Title.ShouldBe("Renamed");
        record.Revision.ShouldBe(4);
        record.FirstAttemptAt.ShouldBe(Now, "the first attempt is history, not overwritten");
    }

    [Fact]
    public void The_first_confirmation_is_kept_on_later_scans()
    {
        var record = First(DriverUpdateInstallStatus.InstalledUnverified);

        record.ConfirmInstalled(Now.AddMinutes(5));
        record.ConfirmInstalled(Now.AddDays(3));

        record.Status.ShouldBe(DriverUpdateInstallStatus.Installed);
        record.ConfirmedAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public void Re_offered_clears_the_confirmation()
    {
        var record = First(DriverUpdateInstallStatus.InstalledUnverified);
        record.ConfirmInstalled(Now.AddMinutes(5));

        record.MarkReOffered();

        record.Status.ShouldBe(DriverUpdateInstallStatus.ReOffered);
        record.ConfirmedAt.ShouldBeNull();
    }

    [Fact]
    public void A_suppression_bounds_its_reason()
    {
        Should.Throw<ArgumentException>(() => new DeviceDriverUpdateSuppression(
            Device, Update, "t", new string('r', DeviceDriverUpdateSuppression.MaxReasonLength + 1),
            Guid.CreateVersion7(), "admin@test"));

        new DeviceDriverUpdateSuppression(Device, Update, "t", "  ", Guid.CreateVersion7(), "admin@test")
            .Reason.ShouldBeNull("a blank reason is no reason");
    }
}
