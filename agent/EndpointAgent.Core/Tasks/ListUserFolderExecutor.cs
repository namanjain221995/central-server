using System.Text.Json;
using EndpointAgent.Core.FileCollection;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Executes the <c>ListUserFolder</c> task (server <see cref="DeviceTaskType"/> value 64):
/// validates the request and hands it to <see cref="CollectFilesWorker"/>, which
/// enumerates the folders and sends the listing, end-to-end encrypted, through the relay.
/// </summary>
public sealed class ListUserFolderExecutor : ITaskExecutor
{
    /// <summary>A sane ceiling if the server does not specify one.</summary>
    private const int DefaultMaxEntries = 20_000;

    private readonly CollectFilesWorker _worker;
    private readonly ILogger<ListUserFolderExecutor> _logger;

    public ListUserFolderExecutor(CollectFilesWorker worker, ILogger<ListUserFolderExecutor> logger)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string TaskType => "ListUserFolder";

    public Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var job, out var refusal))
        {
            _logger.LogWarning("Folder-listing task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return Task.FromResult(new AgentTaskResult(false, refusal, null));
        }

        if (!_worker.Enqueue(job))
        {
            return Task.FromResult(new AgentTaskResult(
                false, "The device is busy; try again shortly.", null));
        }

        return Task.FromResult(new AgentTaskResult(true, "Listing started.", null));
    }

    private static bool TryParse(string? payloadJson, out ListFilesJob job, out string refusal)
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

                    var match = FileCollectionProtocol.AllowedRootKeys
                        .FirstOrDefault(a => string.Equals(a, element.GetString(), StringComparison.OrdinalIgnoreCase));
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

            var maxEntries = DefaultMaxEntries;
            if (root.TryGetProperty("maxEntries", out var max) && max.ValueKind == JsonValueKind.Number
                && max.TryGetInt32(out var value) && value > 0)
            {
                maxEntries = value;
            }

            job = new ListFilesJob(transferId, recipientKey, userSid, roots, maxEntries);
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
