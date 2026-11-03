using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Temporary local administrator elevation over real HTTP against real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are the point. A permission check that is present, a scope check
/// that is enforced server-side, a protected account that cannot be targeted, and
/// a uniqueness rule that holds under a concurrent race are what make this a
/// control rather than a form.
/// </para>
/// <para>
/// The uniqueness test runs the two requests genuinely concurrently, because the
/// domain's snapshot check passes for both and only the database constraint can
/// separate them. A sequential test would pass against an implementation that has
/// no constraint at all.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class LocalAdminElevationEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string MachineSid = "S-1-5-21-7-7-7";
    private const string StandardSid = MachineSid + "-1001";
    private const string BuiltInSid = MachineSid + "-500";

    private static Uri Elevations(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/elevations", UriKind.Relative);

    private static Uri Approve(Guid id) => new($"/admin/v1/elevations/{id}/approve", UriKind.Relative);

    private static Uri Revoke(Guid id) => new($"/admin/v1/elevations/{id}/revoke", UriKind.Relative);

    private static JsonContent RequestBody(
        string sid = StandardSid, int? minutes = 60, string why = "Installing a signed vendor driver.") =>
        JsonContent.Create(new { targetSid = sid, justification = why, durationMinutes = minutes });

    /// <summary>
    /// One sign-in per identity for the whole class.
    /// </summary>
    /// <remarks>
    /// The login rate limiter is real, in-process and shared by every test in
    /// this collection. Signing in once per test spends that budget on
    /// authentication rather than on the behaviour under test, and — as this
    /// suite demonstrated when first written — pushes unrelated tests in other
    /// classes over the limit, where they fail for reasons that have nothing to
    /// do with them. Caching keeps the limiter intact rather than relaxing it for
    /// the convenience of tests.
    /// </remarks>
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

            // A fresh client over a cached session: building the client is free,
            // signing in again is not.
            return _fixture.CreateClientFor(token);
        }
        finally
        {
            SignInGate.Release();
        }
    }

    /// <summary>Seeds a device with one standard account and one built-in administrator.</summary>
    private async Task<Guid> SeedDeviceAsync(string hostname = "ELEV-PC")
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"elev-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, hostname, "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var now = DateTimeOffset.UtcNow;
        db.DeviceLocalUsers.Add(new DeviceLocalUser(
            device.Id, StandardSid, "sarah", "Sarah", null, true, true, true, now, false, now));
        db.DeviceLocalUsers.Add(new DeviceLocalUser(
            device.Id, BuiltInSid, "Administrator", "Built-in", null, false, true, true, now, true, now));

        await db.SaveChangesAsync();
        return device.Id;
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_cannot_request_an_elevation()
    {
        var deviceId = await SeedDeviceAsync();

        var response = await _fixture.Factory.CreateClient()
            .PostAsync(Elevations(deviceId), RequestBody());

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Helpdesk and Auditor cannot grant administrator rights.
    /// </summary>
    /// <remarks>
    /// Asserted over HTTP rather than only against the role table, because what
    /// protects the endpoint is the RequirePermission filter being present on it.
    /// A correct role definition and a missing attribute is still an open door.
    /// </remarks>
    [Theory]
    [InlineData("helpdesk")]
    [InlineData("auditor")]
    public async Task A_role_without_the_permission_cannot_request_or_revoke(string which)
    {
        var deviceId = await SeedDeviceAsync();
        var email = which == "helpdesk"
            ? AdminApiPostgresFixture.HelpdeskEmail
            : AdminApiPostgresFixture.AuditorEmail;

        using var client = await ClientAsync(email);

        (await client.PostAsync(Elevations(deviceId), RequestBody()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsync(Revoke(Guid.CreateVersion7()), JsonContent.Create(new { })))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Viewing is separated from mutating: an Auditor may read.</summary>
    [Fact]
    public async Task An_auditor_can_see_elevations_but_not_create_them()
    {
        var deviceId = await SeedDeviceAsync();

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        (await admin.PostAsync(Elevations(deviceId), RequestBody())).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var auditor = await ClientAsync(AdminApiPostgresFixture.AuditorEmail);
        var list = await auditor.GetAsync(Elevations(deviceId));

        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await list.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().ShouldBe(1);
    }

    // ---- the protected account ---------------------------------------------

    /// <summary>
    /// The built-in Administrator can never be the target.
    /// </summary>
    /// <remarks>
    /// Refused at the API, refused again in the service, and refused a third time
    /// in the domain constructor. Three layers because the consequence of missing
    /// it is an audit record that appears to authorize something nobody could
    /// have authorized.
    /// </remarks>
    [Fact]
    public async Task The_built_in_Administrator_cannot_be_elevated()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await client.PostAsync(Elevations(deviceId), RequestBody(sid: BuiltInSid));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.LocalAdminElevations.CountAsync(e => e.DeviceId == deviceId)).ShouldBe(0);
    }

    [Fact]
    public async Task An_account_the_endpoint_has_never_reported_cannot_be_elevated()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await client.PostAsync(
            Elevations(deviceId), RequestBody(sid: MachineSid + "-9999"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- lifecycle ---------------------------------------------------------

    [Fact]
    public async Task A_self_approved_request_is_approved_with_a_deadline()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await client.PostAsync(Elevations(deviceId), RequestBody(minutes: 60));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("state").GetString().ShouldBe("Approved");
        body.GetProperty("expiresAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    /// <summary>
    /// A request without a duration stays pending and confers nothing.
    /// </summary>
    [Fact]
    public async Task A_request_without_a_duration_is_pending_and_has_no_deadline()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var body = await (await client.PostAsync(Elevations(deviceId), RequestBody(minutes: null)))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("state").GetString().ShouldBe("Requested");
        body.GetProperty("expiresAt").ValueKind.ShouldBe(JsonValueKind.Null);

        // And the console reports it as conferring nothing.
        var rows = await (await client.GetAsync(Elevations(deviceId)))
            .Content.ReadFromJsonAsync<JsonElement>();
        rows.EnumerateArray().Single().GetProperty("isLive").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Approving_a_pending_request_sets_the_deadline()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var created = await (await client.PostAsync(Elevations(deviceId), RequestBody(minutes: null)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var approved = await client.PostAsync(Approve(id), JsonContent.Create(new { durationMinutes = 120 }));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await approved.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("state").GetString().ShouldBe("Approved");
        body.GetProperty("expiresAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Approval cannot be used to extend a live elevation.
    /// </summary>
    /// <remarks>
    /// The rule that keeps "when does this end" a question with one answer. A
    /// longer window means revoking and requesting again, which leaves two audit
    /// records instead of one that changed meaning.
    /// </remarks>
    [Fact]
    public async Task An_already_approved_elevation_cannot_be_approved_again()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var created = await (await client.PostAsync(Elevations(deviceId), RequestBody(minutes: 60)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var firstExpiry = created.GetProperty("expiresAt").GetDateTimeOffset();

        var again = await client.PostAsync(Approve(id), JsonContent.Create(new { durationMinutes = 480 }));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        var stored = await db.LocalAdminElevations.AsNoTracking().SingleAsync(e => e.Id == id);
        stored.ExpiresAt!.Value.ShouldBe(firstExpiry, TimeSpan.FromSeconds(1));
    }

    // ---- delivery to the endpoint -------------------------------------------
    //
    // Until 2026-11 an approved elevation was recorded and never sent: no route
    // queued the ApplyLocalAdminElevation task, so the endpoint never added the
    // account and the console read "Not applied yet" until the deadline.

    /// <summary>The ApplyLocalAdminElevation tasks queued for a device, oldest first, as their SID sets.</summary>
    private async Task<List<string[]>> PushedSetsAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var payloads = await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == Domain.Tasks.DeviceTaskType.ApplyLocalAdminElevation)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.PayloadJson)
            .ToListAsync();

        return payloads
            .Select(p => JsonDocument.Parse(p!).RootElement.GetProperty("elevations").EnumerateArray()
                .Select(e => e.GetProperty("sid").GetString()!).ToArray())
            .ToList();
    }

    [Fact]
    public async Task A_self_approved_request_is_sent_to_the_endpoint_at_once()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(Elevations(deviceId), RequestBody(minutes: 60))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PushedSetsAsync(deviceId)).ShouldHaveSingleItem().ShouldBe([StandardSid]);
    }

    [Fact]
    public async Task A_pending_request_sends_nothing_until_it_is_approved()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var created = await (await client.PostAsync(Elevations(deviceId), RequestBody(minutes: null)))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await PushedSetsAsync(deviceId)).ShouldBeEmpty();

        (await client.PostAsync(Approve(created.GetProperty("id").GetGuid()), JsonContent.Create(new { durationMinutes = 60 })))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PushedSetsAsync(deviceId)).ShouldHaveSingleItem().ShouldBe([StandardSid]);
    }

    [Fact]
    public async Task Revoking_sends_the_endpoint_a_set_without_the_account()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var created = await (await client.PostAsync(Elevations(deviceId), RequestBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await client.PostAsync(Revoke(created.GetProperty("id").GetGuid()), JsonContent.Create(new { note = "Done." })))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var pushed = await PushedSetsAsync(deviceId);
        pushed.Count.ShouldBe(2);
        pushed[0].ShouldBe([StandardSid]);
        pushed[1].ShouldBeEmpty();
    }

    [Fact]
    public async Task Revoking_ends_the_elevation_and_a_second_revoke_is_refused()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var created = await (await client.PostAsync(Elevations(deviceId), RequestBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        (await client.PostAsync(Revoke(id), JsonContent.Create(new { note = "No longer needed." })))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsync(Revoke(id), JsonContent.Create(new { })))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        var stored = await db.LocalAdminElevations.AsNoTracking().SingleAsync(e => e.Id == id);
        stored.State.ShouldBe(LocalAdminElevationState.Revoked);
        stored.IsLive(DateTimeOffset.UtcNow).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(481)]
    [InlineData(2000)]
    public async Task A_duration_outside_the_window_is_refused(int minutes)
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await client.PostAsync(Elevations(deviceId), RequestBody(minutes: minutes));

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);

        await using var db = _fixture.CreateDbContext();
        (await db.LocalAdminElevations.CountAsync(e => e.DeviceId == deviceId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_request_without_a_justification_is_refused()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await client.PostAsync(
            Elevations(deviceId), JsonContent.Create(new { targetSid = StandardSid, durationMinutes = 60 }));

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }

    // ---- uniqueness --------------------------------------------------------

    [Fact]
    public async Task A_second_elevation_for_the_same_account_is_refused()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(Elevations(deviceId), RequestBody())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(Elevations(deviceId), RequestBody())).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// The uniqueness rule holds under a genuine race.
    /// </summary>
    /// <remarks>
    /// This is the test that distinguishes a real guarantee from a courtesy. The
    /// domain's snapshot check passes for both requests -- neither can see the
    /// other's uncommitted row -- so only the partial unique index in PostgreSQL
    /// can stop the second window being created. Run sequentially, this test
    /// would pass against an implementation with no constraint at all.
    /// </remarks>
    [Fact]
    public async Task Concurrent_requests_for_the_same_account_produce_exactly_one_elevation()
    {
        var deviceId = await SeedDeviceAsync();
        using var seed = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var token = SessionTokens[AdminApiPostgresFixture.ItAdminEmail];

        // Separate clients so the two requests are genuinely in flight together.
        var attempts = Enumerable.Range(0, 6).Select(async _ =>
        {
            using var client = _fixture.CreateClientFor(token);
            return await client.PostAsync(Elevations(deviceId), RequestBody());
        });

        var responses = await Task.WhenAll(attempts);

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var refused = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        created.ShouldBe(1);
        refused.ShouldBe(responses.Length - 1);

        await using var db = _fixture.CreateDbContext();
        (await db.LocalAdminElevations.CountAsync(e => e.DeviceId == deviceId)).ShouldBe(1);
    }

    /// <summary>
    /// The constraint is partial: a finished elevation does not block a new one.
    /// </summary>
    /// <remarks>
    /// Without the filter on live states, an account could be elevated exactly
    /// once in its lifetime, because last month's expired record would collide
    /// with today's request.
    /// </remarks>
    [Fact]
    public async Task An_account_can_be_elevated_again_after_the_first_elevation_ends()
    {
        var deviceId = await SeedDeviceAsync();
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var first = await (await client.PostAsync(Elevations(deviceId), RequestBody()))
            .Content.ReadFromJsonAsync<JsonElement>();

        (await client.PostAsync(Revoke(first.GetProperty("id").GetGuid()), JsonContent.Create(new { })))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsync(Elevations(deviceId), RequestBody())).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        (await db.LocalAdminElevations.CountAsync(e => e.DeviceId == deviceId)).ShouldBe(2);
    }

    // ---- audit -------------------------------------------------------------

    [Fact]
    public async Task The_lifecycle_is_audited_with_actor_device_and_target()
    {
        var deviceId = await SeedDeviceAsync("ELEV-AUDIT");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var created = await (await client.PostAsync(Elevations(deviceId), RequestBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        await client.PostAsync(Revoke(id), JsonContent.Create(new { }));

        await using var db = _fixture.CreateDbContext();
        var entries = await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.Action.StartsWith("localuser.elevation") && e.TargetId == id.ToString())
            .ToListAsync();

        entries.Select(e => e.Action).ShouldBe(
            ["localuser.elevation.requested", "localuser.elevation.approved", "localuser.elevation.revoked"],
            ignoreOrder: true);

        foreach (var entry in entries)
        {
            entry.DeviceId.ShouldBe(deviceId);
            entry.ActorDisplay.ShouldBe(AdminApiPostgresFixture.ItAdminEmail);
            entry.TargetDisplay.ShouldBe("sarah");
        }

        // The SID is recorded, because a username alone cannot survive a rename.
        entries.ShouldContain(e => (e.NewState ?? string.Empty).Contains(StandardSid));
    }
}
