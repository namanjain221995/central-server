using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>Authentication policy for the Admin API.</summary>
public sealed class AdminAuthOptions
{
    public const string SectionName = "AdminAuth";

    /// <summary>Absolute session lifetime, hours. No sliding renewal in v1.</summary>
    [Range(1, 72)]
    public int SessionLifetimeHours { get; init; } = 12;

    /// <summary>Failed sign-ins before the account locks.</summary>
    [Range(3, 20)]
    public int LockoutThreshold { get; init; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>
    /// How long a failed sign-in counts toward the lockout threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without a decay the counter is a ratchet - it only ever moves toward locked
    /// - so an account accumulates failures across months of ordinary typos and
    /// eventually locks on an unrelated one. Sixty minutes is long enough that a
    /// real guessing run cannot outwait it at any useful rate, and short enough
    /// that yesterday's mistyped password is forgotten.
    /// </para>
    /// <para>
    /// This is NOT on its own the answer to somebody deliberately locking an
    /// administrator out; see <see cref="SignInAddressThrottle"/>, which is what
    /// stops a failing address from driving the counter at all.
    /// </para>
    /// </remarks>
    [Range(1, 10_080)]
    public int FailureDecayMinutes { get; init; } = 60;

    /// <summary>Sign-in attempts allowed per client address per minute.</summary>
    [Range(1, 10_000)]
    public int LoginAttemptsPerMinutePerAddress { get; init; } = 10;
}
