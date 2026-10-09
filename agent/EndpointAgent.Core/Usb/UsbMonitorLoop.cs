using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Usb;

/// <summary>
/// The agent's USB loop: enforce on arrival, report to the server, and keep
/// reconciling on a timer.
/// </summary>
/// <remarks>
/// <para>
/// The order inside a cycle is deliberate and is the whole safety argument.
/// <b>Enforcement runs before reporting, always.</b> A newly attached storage
/// device or phone is restricted using the policy already on disk before the
/// server is told anything, so access is never waiting on a network round trip.
/// The report then goes out and the response — the authoritative grant set — is
/// applied, which is what turns an approved device read-only or enabled.
/// </para>
/// <para>
/// A device the administrator has already approved therefore goes
/// restricted-then-read-only within a cycle, rather than being writable for the
/// gap. The user sees a drive that takes a moment to appear; they never see one
/// they could have written to.
/// </para>
/// <para>
/// The periodic reconcile is not a fallback bolted on — it is what makes grant
/// expiry work offline. Every tick re-evaluates deadlines against the local
/// clock, and the loop wakes for the earliest deadline specifically, so a grant
/// lapses on schedule on a laptop that has not reached the server in days.
/// </para>
/// </remarks>
public sealed class UsbMonitorLoop(
    UsbPolicyManager policyManager,
    IUsbDeviceWatcher watcher,
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    TimeProvider timeProvider,
    ILogger<UsbMonitorLoop> logger)
{
    /// <summary>
    /// How often to re-evaluate without an external trigger. The safety net
    /// under device notifications: a watcher that never fires still bounds how
    /// long a newly attached device can go unrestricted to this.
    /// </summary>
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);

    /// <summary>Debounce window. A single insertion raises several PnP events.</summary>
    public static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(2);

    /// <summary>The shortest wait, so a deadline that has just passed is acted on promptly rather than spun on.</summary>
    internal static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    private readonly UsbPolicyManager _policyManager = policyManager
        ?? throw new ArgumentNullException(nameof(policyManager));

    private readonly IUsbDeviceWatcher _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));

    private readonly IAgentApiClient _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));

    private readonly IDeviceCredentialStore _credentialStore = credentialStore
        ?? throw new ArgumentNullException(nameof(credentialStore));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<UsbMonitorLoop> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly SemaphoreSlim _wake = new(0, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _watcher.Changed += OnDeviceChanged;

        if (!_watcher.TryStart())
        {
            _logger.LogWarning(
                "USB change notifications are unavailable. Policy is still enforced, but a newly attached "
                + "device may remain unrestricted for up to {Interval}.",
                ReconcileInterval);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RunCycleAsync(cancellationToken);
                await WaitForNextCycleAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _watcher.Changed -= OnDeviceChanged;
            _watcher.Dispose();
        }
    }

    /// <summary>
    /// Stands enforcement down, returning every device this agent touched to
    /// normal Windows behaviour. The policy is kept for the next start.
    /// </summary>
    public Task<UsbReleaseOutcome> ReleaseEnforcementAsync(CancellationToken cancellationToken = default) =>
        _policyManager.ReleaseAllAsync(cancellationToken);

    /// <summary>One enforce-then-report-then-apply cycle.</summary>
    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 1. Enforce from what we already know. This is the step that must
            //    not depend on the network.
            var enforced = await _policyManager.ReconcileAsync(cancellationToken);

            if (enforced.Total > 0)
            {
                _logger.LogDebug(
                    "USB reconcile: {Enabled} read/write, {ReadOnly} read-only, {Restricted} restricted, "
                    + "{Failed} failed.",
                    enforced.Enabled, enforced.ReadOnly, enforced.Restricted, enforced.Failed);
            }

            // 2. Tell the server what is attached and what we are enforcing.
            var credential = await _credentialStore.LoadAsync(cancellationToken);
            if (credential is null)
            {
                // Not enrolled yet. Devices are still restricted by step 1 —
                // an unenrolled machine is the strictest state, not the loosest.
                return;
            }

            var report = _policyManager.BuildReport();
            var response = await _apiClient.ReportUsbAsync(report, credential, cancellationToken);

            if (!response.IsSuccess || response.Value is null)
            {
                _logger.LogDebug(
                    "USB report was not accepted ({Status}); the cached policy stays in force.",
                    response.Status);
                return;
            }

            // 3. Apply the authoritative policy. Parsing is defensive: an entry
            //    that is malformed or already expired is dropped, so the only
            //    thing that can widen access is a well-formed, in-date grant.
            var grants = new List<UsbGrantRecord>();
            foreach (var grant in response.Value.Grants ?? [])
            {
                // Same two-name rule as the pushed task, and for the same
                // reason: this is the other channel by which a policy can reach
                // the endpoint, so a value the task path would reject must not
                // be accepted here.
                if (string.IsNullOrWhiteSpace(grant.InstanceId)
                    || grant.ExpiresAt <= _timeProvider.GetUtcNow())
                {
                    continue;
                }

                UsbEnforcedState policy;
                if (string.Equals(grant.Policy, nameof(UsbEnforcedState.ReadOnly), StringComparison.OrdinalIgnoreCase))
                {
                    policy = UsbEnforcedState.ReadOnly;
                }
                else if (string.Equals(grant.Policy, nameof(UsbEnforcedState.Enabled), StringComparison.OrdinalIgnoreCase))
                {
                    policy = UsbEnforcedState.Enabled;
                }
                else
                {
                    continue;
                }

                grants.Add(new UsbGrantRecord(grant.InstanceId, grant.ExpiresAt, policy));
            }

            await _policyManager.ApplyPolicyAsync(grants, response.Value.IssuedAt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed cycle must not stop the loop. The next one retries, and
            // nothing here can leave a device more accessible than it was.
            _logger.LogError(ex, "A USB monitor cycle failed; the next cycle will retry.");
        }
    }

    /// <summary>
    /// How long to wait before the next unprompted cycle: the reconcile interval,
    /// or less when a grant lapses sooner.
    /// </summary>
    /// <remarks>
    /// Waking for the deadline itself is what makes "access ends at 14:00" mean
    /// 14:00 rather than "some time before 14:01". Clamped below so a deadline
    /// that has already passed is acted on after a short pause rather than
    /// spinning the loop.
    /// </remarks>
    internal static TimeSpan NextWait(DateTimeOffset? nextGrantExpiry, DateTimeOffset now)
    {
        if (nextGrantExpiry is not { } expiry)
        {
            return ReconcileInterval;
        }

        var untilExpiry = expiry - now;

        if (untilExpiry >= ReconcileInterval)
        {
            return ReconcileInterval;
        }

        return untilExpiry < MinimumWait ? MinimumWait : untilExpiry;
    }

    /// <summary>
    /// Waits for the interval, the next grant deadline, or a device change,
    /// whichever comes first, then settles for the debounce window.
    /// </summary>
    /// <remarks>
    /// Plugging one stick in raises creation events for the device, its
    /// interfaces and its disk. Without the debounce each would start its own
    /// cycle, and several concurrent cycles would fight over the same device's
    /// state while the enumeration is still settling.
    /// </remarks>
    private async Task WaitForNextCycleAsync(CancellationToken cancellationToken)
    {
        var wait = NextWait(_policyManager.NextGrantExpiry, _timeProvider.GetUtcNow());
        var woken = await _wake.WaitAsync(wait, cancellationToken);

        if (woken)
        {
            await Task.Delay(ChangeDebounce, _timeProvider, cancellationToken);

            // Drain a change that arrived during the debounce: it is already
            // covered by the cycle about to run.
            if (_wake.CurrentCount > 0)
            {
                await _wake.WaitAsync(TimeSpan.Zero, cancellationToken);
            }
        }
    }

    private void OnDeviceChanged(object? sender, UsbChangeKind kind)
    {
        _logger.LogDebug("USB device {Kind}.", kind);

        try
        {
            // Capacity 1: a second signal before the loop wakes is redundant, and
            // Release would throw rather than queue it.
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Raced with the loop. The pending wake already covers this change.
        }
    }
}
