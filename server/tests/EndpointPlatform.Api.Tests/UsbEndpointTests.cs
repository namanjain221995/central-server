using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// USB peripheral control end to end over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// The cases that matter here are the refusals: who cannot grant access, what
/// cannot be granted, and what the server publishes to an endpoint once a grant
/// is revoked or has lapsed. A green test for the happy path proves the feature
/// works; these prove it is a control.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class UsbEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> ClientAsync(string email) =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(email));

    private async Task<(Guid DeviceId, string CredentialHeader)> SeedDeviceAsync(string hostname)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var enrollmentToken = new EnrollmentToken(
            organizationId, $"usb-test-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "usb-test",
            DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(enrollmentToken);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", "1.1.1",
            "Windows 11 Pro", enrollmentToken.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var keyId = Guid.CreateVersion7().ToString("N");
        var secret = Guid.CreateVersion7().ToString("N");

        db.AgentCredentials.Add(new AgentCredential(
            device.Id,
            keyId,
            Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret))),
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync();
        return (device.Id, $"{keyId}.{secret}");
    }

    /// <summary>Adds a USB device row directly, as an agent report would.</summary>
    private async Task<Guid> SeedUsbAsync(
        Guid deviceId, string instanceId, UsbDeviceClass deviceClass = UsbDeviceClass.Storage)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var usb = new UsbDevice(
            organizationId, deviceId, instanceId, deviceClass,
            "0781", "5581", "ABC123", "SanDisk", "Cruzer Fit", null, DateTimeOffset.UtcNow);

        db.UsbDevices.Add(usb);
        await db.SaveChangesAsync();
        return usb.Id;
    }

    private static Uri GrantOf(Guid deviceId, Guid usbId) =>
        new($"/admin/v1/devices/{deviceId}/usb-devices/{usbId}/grant", UriKind.Relative);

    private static JsonContent GrantBody(int minutes = 120, string why = "Vendor firmware on a stick.") =>
        JsonContent.Create(new { durationMinutes = minutes, justification = why });

    // ---- authorization -----------------------------------------------------

    /// <summary>
    /// Auditor is read-only, including here.
    /// </summary>
    /// <remarks>
    /// Asserted over HTTP rather than only against the role definition, because
    /// the thing that actually protects the endpoint is the RequirePermission
    /// filter being present on it. A correct role table and a missing attribute
    /// would still be an open door.
    /// </remarks>
    [Fact]
    public async Task Auditor_can_see_usb_devices_but_cannot_grant_access()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-RBAC-1");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\RBAC1");

        using var auditor = await ClientAsync(AdminApiPostgresFixture.AuditorEmail);

        var read = await auditor.GetAsync(
            new Uri($"/admin/v1/devices/{deviceId}/usb-devices", UriKind.Relative));
        read.StatusCode.ShouldBe(HttpStatusCode.OK);

        var grant = await auditor.PostAsync(GrantOf(deviceId, usbId), GrantBody());
        grant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Helpdesk_can_see_usb_devices_but_cannot_grant_access()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-RBAC-2");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\RBAC2");

        using var helpdesk = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);

        var read = await helpdesk.GetAsync(
            new Uri($"/admin/v1/devices/{deviceId}/usb-devices", UriKind.Relative));
        read.StatusCode.ShouldBe(HttpStatusCode.OK);

        var grant = await helpdesk.PostAsync(GrantOf(deviceId, usbId), GrantBody());
        grant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_grant_usb_access()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-RBAC-3");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\RBAC3");

        using var anonymous = _fixture.Factory.CreateClient();

        var grant = await anonymous.PostAsync(GrantOf(deviceId, usbId), GrantBody());
        grant.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- granting ----------------------------------------------------------

    [Fact]
    public async Task An_administrator_grant_is_read_only_time_boxed_and_queues_the_policy()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-GRANT-1");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\GRANT1");

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await admin.PostAsync(GrantOf(deviceId, usbId), GrantBody(120));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("policy").GetString().ShouldBe("ReadOnly");

        var expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset();
        expiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(110));
        expiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(130));

        await using var db = _fixture.CreateDbContext();

        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.Id == usbId);
        usb.Policy.ShouldBe(UsbStoragePolicy.ReadOnly);

        // The endpoint is told, through the existing typed-task channel.
        var task = await db.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.ApplyUsbPolicy)
            .OrderByDescending(t => t.CreatedAt)
            .FirstAsync();

        task.PayloadJson.ShouldNotBeNull();
        using var payload = JsonDocument.Parse(task.PayloadJson!);
        var grant = payload.RootElement.GetProperty("grants")[0];
        grant.GetProperty("instanceId").GetString().ShouldBe(@"USB\VID_0781&PID_5581\GRANT1");
        grant.GetProperty("policy").GetString().ShouldBe("ReadOnly");
    }

    [Fact]
    public async Task A_non_storage_peripheral_cannot_be_granted_access()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-GRANT-2");
        var keyboardId = await SeedUsbAsync(
            deviceId, @"USB\VID_046D&PID_C31C\5&1&0&1", UsbDeviceClass.Keyboard);

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await admin.PostAsync(GrantOf(deviceId, keyboardId), GrantBody());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(1)]      // under the minimum
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(4321)]   // over 24 hours
    public async Task A_grant_outside_the_permitted_window_is_refused(int minutes)
    {
        var (deviceId, _) = await SeedDeviceAsync($"USB-DUR-{minutes}");
        var usbId = await SeedUsbAsync(deviceId, $@"USB\VID_0781&PID_5581\DUR{minutes}");

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await admin.PostAsync(GrantOf(deviceId, usbId), GrantBody(minutes));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_grant_without_a_justification_is_refused()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-JUST-1");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\JUST1");

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await admin.PostAsync(
            GrantOf(deviceId, usbId),
            JsonContent.Create(new { durationMinutes = 60, justification = "" }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_overlapping_grants_for_one_device_are_refused()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-DUP-1");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\DUP1");

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await admin.PostAsync(GrantOf(deviceId, usbId), GrantBody())).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        (await admin.PostAsync(GrantOf(deviceId, usbId), GrantBody())).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- revocation --------------------------------------------------------

    /// <summary>
    /// After revocation the server must stop publishing the grant, immediately.
    /// </summary>
    /// <remarks>
    /// This is the assertion that matters most for revocation: not that a status
    /// column changed, but that the next thing the endpoint is told contains no
    /// grant at all. An endpoint acts on the policy it receives, so that is what
    /// the test reads.
    /// </remarks>
    [Fact]
    public async Task Revoking_removes_the_grant_from_what_the_endpoint_is_told()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-REVOKE-1");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\REVOKE1");

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var granted = await admin.PostAsync(GrantOf(deviceId, usbId), GrantBody(600));
        var requestId = (await granted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requestId").GetGuid();

        // What the agent would be told right now: one live grant.
        var before = await PublishedPolicyAsync(deviceId);
        before.Grants.Count.ShouldBe(1);
        before.Grants[0].Policy.ShouldBe("ReadOnly");

        var revoke = await admin.PostAsync(
            new Uri($"/admin/v1/usb-access-requests/{requestId}/revoke", UriKind.Relative),
            JsonContent.Create(new { note = "No longer needed." }));

        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await PublishedPolicyAsync(deviceId);
        after.Grants.ShouldBeEmpty();

        await using var db = _fixture.CreateDbContext();
        var request = await db.UsbAccessRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        request.Status.ShouldBe(UsbAccessRequestStatus.Revoked);
        request.ExpiresAt!.Value.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);

        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.Id == usbId);
        usb.Policy.ShouldBe(UsbStoragePolicy.Restricted);
    }

    [Fact]
    public async Task Revoking_a_grant_that_is_not_live_is_refused()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-REVOKE-2");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\REVOKE2");

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var granted = await admin.PostAsync(GrantOf(deviceId, usbId), GrantBody());
        var requestId = (await granted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requestId").GetGuid();

        var revokeUri = new Uri($"/admin/v1/usb-access-requests/{requestId}/revoke", UriKind.Relative);

        (await admin.PostAsync(revokeUri, JsonContent.Create(new { note = (string?)null })))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await admin.PostAsync(revokeUri, JsonContent.Create(new { note = (string?)null })))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Auditor_cannot_revoke_either()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-REVOKE-3");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\REVOKE3");

        using var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var granted = await admin.PostAsync(GrantOf(deviceId, usbId), GrantBody());
        var requestId = (await granted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requestId").GetGuid();

        using var auditor = await ClientAsync(AdminApiPostgresFixture.AuditorEmail);

        var revoke = await auditor.PostAsync(
            new Uri($"/admin/v1/usb-access-requests/{requestId}/revoke", UriKind.Relative),
            JsonContent.Create(new { note = (string?)null }));

        revoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Asks the server exactly what it would publish to this endpoint.
    /// </summary>
    /// <remarks>
    /// Calls the same <c>BuildPolicyAsync</c> that both delivery channels use —
    /// the pushed task and the agent's report response — so a test asserting on
    /// it is asserting on what an endpoint really receives, not on a
    /// reimplementation of the rule. The Agent API is a separate host and is
    /// exercised in its own suite; this reads the source both hosts share.
    /// </remarks>
    private async Task<Contracts.Agent.UsbPolicyResponse> PublishedPolicyAsync(Guid deviceId)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var usbService = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Peripherals.UsbService>();

        return await usbService.BuildPolicyAsync(deviceId, DateTimeOffset.UtcNow);
    }

    // ---- what the console can actually display -----------------------------

    /// <summary>
    /// Every state the console needs to render, read back over HTTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requirement 7 of the acceptance list, asserted at the layer the dashboard
    /// actually reads. The row carries two separate facts — what an
    /// administrator decided (<c>policy</c>) and what the endpoint confirmed it
    /// is doing (<c>enforcementState</c>) — and the console shows both. Merging
    /// them would let an offline machine display "Restricted" as though the
    /// control were in place.
    /// </para>
    /// <para>
    /// Written as one test over several seeded devices rather than a theory: the
    /// login rate limiter is real and in-process, and a theory here would spend
    /// five sign-ins proving one thing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_console_can_see_attachment_policy_and_enforcement_separately()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-VIEWSTATES");

        // "Enforced" is reserved for a state the endpoint verified against
        // Windows. An agent that reported success without a status — every
        // agent before 1.16.0 — gets "Applied", so the console never calls a
        // device protected on the strength of a call having returned.
        var cases = new (string Suffix, UsbStoragePolicy? Enforced, string? Error, UsbEnforcementStatus? Status,
            string Policy, string State)[]
        {
            ("PENDING", null, null, null, "Restricted", "Pending"),
            ("APPLIED", UsbStoragePolicy.Restricted, null, null, "Restricted", "Applied"),
            ("ENFORCED", UsbStoragePolicy.Restricted, null, UsbEnforcementStatus.Verified, "Restricted", "Enforced"),
            ("UNVERIFIED", UsbStoragePolicy.Restricted, "could not read back", UsbEnforcementStatus.Unverified,
                "Restricted", "Applied"),
            ("DRIFTRO", UsbStoragePolicy.ReadOnly, null, UsbEnforcementStatus.Verified, "Restricted", "Drifted"),
            ("DRIFTRW", UsbStoragePolicy.Enabled, null, null, "Restricted", "Drifted"),
            ("FAILED", null, "access denied", null, "Restricted", "Failed"),
            ("FAILED2", UsbStoragePolicy.Restricted, "kept restricted", UsbEnforcementStatus.Failed, "Restricted", "Failed"),
            ("RESTART", null, "held open", UsbEnforcementStatus.RequiresRestart, "Restricted", "RequiresRestart"),
        };

        var ids = new Dictionary<string, Guid>();

        foreach (var c in cases)
        {
            var usbId = await SeedUsbAsync(deviceId, $@"USB\VID_0781&PID_5581\{c.Suffix}");
            ids[c.Suffix] = usbId;

            await using var db = _fixture.CreateDbContext();
            var usb = await db.UsbDevices.SingleAsync(u => u.Id == usbId);
            usb.ReportEnforcement(c.Enforced, c.Error, DateTimeOffset.UtcNow, c.Status);
            await db.SaveChangesAsync();
        }

        var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var response = await client.GetAsync($"/admin/v1/devices/{deviceId}/usb-devices");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var c in cases)
        {
            var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == ids[c.Suffix]);

            row.GetProperty("isStorage").GetBoolean().ShouldBeTrue();
            row.GetProperty("isConnected").GetBoolean().ShouldBeTrue();
            row.GetProperty("policy").GetString().ShouldBe(c.Policy, $"policy for {c.Suffix}");
            row.GetProperty("enforcementState").GetString().ShouldBe(c.State, $"state for {c.Suffix}");
        }
    }

    /// <summary>
    /// A device the endpoint has restricted is still listed as storage, and can
    /// still be granted access at either level.
    /// </summary>
    /// <remarks>
    /// The regression this pins is the one that made the feature unusable.
    /// Restricting a stick disables its devnode, which removes the driver and the
    /// child devnodes the agent used to recognise storage — so it reappeared as
    /// an anonymous peripheral, dropped out of the console's storage table, and
    /// could never be granted access again. Recovery depends on it staying in the
    /// storage view.
    /// </remarks>
    [Fact]
    public async Task A_restricted_storage_device_stays_visible_and_grantable()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-RECOVERY");
        var usbId = await SeedUsbAsync(deviceId, @"USB\VID_0781&PID_5581\RECOVER1");

        await using (var db = _fixture.CreateDbContext())
        {
            var usb = await db.UsbDevices.SingleAsync(u => u.Id == usbId);
            usb.ReportEnforcement(UsbStoragePolicy.Restricted, null, DateTimeOffset.UtcNow, UsbEnforcementStatus.Verified);
            await db.SaveChangesAsync();
        }

        var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var rows = await (await client.GetAsync($"/admin/v1/devices/{deviceId}/usb-devices"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == usbId);

        row.GetProperty("isStorage").GetBoolean().ShouldBeTrue();
        row.GetProperty("isRestrictable").GetBoolean().ShouldBeTrue();
        row.GetProperty("supportsReadOnly").GetBoolean().ShouldBeTrue();
        row.GetProperty("enforcementState").GetString().ShouldBe("Enforced");

        var granted = await client.PostAsync(
            GrantOf(deviceId, usbId),
            JsonContent.Create(new
            {
                durationMinutes = 60,
                justification = "Recovering a restricted stick.",
                policy = "Enabled",
            }));

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await granted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("policy").GetString().ShouldBe("Enabled");
    }

    /// <summary>
    /// No non-storage peripheral is grantable, at either level.
    /// </summary>
    /// <remarks>
    /// The server's half of the defect that disabled a root hub on the acceptance
    /// machine. The agent no longer classifies a hub as storage, and the server
    /// independently refuses to grant one — neither side relies on the other
    /// having got it right.
    /// </remarks>
    [Fact]
    public async Task No_non_storage_peripheral_is_grantable_at_either_level()
    {
        var (deviceId, _) = await SeedDeviceAsync("USB-NONSTORAGE");

        var classes = new[]
        {
            UsbDeviceClass.Hub,
            UsbDeviceClass.Keyboard,
            UsbDeviceClass.Mouse,
            UsbDeviceClass.NetworkAdapter,
            UsbDeviceClass.Other,
        };

        var ids = new List<(UsbDeviceClass Class, Guid Id)>();
        foreach (var deviceClass in classes)
        {
            ids.Add((deviceClass, await SeedUsbAsync(
                deviceId, $@"USB\ROOT_HUB30\NS_{deviceClass}", deviceClass)));
        }

        var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        foreach (var (deviceClass, usbId) in ids)
        {
            foreach (var level in new[] { "ReadOnly", "Enabled" })
            {
                var response = await client.PostAsync(
                    GrantOf(deviceId, usbId),
                    JsonContent.Create(new
                    {
                        durationMinutes = 60,
                        justification = "Should never be permitted.",
                        policy = level,
                    }));

                response.StatusCode.ShouldBe(
                    HttpStatusCode.Conflict, $"{deviceClass} at {level}");
            }
        }
    }
}
