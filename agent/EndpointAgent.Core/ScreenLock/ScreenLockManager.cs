using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.ScreenLock;

/// <summary>What the overlay shows: the administrator's message and optional contact line. Carries no verifier.</summary>
public sealed record LockScreenContent(string Message, string? ContactLine);

/// <summary>What a release did: whether the device was locked, and which lock was cleared.</summary>
public sealed record ReleaseOutcome(bool WasLocked, Guid? ReleasedLockId);

/// <summary>
/// The reversible machine lockdown a screen lock applies: disabling Task Manager
/// and neutralising the power and lid buttons so the overlay cannot be escaped or
/// the machine casually put to sleep, and -- the part that matters most -- putting
/// every one of those settings back exactly on release.
/// </summary>
/// <remarks>
/// <para>
/// Every change is reversible by construction: <see cref="Capture"/> reads the
/// current values and returns an opaque snapshot WITHOUT changing anything, and
/// <see cref="Restore"/> is later handed that snapshot to put them back. Core never
/// interprets the snapshot; only the Windows applier that wrote it does.
/// </para>
/// <para>
/// <b>Capture and apply are split so the lock can be made crash-safe.</b> The
/// manager captures and <em>persists</em> the snapshot before it applies anything,
/// so the machine is never hardened without a persisted snapshot that can undo it:
/// a crash between persist and apply leaves a device that relocks (and re-applies,
/// idempotently) on the next boot and can still be restored exactly on release.
/// </para>
/// </remarks>
public interface IScreenLockdown
{
    /// <summary>Reads the current machine settings and returns the snapshot to persist, WITHOUT changing anything.</summary>
    string? Capture();

    /// <summary>Applies (or re-applies) the lockdown. Idempotent -- safe on engage, on every watchdog tick, and after a reboot.</summary>
    void Apply();

    /// <summary>Restores the machine to the snapshot <see cref="Capture"/> returned. Idempotent; a null snapshot is a no-op.</summary>
    void Restore(string? snapshot);
}

/// <summary>
/// Keeps the lock overlay running in every signed-in session while the device is
/// locked, and takes them all down on release.
/// </summary>
/// <remarks>
/// The service, as LocalSystem, starts the overlay in each interactive session
/// the way Windows documents for exactly this (see the Windows implementation and
/// ADR-0005). The overlay is one fixed image with no arguments; it reads what to
/// show from the service over the pipe, never from a command line.
/// </remarks>
public interface IScreenLockLauncher
{
    /// <summary>Starts the overlay in every interactive session that does not have one. Best-effort; never throws.</summary>
    IReadOnlyList<uint> EnsureOverlayInAllSessions();

    /// <summary>Stops every overlay this device is running. Best-effort; never throws.</summary>
    void StopAllOverlays();
}

/// <summary>
/// What the passphrase pipe asks of the service: the plain text to show, whether
/// the device is still locked, and the one privileged operation -- verify a
/// recovery code and release on a match. The verifier never leaves the service.
/// </summary>
public interface IScreenLockPipeHandler
{
    /// <summary>Whether the device is holding a screen lock right now.</summary>
    bool CurrentlyLocked { get; }

    /// <summary>The words to show, or null when the device is not locked.</summary>
    LockScreenContent? CurrentContent { get; }

    /// <summary>
    /// Verifies a recovery code against the active lock and releases it on a match.
    /// False when the code is wrong or the device is not locked. The verification
    /// -- the PBKDF2 and the constant-time compare -- happens here, in SYSTEM, and
    /// nowhere else.
    /// </summary>
    Task<bool> VerifyAndReleaseAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the one screen lock this device may hold: engages it (persist, lock down,
/// show the overlay in every session), keeps it up across reboots and against a
/// killed overlay, and releases it -- restoring the machine exactly -- on a server
/// task or a correct on-device unlock code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways out, both ending in the machine put back.</b> A
/// <c>ReleaseScreenLock</c> task calls <see cref="ReleaseAsync"/>; an IT operator at
/// the device presses Ctrl+Alt+U and types the code, which the overlay sends over
/// the pipe to <see cref="VerifyAndReleaseAsync"/>. Both restore every setting the
/// lockdown changed before clearing the stored lock, so a crash between the two
/// leaves the device re-lockable rather than stranded with a half-undone lockdown.
/// </para>
/// <para>
/// <b>Durable and self-healing.</b> The lock is persisted the moment it is engaged,
/// so a reboot or a service restart comes back locked. A five-second tick
/// re-asserts the lockdown and relaunches the overlay in any session that lost it,
/// so killing the overlay process brings it straight back.
/// </para>
/// </remarks>
public sealed class ScreenLockManager : BackgroundService, IScreenLockPipeHandler
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly IScreenLockStore _store;
    private readonly IScreenLockLauncher _launcher;
    private readonly IScreenLockdown _lockdown;
    private readonly ILogger<ScreenLockManager> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Read without the gate by the heartbeat and the pipe; a reference and a bool
    // written under the gate and read volatilely give a consistent-enough snapshot.
    private volatile ScreenLockState? _current;
    private volatile bool _locked;

    public ScreenLockManager(
        IScreenLockStore store,
        IScreenLockLauncher launcher,
        IScreenLockdown lockdown,
        ILogger<ScreenLockManager> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _lockdown = lockdown ?? throw new ArgumentNullException(nameof(lockdown));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool CurrentlyLocked => _locked;

    /// <inheritdoc />
    public LockScreenContent? CurrentContent =>
        _current is { } state ? new LockScreenContent(state.Message, state.ContactLine) : null;

    /// <summary>
    /// Engages a lock: captures and applies the lockdown, persists the lock, and
    /// shows the overlay everywhere. Idempotent per lock, and safe when a lock is
    /// already up -- the first lockdown snapshot is kept, so release still restores
    /// the machine's true prior state rather than an already-locked-down one.
    /// </summary>
    public async Task EngageAsync(ScreenLockState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = _current ?? await _store.LoadAsync(cancellationToken);
            if (existing is not null && existing.LockId == state.LockId)
            {
                // The same lock delivered twice. Already engaged; just make sure
                // it is still showing.
                _current = existing;
                _locked = true;
            }
            else if (existing is not null)
            {
                // A different lock while one is already up. Do NOT recapture the
                // lockdown -- that would record the locked-down state as "prior" and
                // make release a no-op. Keep the first snapshot; adopt the new words
                // and verifier. Persist before re-applying, as below.
                var replaced = state with { PriorPolicy = existing.PriorPolicy };
                await _store.SaveAsync(replaced, cancellationToken);
                _current = replaced;
                _locked = true;
                _lockdown.Apply();
                _logger.LogWarning(
                    "Screen lock replaced on the device: lock {Old} gave way to {New} without re-capturing the lockdown.",
                    existing.LockId, state.LockId);
            }
            else
            {
                // Fresh lock, in a crash-safe order. Capture the machine's current
                // settings WITHOUT changing anything, PERSIST the lock (snapshot
                // included) FIRST, and only THEN harden the machine. So the machine is
                // never left hardened with no persisted snapshot to undo it: a crash
                // after the save relocks and re-applies (idempotent) on the next boot
                // and is still restorable; a throw from the save hardens nothing.
                var snapshot = _lockdown.Capture();
                var engaged = state with { PriorPolicy = snapshot };
                await _store.SaveAsync(engaged, cancellationToken);
                _current = engaged;
                _locked = true;
                _lockdown.Apply();
                _logger.LogWarning(
                    "Screen lock {LockId} engaged on the device; it survives reboot until released.", state.LockId);
            }
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate: starting processes in user sessions must not hold the
        // lock that a verify-and-release also needs.
        _launcher.EnsureOverlayInAllSessions();
    }

    /// <summary>
    /// Releases the lock the device is holding: restores every setting the lockdown
    /// changed, clears the stored lock, and takes down the overlay. Idempotent --
    /// releasing when nothing is locked succeeds and says so. The active lock is
    /// released whatever <paramref name="lockId"/> is: an authenticated release
    /// instruction must never leave a user locked out over an id mismatch.
    /// </summary>
    public async Task<ReleaseOutcome> ReleaseAsync(Guid lockId, CancellationToken cancellationToken = default)
    {
        ScreenLockState released;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var active = _current ?? await _store.LoadAsync(cancellationToken);
            if (active is null)
            {
                return new ReleaseOutcome(WasLocked: false, ReleasedLockId: null);
            }

            // Restore first, then clear: a crash in between re-locks on next boot
            // (recoverable), where clearing first could leave the lockdown applied
            // with no lock and no snapshot to undo it.
            _lockdown.Restore(active.PriorPolicy);
            await _store.SaveAsync(null, cancellationToken);
            _current = null;
            _locked = false;
            released = active;
        }
        finally
        {
            _gate.Release();
        }

        _launcher.StopAllOverlays();

        if (released.LockId != lockId)
        {
            _logger.LogWarning(
                "Screen lock released: the active lock {Active} was cleared by a release naming {Named}.",
                released.LockId, lockId);
        }
        else
        {
            _logger.LogWarning("Screen lock {LockId} released; the machine was restored.", released.LockId);
        }

        return new ReleaseOutcome(WasLocked: true, ReleasedLockId: released.LockId);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAndReleaseAsync(string code, CancellationToken cancellationToken = default)
    {
        var state = _current;
        if (state is null || !state.Verifier.Verify(code))
        {
            return false;
        }

        await ReleaseAsync(state.LockId, cancellationToken);
        return true;
    }

    /// <summary>One watchdog pass: re-assert the lockdown and relaunch the overlay wherever it is missing.</summary>
    public Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!_locked)
        {
            return Task.CompletedTask;
        }

        _lockdown.Apply();
        _launcher.EnsureOverlayInAllSessions();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-engages a lock the device was holding before anything else starts, so a
    /// device that rebooted while locked is locked again the instant the service is
    /// up -- not five seconds later when the first watchdog tick would run. Done in
    /// StartAsync rather than the background loop so the relock is complete by the
    /// time the host considers this service started.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RelockOnStartAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
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
                // A failed watchdog pass must not stop the next: a locked device is
                // still owed its overlay, and the next pass is five seconds away.
                _logger.LogError(ex, "Screen-lock watchdog pass failed; retrying.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>On start, re-engage a lock the device was holding before it stopped or rebooted.</summary>
    private async Task RelockOnStartAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken);
        try
        {
            var state = await _store.LoadAsync(stoppingToken);
            if (state is null)
            {
                return;
            }

            _current = state;
            _locked = true;

            // The registry side of the lockdown persists across a reboot on its own;
            // re-applying is belt-and-braces and idempotent, and puts the power
            // settings back under lock after a scheme change while the device was off.
            _lockdown.Apply();
            _logger.LogWarning(
                "Screen lock {LockId} re-engaged on start; the device was locked when it stopped or rebooted.",
                state.LockId);
        }
        finally
        {
            _gate.Release();
        }

        if (_locked)
        {
            _launcher.EnsureOverlayInAllSessions();
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
