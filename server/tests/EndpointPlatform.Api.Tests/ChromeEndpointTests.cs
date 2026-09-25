using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;
using InstallType = EndpointPlatform.Domain.Chrome.ChromeExtensionInstallType;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The read-only Chrome routes over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The weight here is on the group semantics, because they are the part most
/// easily got wrong: "All Devices" is the built-in group row and lists only the
/// devices that are in no custom group, a custom group lists only its members,
/// and a device moved through the EXISTING membership endpoint changes lists. A
/// Chrome view that disagreed with the Groups page about where a device is would
/// mislead the administrators who use that page to decide who may act on it.
/// </para>
/// <para>
/// Chrome rows are seeded straight into the database rather than through the
/// agent upload: ingestion has its own tests, and these are about what the read
/// side makes of whatever is stored. Every identifier is synthetic.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ChromeEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    private const string Sid = "S-1-5-21-5-5-5-1001";
    private const string SecondSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string DefaultAccount = @"WORKGROUP\user";
    private const string ChromeVersion = "131.0.6778.86";

    private static readonly Uri Overview = new("/admin/v1/chrome/overview", UriKind.Relative);

    private static Uri GroupDevices(Guid groupId, string? q = null) => new(
        $"/admin/v1/chrome/groups/{groupId}/devices" + (q is null ? string.Empty : $"?q={Uri.EscapeDataString(q)}"),
        UriKind.Relative);

    private static Uri DeviceChrome(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/chrome", UriKind.Relative);

    private static Uri ProfileExtensions(Guid deviceId, Guid profileId) =>
        new($"/admin/v1/devices/{deviceId}/chrome/profiles/{profileId}/extensions", UriKind.Relative);

    private Task<HttpClient> ItAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);

    /// <summary>A Chrome extension id: exactly 32 characters in a-p, distinct per leading letter.</summary>
    private static string ExtensionId(char first) => first + "bcdefghijklmnopabcdefghijklmnop";

    // ---------------------------------------------------------------- seeding

    /// <summary>One profile to seed, with the extensions Chrome would record for it.</summary>
    private sealed class ProfileSeed(string sid, string key, string account = DefaultAccount, string? name = null)
    {
        public string Sid { get; } = sid;

        public string Key { get; } = key;

        public string Account { get; } = account;

        public string? Name { get; } = name;

        public List<(string Id, InstallType Type, bool Managed, string? Name)> Extensions { get; } = [];

        public ProfileSeed With(string id, InstallType type, bool managed = false, string? name = null)
        {
            Extensions.Add((id, type, managed, name));
            return this;
        }
    }

    /// <summary>
    /// Seeds the device's Chrome section directly and returns the profile ids by
    /// profile key. Available carries a full installation; anything else carries
    /// only the status, as an agent that found no Chrome would report.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Guid>> SeedChromeAsync(
        Guid deviceId, ChromeReportStatus status, params ProfileSeed[] profiles)
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var available = status == ChromeReportStatus.Available;

        var installation = new ChromeInstallation(deviceId);
        installation.Apply(
            status,
            available ? ChromeVersion : null,
            available ? @"C:\Program Files\Google\Chrome\Application\chrome.exe" : null,
            available ? "x64" : null,
            available ? "stable" : null,
            available ? "Machine" : null,
            installedForUser: null,
            available ? "1.3.195.29" : null,
            available ? now.AddHours(-2) : null,
            now);
        db.ChromeInstallations.Add(installation);

        var ids = new Dictionary<string, Guid>();
        foreach (var seed in profiles)
        {
            var profile = new ChromeProfile(
                deviceId, installation.Id, seed.Sid, seed.Account, seed.Key, seed.Name,
                @"C:\Users\user\AppData\Local\Google\Chrome\User Data\" + seed.Key,
                isManaged: false, now.AddMinutes(-30), now);
            db.ChromeProfiles.Add(profile);
            ids[seed.Key] = profile.Id;

            foreach (var (id, type, managed, name) in seed.Extensions)
            {
                db.ChromeExtensions.Add(new ChromeExtension(
                    deviceId, profile.Id, id, name, "2.4.1", manifestVersion: 3, enabled: true, type, managed,
                    fromWebStore: true, "https://clients2.google.com/service/update2/crx",
                    now.AddDays(-30), now.AddDays(-2), now));
            }
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>A device in a second organization, with that organization's own built-in group.</summary>
    private async Task<(Guid DeviceId, Guid AllDevicesId)> SeedForeignDeviceAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization("Other Org", ("c" + Guid.CreateVersion7().ToString("N"))[..20]);
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var token = new EnrollmentToken(
            org.Id, $"chrome-foreign-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "seed", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "CHR-FOREIGN", "m-" + Guid.CreateVersion7().ToString("N"),
            "1.9.0", "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return (device.Id, device.DeviceGroupId);
    }

    // ---------------------------------------------------------------- reading

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HashSet<Guid> DeviceIds(JsonElement listing) =>
        listing.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("deviceId").GetGuid()).ToHashSet();

    private static async Task<HashSet<Guid>> ListedDevicesAsync(HttpClient client, Guid groupId, string? q = null) =>
        DeviceIds(await ReadAsync(await client.GetAsync(GroupDevices(groupId, q))));

    // ------------------------------------------------------- group semantics

    [Fact]
    public async Task All_Devices_lists_only_devices_whose_group_is_the_built_in_group()
    {
        using var client = await ItAdminAsync();
        var inNoCustomGroup = await _support.SeedDeviceAsync();
        var inCustomGroup = await _support.SeedDeviceAsync();
        await _support.CreateGroupAsync(client, UniqueName("ChromeCustom"), inCustomGroup);
        var allDevices = await _support.AllDevicesIdAsync();

        var listing = await ReadAsync(await client.GetAsync(GroupDevices(allDevices)));

        listing.GetProperty("groupId").GetGuid().ShouldBe(allDevices);
        listing.GetProperty("groupName").GetString().ShouldBe(DeviceGroup.AllDevicesName);
        listing.GetProperty("isBuiltIn").GetBoolean().ShouldBeTrue();

        var ids = DeviceIds(listing);
        ids.ShouldContain(inNoCustomGroup);
        ids.ShouldNotContain(inCustomGroup,
            "a device is in exactly one group, so one in a custom group is not in the built-in one");

        // Every listed device really does point at the built-in group: the list is
        // the group's members, not the fleet under another name.
        await using var db = _fixture.CreateDbContext();
        (await db.Devices.Where(d => ids.Contains(d.Id)).AllAsync(d => d.DeviceGroupId == allDevices)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_custom_group_lists_only_its_members()
    {
        using var client = await ItAdminAsync();
        var member = await _support.SeedDeviceAsync();
        await _support.SeedDeviceAsync(); // stays in All Devices
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeMembers"), member);

        var listing = await ReadAsync(await client.GetAsync(GroupDevices(group)));

        listing.GetProperty("groupId").GetGuid().ShouldBe(group);
        listing.GetProperty("isBuiltIn").GetBoolean().ShouldBeFalse();
        var ids = DeviceIds(listing);
        ids.Count.ShouldBe(1);
        ids.ShouldContain(member);
    }

    [Fact]
    public async Task Moving_a_device_into_a_custom_group_through_the_membership_endpoint_moves_it_between_the_Chrome_lists()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeMove"));
        var allDevices = await _support.AllDevicesIdAsync();

        (await ListedDevicesAsync(client, allDevices)).ShouldContain(device);
        (await ListedDevicesAsync(client, group)).ShouldNotContain(device);

        (await client.PostAsync(Devices(group), Json(new { deviceIds = new[] { device } })))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ListedDevicesAsync(client, allDevices)).ShouldNotContain(device,
            "joining a custom group leaves the built-in one; the Chrome view follows the Groups page");
        (await ListedDevicesAsync(client, group)).ShouldContain(device);
    }

    [Fact]
    public async Task Removing_a_device_from_a_custom_group_returns_it_to_the_All_Devices_Chrome_list()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeLeave"), device);
        var allDevices = await _support.AllDevicesIdAsync();

        (await ListedDevicesAsync(client, group)).ShouldContain(device);
        (await ListedDevicesAsync(client, allDevices)).ShouldNotContain(device);

        (await client.PostAsync(RemoveDevices(group), Json(new { deviceIds = new[] { device } })))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ListedDevicesAsync(client, group)).ShouldNotContain(device);
        (await ListedDevicesAsync(client, allDevices)).ShouldContain(device,
            "a device removed from its group returns to All Devices, and the Chrome view says so");
    }

    [Fact]
    public async Task A_device_is_never_listed_under_both_All_Devices_and_a_custom_group()
    {
        using var client = await ItAdminAsync();
        var loose = await _support.SeedDeviceAsync();
        var grouped = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeOnce"), grouped);
        var allDevices = await _support.AllDevicesIdAsync();

        var inAllDevices = await ListedDevicesAsync(client, allDevices);
        var inGroup = await ListedDevicesAsync(client, group);

        inAllDevices.ShouldContain(loose);
        inGroup.ShouldContain(grouped);
        inAllDevices.Intersect(inGroup).ShouldBeEmpty("a device belongs to exactly one group");
    }

    [Fact]
    public async Task Search_filters_the_group_listing_by_hostname()
    {
        using var client = await ItAdminAsync();
        var suffix = Guid.CreateVersion7().ToString("N")[..8];
        var alpha = await _support.SeedDeviceAsync(hostname: $"CHRQ-ALPHA-{suffix}");
        var beta = await _support.SeedDeviceAsync(hostname: $"CHRQ-BETA-{suffix}");
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeSearch"), alpha, beta);

        // Lower case on purpose: the search is case-insensitive, as the device list's is.
        var hits = await ListedDevicesAsync(client, group, q: "alpha");

        hits.Count.ShouldBe(1);
        hits.ShouldContain(alpha);
        (await ListedDevicesAsync(client, group, q: "no-such-host")).ShouldBeEmpty();
        (await ListedDevicesAsync(client, group)).Count.ShouldBe(2, "no search term lists the whole group");
    }

    // ---------------------------------------------------------------- counts

    [Fact]
    public async Task Per_device_profile_and_extension_counts_exclude_Chromes_own_components()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeCounts"), device);
        await SeedChromeAsync(device, ChromeReportStatus.Available,
            new ProfileSeed(Sid, "Default")
                .With(ExtensionId('a'), InstallType.Internal)
                .With(ExtensionId('b'), InstallType.ExternalPolicy, managed: true)
                .With(ExtensionId('c'), InstallType.Component),
            new ProfileSeed(Sid, "Profile 1")
                .With(ExtensionId('d'), InstallType.ExternalComponent));

        var row = ByDevice((await ReadAsync(await client.GetAsync(GroupDevices(group)))).GetProperty("devices"))[device];

        row.GetProperty("profileCount").GetInt32().ShouldBe(2);
        row.GetProperty("extensionCount").GetInt32().ShouldBe(2,
            "components are Chrome's own, not something anyone installed, and are not counted");
        row.GetProperty("chromeStatus").GetString().ShouldBe("Available");
        row.GetProperty("chromeVersion").GetString().ShouldBe(ChromeVersion);
        row.GetProperty("channel").GetString().ShouldBe("stable");
        row.GetProperty("updateStatus").GetString().ShouldBe("Unknown", "not computable until a version reference exists");
        row.GetProperty("collectedAt").ValueKind.ShouldBe(JsonValueKind.String);
        row.GetProperty("isOnline").GetBoolean().ShouldBeTrue();

        var detail = await ReadAsync(await client.GetAsync(DeviceChrome(device)));
        var profiles = detail.GetProperty("profiles").EnumerateArray()
            .ToDictionary(p => p.GetProperty("profileKey").GetString()!);

        profiles["Default"].GetProperty("extensionCount").GetInt32().ShouldBe(2);
        profiles["Default"].GetProperty("managedExtensionCount").GetInt32().ShouldBe(1);
        profiles["Profile 1"].GetProperty("extensionCount").GetInt32().ShouldBe(0);
        profiles["Profile 1"].GetProperty("managedExtensionCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_device_that_has_never_reported_Chrome_is_listed_with_no_status_and_zero_counts()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, UniqueName("ChromeSilent"), device);

        var row = ByDevice((await ReadAsync(await client.GetAsync(GroupDevices(group)))).GetProperty("devices"))[device];

        // Null, not "NotInstalled": no data is not the same as no Chrome.
        row.GetProperty("chromeStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("chromeVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("channel").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("collectedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("profileCount").GetInt32().ShouldBe(0);
        row.GetProperty("extensionCount").GetInt32().ShouldBe(0);
        row.GetProperty("updateStatus").GetString().ShouldBe("Unknown");

        var detail = await ReadAsync(await client.GetAsync(DeviceChrome(device)));
        detail.GetProperty("installation").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("profiles").GetArrayLength().ShouldBe(0);
    }

    // ---------------------------------------------------------------- detail

    [Fact]
    public async Task Device_detail_reports_the_installation_and_orders_profiles_by_account_then_key()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        await SeedChromeAsync(device, ChromeReportStatus.Available,
            new ProfileSeed(SecondSid, "Default", account: @"WORKGROUP\zed", name: "Person 1"),
            new ProfileSeed(Sid, "Profile 2", account: @"WORKGROUP\amy"),
            new ProfileSeed(Sid, "Profile 1", account: @"WORKGROUP\amy"));

        var detail = await ReadAsync(await client.GetAsync(DeviceChrome(device)));

        detail.GetProperty("deviceId").GetGuid().ShouldBe(device);
        detail.GetProperty("hostname").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("isOnline").GetBoolean().ShouldBeTrue();

        var installation = detail.GetProperty("installation");
        installation.GetProperty("status").GetString().ShouldBe("Available");
        installation.GetProperty("version").GetString().ShouldBe(ChromeVersion);
        installation.GetProperty("executablePath").GetString().ShouldBe(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        installation.GetProperty("architecture").GetString().ShouldBe("x64");
        installation.GetProperty("channel").GetString().ShouldBe("stable");
        installation.GetProperty("installationScope").GetString().ShouldBe("Machine");
        installation.GetProperty("installedForUser").ValueKind.ShouldBe(JsonValueKind.Null);
        installation.GetProperty("updaterVersion").GetString().ShouldBe("1.3.195.29");
        installation.GetProperty("lastUpdateCheck").ValueKind.ShouldBe(JsonValueKind.String);
        installation.GetProperty("updateStatus").GetString().ShouldBe("Unknown");
        installation.GetProperty("collectedAt").ValueKind.ShouldBe(JsonValueKind.String);

        var profiles = detail.GetProperty("profiles").EnumerateArray().ToList();
        profiles.Select(p => p.GetProperty("profileKey").GetString())
            .ShouldBe(["Profile 1", "Profile 2", "Default"], "by account, then by profile key");
        profiles.Select(p => p.GetProperty("userAccount").GetString())
            .ShouldBe([@"WORKGROUP\amy", @"WORKGROUP\amy", @"WORKGROUP\zed"]);

        var zed = profiles[2];
        zed.GetProperty("profileId").GetGuid().ShouldNotBe(Guid.Empty);
        zed.GetProperty("userSid").GetString().ShouldBe(SecondSid);
        zed.GetProperty("profileName").GetString().ShouldBe("Person 1");
        zed.GetProperty("profilePath").GetString().ShouldEndWith(@"\Default");
        zed.GetProperty("isManaged").GetBoolean().ShouldBeFalse();
        zed.GetProperty("lastActiveAt").ValueKind.ShouldBe(JsonValueKind.String);
        zed.GetProperty("collectedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Inventory_refresh_pending_follows_the_devices_request_and_collection_times()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();

        (await ReadAsync(await client.GetAsync(DeviceChrome(device))))
            .GetProperty("inventoryRefreshPending").GetBoolean()
            .ShouldBeTrue("a device that has never uploaded inventory is asked for one on its next heartbeat");

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.Devices.SingleAsync(d => d.Id == device);
            stored.RecordInventory(null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        (await ReadAsync(await client.GetAsync(DeviceChrome(device))))
            .GetProperty("inventoryRefreshPending").GetBoolean().ShouldBeFalse();

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.Devices.SingleAsync(d => d.Id == device);
            stored.RequestInventoryRefresh(DateTimeOffset.UtcNow.AddSeconds(1));
            await db.SaveChangesAsync();
        }

        (await ReadAsync(await client.GetAsync(DeviceChrome(device))))
            .GetProperty("inventoryRefreshPending").GetBoolean()
            .ShouldBeTrue("a request newer than the last collection is outstanding");
    }

    // ------------------------------------------------------------ extensions

    [Fact]
    public async Task Profile_extensions_list_components_last_with_what_Chrome_recorded()
    {
        using var client = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();
        var profiles = await SeedChromeAsync(device, ChromeReportStatus.Available,
            new ProfileSeed(Sid, "Default")
                .With(ExtensionId('c'), InstallType.Component, name: "Built-in Thing")
                .With(ExtensionId('b'), InstallType.ExternalPolicy, managed: true, name: "Policy Tool")
                .With(ExtensionId('a'), InstallType.Internal, name: "Ad Blocker"));

        var rows = (await ReadAsync(await client.GetAsync(ProfileExtensions(device, profiles["Default"]))))
            .EnumerateArray().ToList();

        rows.Count.ShouldBe(3, "the extension list is the facts, components included; only the counts leave them out");
        rows.Select(r => r.GetProperty("extensionId").GetString())
            .ShouldBe([ExtensionId('a'), ExtensionId('b'), ExtensionId('c')], "installed extensions by name, then Chrome's own");

        var policy = rows[1];
        policy.GetProperty("extensionRowId").GetGuid().ShouldNotBe(Guid.Empty);
        policy.GetProperty("name").GetString().ShouldBe("Policy Tool");
        policy.GetProperty("version").GetString().ShouldBe("2.4.1");
        policy.GetProperty("manifestVersion").GetInt32().ShouldBe(3);
        policy.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        policy.GetProperty("installType").GetString().ShouldBe("ExternalPolicy");
        policy.GetProperty("isManaged").GetBoolean().ShouldBeTrue();
        policy.GetProperty("isComponent").GetBoolean().ShouldBeFalse();
        policy.GetProperty("fromWebStore").GetBoolean().ShouldBeTrue();
        policy.GetProperty("updateUrl").GetString().ShouldBe("https://clients2.google.com/service/update2/crx");
        policy.GetProperty("installedAt").ValueKind.ShouldBe(JsonValueKind.String);
        policy.GetProperty("updatedAt").ValueKind.ShouldBe(JsonValueKind.String);

        var component = rows[2];
        component.GetProperty("installType").GetString().ShouldBe("Component");
        component.GetProperty("isComponent").GetBoolean().ShouldBeTrue();
        component.GetProperty("isManaged").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Profile_extensions_are_not_found_when_the_profile_belongs_to_a_different_device()
    {
        using var client = await ItAdminAsync();
        var first = await _support.SeedDeviceAsync();
        var second = await _support.SeedDeviceAsync();
        await SeedChromeAsync(first, ChromeReportStatus.Available,
            new ProfileSeed(Sid, "Default").With(ExtensionId('a'), InstallType.Internal));
        var secondProfiles = await SeedChromeAsync(second, ChromeReportStatus.Available,
            new ProfileSeed(Sid, "Default").With(ExtensionId('b'), InstallType.Internal));

        (await client.GetAsync(ProfileExtensions(first, secondProfiles["Default"]))).StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "a profile id under the wrong device is answered exactly as one that does not exist");
        (await client.GetAsync(ProfileExtensions(first, Guid.CreateVersion7()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(ProfileExtensions(second, secondProfiles["Default"]))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ----------------------------------------------------------------- scope

    [Fact]
    public async Task An_out_of_scope_group_is_not_found_for_a_scoped_administrator()
    {
        using var admin = await ItAdminAsync();
        var mine = await _support.CreateGroupAsync(admin, UniqueName("ChromeMine"), await _support.SeedDeviceAsync());
        var theirs = await _support.CreateGroupAsync(admin, UniqueName("ChromeTheirs"), await _support.SeedDeviceAsync());
        var allDevices = await _support.AllDevicesIdAsync();

        using var scoped = await _support.ScopedAdminAsync(mine);

        (await scoped.GetAsync(GroupDevices(mine))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scoped.GetAsync(GroupDevices(theirs))).StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "404, never 403: an administrator outside a group's scope must not learn that it exists");
        (await scoped.GetAsync(GroupDevices(allDevices))).StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "All Devices is a group like any other for scope; nothing granted it");
    }

    [Fact]
    public async Task An_out_of_scope_device_is_not_found_on_the_device_routes()
    {
        using var admin = await ItAdminAsync();
        var inScope = await _support.SeedDeviceAsync();
        var elsewhere = await _support.SeedDeviceAsync(); // stays in All Devices, which the scoped admin cannot see
        var mine = await _support.CreateGroupAsync(admin, UniqueName("ChromeScope"), inScope);
        var elsewhereProfiles = await SeedChromeAsync(elsewhere, ChromeReportStatus.Available,
            new ProfileSeed(Sid, "Default").With(ExtensionId('a'), InstallType.Internal));

        using var scoped = await _support.ScopedAdminAsync(mine);

        (await scoped.GetAsync(DeviceChrome(inScope))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scoped.GetAsync(DeviceChrome(elsewhere))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.GetAsync(ProfileExtensions(elsewhere, elsewhereProfiles["Default"])))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "a real profile id must not confirm the device exists");
    }

    /// <summary>Organization isolation holds on every Chrome route, group and device alike.</summary>
    [Fact]
    public async Task A_device_of_another_organization_is_not_found()
    {
        using var client = await ItAdminAsync();
        var (foreignDevice, foreignAllDevices) = await SeedForeignDeviceAsync();

        (await client.GetAsync(DeviceChrome(foreignDevice))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(ProfileExtensions(foreignDevice, Guid.CreateVersion7())))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(GroupDevices(foreignAllDevices))).StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "another tenant's built-in group is not this administrator's All Devices");
    }

    // -------------------------------------------------------------- overview

    [Fact]
    public async Task The_overview_counts_match_the_seeded_data_and_leaves_update_availability_null()
    {
        using var admin = await ItAdminAsync();

        var reporting = await _support.SeedDeviceAsync();
        await SeedChromeAsync(reporting, ChromeReportStatus.Available,
            new ProfileSeed(Sid, "Default")
                .With(ExtensionId('a'), InstallType.Internal)
                .With(ExtensionId('b'), InstallType.ExternalPolicy, managed: true)
                .With(ExtensionId('c'), InstallType.Component),
            new ProfileSeed(SecondSid, "Default")
                .With(ExtensionId('d'), InstallType.Internal));

        // Reported, but no Chrome: the uninstaller left a profile directory behind.
        var uninstalled = await _support.SeedDeviceAsync(online: false);
        await SeedChromeAsync(uninstalled, ChromeReportStatus.NotInstalled, new ProfileSeed(Sid, "Default"));

        // Never reported the section at all.
        var silent = await _support.SeedDeviceAsync();

        // The fixture database is shared by every API test class, so exact
        // numbers are only knowable for an administrator scoped to a group of
        // known devices. The unrestricted view is checked to contain at least it.
        var group = await _support.CreateGroupAsync(admin, UniqueName("ChromeOverview"), reporting, uninstalled, silent);
        using var scoped = await _support.ScopedAdminAsync(group);

        var overview = await ReadAsync(await scoped.GetAsync(Overview));

        overview.GetProperty("totalGroups").GetInt32().ShouldBe(1);
        overview.GetProperty("totalDevices").GetInt32().ShouldBe(3);
        overview.GetProperty("onlineDevices").GetInt32().ShouldBe(2);
        overview.GetProperty("devicesReportingChrome").GetInt32().ShouldBe(2, "NotInstalled is still a report");
        overview.GetProperty("devicesWithChrome").GetInt32().ShouldBe(1);
        overview.GetProperty("totalProfiles").GetInt32().ShouldBe(3);
        overview.GetProperty("totalExtensions").GetInt32().ShouldBe(3, "components are not counted");
        overview.GetProperty("devicesWithUpdatesAvailable").ValueKind.ShouldBe(JsonValueKind.Null,
            "not computable until a version reference exists: null, never 0");

        var fleet = await ReadAsync(await admin.GetAsync(Overview));
        fleet.GetProperty("totalGroups").GetInt32().ShouldBeGreaterThanOrEqualTo(2, "All Devices and this group at least");
        fleet.GetProperty("totalDevices").GetInt32().ShouldBeGreaterThanOrEqualTo(3);
        fleet.GetProperty("onlineDevices").GetInt32().ShouldBeGreaterThanOrEqualTo(2);
        fleet.GetProperty("devicesReportingChrome").GetInt32().ShouldBeGreaterThanOrEqualTo(2);
        fleet.GetProperty("devicesWithChrome").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        fleet.GetProperty("totalProfiles").GetInt32().ShouldBeGreaterThanOrEqualTo(3);
        fleet.GetProperty("totalExtensions").GetInt32().ShouldBeGreaterThanOrEqualTo(3);
        fleet.GetProperty("devicesWithUpdatesAvailable").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// The agent reports Available with no installation body when it found Chrome
    /// but could not read its version. The read side applies the entity's own
    /// rule (<see cref="ChromeInstallation.IsInstalled"/>): such a device has
    /// reported, but it is not a device with Chrome, and the headline count must
    /// not claim an installation that no device detail can substantiate.
    /// </summary>
    [Fact]
    public async Task An_available_report_without_a_version_is_reporting_but_is_not_counted_as_Chrome()
    {
        using var admin = await ItAdminAsync();
        var device = await _support.SeedDeviceAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var installation = new ChromeInstallation(device);
            installation.Apply(
                ChromeReportStatus.Available, version: null, executablePath: null, architecture: null, channel: null,
                installationScope: null, installedForUser: null, updaterVersion: null, lastUpdateCheck: null,
                collectedAt: DateTimeOffset.UtcNow);
            db.ChromeInstallations.Add(installation);
            await db.SaveChangesAsync();
        }

        var group = await _support.CreateGroupAsync(admin, UniqueName("ChromeNoVersion"), device);
        using var scoped = await _support.ScopedAdminAsync(group);

        var overview = await ReadAsync(await scoped.GetAsync(Overview));
        overview.GetProperty("devicesReportingChrome").GetInt32().ShouldBe(1, "a report is a report, whatever it said");
        overview.GetProperty("devicesWithChrome").GetInt32().ShouldBe(0, "Available without a version is not installed");

        var row = (await ReadAsync(await scoped.GetAsync(GroupDevices(group)))).GetProperty("devices").EnumerateArray().Single();
        row.GetProperty("chromeStatus").GetString().ShouldBe("Available");
        row.GetProperty("chromeVersion").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ------------------------------------------------------------ permissions

    [Theory]
    [InlineData(AdminApiPostgresFixture.AuditorEmail)]
    [InlineData(AdminApiPostgresFixture.HelpdeskEmail)]
    public async Task Auditor_and_Helpdesk_can_read_every_Chrome_route(string email)
    {
        var device = await _support.SeedDeviceAsync();
        var profiles = await SeedChromeAsync(device, ChromeReportStatus.Available,
            new ProfileSeed(Sid, "Default").With(ExtensionId('a'), InstallType.Internal));
        var allDevices = await _support.AllDevicesIdAsync();

        using var client = await _support.ClientAsAsync(email);

        (await client.GetAsync(Overview)).StatusCode.ShouldBe(HttpStatusCode.OK, "chrome.view is a read");
        (await client.GetAsync(GroupDevices(allDevices))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(DeviceChrome(device))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(ProfileExtensions(device, profiles["Default"]))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/admin/v1/chrome/overview")]
    [InlineData("/admin/v1/chrome/groups/00000000-0000-0000-0000-000000000000/devices")]
    [InlineData("/admin/v1/devices/00000000-0000-0000-0000-000000000000/chrome")]
    [InlineData("/admin/v1/devices/00000000-0000-0000-0000-000000000000/chrome/profiles/00000000-0000-0000-0000-000000000000/extensions")]
    public async Task An_unauthenticated_caller_is_rejected_on_every_Chrome_route(string path)
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(new Uri(path, UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
