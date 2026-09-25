using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Periodically asks devices with stale inventory for a fresh upload.
/// </summary>
/// <remarks>
/// <para>
/// Runs in the Admin host (the management plane), resolves a scoped
/// <see cref="InventoryRefreshSweepService"/> per batch and processes bounded
/// batches so a large fleet is flagged across several short transactions rather
/// than one long one. A failing tick is logged and retried on the next interval;
/// the sweeper never crashes the host.
/// </para>
/// <para>
/// Fifteen minutes between ticks is deliberate: the threshold it enforces is
/// measured in hours, so a finer interval would only add database polls without
/// making any refresh arrive sooner. The first sweep runs at startup so a host
/// that was down for a day catches up immediately rather than a quarter of an
/// hour later.
/// </para>
/// </remarks>
public sealed class InventoryRefreshSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<InventoryRefreshSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<InventoryRefreshSweeper> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Run once at startup, then on each interval tick.
        do
        {
            try
            {
                // Drain in batches until a tick finds nothing more to request.
                // Each batch gets its own scope, and so its own DbContext: a
                // catch-up tick over a large fleet must not carry every earlier
                // batch's rows in one change tracker while the later ones run.
                int requested;
                do
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var sweep = scope.ServiceProvider.GetRequiredService<InventoryRefreshSweepService>();
                    requested = await sweep.SweepAsync(BatchSize, stoppingToken);
                }
                while (requested == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Shutting down.
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the host down; try again next tick.
                _logger.LogError(ex, "Inventory refresh sweep failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
