using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.WorkTime;

/// <summary>
/// Uploads spooled activity samples to the server, offline-safe: it drains the durable
/// spool in batches and removes each batch only once the server has taken it. An offline
/// stretch or an agent restart loses nothing — the samples wait in the spool.
/// </summary>
public sealed class ActivityUploadWorker(
    IActivitySpool spool,
    IActivityApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    ILogger<ActivityUploadWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private const int BatchSize = 500;

    private readonly IActivitySpool _spool = spool;
    private readonly IActivityApiClient _apiClient = apiClient;
    private readonly IDeviceCredentialStore _credentialStore = credentialStore;
    private readonly ILogger<ActivityUploadWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Activity upload tick failed; will retry.");
            }
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await _spool.ClaimAsync(BatchSize, cancellationToken);
            if (batch.Count == 0)
            {
                return;
            }

            var result = await _apiClient.UploadActivityAsync(batch, credential, cancellationToken);
            switch (result.Status)
            {
                case AgentApiStatus.Success:
                    await _spool.CommitAsync(batch.Count, cancellationToken);
                    if (batch.Count < BatchSize)
                    {
                        return; // drained
                    }
                    break; // more may remain; loop

                case AgentApiStatus.Rejected:
                    // The server refused this batch outright; retrying the same bytes will
                    // not help, so drop it rather than block the spool forever.
                    _logger.LogWarning("Server rejected an activity batch of {Count}; dropping it.", batch.Count);
                    await _spool.CommitAsync(batch.Count, cancellationToken);
                    break;

                default:
                    // Transient failure or unauthorized: keep the batch and retry next tick.
                    return;
            }
        }
    }
}
