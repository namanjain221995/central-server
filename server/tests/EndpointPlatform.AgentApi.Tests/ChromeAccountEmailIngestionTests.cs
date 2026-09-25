using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The signed-in account e-mail on a Chrome profile: stored as reported, absent
/// when the agent did not send it (an older agent, or a profile nobody is signed
/// in to), and bounded like every other wire string.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class ChromeAccountEmailIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const string Sid = "S-1-5-21-1000-2000-3000-1001";

    private static InventoryReport Report(InventoryChrome chrome) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, chrome);

    private static InventoryChromeProfile Profile(string key, string? accountEmail) =>
        new(Sid, @"WORKGROUP\someone", key, "Solo", @"C:\Users\someone\AppData\Local\Google\Chrome\User Data\" + key,
            IsManaged: false, LastActiveAt: null, Extensions: [], AccountEmail: accountEmail);

    private static InventoryChrome Section(params InventoryChromeProfile[] profiles) =>
        new("Available",
            new InventoryChromeInstallation("131.0.6778.86", null, "x64", "stable", "Machine", null, null, null),
            profiles);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"chr-mail-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "CHR-MAIL-PC", $"machine-{Guid.CreateVersion7():N}", "1.13.2", null)));
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

    private Task<HttpResponseMessage> UploadAsync(HttpClient client, string credential, InventoryChrome chrome) =>
        client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(chrome), credential));

    [Fact]
    public async Task The_account_email_is_stored_as_reported_and_absent_when_not_sent()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential,
            Section(Profile("Default", "someone@example.com"), Profile("Profile 1", null)));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var rows = await db.ChromeProfiles.Where(p => p.DeviceId == deviceId).OrderBy(p => p.ProfileKey).ToListAsync();

        rows.Count.ShouldBe(2);
        rows[0].AccountEmail.ShouldBe("someone@example.com");
        rows[1].AccountEmail.ShouldBeNull();
    }

    [Fact]
    public async Task An_over_long_account_email_is_refused()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential,
            Section(Profile("Default", new string('a', InventoryChromeProfile.MaxAccountEmail) + "@example.com")));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
