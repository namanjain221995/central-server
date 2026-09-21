using EndpointPlatform.Domain.Identity;

namespace EndpointPlatform.Domain.Tests.Identity;

/// <summary>
/// How the failed sign-in counter ages, and why it has to.
/// </summary>
/// <remarks>
/// <para>
/// These pin a denial-of-service fix. The counter used to have no age and the
/// status was never cleared when a lockout expired, so five failures locked an
/// account for fifteen minutes and then left it at <c>Locked</c> with the count
/// still at the threshold - one failure away from re-locking. A single wrong
/// password every fifteen minutes kept a named administrator locked out
/// indefinitely, and on an internet-facing console the e-mail address needed to
/// aim it is simply their username.
/// </para>
/// <para>
/// <b>The decay window is only half the fix</b>, and the last test here says so
/// explicitly. An attacker can still burst the whole threshold inside the window.
/// What stops them is that <c>AdminAuthService</c> refuses to call this method at
/// all once the source address is throttled. Anyone changing one half should read
/// the test that documents the other.
/// </para>
/// </remarks>
public sealed class PlatformUserLockoutDecayTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private const int Threshold = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Decay = TimeSpan.FromHours(1);

    private static PlatformUser CreateUser()
    {
        var user = new PlatformUser(OrganizationId, "Admin@Company.Local", "Test Admin");
        user.SetPasswordHash("hash", Now);
        return user;
    }

    private static void Fail(PlatformUser user, DateTimeOffset at) =>
        user.RecordFailedSignIn(at, Threshold, Lockout, Decay);

    /// <summary>A failure older than the window no longer counts.</summary>
    [Fact]
    public void Failures_outside_the_decay_window_are_forgotten()
    {
        var user = CreateUser();

        for (var attempt = 0; attempt < Threshold - 1; attempt++)
        {
            Fail(user, Now);
        }

        user.FailedSignInCount.ShouldBe(Threshold - 1);

        // Long enough later that the earlier run is irrelevant.
        Fail(user, Now.Add(Decay).AddMinutes(1));

        user.FailedSignInCount.ShouldBe(1);
        user.Status.ShouldNotBe(PlatformUserStatus.Locked);
    }

    /// <summary>Failures inside the window still accumulate.</summary>
    [Fact]
    public void Failures_inside_the_decay_window_still_lock_the_account()
    {
        var user = CreateUser();

        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            Fail(user, Now.AddMinutes(attempt));
        }

        user.Status.ShouldBe(PlatformUserStatus.Locked);
        user.IsLockedOut(Now.AddMinutes(Threshold)).ShouldBeTrue();
    }

    /// <summary>
    /// The regression itself: an expired lockout does not leave the account primed
    /// to re-lock on one further failure.
    /// </summary>
    /// <remarks>
    /// This is the exact shape of the old denial of service. Before the fix the
    /// single failure below returned the account to Locked, because the counter
    /// was still at the threshold and the status was still Locked.
    /// </remarks>
    [Fact]
    public void One_failure_after_a_lockout_expires_does_not_re_lock_the_account()
    {
        var user = CreateUser();

        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            Fail(user, Now);
        }

        user.Status.ShouldBe(PlatformUserStatus.Locked);

        // The lockout has expired but the decay window has not, which is the
        // interval an attacker would aim for.
        var afterLockout = Now.Add(Lockout).AddMinutes(1);
        user.IsLockedOut(afterLockout).ShouldBeFalse();

        Fail(user, afterLockout);

        user.Status.ShouldNotBe(PlatformUserStatus.Locked);
        user.IsLockedOut(afterLockout).ShouldBeFalse();
        user.FailedSignInCount.ShouldBe(1);
    }

    /// <summary>An expired lockout restores the status the account should have.</summary>
    [Fact]
    public void An_expired_lockout_returns_an_account_with_a_password_to_active()
    {
        var user = CreateUser();

        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            Fail(user, Now);
        }

        Fail(user, Now.Add(Lockout).AddMinutes(1));

        user.Status.ShouldBe(PlatformUserStatus.Active);
    }

    /// <summary>
    /// A disabled account is never revived by a failed sign-in.
    /// </summary>
    /// <remarks>
    /// The expiry reset clears only <see cref="PlatformUserStatus.Locked"/>.
    /// Clearing any status would let anyone re-enable a deliberately disabled
    /// account by typing a wrong password at it.
    /// </remarks>
    [Fact]
    public void A_disabled_account_is_not_revived_by_a_failed_sign_in()
    {
        var user = CreateUser();
        user.Disable();

        Fail(user, Now);
        Fail(user, Now.Add(Decay).AddMinutes(1));

        user.Status.ShouldBe(PlatformUserStatus.Disabled);
    }

    /// <summary>The timestamp is cleared wherever the counter is.</summary>
    /// <remarks>
    /// If the two disagreed, a cleared counter could still carry an old timestamp
    /// and the next failure would be judged against the wrong age.
    /// </remarks>
    [Fact]
    public void Clearing_the_counter_also_clears_the_timestamp()
    {
        var user = CreateUser();

        Fail(user, Now);
        user.LastFailedSignInAt.ShouldBe(Now);

        user.RecordSuccessfulSignIn(Now.AddMinutes(1));
        user.LastFailedSignInAt.ShouldBeNull();

        Fail(user, Now.AddMinutes(2));
        user.LastFailedSignInAt.ShouldBe(Now.AddMinutes(2));

        user.Enable();
        user.LastFailedSignInAt.ShouldBeNull();

        Fail(user, Now.AddMinutes(3));
        user.SetPasswordHash("hash2", Now.AddMinutes(4));
        user.LastFailedSignInAt.ShouldBeNull();
        user.FailedSignInCount.ShouldBe(0);
    }

    /// <summary>
    /// The decay window ALONE does not stop a deliberate lockout, and that is why
    /// the address throttle exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Documented as a passing test rather than left implicit, because the decay
    /// window looks like a complete fix and is not. An attacker who can keep
    /// reaching this method simply sends the whole threshold in a burst, waits for
    /// the lockout to expire, and repeats - the account is locked essentially all
    /// of the time.
    /// </para>
    /// <para>
    /// The real defence is that <c>AdminAuthService</c> checks
    /// <c>SignInAddressThrottle</c> BEFORE it loads the account and returns without
    /// calling this method at all when the address is out of budget. If that
    /// ordering is ever changed, this test still passes and the hole is open again
    /// - so it is spelled out here.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_burst_every_lockout_period_keeps_an_account_locked_which_is_the_address_throttles_job()
    {
        var user = CreateUser();
        var at = Now;

        for (var round = 0; round < 4; round++)
        {
            for (var attempt = 0; attempt < Threshold; attempt++)
            {
                Fail(user, at);
            }

            user.IsLockedOut(at).ShouldBeTrue();

            // Wait just past the lockout and go again.
            at = at.Add(Lockout).AddSeconds(1);
        }

        // Still locked for almost all of the elapsed time. The domain cannot fix
        // this on its own; only refusing the attacker's address can.
        user.IsLockedOut(at.AddSeconds(-2)).ShouldBeTrue();
    }
}
