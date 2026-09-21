using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>Phase 8: Windows Update history + reboot state flow through ingestion.</summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class WindowsUpdateIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static InventoryReport Report(InventoryWindowsUpdate? update) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, WindowsUpdate: update);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"wu-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "WU-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage Req(string route, object body, string? credential = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        { Content = JsonContent.Create(body) };
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    [Fact]
    public async Task Update_status_and_history_persist_and_count_failures()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var report = Report(new InventoryWindowsUpdate(true,
        [
            new InventoryUpdateHistoryEntry("2026-08 Cumulative Update", DateTimeOffset.UtcNow.AddDays(-1), "Installation", "Succeeded"),
            new InventoryUpdateHistoryEntry("Defender definition update", DateTimeOffset.UtcNow.AddDays(-2), "Installation", "Failed"),
            new InventoryUpdateHistoryEntry("Feature update", DateTimeOffset.UtcNow.AddDays(-3), "Installation", "Aborted"),
        ]));

        (await client.SendAsync(Req(AgentProtocol.Routes.Inventory, report, credential)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var status = await db.DeviceUpdateStatus.SingleAsync(u => u.DeviceId == deviceId);
        status.RebootRequired.ShouldBeTrue();
        status.FailedUpdateCount.ShouldBe(2);

        var history = await db.DeviceUpdateHistory.Where(h => h.DeviceId == deviceId).ToListAsync();
        history.Count.ShouldBe(3);
        history.ShouldContain(h => h.Title == "2026-08 Cumulative Update" && h.Result == "Succeeded");
    }

    [Fact]
    public async Task History_is_replaced_wholesale_on_the_next_report()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(new InventoryWindowsUpdate(false,
            [new InventoryUpdateHistoryEntry("Old update", DateTimeOffset.UtcNow.AddDays(-10), "Installation", "Succeeded")])), credential));

        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(new InventoryWindowsUpdate(false,
            [new InventoryUpdateHistoryEntry("New update", DateTimeOffset.UtcNow, "Installation", "Succeeded")])), credential));

        await using var db = _fixture.CreateDbContext();
        var history = await db.DeviceUpdateHistory.Where(h => h.DeviceId == deviceId).ToListAsync();
        history.ShouldHaveSingleItem().Title.ShouldBe("New update");
        (await db.DeviceUpdateStatus.SingleAsync(u => u.DeviceId == deviceId)).FailedUpdateCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_omitted_update_section_keeps_the_previous_snapshot()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(new InventoryWindowsUpdate(true,
            [new InventoryUpdateHistoryEntry("Kept update", DateTimeOffset.UtcNow, "Installation", "Succeeded")])), credential));
        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(null), credential));

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceUpdateHistory.CountAsync(h => h.DeviceId == deviceId)).ShouldBe(1);
        (await db.DeviceUpdateStatus.SingleAsync(u => u.DeviceId == deviceId)).RebootRequired.ShouldBeTrue();
    }

    /// <summary>
    /// A longer update history than expected is truncated, not a reason to discard
    /// the whole inventory. See <see cref="InventoryOversizeTests"/>.
    /// </summary>
    [Fact]
    public async Task Too_many_history_entries_are_truncated_not_rejected()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var tooMany = Enumerable.Range(0, 201)
            .Select(i => new InventoryUpdateHistoryEntry($"KB{i}", DateTimeOffset.UtcNow, "Installation", "Succeeded"))
            .ToArray();

        (await client.SendAsync(Req(AgentProtocol.Routes.Inventory,
            Report(new InventoryWindowsUpdate(false, tooMany)), credential)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceUpdateHistory.CountAsync(h => h.DeviceId == deviceId))
            .ShouldBe(EndpointPlatform.Infrastructure.Devices.DeviceInventoryService.MaxUpdateHistory);
    }
}
