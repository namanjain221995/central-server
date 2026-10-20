using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Heartbeat;
using EndpointAgent.Core.ScreenLock;
using EndpointAgent.Core.Tests.ScreenLock;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Heartbeat;

public sealed class HeartbeatLoopTests
{
    [Fact]
    public void Backoff_never_drops_below_thirty_seconds()
    {
        for (var failures = 1; failures <= 20; failures++)
        {
            for (var sample = 0; sample < 25; sample++)
            {
                HeartbeatLoop.ComputeBackoff(failures)
                    .ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public void Backoff_never_exceeds_five_minutes()
    {
        for (var failures = 1; failures <= 20; failures++)
        {
            for (var sample = 0; sample < 25; sample++)
            {
                HeartbeatLoop.ComputeBackoff(failures)
                    .ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
            }
        }
    }

    [Fact]
    public void Backoff_is_jittered_rather_than_fixed()
    {
        // A fleet of agents that all retry at exactly the same instant is a
        // self-inflicted DDoS against a recovering server. Sampling the backoff
        // for a fixed failure count must produce spread, not one value.
        var samples = Enumerable.Range(0, 50)
            .Select(_ => HeartbeatLoop.ComputeBackoff(5))
            .Distinct()
            .Count();

        samples.ShouldBeGreaterThan(10, "backoff must be jittered");
    }

    /// <summary>
    /// The heartbeat the agent builds carries the device's LIVE screen-lock state.
    /// This is the link the console relies on: a local Ctrl+Alt+U unlock, which
    /// queues no release task, must still be learned from the heartbeat, or the
    /// device would show Locked forever.
    /// </summary>
    [Fact]
    public async Task The_built_heartbeat_carries_the_devices_live_screen_lock_state()
    {
        var manager = new ScreenLockManager(
            new FakeStore(), new FakeLauncher(), new FakeLockdown(), NullLogger<ScreenLockManager>.Instance);

        Build(manager).ScreenLocked.ShouldBe(false, "a device that is not locked reports not locked");

        await manager.EngageAsync(new ScreenLockState(
            Guid.NewGuid(), "This device is locked by IT.", null, DateTimeOffset.UtcNow,
            ScreenLockStateTests.VerifierFor("unlock-code")));
        Build(manager).ScreenLocked.ShouldBe(true, "once locked, the heartbeat reports locked");

        (await manager.VerifyAndReleaseAsync("unlock-code")).ShouldBeTrue();
        Build(manager).ScreenLocked.ShouldBe(false,
            "after an on-device unlock the heartbeat reports false, with no release task involved");
    }

    private static HeartbeatRequest Build(ScreenLockManager manager) =>
        HeartbeatLoop.BuildRequest(
            "host", "1.19.0", "Windows 11", DateTimeOffset.UtcNow, null, manager.CurrentlyLocked, remoteSupportId: null);

    /// <summary>
    /// The heartbeat the agent builds carries the device's RustDesk ID, read from the
    /// installed RustDesk's own config. This is the only way the console learns the
    /// ID to dial: the agent reports it, the server never asks the device for it.
    /// A device with RustDesk not installed (or no reader wired) reports no ID.
    /// </summary>
    [Fact]
    public void The_built_heartbeat_carries_the_rustdesk_id_from_the_reader()
    {
        BuildWith(new FakeRemoteSupportReader { Id = "123456789" }).RemoteSupportId
            .ShouldBe("123456789", "the reader's ID is carried on the wire");

        BuildWith(new FakeRemoteSupportReader { Id = null }).RemoteSupportId
            .ShouldBeNull("a device with RustDesk not installed or not yet registered reports no ID");
    }

    private static HeartbeatRequest BuildWith(IRemoteSupportReader reader) =>
        HeartbeatLoop.BuildRequest(
            "host", "1.20.0", "Windows 11", DateTimeOffset.UtcNow, null,
            screenLocked: false, remoteSupportId: reader.ReadRustDeskId());

    private sealed class FakeRemoteSupportReader : IRemoteSupportReader
    {
        public string? Id { get; set; }
        public string? ReadRustDeskId() => Id;
    }

    private sealed class FakeStore : IScreenLockStore
    {
        private ScreenLockState? _state;

        public ValueTask<ScreenLockState?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_state);

        public ValueTask SaveAsync(ScreenLockState? state, CancellationToken cancellationToken = default)
        {
            _state = state;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLauncher : IScreenLockLauncher
    {
        public IReadOnlyList<uint> EnsureOverlayInAllSessions() => [];
        public void StopAllOverlays() { }
    }

    private sealed class FakeLockdown : IScreenLockdown
    {
        public string? Capture() => "snap";
        public void Apply() { }
        public void Restore(string? snapshot) { }
    }
}
