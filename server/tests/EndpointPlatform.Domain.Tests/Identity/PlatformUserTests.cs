using EndpointPlatform.Domain.Identity;

namespace EndpointPlatform.Domain.Tests.Identity;

public sealed class PlatformUserTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static PlatformUser CreateUser() =>
        new(OrganizationId, "Admin@Company.Local", "Test Admin");

    [Fact]
    public void New_user_starts_invited_and_without_a_password()
    {
        var user = CreateUser();

        user.Status.ShouldBe(PlatformUserStatus.Invited);
        user.PasswordHash.ShouldBeNull();
        user.PasswordUpdatedAt.ShouldBeNull();
        user.SecurityStamp.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Email_is_trimmed_and_normalised_for_lookup()
    {
        var user = new PlatformUser(OrganizationId, "  Admin@Company.Local  ", "Test Admin");

        user.Email.ShouldBe("Admin@Company.Local");
        user.NormalizedEmail.ShouldBe("ADMIN@COMPANY.LOCAL");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Email_is_required(string? email)
    {
        Should.Throw<ArgumentException>(() => new PlatformUser(OrganizationId, email!, "Test Admin"));
    }

    [Fact]
    public void Organization_id_is_required()
    {
        Should.Throw<ArgumentException>(() => new PlatformUser(Guid.Empty, "a@b.local", "Test Admin"));
    }

    [Fact]
    public void Setting_a_password_hash_activates_an_invited_user()
    {
        var user = CreateUser();

        user.SetPasswordHash("$argon2id$v=19$m=65536,t=3,p=1$abc$def", Now);

        user.Status.ShouldBe(PlatformUserStatus.Active);
        user.PasswordUpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Setting_a_password_hash_rotates_the_security_stamp()
    {
        // The stamp is what invalidates already-issued access tokens. If a credential
        // change did not rotate it, tokens minted before a password reset would keep
        // working - which defeats the point of resetting a compromised password.
        var user = CreateUser();
        var before = user.SecurityStamp;

        user.SetPasswordHash("hash", Now);

        user.SecurityStamp.ShouldNotBe(before);
    }

    [Fact]
    public void Disabling_a_user_rotates_the_security_stamp()
    {
        var user = CreateUser();
        user.SetPasswordHash("hash", Now);
        var before = user.SecurityStamp;

        user.Disable();

        user.Status.ShouldBe(PlatformUserStatus.Disabled);
        user.SecurityStamp.ShouldNotBe(before, "a disabled user's outstanding tokens must stop validating");
    }

    [Fact]
    public void Assigning_a_role_rotates_the_security_stamp()
    {
        // Same reasoning: a permission change must take effect immediately, not at
        // the natural expiry of a token issued under the old role set.
        var user = CreateUser();
        var before = user.SecurityStamp;

        user.AssignRole(Guid.CreateVersion7());

        user.Roles.Count.ShouldBe(1);
        user.SecurityStamp.ShouldNotBe(before);
    }

    [Fact]
    public void Assigning_the_same_role_twice_is_idempotent_and_does_not_rotate_the_stamp()
    {
        var user = CreateUser();
        var roleId = Guid.CreateVersion7();
        user.AssignRole(roleId);
        var afterFirst = user.SecurityStamp;

        user.AssignRole(roleId);

        user.Roles.Count.ShouldBe(1);
        user.SecurityStamp.ShouldBe(afterFirst);
    }

    [Fact]
    public void Removing_a_role_that_was_never_assigned_does_not_rotate_the_stamp()
    {
        var user = CreateUser();
        var before = user.SecurityStamp;

        user.RemoveRole(Guid.CreateVersion7());

        user.SecurityStamp.ShouldBe(before);
    }

    [Fact]
    public void Repeated_failed_sign_ins_lock_the_account()
    {
        var user = CreateUser();
        user.SetPasswordHash("hash", Now);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            user.RecordFailedSignIn(
                Now,
                lockoutThreshold: 5,
                lockoutDuration: TimeSpan.FromMinutes(15),
                decayWindow: TimeSpan.FromHours(1));
        }

        user.Status.ShouldBe(PlatformUserStatus.Locked);
        user.IsLockedOut(Now).ShouldBeTrue();
    }

    [Fact]
    public void Lockout_expires_after_the_configured_duration()
    {
        var user = CreateUser();
        user.SetPasswordHash("hash", Now);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            user.RecordFailedSignIn(
                Now,
                lockoutThreshold: 5,
                lockoutDuration: TimeSpan.FromMinutes(15),
                decayWindow: TimeSpan.FromHours(1));
        }

        user.IsLockedOut(Now.AddMinutes(14)).ShouldBeTrue();
        user.IsLockedOut(Now.AddMinutes(16)).ShouldBeFalse();
    }

    [Fact]
    public void A_successful_sign_in_clears_the_failure_counter()
    {
        var user = CreateUser();
        user.SetPasswordHash("hash", Now);
        user.RecordFailedSignIn(Now, 5, TimeSpan.FromMinutes(15), TimeSpan.FromHours(1));
        user.RecordFailedSignIn(Now, 5, TimeSpan.FromMinutes(15), TimeSpan.FromHours(1));

        user.RecordSuccessfulSignIn(Now);

        user.FailedSignInCount.ShouldBe(0);
        user.LastLoginAt.ShouldBe(Now);
    }

    [Fact]
    public void Re_enabling_a_user_who_never_set_a_password_returns_them_to_invited()
    {
        var user = CreateUser();
        user.Disable();

        user.Enable();

        user.Status.ShouldBe(PlatformUserStatus.Invited);
    }
}
