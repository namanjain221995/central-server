using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.Security;

/// <param name="Blocked">Whether this address has spent its failure budget.</param>
/// <param name="RetryAfterSeconds">Roughly how long until the window rolls.</param>
public sealed record SignInAddressDecision(bool Blocked, int RetryAfterSeconds);

/// <summary>
/// Counts failed sign-ins per client address, and blocks an address that keeps
/// failing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to make the attacker bear the cost instead of the victim.</b>
/// Per-account lockout on its own is a denial of service on an internet-facing
/// console: the e-mail address is the username, so anyone who knows an
/// administrator's address can spend failures against it and keep that person
/// locked out. Locking the ADDRESS that is failing inverts that - the attacker
/// runs out of budget, and the administrator's own sign-in is unaffected.
/// </para>
/// <para>
/// <b>It is half of one mechanism.</b> <c>AdminAuthService</c> checks this before
/// it touches the account, and does not count a failure against
/// <c>PlatformUser.RecordFailedSignIn</c> once the address is blocked. That is
/// what stops a blocked attacker from continuing to drive a victim's lockout
/// counter. Account lockout remains as the backstop for a distributed attempt,
/// where no single address trips this limit.
/// </para>
/// <para>
/// <b>Only FAILURES are counted.</b> The ASP.NET fixed-window limiter on the
/// login route already bounds total request rate per address; this is a separate,
/// longer-horizon budget for attempts that were actually wrong, so an office
/// behind one NAT address is not throttled by people signing in successfully.
/// </para>
/// <para>
/// Counted in Redis with INCR plus an expiry set on the first increment, so the
/// window is atomic across instances and needs no sweeper. Modelled on
/// <c>RevealRateLimiter</c>, including the window-stamped key.
/// </para>
/// </remarks>
public sealed class SignInAddressThrottle(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    ILogger<SignInAddressThrottle> logger)
{
    /// <summary>Failed sign-ins allowed from one address per window.</summary>
    /// <remarks>
    /// Set well above the per-account threshold of five. A whole office can share
    /// one public address, so this has to tolerate several people mistyping their
    /// passwords in the same quarter hour while still being far below what an
    /// automated guessing run needs.
    /// </remarks>
    public const int MaxFailuresPerWindow = 25;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<SignInAddressThrottle> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Whether this address is currently out of budget. Does not consume any.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose, and called before the account is loaded. Consuming
    /// budget here would mean a successful sign-in from a busy office counted
    /// against the same limit as a guessing run.
    /// </remarks>
    public async Task<SignInAddressDecision> InspectAsync(
        string? sourceIp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceIp))
        {
            // No address to attribute the failure to. Not blocked: refusing here
            // would turn a missing header into an outage, and the per-account
            // lockout still applies.
            return new SignInAddressDecision(false, 0);
        }

        try
        {
            var count = (long?)await _redis.GetDatabase().StringGetAsync(Key(sourceIp)) ?? 0;

            if (count > MaxFailuresPerWindow)
            {
                return new SignInAddressDecision(true, (int)Window.TotalSeconds);
            }

            return new SignInAddressDecision(false, 0);
        }
        catch (RedisException ex)
        {
            return FailOpen(ex);
        }
    }

    /// <summary>Counts one failed sign-in against this address.</summary>
    public async Task RecordFailureAsync(string? sourceIp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceIp))
        {
            return;
        }

        try
        {
            var db = _redis.GetDatabase();
            var key = Key(sourceIp);
            var count = await db.StringIncrementAsync(key);

            // Only the first increment sets the expiry, which is what makes this a
            // fixed window rather than one that slides forward on every failure
            // and never expires under sustained abuse.
            if (count == 1)
            {
                await db.KeyExpireAsync(key, Window);
            }

            if (count == MaxFailuresPerWindow + 1)
            {
                // Logged once, on the transition, rather than on every subsequent
                // refusal: a sustained run would otherwise fill the log with the
                // same line and bury the event that matters.
                _logger.LogWarning(
                    "Sign-in address {SourceIp} exceeded {Max} failed attempts in {Minutes} minutes "
                    + "and is now refused for the rest of the window.",
                    sourceIp, MaxFailuresPerWindow, Window.TotalMinutes);
            }
        }
        catch (RedisException ex)
        {
            FailOpen(ex);
        }
    }

    /// <summary>
    /// Redis is unavailable: allow the attempt.
    /// </summary>
    /// <remarks>
    /// Fails open, deliberately, and the opposite way from
    /// <c>EphemeralSecretStore</c>. Failing closed here would make a Redis outage
    /// refuse every sign-in and lock every administrator out of the console at
    /// once. Two other brakes survive the outage: the ASP.NET per-address window
    /// on the login route, and the per-account lockout in PostgreSQL. Every
    /// attempt is audited either way.
    /// </remarks>
    private SignInAddressDecision FailOpen(RedisException ex)
    {
        _logger.LogError(ex,
            "Sign-in address throttle unavailable; allowing the attempt. "
            + "The per-account lockout and the request rate limit still apply.");

        return new SignInAddressDecision(false, 0);
    }

    /// <summary>
    /// Window-stamped so counters roll without a sweeper.
    /// </summary>
    /// <remarks>
    /// The address appears in the key. That is not a new disclosure - the audit
    /// trail already stores it as a native <c>inet</c> for every sign-in attempt -
    /// and no credential, token or e-mail address is ever part of a key.
    /// </remarks>
    private RedisKey Key(string sourceIp)
    {
        var window = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / (long)Window.TotalSeconds;
        return (RedisKey)$"auth:signin:addr:{sourceIp}:{window}";
    }
}
