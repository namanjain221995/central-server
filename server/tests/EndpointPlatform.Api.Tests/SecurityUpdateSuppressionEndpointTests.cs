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
/// Hiding and un-hiding a security update over real HTTP against real PostgreSQL:
/// who may do it, about which devices, that a reason is required, that it is
/// idempotent and audited, and that the per-device read and the overview reflect it
/// in the wire shape the dashboard codes against.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class SecurityUpdateSuppressionEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string Lcu = "5e000005-0000-0000-0000-000000000001";
    private const string Dotnet = "5e000005-0000-0000-0000-000000000002";
    private const string Reason = "Breaks the line-of-business app; vendor fix due next month";

    private static Uri Suppress(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/security-updates/suppress", UriKind.Relative);

    private static Uri Unsuppress(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/security-updates/unsuppress", UriKind.Relative);

    private static Uri List(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/security-updates", UriKind.Relative);

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
            org.Id, $"sus-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "SUS-PC", "m-" + Guid.CreateVersion7().ToString("N"), "1.27.0", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var now = DateTimeOffset.UtcNow;
        db.DeviceSecurityUpdates.AddRange(
            new DeviceSecurityUpdate(device.Id, Lcu, 200, "2026-10 Cumulative Update (KB5044284)", "KB5044284", "Critical", false, 700_000_000, now),
            new DeviceSecurityUpdate(device.Id, Dotnet, 1, "Security Update for .NET (KB5043178)", "KB5043178", "Important", false, 20, now));

        var record = new DeviceSecurityUpdateInstallRecord(device.Id, Lcu, 200, "2026-10 Cumulative Update (KB5044284)", now.AddHours(-1));
        record.RecordAttempt(SecurityUpdateInstallStatus.ReOffered, 2, 0, Guid.CreateVersion7(), null, 200, now.AddHours(-1));
        db.DeviceSecurityUpdateInstallRecords.Add(record);

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<List<DeviceSecurityUpdateSuppression>> SuppressionsAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceSecurityUpdateSuppressions.AsNoTracking().Where(s => s.DeviceId == deviceId).ToListAsync();
    }

    [Fact]
    public async Task Hiding_is_idempotent_audited_and_moves_the_update_out_of_the_actionable_list()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var first = await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu, reason = "  " + Reason + "  " });
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var again = await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu.ToUpperInvariant(), reason = "a second reason" });
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent, "hiding an already-hidden update is not an error");

        var stored = (await SuppressionsAsync(deviceId)).ShouldHaveSingleItem("no duplicate rows");
        stored.Reason.ShouldBe(Reason, "trimmed, and the first reason stands");
        stored.Title.ShouldBe("2026-10 Cumulative Update (KB5044284)");
        stored.CreatedByDisplay.ShouldBe(AdminApiPostgresFixture.ItAdminEmail);

        await using (var db = _fixture.CreateDbContext())
        {
            var audits = await db.AuditLogEntries.AsNoTracking()
                .Where(a => a.DeviceId == deviceId && a.Action == "securityupdate.suppress")
                .ToListAsync();
            var audit = audits.ShouldHaveSingleItem("a no-op re-hide writes nothing");
            audit.RequiredPermission.ShouldBe("update.manage");
            audit.NewState.ShouldNotBeNull();
            audit.NewState.ShouldContain(Lcu);
            audit.NewState.ShouldContain("KB5044284");
            audit.NewState.ShouldContain("vendor fix due next month"); // the justification is on the audit trail
        }

        var list = await client.GetFromJsonAsync<JsonElement>(List(deviceId));
        var updates = list.GetProperty("updates").EnumerateArray().ToList();
        updates.Select(u => u.GetProperty("updateId").GetString()).ShouldBe(new[] { Dotnet });

        var hidden = list.GetProperty("hidden").EnumerateArray().Single();
        hidden.GetProperty("updateId").GetString().ShouldBe(Lcu);
        hidden.GetProperty("title").GetString().ShouldBe("2026-10 Cumulative Update (KB5044284)");
        hidden.GetProperty("reason").GetString().ShouldBe(Reason);
        hidden.GetProperty("hiddenBy").GetString().ShouldBe(AdminApiPostgresFixture.ItAdminEmail);
        hidden.GetProperty("hiddenAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    // The one deliberate difference from drivers: a security patch is never hidden silently.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Hiding_without_a_reason_is_refused(string? reason)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu, reason }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await SuppressionsAsync(deviceId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_hiding_is_idempotent_and_audited()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu, reason = Reason })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Lcu })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Lcu })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent, "un-hiding a visible update is not an error");
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Dotnet })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SuppressionsAsync(deviceId)).ShouldBeEmpty();

        await using (var db = _fixture.CreateDbContext())
        {
            var audit = (await db.AuditLogEntries.AsNoTracking()
                    .Where(a => a.DeviceId == deviceId && a.Action == "securityupdate.unsuppress")
                    .ToListAsync())
                .ShouldHaveSingleItem();
            audit.RequiredPermission.ShouldBe("update.manage");
            audit.PreviousState!.ShouldContain(Lcu);
            audit.PreviousState!.ShouldContain("vendor fix due next month");
        }

        var list = await client.GetFromJsonAsync<JsonElement>(List(deviceId));
        list.GetProperty("updates").GetArrayLength().ShouldBe(2);
        list.GetProperty("hidden").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Hiding_needs_update_manage()
    {
        var deviceId = await SeedDeviceAsync();

        // Helpdesk and Auditor read the pending list (update.view) but may not decide
        // what install-all puts on a machine.
        foreach (var email in new[] { AdminApiPostgresFixture.HelpdeskEmail, AdminApiPostgresFixture.AuditorEmail })
        {
            using var client = await ClientAsync(email);
            (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu, reason = Reason })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Lcu })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await SuppressionsAsync(deviceId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_hide_anything()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu, reason = Reason }))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = Lcu }))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_outside_the_callers_scope_or_unknown_is_not_found()
    {
        var inScope = await SeedDeviceAsync();
        var outOfScope = await SeedDeviceAsync();

        var email = $"sus-scoped-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == Domain.Authorization.SystemRoles.SuperAdministrator);

            var group = new DeviceGroup(org.Id, $"SusScope-{Guid.CreateVersion7():N}", "d", DeviceGroupType.Static);
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

        (await client.PostAsJsonAsync(Suppress(inScope), new { updateId = Lcu, reason = Reason })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Suppress(outOfScope), new { updateId = Lcu, reason = Reason })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Unsuppress(outOfScope), new { updateId = Lcu })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Suppress(Guid.CreateVersion7()), new { updateId = Lcu, reason = Reason })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Unsuppress(Guid.CreateVersion7()), new { updateId = Lcu })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The read the dashboard polls is scoped the same way.
        (await client.GetAsync(List(outOfScope))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await SuppressionsAsync(outOfScope)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_or_oversized_request_is_refused()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = "", reason = Reason })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = new string('x', 65), reason = Reason })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu, reason = new string('r', 201) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Unsuppress(deviceId), new { updateId = (string?)null })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Exactly 200 characters, after trimming, is accepted.
        (await client.PostAsJsonAsync(Suppress(deviceId), new { updateId = Lcu, reason = " " + new string('r', 200) + " " }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SuppressionsAsync(deviceId)).ShouldHaveSingleItem().Reason.Length.ShouldBe(200);
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

        var lcu = list.GetProperty("updates").EnumerateArray().Single(u => u.GetProperty("updateId").GetString() == Lcu);
        lcu.GetProperty("installStatus").GetString().ShouldBe("reOffered");
        lcu.GetProperty("attempts").GetInt32().ShouldBe(1);
        lcu.GetProperty("lastAttemptAt").ValueKind.ShouldBe(JsonValueKind.String);
        lcu.GetProperty("lastHresult").GetInt32().ShouldBe(0);
        lcu.GetProperty("revision").GetInt32().ShouldBe(200, "existing fields are unchanged");
        lcu.GetProperty("kbIds").GetString().ShouldBe("KB5044284");
        lcu.GetProperty("severity").GetString().ShouldBe("Critical");
        lcu.GetProperty("isPreview").GetBoolean().ShouldBeFalse();
        lcu.GetProperty("sizeBytes").GetInt64().ShouldBe(700_000_000);

        var dotnet = list.GetProperty("updates").EnumerateArray().Single(u => u.GetProperty("updateId").GetString() == Dotnet);
        dotnet.GetProperty("installStatus").GetString().ShouldBe("none");
        dotnet.GetProperty("attempts").GetInt32().ShouldBe(0);
        dotnet.GetProperty("lastAttemptAt").ValueKind.ShouldBe(JsonValueKind.Null);
        dotnet.GetProperty("lastHresult").ValueKind.ShouldBe(JsonValueKind.Null);

        var overview = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/admin/v1/security-updates/overview", UriKind.Relative));
        var summary = overview.GetProperty("summary");
        summary.GetProperty("needsRestartCount").ValueKind.ShouldBe(JsonValueKind.Number);
        summary.GetProperty("cannotCompleteCount").ValueKind.ShouldBe(JsonValueKind.Number);
        summary.GetProperty("totalPending").ValueKind.ShouldBe(JsonValueKind.Number);
        summary.GetProperty("devicesWithPending").ValueKind.ShouldBe(JsonValueKind.Number);
        summary.GetProperty("devicesRebootPending").ValueKind.ShouldBe(JsonValueKind.Number);

        var row = overview.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("deviceId").GetGuid() == deviceId);
        row.GetProperty("pendingCount").GetInt32().ShouldBe(2,
            "actionable = not hidden: the re-offered cumulative update still counts (a retry is usually the fix)");
        row.GetProperty("cannotCompleteCount").GetInt32().ShouldBe(1, "and it is flagged as needing attention too");
        row.GetProperty("needsRestartCount").GetInt32().ShouldBe(0);
        row.GetProperty("previewCount").GetInt32().ShouldBe(0);
        row.GetProperty("rebootPending").GetBoolean().ShouldBeFalse();
    }
}
