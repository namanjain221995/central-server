using System.ComponentModel;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.SessionNotice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Restarts;

/// <summary>
/// Carries out restarts the server scheduled ahead of time, on this machine's
/// own clock, with or without a network.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the device holds the schedule.</b> A restart the server sends shortly
/// before the moment never reaches a laptop that dropped off the network in the
/// meantime. Armed here instead, the restart needs nothing from the server once
/// it is accepted: <see cref="WarningSeconds"/> before the moment this loop
/// hands Windows the countdown -- exactly what an ordinary restart task does --
/// and Windows restarts the machine.
/// </para>
/// <para>
/// <b>Durable.</b> Armed restarts are written to <see cref="IArmedRestartStore"/>
/// the moment they are accepted, so a service restart or a reboot before the
/// moment does not lose them. An entry is removed once its countdown has been
/// handed to Windows, or once it is skipped.
/// </para>
/// <para>
/// <b>Late is skipped, not caught up.</b> A machine that was off or asleep at
/// the moment and comes back within <see cref="LateTolerance"/> still restarts,
/// with the full warning. Later than that, the restart is dropped: a restart
/// hours after it was expected interrupts someone who has started working.
/// </para>
/// </remarks>
public sealed class RestartScheduler : BackgroundService
{
    /// <summary>How late a restart may still be carried out after a machine was off or asleep at the moment.</summary>
    public static readonly TimeSpan LateTolerance = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    /// <summary>A countdown that starts more than this after its intended start is "late": the user gets the full warning.</summary>
    private static readonly TimeSpan OnTimeSlack = TimeSpan.FromMinutes(1);

    private const int MinimumGraceSeconds = 30;
    private const int MaximumGraceSeconds = 3600;
    private const int ErrorShutdownInProgress = 1115;
    private const int ErrorShutdownIsScheduled = 1190;

    private readonly IArmedRestartStore _store;
    private readonly IDeviceControl _deviceControl;
    private readonly IRestartNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<RestartScheduler> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ArmedRestart>? _armed;

    public RestartScheduler(
        IArmedRestartStore store,
        IDeviceControl deviceControl,
        ILogger<RestartScheduler> logger,
        IRestartNotifier? notifier = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _deviceControl = deviceControl ?? throw new ArgumentNullException(nameof(deviceControl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifier = notifier ?? NullRestartNotifier.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Restarts currently armed, for diagnostics and tests.</summary>
    public async Task<IReadOnlyList<ArmedRestart>> ArmedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return [.. await EnsureLoadedAsync(cancellationToken)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Accepts a restart. Idempotent per task: a task delivered twice is armed
    /// once, with its first due time.
    /// </summary>
    public async Task ArmAsync(ArmedRestart restart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restart);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var armed = await EnsureLoadedAsync(cancellationToken);
            if (armed.Any(a => a.TaskId == restart.TaskId))
            {
                return;
            }

            armed.Add(restart);
            await _store.SaveAsync(armed, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogWarning(
            "Restart {TaskId} armed: the device restarts at {DueAt:u} with a {Warning}s warning, with or without the network.",
            restart.TaskId, restart.DueAt, restart.WarningSeconds);
    }

    /// <summary>Removes an armed restart before its countdown. False when it is not armed (already started, skipped, or never here).</summary>
    public async Task<bool> DisarmAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var armed = await EnsureLoadedAsync(cancellationToken);
            if (armed.RemoveAll(a => a.TaskId == taskId) == 0)
            {
                return false;
            }

            await _store.SaveAsync(armed, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogWarning("Restart {TaskId} disarmed by an authorized server task.", taskId);
        return true;
    }

    /// <summary>One pass: starts every countdown whose time has come and drops every restart too late to carry out.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var armed = await EnsureLoadedAsync(cancellationToken);
            var now = _time.GetUtcNow();
            var changed = false;

            foreach (var restart in armed.OrderBy(a => a.DueAt).ToList())
            {
                var startAt = restart.DueAt.AddSeconds(-restart.WarningSeconds);
                if (now < startAt)
                {
                    continue;
                }

                armed.Remove(restart);
                changed = true;

                if (now - restart.DueAt > LateTolerance)
                {
                    _logger.LogWarning(
                        "Restart {TaskId} skipped: it was due at {DueAt:u} and the machine was not running then; " +
                        "restarting {Late} late would interrupt someone who has started working.",
                        restart.TaskId, restart.DueAt, now - restart.DueAt);
                    continue;
                }

                await StartCountdownAsync(restart, startAt, now, cancellationToken);
            }

            if (changed)
            {
                await _store.SaveAsync(armed, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed tick must not stop the next one: an armed restart is
                // still owed, and the next pass is five seconds away.
                _logger.LogError(ex, "Scheduled restart pass failed; retrying.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task StartCountdownAsync(ArmedRestart restart, DateTimeOffset startAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // On time, the countdown is what is left until the moment. Late -- the
        // machine was asleep or off when it should have started -- the user
        // still gets the whole warning rather than a restart out of nowhere.
        var remaining = (int)Math.Ceiling((restart.DueAt - now).TotalSeconds);
        var grace = now - startAt > OnTimeSlack ? Math.Max(remaining, restart.WarningSeconds) : remaining;
        grace = Math.Clamp(grace, MinimumGraceSeconds, MaximumGraceSeconds);

        try
        {
            await _deviceControl.RestartAsync(grace, restart.Message, cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorShutdownInProgress or ErrorShutdownIsScheduled)
        {
            _logger.LogWarning("Restart {TaskId}: a restart or shutdown is already scheduled on this device; leaving it.", restart.TaskId);
            return;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Restart {TaskId}: Windows refused to schedule the restart.", restart.TaskId);
            return;
        }

        var restartAt = now.AddSeconds(grace);
        _logger.LogWarning(
            "Restart {TaskId}: countdown handed to Windows; the device restarts at {RestartAt:u} ({Grace}s).",
            restart.TaskId, restartAt, grace);

        try
        {
            _notifier.RestartScheduled(new RestartNotice(restartAt, grace));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restart {TaskId}: the session notice could not be sent.", restart.TaskId);
        }
    }

    private async Task<List<ArmedRestart>> EnsureLoadedAsync(CancellationToken cancellationToken) =>
        _armed ??= [.. await _store.LoadAsync(cancellationToken)];

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
