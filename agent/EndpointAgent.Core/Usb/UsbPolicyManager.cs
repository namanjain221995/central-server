using System.Collections.Concurrent;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Usb;

/// <summary>
/// Keeps this machine's USB storage and portable devices in the state the
/// platform says they should be in, and tells the server what it actually
/// managed to do.
/// </summary>
/// <remarks>
/// <para>
/// One rule governs everything here: <b>a restrictable device with no live grant
/// is restricted.</b> Not "restricted once the server says so" — restricted by
/// default, including on a machine that has never enrolled, cannot reach the
/// network, or has just booted with a stick or a phone already in the port.
/// Access is the exception that requires a positive, unexpired,
/// administrator-issued grant for that exact device instance ID.
/// </para>
/// <para>
/// Every failure path therefore lands on Restricted. An unreadable grant store,
/// an unreachable server, an expired cache, a task that never arrived, a
/// malformed payload: all of them mean "no live grant", which means restricted.
/// The only way to widen access is for <see cref="ApplyPolicyAsync"/> to receive
/// a grant that is genuinely still in date.
/// </para>
/// <para>
/// Restrictable means <see cref="UsbClass.Storage"/> or
/// <see cref="UsbClass.PortableDevice"/> — a phone in file-transfer mode is a
/// writable disk in everything but name. Every other class is inventoried and
/// never touched. Disabling a USB keyboard or mouse would lock the user out of
/// their own machine, and no security benefit would justify it.
/// </para>
/// </remarks>
public sealed class UsbPolicyManager(
    IUsbDeviceEnumerator enumerator,
    IUsbPolicyEnforcer enforcer,
    IUsbGrantStore grantStore,
    IUsbRestrictionLedger restrictionLedger,
    TimeProvider timeProvider,
    ILogger<UsbPolicyManager> logger)
{
    /// <summary>
    /// Why a read-only grant on a portable device ends in Restricted rather than
    /// in access. Reported verbatim so the console can show the reason beside
    /// the device.
    /// </summary>
    public const string ReadOnlyUnavailableForPortableDevices =
        UsbEnforcementMessages.ReadOnlyUnavailableForPortableDevices;

    private readonly IUsbDeviceEnumerator _enumerator = enumerator
        ?? throw new ArgumentNullException(nameof(enumerator));

    private readonly IUsbPolicyEnforcer _enforcer = enforcer
        ?? throw new ArgumentNullException(nameof(enforcer));

    private readonly IUsbGrantStore _grantStore = grantStore
        ?? throw new ArgumentNullException(nameof(grantStore));

    private readonly IUsbRestrictionLedger _restrictionLedger = restrictionLedger
        ?? throw new ArgumentNullException(nameof(restrictionLedger));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<UsbPolicyManager> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Serialises the read-modify-enforce path so two reconciles cannot fight
    /// over the same device's state.
    /// </summary>
    /// <remarks>
    /// Genuinely contended: <see cref="ApplyPolicyAsync"/> is called by the task
    /// executor on the heartbeat loop's thread, while <see cref="ReconcileAsync"/>
    /// is called by the USB monitor loop on its own.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Grants currently believed live. Replaced wholesale, never mutated, so a
    /// reader outside the gate always sees one coherent set rather than a
    /// half-updated one.
    /// </summary>
    private volatile UsbGrantSet _grants = UsbGrantSet.Empty;

    private bool _loaded;

    /// <summary>
    /// What the last enforcement attempt achieved, keyed by instance ID.
    /// </summary>
    /// <remarks>
    /// Concurrent because <see cref="BuildReport"/> reads it without taking the
    /// gate — it must stay cheap and must never block the reporting path behind
    /// an in-flight enforcement, which can spend seconds waiting for a disk to
    /// appear.
    /// </remarks>
    private readonly ConcurrentDictionary<string, UsbEnforcementRecord> _lastResult =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Device instances this agent currently has state applied to, mirroring the
    /// persisted ledger.
    /// </summary>
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The earliest deadline among live grants, or null when none is live. The
    /// monitor loop wakes for it, so a grant lapses on time rather than at the
    /// next scheduled sweep.
    /// </summary>
    public DateTimeOffset? NextGrantExpiry
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            DateTimeOffset? next = null;

            foreach (var grant in _grants.Grants)
            {
                if (grant.ExpiresAt > now && (next is null || grant.ExpiresAt < next))
                {
                    next = grant.ExpiresAt;
                }
            }

            return next;
        }
    }

    /// <summary>
    /// Releases every device this agent has applied state to, leaving the machine
    /// as an unmanaged Windows box would have it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called when the agent stops enforcing — service shutdown, and uninstall.
    /// The <em>grant store is deliberately left intact</em>: the policy is still
    /// the administrator's decision, it is merely not being enforced while nothing
    /// is running to enforce it. That is what lets a restart pick the policy back
    /// up without a round trip to the server.
    /// </para>
    /// <para>
    /// A device that fails to release stays in the ledger, so the next shutdown or
    /// the uninstaller gets another attempt rather than silently abandoning it.
    /// </para>
    /// </remarks>
    /// <returns>How many devices were released, and how many could not be.</returns>
    public async Task<UsbReleaseOutcome> ReleaseAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = await LoadLedgerAsync(cancellationToken);

            if (pending.Count == 0)
            {
                return new UsbReleaseOutcome(0, 0);
            }

            var released = 0;
            var failed = new List<string>();

            foreach (var instanceId in pending)
            {
                UsbEnforcementResult result;
                try
                {
                    result = _enforcer.Release(instanceId);
                }
                catch (Exception ex)
                {
                    result = UsbEnforcementResult.Failed(ex.Message);
                }

                if (result.Succeeded)
                {
                    released++;
                    _lastResult.TryRemove(instanceId, out _);
                }
                else
                {
                    failed.Add(instanceId);
                    _logger.LogError(
                        "Could not release USB device {InstanceId}: {Error}. It stays on the release list so "
                        + "the next shutdown or the uninstaller can try again.",
                        instanceId, result.Error);
                }
            }

            _touched.Clear();
            foreach (var instanceId in failed)
            {
                _touched.Add(instanceId);
            }

            await SaveLedgerAsync(cancellationToken);

            _logger.LogInformation(
                "USB enforcement released: {Released} device(s) returned to normal Windows behaviour, "
                + "{Failed} could not be released.",
                released, failed.Count);

            return new UsbReleaseOutcome(released, failed.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<IReadOnlyCollection<string>> LoadLedgerAsync(CancellationToken cancellationToken)
    {
        if (_touched.Count > 0)
        {
            return _touched.ToList();
        }

        try
        {
            foreach (var instanceId in await _restrictionLedger.LoadAsync(cancellationToken))
            {
                _touched.Add(instanceId);
            }
        }
        catch (Exception ex)
        {
            // Nothing can be released that we cannot name. Loud, because the
            // consequence is a device left disabled.
            _logger.LogError(ex, "Could not read the USB release list; devices may stay restricted.");
        }

        return _touched.ToList();
    }

    private async ValueTask SaveLedgerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _restrictionLedger.SaveAsync(_touched.ToList(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Could not persist the USB release list. Enforcement still applies, but a device may "
                + "need re-enabling by hand if the agent is uninstalled before this succeeds.");
        }
    }

    /// <summary>
    /// Replaces the cached policy with one the server issued, then reconciles.
    /// </summary>
    /// <remarks>
    /// Older policies are ignored by <paramref name="issuedAt"/>. Without that, a
    /// task queued before a revocation but delivered after it — entirely possible
    /// for a machine that was offline — would reinstate the access that was
    /// revoked. Rejecting stale policies makes late delivery harmless.
    /// </remarks>
    /// <returns>The devices whose state changed, for logging.</returns>
    public async Task<UsbReconcileOutcome> ApplyPolicyAsync(
        IReadOnlyList<UsbGrantRecord> grants,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);

            if (issuedAt < _grants.IssuedAt)
            {
                _logger.LogInformation(
                    "Ignoring a USB policy issued {IssuedAt} because a newer one issued {Current} is already "
                    + "in force. A late policy must not reinstate access that has since been revoked.",
                    issuedAt, _grants.IssuedAt);

                return await ReconcileLockedAsync(cancellationToken);
            }

            _grants = new UsbGrantSet(grants, issuedAt);
            await _grantStore.SaveAsync(_grants, cancellationToken);

            return await ReconcileLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings every attached restrictable device into line with the cached policy.
    /// </summary>
    /// <remarks>
    /// Safe and cheap to call as often as needed — on device arrival, on a timer,
    /// at startup, after a policy push. Enforcement calls are idempotent, so
    /// reconciling a machine that is already correct changes nothing.
    /// </remarks>
    public async Task<UsbReconcileOutcome> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return await ReconcileLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the report for the server: every attached device, plus what this
    /// agent has actually done about each.
    /// </summary>
    /// <remarks>
    /// Reports the state last <em>applied</em>, with how far the enforcer got in
    /// verifying it — never the state the policy asks for. A restrictable device
    /// that has not been through a reconcile yet reports no state at all, so the
    /// console shows it as not yet confirmed rather than as protected.
    /// </remarks>
    public UsbReport BuildReport()
    {
        var now = _timeProvider.GetUtcNow();
        var devices = SafeEnumerate();

        var entries = devices.Select(device =>
        {
            string? enforced = null;
            string? error = null;
            string? status = null;

            if (device.Class.IsRestrictable() && _lastResult.TryGetValue(device.InstanceId, out var record))
            {
                enforced = record.Applied?.ToString();
                error = record.Error;
                status = record.Status.ToString();
            }

            return new UsbDeviceReport(
                device.InstanceId,
                device.Class.ToString(),
                device.VendorId,
                device.ProductId,
                device.SerialNumber,
                device.Manufacturer,
                device.Product,
                device.HardwareIds,
                IsConnected: true,
                enforced,
                error,
                status);
        }).ToList();

        return new UsbReport(entries, now);
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            _grants = await _grantStore.LoadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Fail closed and say so. An unreadable cache is not a reason to let
            // everything through; it is a reason to restrict everything.
            _logger.LogError(
                ex, "Could not load the cached USB policy. Treating every storage and portable device as restricted.");
            _grants = UsbGrantSet.Empty;
        }

        _loaded = true;
    }

    private async Task<UsbReconcileOutcome> ReconcileLockedAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var devices = SafeEnumerate();

        var restricted = 0;
        var readOnly = 0;
        var enabled = 0;
        var failed = 0;

        // Anything already on the ledger from a previous run stays on it until it
        // is released: a device restricted before the last restart is still
        // restricted now, whether or not it is currently attached.
        await LoadLedgerAsync(cancellationToken);
        var ledgerBefore = _touched.Count;

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!device.Class.IsRestrictable())
            {
                continue;
            }

            var desired = Desired(device.InstanceId, now);

            // A read-only grant on a device that cannot be read-only is not
            // widened to read/write and not honoured as read-only: it is treated
            // as no grant. The device stays restricted and the gap is reported,
            // so the administrator sees why their decision is not in force.
            var readOnlyUnavailable = desired == UsbEnforcedState.ReadOnly && !device.Class.SupportsReadOnly();
            var target = readOnlyUnavailable ? UsbEnforcedState.Restricted : desired;

            UsbEnforcementResult result;
            try
            {
                result = target switch
                {
                    UsbEnforcedState.Enabled => _enforcer.AllowReadWrite(device.InstanceId),
                    UsbEnforcedState.ReadOnly => _enforcer.AllowReadOnly(device.InstanceId),

                    // Restricted, and anything this agent does not recognise.
                    // An unknown state is not a reason to guess upwards.
                    _ => _enforcer.Restrict(device.InstanceId),
                };
            }
            catch (Exception ex)
            {
                result = UsbEnforcementResult.Failed(ex.Message);
            }

            // Recorded on the attempt, not on success. A Restrict that reports
            // failure may still have partially applied — and a device wrongly on
            // the release list costs one redundant re-enable, while a device
            // wrongly absent from it stays disabled after uninstall.
            _touched.Add(device.InstanceId);

            if (!result.Succeeded)
            {
                _lastResult[device.InstanceId] = new UsbEnforcementRecord(null, result.Error, result.Status);
                failed++;
                _logger.LogError(
                    "Could not apply {Target} to USB device {InstanceId} ({Status}): {Error}",
                    target, device.InstanceId, result.Status, result.Error);
                continue;
            }

            if (readOnlyUnavailable)
            {
                _lastResult[device.InstanceId] = new UsbEnforcementRecord(
                    UsbEnforcedState.Restricted, ReadOnlyUnavailableForPortableDevices, UsbEnforcementStatus.Failed);
                failed++;
                _logger.LogWarning(
                    "A read-only grant names {InstanceId}, which is a {Class} and cannot be read-only; it stays "
                    + "restricted.",
                    device.InstanceId, device.Class);
                continue;
            }

            _lastResult[device.InstanceId] = new UsbEnforcementRecord(target, result.Error, result.Status);

            switch (target)
            {
                case UsbEnforcedState.ReadOnly:
                    readOnly++;
                    break;
                case UsbEnforcedState.Enabled:
                    enabled++;
                    break;
                default:
                    restricted++;
                    break;
            }
        }

        // Grants for devices that are no longer attached are dropped once they
        // expire; keeping the set trimmed stops the cache growing without bound
        // on a machine that sees many devices.
        PruneExpiredGrants(now);

        if (_touched.Count != ledgerBefore)
        {
            await SaveLedgerAsync(cancellationToken);
        }

        return new UsbReconcileOutcome(restricted, readOnly, enabled, failed);
    }

    /// <summary>
    /// The heart of the security model: a device gets the level named by a
    /// grant for that exact instance which has not lapsed, and Restricted
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// A grant carrying Restricted is ignored rather than honoured. Restricted
    /// is the absence of a grant, so an entry claiming to grant it is
    /// malformed — and treating it as "no grant" is the same answer this method
    /// gives for every other malformed case.
    /// </remarks>
    private UsbEnforcedState Desired(string instanceId, DateTimeOffset now)
    {
        foreach (var grant in _grants.Grants)
        {
            if (string.Equals(grant.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)
                && grant.ExpiresAt > now
                && grant.Policy is UsbEnforcedState.ReadOnly or UsbEnforcedState.Enabled)
            {
                return grant.Policy;
            }
        }

        return UsbEnforcedState.Restricted;
    }

    private void PruneExpiredGrants(DateTimeOffset now)
    {
        var live = _grants.Grants.Where(g => g.ExpiresAt > now).ToList();

        if (live.Count != _grants.Grants.Count)
        {
            _logger.LogInformation(
                "{Count} USB grant(s) expired and the affected device(s) are now restricted.",
                _grants.Grants.Count - live.Count);

            _grants = _grants with { Grants = live };
        }
    }

    private IReadOnlyList<UsbDeviceInfo> SafeEnumerate()
    {
        try
        {
            return _enumerator.Enumerate();
        }
        catch (Exception ex)
        {
            // Enumeration failing means we cannot see devices to restrict them.
            // Nothing is opened by this — a device already restricted stays
            // disabled — but it must be loud, not silent.
            _logger.LogError(ex, "USB enumeration failed; no device state could be evaluated this round.");
            return [];
        }
    }

    /// <summary>What one reconcile left a device in, as reported to the server.</summary>
    /// <param name="Applied">
    /// The state in force on the device, or null when the attempt failed and the
    /// state is therefore unknown.
    /// </param>
    private sealed record UsbEnforcementRecord(UsbEnforcedState? Applied, string? Error, UsbEnforcementStatus Status);
}

/// <param name="Failed">Devices whose desired state could not be applied. Never hidden from the server.</param>
public sealed record UsbReconcileOutcome(int Restricted, int ReadOnly, int Enabled, int Failed)
{
    public int Total => Restricted + ReadOnly + Enabled + Failed;
}

/// <summary>Result of standing down enforcement.</summary>
/// <param name="Released">Devices returned to normal Windows behaviour.</param>
/// <param name="Failed">
/// Devices still carrying agent-applied state. These remain on the release list,
/// so a later shutdown or the uninstaller retries them.
/// </param>
public sealed record UsbReleaseOutcome(int Released, int Failed);
