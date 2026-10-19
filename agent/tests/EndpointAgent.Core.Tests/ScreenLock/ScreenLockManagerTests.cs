using EndpointAgent.Core.ScreenLock;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.ScreenLock;

/// <summary>
/// The manager that owns the one lock: engage (persist, lock down, show), release
/// (restore exactly, clear, take down), the on-device unlock, the reboot relock,
/// and the watchdog that brings the overlay back. Driven against fakes for the
/// store, the session launcher and the machine lockdown, so every path runs
/// headless.
/// </summary>
public sealed class ScreenLockManagerTests
{
    private static readonly Guid LockA = Guid.Parse("0199d000-0000-7000-8000-00000000000a");
    private static readonly Guid LockB = Guid.Parse("0199d000-0000-7000-8000-00000000000b");

    private sealed class FakeStore : IScreenLockStore
    {
        public ScreenLockState? State { get; set; }
        public int Saves { get; private set; }
        public bool ThrowOnSave { get; set; }

        public ValueTask<ScreenLockState?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(State);

        public ValueTask SaveAsync(ScreenLockState? state, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("the target disk corrupted the write");
            }

            State = state;
            Saves++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLauncher : IScreenLockLauncher
    {
        public int Ensured { get; private set; }
        public int Stopped { get; private set; }

        public IReadOnlyList<uint> EnsureOverlayInAllSessions()
        {
            Ensured++;
            return [];
        }

        public void StopAllOverlays() => Stopped++;
    }

    private sealed class FakeLockdown : IScreenLockdown
    {
        public int Captures { get; private set; }
        public int Applies { get; private set; }
        public int Restores { get; private set; }
        public string? RestoredWith { get; private set; }
        public string Snapshot { get; set; } = "SNAPSHOT-1";

        public string? Capture()
        {
            Captures++;
            return Snapshot;
        }

        public void Apply() => Applies++;

        public void Restore(string? snapshot)
        {
            Restores++;
            RestoredWith = snapshot;
        }
    }

    private static ScreenLockManager Manager(FakeStore store, FakeLauncher launcher, FakeLockdown lockdown) =>
        new(store, launcher, lockdown, NullLogger<ScreenLockManager>.Instance);

    private static ScreenLockState State(Guid lockId, string code = "secret-code") =>
        new(lockId, "This device is locked by IT.", "ext. 4000",
            new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero), ScreenLockStateTests.VerifierFor(code));

    // --------------------------------------------------------------- engaging

    [Fact]
    public async Task Engaging_persists_locks_down_and_shows_the_overlay()
    {
        var store = new FakeStore();
        var launcher = new FakeLauncher();
        var lockdown = new FakeLockdown();
        var manager = Manager(store, launcher, lockdown);

        await manager.EngageAsync(State(LockA));

        manager.CurrentlyLocked.ShouldBeTrue();
        manager.CurrentContent.ShouldNotBeNull().Message.ShouldBe("This device is locked by IT.");
        lockdown.Captures.ShouldBe(1);
        lockdown.Applies.ShouldBe(1, "the machine is hardened");
        store.State.ShouldNotBeNull().PriorPolicy.ShouldBe("SNAPSHOT-1", "the captured machine state is persisted with the lock");
        launcher.Ensured.ShouldBe(1);
    }

    [Fact]
    public async Task Engaging_the_same_lock_twice_locks_down_once()
    {
        var store = new FakeStore();
        var lockdown = new FakeLockdown();
        var manager = Manager(store, new FakeLauncher(), lockdown);

        await manager.EngageAsync(State(LockA));
        await manager.EngageAsync(State(LockA));

        lockdown.Captures.ShouldBe(1, "the same lock delivered twice captures and locks down once");
    }

    [Fact]
    public async Task A_different_lock_while_locked_keeps_the_first_snapshot()
    {
        var store = new FakeStore();
        var lockdown = new FakeLockdown { Snapshot = "FIRST" };
        var manager = Manager(store, new FakeLauncher(), lockdown);

        await manager.EngageAsync(State(LockA));
        lockdown.Snapshot = "SECOND";
        await manager.EngageAsync(State(LockB));

        lockdown.Captures.ShouldBe(1, "the lockdown is not re-captured, or release would restore an already-locked state");
        lockdown.Applies.ShouldBe(2, "the fresh lock hardened once and the replacement re-applied");
        store.State.ShouldNotBeNull();
        store.State!.LockId.ShouldBe(LockB, "the new lock's words are adopted");
        store.State!.PriorPolicy.ShouldBe("FIRST", "the machine's true prior state is kept");
    }

    [Fact]
    public async Task A_save_failure_hardens_nothing_so_no_machine_is_left_crippled()
    {
        // Persist-before-harden: if the lock cannot be saved (a corrupt-write disk,
        // a crash), the machine must NOT be locked down -- otherwise a reboot would
        // see "not locked" and never restore the hardening, a permanently crippled
        // machine. Capture reads only; Apply must not have run.
        var store = new FakeStore { ThrowOnSave = true };
        var lockdown = new FakeLockdown();
        var launcher = new FakeLauncher();
        var manager = Manager(store, launcher, lockdown);

        await Should.ThrowAsync<IOException>(async () => await manager.EngageAsync(State(LockA)));

        lockdown.Applies.ShouldBe(0, "the machine must not be hardened when the lock could not be persisted");
        manager.CurrentlyLocked.ShouldBeFalse();
        launcher.Ensured.ShouldBe(0, "no overlay is shown without a persisted lock");
    }

    // --------------------------------------------------------------- releasing

    [Fact]
    public async Task Releasing_restores_the_machine_clears_the_lock_and_takes_down_the_overlay()
    {
        var store = new FakeStore();
        var launcher = new FakeLauncher();
        var lockdown = new FakeLockdown { Snapshot = "CAPTURED" };
        var manager = Manager(store, launcher, lockdown);
        await manager.EngageAsync(State(LockA));

        var outcome = await manager.ReleaseAsync(LockA);

        outcome.WasLocked.ShouldBeTrue();
        outcome.ReleasedLockId.ShouldBe(LockA);
        lockdown.Restores.ShouldBe(1);
        lockdown.RestoredWith.ShouldBe("CAPTURED", "release restores exactly what engage captured");
        store.State.ShouldBeNull("the lock is cleared");
        launcher.Stopped.ShouldBe(1);
        manager.CurrentlyLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Releasing_when_not_locked_succeeds_and_changes_nothing()
    {
        var launcher = new FakeLauncher();
        var lockdown = new FakeLockdown();
        var manager = Manager(new FakeStore(), launcher, lockdown);

        var outcome = await manager.ReleaseAsync(LockA);

        outcome.WasLocked.ShouldBeFalse();
        lockdown.Restores.ShouldBe(0);
        launcher.Stopped.ShouldBe(0);
    }

    [Fact]
    public async Task A_release_naming_a_different_lock_still_frees_the_device()
    {
        // An authenticated release must never leave a user locked out over an id
        // mismatch; the active lock is cleared whatever id the task names.
        var store = new FakeStore();
        var manager = Manager(store, new FakeLauncher(), new FakeLockdown());
        await manager.EngageAsync(State(LockA));

        var outcome = await manager.ReleaseAsync(LockB);

        outcome.WasLocked.ShouldBeTrue();
        outcome.ReleasedLockId.ShouldBe(LockA);
        manager.CurrentlyLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Releasing_reads_the_lock_from_the_store_when_memory_was_lost()
    {
        // A service that restarted while locked holds nothing in memory; a release
        // still restores from the persisted snapshot.
        var store = new FakeStore { State = State(LockA) with { PriorPolicy = "ON-DISK" } };
        var lockdown = new FakeLockdown();
        var manager = Manager(store, new FakeLauncher(), lockdown);

        var outcome = await manager.ReleaseAsync(LockA);

        outcome.WasLocked.ShouldBeTrue();
        lockdown.RestoredWith.ShouldBe("ON-DISK");
        store.State.ShouldBeNull();
    }

    // ------------------------------------------------------------ on-device unlock

    [Fact]
    public async Task The_correct_code_verifies_and_releases_a_wrong_one_does_not()
    {
        var launcher = new FakeLauncher();
        var lockdown = new FakeLockdown();
        var manager = Manager(new FakeStore(), launcher, lockdown);
        await manager.EngageAsync(State(LockA, code: "OpenSesame"));

        (await manager.VerifyAndReleaseAsync("wrong")).ShouldBeFalse();
        manager.CurrentlyLocked.ShouldBeTrue("a wrong code leaves the device locked");
        lockdown.Restores.ShouldBe(0);

        (await manager.VerifyAndReleaseAsync("OpenSesame")).ShouldBeTrue();
        manager.CurrentlyLocked.ShouldBeFalse("the correct code releases the device");
        lockdown.Restores.ShouldBe(1);
        launcher.Stopped.ShouldBe(1);
    }

    [Fact]
    public async Task Verifying_when_not_locked_is_false()
    {
        var manager = Manager(new FakeStore(), new FakeLauncher(), new FakeLockdown());

        (await manager.VerifyAndReleaseAsync("anything")).ShouldBeFalse();
    }

    // --------------------------------------------------------------- watchdog

    [Fact]
    public async Task A_tick_while_locked_re_asserts_the_lockdown_and_relaunches_the_overlay()
    {
        var launcher = new FakeLauncher();
        var lockdown = new FakeLockdown();
        var manager = Manager(new FakeStore(), launcher, lockdown);

        await manager.TickAsync();
        lockdown.Applies.ShouldBe(0);
        launcher.Ensured.ShouldBe(0, "nothing to watch while not locked");

        await manager.EngageAsync(State(LockA)); // Applies -> 1, Ensured -> 1
        lockdown.Applies.ShouldBe(1);
        await manager.TickAsync();

        lockdown.Applies.ShouldBe(2, "the tick re-applies the lockdown");
        launcher.Ensured.ShouldBe(2, "the tick relaunches the overlay where it was lost -- the watchdog");
    }

    // ------------------------------------------------------------ reboot relock

    [Fact]
    public async Task On_start_a_lock_the_device_was_holding_is_re_engaged()
    {
        var store = new FakeStore { State = State(LockA) with { PriorPolicy = "FROM-BEFORE" } };
        var launcher = new FakeLauncher();
        var lockdown = new FakeLockdown();
        var manager = Manager(store, launcher, lockdown);

        await manager.StartAsync(CancellationToken.None);
        try
        {
            manager.CurrentlyLocked.ShouldBeTrue("a device that rebooted while locked comes back locked");
            lockdown.Applies.ShouldBeGreaterThanOrEqualTo(1, "the lockdown is re-applied on start");
            launcher.Ensured.ShouldBeGreaterThanOrEqualTo(1, "the overlay is shown again on start");
            lockdown.Captures.ShouldBe(0, "a relock re-applies; it does not re-capture the prior state");
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }
}
