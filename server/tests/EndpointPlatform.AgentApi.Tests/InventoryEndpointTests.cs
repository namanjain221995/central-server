using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Phase 2 inventory matrix: authenticated upload persists and replaces,
/// unauthenticated/malformed rejected, heartbeat carries the refresh handshake.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class InventoryEndpointTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static InventoryReport MakeReport(string serial = "SER-001") =>
        new(
            new InventoryHardware(
                serial,
                "Dell Inc.",
                "Latitude 5450",
                "Intel Core Ultra 7 165U",
                12,
                14,
                34_359_738_368,
                [new InventoryDisk("C:", "NTFS", 512_000_000_000, 200_000_000_000)]),
            [new InventoryNetworkInterface("Ethernet", "A1B2C3D4E5F6", ["10.0.0.5", "fe80::1234"], true)],
            @"CORP\jsmith",
            DateTimeOffset.UtcNow);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var secret = SecretGenerator.GenerateSecret();
        dbContext.EnrollmentTokens.Add(new EnrollmentToken(
            organization.Id, $"inv-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await dbContext.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var enroll = NewRequest(AgentProtocol.Routes.Enroll, new EnrollRequest(
            secret, "INV-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", "Windows 11"));
        var response = await client.SendAsync(enroll);
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage NewRequest(string route, object body, string? credential = null)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        }

        return message;
    }

    /// <summary>
    /// An authenticated device, without going through enrollment.
    /// </summary>
    /// <remarks>
    /// Enrollment is rate limited and that budget is shared by every test in this
    /// assembly, so classes that enroll once per test start pushing others into
    /// 429 as the suite grows. The software tests below need an authenticated
    /// device and nothing more, so they seed one rather than spending enrollment
    /// budget that the enrollment tests actually need.
    /// </remarks>
    private async Task<(Guid DeviceId, string Credential)> SeedDeviceCredentialAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"sw-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "software-inventory-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Domain.Devices.Device.Enroll(
            org.Id, "SW-PC", $"machine-{Guid.CreateVersion7()}", "1.5.0",
            "Microsoft Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var secret = SecretGenerator.GenerateSecret();
        db.AgentCredentials.Add(new AgentCredential(
            device.Id, SecretGenerator.GenerateKeyId(), SecretGenerator.HashSecret(secret),
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync();

        var credential = await db.AgentCredentials.AsNoTracking()
            .SingleAsync(c => c.DeviceId == device.Id);

        return (device.Id, $"{credential.KeyId}.{secret}");
    }

    /// <summary>
    /// Per-user software is persisted with the account it belongs to.
    /// </summary>
    /// <remarks>
    /// The 1.5.0 discovery fix exists because per-user installs were invisible:
    /// the agent read HKCU while running as LocalSystem, which is SYSTEM's own
    /// profile and holds nothing. These fields are what make such an install
    /// distinguishable from a machine-wide one once it is finally collected.
    /// </remarks>
    [Fact]
    public async Task Software_scope_user_attribution_and_product_code_are_persisted()
    {
        var (deviceId, credential) = await SeedDeviceCredentialAsync();
        using var client = _fixture.Factory.CreateClient();

        var report = MakeReport() with
        {
            Software =
            [
                new InventorySoftware(
                    "Zoom Workplace", "7.1.5", "Zoom Communications, Inc.", null, null, null,
                    InstallationScope: "User", InstalledForUser: @"CORP\jsmith", ProductCode: null),
                new InventorySoftware(
                    "7-Zip", "24.09", "Igor Pavlov", null, @"C:\Program Files\7-Zip", "x64",
                    InstallationScope: "Machine", InstalledForUser: null,
                    ProductCode: "{23170F69-40C1-2702-2409-000001000000}"),
            ],
        };

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, report, credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _fixture.CreateDbContext();
        var software = await dbContext.DeviceSoftware
            .Where(s => s.DeviceId == deviceId)
            .ToListAsync();

        var zoom = software.Single(s => s.Name == "Zoom Workplace");
        zoom.InstallationScope.ShouldBe("User");
        zoom.InstalledForUser.ShouldBe(@"CORP\jsmith");
        zoom.ProductCode.ShouldBeNull();

        var sevenZip = software.Single(s => s.Name == "7-Zip");
        sevenZip.InstallationScope.ShouldBe("Machine");
        // A machine-wide install belongs to no single account; saying otherwise
        // would be an attribution the platform cannot support.
        sevenZip.InstalledForUser.ShouldBeNull();
        sevenZip.ProductCode.ShouldBe("{23170F69-40C1-2702-2409-000001000000}");
    }

    /// <summary>
    /// An agent predating 1.5.0 keeps working: the fields it never sends are
    /// simply absent, not a rejected payload.
    /// </summary>
    /// <remarks>
    /// Most of the fleet runs 1.1.x. If adding these fields had broken their
    /// uploads, the platform would have lost inventory for nearly every device.
    /// </remarks>
    [Fact]
    public async Task An_older_agent_that_omits_the_new_software_fields_is_still_accepted()
    {
        var (deviceId, credential) = await SeedDeviceCredentialAsync();
        using var client = _fixture.Factory.CreateClient();

        var report = MakeReport() with
        {
            Software = [new InventorySoftware("Legacy App", "1.0", "Contoso", null, null, "x64")],
        };

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, report, credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _fixture.CreateDbContext();
        var app = await dbContext.DeviceSoftware.SingleAsync(s => s.DeviceId == deviceId);

        app.Name.ShouldBe("Legacy App");
        app.InstallationScope.ShouldBeNull();
        app.InstalledForUser.ShouldBeNull();
        app.ProductCode.ShouldBeNull();
    }

    /// <summary>
    /// An over-length new field is refused like any other, rather than reaching
    /// the database and failing there.
    /// </summary>
    [Fact]
    public async Task An_over_length_software_field_is_rejected()
    {
        var (_, credential) = await SeedDeviceCredentialAsync();
        using var client = _fixture.Factory.CreateClient();

        var report = MakeReport() with
        {
            Software =
            [
                new InventorySoftware(
                    "App", "1.0", "Contoso", null, null, "x64",
                    InstalledForUser: new string('u', 257)),
            ],
        };

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, report, credential));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_authenticated_inventory_upload_persists_hardware_and_network()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, MakeReport(), credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _fixture.CreateDbContext();

        var hardware = await dbContext.DeviceHardware.SingleAsync(h => h.DeviceId == deviceId);
        hardware.SerialNumber.ShouldBe("SER-001");
        hardware.Manufacturer.ShouldBe("Dell Inc.");
        hardware.CpuPhysicalCores.ShouldBe(12);
        hardware.DisksJson.ShouldNotBeNull();
        hardware.DisksJson!.ShouldContain("NTFS");

        var nics = await dbContext.DeviceNetworkInterfaces.Where(n => n.DeviceId == deviceId).ToListAsync();
        nics.Count.ShouldBe(1);
        nics[0].MacAddress.ShouldBe("A1:B2:C3:D4:E5:F6");
        nics[0].IpAddressesJson.ShouldNotBeNull();
        nics[0].IpAddressesJson!.ShouldContain("10.0.0.5");

        var device = await dbContext.Devices.SingleAsync(d => d.Id == deviceId);
        device.LoggedOnUser.ShouldBe(@"CORP\jsmith");
        device.InventoryCollectedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_second_upload_replaces_the_snapshot_instead_of_accumulating()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, MakeReport("SER-A"), credential));

        var second = MakeReport("SER-B") with
        {
            NetworkInterfaces =
            [
                new InventoryNetworkInterface("Wi-Fi", "0011223344556677", ["192.168.1.20"], true),
                new InventoryNetworkInterface("Ethernet 2", null, [], false),
            ],
        };
        await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, second, credential));

        await using var dbContext = _fixture.CreateDbContext();

        (await dbContext.DeviceHardware.CountAsync(h => h.DeviceId == deviceId)).ShouldBe(1);
        (await dbContext.DeviceHardware.SingleAsync(h => h.DeviceId == deviceId)).SerialNumber.ShouldBe("SER-B");

        var nics = await dbContext.DeviceNetworkInterfaces.Where(n => n.DeviceId == deviceId).ToListAsync();
        nics.Count.ShouldBe(2, "the interface set must be replaced, not appended");
        nics.Select(n => n.Name).ShouldBe(["Ethernet 2", "Wi-Fi"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_unauthenticated_upload_is_rejected()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, MakeReport()));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_malformed_mac_is_rejected_with_400_not_500()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var report = MakeReport() with
        {
            NetworkInterfaces = [new InventoryNetworkInterface("Evil", "zz:not-a-mac", [], true)],
        };

        var response = await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, report, credential));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// More interfaces than the limit is accepted and truncated, not refused.
    /// </summary>
    /// <remarks>
    /// This test previously asserted the opposite. Refusing cost the entire report
    /// — BitLocker state, local accounts, drivers, software — over one oversized
    /// section, while <see cref="DeviceInventoryService"/> was already written to
    /// truncate that same section and carry on. A real laptop running VMware,
    /// VirtualBox, Hyper-V and a VPN client enumerates 81 interfaces against a
    /// limit of 64, and reported nothing at all, forever, in silence.
    /// <para>
    /// The bound still exists; see <see cref="InventoryOversizeTests"/> for the
    /// ceiling above which a payload is refused as implausible.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_oversized_interface_list_is_truncated_not_rejected()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var report = MakeReport() with
        {
            NetworkInterfaces = Enumerable.Range(0, 100)
                .Select(i => new InventoryNetworkInterface($"nic{i}", null, [], false))
                .ToArray(),
        };

        var response = await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, report, credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceNetworkInterfaces.CountAsync(n => n.DeviceId == deviceId))
            .ShouldBe(DeviceInventoryService.MaxNetworkInterfaces);
    }

    [Fact]
    public async Task The_heartbeat_refresh_handshake_round_trips()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        // 1. Fresh device: no inventory yet -> heartbeat says upload.
        var first = await client.SendAsync(NewRequest(
            AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("INV-PC", "1.0.0", null, DateTimeOffset.UtcNow),
            credential));
        (await first.Content.ReadFromJsonAsync<HeartbeatResponse>())!.InventoryRequested.ShouldBeTrue();

        // 2. Upload -> next heartbeat is satisfied.
        await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, MakeReport(), credential));

        var second = await client.SendAsync(NewRequest(
            AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("INV-PC", "1.0.0", null, DateTimeOffset.UtcNow),
            credential));
        (await second.Content.ReadFromJsonAsync<HeartbeatResponse>())!.InventoryRequested.ShouldBeFalse();

        // 3. Admin requests a refresh -> pending again.
        await using (var dbContext = _fixture.CreateDbContext())
        {
            var device = await dbContext.Devices.SingleAsync(d => d.Id == deviceId);
            device.RequestInventoryRefresh(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        var third = await client.SendAsync(NewRequest(
            AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("INV-PC", "1.0.0", null, DateTimeOffset.UtcNow),
            credential));
        (await third.Content.ReadFromJsonAsync<HeartbeatResponse>())!.InventoryRequested.ShouldBeTrue();
    }
}
