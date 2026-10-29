using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// The task lifecycle tells the truth about long work: a claimed install gets time
/// to run, an expiry says whether the device ever had the task, and a delivered
/// task's late result is accepted rather than thrown away -- while every settled
/// outcome stays exactly as immutable as before.
/// </summary>
public sealed class DeviceTaskLifecycleHonestyTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Device = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static DeviceTask Create(DeviceTaskType type = DeviceTaskType.InstallDriverUpdate, TimeSpan? ttl = null) =>
        DeviceTask.Create(Org, Device, type, null, Actor, "admin@test", Now, ttl ?? TimeSpan.FromHours(1));

    // ---- execution grace ---------------------------------------------------

    [Fact]
    public void The_driver_install_and_scan_carry_execution_grace_and_nothing_else_does_by_default()
    {
        DeviceTaskCatalog.Require(DeviceTaskType.InstallDriverUpdate).ExecutionGraceSeconds.ShouldBe(7200);

        // A Windows Update search can outlast the scan's half-hour lifetime; expiring
        // it mid-search refused its result and had the probe ask again at once.
        DeviceTaskCatalog.Require(DeviceTaskType.ScanDriverUpdates).ExecutionGraceSeconds.ShouldBe(3600);

        // The security-update twins carry their own grace (asserted below); every
        // other type keeps its deadline.
        foreach (var definition in DeviceTaskCatalog.All.Values.Where(d =>
                     d.Type is not (DeviceTaskType.InstallDriverUpdate or DeviceTaskType.ScanDriverUpdates
                         or DeviceTaskType.InstallSecurityUpdate or DeviceTaskType.ScanSecurityUpdates)))
        {
            definition.ExecutionGraceSeconds.ShouldBe(0, $"{definition.Type} must keep today's deadline behaviour");
        }
    }

    [Fact]
    public void The_security_install_and_scan_carry_execution_grace()
    {
        // A cumulative update is the largest download and the longest install
        // Windows Update does: three hours from the claim, more than a driver's two.
        DeviceTaskCatalog.Require(DeviceTaskType.InstallSecurityUpdate).ExecutionGraceSeconds.ShouldBe(10800);
        DeviceTaskCatalog.Require(DeviceTaskType.ScanSecurityUpdates).ExecutionGraceSeconds.ShouldBe(3600);

        // Old agents keep working: only the richness of what they report depends on version.
        DeviceTaskCatalog.Require(DeviceTaskType.InstallSecurityUpdate).MinimumAgentVersion.ShouldBe("1.24.0");
        DeviceTaskCatalog.Require(DeviceTaskType.ScanSecurityUpdates).MinimumAgentVersion.ShouldBe("1.24.0");
    }

    [Fact]
    public void Extending_for_execution_moves_a_delivered_tasks_deadline_out()
    {
        var task = Create();
        task.TryDeliver(Now.AddMinutes(55)).ShouldBeTrue();

        task.ExtendForExecution(Now.AddMinutes(55).AddHours(2)).ShouldBeTrue();

        task.ExpiresAt.ShouldBe(Now.AddMinutes(55).AddHours(2));
    }

    [Fact]
    public void Extending_never_shortens_a_deadline()
    {
        var task = Create(ttl: TimeSpan.FromHours(3));
        task.TryDeliver(Now.AddMinutes(1)).ShouldBeTrue();

        task.ExtendForExecution(Now.AddHours(2)).ShouldBeFalse();

        task.ExpiresAt.ShouldBe(Now.AddHours(3));
    }

    [Fact]
    public void Only_a_delivered_task_can_be_extended()
    {
        var queued = Create();
        queued.ExtendForExecution(Now.AddHours(5)).ShouldBeFalse("a task still waiting to be claimed keeps its claim deadline");
        queued.ExpiresAt.ShouldBe(Now.AddHours(1));

        var done = Create();
        done.TryDeliver(Now.AddMinutes(1));
        done.TryComplete(true, "ok", null, Now.AddMinutes(2));
        done.ExtendForExecution(Now.AddHours(5)).ShouldBeFalse();
    }

    // ---- the two expiries --------------------------------------------------

    [Fact]
    public void A_task_nobody_claimed_expires_saying_the_device_never_picked_it_up()
    {
        var task = Create();

        task.TryExpire(Now.AddHours(2), followUp: "a verification scan has been queued").ShouldBeTrue();

        task.Status.ShouldBe(DeviceTaskStatus.Expired);
        task.ResultMessage.ShouldBe(DeviceTask.ExpiredUnclaimedMessage,
            "a follow-up note only ever applies to a task the device had");
    }

    [Fact]
    public void A_claimed_task_that_went_silent_expires_saying_the_device_had_it()
    {
        var task = Create(DeviceTaskType.Ping, TimeSpan.FromMinutes(5));
        task.TryDeliver(Now.AddMinutes(1));

        task.TryExpire(Now.AddMinutes(10)).ShouldBeTrue();

        task.ResultMessage.ShouldBe(DeviceTask.ExpiredUnreportedMessage + ".");
        task.ResultMessage!.ShouldNotContain("verification scan");
    }

    [Fact]
    public void A_silent_claimed_install_says_what_the_platform_did_about_it()
    {
        var task = Create();
        task.TryDeliver(Now.AddMinutes(1));

        task.TryExpire(Now.AddHours(2), followUp: "a verification scan has been queued").ShouldBeTrue();

        task.ResultMessage.ShouldBe(
            "The device received this task but never reported a result (it may have gone offline mid-task); "
            + "a verification scan has been queued.");
    }

    [Fact]
    public void A_silent_claimed_task_takes_the_follow_up_note_once_the_platform_knows_it()
    {
        var task = Create();
        task.TryDeliver(Now.AddMinutes(1));
        task.TryExpire(Now.AddHours(2)).ShouldBeTrue();

        task.NoteExpiryFollowUp("a verification scan was already queued or running").ShouldBeTrue();

        task.ResultMessage.ShouldBe(
            "The device received this task but never reported a result (it may have gone offline mid-task); "
            + "a verification scan was already queued or running.");

        task.NoteExpiryFollowUp("a verification scan has been queued").ShouldBeFalse("noted once, never rewritten");
        task.ResultMessage!.ShouldEndWith("already queued or running.");
    }

    [Fact]
    public void Only_a_task_that_expired_after_delivery_and_is_still_expired_takes_a_follow_up_note()
    {
        var unclaimed = Create();
        unclaimed.TryExpire(Now.AddHours(2)).ShouldBeTrue();
        unclaimed.NoteExpiryFollowUp("a verification scan has been queued").ShouldBeFalse("nothing ran, nothing to verify");
        unclaimed.ResultMessage.ShouldBe(DeviceTask.ExpiredUnclaimedMessage);

        var settledLate = Create();
        settledLate.TryDeliver(Now.AddMinutes(1));
        settledLate.TryExpire(Now.AddHours(4));
        settledLate.TryCompleteLate(true, "Installed 1 driver update(s).", null, Now.AddHours(5)).ShouldBeTrue();
        settledLate.NoteExpiryFollowUp("a verification scan has been queued").ShouldBeFalse("its late result is the truth now");
        settledLate.ResultMessage.ShouldBe("Installed 1 driver update(s)." + DeviceTask.LateResultSuffix);

        var running = Create();
        running.TryDeliver(Now.AddMinutes(1));
        running.NoteExpiryFollowUp("a verification scan has been queued").ShouldBeFalse();
        running.ResultMessage.ShouldBeNull();
    }

    // ---- late results ------------------------------------------------------

    [Fact]
    public void A_delivered_task_that_expired_accepts_its_late_result_and_says_so()
    {
        var task = Create();
        task.TryDeliver(Now.AddMinutes(1));
        task.TryExpire(Now.AddHours(4)).ShouldBeTrue();

        task.TryCompleteLate(true, "Installed 2 driver update(s).", """{"rebootRequired":false}""", Now.AddHours(5))
            .ShouldBeTrue();

        task.Status.ShouldBe(DeviceTaskStatus.Succeeded);
        task.ResultMessage.ShouldBe("Installed 2 driver update(s)." + DeviceTask.LateResultSuffix);
        task.ResultJson.ShouldBe("""{"rebootRequired":false}""");
        task.CompletedAt.ShouldBe(Now.AddHours(5));
    }

    [Fact]
    public void A_late_failure_settles_as_failed()
    {
        var task = Create();
        task.TryDeliver(Now.AddMinutes(1));
        task.TryExpire(Now.AddHours(4));

        task.TryCompleteLate(false, null, null, Now.AddHours(5)).ShouldBeTrue();

        task.Status.ShouldBe(DeviceTaskStatus.Failed);
        task.ResultMessage!.ShouldEndWith(DeviceTask.LateResultSuffix);
    }

    [Fact]
    public void A_long_late_message_is_trimmed_to_fit_the_suffix()
    {
        var task = Create();
        task.TryDeliver(Now.AddMinutes(1));
        task.TryExpire(Now.AddHours(4));

        task.TryCompleteLate(true, new string('x', 1024), null, Now.AddHours(5)).ShouldBeTrue();

        task.ResultMessage!.Length.ShouldBe(1024);
        task.ResultMessage.ShouldEndWith(DeviceTask.LateResultSuffix);
    }

    [Fact]
    public void A_task_that_expired_unclaimed_never_takes_a_result()
    {
        var task = Create();
        task.TryExpire(Now.AddHours(2)).ShouldBeTrue();

        task.TryCompleteLate(true, "forged", null, Now.AddHours(3)).ShouldBeFalse("nothing ran: there is no truth to accept");
        task.Status.ShouldBe(DeviceTaskStatus.Expired);
    }

    [Fact]
    public void A_task_that_expired_at_claim_time_never_takes_a_result()
    {
        var task = Create(ttl: TimeSpan.FromMinutes(1));
        task.TryDeliver(Now.AddMinutes(5)).ShouldBeFalse();

        task.TryCompleteLate(true, "forged", null, Now.AddMinutes(6)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(DeviceTaskStatus.Succeeded)]
    [InlineData(DeviceTaskStatus.Failed)]
    [InlineData(DeviceTaskStatus.Cancelled)]
    public void Settled_outcomes_stay_unwritable_late(DeviceTaskStatus settled)
    {
        var task = Create();
        if (settled == DeviceTaskStatus.Cancelled)
        {
            task.TryCancel(Now.AddMinutes(1), "no").ShouldBeTrue();
        }
        else
        {
            task.TryDeliver(Now.AddMinutes(1));
            task.TryComplete(settled == DeviceTaskStatus.Succeeded, "done", null, Now.AddMinutes(2)).ShouldBeTrue();
        }

        task.TryCompleteLate(false, "tampered", null, Now.AddMinutes(3)).ShouldBeFalse();
        task.Status.ShouldBe(settled);
    }

    [Fact]
    public void A_still_delivered_task_is_completed_on_time_not_late()
    {
        var task = Create();
        task.TryDeliver(Now.AddMinutes(1));

        task.TryCompleteLate(true, "early", null, Now.AddMinutes(2)).ShouldBeFalse(
            "the late path is only for a task the sweeper already expired");
        task.TryComplete(true, "on time", null, Now.AddMinutes(2)).ShouldBeTrue();
        task.ResultMessage.ShouldBe("on time");
    }
}
