using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The sign-in lifecycle facts on a Chrome profile: stored exactly as reported,
/// and null -- unknown, not false -- when the agent did not send them, which is
/// what every agent older than the fields does.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class ChromeSignInIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const string Sid = "S-1-5-21-1000-2000-3000-1001";

    private static InventoryReport Report(InventoryChrome chrome) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, chrome);

    private static InventoryChromeProfile Profile(string key, bool? signedIn, bool? signInBroken) =>
        new(Sid, @"WORKGROUP\someone", key, "Solo", @"C:\Users\someone\AppData\Local\Google\Chrome\User Data\" + key,
            IsManaged: false, LastActiveAt: null, Extensions: [], AccountEmail: null,
            SignedIn: signedIn, SignInBroken: signInBroken);

    private static InventoryChrome Section(params InventoryChromeProfile[] profiles) =>
        new("Available",
            new InventoryChromeInstallation("131.0.6778.86", null, "x64", "stable", "Machine", null, null, null),
            profiles);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"chr-signin-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "CHR-SIGNIN-PC", $"machine-{Guid.CreateVersion7():N}", "1.25.0", null)));
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
    public async Task The_sign_in_facts_are_stored_as_reported()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await UploadAsync(client, credential,
            Section(
                Profile("Default", signedIn: true, signInBroken: false),
                Profile("Profile 1", signedIn: false, signInBroken: null)));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var rows = await db.ChromeProfiles.Where(p => p.DeviceId == deviceId).OrderBy(p => p.ProfileKey).ToListAsync();

        rows.Count.ShouldBe(2);
        rows[0].SignedIn.ShouldBe(true);
        rows[0].SignInBroken.ShouldBe(false);
        rows[1].SignedIn.ShouldBe(false);
        rows[1].SignInBroken.ShouldBeNull("today's agents always send null here; null is unknown, never healthy");
    }

    /// <summary>
    /// An agent older than the fields omits them from the JSON entirely; the
    /// record defaults bind them to null and the stored row says unknown.
    /// </summary>
    [Fact]
    public async Task An_old_agent_that_omits_the_fields_stores_unknown_not_false()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        // Serialized by hand to send exactly what a pre-lifecycle agent sends:
        // a profile entry with no signedIn and no signInBroken member at all.
        var report = new
        {
            hardware = new { serialNumber = (string?)null, manufacturer = (string?)null, model = (string?)null, cpuName = (string?)null, cpuPhysicalCores = (int?)null, cpuLogicalProcessors = (int?)null, totalMemoryBytes = (long?)null, disks = Array.Empty<object>() },
            networkInterfaces = Array.Empty<object>(),
            loggedOnUser = (string?)null,
            collectedAt = DateTimeOffset.UtcNow,
            chrome = new
            {
                status = "Available",
                installation = new
                {
                    version = "126.0.6478.61",
                    executablePath = (string?)null,
                    architecture = "x64",
                    channel = "stable",
                    installationScope = "Machine",
                    installedForUser = (string?)null,
                    updaterVersion = (string?)null,
                    lastUpdateCheck = (DateTimeOffset?)null,
                },
                profiles = new[]
                {
                    new
                    {
                        userSid = Sid,
                        userAccount = @"WORKGROUP\someone",
                        profileKey = "Default",
                        profileName = "Solo",
                        profilePath = @"C:\Users\someone\AppData\Local\Google\Chrome\User Data\Default",
                        isManaged = false,
                        lastActiveAt = (DateTimeOffset?)null,
                        extensions = Array.Empty<object>(),
                    },
                },
            },
        };

        var response = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, report, credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var row = await db.ChromeProfiles.SingleAsync(p => p.DeviceId == deviceId);

        row.SignedIn.ShouldBeNull();
        row.SignInBroken.ShouldBeNull();
    }
}
