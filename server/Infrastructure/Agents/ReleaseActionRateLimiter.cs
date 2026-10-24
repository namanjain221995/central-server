using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.Agents;

/// <param name="Allowed">Whether this attempt may proceed.</param>
/// <param name="RetryAfterSeconds">Roughly how long until the window rolls.</param>
public sealed record ReleaseActionRateLimitDecision(bool Allowed, int RetryAfterSeconds);

/// <summary>
/// Limits how often one administrator may drive the dangerous agent-release
/// actions: publishing a release, replacing a draft's artifact, and queueing a
/// device's self-update.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="BitLocker.RevealRateLimiter"/> with the same
/// mechanics, deliberately: counted in Redis with INCR plus an expiry set on
/// first increment, so the fixed window is atomic across instances and needs no
/// cleanup; the attempt is counted <b>before</b> it is judged, so an attempt
/// that trips the limit still consumes budget and a caller cannot sit exactly at
/// the boundary indefinitely; and a permitted action does not reset the counter,
/// because it is precisely the successful actions -- each one SYSTEM-level code
/// headed for the fleet -- that need bounding.
/// </para>
/// <para>
/// One per-user window only, unlike the reveal limiter's user+device pair.
/// Publish and artifact replacement act on a platform-global release, so there
/// is no device axis to bound; update-agent does name a device, but it counts
/// against the same per-user scope so the three gated actions share one budget.
/// </para>
/// <para>
/// Fails OPEN when Redis is down, exactly like the reveal limiter and for the
/// same reason: this is a brake behind a permission check and a step-up password
/// re-verification, every attempt is audited regardless, and failing closed
/// would let a cache outage block the release pipeline outright.
/// </para>
/// <para>
/// <b>No secret appears in a key, a log line or a response.</b> Keys are derived
/// from the user id only.
/// </para>
/// </remarks>
public sealed class ReleaseActionRateLimiter(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    ILogger<ReleaseActionRateLimiter> logger)
{
    /// <summary>Attempts allowed per user per window, across all three gated actions.</summary>
    public const int MaxAttemptsPerWindow = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<ReleaseActionRateLimiter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Counts this attempt against the caller's window and says whether it may proceed.
    /// </summary>
    public async Task<ReleaseActionRateLimitDecision> TryConsumeAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = _redis.GetDatabase();
            var key = Key(userId);

            var count = await db.StringIncrementAsync(key);

            // Only the first increment sets the expiry, which is what makes this a
            // fixed window rather than one that slides forward on every attempt and
            // never expires under sustained abuse.
            if (count == 1)
            {
                await db.KeyExpireAsync(key, Window);
            }

            if (count > MaxAttemptsPerWindow)
            {
                _logger.LogWarning(
                    "Agent-release action refused: user {UserId} has made {Count} attempts in the window.",
                    userId, count);

                return new ReleaseActionRateLimitDecision(false, (int)Window.TotalSeconds);
            }

            return new ReleaseActionRateLimitDecision(true, 0);
        }
        catch (RedisException ex)
        {
            // Fail open, deliberately and narrowly: the limiter is a brake, not the
            // access control -- the permission and the step-up password are.
            _logger.LogError(ex,
                "Agent-release action rate limiter unavailable; allowing the attempt. "
                + "The permission and step-up checks still applied.");

            return new ReleaseActionRateLimitDecision(true, 0);
        }
    }

    /// <summary>
    /// Window-stamped so counters roll without a sweeper. Contains only the user
    /// id -- never a release id, a version or any secret.
    /// </summary>
    private RedisKey Key(Guid userId)
    {
        var window = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / (long)Window.TotalSeconds;
        return (RedisKey)$"release:action:{userId:N}:{window}";
    }
}
