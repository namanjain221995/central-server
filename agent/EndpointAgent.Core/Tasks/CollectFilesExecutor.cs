using System.Text.Json;
using EndpointAgent.Core.FileCollection;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Executes the <c>CollectFiles</c> task (server <see cref="DeviceTaskType"/> value 63):
/// validates the request and hands it to <see cref="CollectFilesWorker"/>, which does
/// the zip, encryption and upload off the heartbeat thread.
/// </summary>
/// <remarks>
/// The executor returns the moment the job is queued. The real outcome — success,
/// truncation or failure — flows back through the file-collection relay, which the
/// operator's download and the status route observe; the task's own result only says
/// the collection was accepted and started.
/// </remarks>
public sealed class CollectFilesExecutor : ITaskExecutor
{
    private readonly CollectFilesWorker _worker;
    private readonly ILogger<CollectFilesExecutor> _logger;

    public CollectFilesExecutor(CollectFilesWorker worker, ILogger<CollectFilesExecutor> logger)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string TaskType => "CollectFiles";

    public Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var job, out var refusal))
        {
            _logger.LogWarning("File collection task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return Task.FromResult(new AgentTaskResult(false, refusal, null));
        }

        if (!_worker.Enqueue(job))
        {
            return Task.FromResult(new AgentTaskResult(
                false, "The device is already handling too many collections; try again shortly.", null));
        }

        _logger.LogInformation(
            "File collection {TransferId} queued for user {UserSid}, folders {Folders}.",
            job.TransferId, job.UserSid, string.Join(",", job.RootKeys));

        return Task.FromResult(new AgentTaskResult(
            true, "Collection started; the archive will stream to the operator.", null));
    }

    /// <summary>
    /// Reads the payload strictly: a usable transfer id, recipient key and user SID,
    /// at least one recognised folder key, and a positive byte ceiling. Unknown extra
    /// properties are ignored so a newer server does not strand this agent.
    /// </summary>
    private static bool TryParse(string? payloadJson, out CollectFilesJob job, out string refusal)
    {
        job = null!;
        refusal = string.Empty;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            refusal = "Empty payload.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            var transferId = GetString(root, "transferId");
            var recipientKey = GetString(root, "recipientPublicKey");
            var userSid = GetString(root, "userSid");

            if (string.IsNullOrWhiteSpace(transferId)
                || string.IsNullOrWhiteSpace(recipientKey)
                || string.IsNullOrWhiteSpace(userSid))
            {
                refusal = "Missing transfer id, recipient key or user SID.";
                return false;
            }

            var roots = new List<string>();
            if (root.TryGetProperty("rootKeys", out var rootKeys) && rootKeys.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in rootKeys.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = element.GetString();
                    var match = FileCollectionProtocol.AllowedRootKeys
                        .FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !roots.Contains(match))
                    {
                        roots.Add(match);
                    }
                }
            }

            if (roots.Count == 0)
            {
                refusal = "No valid folder was requested.";
                return false;
            }

            long maxBytes = 0;
            if (root.TryGetProperty("maxBytes", out var max) && max.ValueKind == JsonValueKind.Number)
            {
                max.TryGetInt64(out maxBytes);
            }

            if (maxBytes <= 0)
            {
                refusal = "A positive size ceiling is required.";
                return false;
            }

            List<string>? selected = null;
            if (root.TryGetProperty("selectedPaths", out var paths) && paths.ValueKind == JsonValueKind.Array)
            {
                selected = [];
                foreach (var element in paths.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            selected.Add(value);
                        }
                    }
                }
            }

            job = new CollectFilesJob(transferId, recipientKey, userSid, roots, maxBytes, selected);
            return true;
        }
        catch (JsonException ex)
        {
            refusal = $"Malformed payload: {ex.Message}";
            return false;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
