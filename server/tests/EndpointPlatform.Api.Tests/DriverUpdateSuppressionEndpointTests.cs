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
/// Hiding and un-hiding a driver update over real HTTP against real PostgreSQL:
/// who may do it, about which devices, that it is idempotent and audited, and that
/// the per-device read and the overview reflect it in the wire shape the dashboard
/// codes against.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DriverUpdateSuppressionEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string Firmware = "dddddddd-0000-0000-0000-000000000001";
    private const string Display = "dddddddd-0000-0000-0000-000000000002";

    private static Uri Suppress(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/driver-updates/suppress", UriKind.Relative);

    private static Uri Unsuppress(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/driver-updates/unsuppress", UriKind.Relative);

    private static Uri List(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/driver-updates", UriKind.Relative);

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

    private async Task<Guid> SeedDeviceAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"dus-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "DUS-PC", "m-" + Guid.CreateVersion7().ToString("N"), "1.26.0", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var now = DateTimeOffset.UtcNow;
        db.DeviceDriverUpdates.AddRange(
            new DeviceDriverUpdate(device.Id, Firmware, 1, "Vendor - Firmware - 1.0", "Firmware", "Vendor", null, false, 10, now),
            new DeviceDriverUpdate(device.Id, Display, 1, "Vendor - Display - 2.0", "Display", "Vendor", null, false, 20, now));

        var record = new DeviceDriverUpdateInstallRecord(device.Id, Firmware, 1, "Vendor - Firmware - 1.0", now.AddHours(-1));
        record.RecordAttempt(DriverUpdateInstallStatus.ReOffered, 2, 0, Guid.CreateVersion7(), null, 1, now.AddHours(-1));
        db.DeviceDriverUpdateInstallRecords.Add(record);

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<List<DeviceDriverUpdateSuppression>> SuppressionsAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceDriverUpdateSuppressions.AsNoTracking().Where(s => s.DeviceId == deviceId).ToListAsync();
    }

    [Fact]
    public async Task Hiding_is_idempotent_audited_and_moves_the_update_out_of_the_actionable_list()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var first = await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Firmware, reason = "OEM firmware; use the vendor tool" });
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var again = await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Firmware.ToUpperInvariant() });
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent, "hiding an already-hidden update is not an error");

        var stored = (await SuppressionsAsync(deviceId)).ShouldHaveSingleItem("no duplicate rows");
        stored.Reason.ShouldBe("OEM firmware; use the vendor tool");
        stored.Title.ShouldBe("Vendor - Firmware - 1.0");
        stored.CreatedByDisplay.ShouldBe(AdminApiPostgresFixture.ItAdminEmail);

        await using (var db = _fixture.CreateDbContext())
        {
            var audits = await db.AuditLogEntries.AsNoTracking()
                .Where(a => a.DeviceId == deviceId && a.Action == "driverupdate.suppress")
                .ToListAsync();
            var audit = audits.ShouldHaveSingleItem("a no-op re-hide writes nothing");
            audit.RequiredPermission.ShouldBe("driver.manage");
            audit.NewState.ShouldNotBeNull();
            audit.NewState.ShouldContain(Firmware);
            audit.NewState.ShouldContain("Vendor - Firmware - 1.0");
        }

        var list = await client.GetFromJsonAsync<JsonElement>(List(deviceId));
        var updates = list.GetProperty("updates").EnumerateArray().ToList();
        updates.Select(u => u.GetProperty("updateId").GetString()).ShouldBe(new[] { Display });

        var hidden = list.GetProperty("hidden").EnumerateArray().Single();
        hidden.GetProperty("updateId").GetString().ShouldBe(Firmware);
        hidden.GetProperty("title").GetString().ShouldBe("Vendor - Firmware - 1.0");
        hidden.GetProperty("reason").GetString().ShouldBe("OEM firmware; use the vendor tool");
        hidden.GetProperty("hiddenBy").GetString().ShouldBe(AdminApiPostgresFixture.ItAdminEmail);
        hidden.GetProperty("hiddenAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Un_hiding_is_idempotent_and_audited()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Firmware })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent, "un-hiding a visible update is not an error");
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Display })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SuppressionsAsync(deviceId)).ShouldBeEmpty();

        await using (var db = _fixture.CreateDbContext())
        {
            var audit = (await db.AuditLogEntries.AsNoTracking()
                    .Where(a => a.DeviceId == deviceId && a.Action == "driverupdate.unsuppress")
                    .ToListAsync())
                .ShouldHaveSingleItem();
            audit.PreviousState!.ShouldContain(Firmware);
        }

        var list = await client.GetFromJsonAsync<JsonElement>(List(deviceId));
        list.GetProperty("updates").GetArrayLength().ShouldBe(2);
        list.GetProperty("hidden").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Hiding_needs_driver_manage()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await SuppressionsAsync(deviceId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_hide_anything()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Firmware }))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_outside_the_callers_scope_or_unknown_is_not_found()
    {
        var inScope = await SeedDeviceAsync();
        var outOfScope = await SeedDeviceAsync();

        var email = $"dus-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"DusScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
            db.DeviceGroups.Add(group);
            (await db.Devices.SingleAsync(d => d.Id == inScope)).MoveToGroup(group.Id);

            var user = new PlatformUser(org.Id, email, "Scoped Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();

            db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));

        (await client.PostAsJsonAsync(Suppress(inScope), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Suppress(outOfScope), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Unsuppress(outOfScope), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Suppress(Guid.CreateVersion7()), new { updateId = Firmware })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await SuppressionsAsync(outOfScope)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_or_oversized_request_is_refused()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = new string('x', 65) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Firmware, reason = new string('r', 201) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = (string?)null })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await SuppressionsAsync(deviceId)).ShouldBeEmpty();
    }

    /// <summary>
    /// The wire shape is the contract two other slices code against: every new
    /// field is present, camelCase, additive.
    /// </summary>
    [Fact]
    public async Task The_device_read_and_the_overview_carry_the_contract_fields()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var list = await client.GetFromJsonAsync<JsonElement>(List(deviceId));
        list.GetProperty("lastScanAt").ValueKind.ShouldBe(JsonValueKind.String);
        list.GetProperty("rebootPending").ValueKind.ShouldBe(JsonValueKind.False);
        list.GetProperty("activeInstall").ValueKind.ShouldBe(JsonValueKind.Null);
        list.GetProperty("recentInstalls").ValueKind.ShouldBe(JsonValueKind.Array);
        list.GetProperty("hidden").ValueKind.ShouldBe(JsonValueKind.Array);

        var firmware = list.GetProperty("updates").EnumerateArray().Single(u => u.GetProperty("updateId").GetString() == Firmware);
        firmware.GetProperty("installStatus").GetString().ShouldBe("reOffered");
        firmware.GetProperty("attempts").GetInt32().ShouldBe(1);
        firmware.GetProperty("lastAttemptAt").ValueKind.ShouldBe(JsonValueKind.String);
        firmware.GetProperty("lastHresult").GetInt32().ShouldBe(0);
        firmware.GetProperty("revision").GetInt32().ShouldBe(1, "existing fields are unchanged");
        firmware.GetProperty("isPreview").GetBoolean().ShouldBeFalse();

        var display = list.GetProperty("updates").EnumerateArray().Single(u => u.GetProperty("updateId").GetString() == Display);
        display.GetProperty("installStatus").GetString().ShouldBe("none");
        display.GetProperty("lastAttemptAt").ValueKind.ShouldBe(JsonValueKind.Null);
        display.GetProperty("lastHresult").ValueKind.ShouldBe(JsonValueKind.Null);

        var overview = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/admin/v1/driver-updates/overview", UriKind.Relative));
        var summary = overview.GetProperty("summary");
        summary.GetProperty("needsRestartCount").ValueKind.ShouldBe(JsonValueKind.Number);
        summary.GetProperty("cannotCompleteCount").ValueKind.ShouldBe(JsonValueKind.Number);
        summary.GetProperty("totalPending").ValueKind.ShouldBe(JsonValueKind.Number);

        var row = overview.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("deviceId").GetGuid() == deviceId);
        row.GetProperty("pendingCount").GetInt32().ShouldBe(1, "actionable only: the re-offered firmware is not counted");
        row.GetProperty("cannotCompleteCount").GetInt32().ShouldBe(1);
        row.GetProperty("needsRestartCount").GetInt32().ShouldBe(0);
        row.GetProperty("previewCount").GetInt32().ShouldBe(0);
        row.GetProperty("rebootPending").GetBoolean().ShouldBeFalse();
    }
}
