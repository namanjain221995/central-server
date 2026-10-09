using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Usb;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// How a phone the agent disabled gets its access back when something goes wrong:
/// a crash, an unplug while nothing runs, a release Windows refuses, a disable
/// Windows defers to a restart, a server that cannot be reached, a downgrade to a
/// version that no longer classifies the device as restrictable.
/// </summary>
/// <remarks>
/// Every path ends in the same place — the release list and the release that runs
/// when the service stops — so these pin that no path can drop a device from the
/// list while it may still be disabled. What Windows does with the release on real
/// hardware is the physical acceptance's to prove, not these tests'.
/// </remarks>
public sealed class UsbRecoveryTests
{
    private const string PhoneId = @"USB\VID_2717&PID_FF40\EXAMPLE0SERIAL01";

    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static UsbDeviceInfo Phone(UsbClass deviceClass = UsbClass.PortableDevice) =>
        new(PhoneId, deviceClass, "2717", "FF40", "EXAMPLE0SERIAL01", "Xiaomi", "Phone",
            @"USB\VID_2717&PID_FF40", IsEnabled: true);

    /// <summary>One machine across several agent lifetimes, over shared persistent state.</summary>
    private sealed class Machine
    {
        public Enforcer Enforcer { get; } = new();

        public Store Grants { get; } = new();

        public Ledger Ledger { get; } = new();

        public TestClock Clock { get; } = new(Start);

        public UsbDeviceInfo[] Attached { get; set; } = [Phone()];

        public UsbPolicyManager StartAgent() =>
            new(new Enumerator(() => Attached), Enforcer, Grants, Ledger, Clock, NullLogger<UsbPolicyManager>.Instance);
    }

    [Fact]
    public async Task A_phone_left_disabled_by_a_crashed_agent_is_released_by_the_next_stop_even_when_unplugged()
    {
        var machine = new Machine();
        await machine.StartAgent().ReconcileAsync();

        // The process dies: no release ran. Then the phone is unplugged.
        machine.Ledger.Saved.ShouldBe([PhoneId]);
        machine.Attached = [];
        machine.Enforcer.Calls.Clear();

        var outcome = await machine.StartAgent().ReleaseAllAsync();

        outcome.Released.ShouldBe(1);
        machine.Enforcer.Calls.ShouldBe([("Release", PhoneId)]);
        machine.Ledger.Saved.ShouldBeEmpty();
    }

    /// <summary>
    /// What a downgrade, or any change of classification, looks like from inside: the
    /// release list names a device the running classifier no longer restricts. It must
    /// be neither restricted again nor forgotten — forgetting it would leave it
    /// disabled with nothing left that will ever release it.
    /// </summary>
    [Fact]
    public async Task A_listed_device_this_version_does_not_restrict_stays_listed_and_is_released_on_stop()
    {
        var machine = new Machine();
        await machine.StartAgent().ReconcileAsync();

        machine.Attached = [Phone(UsbClass.Other)];
        machine.Enforcer.Calls.Clear();

        var agent = machine.StartAgent();
        await agent.ReconcileAsync();

        machine.Enforcer.Calls.ShouldBeEmpty();
        machine.Ledger.Saved.ShouldBe([PhoneId]);

        var outcome = await agent.ReleaseAllAsync();

        outcome.Released.ShouldBe(1);
        machine.Enforcer.Calls.ShouldBe([("Release", PhoneId)]);
        machine.Ledger.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_disable_Windows_deferred_to_a_restart_is_listed_so_stopping_first_cancels_it()
    {
        var machine = new Machine();
        machine.Enforcer.Respond = action => action == "Restrict"
            ? UsbEnforcementResult.RestartRequired("The device is in use.")
            : UsbEnforcementResult.Ok;

        var agent = machine.StartAgent();
        await agent.ReconcileAsync();

        machine.Ledger.Saved.ShouldBe([PhoneId]);
        agent.BuildReport().Devices.Single().EnforcementStatus.ShouldBe("RequiresRestart");

        machine.Enforcer.Calls.Clear();
        var outcome = await agent.ReleaseAllAsync();

        outcome.Released.ShouldBe(1);
        machine.Enforcer.Calls.ShouldBe([("Release", PhoneId)]);
    }

    [Fact]
    public async Task A_release_Windows_refuses_keeps_the_phone_listed_and_the_next_stop_tries_again()
    {
        var machine = new Machine();
        var agent = machine.StartAgent();
        await agent.ReconcileAsync();

        machine.Enforcer.Respond = action => action == "Release"
            ? UsbEnforcementResult.Failed("SetupDiCallClassInstaller failed (Win32 5).")
            : UsbEnforcementResult.Ok;

        var first = await agent.ReleaseAllAsync();

        first.Released.ShouldBe(0);
        first.Failed.ShouldBe(1);
        machine.Ledger.Saved.ShouldBe([PhoneId]);

        machine.Enforcer.Respond = _ => UsbEnforcementResult.Ok;
        var second = await machine.StartAgent().ReleaseAllAsync();

        second.Released.ShouldBe(1);
        machine.Ledger.Saved.ShouldBeEmpty();
    }

    /// <summary>
    /// With the server unreachable nothing arrives to change the policy, so the phone
    /// stays restricted however long that lasts: there is no timeout that opens it. The
    /// way back without a server is a local one — stopping the service.
    /// </summary>
    [Fact]
    public async Task With_no_server_a_restricted_phone_stays_restricted_until_the_service_stops()
    {
        var machine = new Machine();
        var agent = machine.StartAgent();

        for (var hour = 0; hour < 24; hour++)
        {
            await agent.ReconcileAsync();
            machine.Clock.Advance(TimeSpan.FromHours(1));
        }

        machine.Enforcer.Calls.Count.ShouldBe(24);
        machine.Enforcer.Calls.ShouldAllBe(call => call.Action == "Restrict");

        var outcome = await agent.ReleaseAllAsync();
        outcome.Released.ShouldBe(1);
    }

    [Fact]
    public async Task A_grant_cached_before_the_server_went_away_still_lapses_on_the_agent_clock()
    {
        var machine = new Machine();
        var agent = machine.StartAgent();

        await agent.ApplyPolicyAsync(
            [new UsbGrantRecord(PhoneId, Start.AddMinutes(30), UsbEnforcedState.Enabled)], Start);
        machine.Enforcer.Calls.Last().ShouldBe(("AllowReadWrite", PhoneId));

        // No server from here on.
        machine.Clock.Advance(TimeSpan.FromMinutes(31));
        await agent.ReconcileAsync();

        machine.Enforcer.Calls.Last().ShouldBe(("Restrict", PhoneId));
    }

    [Fact]
    public async Task Revoking_a_phone_grant_restricts_it_with_the_policy_that_follows()
    {
        var machine = new Machine();
        var agent = machine.StartAgent();

        await agent.ApplyPolicyAsync(
            [new UsbGrantRecord(PhoneId, Start.AddHours(1), UsbEnforcedState.Enabled)], Start);
        machine.Enforcer.Calls.Last().ShouldBe(("AllowReadWrite", PhoneId));

        await agent.ApplyPolicyAsync([], Start.AddMinutes(1));

        machine.Enforcer.Calls.Last().ShouldBe(("Restrict", PhoneId));
        agent.BuildReport().Devices.Single().EnforcedPolicy.ShouldBe("Restricted");
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class Enumerator(Func<UsbDeviceInfo[]> attached) : IUsbDeviceEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate() => attached();
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
