using System.Net;
using EndpointPlatform.Domain.Security;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Security;

/// <summary>
/// The network access policy provider against real PostgreSQL: a missing row is a
/// disabled policy, an enabled policy matches exactly its ranges (with IPv4-mapped
/// IPv6 callers unmapped first), the snapshot is cached until it expires or is
/// invalidated, and a database failure fails OPEN rather than locking the console.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NetworkAccessPolicyProviderTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    /// <summary>A clock the test moves by hand, so cache expiry is deterministic.</summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private NetworkAccessPolicyProvider CreateProvider(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateDbContext());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new NetworkAccessPolicyProvider(
            scopeFactory,
            timeProvider ?? TimeProvider.System,
            NullLogger<NetworkAccessPolicyProvider>.Instance);
    }

    /// <summary>Puts the shared singleton row into exactly this state.</summary>
    private async Task SetPolicyAsync(bool enabled, params string[] cidrs)
    {
        await using var db = _fixture.CreateDbContext();

        var policy = await db.NetworkAccessPolicies
            .SingleOrDefaultAsync(p => p.Id == NetworkAccessPolicy.SingletonId);

        if (policy is null)
        {
            db.NetworkAccessPolicies.Add(
                NetworkAccessPolicy.Create(enabled, cidrs, DateTimeOffset.UtcNow, null, null));
        }
        else
        {
            policy.Update(enabled, cidrs, DateTimeOffset.UtcNow, null, null);
        }

        await db.SaveChangesAsync();
    }

    private async Task RemovePolicyAsync()
    {
        await using var db = _fixture.CreateDbContext();
        await db.NetworkAccessPolicies
            .Where(p => p.Id == NetworkAccessPolicy.SingletonId)
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task A_missing_row_is_a_disabled_policy()
    {
        await RemovePolicyAsync();

        var snapshot = await CreateProvider().GetSnapshotAsync();

        snapshot.Enabled.ShouldBeFalse();
        snapshot.Networks.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_enabled_policy_matches_in_range_callers_and_rejects_the_rest()
    {
        await SetPolicyAsync(enabled: true, "203.0.113.0/24", "2001:db8::/32");

        var snapshot = await CreateProvider().GetSnapshotAsync();

        snapshot.Enabled.ShouldBeTrue();
        snapshot.IsAllowed(IPAddress.Parse("203.0.113.9")).ShouldBeTrue();
        snapshot.IsAllowed(IPAddress.Parse("203.0.114.9")).ShouldBeFalse();
        snapshot.IsAllowed(IPAddress.Parse("198.51.100.1")).ShouldBeFalse();
        snapshot.IsAllowed(IPAddress.Parse("2001:db8::42")).ShouldBeTrue();
        // An IPv6 caller against ranges that do not include it: a family mismatch
        // with the IPv4 range is simply "not matched", never an exception.
        snapshot.IsAllowed(IPAddress.Parse("2600::1")).ShouldBeFalse();
    }

    [Fact]
    public async Task An_ipv4_mapped_ipv6_caller_matches_an_ipv4_range()
    {
        await SetPolicyAsync(enabled: true, "203.0.113.0/24");

        var snapshot = await CreateProvider().GetSnapshotAsync();

        // What a dual-stack socket reports for a plain IPv4 caller.
        snapshot.IsAllowed(IPAddress.Parse("::ffff:203.0.113.9")).ShouldBeTrue();
        snapshot.IsAllowed(IPAddress.Parse("::ffff:198.51.100.1")).ShouldBeFalse();
    }

    [Fact]
    public async Task The_snapshot_is_cached_until_invalidated_and_invalidate_applies_a_change_immediately()
    {
        await SetPolicyAsync(enabled: true, "203.0.113.0/24");

        var time = new TestTimeProvider();
        var provider = CreateProvider(time);

        (await provider.GetSnapshotAsync()).Enabled.ShouldBeTrue();

        // The row changes underneath; with the clock frozen the cache still serves.
        await SetPolicyAsync(enabled: false);
        (await provider.GetSnapshotAsync()).Enabled.ShouldBeTrue();

        // Invalidate is what the PUT endpoint calls: the next read sees the change.
        provider.Invalidate();
        (await provider.GetSnapshotAsync()).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_stale_snapshot_reloads_by_itself_after_its_lifetime()
    {
        await SetPolicyAsync(enabled: true, "203.0.113.0/24");

        var time = new TestTimeProvider();
        var provider = CreateProvider(time);

        (await provider.GetSnapshotAsync()).Enabled.ShouldBeTrue();

        await SetPolicyAsync(enabled: false);
        time.Now += NetworkAccessPolicyProvider.SnapshotLifetime + TimeSpan.FromSeconds(1);

        (await provider.GetSnapshotAsync()).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_database_failure_fails_open_as_a_disabled_policy()
    {
        // Port 1 is reserved and never listening, so the load fails fast. The
        // provider must degrade to "disabled" - a database outage locking every
        // administrator out of the console would compound the outage.
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable_by_design;Username=none;Password=none;Timeout=1")
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new EndpointPlatformDbContext(options));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var provider = new NetworkAccessPolicyProvider(
            scopeFactory, TimeProvider.System, NullLogger<NetworkAccessPolicyProvider>.Instance);

        var snapshot = await provider.GetSnapshotAsync();

        snapshot.Enabled.ShouldBeFalse();
        snapshot.Networks.ShouldBeEmpty();
    }
}
