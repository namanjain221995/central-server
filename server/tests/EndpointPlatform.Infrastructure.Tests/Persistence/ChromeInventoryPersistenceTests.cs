using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// How the Chrome inventory is stored, and the constraints that hold its shape.
/// </summary>
/// <remarks>
/// <para>
/// Ingestion dedupes profiles and extensions in memory before it writes, but a
/// dedupe reads a snapshot and can be beaten by two uploads from one device
/// landing together. The unique indexes are the only thing that genuinely
/// prevents a duplicate row, so these tests reach past any service and insert
/// directly: a test that went through ingestion would pass on its dedupe and
/// prove nothing about the database.
/// </para>
/// <para>
/// The cascade test matters for the same reason. Retiring or deleting a device
/// must take its whole Chrome section with it, and only the foreign keys can
/// promise that for every code path.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class ChromeInventoryPersistenceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private const string Sid = "S-1-5-21-5-5-5-1001";
    private const string ExtensionId = "abcdefghijklmnopabcdefghijklmnop";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static async Task<Organization> EnsureOrganizationAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db)
    {
        var existing = await db.Organizations.FirstOrDefaultAsync(o => o.Slug == "test-org");
        if (existing is not null)
        {
            return existing;
        }

        var org = new Organization("Test Organization", "test-org");
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task<Device> SeedDeviceAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db, Guid organizationId)
    {
        var token = new EnrollmentToken(
            organizationId, $"chr-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", Now.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, "CHR-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, Now);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static ChromeInstallation Installation(Guid deviceId)
    {
        var installation = new ChromeInstallation(deviceId);
        installation.Apply(
            ChromeReportStatus.Available, "131.0.6778.86",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe", "x64", "stable", "Machine",
            null, "1.3.195.29", Now.AddHours(-2), Now);
        return installation;
    }

    private static ChromeProfile Profile(Guid deviceId, Guid installationId, string sid = Sid, string key = "Default") =>
        new(deviceId, installationId, sid, @"WORKGROUP\user", key, "Person 1",
            @"C:\Users\user\AppData\Local\Google\Chrome\User Data\" + key, false, Now.AddMinutes(-30), Now);

    private static ChromeExtension Extension(
        Guid deviceId, Guid profileId, string extensionId = ExtensionId,
        ChromeExtensionInstallType installType = ChromeExtensionInstallType.ExternalPolicy) =>
        new(deviceId, profileId, extensionId, "Example", "2.4.1", 3, true, installType,
            isManaged: installType == ChromeExtensionInstallType.ExternalPolicy, fromWebStore: true,
            "https://clients2.google.com/service/update2/crx", Now.AddDays(-30), Now.AddDays(-2), Now);

    private static async Task<PostgresException> ShouldRefuseAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db)
    {
        var exception = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        return exception.InnerException.ShouldBeOfType<PostgresException>();
    }

    // ---- round trip --------------------------------------------------------

    [Fact]
    public async Task The_chrome_section_round_trips_with_its_enums_as_text()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        var installation = Installation(device.Id);
        var profile = Profile(device.Id, installation.Id);
        var extension = Extension(device.Id, profile.Id);
        db.ChromeInstallations.Add(installation);
        db.ChromeProfiles.Add(profile);
        db.ChromeExtensions.Add(extension);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();

        var storedInstallation = await db.ChromeInstallations.AsNoTracking().SingleAsync(i => i.DeviceId == device.Id);
        storedInstallation.Status.ShouldBe(ChromeReportStatus.Available);
        storedInstallation.Version.ShouldBe("131.0.6778.86");
        storedInstallation.IsInstalled.ShouldBeTrue();
        storedInstallation.LastUpdateCheck.ShouldNotBeNull();

        var storedProfile = await db.ChromeProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id);
        storedProfile.ChromeInstallationId.ShouldBe(installation.Id);
        storedProfile.UserSid.ShouldBe(Sid);
        storedProfile.ProfileKey.ShouldBe("Default");
        storedProfile.IsManaged.ShouldBe(false);

        var storedExtension = await db.ChromeExtensions.AsNoTracking().SingleAsync(e => e.Id == extension.Id);
        storedExtension.ChromeProfileId.ShouldBe(profile.Id);
        storedExtension.ExtensionId.ShouldBe(ExtensionId);
        storedExtension.InstallType.ShouldBe(ChromeExtensionInstallType.ExternalPolicy);
        storedExtension.IsManaged.ShouldBeTrue();
        storedExtension.ManifestVersion.ShouldBe(3);
        storedExtension.ExtensionUpdatedAt.ShouldNotBeNull();
        storedExtension.IsComponent.ShouldBeFalse();

        // Stored as text, not as an ordinal: reordering the enums can then never
        // silently reinterpret stored history, and the columns are legible in psql.
        (await db.Database
            .SqlQuery<string>(
                $"""select status as "Value" from endpoint_platform.chrome_installations where id = {installation.Id}""")
            .SingleAsync()).ShouldBe("Available");
        (await db.Database
            .SqlQuery<string>(
                $"""select install_type as "Value" from endpoint_platform.chrome_extensions where id = {extension.Id}""")
            .SingleAsync()).ShouldBe("ExternalPolicy");
    }

    // ---- the identities ----------------------------------------------------

    [Fact]
    public async Task A_device_can_have_only_one_installation_row()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        db.ChromeInstallations.Add(Installation(device.Id));
        await db.SaveChangesAsync();

        db.ChromeInstallations.Add(Installation(device.Id));

        var pg = await ShouldRefuseAsync(db);
        pg.SqlState.ShouldBe("23505");
        pg.ConstraintName.ShouldBe("ux_chrome_installations_device_id");
    }

    [Fact]
    public async Task A_profile_is_unique_per_device_user_and_directory()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);
        var installation = Installation(device.Id);
        db.ChromeInstallations.Add(installation);
        db.ChromeProfiles.Add(Profile(device.Id, installation.Id));
        // Same directory name for a different Windows user is a different profile.
        db.ChromeProfiles.Add(Profile(device.Id, installation.Id, sid: "S-1-5-21-5-5-5-1002"));
        await db.SaveChangesAsync();

        db.ChromeProfiles.Add(Profile(device.Id, installation.Id));

        var pg = await ShouldRefuseAsync(db);
        pg.SqlState.ShouldBe("23505");
        pg.ConstraintName.ShouldBe("ux_chrome_profiles_device_user_profile");
    }

    [Fact]
    public async Task An_extension_is_unique_per_profile()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);
        var installation = Installation(device.Id);
        var first = Profile(device.Id, installation.Id);
        var second = Profile(device.Id, installation.Id, key: "Profile 1");
        db.ChromeInstallations.Add(installation);
        db.ChromeProfiles.AddRange(first, second);
        db.ChromeExtensions.Add(Extension(device.Id, first.Id));
        // The same extension in another profile is another installation of it.
        db.ChromeExtensions.Add(Extension(device.Id, second.Id));
        await db.SaveChangesAsync();

        db.ChromeExtensions.Add(Extension(device.Id, first.Id));

        var pg = await ShouldRefuseAsync(db);
        pg.SqlState.ShouldBe("23505");
        pg.ConstraintName.ShouldBe("ux_chrome_extensions_profile_extension");
    }

    // ---- the cascades ------------------------------------------------------

    [Fact]
    public async Task Deleting_a_device_removes_its_whole_chrome_section()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);
        var installation = Installation(device.Id);
        var profile = Profile(device.Id, installation.Id);
        db.ChromeInstallations.Add(installation);
        db.ChromeProfiles.Add(profile);
        db.ChromeExtensions.Add(Extension(device.Id, profile.Id));
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"delete from endpoint_platform.devices where id = {device.Id}");

        (await db.ChromeInstallations.AsNoTracking().CountAsync(i => i.DeviceId == device.Id)).ShouldBe(0);
        (await db.ChromeProfiles.AsNoTracking().CountAsync(p => p.DeviceId == device.Id)).ShouldBe(0);
        (await db.ChromeExtensions.AsNoTracking().CountAsync(e => e.DeviceId == device.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_a_profile_removes_its_extensions_and_nothing_else()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);
        var installation = Installation(device.Id);
        var doomed = Profile(device.Id, installation.Id);
        var kept = Profile(device.Id, installation.Id, key: "Profile 1");
        db.ChromeInstallations.Add(installation);
        db.ChromeProfiles.AddRange(doomed, kept);
        db.ChromeExtensions.AddRange(Extension(device.Id, doomed.Id), Extension(device.Id, kept.Id));
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"delete from endpoint_platform.chrome_profiles where id = {doomed.Id}");

        (await db.ChromeExtensions.AsNoTracking().CountAsync(e => e.ChromeProfileId == doomed.Id)).ShouldBe(0);
        (await db.ChromeExtensions.AsNoTracking().CountAsync(e => e.ChromeProfileId == kept.Id)).ShouldBe(1);
        (await db.ChromeInstallations.AsNoTracking().CountAsync(i => i.Id == installation.Id))
            .ShouldBe(1, "a profile going away says nothing about the installation");
    }
}
