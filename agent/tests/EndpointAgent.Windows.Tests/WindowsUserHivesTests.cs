using System.Security.Principal;
using EndpointAgent.Windows;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The loaded-hive helper every per-user source shares, against this machine.
/// </summary>
public sealed class WindowsUserHivesTests
{
    private static string CurrentSid() => WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public void The_current_user_is_among_the_loaded_hives()
    {
        var hives = WindowsUserHives.Loaded();

        hives.ShouldContain(h => h.Sid == CurrentSid());
        hives.ShouldAllBe(h => WindowsUserHives.IsRealUserSid(h.Sid));
        hives.ShouldAllBe(h => !string.IsNullOrWhiteSpace(h.Account));
    }

    [Theory]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-19", false)]
    [InlineData("S-1-5-20", false)]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-5-21-1-2-3-1001_Classes", false)]
    [InlineData("S-1-12-1-1-2-3-4", true)]
    [InlineData(".DEFAULT", false)]
    public void Only_people_are_real_user_sids(string sid, bool real)
    {
        WindowsUserHives.IsRealUserSid(sid).ShouldBe(real);
    }

    [Fact]
    public void The_current_users_profile_path_is_a_real_directory()
    {
        var profile = WindowsUserHives.ProfilePath(CurrentSid()).ShouldNotBeNull();

        Directory.Exists(profile).ShouldBeTrue(profile);
        Path.IsPathFullyQualified(profile).ShouldBeTrue();
    }

    [Fact]
    public void The_current_users_start_menu_is_a_local_absolute_directory()
    {
        var programs = WindowsUserHives.StartMenuPrograms(CurrentSid()).ShouldNotBeNull();

        Path.IsPathFullyQualified(programs).ShouldBeTrue();
        programs.ShouldNotStartWith(@"\\");
        programs.ShouldEndWith(@"\Start Menu\Programs", Case.Insensitive);
    }

    [Theory]
    [InlineData("S-1-5-21-0-0-0-424242")]
    [InlineData("")]
    [InlineData(@"S-1-5-21-1-2-3-1001\..\..\SOFTWARE")]
    public void An_unknown_or_malformed_sid_has_no_profile(string sid)
    {
        WindowsUserHives.ProfilePath(sid).ShouldBeNull();
        WindowsUserHives.StartMenuPrograms(sid).ShouldBeNull();
    }

    /// <summary>
    /// The on-disk enumeration sees the current user, and the directory it names
    /// is the one they are signed into.
    /// </summary>
    [Fact]
    public void All_profiles_include_the_current_user_with_an_existing_directory()
    {
        var profiles = WindowsUserHives.AllProfiles();

        profiles.ShouldContain(p => p.Sid == CurrentSid());

        var mine = profiles.Single(p => p.Sid == CurrentSid());
        Directory.Exists(mine.Path).ShouldBeTrue(mine.Path);
    }

    /// <summary>
    /// The on-disk enumeration leaves names to the caller (an LSA lookup per
    /// stale domain SID is what made it too slow to do up front), so the
    /// resolution a caller relies on is pinned here on its own.
    /// </summary>
    [Fact]
    public void The_current_users_sid_resolves_to_an_account_name()
    {
        var account = WindowsUserHives.ResolveAccountName(CurrentSid());

        account.ShouldNotBeNullOrWhiteSpace();
        account.ShouldNotBe(CurrentSid());
        account.ShouldContain("\\");
    }

    /// <summary>
    /// The profile list holds entries for SYSTEM and the two service accounts and,
    /// after a failed logon, a "&lt;SID&gt;.bak" entry; none of them is a person.
    /// </summary>
    [Fact]
    public void All_profiles_are_people_and_never_the_service_accounts()
    {
        var profiles = WindowsUserHives.AllProfiles();

        profiles.ShouldAllBe(p => WindowsUserHives.IsRealUserSid(p.Sid));
        profiles.ShouldNotContain(p => p.Sid == "S-1-5-18" || p.Sid == "S-1-5-19" || p.Sid == "S-1-5-20");
        profiles.ShouldAllBe(p => !p.Sid.Contains('.'));
        profiles.Select(p => p.Sid).ShouldBeUnique();
    }

    /// <summary>
    /// Every path is somewhere the service may walk: absolute, and on this
    /// machine rather than on a share.
    /// </summary>
    [Fact]
    public void Every_profile_path_is_a_local_absolute_directory()
    {
        var profiles = WindowsUserHives.AllProfiles();

        profiles.ShouldNotBeEmpty();
        profiles.ShouldAllBe(p => Path.IsPathFullyQualified(p.Path));
        profiles.ShouldAllBe(p => !p.Path.StartsWith(@"\\"));
    }
}
