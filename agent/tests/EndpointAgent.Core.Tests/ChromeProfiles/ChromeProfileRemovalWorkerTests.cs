using EndpointAgent.Core.ChromeProfiles;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Tests.Usb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Tests.ChromeProfiles;

/// <summary>
/// The deferred-removal worker's decision table, against the same fake machine
/// the executor is pinned with: a pass over an empty store is a no-op; Chrome
/// still open leaves the entry pending; Chrome found closed completes the
/// removal through the one shared engine; a request the world moved under (the
/// account changed hands, the user is gone) is dropped with a warning, never
/// retried into place; a profile already gone is the idempotent success; a
/// partial removal stays for the next pass; and an entry older than seven days
/// is dropped before any attempt. Plus the store itself: round-trip, the safe
/// misreading of a damaged file, and intent surviving a service restart.
/// </summary>
public sealed class ChromeProfileRemovalWorkerTests
{
    private const string Sid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
    private const string UserData = @"C:\Users\casey\AppData\Local\Google\Chrome\User Data";

    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private const string LocalStateDocument = """
        {
          "profile": {
            "info_cache": {
              "Default": { "name": "Casey", "user_name": "casey.example@example.com" },
              "Profile 9": { "name": "Work", "user_name": "casey@example.com" }
            },
            "last_used": "Profile 9"
          }
        }
        """;

    // ===============================================================  THE WIRING

    private sealed class RecordingLogger : ILogger<ChromeProfileRemovalWorker>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    private static FakeChromeProfileRemover Remover(string? localState = LocalStateDocument)
    {
        var remover = new FakeChromeProfileRemover { LocalState = localState };
        remover.UserDataBySid[Sid] = UserData;
        remover.Directories.Add("Profile 9");
        return remover;
    }

    private static ChromeProfileRemovalWorker Worker(
        FakeChromeProfileRemover remover,
        IPendingChromeRemovalStore store,
        TimeProvider? clock = null,
        ILogger<ChromeProfileRemovalWorker>? logger = null) =>
        new(store,
            new ChromeProfileRemovalEngine(remover, NullLogger<ChromeProfileRemovalEngine>.Instance),
            logger ?? NullLogger<ChromeProfileRemovalWorker>.Instance,
            clock ?? new TestClock(Start));

    private static PendingChromeProfileRemoval Pending(
        DateTimeOffset? requestedAt = null,
        string key = "Profile 9",
        string? email = "casey@example.com",
        string sid = Sid) =>
        new(Guid.CreateVersion7(), sid, key, email, UserData + "\\" + key, requestedAt ?? Start);

    // ============================================================  THE DECISIONS

    [Fact]
    public async Task An_empty_store_makes_the_pass_a_no_op()
    {
        var remover = Remover();
        var store = new MemoryPendingChromeRemovalStore();
        var worker = Worker(remover, store);

        await worker.RunPendingAsync();

        remover.ChromeRunningChecks.ShouldBe(0, "with nothing pending there is nothing to even look at");
        store.Saves.ShouldBe(0);
        remover.Deleted.ShouldBeEmpty();
    }

    /// <summary>The heart of it: the operator clicked once, hours ago; the first pass that finds Chrome closed finishes the job.</summary>
    [Fact]
    public async Task A_pending_removal_completes_the_moment_chrome_is_found_closed()
    {
        var remover = Remover();
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending());
        var worker = Worker(remover, store);

        await worker.RunPendingAsync();

        remover.Deleted.ShouldBe(["Profile 9"]);
        remover.Written.ShouldHaveSingleItem("the Local State entry comes out, exactly as a live task would take it out");
        store.Saved.ShouldBeEmpty("a completed removal is no longer pending");
        (await worker.PendingAsync()).ShouldBeEmpty();

        await worker.RunPendingAsync();
        remover.DeleteCalls.ShouldBe(1, "never twice");
    }

    [Fact]
    public async Task A_pending_removal_stays_while_chrome_still_runs()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending());
        var worker = Worker(remover, store);

        await worker.RunPendingAsync();
        await worker.RunPendingAsync();

        remover.Deleted.ShouldBeEmpty();
        remover.Written.ShouldBeEmpty();
        (await worker.PendingAsync()).ShouldHaveSingleItem();
        store.Saves.ShouldBe(0, "an unchanged pending list is not rewritten every two minutes");
    }

    /// <summary>
    /// "Profile now gone" is the engine's idempotent success -- a re-sent task
    /// or the user removed it themselves -- so the entry is done, not an error.
    /// </summary>
    [Fact]
    public async Task A_profile_already_fully_removed_counts_as_done_and_the_entry_is_dropped()
    {
        var remover = Remover(localState: """
            { "profile": { "info_cache": { "Default": { "name": "Casey" } }, "last_used": "Default" } }
            """);
        remover.Directories.Clear();
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending());
        var worker = Worker(remover, store);

        await worker.RunPendingAsync();

        remover.Written.ShouldBeEmpty();
        remover.Deleted.ShouldBeEmpty();
        store.Saved.ShouldBeEmpty("there is nothing left to wait for");
    }

    /// <summary>
    /// The console's view changed while the request waited: the profile's account
    /// is someone else's now. The same stale-inventory gate a live task faces
    /// drops the entry -- with a warning, because an operator asked for something
    /// that silently will not happen.
    /// </summary>
    [Fact]
    public async Task An_account_that_changed_hands_drops_the_entry_with_a_warning()
    {
        var remover = Remover();
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending(email: "someoneelse@example.com"));
        var log = new RecordingLogger();
        var worker = Worker(remover, store, logger: log);

        await worker.RunPendingAsync();

        remover.Written.ShouldBeEmpty("a stale request removes nothing");
        remover.Deleted.ShouldBeEmpty();
        store.Saved.ShouldBeEmpty("a request the world moved under is dropped, not retried into place");
        log.Lines.ShouldContain(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("no longer matches the console's view"));
    }

    /// <summary>A Windows account deleted while the request waited is the same staleness: dropped, warned, nothing touched.</summary>
    [Fact]
    public async Task An_unresolvable_user_drops_the_entry_with_a_warning()
    {
        var remover = Remover();
        remover.UserDataBySid.Clear();
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending());
        var log = new RecordingLogger();
        var worker = Worker(remover, store, logger: log);

        await worker.RunPendingAsync();

        store.Saved.ShouldBeEmpty();
        remover.Deleted.ShouldBeEmpty();
        log.Lines.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.Contains("no longer holds"));
    }

    /// <summary>
    /// Stale intent must not fire weeks later: past seven days the entry is
    /// dropped BEFORE any attempt -- even with Chrome closed and the removal one
    /// call away -- while an entry at exactly the boundary still runs.
    /// </summary>
    [Fact]
    public async Task An_entry_older_than_seven_days_is_dropped_without_an_attempt()
    {
        var remover = Remover();
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending(requestedAt: Start - ChromeProfileRemovalWorker.MaxPendingAge - TimeSpan.FromSeconds(1)));
        var log = new RecordingLogger();
        var worker = Worker(remover, store, logger: log);

        await worker.RunPendingAsync();

        remover.Deleted.ShouldBeEmpty("an expired intent is forgotten, not carried out");
        remover.Written.ShouldBeEmpty();
        remover.ChromeRunningChecks.ShouldBe(0, "dropped before the engine is even asked");
        store.Saved.ShouldBeEmpty();
        log.Lines.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.Contains("stale intent"));
    }

    [Fact]
    public async Task An_entry_at_exactly_seven_days_still_runs()
    {
        var remover = Remover();
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending(requestedAt: Start - ChromeProfileRemovalWorker.MaxPendingAge));
        var worker = Worker(remover, store);

        await worker.RunPendingAsync();

        remover.Deleted.ShouldBe(["Profile 9"]);
        store.Saved.ShouldBeEmpty();
    }

    /// <summary>
    /// A delete that fails after the list was rewritten leaves the engine's
    /// partial state; the entry stays, and the next pass finishes the job the
    /// way a re-run of the task would.
    /// </summary>
    [Fact]
    public async Task A_failed_directory_delete_leaves_the_entry_and_the_next_pass_finishes_it()
    {
        var remover = Remover();
        remover.DeleteFailures.Enqueue(new UnauthorizedAccessException("Access to the path is denied."));
        var store = new MemoryPendingChromeRemovalStore();
        store.Saved.Add(Pending());
        var worker = Worker(remover, store);

        await worker.RunPendingAsync();

        (await worker.PendingAsync()).ShouldHaveSingleItem("the directory is still there; the job is not done");
        remover.Written.ShouldHaveSingleItem("the Local State edit stands");

        await worker.RunPendingAsync();

        remover.Deleted.ShouldBe(["Profile 9"]);
        store.Saved.ShouldBeEmpty();
        remover.Written.ShouldHaveSingleItem("the second pass only had the leftover directory to delete");
    }

    [Fact]
    public async Task Scheduling_the_same_target_twice_keeps_one_entry_with_the_latest_request()
    {
        var store = new MemoryPendingChromeRemovalStore();
        var worker = Worker(Remover(), store);
        var first = Pending(requestedAt: Start);
        var second = Pending(requestedAt: Start.AddHours(5));

        await worker.ScheduleAsync(first);
        await worker.ScheduleAsync(second);

        var entry = (await worker.PendingAsync()).ShouldHaveSingleItem();
        entry.TaskId.ShouldBe(second.TaskId);
        entry.RequestedAt.ShouldBe(Start.AddHours(5));
    }

    [Fact]
    public async Task Different_targets_queue_side_by_side()
    {
        var store = new MemoryPendingChromeRemovalStore();
        var worker = Worker(Remover(), store);

        await worker.ScheduleAsync(Pending());
        await worker.ScheduleAsync(Pending(key: "Profile 2"));
        await worker.ScheduleAsync(Pending(sid: "S-1-5-21-1000000000-2000000000-3000000000-1002"));

        (await worker.PendingAsync()).Count.ShouldBe(3);
    }

    // ============================================================  DURABILITY

    /// <summary>
    /// The whole point of deferring: the intent outlives the service process.
    /// One worker takes the request; after a "restart" a fresh worker over the
    /// same file completes it.
    /// </summary>
    [Fact]
    public async Task A_pending_removal_survives_the_service_restarting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pending-chrome-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new AgentOptions { StateDirectory = dir });

            var before = Worker(Remover(), new FilePendingChromeRemovalStore(
                options, NullLogger<FilePendingChromeRemovalStore>.Instance));
            await before.ScheduleAsync(Pending());

            var remover = Remover();
            var afterRestart = Worker(remover, new FilePendingChromeRemovalStore(
                options, NullLogger<FilePendingChromeRemovalStore>.Instance));

            await afterRestart.RunPendingAsync();

            remover.Deleted.ShouldBe(["Profile 9"]);
            (await afterRestart.PendingAsync()).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_file_store_round_trips_and_reads_a_missing_or_damaged_file_as_nothing_pending()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pending-chrome-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FilePendingChromeRemovalStore(
                Options.Create(new AgentOptions { StateDirectory = dir }),
                NullLogger<FilePendingChromeRemovalStore>.Instance);
            (await store.LoadAsync()).ShouldBeEmpty();

            var entry = Pending();
            await store.SaveAsync([entry]);
            (await store.LoadAsync()).ShouldBe([entry]);

            await File.WriteAllTextAsync(Path.Combine(dir, FilePendingChromeRemovalStore.StateFileName), "{ not json");
            (await store.LoadAsync()).ShouldBeEmpty("a damaged file costs the operator a click, never deletes unasked");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
