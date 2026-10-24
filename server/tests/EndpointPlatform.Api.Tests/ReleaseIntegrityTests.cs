using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Agents;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Agents;
using EndpointPlatform.Infrastructure.Software;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// A release row and its artifact must be one thing: the version the row
/// declares is the ProductVersion inside the package, and one package is one
/// release.
/// </summary>
/// <remarks>
/// <para>
/// Written against what actually happened. Release 1.5.1 was registered and
/// published with the exact bytes of 1.5.0; the row said one version, the
/// package said another, and the platform compared nothing. It was revoked, so
/// the fleet was not looped -- but nothing had prevented it. These tests hold
/// the two checks that now do, at the API, the way an administrator or an
/// attacker reaches them.
/// </para>
/// <para>
/// History is not rewritten. The production 1.5.0/1.5.1 pair is reproduced here
/// as seeded rows, and the assertions are that 1.5.0 keeps working and 1.5.1
/// stays exactly what it is: revoked, undownloadable, unpublishable, listed.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ReleaseIntegrityTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Releases = new("/admin/v1/agent-releases", UriKind.Relative);
    private static Uri Release(Guid id, string action) => new($"/admin/v1/agent-releases/{id}/{action}", UriKind.Relative);
    private static Uri UpdateAgent(Guid deviceId) => new($"/admin/v1/devices/{deviceId}/actions/update-agent", UriKind.Relative);

    /// <summary>
    /// A fresh deploy-capable administrator per test: publish, replace and
    /// update-agent are step-up gated behind a shared per-user rate limit, so a
    /// fixture account reused across tests would exhaust the window. Tests that
    /// assert the audit actor destructure the tuple for the minted email.
    /// </summary>
    private Task<(HttpClient Client, string Email)> AdminWithEmailAsync() =>
        _fixture.MintDeployAdminAsync("integrity");

    private async Task<HttpClient> AdminAsync() => (await AdminWithEmailAsync()).Client;

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid id) =>
        client.PostAsync(Release(id, "publish"), ReleaseStepUpTestSupport.PublishBody());

    private static string Seed(string tag) => $"{tag}-{Guid.CreateVersion7():N}";

    private static MultipartFormDataContent Form(byte[] bytes, string version)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { file, "file", $"EndpointPlatformAgent-{version}-x64.msi" },
            { new StringContent(version), "version" },
        };
    }

    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();
        return Guid.Parse(JsonDocument.Parse(body).RootElement.GetProperty("releaseId").GetString()!);
    }

    private async Task<AgentRelease> RowAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<bool> ExistsAsync(string version)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgentReleases.AnyAsync(r => r.Version == version);
    }

    private string ContentDirectory =>
        _fixture.Factory.Services.GetRequiredService<IOptions<PackageStorageOptions>>().Value.Directory;

    /// <summary>
    /// A release row written directly, as history was: bytes in the store, row in
    /// the table, no gate consulted. This is how rows that predate the check --
    /// and the 1.5.0/1.5.1 pair in particular -- are reproduced.
    /// </summary>
    private async Task<Guid> SeedHistoricalAsync(string version, byte[] bytes, AgentReleaseStatus status)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(ContentDirectory, sha256 + ".bin"), bytes);

        await using var db = _fixture.CreateDbContext();
        var release = new AgentRelease(
            version, "windows", "x64", $"EndpointPlatformAgent-{version}-x64.msi",
            sha256, null, null, bytes.LongLength, Guid.CreateVersion7(), "history@test.local");

        var now = DateTimeOffset.UtcNow;
        if (status != AgentReleaseStatus.Draft)
        {
            release.Publish(now);
        }

        if (status == AgentReleaseStatus.Revoked)
        {
            release.Revoke(now);
        }

        db.AgentReleases.Add(release);
        await db.SaveChangesAsync();
        return release.Id;
    }

    private async Task<Guid> SeedDeviceAsync(string agentVersion)
    {
        await using var db = _fixture.CreateDbContext();
        var orgId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        var token = new EnrollmentToken(
            orgId, $"integrity-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            Guid.CreateVersion7(), "integrity-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(
            orgId, "INTEG-PC", $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<List<Domain.Auditing.AuditLogEntry>> RefusalsAsync(string action, string targetDisplay)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.Action == action && e.Result == AuditResult.Failure && e.TargetDisplay == targetDisplay)
            .ToListAsync();
    }

    // ---- A: agreement is accepted ------------------------------------------------

    [Fact]
    public async Task A_release_whose_declared_version_is_the_msi_product_version_registers_and_publishes()
    {
        using var client = await AdminAsync();
        var msi = TestArtifacts.UnsignedMsi(Seed("6.1.0"), "6.1.0");

        var id = await IdOf(await client.PostAsync(Releases, Form(msi, "6.1.0")));

        (await PublishAsync(client, id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Published);
    }

    // ---- B and C: disagreement is refused at registration -------------------------

    [Fact]
    public async Task Declaring_a_newer_version_than_the_msi_carries_is_refused_and_names_both()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.2.0"), "6.2.0");

        var response = await client.PostAsync(Releases, Form(package, "6.2.1"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Declared release: 6.2.1");
        body.ShouldContain("MSI ProductVersion: 6.2.0");
        (await ExistsAsync("6.2.1")).ShouldBeFalse("a refused upload records nothing");
    }

    [Fact]
    public async Task Declaring_an_older_version_than_the_msi_carries_is_refused()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.3.1"), "6.3.1");

        var response = await client.PostAsync(Releases, Form(package, "6.3.0"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("MSI ProductVersion: 6.3.1");
        (await ExistsAsync("6.3.0")).ShouldBeFalse();
    }

    /// <summary>The declared version is never quietly corrected to the package's.</summary>
    [Fact]
    public async Task A_refused_upload_does_not_register_the_msi_version_instead()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.4.0"), "6.4.0");

        (await client.PostAsync(Releases, Form(package, "6.4.9"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await ExistsAsync("6.4.9")).ShouldBeFalse();
        (await ExistsAsync("6.4.0")).ShouldBeFalse("the server does not rewrite what was asked for");
    }

    [Fact]
    public async Task A_refused_registration_is_audited_with_its_category()
    {
        var (client, email) = await AdminWithEmailAsync();
        using var _1 = client;
        var package = TestArtifacts.UnsignedMsi(Seed("6.5.0"), "6.5.0");

        (await client.PostAsync(Releases, Form(package, "6.5.1"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var refusals = await RefusalsAsync("agent_release.created", "windows/x64 6.5.1");
        var entry = refusals.ShouldHaveSingleItem();
        entry.ActorDisplay.ShouldBe(email);
        entry.FailureReason.ShouldStartWith("ProductVersionMismatch:");
        entry.FailureReason.ShouldContain("6.5.0");
    }

    // ---- D and E: nothing to compare -------------------------------------------------

    [Fact]
    public async Task A_compound_file_with_no_installer_database_is_refused()
    {
        using var client = await AdminAsync();

        var response = await client.PostAsync(Releases, Form(TestArtifacts.MsiWithoutDatabase(Seed("6.6.0")), "6.6.0"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("ProductVersion could not be read");
        (await ExistsAsync("6.6.0")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_package_with_no_product_version_is_refused()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.MsiWithProperties([("ProductName", "Versionless")], Seed("6.7.0"));

        var response = await client.PostAsync(Releases, Form(package, "6.7.0"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("declares no ProductVersion");
        (await ExistsAsync("6.7.0")).ShouldBeFalse();
    }

    // ---- F: the gate re-reads at publish -----------------------------------------------

    /// <summary>
    /// A draft registered as 6.8.0 whose stored bytes are then swapped for a
    /// 6.8.1 package with the row's hash left as it was: the hash check catches
    /// it first, which is right -- the bytes are simply not the bytes.
    /// </summary>
    [Fact]
    public async Task A_stored_artifact_swapped_for_another_version_cannot_be_published()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("6.8.0"), "6.8.0"), "6.8.0")));
        var row = await RowAsync(id);

        await File.WriteAllBytesAsync(
            Path.Combine(ContentDirectory, row.Sha256 + ".bin"),
            TestArtifacts.UnsignedMsi(Seed("6.8.1"), "6.8.1"));

        var response = await PublishAsync(client, id);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    /// <summary>
    /// A row that predates the check, holding a package of the wrong version under
    /// the correct hash. Nothing but the version is wrong, and publish refuses it.
    /// </summary>
    [Fact]
    public async Task A_historical_draft_whose_package_disagrees_with_its_row_cannot_be_published()
    {
        using var client = await AdminAsync();
        var id = await SeedHistoricalAsync("6.9.1", TestArtifacts.UnsignedMsi(Seed("6.9.0"), "6.9.0"), AgentReleaseStatus.Draft);

        var response = await PublishAsync(client, id);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Declared release: 6.9.1");
        body.ShouldContain("MSI ProductVersion: 6.9.0");
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    // ---- H: one package is one release -----------------------------------------------

    [Fact]
    public async Task The_same_bytes_cannot_be_registered_under_a_second_version()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.10.0"), "6.10.0");
        await IdOf(await client.PostAsync(Releases, Form(package, "6.10.0")));

        var response = await client.PostAsync(Releases, Form(package, "6.10.1"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("already belongs to another release");
        body.ShouldContain("6.10.0");
        (await ExistsAsync("6.10.1")).ShouldBeFalse();

        var entry = (await RefusalsAsync("agent_release.created", "windows/x64 6.10.1")).ShouldHaveSingleItem();
        entry.FailureReason.ShouldStartWith("DuplicateArtifact:");
    }

    /// <summary>
    /// The production shape, going forward: a draft that somehow holds a published
    /// release's bytes. The duplicate check at publish is the second line, for rows
    /// that never passed the first.
    /// </summary>
    [Fact]
    public async Task A_draft_holding_another_releases_bytes_cannot_be_published()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.11.0"), "6.11.0");
        var published = await SeedHistoricalAsync("6.11.0", package, AgentReleaseStatus.Published);
        var draft = await SeedHistoricalAsync("6.11.1", package, AgentReleaseStatus.Draft);

        var response = await PublishAsync(client, draft);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        // Both checks would refuse this; the version check speaks first. Either
        // way the draft stays a draft.
        (body.Contains("Declared release: 6.11.1") || body.Contains("already belongs to another release")).ShouldBeTrue(body);
        (await RowAsync(draft)).Status.ShouldBe(AgentReleaseStatus.Draft);
        (await RowAsync(published)).Status.ShouldBe(AgentReleaseStatus.Published, "the legitimate owner is untouched");
    }

    /// <summary>
    /// The publish-time duplicate check on its own, which needs a case the version
    /// check lets through: a historical row mislabelled as 6.13.9 that owns the
    /// 6.13.0 package, and a draft correctly labelled 6.13.0 holding the same
    /// bytes. The draft's version agrees with its package -- and it still cannot
    /// publish, because those bytes are already a release. History is not
    /// silently "fixed" by publishing over it.
    /// </summary>
    [Fact]
    public async Task A_draft_cannot_publish_bytes_a_mislabelled_historical_release_already_owns()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.13.0"), "6.13.0");
        var mislabelled = await SeedHistoricalAsync("6.13.9", package, AgentReleaseStatus.Revoked);
        var draft = await SeedHistoricalAsync("6.13.0", package, AgentReleaseStatus.Draft);

        var response = await PublishAsync(client, draft);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("already belongs to another release");
        body.ShouldContain("6.13.9 (Revoked)");
        (await RowAsync(draft)).Status.ShouldBe(AgentReleaseStatus.Draft);
        (await RowAsync(mislabelled)).Status.ShouldBe(AgentReleaseStatus.Revoked);

        var entry = (await RefusalsAsync("agent_release.published", "windows/x64 6.13.0")).ShouldHaveSingleItem();
        entry.FailureReason.ShouldStartWith("DuplicateArtifact:");
    }

    /// <summary>A revoked release still owns its bytes: history is what the check protects.</summary>
    [Fact]
    public async Task Bytes_of_a_revoked_release_cannot_be_registered_again()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.12.0"), "6.12.0");
        await SeedHistoricalAsync("6.12.0", package, AgentReleaseStatus.Revoked);

        var response = await client.PostAsync(Releases, Form(package, "6.12.5"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("6.12.0 (Revoked)");
    }

    // ---- I and J: history is preserved ---------------------------------------------

    /// <summary>1.5.0's shape: published before the check, bytes and row in agreement. Still fully usable.</summary>
    [Fact]
    public async Task An_existing_published_release_keeps_working()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.50.0"), "6.50.0");
        var id = await SeedHistoricalAsync("6.50.0", package, AgentReleaseStatus.Published);

        var listing = JsonDocument.Parse(await client.GetStringAsync(new Uri("/admin/v1/agent-releases/", UriKind.Relative)));
        listing.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == id.ToString())
            .GetProperty("status").GetString().ShouldBe("Published");

        var download = await client.GetAsync(Release(id, "download"));
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(package);

        var deviceId = await SeedDeviceAsync("1.0.0");
        (await client.PostAsync(UpdateAgent(deviceId), ReleaseStepUpTestSupport.UpdateAgentBody(id)))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted, "a published release is still a fleet target");
    }

    /// <summary>
    /// The production pair exactly: 6.50.0 Published and 6.50.1 Revoked over the
    /// same bytes. 6.50.1 stays revoked, undownloadable and unpublishable -- and
    /// stays listed, because it happened. 6.50.0 is unaffected by its neighbour.
    /// </summary>
    [Fact]
    public async Task A_revoked_duplicate_of_a_published_release_stays_historical_and_revoked()
    {
        using var client = await AdminAsync();
        var package = TestArtifacts.UnsignedMsi(Seed("6.51.0"), "6.51.0");
        var published = await SeedHistoricalAsync("6.51.0", package, AgentReleaseStatus.Published);
        var revoked = await SeedHistoricalAsync("6.51.1", package, AgentReleaseStatus.Revoked);

        (await PublishAsync(client, revoked))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict, "revoked is terminal; the gate is never even consulted");
        (await client.GetAsync(Release(revoked, "download"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var rows = JsonDocument.Parse(await client.GetStringAsync(new Uri("/admin/v1/agent-releases/", UriKind.Relative)))
            .RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("status").GetString());
        rows[revoked.ToString()].ShouldBe("Revoked");
        rows[published.ToString()].ShouldBe("Published");

        var revokedRow = await RowAsync(revoked);
        revokedRow.Status.ShouldBe(AgentReleaseStatus.Revoked);
        revokedRow.RevokedAt.ShouldNotBeNull();
        revokedRow.Sha256.ShouldBe((await RowAsync(published)).Sha256, "history is not rewritten");

        (await client.GetAsync(Release(published, "download"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- K and L: authorization and the refusal's footprint --------------------------

    [Fact]
    public async Task Helpdesk_cannot_publish_a_draft_and_the_draft_is_untouched()
    {
        using var admin = await AdminAsync();
        var id = await IdOf(await admin.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("6.60.0"), "6.60.0"), "6.60.0")));

        using var helpdesk = _fixture.CreateClientFor(await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail));

        (await helpdesk.PostAsync(Release(id, "publish"), null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    [Fact]
    public async Task A_refused_publish_leaves_the_draft_a_draft_and_records_the_refusal()
    {
        var (client, email) = await AdminWithEmailAsync();
        using var _1 = client;
        var id = await SeedHistoricalAsync("6.61.1", TestArtifacts.UnsignedMsi(Seed("6.61.0"), "6.61.0"), AgentReleaseStatus.Draft);
        var before = await RowAsync(id);

        (await PublishAsync(client, id)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var after = await RowAsync(id);
        after.Status.ShouldBe(AgentReleaseStatus.Draft);
        after.PublishedAt.ShouldBeNull();
        after.Sha256.ShouldBe(before.Sha256);
        after.SignerSubject.ShouldBe(before.SignerSubject);

        var entry = (await RefusalsAsync("agent_release.published", "windows/x64 6.61.1")).ShouldHaveSingleItem();
        entry.TargetId.ShouldBe(id.ToString());
        entry.ActorDisplay.ShouldBe(email);
        entry.FailureReason.ShouldStartWith("ProductVersionMismatch:");
        entry.FailureReason.ShouldNotContain("0x");
        entry.FailureReason.ShouldNotContain(".bin");

        await using var db = _fixture.CreateDbContext();
        (await db.AuditLogEntries.AnyAsync(e => e.TargetId == id.ToString() && e.Action == "agent_release.published" && e.Result == AuditResult.Success))
            .ShouldBeFalse("nothing records a publish that did not happen");
    }

    // ---- M: the Internal model is unchanged ----------------------------------------

    [Fact]
    public async Task A_valid_unsigned_msi_still_publishes_under_internal()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("6.70.0"), "6.70.0"), "6.70.0")));

        (await PublishAsync(client, id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var row = await RowAsync(id);
        row.Status.ShouldBe(AgentReleaseStatus.Published);
        row.SignerSubject.ShouldBeNull("Internal reads no signature");
    }

    /// <summary>Replacing a draft's artifact is held to the same rule.</summary>
    [Fact]
    public async Task A_replacement_artifact_of_the_wrong_version_is_refused_and_the_draft_is_untouched()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("6.71.0"), "6.71.0"), "6.71.0")));
        var before = await RowAsync(id);

        var response = await client.PutAsync(
            Release(id, "artifact"),
            Form(TestArtifacts.UnsignedMsi(Seed("6.71.9"), "6.71.9"), "6.71.0").WithStepUp());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("MSI ProductVersion: 6.71.9");
        var after = await RowAsync(id);
        after.Sha256.ShouldBe(before.Sha256);
        after.ContentSizeBytes.ShouldBe(before.ContentSizeBytes);
        after.Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    // ---- N: the real thing ------------------------------------------------------------

    /// <summary>
    /// The staged 1.7.0 package, through the real endpoints. Runs only when the
    /// package is supplied; skipped, and shown as skipped, otherwise.
    /// </summary>
    [RealAgentMsiFact]
    public async Task The_real_agent_package_registers_under_its_own_version_and_no_other()
    {
        var bytes = await File.ReadAllBytesAsync(RealAgentMsiFactAttribute.Path!);
        var product = MsiDatabase.TryReadProductVersion(bytes);
        product.IsFound.ShouldBeTrue(product.Outcome.ToString());
        var version = AgentVersionNumber.Normalize(product.Value!);

        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == RealAgentMsiFactAttribute.Agent170Sha256)
        {
            version.ShouldBe("1.7.0");
        }

        using var client = await AdminAsync();

        // The wrong version, refused, naming what the package really is.
        var wrong = AgentVersionNumber.TryParse(version, out var v) ? $"{v.Major}.{v.Minor}.{v.Build + 1}" : "0.0.1";
        var refused = await client.PostAsync(Releases, Form(bytes, wrong));
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).ShouldContain($"MSI ProductVersion: {version}");

        // Its own version: registered, and published under Internal.
        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, version)));
        (await PublishAsync(client, id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var row = await RowAsync(id);
        row.Status.ShouldBe(AgentReleaseStatus.Published);
        row.Version.ShouldBe(version);
        row.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
