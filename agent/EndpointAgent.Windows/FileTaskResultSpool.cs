using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using EndpointAgent.Core;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows;

/// <summary>
/// Undelivered task results as small plain-JSON files, one per task, in a
/// <c>task-results</c> folder of the agent's state directory.
/// </summary>
/// <remarks>
/// <para>
/// The same deliberately unclever file-store pattern as <see cref="FileElevationLedger"/>:
/// readable at the worst moment, write-then-move so a torn write never reads as
/// half an entry. Plain JSON, NOT DPAPI-sealed, because nothing in it is a secret:
/// an entry is exactly the task result that would have gone over the wire, and task
/// results carry no secrets by contract (<see cref="AgentTaskResult"/>). The state
/// directory's ACL (SYSTEM and Administrators) already keeps ordinary users out,
/// and the folder inherits it.
/// </para>
/// <para>
/// Bounded twice: at most <see cref="TaskResultSpoolLimits.MaxEntries"/> files (saving
/// past that deletes the oldest), and an entry larger than
/// <see cref="MaxEntryBytes"/> is refused rather than written. File names begin with
/// the spool time's UTC ticks, so an ordinal sort of the names is oldest-first
/// without opening a file.
/// </para>
/// <para>
/// <see cref="HasEntries"/> is decided once, by one directory check, when the spool
/// is created at service start -- which is how a result spooled just before an agent
/// restart is re-detected -- and kept current in memory afterwards, so the
/// per-heartbeat flush touches the disk only when something is actually waiting.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FileTaskResultSpool : ITaskResultSpool
{
    /// <summary>The folder under the state directory. Referenced by operators diagnosing a device; do not rename lightly.</summary>
    public const string DirectoryName = "task-results";

    /// <summary>Largest entry written. A real result is a few hundred KB at most (a 500-update postScan).</summary>
    public const int MaxEntryBytes = 2 * 1024 * 1024;

    private const string EntryPattern = "*.json";
    private const string TemporarySuffix = ".tmp";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly ILogger<FileTaskResultSpool> _logger;
    private readonly Lock _gate = new();
    private volatile bool _hasEntries;

    public FileTaskResultSpool(IOptions<AgentOptions> options, ILogger<FileTaskResultSpool> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _directory = Path.Combine(options.Value.StateDirectory ?? AgentPaths.StateDirectory, DirectoryName);

        lock (_gate)
        {
            DeleteTornWrites();
            _hasEntries = ListEntryFiles().Count > 0;
        }

        if (_hasEntries)
        {
            _logger.LogInformation("Undelivered task results are waiting in {Directory}; they will be re-sent.", _directory);
        }
    }

    public bool HasEntries => _hasEntries;

    public ValueTask SaveAsync(SpooledTaskResult entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new SpoolFile(FormatVersion, entry.TaskId, entry.SpooledAt, entry.Result), Json);

        if (bytes.Length > MaxEntryBytes)
        {
            _logger.LogError(
                "The result of task {TaskId} is {Size:N0} bytes, above the {Max:N0}-byte spool limit; it was not spooled.",
                entry.TaskId, bytes.Length, MaxEntryBytes);
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            Directory.CreateDirectory(_directory);

            // One entry per task: a re-save replaces, never duplicates.
            DeleteEntriesFor(entry.TaskId);

            var path = Path.Combine(_directory, FileNameFor(entry));
            var temporaryPath = path + TemporarySuffix;
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
            _hasEntries = true;

            // The cap: oldest first out.
            var files = ListEntryFiles();
            for (var i = 0; i < files.Count - TaskResultSpoolLimits.MaxEntries; i++)
            {
                _logger.LogWarning(
                    "The task-result spool is full ({Max} entries); the oldest entry {File} was dropped undelivered.",
                    TaskResultSpoolLimits.MaxEntries, Path.GetFileName(files[i]));
                TryDelete(files[i]);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SpooledTaskResult>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<SpooledTaskResult>();

        lock (_gate)
        {
            foreach (var file in ListEntryFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var stored = JsonSerializer.Deserialize<SpoolFile>(File.ReadAllBytes(file), Json);
                    if (stored is null || stored.TaskId == Guid.Empty || stored.Result is null)
                    {
                        throw new JsonException("The entry is empty or incomplete.");
                    }

                    entries.Add(new SpooledTaskResult(stored.TaskId, stored.Result, stored.SpooledAt));
                }
                catch (JsonException ex)
                {
                    // Unreadable now means unreadable forever: it can never be
                    // delivered, so keeping it would only take a slot.
                    _logger.LogWarning(ex, "Spooled task result {File} is unreadable and was discarded.", Path.GetFileName(file));
                    TryDelete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Possibly transient (a scanner holding the file); leave it for the next pass.
                    _logger.LogWarning(ex, "Could not read spooled task result {File}; it will be retried.", Path.GetFileName(file));
                }
            }

            _hasEntries = ListEntryFiles().Count > 0;
        }

        return ValueTask.FromResult<IReadOnlyList<SpooledTaskResult>>(entries);
    }

    public ValueTask RemoveAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            DeleteEntriesFor(taskId);
            _hasEntries = ListEntryFiles().Count > 0;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary><c>{spooledAt UTC ticks, 19 digits}-{task id, 32 hex}.json</c>: sorts oldest-first, finds by task id.</summary>
    private static string FileNameFor(SpooledTaskResult entry) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.SpooledAt.UtcTicks:D19}-{entry.TaskId:N}.json");

    /// <summary>The entry files, oldest first. Empty when the folder does not exist or cannot be listed.</summary>
    private List<string> ListEntryFiles()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            var files = Directory.EnumerateFiles(_directory, EntryPattern, SearchOption.TopDirectoryOnly).ToList();
            files.Sort(StringComparer.Ordinal);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list the task-result spool at {Directory}.", _directory);
            return [];
        }
    }

    private void DeleteEntriesFor(Guid taskId)
    {
        var suffix = string.Create(CultureInfo.InvariantCulture, $"-{taskId:N}.json");
        foreach (var file in ListEntryFiles())
        {
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>A crash between write and move leaves a <c>.tmp</c>; it never became an entry.</summary>
    private void DeleteTornWrites()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_directory, "*" + TemporarySuffix, SearchOption.TopDirectoryOnly))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not tidy torn writes in the task-result spool.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete spooled task result {File}.", Path.GetFileName(path));
        }
    }

    /// <summary>The on-disk shape. <c>Version</c> lets a later agent read or discard an older layout deliberately.</summary>
    private sealed record SpoolFile(int Version, Guid TaskId, DateTimeOffset SpooledAt, AgentTaskResult? Result);
}
