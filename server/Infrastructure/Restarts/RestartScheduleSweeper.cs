using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Restarts;

/// <summary>
/// Sends the restart tasks for schedules whose moment has come.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place a scheduled restart turns into device tasks. It runs
/// in the Admin host, like every other management-plane job, resolves a scoped
/// <see cref="RestartScheduleService"/> per tick, and processes a bounded batch.
/// A failing tick is logged and retried on the next interval; the sweeper never
/// takes the host down.
/// </para>
/// <para>
/// The interval bounds how late a restart can go out: with the default 15
/// seconds and a 15-second agent heartbeat, a device has the task well inside
/// a minute of the dispatch moment, and Windows counts the rest. A tick that
/// finds a schedule already more than the configured tolerance late marks it
/// Missed rather than sending it.
/// </para>
/// </remarks>
public sealed class RestartScheduleSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<RestartScheduleOptions> options,
    ILogger<RestartScheduleSweeper> logger) : BackgroundService
{
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(options.Value.SweepIntervalSeconds);
    private readonly ILogger<RestartScheduleSweeper> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        do
        {
            try
            {
                int dispatched;
                do
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<RestartScheduleService>();
                    dispatched = await service.DispatchDueAsync(BatchSize, stoppingToken);
                }
                while (dispatched == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restart schedule sweep failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
