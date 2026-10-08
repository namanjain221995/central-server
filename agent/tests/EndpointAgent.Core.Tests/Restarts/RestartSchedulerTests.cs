using System.ComponentModel;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Restarts;
using EndpointAgent.Core.SessionNotice;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Tests.Restarts;

/// <summary>
/// Restarts the device holds itself: armed when the task arrives, carried out
/// at the moment with no server involved, durable across a restart of the
/// service, skipped when the machine comes back too late, and removable by a
/// cancellation until the countdown starts.
/// </summary>
public sealed class RestartSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IArmedRestartStore
    {
        public List<ArmedRestart> Saved { get; private set; } = [];
        public int Saves { get; private set; }

        public ValueTask<IReadOnlyList<ArmedRestart>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ArmedRestart>>([.. Saved]);

        public ValueTask SaveAsync(IReadOnlyList<ArmedRestart> restarts, CancellationToken cancellationToken = default)
        {
            Saved = [.. restarts];
            Saves++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDeviceControl : IDeviceControl
    {
        public List<(int Grace, string? Message)> Restarts { get; } = [];
        public int Aborts { get; private set; }
        public Exception? Throw { get; set; }

        public Task RestartAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default)
        {
            if (Throw is not null) throw Throw;
            Restarts.Add((graceSeconds, message));
            return Task.CompletedTask;
        }

        public Task AbortRestartAsync(CancellationToken cancellationToken = default)
        {
            Aborts++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("never a shutdown");

        public Task LockAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class RecordingNotifier : IRestartNotifier
    {
        public List<RestartNotice> Notices { get; } = [];
        public void RestartScheduled(RestartNotice notice) => Notices.Add(notice);
        public void RestartCancelled() { }
    }

    private static RestartScheduler Scheduler(MemoryStore store, FakeDeviceControl control, Clock clock, RecordingNotifier? notifier = null) =>
        new(store, control, NullLogger<RestartScheduler>.Instance, notifier, clock);

    private static ArmedRestart Armed(Guid id, DateTimeOffset dueAt, int warning = 300) =>
        new(id, dueAt, warning, "Your IT administrator scheduled a restart.", Start);

    // ------------------------------------------------------------ the moment

    [Fact]
    public async Task Nothing_happens_before_the_warning_starts()
    {
        var clock = new Clock(Start);
        var control = new FakeDeviceControl();
        var scheduler = Scheduler(new MemoryStore(), control, clock);
        await scheduler.ArmAsync(Armed(Guid.NewGuid(), Start.AddMinutes(30)));

        clock.Now = Start.AddMinutes(25).AddSeconds(-1);
        await scheduler.TickAsync();

        control.Restarts.ShouldBeEmpty();
        (await scheduler.ArmedAsync()).Count.ShouldBe(1);
    }

    /// <summary>
    /// The heart of it: at the start of the warning the countdown goes to
    /// Windows, with nothing asked of the server -- the device may be offline.
    /// </summary>
    [Fact]
    public async Task At_the_warning_the_countdown_is_handed_to_windows_and_the_restart_is_disarmed()
    {
        var clock = new Clock(Start);
        var control = new FakeDeviceControl();
        var store = new MemoryStore();
        var notifier = new RecordingNotifier();
        var scheduler = Scheduler(store, control, clock, notifier);
        await scheduler.ArmAsync(Armed(Guid.NewGuid(), Start.AddMinutes(30)));

        clock.Now = Start.AddMinutes(25);
        await scheduler.TickAsync();

        control.Restarts.ShouldBe([(300, "Your IT administrator scheduled a restart.")]);
        notifier.Notices.ShouldBe([new RestartNotice(Start.AddMinutes(30), 300)]);
        (await scheduler.ArmedAsync()).ShouldBeEmpty();
        store.Saved.ShouldBeEmpty("a started countdown is no longer armed, so a reboot does not repeat it");

        await scheduler.TickAsync();
        control.Restarts.Count.ShouldBe(1, "never twice");
    }

    [Fact]
    public async Task A_machine_that_wakes_late_but_within_the_tolerance_still_restarts_with_the_full_warning()
    {
        var clock = new Clock(Start);
        var control = new FakeDeviceControl();
        var scheduler = Scheduler(new MemoryStore(), control, clock);
        await scheduler.ArmAsync(Armed(Guid.NewGuid(), Start.AddMinutes(30)));

        clock.Now = Start.AddMinutes(30) + RestartScheduler.LateTolerance - TimeSpan.FromSeconds(1);
        await scheduler.TickAsync();

        control.Restarts.Single().Grace.ShouldBe(300, "a user who just opened the lid gets the whole warning, not a restart out of nowhere");
    }

    [Fact]
    public async Task A_machine_that_comes_back_after_the_tolerance_does_not_restart()
    {
        var clock = new Clock(Start);
        var control = new FakeDeviceControl();
        var store = new MemoryStore();
        var scheduler = Scheduler(store, control, clock);
        await scheduler.ArmAsync(Armed(Guid.NewGuid(), Start.AddMinutes(30)));

        clock.Now = Start.AddMinutes(30) + RestartScheduler.LateTolerance + TimeSpan.FromSeconds(1);
        await scheduler.TickAsync();

        control.Restarts.ShouldBeEmpty();
        store.Saved.ShouldBeEmpty("a skipped restart is forgotten, not retried");
    }

    [Fact]
    public async Task A_short_warning_on_time_counts_down_only_what_is_left()
    {
        var clock = new Clock(Start);
        var control = new FakeDeviceControl();
        var scheduler = Scheduler(new MemoryStore(), control, clock);
        await scheduler.ArmAsync(Armed(Guid.NewGuid(), Start.AddMinutes(2), warning: 120));

        clock.Now = Start.AddSeconds(5);
        await scheduler.TickAsync();

        control.Restarts.Single().Grace.ShouldBe(115);
    }

    // ------------------------------------------------------------ durability

    [Fact]
    public async Task An_armed_restart_survives_the_service_restarting()
    {
        var clock = new Clock(Start);
        var store = new MemoryStore();
        var first = Scheduler(store, new FakeDeviceControl(), clock);
        var id = Guid.NewGuid();
        await first.ArmAsync(Armed(id, Start.AddMinutes(30)));

        var control = new FakeDeviceControl();
        var afterReboot = Scheduler(store, control, clock);
        clock.Now = Start.AddMinutes(25);
        await afterReboot.TickAsync();

        control.Restarts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Arming_the_same_task_twice_arms_it_once()
    {
        var store = new MemoryStore();
        var scheduler = Scheduler(store, new FakeDeviceControl(), new Clock(Start));
        var id = Guid.NewGuid();

        await scheduler.ArmAsync(Armed(id, Start.AddMinutes(30)));
        await scheduler.ArmAsync(Armed(id, Start.AddMinutes(45)));

        (await scheduler.ArmedAsync()).ShouldHaveSingleItem().DueAt.ShouldBe(Start.AddMinutes(30));
    }

    [Fact]
    public async Task The_file_store_round_trips_and_reads_a_missing_or_damaged_file_as_nothing_armed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "armed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileArmedRestartStore(Options.Create(new AgentOptions { StateDirectory = dir }), NullLogger<FileArmedRestartStore>.Instance);
            (await store.LoadAsync()).ShouldBeEmpty();

            var restart = Armed(Guid.NewGuid(), Start.AddHours(2));
            await store.SaveAsync([restart]);
            (await store.LoadAsync()).ShouldBe([restart]);

            await File.WriteAllTextAsync(Path.Combine(dir, FileArmedRestartStore.StateFileName), "{ not json");
            (await store.LoadAsync()).ShouldBeEmpty("a damaged file costs a restart, never causes one");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------ refusals

    [Theory]
    [InlineData(1115)]
    [InlineData(1190)]
    public async Task A_restart_already_scheduled_by_windows_is_left_alone(int code)
    {
        var clock = new Clock(Start.AddMinutes(25));
        var control = new FakeDeviceControl { Throw = new Win32Exception(code) };
        var scheduler = Scheduler(new MemoryStore(), control, clock);
        await scheduler.ArmAsync(Armed(Guid.NewGuid(), Start.AddMinutes(30)));

        await scheduler.TickAsync();

        (await scheduler.ArmedAsync()).ShouldBeEmpty("not retried every five seconds");
    }

    // ------------------------------------------------------------ the task

    private static AgentTask ScheduleTask(Guid id, DateTimeOffset restartAt, DateTimeOffset? serverTime, int warning = 300) =>
        new(id, "ScheduleRestart",
            JsonSerializer.Serialize(new { restartAt, warningSeconds = warning, message = "Your IT administrator scheduled a restart." }),
            restartAt, serverTime);

    [Fact]
    public async Task The_task_arms_the_restart_at_the_servers_moment_even_when_the_device_clock_is_wrong()
    {
        // The device's clock runs ten minutes fast.
        var deviceClock = new Clock(Start.AddMinutes(10));
        var scheduler = Scheduler(new MemoryStore(), new FakeDeviceControl(), deviceClock);
        var executor = new ScheduleRestartTaskExecutor(scheduler, NullLogger<ScheduleRestartTaskExecutor>.Instance, deviceClock);
        var id = Guid.NewGuid();

        var result = await executor.ExecuteAsync(ScheduleTask(id, Start.AddMinutes(30), serverTime: Start));

        result.Succeeded.ShouldBeTrue();
        var armed = (await scheduler.ArmedAsync()).ShouldHaveSingleItem();
        armed.TaskId.ShouldBe(id);
        armed.DueAt.ShouldBe(Start.AddMinutes(40), "thirty minutes from now on the device's own clock");
        var json = JsonDocument.Parse(result.ResultJson!).RootElement;
        json.GetProperty("outcome").GetString().ShouldBe("Armed");
        json.GetProperty("restartAt").GetDateTimeOffset().ShouldBe(Start.AddMinutes(30), "reported in the server's time");
    }

    [Fact]
    public async Task A_task_whose_moment_has_passed_is_refused_and_arms_nothing()
    {
        var clock = new Clock(Start);
        var scheduler = Scheduler(new MemoryStore(), new FakeDeviceControl(), clock);
        var executor = new ScheduleRestartTaskExecutor(scheduler, NullLogger<ScheduleRestartTaskExecutor>.Instance, clock);

        var result = await executor.ExecuteAsync(ScheduleTask(Guid.NewGuid(), Start.AddSeconds(-1), serverTime: Start));

        result.Succeeded.ShouldBeFalse();
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcome").GetString().ShouldBe("Expired");
        (await scheduler.ArmedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_payload_arms_nothing()
    {
        var clock = new Clock(Start);
        var scheduler = Scheduler(new MemoryStore(), new FakeDeviceControl(), clock);
        var executor = new ScheduleRestartTaskExecutor(scheduler, NullLogger<ScheduleRestartTaskExecutor>.Instance, clock);

        var result = await executor.ExecuteAsync(new AgentTask(Guid.NewGuid(), "ScheduleRestart", "{\"nope\":1}"));

        result.Succeeded.ShouldBeFalse();
        (await scheduler.ArmedAsync()).ShouldBeEmpty();
    }

    // ------------------------------------------------------------ cancelling

    [Fact]
    public async Task A_cancellation_before_the_countdown_disarms_without_touching_windows()
    {
        var clock = new Clock(Start);
        var control = new FakeDeviceControl();
        var scheduler = Scheduler(new MemoryStore(), control, clock);
        var id = Guid.NewGuid();
        await scheduler.ArmAsync(Armed(id, Start.AddMinutes(30)));
        var cancel = new CancelRestartTaskExecutor(control, NullLogger<CancelRestartTaskExecutor>.Instance, scheduler: scheduler);

        var result = await cancel.ExecuteAsync(new AgentTask(Guid.NewGuid(), "CancelRestart",
            JsonSerializer.Serialize(new { restartTaskId = id, requestedBy = "admin@example.invalid" })));

        result.Succeeded.ShouldBeTrue();
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcome").GetString().ShouldBe("Cancelled");
        control.Aborts.ShouldBe(0, "there was no Windows countdown to abort");
        (await scheduler.ArmedAsync()).ShouldBeEmpty();

        clock.Now = Start.AddMinutes(25);
        await scheduler.TickAsync();
        control.Restarts.ShouldBeEmpty("a disarmed restart never happens");
    }

    [Fact]
    public async Task A_cancellation_after_the_countdown_started_aborts_it_in_windows()
    {
        var clock = new Clock(Start);
        var control = new FakeDeviceControl();
        var scheduler = Scheduler(new MemoryStore(), control, clock);
        var id = Guid.NewGuid();
        await scheduler.ArmAsync(Armed(id, Start.AddMinutes(30)));
        clock.Now = Start.AddMinutes(26);
        await scheduler.TickAsync();
        var cancel = new CancelRestartTaskExecutor(control, NullLogger<CancelRestartTaskExecutor>.Instance, scheduler: scheduler);

        var result = await cancel.ExecuteAsync(new AgentTask(Guid.NewGuid(), "CancelRestart",
            JsonSerializer.Serialize(new { restartTaskId = id, requestedBy = "admin@example.invalid" })));

        result.Succeeded.ShouldBeTrue();
        control.Aborts.ShouldBe(1);
    }
}
