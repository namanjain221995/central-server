using EndpointPlatform.Domain.Restarts;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Restarts;

/// <summary>
/// The rules of a scheduled restart: which delays are accepted, how much of a
/// delay becomes the warning Windows shows, and which transitions a schedule
/// and its device rows allow.
/// </summary>
public sealed class RestartScheduleTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Group = Guid.CreateVersion7();
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static RestartSchedule Create(int delay = 7200, int warning = 300) =>
        RestartSchedule.Create(Org, Group, delay, warning, Admin, "admin@test.local", Now);

    // ----------------------------------------------------------------- delay

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(300)]
    [InlineData(7200)]
    [InlineData(7 * 24 * 3600)]
    public void Delays_from_one_minute_to_seven_days_are_accepted(int seconds) =>
        RestartScheduleDelay.IsAccepted(seconds).ShouldBeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(-60)]
    [InlineData(7 * 24 * 3600 + 1)]
    [InlineData(int.MaxValue)]
    public void Delays_outside_that_range_are_refused(int seconds) =>
        RestartScheduleDelay.IsAccepted(seconds).ShouldBeFalse();

    [Fact]
    public void Every_preset_the_console_offers_is_accepted()
    {
        foreach (var preset in RestartScheduleDelay.PresetSeconds)
        {
            RestartScheduleDelay.IsAccepted(preset).ShouldBeTrue(preset.ToString());
        }

        RestartScheduleDelay.PresetSeconds.ShouldBe([60, 120, 300, 600, 900, 1800, 3600, 7200]);
    }

    /// <summary>
    /// The warning is the configured period, or the whole delay when that is
    /// shorter -- and never outside what the agent accepts as a grace period.
    /// </summary>
    [Theory]
    [InlineData(7200, 300, 300)]
    [InlineData(600, 300, 300)]
    [InlineData(300, 300, 300)]
    [InlineData(120, 300, 120)]
    [InlineData(60, 300, 60)]
    [InlineData(7200, 10, 30)]
    [InlineData(7200, 9999, 3600)]
    public void The_warning_is_the_configured_period_capped_by_the_delay_and_the_agent(int delay, int configured, int expected) =>
        RestartScheduleDelay.WarningFor(delay, configured).ShouldBe(expected);

    [Theory]
    [InlineData(60, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(5400, "1 hour 30 minutes")]
    [InlineData(7200, "2 hours")]
    [InlineData(86_400, "1 day")]
    [InlineData(93_900, "1 day 2 hours 5 minutes")]
    [InlineData(45, "45 seconds")]
    public void A_delay_is_described_in_words(int seconds, string expected) =>
        RestartScheduleDelay.Describe(seconds).ShouldBe(expected);

    // -------------------------------------------------------------- schedule

    [Fact]
    public void A_new_schedule_is_pending_and_dispatches_one_warning_before_the_restart()
    {
        var schedule = Create(delay: 7200, warning: 300);

        schedule.Status.ShouldBe(RestartScheduleStatus.Pending);
        schedule.RestartAt.ShouldBe(Now.AddSeconds(7200));
        schedule.DispatchAt.ShouldBe(Now.AddSeconds(7200 - 300));
        schedule.IsDue(Now.AddSeconds(6899)).ShouldBeFalse();
        schedule.IsDue(Now.AddSeconds(6900)).ShouldBeTrue();
    }

    [Fact]
    public void A_schedule_whose_warning_equals_its_delay_is_due_at_once()
    {
        var schedule = Create(delay: 120, warning: 120);

        schedule.DispatchAt.ShouldBe(Now);
        schedule.IsDue(Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(59, 30)]
    [InlineData(7 * 24 * 3600 + 1, 300)]
    public void A_schedule_outside_the_accepted_delays_cannot_be_created(int delay, int warning) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Create(delay, warning));

    [Theory]
    [InlineData(600, 0)]
    [InlineData(600, 601)]
    public void A_warning_must_be_positive_and_no_longer_than_the_delay(int delay, int warning) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Create(delay, warning));

    [Fact]
    public void Cancelling_a_pending_schedule_records_who_and_when()
    {
        var schedule = Create();
        var by = Guid.CreateVersion7();

        schedule.TryCancel(Now.AddMinutes(5), by, "other@test.local").ShouldBeTrue();

        schedule.Status.ShouldBe(RestartScheduleStatus.Cancelled);
        schedule.CancelledAt.ShouldBe(Now.AddMinutes(5));
        schedule.CancelledByUserId.ShouldBe(by);
        schedule.CancelledByDisplay.ShouldBe("other@test.local");
        schedule.IsDue(Now.AddDays(1)).ShouldBeFalse("a cancelled schedule is never due");
    }

    [Fact]
    public void A_dispatched_schedule_cannot_be_cancelled_cleanly_but_records_a_cancel_request()
    {
        var schedule = Create();
        schedule.MarkDispatched(Now.AddSeconds(6900));

        schedule.Status.ShouldBe(RestartScheduleStatus.Dispatched);
        schedule.DispatchedAt.ShouldBe(Now.AddSeconds(6900));
        schedule.TryCancel(Now.AddSeconds(6901), Admin, "admin@test.local").ShouldBeFalse();

        schedule.RecordCancelRequested(Now.AddSeconds(6901), Admin, "admin@test.local");
        schedule.Status.ShouldBe(RestartScheduleStatus.Dispatched, "cancelling after dispatch is per device; the schedule stays dispatched");
        schedule.CancelledAt.ShouldBe(Now.AddSeconds(6901));
    }

    [Fact]
    public void Only_a_pending_schedule_can_be_dispatched_missed_or_cancelled()
    {
        var cancelled = Create();
        cancelled.TryCancel(Now, Admin, "admin@test.local");
        Should.Throw<InvalidOperationException>(() => cancelled.MarkDispatched(Now));
        Should.Throw<InvalidOperationException>(() => cancelled.MarkMissed(Now));
        Should.Throw<InvalidOperationException>(() => cancelled.RecordCancelRequested(Now, Admin, "admin@test.local"));

        var missed = Create();
        missed.MarkMissed(Now.AddHours(3));
        missed.Status.ShouldBe(RestartScheduleStatus.Missed);
        missed.MissedAt.ShouldBe(Now.AddHours(3));
        missed.TryCancel(Now.AddHours(3), Admin, "admin@test.local").ShouldBeFalse();
    }

    // ----------------------------------------------------------- device rows

    [Fact]
    public void A_queued_device_row_carries_its_task_and_only_a_queued_one_does()
    {
        var scheduleId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();

        var queued = new RestartScheduleDevice(scheduleId, Guid.CreateVersion7(), "PC-1", RestartDispatchOutcome.Queued, taskId);
        queued.RestartTaskId.ShouldBe(taskId);
        queued.CancelOutcome.ShouldBeNull();

        var offline = new RestartScheduleDevice(scheduleId, Guid.CreateVersion7(), "PC-2", RestartDispatchOutcome.Offline, null);
        offline.RestartTaskId.ShouldBeNull("nothing was sent to an offline device");

        Should.Throw<ArgumentException>(() =>
            new RestartScheduleDevice(scheduleId, Guid.CreateVersion7(), "PC-3", RestartDispatchOutcome.Queued, null));
        Should.Throw<ArgumentException>(() =>
            new RestartScheduleDevice(scheduleId, Guid.CreateVersion7(), "PC-4", RestartDispatchOutcome.Excluded, taskId));
    }

    [Fact]
    public void A_cancellation_is_recorded_once_with_its_task_when_it_was_sent()
    {
        var row = new RestartScheduleDevice(Guid.CreateVersion7(), Guid.CreateVersion7(), "PC-1", RestartDispatchOutcome.Queued, Guid.CreateVersion7());
        var cancelId = Guid.CreateVersion7();

        row.RecordCancel(RestartCancelOutcome.Requested, cancelId, Now, "admin@test.local");

        row.CancelOutcome.ShouldBe(RestartCancelOutcome.Requested);
        row.CancelTaskId.ShouldBe(cancelId);
        row.CancelRequestedAt.ShouldBe(Now);

        Should.Throw<ArgumentException>(() => row.RecordCancel(RestartCancelOutcome.Requested, null, Now, "x"));
        Should.Throw<ArgumentException>(() => row.RecordCancel(RestartCancelOutcome.Unsupported, cancelId, Now, "x"));

        // A later attempt replaces the record: the row keeps only the latest word.
        var retry = Guid.CreateVersion7();
        row.RecordCancel(RestartCancelOutcome.Requested, retry, Now.AddMinutes(1), "other@test.local");
        row.CancelTaskId.ShouldBe(retry);
        row.CancelRequestedByDisplay.ShouldBe("other@test.local");
    }

    // ------------------------------------------------------- the cancel task

    [Fact]
    public void The_cancel_restart_task_needs_the_agent_that_can_abort_and_is_not_high_risk()
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.CancelRestart);

        definition.RequiredPermission.ShouldBe(Domain.Authorization.Permissions.Device.Restart,
            "undoing a restart is the same authority as ordering one");
        definition.HighRisk.ShouldBeFalse("it prevents a restart rather than causing one");
        definition.MinimumAgentVersion.ShouldBe("1.14.0");
        DeviceTaskCatalog.IsSupportedBy(definition, "1.13.4").ShouldBeFalse();
        DeviceTaskCatalog.IsSupportedBy(definition, "1.14.0").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(definition, "1.15.2+ci").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(definition, null).ShouldBeFalse();
    }

    [Fact]
    public void An_accepted_restart_becomes_cancelled_when_the_device_aborts_it()
    {
        var restart = DeviceTask.Create(Org, Guid.CreateVersion7(), DeviceTaskType.RestartDevice, null, Admin, "admin", Now, TimeSpan.FromMinutes(15));
        restart.TryDeliver(Now.AddSeconds(5)).ShouldBeTrue();
        restart.TryComplete(true, "accepted", """{"outcome":"Scheduled"}""", Now.AddSeconds(6)).ShouldBeTrue();

        restart.TryCancelAcceptedRestart(Now.AddSeconds(60), "Cancelled by admin: aborted.").ShouldBeTrue();

        restart.Status.ShouldBe(DeviceTaskStatus.Cancelled);
        restart.CompletedAt.ShouldBe(Now.AddSeconds(60));
        restart.ResultMessage.ShouldBe("Cancelled by admin: aborted.");
        restart.ResultJson.ShouldBe("""{"outcome":"Scheduled"}""", "what Windows said at the time is kept");
    }

    [Fact]
    public void A_delivered_restart_the_device_has_not_reported_can_also_be_cancelled()
    {
        var restart = DeviceTask.Create(Org, Guid.CreateVersion7(), DeviceTaskType.RestartDevice, null, Admin, "admin", Now, TimeSpan.FromMinutes(15));
        restart.TryDeliver(Now.AddSeconds(5)).ShouldBeTrue();

        restart.TryCancelAcceptedRestart(Now.AddSeconds(10), "aborted").ShouldBeTrue();
        restart.Status.ShouldBe(DeviceTaskStatus.Cancelled);
    }

    [Theory]
    [InlineData(DeviceTaskStatus.Queued)]
    [InlineData(DeviceTaskStatus.Failed)]
    [InlineData(DeviceTaskStatus.Expired)]
    [InlineData(DeviceTaskStatus.Cancelled)]
    public void A_restart_in_any_other_state_is_left_alone(DeviceTaskStatus status)
    {
        var restart = DeviceTask.Create(Org, Guid.CreateVersion7(), DeviceTaskType.RestartDevice, null, Admin, "admin", Now, TimeSpan.FromMinutes(15));
        switch (status)
        {
            case DeviceTaskStatus.Failed:
                restart.TryDeliver(Now).ShouldBeTrue();
                restart.TryComplete(false, "no", null, Now).ShouldBeTrue();
                break;
            case DeviceTaskStatus.Expired:
                restart.TryExpire(Now.AddHours(1)).ShouldBeTrue();
                break;
            case DeviceTaskStatus.Cancelled:
                restart.TryCancel(Now, "gone").ShouldBeTrue();
                break;
        }

        restart.Status.ShouldBe(status);
        restart.TryCancelAcceptedRestart(Now.AddMinutes(1), "aborted").ShouldBeFalse();
        restart.Status.ShouldBe(status);
    }

    [Fact]
    public void Only_a_restart_can_be_cancelled_this_way()
    {
        var shutdown = DeviceTask.Create(Org, Guid.CreateVersion7(), DeviceTaskType.ShutdownDevice, null, Admin, "admin", Now, TimeSpan.FromMinutes(15));
        shutdown.TryDeliver(Now).ShouldBeTrue();
        shutdown.TryComplete(true, "ok", null, Now).ShouldBeTrue();

        shutdown.TryCancelAcceptedRestart(Now.AddMinutes(1), "aborted").ShouldBeFalse();
        shutdown.Status.ShouldBe(DeviceTaskStatus.Succeeded);
    }
}
