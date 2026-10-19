using System.Text.Json;
using EndpointAgent.Core.ScreenLock;
using EndpointAgent.Core.Tasks;
using EndpointAgent.Core.Tests.ScreenLock;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The engage and release executors: the strict gate in front of the manager
/// (a bad payload or an expired task locks nothing) and the honest result behind
/// it (a release of an already-unlocked device still succeeds).
/// </summary>
public sealed class ScreenLockExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid LockId = Guid.Parse("0199d000-0000-7000-8000-00000000f00d");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStore : IScreenLockStore
    {
        public ScreenLockState? State { get; set; }
        public ValueTask<ScreenLockState?> LoadAsync(CancellationToken ct = default) => ValueTask.FromResult(State);
        public ValueTask SaveAsync(ScreenLockState? state, CancellationToken ct = default) { State = state; return ValueTask.CompletedTask; }
    }

    private sealed class NoopLauncher : IScreenLockLauncher
    {
        public IReadOnlyList<uint> EnsureOverlayInAllSessions() => [];
        public void StopAllOverlays() { }
    }

    private sealed class NoopLockdown : IScreenLockdown
    {
        public string? Capture() => "snap";
        public void Apply() { }
        public void Restore(string? snapshot) { }
    }

    private static ScreenLockManager NewManager(FakeStore? store = null) =>
        new(store ?? new FakeStore(), new NoopLauncher(), new NoopLockdown(), NullLogger<ScreenLockManager>.Instance);

    private static AgentTask EngageTask(string? payload, DateTimeOffset? expiresAt = null) =>
        new(Guid.CreateVersion7(), "EngageScreenLock", payload, expiresAt);

    private static string EngagePayload(
        string message = "This device is locked by IT.",
        string? contact = "ext. 4000",
        string code = "unlock-code",
        object? unlockOverride = null)
    {
        var v = ScreenLockStateTests.VerifierFor(code);
        object unlock = unlockOverride ?? new { algo = v.Algo, iterations = v.Iterations, salt = v.Salt, hash = v.Hash };
        return JsonSerializer.Serialize(new { lockId = LockId, message, contactLine = contact, unlock });
    }

    private static EngageScreenLockExecutor Engage(ScreenLockManager manager) =>
        new(manager, NullLogger<EngageScreenLockExecutor>.Instance, new FixedClock(Now));

    // --------------------------------------------------------------- engage

    [Fact]
    public async Task A_valid_payload_engages_the_lock()
    {
        var manager = NewManager();

        var result = await Engage(manager).ExecuteAsync(EngageTask(EngagePayload()));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("Screen lock engaged.");
        manager.CurrentlyLocked.ShouldBeTrue();
    }

    [Fact]
    public async Task An_expired_task_engages_nothing()
    {
        var manager = NewManager();

        var result = await Engage(manager).ExecuteAsync(EngageTask(EngagePayload(), expiresAt: Now.AddSeconds(-1)));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldNotBeNull().ShouldContain("expired");
        manager.CurrentlyLocked.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"x\"}")]  // no lockId, no verifier
    public async Task A_missing_or_malformed_payload_engages_nothing(string? payload)
    {
        var manager = NewManager();

        var result = await Engage(manager).ExecuteAsync(EngageTask(payload));

        result.Succeeded.ShouldBeFalse();
        manager.CurrentlyLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_message_or_contact_out_of_bounds_engages_nothing()
    {
        var manager = NewManager();

        var longMessage = await Engage(manager).ExecuteAsync(EngageTask(EngagePayload(message: new string('x', 501))));
        longMessage.Succeeded.ShouldBeFalse();

        var longContact = await Engage(manager).ExecuteAsync(EngageTask(EngagePayload(contact: new string('x', 201))));
        longContact.Succeeded.ShouldBeFalse();

        var controlChars = await Engage(manager).ExecuteAsync(EngageTask(EngagePayload(message: "line\u0007bell")));
        controlChars.Succeeded.ShouldBeFalse();

        manager.CurrentlyLocked.ShouldBeFalse("nothing with bad words ever engaged");
    }

    [Fact]
    public async Task A_bad_verifier_engages_nothing()
    {
        var manager = NewManager();
        var v = ScreenLockStateTests.VerifierFor("code");

        var wrongAlgo = await Engage(manager).ExecuteAsync(EngageTask(
            EngagePayload(unlockOverride: new { algo = "bcrypt", iterations = v.Iterations, salt = v.Salt, hash = v.Hash })));
        wrongAlgo.Succeeded.ShouldBeFalse("a lock that could never be released by a correct code must not be put up");

        var tooFew = await Engage(manager).ExecuteAsync(EngageTask(
            EngagePayload(unlockOverride: new { algo = v.Algo, iterations = 10, salt = v.Salt, hash = v.Hash })));
        tooFew.Succeeded.ShouldBeFalse();

        var missingField = await Engage(manager).ExecuteAsync(EngageTask(
            EngagePayload(unlockOverride: new { algo = v.Algo, iterations = v.Iterations, salt = v.Salt })));
        missingField.Succeeded.ShouldBeFalse();

        manager.CurrentlyLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_payload_without_a_contact_line_still_engages()
    {
        var manager = NewManager();

        var result = await Engage(manager).ExecuteAsync(EngageTask(EngagePayload(contact: null)));

        result.Succeeded.ShouldBeTrue();
        manager.CurrentlyLocked.ShouldBeTrue();
    }

    // -------------------------------------------------------------- release

    [Fact]
    public async Task Release_frees_a_locked_device()
    {
        var manager = NewManager();
        await Engage(manager).ExecuteAsync(EngageTask(EngagePayload()));

        var release = new ReleaseScreenLockExecutor(manager, NullLogger<ReleaseScreenLockExecutor>.Instance);
        var result = await release.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ReleaseScreenLock",
            JsonSerializer.Serialize(new { lockId = LockId })));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldNotBeNull().ShouldContain("released");
        manager.CurrentlyLocked.ShouldBeFalse();
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcome").GetString().ShouldBe("Released");
    }

    [Fact]
    public async Task Release_of_an_unlocked_device_still_succeeds_and_says_so()
    {
        var manager = NewManager();
        var release = new ReleaseScreenLockExecutor(manager, NullLogger<ReleaseScreenLockExecutor>.Instance);

        var result = await release.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ReleaseScreenLock",
            JsonSerializer.Serialize(new { lockId = LockId })));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldNotBeNull().ShouldContain("not locked");
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcome").GetString().ShouldBe("NotLocked");
    }

    [Fact]
    public async Task Release_tolerates_a_missing_payload()
    {
        var manager = NewManager();
        await Engage(manager).ExecuteAsync(EngageTask(EngagePayload()));

        var release = new ReleaseScreenLockExecutor(manager, NullLogger<ReleaseScreenLockExecutor>.Instance);
        var result = await release.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ReleaseScreenLock", null));

        // A release with no id still frees the device (the recovery path is sacred).
        result.Succeeded.ShouldBeTrue();
        manager.CurrentlyLocked.ShouldBeFalse();
    }
}
