using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Usb;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// A phone is governed by the same rule as a stick: restricted unless a live
/// grant names it. What differs is that it has no read-only mode, and that the
/// agent now reports how far it got rather than only whether the call returned.
/// </summary>
public sealed class UsbPortableDeviceTests
{
    private const string PhoneId = @"USB\VID_2717&PID_FF40\EXAMPLE0SERIAL01";
    private const string StickId = @"USB\VID_0781&PID_5581\ABC123";
    private const string KeyboardId = @"USB\VID_046D&PID_C31C\5&12345&0&1";

    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static UsbDeviceInfo Phone(bool enabled = true) =>
        new(PhoneId, UsbClass.PortableDevice, "2717", "FF40", "EXAMPLE0SERIAL01", "Xiaomi",
            "Redmi Note 14 Pro 5G", @"USB\VID_2717&PID_FF40", enabled);

    private static UsbDeviceInfo Stick() =>
        new(StickId, UsbClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer", null, IsEnabled: true);

    private static UsbDeviceInfo Keyboard() =>
        new(KeyboardId, UsbClass.Keyboard, "046D", "C31C", null, "Logitech", "Keyboard", null, IsEnabled: true);

    private static (UsbPolicyManager Manager, Enforcer Enforcer, Ledger Ledger, TestClock Clock) Build(
        params UsbDeviceInfo[] devices)
    {
        var enforcer = new Enforcer();
        var ledger = new Ledger();
        var clock = new TestClock(Start);

        var manager = new UsbPolicyManager(
            new Enumerator(devices), enforcer, new Store(), ledger, clock, NullLogger<UsbPolicyManager>.Instance);

        return (manager, enforcer, ledger, clock);
    }

    // ---- the default --------------------------------------------------------

    [Fact]
    public async Task A_phone_with_no_grant_is_restricted()
    {
        var (manager, enforcer, _, _) = Build(Phone());

        var outcome = await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", PhoneId)]);
        outcome.Restricted.ShouldBe(1);
        outcome.Failed.ShouldBe(0);

        var entry = manager.BuildReport().Devices.Single();
        entry.DeviceClass.ShouldBe("PortableDevice");
        entry.EnforcedPolicy.ShouldBe("Restricted");
        entry.EnforcementStatus.ShouldBe("Verified");
        entry.EnforcementError.ShouldBeNull();
    }

    /// <summary>
    /// The phone found already attached when the agent starts — the "connected
    /// before the service started" case — is handled by the very first
    /// reconcile, with nothing from the server.
    /// </summary>
    [Fact]
    public async Task A_phone_attached_before_the_agent_started_is_restricted_on_the_first_reconcile()
    {
        var (manager, enforcer, ledger, _) = Build(Phone(), Stick(), Keyboard());

        await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", PhoneId), ("Restrict", StickId)], ignoreOrder: true);
        ledger.Saved.ShouldBe([PhoneId, StickId], ignoreOrder: true);
    }

    [Fact]
    public async Task A_restricted_phone_is_reconciled_again_rather_than_skipped()
    {
        var (manager, enforcer, _, _) = Build(Phone(enabled: false));

        await manager.ReconcileAsync();

        // Idempotent, and deliberately repeated: this is what catches a local
        // administrator re-enabling the phone in Device Manager.
        enforcer.Calls.ShouldBe([("Restrict", PhoneId)]);
    }

    // ---- grants -------------------------------------------------------------

    [Fact]
    public async Task A_read_write_grant_enables_the_phone_and_lapses_on_time()
    {
        var (manager, enforcer, _, clock) = Build(Phone());

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(PhoneId, Start.AddHours(1), UsbEnforcedState.Enabled)], Start);

        enforcer.Calls.ShouldBe([("AllowReadWrite", PhoneId)]);
        manager.BuildReport().Devices.Single().EnforcedPolicy.ShouldBe("Enabled");
        manager.NextGrantExpiry.ShouldBe(Start.AddHours(1));

        enforcer.Calls.Clear();
        clock.Advance(TimeSpan.FromMinutes(61));
        await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", PhoneId)]);
        manager.BuildReport().Devices.Single().EnforcedPolicy.ShouldBe("Restricted");
        manager.NextGrantExpiry.ShouldBeNull();
    }

    /// <summary>
    /// A read-only grant on a phone is neither widened to read/write nor
    /// pretended: the phone stays restricted and the gap is reported.
    /// </summary>
    /// <remarks>
    /// The server refuses to issue such a grant, so this is the agent's own
    /// guard for a server that predates portable devices, or a payload that
    /// was tampered with. Either way the device must not become accessible
    /// through a level that cannot be enforced on it.
    /// </remarks>
    [Fact]
    public async Task A_read_only_grant_on_a_phone_keeps_it_restricted_and_says_why()
    {
        var (manager, enforcer, _, _) = Build(Phone());

        var outcome = await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(PhoneId, Start.AddHours(1), UsbEnforcedState.ReadOnly)], Start);

        enforcer.Calls.ShouldBe([("Restrict", PhoneId)]);
        outcome.Failed.ShouldBe(1);
        outcome.ReadOnly.ShouldBe(0);

        var entry = manager.BuildReport().Devices.Single();
        entry.EnforcedPolicy.ShouldBe("Restricted");
        entry.EnforcementStatus.ShouldBe("Failed");
        entry.EnforcementError.ShouldBe(UsbPolicyManager.ReadOnlyUnavailableForPortableDevices);
    }

    [Fact]
    public async Task A_read_only_grant_on_a_stick_is_unaffected()
    {
        var (manager, enforcer, _, _) = Build(Stick(), Phone());

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(1), UsbEnforcedState.ReadOnly)], Start);

        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId), ("Restrict", PhoneId)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_grant_for_the_phone_does_not_leak_to_the_stick_or_vice_versa()
    {
        var (manager, enforcer, _, _) = Build(Stick(), Phone());

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(PhoneId, Start.AddHours(1), UsbEnforcedState.Enabled)], Start);

        enforcer.Calls.ShouldBe([("AllowReadWrite", PhoneId), ("Restrict", StickId)], ignoreOrder: true);
    }

    // ---- honesty about how far enforcement got ------------------------------

    /// <summary>
    /// "Windows accepted the disable for the next restart" is not enforced, and
    /// is reported as its own state rather than as success or as a generic
    /// failure, so the console can say what will fix it.
    /// </summary>
    [Fact]
    public async Task A_restriction_deferred_to_the_next_restart_is_reported_as_such()
    {
        var (manager, enforcer, _, _) = Build(Phone());
        enforcer.Respond = _ => UsbEnforcementResult.RestartRequired("a program is holding the device open");

        var outcome = await manager.ReconcileAsync();

        outcome.Failed.ShouldBe(1);
        outcome.Restricted.ShouldBe(0);

        var entry = manager.BuildReport().Devices.Single();
        entry.EnforcedPolicy.ShouldBeNull();
        entry.EnforcementStatus.ShouldBe("RequiresRestart");
        entry.EnforcementError.ShouldBe("a program is holding the device open");
    }

    [Fact]
    public async Task A_restriction_that_could_not_be_read_back_is_reported_unverified()
    {
        var (manager, enforcer, _, _) = Build(Phone());
        enforcer.Respond = _ => UsbEnforcementResult.Unverified("the device state could not be read back");

        var outcome = await manager.ReconcileAsync();

        outcome.Restricted.ShouldBe(1);

        var entry = manager.BuildReport().Devices.Single();
        entry.EnforcedPolicy.ShouldBe("Restricted");
        entry.EnforcementStatus.ShouldBe("Unverified");
        entry.EnforcementError.ShouldBe("the device state could not be read back");
    }

    [Fact]
    public async Task A_failed_restriction_reports_no_state_and_the_reason()
    {
        var (manager, enforcer, _, _) = Build(Phone());
        enforcer.Respond = _ => UsbEnforcementResult.Failed("access denied");

        await manager.ReconcileAsync();

        var entry = manager.BuildReport().Devices.Single();
        entry.EnforcedPolicy.ShouldBeNull();
        entry.EnforcementStatus.ShouldBe("Failed");
        entry.EnforcementError.ShouldBe("access denied");
    }

    /// <summary>
    /// A device that has not been through a reconcile yet is reported with no
    /// state at all — never as restricted on the strength of what the policy
    /// says it should be.
    /// </summary>
    [Fact]
    public async Task A_device_not_yet_reconciled_reports_no_enforcement_state()
    {
        var (manager, _, _, _) = Build(Phone());

        var entry = manager.BuildReport().Devices.Single();

        entry.EnforcedPolicy.ShouldBeNull();
        entry.EnforcementStatus.ShouldBeNull();
        entry.EnforcementError.ShouldBeNull();

        await manager.ReconcileAsync();
        manager.BuildReport().Devices.Single().EnforcedPolicy.ShouldBe("Restricted");
    }

    [Fact]
    public async Task A_keyboard_still_reports_no_enforcement_state()
    {
        var (manager, enforcer, _, _) = Build(Keyboard());

        await manager.ReconcileAsync();

        enforcer.Calls.ShouldBeEmpty();
        var entry = manager.BuildReport().Devices.Single();
        entry.EnforcedPolicy.ShouldBeNull();
        entry.EnforcementStatus.ShouldBeNull();
    }

    // ---- stop and restart ---------------------------------------------------

    [Fact]
    public async Task Stopping_the_agent_releases_the_phone_too()
    {
        var (manager, enforcer, ledger, _) = Build(Phone(), Stick());

        await manager.ReconcileAsync();
        enforcer.Calls.Clear();

        var released = await manager.ReleaseAllAsync();

        released.Released.ShouldBe(2);
        enforcer.Calls.ShouldBe([("Release", PhoneId), ("Release", StickId)], ignoreOrder: true);
        ledger.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_restart_restricts_the_phone_again_from_local_state_alone()
    {
        var ledger = new Ledger();
        var store = new Store();

        var first = new UsbPolicyManager(
            new Enumerator([Phone()]), new Enforcer(), store, ledger, new TestClock(Start),
            NullLogger<UsbPolicyManager>.Instance);
        await first.ReconcileAsync();
        await first.ReleaseAllAsync();

        // The next lifetime, over the same files, with no server contact.
        var enforcer = new Enforcer();
        var second = new UsbPolicyManager(
            new Enumerator([Phone()]), enforcer, store, ledger, new TestClock(Start.AddMinutes(5)),
            NullLogger<UsbPolicyManager>.Instance);

        await second.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", PhoneId)]);
    }

    // ---- waking for the deadline ---------------------------------------------

    [Fact]
    public void The_loop_waits_the_full_interval_when_nothing_is_due()
    {
        UsbMonitorLoop.NextWait(null, Start).ShouldBe(UsbMonitorLoop.ReconcileInterval);
        UsbMonitorLoop.NextWait(Start.AddHours(2), Start).ShouldBe(UsbMonitorLoop.ReconcileInterval);
    }

    [Fact]
    public void The_loop_wakes_for_a_grant_that_lapses_before_the_next_sweep()
    {
        UsbMonitorLoop.NextWait(Start.AddSeconds(20), Start).ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_deadline_that_has_already_passed_is_acted_on_after_the_minimum_wait()
    {
        UsbMonitorLoop.NextWait(Start.AddSeconds(-5), Start).ShouldBe(UsbMonitorLoop.MinimumWait);
        UsbMonitorLoop.NextWait(Start, Start).ShouldBe(UsbMonitorLoop.MinimumWait);
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class Enumerator(UsbDeviceInfo[] devices) : IUsbDeviceEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate() => devices;
    }

    private sealed class Enforcer : IUsbPolicyEnforcer
    {
        public List<(string Action, string InstanceId)> Calls { get; } = [];

        public Func<string, UsbEnforcementResult> Respond { get; set; } = _ => UsbEnforcementResult.Ok;

        public UsbEnforcementResult Restrict(string instanceId) => Record("Restrict", instanceId);

        public UsbEnforcementResult AllowReadOnly(string instanceId) => Record("AllowReadOnly", instanceId);

        public UsbEnforcementResult AllowReadWrite(string instanceId) => Record("AllowReadWrite", instanceId);

        public UsbEnforcementResult Release(string instanceId) => Record("Release", instanceId);

        private UsbEnforcementResult Record(string action, string instanceId)
        {
            Calls.Add((action, instanceId));
            return Respond(action);
        }
    }

    private sealed class Store : IUsbGrantStore
    {
        private UsbGrantSet _grants = UsbGrantSet.Empty;

        public ValueTask<UsbGrantSet> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_grants);

        public ValueTask SaveAsync(UsbGrantSet grants, CancellationToken cancellationToken = default)
        {
            _grants = grants;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Ledger : IUsbRestrictionLedger
    {
        public IReadOnlyCollection<string> Saved { get; private set; } = [];

        public ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Saved);

        public ValueTask SaveAsync(
            IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default)
        {
            Saved = instanceIds;
            return ValueTask.CompletedTask;
        }
    }
}
