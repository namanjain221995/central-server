using System.Text.Json;
using EndpointAgent.Core.Configuration;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.WorkTime;

/// <summary>
/// A durable, offline-safe activity spool backed by two newline-delimited JSON files
/// under ProgramData: new samples are appended to <c>pending.jsonl</c>; an upload claims
/// them into <c>batch.jsonl</c>, uploads in chunks, and commits each chunk by rewriting
/// the file without it. A sample survives an agent restart and an offline stretch, and
/// is removed only after the server has accepted it — nothing is lost.
/// </summary>
public sealed class FileActivitySpool : IActivitySpool
{
    public const string DirectoryName = "activity";

    private const string PendingFile = "pending.jsonl";
    private const string BatchFile = "batch.jsonl";
    private const string TemporarySuffix = ".tmp";

    /// <summary>Caps the spool so a long offline stretch cannot fill the disk (~weeks for one user).</summary>
    private const long MaxPendingBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly ILogger<FileActivitySpool> _logger;
    private readonly Lock _gate = new();

    public FileActivitySpool(IOptions<AgentOptions> options, ILogger<FileActivitySpool> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = Path.Combine(options.Value.StateDirectory ?? AgentPaths.StateDirectory, DirectoryName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string Pending => Path.Combine(_directory, PendingFile);
    private string Batch => Path.Combine(_directory, BatchFile);

    public ValueTask AppendAsync(ActivitySampleDto sample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var line = JsonSerializer.Serialize(sample, Json) + "\n";

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var pending = Pending;
                if (File.Exists(pending) && new FileInfo(pending).Length > MaxPendingBytes)
                {
                    _logger.LogWarning("Activity spool is full ({Max} bytes); dropping a sample.", MaxPendingBytes);
                    return ValueTask.CompletedTask;
                }

                File.AppendAllText(pending, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not append an activity sample to the spool.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ActivitySampleDto>> ClaimAsync(int max, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            try
            {
                // Promote pending → batch when there is no batch in flight, so new
                // samples keep accumulating in pending while this batch is uploaded.
                if (!File.Exists(Batch) && File.Exists(Pending))
                {
                    File.Move(Pending, Batch, overwrite: true);
                }

                if (!File.Exists(Batch))
                {
                    return ValueTask.FromResult<IReadOnlyList<ActivitySampleDto>>([]);
                }

                var samples = new List<ActivitySampleDto>();
                foreach (var line in File.ReadLines(Batch))
                {
                    if (samples.Count >= max)
                    {
                        break;
                    }
                    if (TryParse(line, out var sample))
                    {
                        samples.Add(sample);
                    }
                }

                return ValueTask.FromResult<IReadOnlyList<ActivitySampleDto>>(samples);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not claim activity samples from the spool.");
                return ValueTask.FromResult<IReadOnlyList<ActivitySampleDto>>([]);
            }
        }
    }

    public ValueTask CommitAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            try
            {
                if (!File.Exists(Batch))
                {
                    return ValueTask.CompletedTask;
                }

                var remaining = File.ReadAllLines(Batch).Skip(count).ToArray();
                if (remaining.Length == 0)
                {
                    File.Delete(Batch);
                    return ValueTask.CompletedTask;
                }

                var temp = Batch + TemporarySuffix;
                File.WriteAllLines(temp, remaining);
                File.Move(temp, Batch, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not commit activity samples in the spool.");
            }
        }

        return ValueTask.CompletedTask;
    }

    private static bool TryParse(string line, out ActivitySampleDto sample)
    {
        sample = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ActivitySampleDto>(line, Json);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.UserName))
            {
                return false;
            }
            sample = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
