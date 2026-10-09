using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Peripherals;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Phones over the administrator API: grantable at read/write only, refused at
/// read-only, and shown with an enforcement state that says how far the
/// endpoint actually got.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class UsbPortableDeviceEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string PhoneId = @"USB\VID_2717&PID_FF40\EXAMPLE0SERIAL01";

    private async Task<HttpClient> ClientAsync(string email) =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(email));

    private async Task<Guid> SeedDeviceAsync(string hostname)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var enrollmentToken = new EnrollmentToken(
            organizationId, $"usb-phone-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "usb-phone",
            DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(enrollmentToken);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", "1.16.0",
            "Windows 11 Pro", enrollmentToken.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> SeedUsbAsync(
        Guid deviceId, string instanceId, UsbDeviceClass deviceClass = UsbDeviceClass.PortableDevice)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var usb = new UsbDevice(
            organizationId, deviceId, instanceId, deviceClass,
            "2717", "FF40", "EXAMPLE0SERIAL01", "Xiaomi", "Redmi Note 14 Pro 5G",
            @"USB\VID_2717&PID_FF40", DateTimeOffset.UtcNow);

        db.UsbDevices.Add(usb);
        await db.SaveChangesAsync();
        return usb.Id;
    }

    private static Uri GrantOf(Guid deviceId, Guid usbId) =>
        new($"/admin/v1/devices/{deviceId}/usb-devices/{usbId}/grant", UriKind.Relative);

    private static JsonContent GrantBody(string? policy, int minutes = 60) =>
        JsonContent.Create(new { durationMinutes = minutes, justification = "Field photos for the report.", policy });

    private async Task<JsonElement> RowAsync(HttpClient client, Guid deviceId, Guid usbId)
    {
        var rows = await (await client.GetAsync($"/admin/v1/devices/{deviceId}/usb-devices"))
            .Content.ReadFromJsonAsync<JsonElement>();

        return rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == usbId);
    }

    [Fact]
    public async Task A_phone_is_shown_as_restrictable_without_read_only()
    {
        var deviceId = await SeedDeviceAsync("USB-PHONE-VIEW");
        var usbId = await SeedUsbAsync(deviceId, PhoneId);

        var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);
        var row = await RowAsync(client, deviceId, usbId);

        row.GetProperty("deviceClass").GetString().ShouldBe("PortableDevice");
        row.GetProperty("isStorage").GetBoolean().ShouldBeFalse();
        row.GetProperty("isPortableDevice").GetBoolean().ShouldBeTrue();
        row.GetProperty("isRestrictable").GetBoolean().ShouldBeTrue();
        row.GetProperty("supportsReadOnly").GetBoolean().ShouldBeFalse();
        row.GetProperty("policy").GetString().ShouldBe("Restricted");
        row.GetProperty("enforcementState").GetString().ShouldBe("Pending");
    }

    /// <summary>
    /// Read-only on a phone is refused with 400 and nothing is recorded: the
    /// request is malformed for the device, and the two ways of rounding it
    /// are both wrong.
    /// </summary>
    [Theory]
    [InlineData("ReadOnly")]
    [InlineData(null)]
    public async Task A_phone_cannot_be_granted_read_only(string? policy)
    {
        var deviceId = await SeedDeviceAsync("USB-PHONE-RO");
        var usbId = await SeedUsbAsync(deviceId, PhoneId);

        var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var response = await client.PostAsync(GrantOf(deviceId, usbId), GrantBody(policy));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var db = _fixture.CreateDbContext();
        (await db.UsbAccessRequests.AnyAsync(r => r.UsbDeviceId == usbId)).ShouldBeFalse();
        (await db.UsbDevices.SingleAsync(u => u.Id == usbId)).Policy.ShouldBe(UsbStoragePolicy.Restricted);
    }

    [Fact]
    public async Task A_phone_can_be_granted_read_write_access_and_revoked()
    {
        var deviceId = await SeedDeviceAsync("USB-PHONE-RW");
        var usbId = await SeedUsbAsync(deviceId, PhoneId);

        var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var granted = await client.PostAsync(GrantOf(deviceId, usbId), GrantBody("Enabled"));

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await granted.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("policy").GetString().ShouldBe("Enabled");
        var requestId = body.GetProperty("requestId").GetGuid();

        var row = await RowAsync(client, deviceId, usbId);
        row.GetProperty("policy").GetString().ShouldBe("Enabled");
        row.GetProperty("liveRequestId").GetGuid().ShouldBe(requestId);

        var revoked = await client.PostAsync(
            new Uri($"/admin/v1/usb-access-requests/{requestId}/revoke", UriKind.Relative),
            JsonContent.Create(new { note = "Done." }));
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RowAsync(client, deviceId, usbId)).GetProperty("policy").GetString().ShouldBe("Restricted");
    }

    [Fact]
    public async Task Helpdesk_cannot_grant_a_phone_either()
    {
        var deviceId = await SeedDeviceAsync("USB-PHONE-HELPDESK");
        var usbId = await SeedUsbAsync(deviceId, PhoneId);

        var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);
        var response = await client.PostAsync(GrantOf(deviceId, usbId), GrantBody("Enabled"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The enforcement column tells the truth about how far the endpoint got.
    /// Only a verified state is "Enforced"; a restart-deferred change and a
    /// failure that kept the device restricted are each shown as what they are.
    /// </summary>
    [Fact]
    public async Task The_console_shows_how_far_the_endpoint_got()
    {
        var deviceId = await SeedDeviceAsync("USB-PHONE-STATES");

        var cases = new (string Suffix, UsbStoragePolicy? Enforced, string? Error, UsbEnforcementStatus? Status, string State)[]
        {
            ("VERIFIED", UsbStoragePolicy.Restricted, null, UsbEnforcementStatus.Verified, "Enforced"),
            ("UNVERIFIED", UsbStoragePolicy.Restricted, "could not read back", UsbEnforcementStatus.Unverified, "Applied"),
            ("RESTART", null, "a program is holding the device open", UsbEnforcementStatus.RequiresRestart, "RequiresRestart"),
            ("READONLY", UsbStoragePolicy.Restricted, "read-only is not available", UsbEnforcementStatus.Failed, "Failed"),
            ("OLDAGENT", UsbStoragePolicy.Restricted, null, null, "Applied"),
        };

        var ids = new Dictionary<string, Guid>();
        foreach (var c in cases)
        {
            var usbId = await SeedUsbAsync(deviceId, $@"USB\VID_2717&PID_FF40\{c.Suffix}");
            ids[c.Suffix] = usbId;

            await using var db = _fixture.CreateDbContext();
            var usb = await db.UsbDevices.SingleAsync(u => u.Id == usbId);
            usb.ReportEnforcement(c.Enforced, c.Error, DateTimeOffset.UtcNow, c.Status);
            await db.SaveChangesAsync();
        }

        var webcamId = await SeedUsbAsync(deviceId, @"USB\VID_2B7E&PID_B851\SN0001", UsbDeviceClass.Other);

        var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        foreach (var c in cases)
        {
            var row = await RowAsync(client, deviceId, ids[c.Suffix]);
            row.GetProperty("enforcementState").GetString().ShouldBe(c.State, $"state for {c.Suffix}");
            row.GetProperty("enforcementError").GetString().ShouldBe(c.Error, $"error for {c.Suffix}");
        }

        var webcam = await RowAsync(client, deviceId, webcamId);
        webcam.GetProperty("isRestrictable").GetBoolean().ShouldBeFalse();
        webcam.GetProperty("enforcementState").GetString().ShouldBe("NotApplicable");
    }
}
