using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.ChromeProfiles;

/// <summary>
/// Carries out Chrome profile removals that were deferred because Chrome was
/// open: on service start and then every <see cref="CheckInterval"/>, each
/// pending request is re-run through the same engine the live task runs
/// through, and it completes the moment Chrome is found closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the device holds the intent.</b> On a busy laptop Chrome is open all
/// day, so a removal that refuses while Chrome runs turns into an operator
/// retrying at the right moment. Deferred here instead, the operator clicks
/// once: the request is persisted (<see cref="IPendingChromeRemovalStore"/>),
/// survives service restarts and reboots, and fires at the first check that
/// finds Chrome closed for that user.
/// </para>
/// <para>
/// <b>The same gates, every time.</b> Each attempt is a full
/// <see cref="ChromeProfileRemovalEngine"/> run, not a replay of a decision
/// taken earlier: the path and account checks run again against the device as
/// it is NOW. A request the world has moved under -- the profile's account
/// changed hands, the user profile is gone -- comes back as a hard refusal and
/// is dropped with a warning rather than retried; a profile already removed
/// (by a re-sent task, or a partial run this worker is finishing) is the
/// engine's idempotent success and the entry is simply done.
/// </para>
/// <para>
/// <b>Bounded intent.</b> An entry older than <see cref="MaxPendingAge"/> is
/// dropped with a log line before any attempt: an operator's intent from last
/// week must not fire into whatever the machine looks like today. Within that
/// window, an attempt that cannot conclude (Chrome still open; a directory
/// delete that failed after the list was rewritten) leaves the entry pending
/// for the next pass -- the engine finishes a partial run idempotently.
/// </para>
/// </remarks>
public sealed class ChromeProfileRemovalWorker : BackgroundService
{
    /// <summary>How often pending removals look for their moment. Chrome closing is caught within this.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(2);

    /// <summary>How long a deferred removal stays worth doing. Stale intent must not fire weeks later.</summary>
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromDays(7);

    private readonly IPendingChromeRemovalStore _store;
    private readonly ChromeProfileRemovalEngine _engine;
    private readonly ILogger<ChromeProfileRemovalWorker> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PendingChromeProfileRemoval>? _pending;

    public ChromeProfileRemovalWorker(
        IPendingChromeRemovalStore store,
        ChromeProfileRemovalEngine engine,
        ILogger<ChromeProfileRemovalWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Removals currently pending, for diagnostics and tests.</summary>
    public async Task<IReadOnlyList<PendingChromeProfileRemoval>> PendingAsync(CancellationToken cancellationToken = default)
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
    /// Accepts a deferred removal. One entry per profile per user: a request for
    /// a target already pending replaces it, so <c>requestedAt</c> (and the task
    /// the completion will be correlated to) reflect the operator's latest click
    /// and the seven-day window restarts.
    /// </summary>
    public async Task ScheduleAsync(PendingChromeProfileRemoval removal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removal);

        bool requeued;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = await EnsureLoadedAsync(cancellationToken);
            requeued = pending.RemoveAll(removal.SameTarget) > 0;
            pending.Add(removal);
            await _store.SaveAsync(pending, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation(
            "Chrome profile removal task {TaskId} deferred: profile '{ProfileKey}' of {UserSid} is "
            + "{Queueing} for removal the moment Chrome closes for that user, or at the next service start.",
            removal.TaskId, removal.ProfileKey, removal.UserSid, requeued ? "re-queued" : "queued");
    }

    /// <summary>
    /// One pass over everything pending: drops what has gone stale, completes
    /// what can complete, and leaves the rest for the next pass. A no-op when
    /// nothing is pending -- the quiet, usual case.
    /// </summary>
    public async Task RunPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = await EnsureLoadedAsync(cancellationToken);
            if (pending.Count == 0)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var changed = false;

            foreach (var entry in pending.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (now - entry.RequestedAt > MaxPendingAge)
                {
                    pending.Remove(entry);
                    changed = true;
                    _logger.LogWarning(
                        "Chrome profile removal task {TaskId} dropped: profile '{ProfileKey}' of {UserSid} was "
                        + "requested at {RequestedAt:u} and has waited longer than {MaxAge}; a stale intent is "
                        + "not carried out. Ask again from the console if the profile should still go.",
                        entry.TaskId, entry.ProfileKey, entry.UserSid, entry.RequestedAt, MaxPendingAge);
                    continue;
                }

                ChromeProfileRemovalOutcome outcome;
                try
                {
                    outcome = await _engine.RunAsync(entry.TaskId, entry.ToPayload(), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The engine answers for expected failures itself; anything that
                    // escapes it is a machine-level surprise. The entry stays for the
                    // next pass, bounded by the seven-day window.
                    _logger.LogError(ex,
                        "Chrome profile removal task {TaskId}: the deferred attempt for profile '{ProfileKey}' "
                        + "failed unexpectedly; it stays pending for the next pass.",
                        entry.TaskId, entry.ProfileKey);
                    continue;
                }

                switch (outcome.Disposition)
                {
                    case ChromeProfileRemovalDisposition.Removed:
                        pending.Remove(entry);
                        changed = true;
                        _logger.LogInformation(
                            "Chrome profile removal task {TaskId} completed deferred (requested {RequestedAt:u}): {Message}",
                            entry.TaskId, entry.RequestedAt, outcome.Message);
                        break;

                    case ChromeProfileRemovalDisposition.Refused:
                        // The world changed under the request -- the account no longer
                        // matches the console's view, the user or the path is gone.
                        // A stale intent is dropped, never retried into place.
                        pending.Remove(entry);
                        changed = true;
                        _logger.LogWarning(
                            "Chrome profile removal task {TaskId} dropped: the deferred request for profile "
                            + "'{ProfileKey}' of {UserSid} no longer holds. {Reason}",
                            entry.TaskId, entry.ProfileKey, entry.UserSid, outcome.Message);
                        break;

                    case ChromeProfileRemovalDisposition.PartiallyRemoved:
                        // The list is clean, the directory survived its delete. The
                        // engine finishes a partial run idempotently, so the entry
                        // stays for the next pass (and the seven-day bound still holds).
                        _logger.LogWarning(
                            "Chrome profile removal task {TaskId}: the deferred removal is partial and stays "
                            + "pending for the next pass. {Detail}",
                            entry.TaskId, outcome.Message);
                        break;

                    case ChromeProfileRemovalDisposition.ChromeRunning:
                    default:
                        // Still waiting for its moment. Debug, not Information: with
                        // Chrome open all day this line would otherwise recur every
                        // two minutes per entry; the terminal outcomes above are the
                        // ones worth the operational log.
                        _logger.LogDebug(
                            "Chrome profile removal task {TaskId}: Chrome is still running for {UserSid}; "
                            + "profile '{ProfileKey}' stays pending.",
                            entry.TaskId, entry.UserSid, entry.ProfileKey);
                        break;
                }
            }

            if (changed)
            {
                await _store.SaveAsync(pending, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass runs NOW, before the first timer tick: the worker is
        // hosted before the task loop, so after a reboot the deferred removal
        // usually completes before the user has reopened Chrome. "Usually":
        // Chrome set to launch at sign-in can still win this race on a fast
        // sign-in, and that is fine -- the entry stays pending and the next
        // closed moment (or the next start) catches it.
        using var timer = new PeriodicTimer(CheckInterval, _time);
        do
        {
            try
            {
                await RunPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed pass must not stop the next one: a pending removal is
                // still owed its moment, and the next pass is two minutes away.
                _logger.LogError(ex, "Deferred Chrome profile removal pass failed; retrying.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private async Task<List<PendingChromeProfileRemoval>> EnsureLoadedAsync(CancellationToken cancellationToken) =>
        _pending ??= [.. await _store.LoadAsync(cancellationToken)];
}
