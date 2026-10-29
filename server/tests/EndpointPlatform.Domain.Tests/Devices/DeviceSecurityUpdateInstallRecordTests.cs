using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// The memory of a security-update install: every attempt counts, a new attempt
/// re-opens the question of whether it stuck, and the first confirmation is kept.
/// A hidden security update must always say why.
/// </summary>
public sealed class DeviceSecurityUpdateInstallRecordTests
{
    private static readonly Guid Device = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private const string Update = "5e000000-1111-1111-1111-111111111111";

    private static DeviceSecurityUpdateInstallRecord First(SecurityUpdateInstallStatus status)
    {
        var record = new DeviceSecurityUpdateInstallRecord(Device, Update, 200, "2026-10 Cumulative Update (KB5044284)", Now);
        record.RecordAttempt(status, 2, 0, Guid.CreateVersion7(), null, 200, Now);
        return record;
    }

    [Fact]
    public void A_first_attempt_is_counted_once()
    {
        var record = First(SecurityUpdateInstallStatus.PendingReboot);

        record.Attempts.ShouldBe(1);
        record.Status.ShouldBe(SecurityUpdateInstallStatus.PendingReboot);
        record.FirstAttemptAt.ShouldBe(Now);
        record.LastAttemptAt.ShouldBe(Now);
        record.LastResultCode.ShouldBe(2);
        record.Revision.ShouldBe(200);
    }

    [Fact]
    public void Another_attempt_counts_and_clears_an_earlier_confirmation()
    {
        var record = First(SecurityUpdateInstallStatus.InstalledUnverified);
        record.ConfirmInstalled(Now.AddMinutes(5));
        record.ConfirmedAt.ShouldBe(Now.AddMinutes(5));

        var task = Guid.CreateVersion7();
        record.RecordAttempt(SecurityUpdateInstallStatus.Failed, 4, unchecked((int)0x80240017), task, "Renamed", 201, Now.AddDays(1));

        record.Attempts.ShouldBe(2);
        record.Status.ShouldBe(SecurityUpdateInstallStatus.Failed);
        record.ConfirmedAt.ShouldBeNull();
        record.LastTaskId.ShouldBe(task);
        record.LastHresult.ShouldBe(unchecked((int)0x80240017));
        record.Title.ShouldBe("Renamed");
        record.Revision.ShouldBe(201);
        record.FirstAttemptAt.ShouldBe(Now, "the first attempt is history, not overwritten");
    }

    [Fact]
    public void The_first_confirmation_is_kept_on_later_scans()
    {
        var record = First(SecurityUpdateInstallStatus.InstalledUnverified);

        record.ConfirmInstalled(Now.AddMinutes(5));
        record.ConfirmInstalled(Now.AddDays(3));

        record.Status.ShouldBe(SecurityUpdateInstallStatus.Installed);
        record.ConfirmedAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public void Re_offered_clears_the_confirmation()
    {
        var record = First(SecurityUpdateInstallStatus.InstalledUnverified);
        record.ConfirmInstalled(Now.AddMinutes(5));

        record.MarkReOffered();

        record.Status.ShouldBe(SecurityUpdateInstallStatus.ReOffered);
        record.ConfirmedAt.ShouldBeNull();
    }

    // The one deliberate difference from drivers: hiding a security patch must say why.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_suppression_requires_a_reason(string? reason)
    {
        Should.Throw<ArgumentException>(() => new DeviceSecurityUpdateSuppression(
            Device, Update, "t", reason!, Guid.CreateVersion7(), "admin@test"));
    }

    [Fact]
    public void A_suppression_bounds_and_trims_its_reason()
    {
        Should.Throw<ArgumentException>(() => new DeviceSecurityUpdateSuppression(
            Device, Update, "t", new string('r', DeviceSecurityUpdateSuppression.MaxReasonLength + 1),
            Guid.CreateVersion7(), "admin@test"));

        new DeviceSecurityUpdateSuppression(Device, Update, "t", "  breaks the line-of-business app  ", Guid.CreateVersion7(), "admin@test")
            .Reason.ShouldBe("breaks the line-of-business app");
    }
}
