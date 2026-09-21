using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// BitLocker inventory and readiness over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Read-only endpoints, so the weight is on who may read them, about which devices,
/// and on the two ways the answer could mislead: a machine that would not answer must
/// not read as unencrypted, and a suspended volume must not read as protected.
/// </para>
/// <para>
/// The leakage tests assert over the whole serialised response rather than named
/// fields, so a future field that carried key material would fail them even though
/// nothing here knows its name.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed partial class BitLockerEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const int FullyDecrypted = 0;
    private const int FullyEncrypted = 1;
    private const int ProtectionOff = 0;
    private const int ProtectionOn = 1;
    private const int OsVolume = 0;
    private const int FixedDataVolume = 1;

    private static Uri Volumes(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/bitlocker-volumes", UriKind.Relative);

    private static Uri Readiness(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/bitlocker-readiness", UriKind.Relative);

    private static readonly Dictionary<string, string> SessionTokens = [];
    private static readonly SemaphoreSlim SignInGate = new(1, 1);

    private async Task<HttpClient> ClientAsync(string email)
    {
        await SignInGate.WaitAsync();
        try
        {
            if (!SessionTokens.TryGetValue(email, out var token))
            {
                token = await _fixture.SignInAsync(email);
                SessionTokens[email] = token;
            }

            return _fixture.CreateClientFor(token);
        }
        finally
        {
            SignInGate.Release();
        }
    }

    private sealed record Vol(
        string Letter, int? Conversion, int? Protection, int Type = OsVolume, string? ProtectorId = null);

    private async Task<Guid> SeedDeviceAsync(
        BitLockerAvailability? availability = BitLockerAvailability.Available,
        bool? tpmPresent = true,
        bool? tpmEnabled = true,
        params Vol[] volumes)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"bl-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "BL-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var now = DateTimeOffset.UtcNow;

        if (availability is { } value)
        {
            var status = new DeviceBitLockerStatus(device.Id);
            status.Apply(value, now);
            db.DeviceBitLockerStatus.Add(status);
        }

        foreach (var v in volumes)
        {
            db.DeviceBitLockerVolumes.Add(new DeviceBitLockerVolume(
                device.Id, $"\\\\?\\Volume{{{Guid.NewGuid()}}}\\", v.Letter, "pv-1", v.Type,
                v.Conversion, v.Protection, v.Conversion == FullyEncrypted ? 100 : 0, 7,
                v.ProtectorId is not null, v.ProtectorId, now));
        }

        var posture = new DeviceSecurityPosture(device.Id);
        posture.Apply(null, null, null, null, null, null, null, tpmPresent, tpmEnabled, "2.0",
            volumes.FirstOrDefault(v => v.Type == OsVolume)?.Protection switch
            {
                ProtectionOn => "On",
                ProtectionOff => "Off",
                _ => null,
            },
            null, now);
        db.DeviceSecurityPosture.Add(posture);

        await db.SaveChangesAsync();
        return device.Id;
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_sees_nothing()
    {
        var deviceId = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOn));

        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(Volumes(deviceId)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.GetAsync(Readiness(deviceId)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Reading encryption state grants nothing: encrypting, suspending and above all
    /// decrypting are separate permissions that do not exist yet and will not be held
    /// by these roles when they do.
    /// </summary>
    [Theory]
    [InlineData("helpdesk")]
    [InlineData("auditor")]
    public async Task Read_only_roles_can_see_encryption_state(string which)
    {
        var deviceId = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOn));

        var email = which == "helpdesk"
            ? AdminApiPostgresFixture.HelpdeskEmail
            : AdminApiPostgresFixture.AuditorEmail;

        using var client = await ClientAsync(email);

        (await client.GetAsync(Volumes(deviceId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Readiness(deviceId))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_device_outside_the_callers_scope_is_invisible()
    {
        var inScope = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOn));
        var outOfScope = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOn));

        var email = $"bl-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"BlScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            (await db.Devices.SingleAsync(candidate => candidate.Id == inScope)).MoveToGroup(group.Id);

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password),
                DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));

        (await client.GetAsync(Volumes(inScope))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Readiness(inScope))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync(Volumes(outOfScope))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(Readiness(outOfScope))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- what the endpoints report -----------------------------------------

    [Fact]
    public async Task The_volume_list_reports_raw_windows_values_beside_the_state()
    {
        var protectorId = Guid.NewGuid().ToString();
        var deviceId = await SeedDeviceAsync(
            volumes: new Vol("C:", FullyEncrypted, ProtectionOn, OsVolume, protectorId));

        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var row = (await client.GetFromJsonAsync<JsonElement>(Volumes(deviceId)))
            .EnumerateArray().Single();

        row.GetProperty("driveLetter").GetString().ShouldBe("C:");
        row.GetProperty("conversionStatus").GetInt32().ShouldBe(FullyEncrypted);
        row.GetProperty("protectionStatus").GetInt32().ShouldBe(ProtectionOn);
        row.GetProperty("state").GetString().ShouldBe("Protected");
        row.GetProperty("encryptionPercentage").GetInt32().ShouldBe(100);
        row.GetProperty("hasRecoveryPasswordProtector").GetBoolean().ShouldBeTrue();
        row.GetProperty("recoveryProtectorIds").EnumerateArray().Single().GetString().ShouldBe(protectorId);
    }

    [Fact]
    public async Task An_encrypted_and_protected_endpoint_is_reported_protected()
    {
        var deviceId = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOn));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var body = await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId));

        body.GetProperty("readiness").GetString().ShouldBe("Protected");
        body.GetProperty("availability").GetString().ShouldBe("Available");
        body.GetProperty("protectedVolumeCount").GetInt32().ShouldBe(1);
        body.GetProperty("lastReportedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Encrypted with protection off is a deliberately weakened machine, and the
    /// console must not round it up to "encrypted".
    /// </summary>
    [Fact]
    public async Task A_suspended_volume_is_reported_as_suspended_not_protected()
    {
        var deviceId = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOff));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var body = await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId));

        body.GetProperty("readiness").GetString().ShouldBe("Suspended");
        body.GetProperty("protectedVolumeCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task An_unencrypted_endpoint_with_a_working_tpm_is_ready_to_encrypt()
    {
        var deviceId = await SeedDeviceAsync(
            tpmPresent: true, tpmEnabled: true, volumes: new Vol("C:", FullyDecrypted, ProtectionOff));

        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId)))
            .GetProperty("readiness").GetString().ShouldBe("ReadyToEncrypt");
    }

    [Fact]
    public async Task An_unencrypted_endpoint_without_a_usable_tpm_is_reported_not_ready()
    {
        var deviceId = await SeedDeviceAsync(
            tpmPresent: false, tpmEnabled: null, volumes: new Vol("C:", FullyDecrypted, ProtectionOff));

        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId)))
            .GetProperty("readiness").GetString().ShouldBe("TpmNotReady");
    }

    /// <summary>
    /// The failure this whole design guards against, asserted end to end.
    /// </summary>
    [Fact]
    public async Task An_endpoint_that_refused_the_query_is_unknown_not_unencrypted()
    {
        var deviceId = await SeedDeviceAsync(
            BitLockerAvailability.AccessDenied,
            volumes: new Vol("C:", FullyEncrypted, ProtectionOn));

        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var body = await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId));

        body.GetProperty("readiness").GetString().ShouldBe("Unknown");
        body.GetProperty("readiness").GetString().ShouldNotBe("NotEncrypted");
        body.GetProperty("availability").GetString().ShouldBe("AccessDenied");
    }

    [Fact]
    public async Task An_endpoint_that_has_reported_nothing_is_unknown()
    {
        var deviceId = await SeedDeviceAsync(availability: null);
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var body = await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId));

        body.GetProperty("readiness").GetString().ShouldBe("Unknown");
        body.GetProperty("availability").GetString().ShouldBe("Unknown");
        body.GetProperty("lastReportedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_edition_without_bitlocker_is_reported_as_unsupported()
    {
        var deviceId = await SeedDeviceAsync(BitLockerAvailability.NotAvailable);
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId)))
            .GetProperty("readiness").GetString().ShouldBe("NotSupported");
    }

    /// <summary>
    /// The long-standing single-field summary is surfaced unchanged, so a caller can
    /// see the same value the compliance score was computed from.
    /// </summary>
    [Fact]
    public async Task The_legacy_system_drive_status_is_reported_alongside_the_new_verdict()
    {
        var deviceId = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOn));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var body = await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId));

        body.GetProperty("systemDriveStatus").GetString().ShouldBe("On");
        body.GetProperty("tpmPresent").GetBoolean().ShouldBeTrue();
        body.GetProperty("tpmSpecVersion").GetString().ShouldBe("2.0");
    }

    [Fact]
    public async Task Readiness_states_its_own_limitation()
    {
        var deviceId = await SeedDeviceAsync(volumes: new Vol("C:", FullyEncrypted, ProtectionOn));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.GetFromJsonAsync<JsonElement>(Readiness(deviceId)))
            .GetProperty("limitation").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unknown_device_is_not_found()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.GetAsync(Readiness(Guid.CreateVersion7()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- secrets -----------------------------------------------------------

    /// <summary>
    /// Walks every property and every string value in both responses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Structural rather than a substring scan of the body, because the body
    /// legitimately contains <c>hasRecoveryPasswordProtector</c> -- a boolean saying a
    /// protector exists, which is exactly what this milestone is meant to report. A
    /// blunt search for "recoveryPassword" flags that and teaches the next person to
    /// weaken the test.
    /// </para>
    /// <para>
    /// So two precise things are asserted instead: no property is <em>named</em> as
    /// though it holds key material, and no string value anywhere has the shape of a
    /// recovery password. A field added later that carried a key would fail one or
    /// both without anyone having to remember this test exists.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task No_response_carries_a_recovery_key_in_any_property_or_value()
    {
        var deviceId = await SeedDeviceAsync(
            volumes:
            [
                new Vol("C:", FullyEncrypted, ProtectionOn, OsVolume, Guid.NewGuid().ToString()),
                new Vol("D:", FullyDecrypted, ProtectionOff, FixedDataVolume, Guid.NewGuid().ToString()),
            ]);

        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        // Names that would denote the key itself. "hasRecoveryPasswordProtector" is
        // deliberately not among them: it reports presence, never the value.
        string[] forbiddenNames =
        [
            "recoveryKey", "recoveryPassword", "numericalPassword", "key", "password", "secret",
        ];

        foreach (var uri in new[] { Volumes(deviceId), Readiness(deviceId) })
        {
            using var document = JsonDocument.Parse(
                await (await client.GetAsync(uri)).Content.ReadAsStringAsync());

            Walk(document.RootElement, uri.ToString(), forbiddenNames);
        }
    }

    /// <summary>A canonical GUID, in any of the casings these responses use.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial System.Text.RegularExpressions.Regex GuidShape();

    private static void Walk(JsonElement element, string where, string[] forbiddenNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    forbiddenNames.ShouldNotContain(
                        property.Name,
                        StringComparer.OrdinalIgnoreCase,
                        $"{where} exposes a property named '{property.Name}'");

                    Walk(property.Value, where, forbiddenNames);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, where, forbiddenNames);
                }

                break;

            case JsonValueKind.String:
                var value = element.GetString() ?? "";

                // Applied to the value exactly as returned. A canonical GUID cannot
                // match this -- its four-character groups break any six-digit run --
                // so nothing legitimate is excused from it.
                System.Text.RegularExpressions.Regex.IsMatch(value, @"\d{6}-\d{6}")
                    .ShouldBeFalse($"{where} returned a value shaped like a recovery password");

                // A recovery password is 48 digits in six-digit groups. Nothing this
                // API returns has any business carrying a long digit run.
                //
                // Identifiers are the one honest source of long digit runs here: a
                // device, volume or protector GUID is hex, and about 3% of GUIDs
                // contain nine consecutive digits purely by chance. This test walks
                // several of them per run, which made it fail at random roughly one
                // run in three. A GUID names a thing and unlocks nothing, and a real
                // recovery password is never one, so they are removed before the
                // heuristic rather than the heuristic being loosened.
                var scanned = GuidShape().Replace(value, "");

                System.Text.RegularExpressions.Regex.IsMatch(scanned, @"\d{9,}")
                    .ShouldBeFalse($"{where} returned a suspiciously long digit run");

                break;
        }
    }

    /// <summary>
    /// Protector identifiers are returned, and they are GUIDs. A GUID identifies a
    /// protector; it does not unlock anything, and the value that would is never read
    /// from Windows in the first place.
    /// </summary>
    [Fact]
    public async Task Protector_identifiers_are_guids_and_nothing_else()
    {
        var deviceId = await SeedDeviceAsync(
            volumes: new Vol("C:", FullyEncrypted, ProtectionOn, OsVolume, Guid.NewGuid().ToString()));

        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var ids = (await client.GetFromJsonAsync<JsonElement>(Volumes(deviceId)))
            .EnumerateArray().Single()
            .GetProperty("recoveryProtectorIds")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        ids.ShouldNotBeEmpty();
        ids.All(id => Guid.TryParse(id, out _)).ShouldBeTrue();
    }
}
