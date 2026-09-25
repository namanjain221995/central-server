using EndpointPlatform.Domain.Chrome;

namespace EndpointPlatform.Domain.Tests.Chrome;

/// <summary>
/// What a Chrome profile row must be before it can exist.
/// </summary>
/// <remarks>
/// The three required fields are the profile's identity and location: the Windows
/// SID, the directory name Chrome keys it by, and the directory itself. A row
/// missing any of them could not be matched against the next upload, so the
/// constructor refuses it rather than leaving the unique index to find out.
/// </remarks>
public sealed class ChromeProfileTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Path = @"C:\Users\user\AppData\Local\Google\Chrome\User Data\Default";

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static ChromeProfile Create(
        Guid? deviceId = null,
        Guid? installationId = null,
        string userSid = Sid,
        string? userAccount = @"WORKGROUP\user",
        string profileKey = "Default",
        string? profileName = "Person 1",
        string profilePath = Path,
        bool? isManaged = false,
        DateTimeOffset? lastActiveAt = null) =>
        new(
            deviceId ?? Guid.CreateVersion7(),
            installationId ?? Guid.CreateVersion7(),
            userSid, userAccount, profileKey, profileName, profilePath, isManaged, lastActiveAt ?? Now.AddHours(-1), Now);

    [Fact]
    public void A_well_formed_profile_is_accepted_and_trimmed()
    {
        var deviceId = Guid.CreateVersion7();
        var installationId = Guid.CreateVersion7();

        var profile = Create(deviceId, installationId, userSid: $"  {Sid}  ", profileKey: " Profile 1 ");

        profile.DeviceId.ShouldBe(deviceId);
        profile.ChromeInstallationId.ShouldBe(installationId);
        profile.UserSid.ShouldBe(Sid);
        profile.UserAccount.ShouldBe(@"WORKGROUP\user");
        profile.ProfileKey.ShouldBe("Profile 1");
        profile.ProfileName.ShouldBe("Person 1");
        profile.ProfilePath.ShouldBe(Path);
        profile.IsManaged.ShouldBe(false);
        profile.LastActiveAt.ShouldBe(Now.AddHours(-1));
        profile.CollectedAt.ShouldBe(Now);
    }

    [Fact]
    public void Unrecorded_optional_facts_stay_null_rather_than_defaulting()
    {
        var profile = Create(userAccount: null, profileName: "  ", isManaged: null, lastActiveAt: null);

        profile.UserAccount.ShouldBeNull();
        profile.ProfileName.ShouldBeNull("whitespace is not a name");
        profile.IsManaged.ShouldBeNull("unknown is not false");
        // The helper substitutes a default for null, so build this one directly.
        new ChromeProfile(Guid.CreateVersion7(), Guid.CreateVersion7(), Sid, null, "Default", null, Path, null, null, Now)
            .LastActiveAt.ShouldBeNull();
    }

    // ---- identity is mandatory ---------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_profile_without_a_user_sid_cannot_exist(string? sid)
    {
        Should.Throw<ArgumentException>(() => Create(userSid: sid!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_profile_without_a_profile_key_cannot_exist(string? key)
    {
        Should.Throw<ArgumentException>(() => Create(profileKey: key!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_profile_without_a_path_cannot_exist(string? path)
    {
        Should.Throw<ArgumentException>(() => Create(profilePath: path!));
    }

    [Fact]
    public void Empty_owner_identifiers_are_refused()
    {
        Should.Throw<ArgumentException>(() => Create(deviceId: Guid.Empty));
        Should.Throw<ArgumentException>(() => Create(installationId: Guid.Empty));
    }

    // ---- the contract limits -----------------------------------------------

    /// <summary>
    /// The limits are the wire contract's <c>InventoryChromeProfile.Max*</c>
    /// constants, refused here again so no route but the Agent API can widen them.
    /// </summary>
    [Theory]
    [InlineData("userSid", 184)]
    [InlineData("userAccount", 256)]
    [InlineData("profileKey", 64)]
    [InlineData("profileName", 256)]
    [InlineData("profilePath", 512)]
    public void A_value_over_the_contract_limit_is_refused_and_one_at_the_limit_is_kept(string field, int limit)
    {
        var atLimit = new string('a', limit);
        var overLimit = new string('a', limit + 1);

        Should.NotThrow(() => CreateWith(field, atLimit));
        Should.Throw<ArgumentException>(() => CreateWith(field, overLimit));
    }

    private static ChromeProfile CreateWith(string field, string value) => Create(
        userSid: field == "userSid" ? value : Sid,
        userAccount: field == "userAccount" ? value : null,
        profileKey: field == "profileKey" ? value : "Default",
        profileName: field == "profileName" ? value : null,
        profilePath: field == "profilePath" ? value : Path);
}
