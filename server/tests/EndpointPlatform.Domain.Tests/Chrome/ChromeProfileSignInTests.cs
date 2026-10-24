using EndpointPlatform.Domain.Chrome;

namespace EndpointPlatform.Domain.Tests.Chrome;

/// <summary>
/// The sign-in lifecycle facts on a profile: optional, tri-state, and never
/// collapsed -- null means the agent did not say, which is not false.
/// </summary>
public sealed class ChromeProfileSignInTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ChromeProfile Profile(bool? signedIn, bool? signInBroken) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "S-1-5-21-1-2-3-1001", null, "Profile 1", null,
            @"C:\Users\someone\AppData\Local\Google\Chrome\User Data\Profile 1", null, null, Now,
            accountEmail: null, signedIn, signInBroken);

    [Fact]
    public void The_facts_are_kept_exactly_as_reported()
    {
        var reported = Profile(signedIn: true, signInBroken: false);

        reported.SignedIn.ShouldBe(true);
        reported.SignInBroken.ShouldBe(false);
    }

    [Fact]
    public void An_agent_that_did_not_say_leaves_both_unknown()
    {
        // The pre-lifecycle constructor shape: callers and fixtures written
        // before the fields bind unchanged and report unknown, not healthy.
        var older = new ChromeProfile(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "S-1-5-21-1-2-3-1001", null, "Default", null,
            @"C:\Users\someone\AppData\Local\Google\Chrome\User Data\Default", null, null, Now);

        older.SignedIn.ShouldBeNull("unknown is not false");
        older.SignInBroken.ShouldBeNull("unknown is not healthy");
    }
}
