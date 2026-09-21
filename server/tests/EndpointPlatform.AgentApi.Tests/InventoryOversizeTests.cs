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
/// A section larger than its limit is truncated, not a reason to discard the report.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the opposite shipped. Every <c>Max*</c> in
/// <see cref="DeviceInventoryService"/> is a truncation limit — the service takes
/// that many entries and ignores the rest — but the endpoint separately refused any
/// report that exceeded one of those counts. The two disagreed, and the endpoint won,
/// so a single oversized section threw away the WHOLE report: BitLocker state, local
/// accounts, drivers, software, security posture, all of it.
/// </para>
/// <para>
/// It was not a corner case. A developer laptop running VMware, VirtualBox, Hyper-V
/// and a VPN client enumerated 81 network interfaces against a limit of 64. That
/// machine reported no inventory at all, retried every 98 seconds forever, and looked
/// perfectly healthy in the console the whole time — online, Active, heartbeating.
/// Nothing was logged server-side, so the only evidence was the agent's own log.
/// </para>
/// <para>
/// So: oversized is accepted and truncated; only an implausible payload
/// (<see cref="DeviceInventoryService.OversizeRefusalMultiplier"/> times the limit)
/// is refused, because an unbounded list from a compromised agent is still a
/// denial-of-service vector.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class InventoryOversizeTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static InventoryReport ReportWithInterfaces(int count)
    {
        var interfaces = Enumerable.Range(0, count)
            .Select(i => new InventoryNetworkInterface(
                $"Ethernet {i}",
                // A distinct, well-formed MAC per interface.
                $"A1B2C3{i:D6}"[..12],
                [$"10.0.{i / 256}.{i % 256}"],
                IsUp: i % 2 == 0))
            .ToArray();

        return new InventoryReport(
            new InventoryHardware(
                "SER-OVERSIZE", "Contoso", "Model X", "CPU", 8, 16, 34_359_738_368,
                [new InventoryDisk("C:", "NTFS", 512_000_000_000, 200_000_000_000)]),
            interfaces,
            @"CORP\jsmith",
            DateTimeOffset.UtcNow);
    }

    private async Task<(Guid DeviceId, string Credential)> SeedDeviceCredentialAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"oversize-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "inventory-oversize-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Domain.Devices.Device.Enroll(
            org.Id, "OVERSIZE-PC", $"machine-{Guid.CreateVersion7()}", "1.11.0",
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

    private static HttpRequestMessage NewRequest(string route, object body, string credential)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return message;
    }

    /// <summary>
    /// The exact shape that was failing in the field: more interfaces than the
    /// limit, by a margin a real machine actually produces.
    /// </summary>
    [Fact]
    public async Task More_network_interfaces_than_the_limit_is_accepted_and_truncated()
    {
        const int Reported = 81; // the count observed on a real developer laptop
        Assert.True(Reported > DeviceInventoryService.MaxNetworkInterfaces);

        var (deviceId, credential) = await SeedDeviceCredentialAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, ReportWithInterfaces(Reported), credential));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a machine with more interfaces than expected must still get an inventory, "
            + "not lose every other section of the report");

        await using var db = _fixture.CreateDbContext();

        var stored = await db.DeviceNetworkInterfaces.AsNoTracking()
            .Where(n => n.DeviceId == deviceId)
            .CountAsync();

        stored.ShouldBe(
            DeviceInventoryService.MaxNetworkInterfaces,
            "the service truncates to its limit rather than storing everything");

        // The part that actually mattered: the rest of the report survived.
        var device = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        device.InventoryCollectedAt.ShouldNotBeNull(
            "an accepted report sets the collection timestamp; a refused one leaves it null, "
            + "which is what made the failure invisible in the console");
    }

    /// <summary>
    /// The bound is still a bound. An implausible payload is refused outright.
    /// </summary>
    [Fact]
    public async Task An_implausible_number_of_interfaces_is_still_refused()
    {
        var beyondCeiling =
            (DeviceInventoryService.MaxNetworkInterfaces * DeviceInventoryService.OversizeRefusalMultiplier) + 1;

        var (deviceId, credential) = await SeedDeviceCredentialAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, ReportWithInterfaces(beyondCeiling), credential));

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "an unbounded list from a compromised agent is a denial-of-service vector");

        await using var db = _fixture.CreateDbContext();
        var device = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        device.InventoryCollectedAt.ShouldBeNull("a refused report must not be applied");
    }

    /// <summary>
    /// Exactly at the limit is ordinary, and exactly at the ceiling is still accepted —
    /// the refusal is strictly above it, so neither boundary is off by one.
    /// </summary>
    [Theory]
    [InlineData(DeviceInventoryService.MaxNetworkInterfaces)]
    [InlineData(DeviceInventoryService.MaxNetworkInterfaces * DeviceInventoryService.OversizeRefusalMultiplier)]
    public async Task Boundaries_are_accepted(int count)
    {
        var (_, credential) = await SeedDeviceCredentialAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, ReportWithInterfaces(count), credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
