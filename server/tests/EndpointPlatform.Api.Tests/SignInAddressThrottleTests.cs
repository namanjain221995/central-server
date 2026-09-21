using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The per-address failure budget that stops one attacker locking out every
/// administrator.
/// </summary>
/// <remarks>
/// <para>
/// Runs against the real local Redis, like everything else that touches it here:
/// the counter semantics being pinned are INCR-plus-expiry-on-first-increment,
/// which a fake would only restate rather than verify.
/// </para>
/// <para>
/// Every test uses an address of its own. Redis is shared with the rest of the
/// suite and the keys are window-stamped, not per-test, so a fixed address would
/// make these tests interfere with each other when run in parallel.
/// </para>
/// </remarks>
public sealed class SignInAddressThrottleTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;

    public async Task InitializeAsync() =>
        _redis = await ConnectionMultiplexer.ConnectAsync(TestRedis.ConnectionString);

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    /// <summary>A clock the test moves, so window rolling does not need a real wait.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private (SignInAddressThrottle Throttle, TestClock Clock) Create()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        return (new SignInAddressThrottle(_redis, clock, NullLogger<SignInAddressThrottle>.Instance), clock);
    }

    /// <summary>A unique address per test, so shared Redis cannot cross-contaminate.</summary>
    private static string Address() => $"203.0.113.{Random.Shared.Next(1, 255)}-{Guid.NewGuid():N}";

    [Fact]
    public async Task An_address_within_its_budget_is_not_blocked()
    {
        var (throttle, _) = Create();
        var address = Address();

        for (var i = 0; i < SignInAddressThrottle.MaxFailuresPerWindow; i++)
        {
            await throttle.RecordFailureAsync(address);
        }

        (await throttle.InspectAsync(address)).Blocked.ShouldBeFalse();
    }

    [Fact]
    public async Task An_address_over_its_budget_is_blocked()
    {
        var (throttle, _) = Create();
        var address = Address();

        for (var i = 0; i < SignInAddressThrottle.MaxFailuresPerWindow + 1; i++)
        {
            await throttle.RecordFailureAsync(address);
        }

        var decision = await throttle.InspectAsync(address);

        decision.Blocked.ShouldBeTrue();
        decision.RetryAfterSeconds.ShouldBe((int)SignInAddressThrottle.Window.TotalSeconds);
    }

    /// <summary>
    /// The block is a window, not a ban: it lifts on its own.
    /// </summary>
    /// <remarks>
    /// A permanent block would recreate the problem this class exists to solve,
    /// one layer down - an office behind a single NAT address could be shut out of
    /// the console indefinitely by one person's bad morning.
    /// </remarks>
    [Fact]
    public async Task The_block_lifts_when_the_window_rolls()
    {
        var (throttle, clock) = Create();
        var address = Address();

        for (var i = 0; i < SignInAddressThrottle.MaxFailuresPerWindow + 1; i++)
        {
            await throttle.RecordFailureAsync(address);
        }

        (await throttle.InspectAsync(address)).Blocked.ShouldBeTrue();

        clock.Advance(SignInAddressThrottle.Window + TimeSpan.FromSeconds(1));

        (await throttle.InspectAsync(address)).Blocked.ShouldBeFalse();
    }

    /// <summary>One address spending its budget does not affect another.</summary>
    [Fact]
    public async Task Addresses_are_counted_independently()
    {
        var (throttle, _) = Create();
        var noisy = Address();
        var innocent = Address();

        for (var i = 0; i < SignInAddressThrottle.MaxFailuresPerWindow + 1; i++)
        {
            await throttle.RecordFailureAsync(noisy);
        }

        (await throttle.InspectAsync(noisy)).Blocked.ShouldBeTrue();
        (await throttle.InspectAsync(innocent)).Blocked.ShouldBeFalse();
    }

    /// <summary>
    /// Inspecting does not consume budget.
    /// </summary>
    /// <remarks>
    /// Inspect runs on every sign-in attempt including the successful ones. If it
    /// counted, a busy office sharing one public address would throttle itself
    /// just by signing in normally.
    /// </remarks>
    [Fact]
    public async Task Inspecting_does_not_consume_budget()
    {
        var (throttle, _) = Create();
        var address = Address();

        for (var i = 0; i < SignInAddressThrottle.MaxFailuresPerWindow * 3; i++)
        {
            (await throttle.InspectAsync(address)).Blocked.ShouldBeFalse();
        }
    }

    /// <summary>
    /// A missing address is never blocked, and recording one is a no-op.
    /// </summary>
    /// <remarks>
    /// Blocking on a missing address would turn an absent or misconfigured
    /// forwarded header into a total sign-in outage. The per-account lockout still
    /// applies in that case.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_address_is_never_blocked(string? address)
    {
        var (throttle, _) = Create();

        await throttle.RecordFailureAsync(address);

        (await throttle.InspectAsync(address)).Blocked.ShouldBeFalse();
    }
}
