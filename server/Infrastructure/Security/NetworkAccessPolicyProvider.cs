using System.Net;
using EndpointPlatform.Domain.Security;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Serves the Admin API's network access policy as a compiled, cached snapshot, so
/// the per-request middleware check costs an array scan rather than a query.
/// </summary>
/// <remarks>
/// <para>
/// A singleton over scoped state: the policy is read on every request, before
/// authentication, so it cannot ride the request's scoped DbContext. It opens its
/// own scope to reload, keeps the compiled result for
/// <see cref="SnapshotLifetime"/>, and <see cref="Invalidate"/> drops the cache so
/// the PUT endpoint's change applies on the very next request rather than within
/// fifteen seconds.
/// </para>
/// <para>
/// <b>A failed load FAILS OPEN</b> - no row, an unreachable database, a corrupt
/// entry all degrade to "policy disabled", with an error logged. Failing closed
/// would turn a database outage into every administrator locked out of the console
/// at once, and the policy is a perimeter in front of authentication, not the
/// authentication: every endpoint behind it keeps its session, permission and CSRF
/// checks regardless.
/// </para>
/// </remarks>
public sealed class NetworkAccessPolicyProvider(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<NetworkAccessPolicyProvider> logger)
{
    /// <summary>
    /// How long a snapshot serves before it is lazily reloaded. Long enough that
    /// the database sees a few requests a minute instead of every request; short
    /// enough that an edit made on another instance applies within moments even
    /// without <see cref="Invalidate"/>.
    /// </summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The most wall-clock time one reload may spend before it fails open. This
    /// check sits in front of EVERY request, so it must never inherit the
    /// database's full retry budget (the better part of a minute): a database that
    /// slow is a database outage for this purpose, and the policy degrades to
    /// disabled for one <see cref="SnapshotLifetime"/> rather than stalling the
    /// whole console behind connection retries.
    /// </summary>
    public static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<NetworkAccessPolicyProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Single-flight reloads: one query refreshes the cache for everyone waiting.</summary>
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private volatile NetworkAccessPolicySnapshot? _snapshot;

    /// <summary>The current snapshot, reloading it first when stale or invalidated.</summary>
    public async ValueTask<NetworkAccessPolicySnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (CurrentFreshSnapshot() is { } fresh)
        {
            return fresh;
        }

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            // Double-checked: whoever held the gate first has already reloaded.
            if (CurrentFreshSnapshot() is { } reloaded)
            {
                return reloaded;
            }

            var snapshot = await LoadAsync(cancellationToken);
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Drops the cached snapshot so the next request reloads. Called by the PUT
    /// endpoint so a saved policy applies immediately rather than on cache expiry.
    /// </summary>
    public void Invalidate() => _snapshot = null;

    private NetworkAccessPolicySnapshot? CurrentFreshSnapshot()
    {
        var snapshot = _snapshot;

        return snapshot is not null && _timeProvider.GetUtcNow() - snapshot.LoadedAt < SnapshotLifetime
            ? snapshot
            : null;
    }

    private async Task<NetworkAccessPolicySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // The budget cancels the query (and EF's connection retries with it) so a
        // slow or down database costs every 15 seconds one bounded probe, not a
        // minute of retries on the request that happened to trigger the reload.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LoadBudget);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<EndpointPlatformDbContext>();

            var policy = await dbContext.NetworkAccessPolicies
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == NetworkAccessPolicy.SingletonId, budget.Token);

            if (policy is null)
            {
                // Never configured. Disabled is the correct default, not an error.
                return new NetworkAccessPolicySnapshot(Enabled: false, Networks: [], LoadedAt: now);
            }

            var networks = new List<IPNetwork>(policy.AllowedCidrs.Count);

            foreach (var cidr in policy.AllowedCidrs)
            {
                if (IPNetwork.TryParse(cidr, out var network))
                {
                    networks.Add(network);
                }
                else
                {
                    // Stored normalized, so this only happens to a hand-edited row.
                    // Skipping the entry narrows the policy; it never widens it.
                    _logger.LogError(
                        "Stored network access policy entry {Cidr} does not parse and is ignored.", cidr);
                }
            }

            return new NetworkAccessPolicySnapshot(policy.Enabled, networks.ToArray(), now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up; nothing to cache and nothing to log as a failure.
            throw;
        }
        catch (Exception ex)
        {
            // FAIL OPEN, deliberately and broadly: whatever broke the read (database
            // down, migration pending, pool exhausted, the LoadBudget spent - which
            // lands here as its own OperationCanceledException), locking every
            // administrator out of the console would only compound it. The auth
            // stack behind this perimeter still protects every endpoint, and the
            // error is logged so the degradation is visible. The failure snapshot is
            // cached like any other, so a down database is probed once per
            // lifetime, not per request.
            _logger.LogError(ex,
                "Could not load the network access policy; treating it as disabled until the next "
                + "refresh. Authentication and authorization still protect every endpoint.");

            return new NetworkAccessPolicySnapshot(Enabled: false, Networks: [], LoadedAt: now);
        }
    }
}

/// <summary>
/// One compiled, immutable reading of the policy: whether it is in force, and the
/// parsed networks to match a caller against.
/// </summary>
public sealed record NetworkAccessPolicySnapshot(
    bool Enabled,
    IPNetwork[] Networks,
    DateTimeOffset LoadedAt)
{
    /// <summary>
    /// Whether this address falls inside any allowed network. The address is
    /// normalized first (IPv4-mapped IPv6 becomes IPv4);
    /// <see cref="IPNetwork.Contains(IPAddress)"/> is strict about address family,
    /// so an IPv6 caller checked against IPv4-only ranges is simply not matched.
    /// </summary>
    public bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var normalized = NetworkAccessPolicy.NormalizeAddress(address);

        foreach (var network in Networks)
        {
            if (network.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }
}
