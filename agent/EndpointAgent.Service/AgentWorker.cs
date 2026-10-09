using EndpointAgent.Core.Heartbeat;
using EndpointAgent.Core.Usb;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Service;

/// <summary>
/// Hosts the agent's main loops as the Windows service's background work.
/// </summary>
/// <remarks>
/// <para>
/// Thin by design: all behaviour lives in <see cref="HeartbeatLoop"/> and
/// <see cref="UsbMonitorLoop"/> (platform-neutral, unit-tested); this class only
/// adapts them to <see cref="BackgroundService"/> and makes sure a crash is
/// logged before the service host sees it.
/// </para>
/// <para>
/// The two loops run concurrently rather than interleaved. USB enforcement must
/// respond to a device being plugged in within seconds, and it must not be
/// delayed behind an inventory collection that takes considerably longer than
/// that. A fault in the USB loop is contained: it is logged and the loop
/// restarts, because losing USB enforcement is not a reason to stop
/// heartbeating, and a machine that stops heartbeating is one an administrator
/// can no longer act on at all.
/// </para>
/// </remarks>
public sealed class AgentWorker(
    HeartbeatLoop heartbeatLoop,
    UsbMonitorLoop usbMonitorLoop,
    ILogger<AgentWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long the release on shutdown may take before the service stops anyway.
    /// Generous enough for a device to re-enumerate, short enough that the SCM
    /// does not lose patience and kill us mid-release.
    /// </summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(20);

    private readonly HeartbeatLoop _heartbeatLoop = heartbeatLoop
        ?? throw new ArgumentNullException(nameof(heartbeatLoop));

    private readonly UsbMonitorLoop _usbMonitorLoop = usbMonitorLoop
        ?? throw new ArgumentNullException(nameof(usbMonitorLoop));

    private readonly ILogger<AgentWorker> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Endpoint agent worker starting (version {Version}).", _heartbeatLoop.AgentVersion);

        var usb = RunUsbLoopAsync(stoppingToken);

        try
        {
            await _heartbeatLoop.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // An unhandled exception here would stop the service silently; log it
            // as fatal and rethrow so the SCM records the failure and restarts us
            // per the service recovery policy.
            _logger.LogCritical(ex, "The agent worker crashed.");
            throw;
        }
        finally
        {
            await usb;
            await ReleaseUsbEnforcementAsync();
            _logger.LogInformation("Endpoint agent worker stopped.");
        }
    }

    /// <summary>
    /// Stands USB enforcement down so a stopped agent leaves an ordinary Windows
    /// machine behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enforcement is intentionally not durable. Disabling a devnode writes a
    /// registry flag that Windows honours indefinitely, so without this the
    /// product would keep controlling the machine while nothing was running to
    /// manage it — an administrator could not lift a restriction, because the
    /// agent that would receive the instruction is stopped. The policy itself is
    /// kept, so a restart re-establishes exactly what was in force.
    /// </para>
    /// <para>
    /// <b>The limit, stated plainly.</b> This runs on an orderly stop: a service
    /// stop, a restart, a reboot, an upgrade, or an uninstall. It cannot run if
    /// the process is killed outright, bugchecks, or loses power — Windows offers
    /// no way to make a SetupAPI disable revert when a user-mode process dies. In
    /// that case devices stay restricted, and stay on the release list, until the
    /// next orderly stop releases them; a start in between reapplies the current
    /// policy to them. The installer runs no release of its own: an uninstall or an
    /// upgrade releases devices only because it stops this service, so removing the
    /// product while the service is not running leaves them disabled, with the
    /// release list kept in the state directory. The manual way back is in
    /// docs/runbooks/usb-portable-devices-rollout.md.
    /// </para>
    /// <para>
    /// A fresh token, because the shutdown token is already cancelled by the time
    /// this runs. The timeout is what keeps a wedged release from holding the
    /// service host open until the SCM kills it — which would be the one outcome
    /// worse than a slow stop, since a killed process releases nothing.
    /// </para>
    /// </remarks>
    private async Task ReleaseUsbEnforcementAsync()
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);

        try
        {
            var outcome = await _usbMonitorLoop.ReleaseEnforcementAsync(timeout.Token);

            if (outcome.Released > 0 || outcome.Failed > 0)
            {
                _logger.LogInformation(
                    "USB enforcement stood down on stop: {Released} device(s) released, {Failed} could not be.",
                    outcome.Released, outcome.Failed);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogError(
                "Releasing USB enforcement did not finish within {Timeout}. Some devices may remain "
                + "restricted until the agent next starts.", ReleaseTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Releasing USB enforcement on shutdown failed.");
        }
    }

    /// <summary>
    /// Runs the USB loop, restarting it if it faults.
    /// </summary>
    /// <remarks>
    /// The restart matters for the security control. If this loop dies and stays
    /// dead, newly attached storage stops being restricted — silently, because
    /// the machine keeps heartbeating and looks healthy. Restarting on a short
    /// backoff means a transient fault (WMI hiccup, a driver returning an
    /// unexpected error) costs seconds of coverage rather than the rest of the
    /// service's lifetime.
    /// </remarks>
    private async Task RunUsbLoopAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _usbMonitorLoop.RunAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "The USB monitor loop faulted; restarting it in {Backoff}.", backoff);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
