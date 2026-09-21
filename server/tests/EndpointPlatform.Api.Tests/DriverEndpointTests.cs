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
/// Driver inventory and driver health over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Read-only endpoints, so the weight here is on who may read them and about which
/// devices — asserted over HTTP rather than against the role table, because what
/// protects a route is the filter being present on it. A correct role definition
/// with a missing attribute is still an open door.
/// </para>
/// <para>
/// The rest asserts that the verdict the API returns is the one the domain computes,
/// including the two cases most easily got wrong: an unread problem state must not
/// read as healthy, and a device this platform disabled must not read as broken.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DriverEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri Drivers(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/drivers", UriKind.Relative);

    private static Uri Health(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/driver-health", UriKind.Relative);

    // One sign-in per identity for the whole class: the login rate limiter is real,
    // in-process and shared across this collection.
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

    private async Task<Guid> SeedDeviceAsync(params (string Name, int? ProblemCode)[] drivers)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"drv-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "DRV-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var now = DateTimeOffset.UtcNow;
        foreach (var (name, problemCode) in drivers)
        {
            db.DeviceDrivers.Add(new DeviceDriver(
                device.Id, $"PCI\\VEN_1234&{name}", name, "System", "Contoso",
                "Contoso Inc", "1.2.3.4", now.AddYears(-1), "oem7.inf", problemCode, true, now));
        }

        await db.SaveChangesAsync();
        return device.Id;
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_sees_nothing()
    {
        var deviceId = await SeedDeviceAsync(("nic", 0));

        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(Drivers(deviceId)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.GetAsync(Health(deviceId)))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Reading why a device is not working is diagnosis, so Helpdesk and Auditor
    /// both hold it. Neither can change anything: there is no mutating route here
    /// to hold in the first place.
    /// </summary>
    [Theory]
    [InlineData("helpdesk")]
    [InlineData("auditor")]
    public async Task Read_only_roles_can_see_driver_health(string which)
    {
        var deviceId = await SeedDeviceAsync(("nic", 0), ("gpu", 28));

        var email = which == "helpdesk"
            ? AdminApiPostgresFixture.HelpdeskEmail
            : AdminApiPostgresFixture.AuditorEmail;

        using var client = await ClientAsync(email);

        (await client.GetAsync(Drivers(deviceId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Health(deviceId))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A device outside the caller's assigned scope is not merely unreadable, it is
    /// invisible: the response says the device is not there rather than admitting it
    /// exists and is off-limits.
    /// </summary>
    [Fact]
    public async Task A_device_outside_the_callers_scope_is_invisible()
    {
        var inScope = await SeedDeviceAsync(("nic", 0));
        var outOfScope = await SeedDeviceAsync(("nic", 0));

        var email = $"drv-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"DrvScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
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

        (await client.GetAsync(Drivers(inScope))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Health(inScope))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync(Drivers(outOfScope))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(Health(outOfScope))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- what the endpoints report -----------------------------------------

    [Fact]
    public async Task The_inventory_reports_the_facts_and_the_verdict_side_by_side()
    {
        var deviceId = await SeedDeviceAsync(("gpu", 28));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var rows = await client.GetFromJsonAsync<JsonElement>(Drivers(deviceId));
        var row = rows.EnumerateArray().Single();

        row.GetProperty("deviceName").GetString().ShouldBe("gpu");
        row.GetProperty("driverProvider").GetString().ShouldBe("Contoso Inc");
        row.GetProperty("driverVersion").GetString().ShouldBe("1.2.3.4");
        row.GetProperty("infName").GetString().ShouldBe("oem7.inf");
        row.GetProperty("isSigned").GetBoolean().ShouldBeTrue();

        // The raw code an engineer searches for, and this platform's reading of it.
        row.GetProperty("problemCode").GetInt32().ShouldBe(28);
        row.GetProperty("health").GetString().ShouldBe("Problem");
        row.GetProperty("faultKind").GetString().ShouldBe("Driver");
        row.GetProperty("problemDescription").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_inventory_can_be_narrowed_to_the_devices_that_are_faulted()
    {
        var deviceId = await SeedDeviceAsync(("nic", 0), ("gpu", 28), ("stick", 22));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var all = await client.GetFromJsonAsync<JsonElement>(Drivers(deviceId));
        all.GetArrayLength().ShouldBe(3);

        var faults = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/admin/v1/devices/{deviceId}/drivers?problemsOnly=true", UriKind.Relative));

        faults.GetArrayLength().ShouldBe(1);
        faults.EnumerateArray().Single().GetProperty("deviceName").GetString().ShouldBe("gpu");
    }

    [Fact]
    public async Task Health_summarises_the_faults_by_what_they_are_attributable_to()
    {
        var deviceId = await SeedDeviceAsync(("nic", 0), ("gpu", 28), ("dock", 24), ("odd", 10));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var health = await client.GetFromJsonAsync<JsonElement>(Health(deviceId));

        health.GetProperty("state").GetString().ShouldBe("Problem");
        health.GetProperty("driverFaultCount").GetInt32().ShouldBe(1);
        health.GetProperty("deviceFaultCount").GetInt32().ShouldBe(1);
        health.GetProperty("indeterminateFaultCount").GetInt32().ShouldBe(1);
        health.GetProperty("totalCount").GetInt32().ShouldBe(4);
        health.GetProperty("faults").GetArrayLength().ShouldBe(3);
        health.GetProperty("lastReportedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    /// <summary>
    /// A device this platform disabled — which is what USB storage restriction does
    /// — must not make the endpoint look damaged.
    /// </summary>
    [Fact]
    public async Task A_disabled_device_is_reported_separately_and_is_not_a_fault()
    {
        var deviceId = await SeedDeviceAsync(("nic", 0), ("stick", 22));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var health = await client.GetFromJsonAsync<JsonElement>(Health(deviceId));

        health.GetProperty("state").GetString().ShouldBe("Healthy");
        health.GetProperty("disabledCount").GetInt32().ShouldBe(1);
        health.GetProperty("faults").GetArrayLength().ShouldBe(0);
    }

    /// <summary>
    /// The distinction the nullable problem code exists for, asserted end to end:
    /// an unread state must not arrive at a console as health.
    /// </summary>
    [Fact]
    public async Task An_unread_problem_state_is_reported_as_unknown_not_healthy()
    {
        var deviceId = await SeedDeviceAsync(("mystery", null));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var health = await client.GetFromJsonAsync<JsonElement>(Health(deviceId));
        health.GetProperty("state").GetString().ShouldBe("Unknown");
        health.GetProperty("unknownCount").GetInt32().ShouldBe(1);

        var row = (await client.GetFromJsonAsync<JsonElement>(Drivers(deviceId))).EnumerateArray().Single();
        row.GetProperty("health").GetString().ShouldBe("Unknown");
        row.GetProperty("problemCode").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// An endpoint that has never reported drivers is Unknown. Reporting Healthy
    /// would claim the estate had been checked when it has not.
    /// </summary>
    [Fact]
    public async Task A_device_that_has_reported_nothing_is_unknown()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var health = await client.GetFromJsonAsync<JsonElement>(Health(deviceId));

        health.GetProperty("state").GetString().ShouldBe("Unknown");
        health.GetProperty("totalCount").GetInt32().ShouldBe(0);
        health.GetProperty("lastReportedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>The stated scope travels with the verdict, as it does for posture.</summary>
    [Fact]
    public async Task Health_states_its_own_limitation()
    {
        var deviceId = await SeedDeviceAsync(("nic", 0));
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var health = await client.GetFromJsonAsync<JsonElement>(Health(deviceId));

        health.GetProperty("limitation").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unknown_device_is_not_found()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.GetAsync(Health(Guid.CreateVersion7()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
