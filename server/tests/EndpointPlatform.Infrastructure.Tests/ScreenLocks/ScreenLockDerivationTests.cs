using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.ScreenLocks;

namespace EndpointPlatform.Infrastructure.Tests.ScreenLocks;

/// <summary>
/// The rule for deriving whether a device is behind a screen lock right now, pinned
/// as a pure function: the latest engage that reached the device, no newer release
/// that succeeded, and the agent's reported state as the override that lets a
/// locally-cleared lock show as released.
/// </summary>
public sealed class ScreenLockDerivationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    private static ScreenLockTaskSnapshot Engage(DeviceTaskStatus status, int minutes) =>
        new(DeviceTaskType.EngageScreenLock, status, T0.AddMinutes(minutes));

    private static ScreenLockTaskSnapshot Release(DeviceTaskStatus status, int minutes) =>
        new(DeviceTaskType.ReleaseScreenLock, status, T0.AddMinutes(minutes));

    [Fact]
    public void No_screen_lock_task_means_not_locked()
    {
        ScreenLockService.DeriveDeviceLocked(null, []).ShouldBeFalse();
        ScreenLockService.DeriveDeviceLocked(true, []).ShouldBeFalse();
    }

    [Fact]
    public void A_queued_engage_has_not_reached_the_screen_yet()
    {
        ScreenLockService.DeriveDeviceLocked(null, [Engage(DeviceTaskStatus.Queued, 0)]).ShouldBeFalse();
    }

    [Fact]
    public void A_delivered_or_succeeded_engage_with_nothing_since_is_locked()
    {
        ScreenLockService.DeriveDeviceLocked(null, [Engage(DeviceTaskStatus.Delivered, 0)]).ShouldBeTrue();
        ScreenLockService.DeriveDeviceLocked(null, [Engage(DeviceTaskStatus.Succeeded, 0)]).ShouldBeTrue();
    }

    [Fact]
    public void A_newer_succeeded_release_lifts_the_lock_but_a_merely_delivered_one_does_not()
    {
        ScreenLockService.DeriveDeviceLocked(null,
            [Engage(DeviceTaskStatus.Succeeded, 0), Release(DeviceTaskStatus.Succeeded, 5)]).ShouldBeFalse();

        ScreenLockService.DeriveDeviceLocked(null,
            [Engage(DeviceTaskStatus.Succeeded, 0), Release(DeviceTaskStatus.Delivered, 5)]).ShouldBeTrue();
    }

    [Fact]
    public void A_later_engage_relocks_after_a_release()
    {
        ScreenLockService.DeriveDeviceLocked(null,
        [
            Engage(DeviceTaskStatus.Succeeded, 0),
            Release(DeviceTaskStatus.Succeeded, 5),
            Engage(DeviceTaskStatus.Succeeded, 10),
        ]).ShouldBeTrue();
    }

    [Fact]
    public void The_agent_reporting_unlocked_wins_over_a_lock_with_no_release_task()
    {
        // The on-device IT unlock code: the agent says it is no longer locked, so it
        // is not, even though no release task was ever queued.
        ScreenLockService.DeriveDeviceLocked(false, [Engage(DeviceTaskStatus.Succeeded, 0)]).ShouldBeFalse();
    }

    [Fact]
    public void A_reported_true_or_null_defers_to_the_tasks()
    {
        ScreenLockService.DeriveDeviceLocked(true, [Engage(DeviceTaskStatus.Succeeded, 0)]).ShouldBeTrue();
        // Null is an older agent that does not report; the tasks decide.
        ScreenLockService.DeriveDeviceLocked(null, [Engage(DeviceTaskStatus.Succeeded, 0)]).ShouldBeTrue();
        ScreenLockService.DeriveDeviceLocked(true, []).ShouldBeFalse();
    }
}
