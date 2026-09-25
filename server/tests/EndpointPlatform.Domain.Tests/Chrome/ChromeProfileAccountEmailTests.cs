using EndpointPlatform.Domain.Chrome;

namespace EndpointPlatform.Domain.Tests.Chrome;

/// <summary>
/// The signed-in account e-mail on a profile: optional, bounded, and never a
/// requirement -- a profile nobody is signed in to is still a profile.
/// </summary>
public sealed class ChromeProfileAccountEmailTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static ChromeProfile Profile(string? accountEmail) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "S-1-5-21-1-2-3-1001", null, "Default", null,
            @"C:\Users\someone\AppData\Local\Google\Chrome\User Data\Default", null, null, Now, accountEmail);

    [Fact]
    public void The_email_is_kept_when_given_and_absent_when_not()
    {
        Profile("someone@example.com").AccountEmail.ShouldBe("someone@example.com");
        Profile(null).AccountEmail.ShouldBeNull();
        Profile("   ").AccountEmail.ShouldBeNull();
    }

    [Fact]
    public void An_email_over_the_column_width_is_refused()
    {
        Should.Throw<ArgumentException>(() => Profile(new string('a', 245) + "@example.com"));
        Profile(new string('a', 244) + "@example.com").AccountEmail!.Length.ShouldBe(256);
    }
}
