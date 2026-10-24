using EndpointPlatform.Infrastructure.Chrome;

namespace EndpointPlatform.Infrastructure.Tests.Chrome;

/// <summary>
/// The profile status decision table: what the console says about a profile,
/// derived at read time from the stored facts.
/// </summary>
/// <remarks>
/// The precedence is the point, so it is pinned case by case rather than left to
/// a truth table nobody reads: a broken sign-in beats everything (it needs a
/// person whether or not the profile is used); dormancy beats the sign-in facts
/// (they describe a profile somebody still opens); and a freshly used profile
/// with no account is notSignedIn only while nothing says an account is there.
/// The strings are the wire contract the dashboard switches on, asserted
/// verbatim.
/// </remarks>
public sealed class ChromeProfileStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static string Derive(
        bool? signInBroken = null,
        bool? signedIn = null,
        string? accountEmail = null,
        DateTimeOffset? lastActiveAt = null) =>
        ChromeReadService.DeriveProfileStatus(signInBroken, signedIn, accountEmail, lastActiveAt, Now);

    private static DateTimeOffset DaysAgo(double days) => Now.AddDays(-days);

    [Fact]
    public void A_freshly_used_signed_in_profile_is_active()
    {
        Derive(signedIn: true, accountEmail: "someone@example.com", lastActiveAt: DaysAgo(1))
            .ShouldBe("active");
    }

    [Fact]
    public void A_broken_sign_in_wins_over_everything_else()
    {
        Derive(signInBroken: true, signedIn: true, accountEmail: "someone@example.com", lastActiveAt: DaysAgo(1))
            .ShouldBe("signInBroken", "a paused sign-in needs a person even on a profile in daily use");

        Derive(signInBroken: true, lastActiveAt: DaysAgo(30))
            .ShouldBe("signInBroken", "deliberate order: a broken sign-in beats dormancy");

        Derive(signInBroken: true, lastActiveAt: null)
            .ShouldBe("signInBroken");
    }

    [Fact]
    public void A_profile_not_used_for_over_a_week_is_dormant_whatever_its_account_says()
    {
        Derive(signedIn: true, accountEmail: "someone@example.com", lastActiveAt: DaysAgo(8))
            .ShouldBe("dormant", "sign-in facts describe a profile somebody still opens");
    }

    [Fact]
    public void A_profile_whose_last_use_was_never_recorded_is_dormant()
    {
        Derive(signedIn: true, accountEmail: "someone@example.com", lastActiveAt: null)
            .ShouldBe("dormant", "no recorded activity reads as none, not as fresh");
    }

    /// <summary>
    /// The window is <see cref="ChromeReadService.DormancyDays"/>, stated once:
    /// "older than" is strict, so exactly-seven-days is still the fresh side.
    /// </summary>
    [Fact]
    public void The_dormancy_window_is_seven_days_and_older_than_means_older()
    {
        ChromeReadService.DormancyDays.ShouldBe(7);

        Derive(accountEmail: "someone@example.com", lastActiveAt: DaysAgo(7)).ShouldBe("active");
        Derive(accountEmail: "someone@example.com", lastActiveAt: DaysAgo(7).AddMinutes(-1)).ShouldBe("dormant");
    }

    [Fact]
    public void A_freshly_used_profile_with_no_account_is_notSignedIn_not_active()
    {
        Derive(signedIn: null, accountEmail: null, lastActiveAt: DaysAgo(1)).ShouldBe("notSignedIn");
        Derive(signedIn: false, accountEmail: null, lastActiveAt: DaysAgo(1)).ShouldBe("notSignedIn");
        Derive(signedIn: false, accountEmail: "", lastActiveAt: DaysAgo(1)).ShouldBe("notSignedIn");
    }

    [Fact]
    public void An_explicit_signed_in_outranks_a_missing_email()
    {
        // Chrome records the two separately; an entry can say "signed in" before
        // the e-mail is written, and that profile is not a sign-in problem.
        Derive(signedIn: true, accountEmail: null, lastActiveAt: DaysAgo(1)).ShouldBe("active");
    }

    [Fact]
    public void An_email_alone_is_enough_for_active()
    {
        // Agents older than the signedIn fact report only the address; a profile
        // with an account and recent use must not read as a problem.
        Derive(signedIn: null, accountEmail: "someone@example.com", lastActiveAt: DaysAgo(1))
            .ShouldBe("active");
    }
}
