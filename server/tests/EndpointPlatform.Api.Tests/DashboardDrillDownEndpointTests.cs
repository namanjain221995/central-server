using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Peripherals;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// What the dashboard's tiles open: the device list filtered to online or offline
/// machines, the USB storage devices and phones plugged in across the fleet, and
/// the audit log behind "Recent activity".
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DashboardDrillDownEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private async Task<HttpClient> ClientAsync(string email) =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(email));

    private async Task<Guid> SeedDeviceAsync(string hostname, DateTimeOffset lastSeenAt)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var enrollmentToken = new EnrollmentToken(
            organizationId, $"drill-down-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "drill-down",
            DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(enrollmentToken);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", "1.16.2",
            "Windows 11 Pro", enrollmentToken.Id, lastSeenAt);
        db.Devices.Add(device);

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> SeedUsbAsync(Guid deviceId, UsbDeviceClass deviceClass, string product)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var usb = new UsbDevice(
            organizationId, deviceId, $@"USB\VID_2717&PID_FF40\{Guid.CreateVersion7():N}", deviceClass,
            "2717", "FF40", null, "Example", product, @"USB\VID_2717&PID_FF40", DateTimeOffset.UtcNow);

        db.UsbDevices.Add(usb);
        await db.SaveChangesAsync();
        return usb.Id;
    }

    private static async Task<List<string>> HostnamesAsync(HttpClient client, string query)
    {
        var page = await (await client.GetAsync($"/admin/v1/devices?{query}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        return page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("hostname").GetString()!)
            .ToList();
    }

    [Fact]
    public async Task The_device_list_filters_to_online_or_offline_machines_as_the_counts_do()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await SeedDeviceAsync($"ON-{tag}", DateTimeOffset.UtcNow);
        await SeedDeviceAsync($"OFF-{tag}", DateTimeOffset.UtcNow.AddHours(-2));

        var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);

        (await HostnamesAsync(client, $"search={tag}&connectivity=online")).ShouldBe([$"ON-{tag}"]);
        (await HostnamesAsync(client, $"search={tag}&connectivity=offline")).ShouldBe([$"OFF-{tag}"]);
        (await HostnamesAsync(client, $"search={tag}")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_connectivity_is_refused_rather_than_answered_with_an_empty_page()
    {
        var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);

        var response = await client.GetAsync("/admin/v1/devices?connectivity=sometimes");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Connected_usb_lists_storage_and_phones_with_their_machine_and_leaves_out_inventory()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var deviceId = await SeedDeviceAsync($"USB-FLEET-{tag}", DateTimeOffset.UtcNow);
        var phoneId = await SeedUsbAsync(deviceId, UsbDeviceClass.PortableDevice, "Phone " + tag);
        var stickId = await SeedUsbAsync(deviceId, UsbDeviceClass.Storage, "Stick " + tag);
        var keyboardId = await SeedUsbAsync(deviceId, UsbDeviceClass.Keyboard, "Keyboard " + tag);

        var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);
        var response = await client.GetAsync("/admin/v1/usb-devices/connected");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Where(r => r.GetProperty("deviceId").GetGuid() == deviceId)
            .ToList();

        rows.Select(r => r.GetProperty("id").GetGuid()).ShouldBe([phoneId, stickId], ignoreOrder: true);
        rows.ShouldNotContain(r => r.GetProperty("id").GetGuid() == keyboardId);

        var phone = rows.Single(r => r.GetProperty("id").GetGuid() == phoneId);
        phone.GetProperty("deviceName").GetString().ShouldBe($"USB-FLEET-{tag}");
        phone.GetProperty("isPortableDevice").GetBoolean().ShouldBeTrue();
        phone.GetProperty("policy").GetString().ShouldBe("Restricted");
    }

    [Fact]
    public async Task The_audit_log_lists_what_happened_to_a_device_newest_first()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var deviceId = await SeedDeviceAsync($"AUDIT-{tag}", DateTimeOffset.UtcNow);

        var admin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        for (var i = 1; i <= 2; i++)
        {
            (await admin.PatchAsJsonAsync($"/admin/v1/devices/{deviceId}/display-name", new { displayName = $"Desk {i}" }))
                .IsSuccessStatusCode.ShouldBeTrue();
        }

        var auditor = await ClientAsync(AdminApiPostgresFixture.AuditorEmail);
        var response = await auditor.GetAsync($"/admin/v1/audit-log?deviceId={deviceId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        rows.Count.ShouldBeGreaterThanOrEqualTo(2);
        rows.ShouldAllBe(r => r.GetProperty("deviceId").GetGuid() == deviceId);
        rows.Select(r => r.GetProperty("occurredAt").GetDateTimeOffset())
            .ShouldBeInOrder(SortDirection.Descending);
        rows[0].GetProperty("actorDisplay").GetString().ShouldNotBeNullOrWhiteSpace();
        rows[0].GetProperty("action").GetString().ShouldNotBeNullOrWhiteSpace();

        // Paging by time: everything before the newest row is the rest of the list.
        var newest = rows[0].GetProperty("occurredAt").GetDateTimeOffset();
        var older = (await (await auditor.GetAsync(
                $"/admin/v1/audit-log?deviceId={deviceId}&before={Uri.EscapeDataString(newest.ToString("O"))}"))
            .Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        older.Count.ShouldBe(rows.Count - 1);
    }

    [Fact]
    public async Task The_audit_log_needs_audit_view()
    {
        var helpdesk = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);

        var response = await helpdesk.GetAsync("/admin/v1/audit-log");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
