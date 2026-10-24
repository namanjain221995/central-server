using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Agent release lifecycle over real HTTP against real PostgreSQL: upload,
/// publish, revoke, download, and queueing a device self-update — plus the
/// refusals that make the mechanism safe (wrong hash, downgrades, unpublished
/// releases, and roles without deploy rights).
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class AgentReleaseEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Releases = new("/admin/v1/agent-releases", UriKind.Relative);

    /// <summary>
    /// An MSI-shaped artifact signed by the fixture authority, so it passes the
    /// publish gate. The seed is folded into a second stream so each call yields
    /// distinct bytes and therefore a distinct content address.
    /// </summary>
    private static (byte[] Bytes, string Sha256) Content(string seed, string version)
    {
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: seed, productVersion: version);
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static MultipartFormDataContent Upload(
        byte[] bytes, string sha256, string version, string? notes = null)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent
        {
            { file, "file", $"EndpointPlatformAgent-{version}-x64.msi" },
            { new StringContent(version), "version" },
            { new StringContent(sha256), "sha256" },
        };
        if (notes is not null)
        {
            form.Add(new StringContent(notes), "releaseNotes");
        }

        return form;
    }

    /// <summary>
    /// A fresh deploy-capable administrator per test. Publish and update-agent
    /// are step-up gated behind a per-user rate limit (five actions per window,
    /// success does not reset it), so tests sharing one account would exhaust
    /// the budget between them; see <see cref="ReleaseStepUpTestSupport"/>.
    /// </summary>
    private async Task<HttpClient> AdminAsync() =>
        (await _fixture.MintDeployAdminAsync("rel-ep")).Client;

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid releaseId) =>
        client.PostAsync(
            new Uri($"/admin/v1/agent-releases/{releaseId}/publish", UriKind.Relative),
            ReleaseStepUpTestSupport.PublishBody());

    private async Task<Guid> CreateDraftAsync(HttpClient client, string version, string seed)
    {
        var (bytes, sha) = Content(seed, version);
        var response = await client.PostAsync(Releases, Upload(bytes, sha, version));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        return Guid.Parse(System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("releaseId").GetString()!);
    }

    // ------------------------------------------------------------- lifecycle

    [Fact]
    public async Task Upload_publish_download_round_trip()
    {
        using var client = await AdminAsync();
        var (bytes, sha) = Content("roundtrip", "9.0.1");

        var create = await client.PostAsync(Releases, Upload(bytes, sha, "9.0.1", "First release"));
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var releaseId = Guid.Parse(System.Text.Json.JsonDocument.Parse(
            await create.Content.ReadAsStringAsync()).RootElement.GetProperty("releaseId").GetString()!);

        // A draft IS downloadable by an administrator: fetching a build to install
        // by hand is a different act from letting the platform push it to machines.
        // What publishing changes is device eligibility, which AgentUpdateContentTests
        // covers on the agent's own content route.
        var draftDownload = await client.GetAsync(new Uri($"/admin/v1/agent-releases/{releaseId}/download", UriKind.Relative));
        draftDownload.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PublishAsync(client, releaseId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var download = await client.GetAsync(new Uri($"/admin/v1/agent-releases/{releaseId}/download", UriKind.Relative));
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        var downloaded = await download.Content.ReadAsByteArrayAsync();
        Convert.ToHexStringLower(SHA256.HashData(downloaded)).ShouldBe(sha);
    }

    [Fact]
    public async Task An_upload_whose_bytes_do_not_match_the_declared_hash_is_refused()
    {
        using var client = await AdminAsync();
        var (bytes, _) = Content("mismatch", "9.0.2");
        var wrongSha = new string('b', 64);

        var response = await client.PostAsync(Releases, Upload(bytes, wrongSha, "9.0.2"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // And nothing was recorded: the lie discarded the upload entirely.
        await using var db = _fixture.CreateDbContext();
        (await db.AgentReleases.AnyAsync(r => r.Version == "9.0.2")).ShouldBeFalse();
    }

    [Fact]
    public async Task Duplicate_versions_are_refused()
    {
        using var client = await AdminAsync();
        await CreateDraftAsync(client, "9.0.3", "dup-a");
        var (bytes, sha) = Content("dup-b", "9.0.3");

        var response = await client.PostAsync(Releases, Upload(bytes, sha, "9.0.3"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_revoked_release_is_not_downloadable_and_cannot_be_republished()
    {
        using var client = await AdminAsync();
        var releaseId = await CreateDraftAsync(client, "9.0.4", "revoke");
        await PublishAsync(client, releaseId);

        // Revoke deliberately takes no step-up body: withdrawing is the safe direction.
        (await client.PostAsync(new Uri($"/admin/v1/agent-releases/{releaseId}/revoke", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.GetAsync(new Uri($"/admin/v1/agent-releases/{releaseId}/download", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PublishAsync(client, releaseId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Latest_returns_the_numerically_newest_published_release()
    {
        using var client = await AdminAsync();
        // 99.0.10 vs 99.0.9: string ordering would pick 99.0.9. Versions sit at the
        // numeric top so releases published by sibling tests cannot outrank them.
        var older = await CreateDraftAsync(client, "99.0.9", "latest-old");
        var newer = await CreateDraftAsync(client, "99.0.10", "latest-new");
        await PublishAsync(client, older);
        await PublishAsync(client, newer);

        var latest = await client.GetStringAsync(new Uri("/admin/v1/agent-releases/latest", UriKind.Relative));

        System.Text.Json.JsonDocument.Parse(latest).RootElement
            .GetProperty("version").GetString().ShouldBe("99.0.10");
    }

    [Fact]
    public async Task The_release_created_published_and_downloaded_are_audited()
    {
        using var client = await AdminAsync();
        var releaseId = await CreateDraftAsync(client, "9.0.5", "audit");
        await PublishAsync(client, releaseId);
        await client.GetAsync(new Uri($"/admin/v1/agent-releases/{releaseId}/download", UriKind.Relative));

        await using var db = _fixture.CreateDbContext();
        var actions = await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.TargetId == releaseId.ToString())
            .Select(e => e.Action)
            .ToListAsync();

        actions.ShouldContain("agent_release.created");
        actions.ShouldContain("agent_release.published");
        actions.ShouldContain("agent_release.downloaded");
    }

    // ------------------------------------------------------------------ rbac

    [Fact]
    public async Task Helpdesk_cannot_create_or_publish_releases()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var client = _fixture.CreateClientFor(token);
        var (bytes, sha) = Content("helpdesk", "9.0.6");

        (await client.PostAsync(Releases, Upload(bytes, sha, "9.0.6")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Auditor_can_list_but_not_mutate()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(Releases)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(new Uri($"/admin/v1/agent-releases/{Guid.CreateVersion7()}/publish", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unauthenticated_caller_gets_nothing()
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(Releases)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(new Uri("/admin/v1/agent-releases/latest", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------- update queueing

    private async Task<Guid> SeedDeviceAsync(string hostname, string agentVersion)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var token = new EnrollmentToken(
            organizationId, $"agent-release-test-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "agent-release-test",
            DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    [Fact]
    public async Task Queueing_an_update_to_a_newer_published_release_creates_an_UpdateAgent_task()
    {
        using var client = await AdminAsync();
        var releaseId = await CreateDraftAsync(client, "9.1.0", "queue-ok");
        await PublishAsync(client, releaseId);
        var deviceId = await SeedDeviceAsync("UPD-OK", "1.0.0");

        var response = await client.PostAsync(
            new Uri($"/admin/v1/devices/{deviceId}/actions/update-agent", UriKind.Relative),
            ReleaseStepUpTestSupport.UpdateAgentBody(releaseId));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.AsNoTracking()
            .SingleAsync(t => t.DeviceId == deviceId);
        task.Type.ShouldBe(Domain.Tasks.DeviceTaskType.UpdateAgent);
        task.PayloadJson.ShouldNotBeNull();
        task.PayloadJson!.ShouldContain("9.1.0");
    }

    [Fact]
    public async Task A_downgrade_or_same_version_update_is_refused()
    {
        using var client = await AdminAsync();
        var releaseId = await CreateDraftAsync(client, "9.2.0", "queue-downgrade");
        await PublishAsync(client, releaseId);

        // Device already ahead of the release.
        var ahead = await SeedDeviceAsync("UPD-AHEAD", "9.3.0");
        (await client.PostAsync(
                new Uri($"/admin/v1/devices/{ahead}/actions/update-agent", UriKind.Relative),
                ReleaseStepUpTestSupport.UpdateAgentBody(releaseId)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Device already exactly on the release.
        var same = await SeedDeviceAsync("UPD-SAME", "9.2.0");
        (await client.PostAsync(
                new Uri($"/admin/v1/devices/{same}/actions/update-agent", UriKind.Relative),
                ReleaseStepUpTestSupport.UpdateAgentBody(releaseId)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_unpublished_release_cannot_be_queued()
    {
        using var client = await AdminAsync();
        var draft = await CreateDraftAsync(client, "9.4.0", "queue-draft");
        var deviceId = await SeedDeviceAsync("UPD-DRAFT", "1.0.0");

        (await client.PostAsync(
                new Uri($"/admin/v1/devices/{deviceId}/actions/update-agent", UriKind.Relative),
                ReleaseStepUpTestSupport.UpdateAgentBody(draft)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Helpdesk_cannot_queue_an_agent_update()
    {
        using var admin = await AdminAsync();
        var releaseId = await CreateDraftAsync(admin, "9.5.0", "queue-helpdesk");
        await PublishAsync(admin, releaseId);
        var deviceId = await SeedDeviceAsync("UPD-HD", "1.0.0");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var helpdesk = _fixture.CreateClientFor(token);

        (await helpdesk.PostAsync(
                new Uri($"/admin/v1/devices/{deviceId}/actions/update-agent", UriKind.Relative),
                ReleaseStepUpTestSupport.UpdateAgentBody(releaseId)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
