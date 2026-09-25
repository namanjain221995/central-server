using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Chrome inventory ingestion: what an endpoint reports about the browser, its
/// profiles and their extensions, and what the server makes of it.
/// </summary>
/// <remarks>
/// <para>
/// The section has three statuses and they are not interchangeable. Available and
/// NotInstalled are complete answers and replace the stored profiles wholesale;
/// Error means the agent's enumeration was cut short, and a fragment must not
/// replace the last complete picture. The tests here pin each of those, plus the
/// payloads a well-behaved agent never sends: duplicate profiles and ids, more of
/// either than any machine has, and identifiers that are not Chrome's.
/// </para>
/// <para>
/// Nothing here is audited, and nothing here checks that it is. Inventory arrives
/// every cycle; a row per upload would bury the events an auditor is looking for.
/// </para>
/// <para>
/// Every identifier below is invented. The SIDs are shaped like Windows account
/// SIDs and belong to no machine; the extension ids are 32 letters in a-p, as
/// Chrome's are, and name no real extension.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class ChromeIngestionTests(AgentApiPostgresFixture fixture)
{
    private const string FirstSid = "S-1-5-21-1000-2000-3000-1001";
    private const string SecondSid = "S-1-5-21-1000-2000-3000-1002";
    private const string DefaultProfilePath = @"C:\Users\someone\AppData\Local\Google\Chrome\User Data\Default";
    private const string OtherUserProfilePath = @"C:\Users\other\AppData\Local\Google\Chrome\User Data\Default";
    private const string ChromeExecutable = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string UpdateUrl = "https://extensions.example.com/update2/crx";

    private const string TidyId = "abcdefghijklmnopabcdefghijklmnop";
    private const string ClipId = "ppppoooonnnnmmmmllllkkkkjjjjiiii";
    private const string BuiltInId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // Fixed instants: PostgreSQL keeps microseconds, so a DateTimeOffset.UtcNow
    // with its 100 ns ticks would not round-trip equal.
    private static readonly DateTimeOffset LastActiveInstant = new(2026, 9, 20, 8, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InstalledInstant = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExtensionUpdatedInstant = new(2026, 9, 18, 6, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdateCheckInstant = new(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);

    private readonly AgentApiPostgresFixture _fixture = fixture;

    // Chrome is the trailing section of the report, after BitLocker: four required
    // arguments, then eight optional sections that are not under test here.
    private static InventoryReport Report(InventoryChrome? chrome) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, chrome);

    private static InventoryChrome Section(
        string status = "Available",
        InventoryChromeInstallation? installation = null,
        IReadOnlyList<InventoryChromeProfile>? profiles = null) =>
        new(status, installation, profiles ?? []);

    private static InventoryChromeInstallation Installation(string version = "131.0.6778.86") =>
        new(
            Version: version,
            ExecutablePath: ChromeExecutable,
            Architecture: "x64",
            Channel: "stable",
            InstallationScope: "Machine",
            InstalledForUser: null,
            UpdaterVersion: "1.3.195.35",
            LastUpdateCheck: UpdateCheckInstant);

    private static InventoryChromeProfile Profile(
        string userSid = FirstSid,
        string profileKey = "Default",
        string? profileName = "Person 1",
        string profilePath = DefaultProfilePath,
        string? userAccount = @"WORKGROUP\someone",
        IReadOnlyList<InventoryChromeExtension>? extensions = null) =>
        new(
            UserSid: userSid,
            UserAccount: userAccount,
            ProfileKey: profileKey,
            ProfileName: profileName,
            ProfilePath: profilePath,
            IsManaged: false,
            LastActiveAt: LastActiveInstant,
            Extensions: extensions ?? []);

    private static InventoryChromeExtension Extension(
        string extensionId = TidyId,
        string? name = "Tab Tidy",
        string installType = "Internal",
        int? manifestVersion = 3,
        bool isManaged = false) =>
        new(
            ExtensionId: extensionId,
            Name: name,
            Version: "4.2.0",
            ManifestVersion: manifestVersion,
            Enabled: true,
            InstallType: installType,
            IsManaged: isManaged,
            FromWebStore: true,
            UpdateUrl: UpdateUrl,
            InstalledAt: InstalledInstant,
            UpdatedAt: ExtensionUpdatedInstant);

    /// <summary>
    /// A distinct, well-formed extension id per index: each hex digit of the index
    /// becomes one letter of a-p, padded to Chrome's 32.
    /// </summary>
    private static string ExtensionIdFor(int index)
    {
        var suffix = string.Concat(index.ToString("x8")
            .Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))));
        return new string('c', 24) + suffix;
    }

    private static InventoryChromeProfile NumberedProfile(int index) =>
        Profile(
            profileKey: $"Profile {index}",
            profileName: $"Person {index}",
            profilePath: $@"C:\Users\someone\AppData\Local\Google\Chrome\User Data\Profile {index}");

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"chr-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "CHR-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage Req(string route, object body, string? credential = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        { Content = JsonContent.Create(body) };
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    private Task<HttpResponseMessage> UploadAsync(HttpClient client, string credential, InventoryChrome? chrome) =>
        client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(chrome), credential));

    private async Task<(ChromeInstallation? Installation, List<ChromeProfile> Profiles, List<ChromeExtension> Extensions)>
        StoredAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var installation = await db.ChromeInstallations.SingleOrDefaultAsync(i => i.DeviceId == deviceId);
        var profiles = await db.ChromeProfiles.Where(p => p.DeviceId == deviceId).OrderBy(p => p.ProfileKey).ToListAsync();
        var extensions = await db.ChromeExtensions.Where(e => e.DeviceId == deviceId).OrderBy(e => e.ExtensionId).ToListAsync();
        return (installation, profiles, extensions);
    }

    // ---- persistence -------------------------------------------------------

    [Fact]
    public async Task A_full_section_persists_the_installation_its_profiles_and_their_extensions()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles:
            [
                Profile(extensions:
                [
                    Extension(),
                    Extension(ClipId, "Note Clip", "ExternalPolicy", manifestVersion: 2, isManaged: true),
                ]),
                Profile(SecondSid, "Profile 1", "Person 2", OtherUserProfilePath, @"WORKGROUP\other",
                    extensions: [Extension(BuiltInId, "Built In", "Component")]),
            ]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (installation, profiles, extensions) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull();
        installation.Status.ShouldBe(ChromeReportStatus.Available);
        installation.Version.ShouldBe("131.0.6778.86");
        installation.ExecutablePath.ShouldBe(ChromeExecutable);
        installation.Architecture.ShouldBe("x64");
        installation.Channel.ShouldBe("stable");
        installation.InstallationScope.ShouldBe("Machine");
        installation.InstalledForUser.ShouldBeNull();
        installation.UpdaterVersion.ShouldBe("1.3.195.35");
        installation.LastUpdateCheck.ShouldBe(UpdateCheckInstant);
        installation.IsInstalled.ShouldBeTrue();

        profiles.Count.ShouldBe(2);

        var first = profiles.Single(p => p.ProfileKey == "Default");
        first.ChromeInstallationId.ShouldBe(installation.Id);
        first.UserSid.ShouldBe(FirstSid);
        first.UserAccount.ShouldBe(@"WORKGROUP\someone");
        first.ProfileName.ShouldBe("Person 1");
        first.ProfilePath.ShouldBe(DefaultProfilePath);
        first.IsManaged.ShouldBe(false);
        first.LastActiveAt.ShouldBe(LastActiveInstant);

        var second = profiles.Single(p => p.ProfileKey == "Profile 1");
        second.ChromeInstallationId.ShouldBe(installation.Id);
        second.UserSid.ShouldBe(SecondSid);
        second.UserAccount.ShouldBe(@"WORKGROUP\other");
        second.ProfileName.ShouldBe("Person 2");
        second.ProfilePath.ShouldBe(OtherUserProfilePath);

        extensions.Count.ShouldBe(3);

        var tidy = extensions.Single(e => e.ExtensionId == TidyId);
        tidy.ChromeProfileId.ShouldBe(first.Id);
        tidy.Name.ShouldBe("Tab Tidy");
        tidy.Version.ShouldBe("4.2.0");
        tidy.ManifestVersion.ShouldBe(3);
        tidy.Enabled.ShouldBe(true);
        tidy.InstallType.ShouldBe(ChromeExtensionInstallType.Internal);
        tidy.IsManaged.ShouldBeFalse();
        tidy.FromWebStore.ShouldBe(true);
        tidy.UpdateUrl.ShouldBe(UpdateUrl);
        tidy.InstalledAt.ShouldBe(InstalledInstant);
        tidy.ExtensionUpdatedAt.ShouldBe(ExtensionUpdatedInstant);
        tidy.IsComponent.ShouldBeFalse();

        var clip = extensions.Single(e => e.ExtensionId == ClipId);
        clip.ChromeProfileId.ShouldBe(first.Id);
        clip.InstallType.ShouldBe(ChromeExtensionInstallType.ExternalPolicy);
        clip.IsManaged.ShouldBeTrue();
        clip.ManifestVersion.ShouldBe(2);

        extensions.Single(e => e.ExtensionId == BuiltInId).ChromeProfileId.ShouldBe(second.Id);
    }

    /// <summary>
    /// The installation is one row per device, re-applied in place; the profiles
    /// and extensions under it are a snapshot and go wholesale. A profile that is
    /// reported again lands under the same unique key it just vacated, which is the
    /// delete-then-insert the persistence layer has to order correctly.
    /// </summary>
    [Fact]
    public async Task A_second_upload_replaces_profiles_and_extensions_and_keeps_one_installation_row()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, Section(
            installation: Installation("130.0.6723.117"),
            profiles:
            [
                Profile(extensions: [Extension(), Extension(ClipId, "Note Clip")]),
                Profile(SecondSid, "Profile 1", "Person 2", OtherUserProfilePath, @"WORKGROUP\other"),
            ]));
        var firstRowId = (await StoredAsync(deviceId)).Installation.ShouldNotBeNull().Id;

        var second = await UploadAsync(client, credential, Section(
            installation: Installation("131.0.6778.86"),
            profiles: [Profile(profileName: "Renamed", extensions: [Extension(ClipId, "Note Clip")])]));
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (installation, profiles, extensions) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull();
        installation.Id.ShouldBe(firstRowId);
        installation.Version.ShouldBe("131.0.6778.86");

        profiles.Count.ShouldBe(1);
        profiles.Single().ProfileKey.ShouldBe("Default");
        profiles.Single().ProfileName.ShouldBe("Renamed");

        extensions.Count.ShouldBe(1);
        extensions.Single().ExtensionId.ShouldBe(ClipId);
        extensions.Single().ChromeProfileId.ShouldBe(profiles.Single().Id);

        await using var db = _fixture.CreateDbContext();
        (await db.ChromeInstallations.CountAsync(i => i.DeviceId == deviceId)).ShouldBe(1);
    }

    /// <summary>
    /// An agent that predates this section omits it, and the server keeps whatever
    /// it last knew rather than treating the omission as "no Chrome".
    /// </summary>
    [Fact]
    public async Task An_agent_that_reports_no_chrome_section_leaves_the_stored_rows_alone()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: [Extension()])]));
        (await UploadAsync(client, credential, null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (installation, profiles, extensions) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull();
        installation.Status.ShouldBe(ChromeReportStatus.Available);
        installation.Version.ShouldBe("131.0.6778.86");
        profiles.Count.ShouldBe(1);
        extensions.Count.ShouldBe(1);
    }

    // ---- status semantics --------------------------------------------------

    /// <summary>
    /// Error means the enumeration was cut short and the list is a fragment. The
    /// status is recorded so the console can say so, but a fragment must not
    /// replace the last complete picture: the same keep-last-known rule BitLocker
    /// applies to a query that did not succeed.
    /// </summary>
    [Fact]
    public async Task An_error_status_updates_the_installation_but_keeps_the_last_known_profiles()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles:
            [
                Profile(extensions: [Extension(), Extension(ClipId, "Note Clip")]),
                Profile(SecondSid, "Profile 1", "Person 2", OtherUserProfilePath, @"WORKGROUP\other"),
            ]));

        // The agent could read one profile directory before something failed.
        var partial = await UploadAsync(client, credential, Section(
            "Error",
            installation: Installation(),
            profiles: [Profile()]));
        partial.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (installation, profiles, extensions) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull();
        installation.Status.ShouldBe(ChromeReportStatus.Error);
        installation.Version.ShouldBe("131.0.6778.86");
        profiles.Count.ShouldBe(2);
        extensions.Count.ShouldBe(2);
    }

    /// <summary>
    /// NotInstalled is a complete answer, so the list it carries is the truth: an
    /// empty one means the uninstaller took User Data with it.
    /// </summary>
    [Fact]
    public async Task Not_installed_with_no_profiles_clears_the_stored_profiles()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: [Extension()])]));

        var removed = await UploadAsync(client, credential, Section("NotInstalled", installation: null, profiles: []));
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (installation, profiles, extensions) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull();
        installation.Status.ShouldBe(ChromeReportStatus.NotInstalled);
        installation.Version.ShouldBeNull();
        installation.IsInstalled.ShouldBeFalse();
        profiles.ShouldBeEmpty();
        extensions.ShouldBeEmpty();
    }

    /// <summary>
    /// Chrome's uninstaller leaves User Data behind by default, and what it
    /// recorded there is still fact. The profiles are stored under a NotInstalled
    /// row; a non-empty profile list is never read as "Chrome is present".
    /// </summary>
    [Fact]
    public async Task Not_installed_with_leftover_profiles_keeps_them_stored()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            "NotInstalled",
            installation: null,
            profiles: [Profile(extensions: [Extension()])]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (installation, profiles, extensions) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull();
        installation.Status.ShouldBe(ChromeReportStatus.NotInstalled);
        installation.IsInstalled.ShouldBeFalse();
        profiles.Count.ShouldBe(1);
        extensions.Count.ShouldBe(1);
    }

    /// <summary>
    /// The agent reports "Available" with no installation entry when it found
    /// Chrome but could not read a version for it, because the contract requires
    /// one. That is a report of an installation the console can say nothing
    /// about: accepted, stored as Available with no version, and not installed.
    /// </summary>
    [Fact]
    public async Task Available_with_no_installation_entry_is_stored_as_available_without_a_version()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(installation: null, profiles: [Profile()]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (installation, profiles, _) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull();
        installation.Status.ShouldBe(ChromeReportStatus.Available);
        installation.Version.ShouldBeNull();
        installation.IsInstalled.ShouldBeFalse("a status the agent could not back with a version is not an installation");
        profiles.Count.ShouldBe(1);
    }

    // ---- timestamps --------------------------------------------------------

    /// <summary>
    /// The shipped agent stamps UTC, but the wire carries an offset and a
    /// differently-serialising or hostile agent may send one. PostgreSQL's
    /// <c>timestamptz</c> accepts only offset zero, so every agent-supplied
    /// instant is normalised to UTC before it is stored: the upload succeeds and
    /// the instant, not its representation, is what is kept. Without that, one
    /// such value would fail the whole upload -- every section, on every retry.
    /// </summary>
    [Fact]
    public async Task Timestamps_with_a_non_utc_offset_are_stored_as_the_same_instant()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var indiaStandardTime = TimeSpan.FromMinutes(330);
        var response = await UploadAsync(client, credential, Section(
            installation: Installation() with { LastUpdateCheck = UpdateCheckInstant.ToOffset(indiaStandardTime) },
            profiles:
            [
                Profile(extensions:
                [
                    Extension() with
                    {
                        InstalledAt = InstalledInstant.ToOffset(indiaStandardTime),
                        UpdatedAt = ExtensionUpdatedInstant.ToOffset(indiaStandardTime),
                    },
                ]) with { LastActiveAt = LastActiveInstant.ToOffset(indiaStandardTime) },
            ]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "an offset is a representation of an instant, not a malformed value");

        var (installation, profiles, extensions) = await StoredAsync(deviceId);

        installation.ShouldNotBeNull().LastUpdateCheck.ShouldBe(UpdateCheckInstant);
        profiles.Single().LastActiveAt.ShouldBe(LastActiveInstant);
        extensions.Single().InstalledAt.ShouldBe(InstalledInstant);
        extensions.Single().ExtensionUpdatedAt.ShouldBe(ExtensionUpdatedInstant);
    }

    // ---- duplicates and caps -----------------------------------------------

    /// <summary>
    /// (SID, directory name) is the profile's identity and NTFS is
    /// case-insensitive, so "Default" and "default" are one directory. The first
    /// entry wins, as for every other duplicate in an inventory upload.
    /// </summary>
    [Fact]
    public async Task Duplicate_profiles_collapse_to_the_first_regardless_of_case()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles:
            [
                Profile(profileKey: "Default", profileName: "First"),
                Profile(profileKey: "default", profileName: "Second"),
                Profile(profileKey: "DEFAULT", profileName: "Third"),
            ]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, profiles, _) = await StoredAsync(deviceId);

        profiles.Count.ShouldBe(1);
        profiles.Single().ProfileKey.ShouldBe("Default");
        profiles.Single().ProfileName.ShouldBe("First");
    }

    [Fact]
    public async Task Duplicate_extension_ids_within_a_profile_collapse_to_the_first()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles:
            [
                Profile(extensions:
                [
                    Extension(TidyId, "First"),
                    Extension(TidyId, "Second"),
                    Extension(ClipId, "Note Clip"),
                ]),
            ]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, _, extensions) = await StoredAsync(deviceId);

        extensions.Count.ShouldBe(2);
        extensions.Single(e => e.ExtensionId == TidyId).Name.ShouldBe("First");
    }

    [Fact]
    public async Task Profiles_beyond_the_cap_are_dropped_rather_than_refused()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var profiles = Enumerable.Range(0, Infrastructure.Devices.DeviceInventoryService.MaxChromeProfiles + 1)
            .Select(NumberedProfile)
            .ToList();

        var response = await UploadAsync(client, credential, Section(installation: Installation(), profiles: profiles));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, stored, _) = await StoredAsync(deviceId);

        stored.Count.ShouldBe(Infrastructure.Devices.DeviceInventoryService.MaxChromeProfiles);
    }

    [Fact]
    public async Task Extensions_beyond_the_cap_are_dropped_rather_than_refused()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var extensions = Enumerable.Range(0, Infrastructure.Devices.DeviceInventoryService.MaxChromeExtensionsPerProfile + 1)
            .Select(i => Extension(ExtensionIdFor(i), $"Extension {i}"))
            .ToList();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: extensions)]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, _, stored) = await StoredAsync(deviceId);

        stored.Count.ShouldBe(Infrastructure.Devices.DeviceInventoryService.MaxChromeExtensionsPerProfile);
    }

    /// <summary>
    /// The cap counts what is kept. A duplicate is dropped before it is counted,
    /// so a payload that repeats one profile ahead of a full set of real ones
    /// still stores every real one rather than losing the last to the repeat.
    /// </summary>
    [Fact]
    public async Task A_duplicate_profile_does_not_cost_a_real_one_its_place_under_the_cap()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        // One profile spelt twice, then enough distinct ones to fill the cap exactly.
        var profiles = new List<InventoryChromeProfile>
        {
            Profile(profileKey: "Default", profileName: "First"),
            Profile(profileKey: "default", profileName: "Second"),
        };
        profiles.AddRange(Enumerable.Range(0, Infrastructure.Devices.DeviceInventoryService.MaxChromeProfiles - 1)
            .Select(NumberedProfile));

        var response = await UploadAsync(client, credential, Section(installation: Installation(), profiles: profiles));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, stored, _) = await StoredAsync(deviceId);

        stored.Count.ShouldBe(Infrastructure.Devices.DeviceInventoryService.MaxChromeProfiles,
            "the duplicate is dropped first-wins and every distinct profile within the cap is kept");
        stored.Single(p => p.ProfileKey == "Default").ProfileName.ShouldBe("First");
    }

    [Fact]
    public async Task A_duplicate_extension_does_not_cost_a_real_one_its_place_under_the_cap()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var extensions = new List<InventoryChromeExtension> { Extension(TidyId, "First"), Extension(TidyId, "Second") };
        extensions.AddRange(Enumerable.Range(0, Infrastructure.Devices.DeviceInventoryService.MaxChromeExtensionsPerProfile - 1)
            .Select(i => Extension(ExtensionIdFor(i), $"Extension {i}")));

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: extensions)]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, _, stored) = await StoredAsync(deviceId);

        stored.Count.ShouldBe(Infrastructure.Devices.DeviceInventoryService.MaxChromeExtensionsPerProfile);
        stored.Single(e => e.ExtensionId == TidyId).Name.ShouldBe("First");
    }

    /// <summary>
    /// Component extensions are Chrome's own built-ins. They are stored, because
    /// the agent reported them, and marked so the console can count "extensions"
    /// without them.
    /// </summary>
    [Theory]
    [InlineData("Component", ChromeExtensionInstallType.Component)]
    [InlineData("ExternalComponent", ChromeExtensionInstallType.ExternalComponent)]
    public async Task A_component_extension_is_stored_and_marked_as_a_component(
        string installType, ChromeExtensionInstallType expected)
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: [Extension(BuiltInId, "Built In", installType)])]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, _, extensions) = await StoredAsync(deviceId);

        var row = extensions.Single();
        row.InstallType.ShouldBe(expected);
        row.IsComponent.ShouldBeTrue();
    }

    // ---- refusals ----------------------------------------------------------

    /// <summary>
    /// The id keys the fleet-wide "which devices have extension X" index. One that
    /// is not shaped like Chrome's is refused outright rather than stored where
    /// nothing could ever find it. Nothing of the report is kept.
    /// </summary>
    [Theory]
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdef")]
    [InlineData("abcdefghijklmnopabcdefghijklmno")]
    [InlineData("")]
    public async Task An_extension_id_that_is_not_chromes_is_refused(string extensionId)
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: [Extension(), Extension(extensionId, "Impostor")])]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StoredAsync(deviceId)).Installation.ShouldBeNull();
    }

    /// <summary>
    /// The status decides whether the profile list replaces the stored one, so a
    /// value the contract does not name is refused rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData("Installed")]
    [InlineData("available")]
    [InlineData("")]
    public async Task An_unrecognised_status_is_refused(string status)
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(status, installation: Installation()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The contract requires a version of an installation entry, and the agent
    /// honours it: an installation whose version it could not read is reported as
    /// "Available" with no entry at all. An entry that is present but names no
    /// version is therefore malformed, as a software entry without a name is,
    /// and the upload is refused. Nothing of the report is kept.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_installation_entry_without_a_version_is_refused(string? version)
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(installation: Installation(version!)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StoredAsync(deviceId)).Installation.ShouldBeNull();
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("Sideloaded")]
    [InlineData("")]
    public async Task An_unrecognised_install_type_is_refused(string installType)
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: [Extension(installType: installType)])]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_over_long_extension_name_is_refused()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: [Extension(name: new string('n', InventoryChromeExtension.MaxName + 1))])]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Over the cap is truncated; over the refusal ceiling is not a machine. The
    /// line is <see cref="Infrastructure.Devices.DeviceInventoryService.OversizeRefusalMultiplier"/>
    /// times the cap, as for every other section.
    /// </summary>
    [Fact]
    public async Task An_implausible_number_of_profiles_is_refused()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var ceiling = Infrastructure.Devices.DeviceInventoryService.MaxChromeProfiles
            * Infrastructure.Devices.DeviceInventoryService.OversizeRefusalMultiplier;
        var profiles = Enumerable.Range(0, ceiling + 1).Select(NumberedProfile).ToList();

        var response = await UploadAsync(client, credential, Section(installation: Installation(), profiles: profiles));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21-abc-def")]
    [InlineData("")]
    public async Task A_malformed_user_sid_is_refused(string userSid)
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(userSid: userSid)]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_manifest_version_of_zero_is_refused()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential, Section(
            installation: Installation(),
            profiles: [Profile(extensions: [Extension(manifestVersion: 0)])]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
