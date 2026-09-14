using System.ComponentModel;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.SessionNotice;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// Restarting the device: what is handed to Windows, what is reported back,
/// and the three refusals.
/// </summary>
/// <remarks>
/// Nothing here reboots anything. <see cref="FakeDeviceControl"/> stands in for
/// the Win32 layer and records the grace period it was given, or throws what
/// Windows would. The executor's job is the honest translation between the
/// task and that call, which is exactly what these tests pin.
/// </remarks>
public sealed class RestartTaskExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A settable clock, so "expired" is a fact about the test and not the wall.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The Win32 layer, minus Windows.</summary>
    private sealed class FakeDeviceControl : IDeviceControl
    {
        public List<(int Grace, string? Message)> Restarts { get; } = [];
        public Exception? Throw { get; set; }

        public Task RestartAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Restarts.Add((graceSeconds, message));
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a restart task must never shut the device down");

        public Task LockAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private static RestartTaskExecutor Executor(FakeDeviceControl control, DateTimeOffset? now = null) =>
        new(control, NullLogger<RestartTaskExecutor>.Instance, new FixedClock(now ?? Now));

    private static AgentTask Task_(int? graceSeconds, DateTimeOffset? expiresAt = null) =>
        new(
            Guid.CreateVersion7(),
            "RestartDevice",
            graceSeconds is null ? null : $$"""{"graceSeconds":{{graceSeconds}},"message":"IT scheduled a restart."}""",
            expiresAt);

    private static JsonElement Result(AgentTaskResult result) =>
        JsonDocument.Parse(result.ResultJson!).RootElement;

    // ------------------------------------------------------------- accepted

    /// <summary>
    /// Windows accepted the request: the grace period went through unchanged,
    /// and the result says when Windows will act -- the executor's clock plus the
    /// grace, because Windows counts from the moment the call returned.
    /// </summary>
    [Fact]
    public async Task An_accepted_restart_reports_when_windows_will_act()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_(600, Now.AddMinutes(15)));

        result.Succeeded.ShouldBeTrue();
        control.Restarts.ShouldBe([(600, "IT scheduled a restart.")]);
        result.Message.ShouldBe(
            "Restart accepted by Windows: the device restarts at 2026-09-12 10:10:00Z, 600s from when it received the task.");

        var json = Result(result);
        json.GetProperty("outcome").GetString().ShouldBe("Scheduled");
        json.GetProperty("graceSeconds").GetInt32().ShouldBe(600);
        json.GetProperty("restartAt").GetDateTimeOffset().ShouldBe(Now.AddMinutes(10));
        json.GetProperty("code").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>"Now" from an older server is a zero grace, and is still honest about it.</summary>
    [Fact]
    public async Task A_zero_grace_says_the_device_is_restarting_now()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_(0));

        result.Succeeded.ShouldBeTrue();
        control.Restarts.Single().Grace.ShouldBe(0);
        result.Message.ShouldBe("Restart accepted by Windows: the device is restarting now.");
        Result(result).GetProperty("restartAt").GetDateTimeOffset().ShouldBe(Now);
    }

    /// <summary>A task with no deadline -- a server that predates the field -- executes.</summary>
    [Fact]
    public async Task A_task_without_a_deadline_is_executed()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_(30, expiresAt: null));

        result.Succeeded.ShouldBeTrue();
        control.Restarts.Count.ShouldBe(1);
    }

    /// <summary>The payload's clamp is the deployed contract: over the ceiling is the ceiling, and absent is thirty.</summary>
    [Theory]
    [InlineData(3600, 3600)]
    [InlineData(99_999, 3600)]
    [InlineData(-5, 0)]
    public async Task The_grace_is_clamped_to_what_windows_is_ever_given(int requested, int expected)
    {
        var control = new FakeDeviceControl();

        await Executor(control).ExecuteAsync(Task_(requested));

        control.Restarts.Single().Grace.ShouldBe(expected);
    }

    [Fact]
    public async Task A_missing_or_malformed_payload_falls_back_to_the_standard_warning()
    {
        var control = new FakeDeviceControl();
        var executor = Executor(control);

        await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "RestartDevice", null));
        await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "RestartDevice", "{not json"));

        control.Restarts.Select(r => r.Grace).ShouldBe([30, 30]);
    }

    // -------------------------------------------------------------- refused

    /// <summary>
    /// The server never hands out an expired task, so reaching this means a long
    /// stall or a skewed clock. Either way nobody is expecting the restart any
    /// more, and Windows is not asked.
    /// </summary>
    [Fact]
    public async Task An_expired_task_is_refused_without_touching_windows()
    {
        var control = new FakeDeviceControl();
        var deadline = Now.AddSeconds(-1);

        var result = await Executor(control).ExecuteAsync(Task_(600, deadline));

        result.Succeeded.ShouldBeFalse();
        control.Restarts.ShouldBeEmpty("an expired restart must never reach the shutdown API");
        result.Message.ShouldBe(
            "Restart refused: the task expired at 2026-09-12 09:59:59Z before the device executed it; nothing was restarted.");
        Result(result).GetProperty("outcome").GetString().ShouldBe("Expired");
    }

    /// <summary>Exactly at the deadline counts as expired: the boundary is closed on the safe side.</summary>
    [Fact]
    public async Task A_task_expiring_this_instant_is_refused()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_(60, Now));

        result.Succeeded.ShouldBeFalse();
        control.Restarts.ShouldBeEmpty();
    }

    /// <summary>One second inside the deadline is still valid.</summary>
    [Fact]
    public async Task A_task_still_inside_its_deadline_is_executed()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_(60, Now.AddSeconds(1)));

        result.Succeeded.ShouldBeTrue();
        control.Restarts.Count.ShouldBe(1);
    }

    /// <summary>
    /// Windows said no. The attempt was made and it failed; reporting success
    /// would tell an operator the machine is about to restart when it is not.
    /// </summary>
    [Fact]
    public async Task A_windows_refusal_is_reported_as_the_failure_it_is()
    {
        var control = new FakeDeviceControl
        {
            Throw = new Win32Exception(5, "InitiateSystemShutdownEx failed (restart=True, error=5)."),
        };

        var result = await Executor(control).ExecuteAsync(Task_(60));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe("Restart failed: InitiateSystemShutdownEx failed (restart=True, error=5).");

        var json = Result(result);
        json.GetProperty("outcome").GetString().ShouldBe("Failed");
        json.GetProperty("code").GetInt32().ShouldBe(5);
        json.GetProperty("restartAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// A shutdown already scheduled is its own outcome: the machine will go down,
    /// but not on this task's timing, so this task did not do what it was asked.
    /// </summary>
    [Fact]
    public async Task A_restart_already_in_progress_is_a_distinct_honest_failure()
    {
        var control = new FakeDeviceControl
        {
            Throw = new Win32Exception(RestartTaskExecutor.ErrorShutdownInProgress, "A system shutdown has already been scheduled."),
        };

        var result = await Executor(control).ExecuteAsync(Task_(300));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe("Restart not applied: a restart or shutdown is already in progress on this device.");
        Result(result).GetProperty("outcome").GetString().ShouldBe("AlreadyInProgress");
        Result(result).GetProperty("code").GetInt32().ShouldBe(1115);
    }

    /// <summary>The privilege step can fail before the API is reached; that is a failure too, not a success.</summary>
    [Fact]
    public async Task A_privilege_failure_before_the_api_is_reported_honestly()
    {
        var control = new FakeDeviceControl
        {
            Throw = new InvalidOperationException("SeShutdownPrivilege could not be enabled."),
        };

        var result = await Executor(control).ExecuteAsync(Task_(60));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe("Restart failed: SeShutdownPrivilege could not be enabled.");
    }

    [Fact]
    public void The_executor_answers_to_its_own_task_type() =>
        Executor(new FakeDeviceControl()).TaskType.ShouldBe("RestartDevice");

    // ------------------------------------------------------- session notice

    private sealed class RecordingNotifier : IRestartNotifier
    {
        public List<RestartNotice> Notices { get; } = [];
        public Exception? Throw { get; set; }

        public void RestartScheduled(RestartNotice notice)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Notices.Add(notice);
        }
    }

    private static RestartTaskExecutor Executor(FakeDeviceControl control, IRestartNotifier notifier, DateTimeOffset? now = null) =>
        new(control, NullLogger<RestartTaskExecutor>.Instance, new FixedClock(now ?? Now), notifier);

    /// <summary>
    /// Once Windows has accepted the restart, the signed-in user is told the same
    /// moment the result reports -- the executor's clock plus the grace period.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(600)]
    [InlineData(3600)]
    public async Task An_accepted_restart_is_announced_with_the_moment_windows_will_act(int grace)
    {
        var notifier = new RecordingNotifier();

        var result = await Executor(new FakeDeviceControl(), notifier).ExecuteAsync(Task_(grace));

        result.Succeeded.ShouldBeTrue();
        var notice = notifier.Notices.ShouldHaveSingleItem();
        notice.GraceSeconds.ShouldBe(grace);
        notice.RestartAt.ShouldBe(Now.AddSeconds(grace));
        notice.RestartAt.ShouldBe(Result(result).GetProperty("restartAt").GetDateTimeOffset(),
            "the user's countdown and the console's result must name the same moment");
    }

    /// <summary>
    /// Nothing is announced unless Windows said yes. A notice for a restart that
    /// will not happen is exactly the false claim the notice must never make.
    /// </summary>
    [Fact]
    public async Task Nothing_is_announced_for_a_restart_windows_refused_or_that_expired()
    {
        var refused = new RecordingNotifier();
        await Executor(new FakeDeviceControl { Throw = new Win32Exception(5) }, refused).ExecuteAsync(Task_(60));
        refused.Notices.ShouldBeEmpty("Windows refused");

        var busy = new RecordingNotifier();
        await Executor(new FakeDeviceControl { Throw = new Win32Exception(1115) }, busy).ExecuteAsync(Task_(60));
        busy.Notices.ShouldBeEmpty("a restart was already in progress; this one did not schedule anything");

        var expired = new RecordingNotifier();
        await Executor(new FakeDeviceControl(), expired, now: Now).ExecuteAsync(Task_(60, expiresAt: Now.AddSeconds(-1)));
        expired.Notices.ShouldBeEmpty("an expired task restarts nothing");
    }

    /// <summary>
    /// The notice is a courtesy. If it cannot be sent, the restart has still been
    /// accepted and must still be reported as exactly that.
    /// </summary>
    [Fact]
    public async Task A_notifier_that_throws_changes_neither_the_restart_nor_its_result()
    {
        var control = new FakeDeviceControl();
        var notifier = new RecordingNotifier { Throw = new IOException("pipe gone") };

        var result = await Executor(control, notifier).ExecuteAsync(Task_(120));

        control.Restarts.ShouldHaveSingleItem().Grace.ShouldBe(120);
        result.Succeeded.ShouldBeTrue();
        Result(result).GetProperty("outcome").GetString().ShouldBe("Scheduled");
    }

    /// <summary>Without a notifier (every existing construction) the executor behaves as it always has.</summary>
    [Fact]
    public async Task No_notifier_is_the_same_as_a_silent_one()
    {
        var result = await Executor(new FakeDeviceControl()).ExecuteAsync(Task_(60));

        result.Succeeded.ShouldBeTrue();
    }
}
