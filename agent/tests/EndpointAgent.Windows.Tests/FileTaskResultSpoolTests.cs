using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Windows;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The on-disk task-result spool, against a real per-test temp state directory:
/// round trip, the restart re-detection, the entry cap, one entry per task, and
/// that a damaged file is discarded rather than wedging the spool.
/// </summary>
public sealed class FileTaskResultSpoolTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), $"epa-spool-test-{Guid.CreateVersion7():N}");

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }

    private string SpoolDirectory => Path.Combine(_stateDirectory, FileTaskResultSpool.DirectoryName);

    private FileTaskResultSpool NewSpool() =>
        new(
            Options.Create(new AgentOptions { ServerBaseUrl = "https://localhost:5081", StateDirectory = _stateDirectory }),
            NullLogger<FileTaskResultSpool>.Instance);

    private static SpooledTaskResult Entry(Guid taskId, DateTimeOffset at, string message = "Installed 1.") =>
        new(taskId, new AgentTaskResult(true, message, "{\"rebootRequired\":false,\"outcomes\":[]}"), at);

    [Fact]
    public async Task An_empty_or_missing_spool_has_no_entries_and_loads_nothing()
    {
        var spool = NewSpool();

        spool.HasEntries.ShouldBeFalse();
        (await spool.LoadAsync()).ShouldBeEmpty();
        Directory.Exists(SpoolDirectory).ShouldBeFalse("nothing is created until something is spooled");
    }

    [Fact]
    public async Task A_saved_result_round_trips_exactly_and_removing_it_clears_the_flag()
    {
        var spool = NewSpool();
        var taskId = Guid.CreateVersion7();

        await spool.SaveAsync(Entry(taskId, Now));

        spool.HasEntries.ShouldBeTrue();
        var loaded = (await spool.LoadAsync()).ShouldHaveSingleItem();
        loaded.TaskId.ShouldBe(taskId);
        loaded.SpooledAt.ShouldBe(Now);
        loaded.Result.ShouldBe(new AgentTaskResult(true, "Installed 1.", "{\"rebootRequired\":false,\"outcomes\":[]}"));

        await spool.RemoveAsync(taskId);

        spool.HasEntries.ShouldBeFalse();
        (await spool.LoadAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_restarted_agent_re_detects_what_the_previous_run_spooled()
    {
        var taskId = Guid.CreateVersion7();
        await NewSpool().SaveAsync(Entry(taskId, Now));

        // A new instance is what the service builds at start: one directory check.
        var afterRestart = NewSpool();

        afterRestart.HasEntries.ShouldBeTrue("a result spooled just before a restart must not wait for a new failure");
        (await afterRestart.LoadAsync()).ShouldHaveSingleItem().TaskId.ShouldBe(taskId);
    }

    [Fact]
    public async Task Entries_load_oldest_first()
    {
        var spool = NewSpool();
        var newest = Guid.CreateVersion7();
        var oldest = Guid.CreateVersion7();
        var middle = Guid.CreateVersion7();

        await spool.SaveAsync(Entry(newest, Now));
        await spool.SaveAsync(Entry(oldest, Now.AddHours(-3)));
        await spool.SaveAsync(Entry(middle, Now.AddHours(-1)));

        (await spool.LoadAsync()).Select(e => e.TaskId).ToArray().ShouldBe([oldest, middle, newest]);
    }

    [Fact]
    public async Task The_spool_is_capped_and_drops_the_oldest_first()
    {
        var spool = NewSpool();
        var ids = Enumerable.Range(0, TaskResultSpoolLimits.MaxEntries + 5).Select(_ => Guid.CreateVersion7()).ToList();

        for (var i = 0; i < ids.Count; i++)
        {
            await spool.SaveAsync(Entry(ids[i], Now.AddMinutes(i)));
        }

        var loaded = await spool.LoadAsync();
        loaded.Count.ShouldBe(TaskResultSpoolLimits.MaxEntries);
        loaded.Select(e => e.TaskId).ToArray().ShouldBe(ids.Skip(5).ToArray());
        Directory.EnumerateFiles(SpoolDirectory, "*.json").Count().ShouldBe(TaskResultSpoolLimits.MaxEntries);
    }

    [Fact]
    public async Task Saving_the_same_task_twice_keeps_one_entry_the_latest()
    {
        var spool = NewSpool();
        var taskId = Guid.CreateVersion7();

        await spool.SaveAsync(Entry(taskId, Now.AddMinutes(-5), "first"));
        await spool.SaveAsync(Entry(taskId, Now, "second"));

        var loaded = (await spool.LoadAsync()).ShouldHaveSingleItem();
        loaded.Result.Message.ShouldBe("second");
    }

    [Fact]
    public async Task A_damaged_entry_is_discarded_and_the_rest_still_load()
    {
        var spool = NewSpool();
        var good = Guid.CreateVersion7();
        await spool.SaveAsync(Entry(good, Now));
        var damaged = Path.Combine(SpoolDirectory, $"{Now.AddHours(-1).UtcTicks:D19}-{Guid.CreateVersion7():N}.json");
        await File.WriteAllTextAsync(damaged, "{ not json");

        (await spool.LoadAsync()).ShouldHaveSingleItem().TaskId.ShouldBe(good);
        File.Exists(damaged).ShouldBeFalse("an unreadable entry can never be delivered; it must not hold a slot");
    }

    [Fact]
    public async Task A_torn_write_left_by_a_crash_is_tidied_and_never_read_as_an_entry()
    {
        Directory.CreateDirectory(SpoolDirectory);
        var torn = Path.Combine(SpoolDirectory, $"{Now.UtcTicks:D19}-{Guid.CreateVersion7():N}.json.tmp");
        await File.WriteAllTextAsync(torn, "{\"version\":1,");

        var spool = NewSpool();

        spool.HasEntries.ShouldBeFalse();
        File.Exists(torn).ShouldBeFalse();
        (await spool.LoadAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Entries_live_as_plain_json_files_under_the_state_directory()
    {
        var spool = NewSpool();
        var taskId = Guid.CreateVersion7();

        await spool.SaveAsync(Entry(taskId, Now));

        var file = Directory.EnumerateFiles(SpoolDirectory).ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldBe($"{Now.UtcTicks:D19}-{taskId:N}.json");
        var text = await File.ReadAllTextAsync(file);
        text.ShouldContain("\"taskId\"");
        text.ShouldContain("\"spooledAt\"");
        text.ShouldContain("\"result\"");
    }
}
