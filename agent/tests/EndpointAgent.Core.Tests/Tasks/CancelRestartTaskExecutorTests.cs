using System.ComponentModel;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.SessionNotice;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// Cancelling a restart: what is asked of Windows, what is reported back, and
/// the two ways Windows can say no.
/// </summary>
/// <remarks>
/// Nothing here aborts anything. <see cref="FakeDeviceControl"/> stands in for
/// the Win32 layer and records that it was asked, or throws what Windows would.
/// The executor's job is the honest translation between the task and that call.
/// </remarks>
public sealed class CancelRestartTaskExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid RestartId = Guid.Parse("0199c0d2-3b1e-7c8a-9f00-000000000001");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeDeviceControl : IDeviceControl
    {
        public int Aborts { get; private set; }
        public Exception? Throw { get; set; }

        public Task AbortRestartAsync(CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Aborts++;
            return Task.CompletedTask;
        }

        public Task RestartAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a cancel task must never restart the device");

        public Task ShutdownAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a cancel task must never shut the device down");

        public Task LockAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class RecordingNotifier : IRestartNotifier
    {
        public int Cancellations { get; private set; }
        public Exception? Throw { get; set; }

        public void RestartScheduled(RestartNotice notice) =>
            throw new InvalidOperationException("a cancel task must never announce a restart");

        public void RestartCancelled()
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Cancellations++;
        }
    }

    private static CancelRestartTaskExecutor Executor(FakeDeviceControl control, IRestartNotifier? notifier = null) =>
        new(control, NullLogger<CancelRestartTaskExecutor>.Instance, notifier, new FixedClock(Now));

    private static AgentTask Task_(Guid? restartTaskId = null, DateTimeOffset? expiresAt = null) =>
        new(
            Guid.CreateVersion7(),
            "CancelRestart",
            restartTaskId is null ? null : $$"""{"restartTaskId":"{{restartTaskId}}","requestedBy":"admin@example.invalid"}""",
            expiresAt);

    private static JsonElement Result(AgentTaskResult result) => JsonDocument.Parse(result.ResultJson!).RootElement;

    [Fact]
    public async Task An_aborted_restart_is_reported_as_cancelled_and_names_the_restart_it_undid()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_(RestartId));

        result.Succeeded.ShouldBeTrue();
        control.Aborts.ShouldBe(1);
        result.Message.ShouldBe("Restart cancelled: Windows aborted the pending restart before it happened.");

        var json = Result(result);
        json.GetProperty("outcome").GetString().ShouldBe("Cancelled");
        json.GetProperty("restartTaskId").GetGuid().ShouldBe(RestartId);
        json.GetProperty("code").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_task_without_a_payload_still_aborts_and_reports_no_restart_id()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        control.Aborts.ShouldBe(1);
        Result(result).GetProperty("restartTaskId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Windows had nothing pending: the restart already happened, was never
    /// accepted, or was aborted on the machine. The task asked for something that
    /// did not happen, so it is a failure -- but a named one, not an error.
    /// </summary>
    [Fact]
    public async Task Nothing_pending_is_reported_as_nothing_to_cancel_not_as_success()
    {
        var control = new FakeDeviceControl
        {
            Throw = new Win32Exception(CancelRestartTaskExecutor.ErrorNoShutdownInProgress, "No shutdown in progress."),
        };
        var notifier = new RecordingNotifier();

        var result = await Executor(control, notifier).ExecuteAsync(Task_(RestartId));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldStartWith("Nothing to cancel: Windows reports no restart or shutdown pending");
        var json = Result(result);
        json.GetProperty("outcome").GetString().ShouldBe("NothingToCancel");
        json.GetProperty("code").GetInt32().ShouldBe(1116);
        notifier.Cancellations.ShouldBe(0, "the user must not be told a restart was cancelled when none was pending");
    }

    [Fact]
    public async Task A_windows_refusal_is_a_failure_with_the_error_code()
    {
        var control = new FakeDeviceControl { Throw = new Win32Exception(5, "Access is denied.") };

        var result = await Executor(control).ExecuteAsync(Task_(RestartId));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe("Cancel failed: Access is denied.");
        var json = Result(result);
        json.GetProperty("outcome").GetString().ShouldBe("Failed");
        json.GetProperty("code").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task An_unelevated_agent_reports_the_privilege_failure()
    {
        var control = new FakeDeviceControl
        {
            Throw = new InvalidOperationException("SeShutdownPrivilege could not be enabled; the agent is not running elevated."),
        };

        var result = await Executor(control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe("Cancel failed: SeShutdownPrivilege could not be enabled; the agent is not running elevated.");
        Result(result).GetProperty("code").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Unlike a restart, a cancellation past its deadline is still carried out:
    /// aborting what is still pending is always the safe thing to do, and doing
    /// nothing would let a restart the administrator called off go ahead.
    /// </summary>
    [Fact]
    public async Task An_expired_cancellation_is_still_executed()
    {
        var control = new FakeDeviceControl();

        var result = await Executor(control).ExecuteAsync(Task_(RestartId, expiresAt: Now.AddMinutes(-1)));

        result.Succeeded.ShouldBeTrue();
        control.Aborts.ShouldBe(1);
    }

    [Fact]
    public async Task A_malformed_payload_does_not_stop_the_abort()
    {
        var control = new FakeDeviceControl();
        var task = new AgentTask(Guid.CreateVersion7(), "CancelRestart", "{not json", null);

        var result = await Executor(control).ExecuteAsync(task);

        result.Succeeded.ShouldBeTrue();
        control.Aborts.ShouldBe(1);
    }

    // ------------------------------------------------------- session notice

    [Fact]
    public async Task The_user_is_told_once_the_restart_is_really_cancelled()
    {
        var notifier = new RecordingNotifier();

        await Executor(new FakeDeviceControl(), notifier).ExecuteAsync(Task_(RestartId));

        notifier.Cancellations.ShouldBe(1);
    }

    [Fact]
    public async Task A_notice_that_cannot_be_sent_does_not_change_the_result()
    {
        var notifier = new RecordingNotifier { Throw = new IOException("pipe closed") };

        var result = await Executor(new FakeDeviceControl(), notifier).ExecuteAsync(Task_(RestartId));

        result.Succeeded.ShouldBeTrue();
        Result(result).GetProperty("outcome").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    public void The_executor_answers_to_its_own_task_type() =>
        Executor(new FakeDeviceControl()).TaskType.ShouldBe("CancelRestart");
}
