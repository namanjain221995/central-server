using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The local-administrator posture endpoint, over real HTTP against real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Milestone 11b is an evaluation milestone, so the assertions divide in two:
/// the verdict is right, and <b>nothing was changed to reach it</b>. The second
/// is the one worth testing over HTTP — a reporting feature that quietly
/// remediated would be a far worse defect than one that reported wrongly.
/// </para>
/// <para>
/// The verdict logic itself is covered exhaustively in
/// <c>LocalAdministratorPostureTests</c> against the pure domain function. These
/// tests are about the endpoint around it: authorization, device scope, the
/// shape of the payload, and the Unknown case.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class LocalAdminPostureEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string MachineSid = "S-1-5-21-9-8-7";

    private static Uri PostureOf(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/local-admin-posture", UriKind.Relative);

    /// <summary>Seeds a device, optionally with local accounts.</summary>
    private async Task<Guid> SeedDeviceAsync(params (int Rid, string Name, bool Enabled, bool IsAdmin)[] accounts)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"posture-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "POSTURE-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var now = DateTimeOffset.UtcNow;
        foreach (var (rid, name, enabled, isAdmin) in accounts)
        {
            db.DeviceLocalUsers.Add(new DeviceLocalUser(
                device.Id, $"{MachineSid}-{rid}", name, name, null,
                enabled, true, true, now, isAdmin, now));
        }

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<JsonElement> GetPostureAsync(Guid deviceId, string email)
    {
        var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));
        var response = await client.GetAsync(PostureOf(deviceId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---- the verdict, end to end -------------------------------------------

    [Fact]
    public async Task A_machine_whose_interactive_user_is_standard_reports_Compliant()
    {
        var deviceId = await SeedDeviceAsync(
            (1001, "sarah", true, false),
            (500, "Administrator", false, true));

        var body = await GetPostureAsync(deviceId, AdminApiPostgresFixture.ItAdminEmail);

        body.GetProperty("compliance").GetString().ShouldBe("Compliant");
        body.GetProperty("interactiveAdministrators").GetArrayLength().ShouldBe(0);
        body.GetProperty("lastReportedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_machine_with_an_interactive_administrator_reports_NonCompliant_and_names_it()
    {
        var deviceId = await SeedDeviceAsync(
            (1001, "sarah", true, true),
            (1002, "raj", true, false));

        var body = await GetPostureAsync(deviceId, AdminApiPostgresFixture.ItAdminEmail);

        body.GetProperty("compliance").GetString().ShouldBe("NonCompliant");

        var offenders = body.GetProperty("interactiveAdministrators").EnumerateArray().ToList();
        offenders.Count.ShouldBe(1);
        offenders[0].GetProperty("username").GetString().ShouldBe("sarah");
    }

    /// <summary>
    /// No reported accounts is Unknown, and says so with a null timestamp.
    /// </summary>
    /// <remarks>
    /// The requirement that Unknown never quietly becomes Compliant, asserted at
    /// the boundary a console actually reads. The absent timestamp is the
    /// evidence: there is no verdict because there is no report.
    /// </remarks>
    [Fact]
    public async Task A_device_that_has_reported_no_accounts_is_Unknown()
    {
        var deviceId = await SeedDeviceAsync();

        var body = await GetPostureAsync(deviceId, AdminApiPostgresFixture.ItAdminEmail);

        body.GetProperty("compliance").GetString().ShouldBe("Unknown");
        body.GetProperty("lastReportedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("findings").GetArrayLength().ShouldBe(0);
    }

    /// <summary>
    /// Excluded accounts are returned with the reason they were discounted.
    /// </summary>
    [Fact]
    public async Task Excluded_accounts_are_reported_rather_than_omitted()
    {
        var deviceId = await SeedDeviceAsync(
            (500, "Administrator", true, true),
            (1001, "sarah", true, false));

        var body = await GetPostureAsync(deviceId, AdminApiPostgresFixture.ItAdminEmail);

        body.GetProperty("compliance").GetString().ShouldBe("Compliant");

        var findings = body.GetProperty("findings").EnumerateArray().ToList();
        findings.Count.ShouldBe(2);

        var builtIn = findings.Single(f => f.GetProperty("username").GetString() == "Administrator");
        builtIn.GetProperty("isAdministrator").GetBoolean().ShouldBeTrue();
        builtIn.GetProperty("countsAgainstCompliance").GetBoolean().ShouldBeFalse();
        builtIn.GetProperty("excludedReason").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>The stated scope travels with the verdict.</summary>
    /// <remarks>
    /// A caller acting on this answer should be able to see what it does not
    /// cover without reading the source or the documentation.
    /// </remarks>
    [Fact]
    public async Task The_nested_group_limitation_is_stated_in_the_payload()
    {
        var deviceId = await SeedDeviceAsync((1001, "sarah", true, false));

        var body = await GetPostureAsync(deviceId, AdminApiPostgresFixture.ItAdminEmail);

        var limitation = body.GetProperty("limitation").GetString();
        limitation.ShouldNotBeNull();
        limitation!.ShouldContain("nested group");
    }

    // ---- it evaluates, it does not remediate --------------------------------

    /// <summary>
    /// Reading the posture changes nothing on the endpoint.
    /// </summary>
    /// <remarks>
    /// The load-bearing test of this milestone. A reporting feature that
    /// remediated as a side effect — demoting the administrator it found, or
    /// queueing a task to do so — would be a far more serious defect than a
    /// wrong verdict, and it would be invisible until somebody's machine changed
    /// under them. Asserted against both the account rows and the task queue.
    /// </remarks>
    [Fact]
    public async Task Reading_the_posture_neither_changes_an_account_nor_queues_a_task()
    {
        var deviceId = await SeedDeviceAsync(
            (1001, "sarah", true, true),
            (500, "Administrator", true, true));

        await GetPostureAsync(deviceId, AdminApiPostgresFixture.ItAdminEmail);
        await GetPostureAsync(deviceId, AdminApiPostgresFixture.ItAdminEmail);

        await using var db = _fixture.CreateDbContext();

        var accounts = await db.DeviceLocalUsers.AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .ToListAsync();

        accounts.Count.ShouldBe(2);
        accounts.ShouldAllBe(a => a.IsLocalAdministrator);
        accounts.ShouldAllBe(a => a.Enabled);

        (await db.DeviceTasks.AsNoTracking().CountAsync(t => t.DeviceId == deviceId))
            .ShouldBe(0);
    }

    // ---- authorization ------------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        var deviceId = await SeedDeviceAsync((1001, "sarah", true, true));

        var response = await _fixture.Factory.CreateClient().GetAsync(PostureOf(deviceId));

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Auditor may read the posture; it is a view, and Auditor is read-only.
    /// </summary>
    [Fact]
    public async Task An_auditor_can_read_the_posture()
    {
        var deviceId = await SeedDeviceAsync((1001, "sarah", true, true));

        var body = await GetPostureAsync(deviceId, AdminApiPostgresFixture.AuditorEmail);

        body.GetProperty("compliance").GetString().ShouldBe("NonCompliant");
    }

    [Fact]
    public async Task An_unknown_device_is_not_found()
    {
        var client = _fixture.CreateClientFor(
            await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail));

        var response = await client.GetAsync(PostureOf(Guid.CreateVersion7()));

        // Exactly 404 now: the device routes answer unknown and out-of-scope ids
        // identically, so this no longer has to tolerate the old scope 403.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
