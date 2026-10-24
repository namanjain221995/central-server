using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Agents;
using EndpointPlatform.Infrastructure.Software;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The server-side publish gate under the Internal trust model, called the way an
/// attacker would call it: over HTTP, with no dashboard in between.
/// </summary>
/// <remarks>
/// <para>
/// Internal is Techsara's deployment: one company, a private network, controlled
/// machines. Integrity is the server-computed SHA-256 -- re-checked over the bytes
/// on disk at publish and by the agent at install -- under authorization, audit
/// and HTTPS. No CA-issued Authenticode signature is required, and none is read.
/// </para>
/// <para>
/// So the gate has three properties, each held at the API. The hash is the
/// server's and nothing the client says can change it. The bytes on disk must
/// still match that hash at publish, so a build tampered with after upload is
/// refused. And a signature, present or absent, makes no difference at all --
/// which is asserted in both directions, because "not required" must not
/// quietly mean "still checked". Public-mode behaviour is covered at the
/// verifier level in <c>ReleasePublishVerifierTests</c> and
/// <c>AuthenticodeVerifierTests</c>.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class PublishGateTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Releases = new("/admin/v1/agent-releases", UriKind.Relative);
    private static Uri Release(Guid id, string action) => new($"/admin/v1/agent-releases/{id}/{action}", UriKind.Relative);

    /// <summary>
    /// A fresh deploy-capable administrator per test. Publish and artifact
    /// replacement are step-up gated behind a shared per-user rate limit (five
    /// actions per fifteen-minute window), so a fixture account reused across
    /// tests would exhaust the budget; see <see cref="ReleaseStepUpTestSupport"/>.
    /// </summary>
    private Task<(HttpClient Client, string Email)> AdminWithEmailAsync() =>
        _fixture.MintDeployAdminAsync("gate");

    private async Task<HttpClient> AdminAsync() => (await AdminWithEmailAsync()).Client;

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid id) =>
        client.PostAsync(Release(id, "publish"), ReleaseStepUpTestSupport.PublishBody());

    private static string Seed(string version) => $"{version}-{Guid.CreateVersion7():N}";

    private static MultipartFormDataContent Form(byte[] bytes, string version, string? sha256 = null, string? signer = null)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent
        {
            { file, "file", $"EndpointPlatformAgent-{version}-x64.msi" },
            { new StringContent(version), "version" },
        };
        if (sha256 is not null) form.Add(new StringContent(sha256), "sha256");
        if (signer is not null) form.Add(new StringContent(signer), "signerSubject");
        return form;
    }

    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        return Guid.Parse(JsonDocument.Parse(body).RootElement.GetProperty("releaseId").GetString()!);
    }

    private async Task<AgentRelease> RowAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    // ---- SHA-256 is the server's ---------------------------------------------

    /// <summary>The stored hash is computed over the stored bytes, whatever was declared.</summary>
    [Fact]
    public async Task The_recorded_hash_is_computed_by_the_server()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.60.0"), productVersion: "7.60.0");

        // No hash declared at all.
        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.60.0")));

        (await RowAsync(id)).Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>A declared hash that disagrees with the bytes is a damaged upload, refused.</summary>
    [Fact]
    public async Task A_client_cannot_override_the_hash_with_a_wrong_declaration()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.61.0"), productVersion: "7.61.0");

        var response = await client.PostAsync(Releases, Form(bytes, "7.61.0", sha256: new string('c', 64)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var db = _fixture.CreateDbContext();
        (await db.AgentReleases.AnyAsync(r => r.Version == "7.61.0")).ShouldBeFalse("nothing is recorded from a refused upload");
    }

    /// <summary>A declared hash that agrees is accepted -- and still not what is stored; the server's is.</summary>
    [Fact]
    public async Task A_correct_declared_hash_is_accepted_as_a_cross_check()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.62.0"), productVersion: "7.62.0");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.62.0", sha256: sha.ToUpperInvariant())));

        (await RowAsync(id)).Sha256.ShouldBe(sha, "stored in the server's canonical lower-case form");
    }

    // ---- signer metadata is never the client's -------------------------------

    /// <summary>
    /// A signer typed into the upload form is discarded. Under Internal the
    /// artifact's own signature is not read either, so the recorded signer is null
    /// whether or not the build happens to be signed. Nothing about who signed a
    /// build is ever client-controlled metadata.
    /// </summary>
    [Fact]
    public async Task A_typed_signer_is_ignored_and_no_signer_is_recorded_under_internal()
    {
        using var client = await AdminAsync();

        var unsigned = await IdOf(await client.PostAsync(
            Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.63.0"), "7.63.0"), "7.63.0", signer: "CN=Whoever I Say")));
        (await RowAsync(unsigned)).SignerSubject.ShouldBeNull();

        var signed = await IdOf(await client.PostAsync(
            Releases, Form(TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.64.0"), productVersion: "7.64.0"), "7.64.0", signer: "CN=Whoever I Say")));
        (await RowAsync(signed)).SignerSubject.ShouldBeNull("Internal does not read the signature, so it records no signer");
    }

    // ---- the gate -------------------------------------------------------------

    /// <summary>The policy says what it requires, so the console does not have to guess.</summary>
    [Fact]
    public async Task The_policy_endpoint_reports_internal()
    {
        using var client = await AdminAsync();

        var body = await client.GetStringAsync(new Uri("/admin/v1/agent-releases/policy", UriKind.Relative));

        JsonDocument.Parse(body).RootElement.GetProperty("trustMode").GetString().ShouldBe("Internal");
    }

    /// <summary>Acceptance criterion 1: an unsigned MSI publishes under Internal.</summary>
    [Fact]
    public async Task An_unsigned_artifact_publishes_under_internal()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.65.0"), "7.65.0"), "7.65.0")));

        var response = await PublishAsync(client, id);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var row = await RowAsync(id);
        row.Status.ShouldBe(AgentReleaseStatus.Published);
        row.SignerSubject.ShouldBeNull();
    }

    /// <summary>
    /// A signature -- even a wrong one -- is irrelevant under Internal. Asserted so
    /// that "not required" can never quietly regress into "still checked".
    /// </summary>
    [Fact]
    public async Task A_signature_makes_no_difference_under_internal()
    {
        using var client = await AdminAsync();
        using var impostor = TestArtifacts.IssueLeaf(AdminApiPostgresFixture.SigningAuthority, "CN=Not Techsara Ltd");
        var bytes = TestArtifacts.SignedMsi(impostor, AdminApiPostgresFixture.SigningAuthority.Root, Seed("7.66.0"), productVersion: "7.66.0");

        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.66.0")));

        (await PublishAsync(client, id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RowAsync(id)).SignerSubject.ShouldBeNull();
    }

    /// <summary>
    /// Bytes that are not a Windows Installer package are refused in every mode --
    /// at registration now, so no such draft can exist, and at publish still, for
    /// bytes that stopped being an MSI after they were stored.
    /// </summary>
    [Fact]
    public async Task An_artifact_that_is_not_an_msi_cannot_be_registered_or_published()
    {
        using var client = await AdminAsync();
        var exe = System.Text.Encoding.ASCII.GetBytes("MZ definitely-an-exe " + Seed("7.67.0") + new string('x', 2048));

        var refused = await client.PostAsync(Releases, Form(exe, "7.67.0"));
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("Windows Installer");
        await using (var db = _fixture.CreateDbContext())
        {
            (await db.AgentReleases.AnyAsync(r => r.Version == "7.67.0")).ShouldBeFalse("nothing is recorded from a refused upload");
        }

        // A draft that was an MSI when stored, overwritten on disk with something
        // that is not: the publish gate re-reads the bytes and refuses.
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.67.1"), "7.67.1"), "7.67.1")));
        var row = await RowAsync(id);
        var directory = _fixture.Factory.Services.GetRequiredService<IOptions<PackageStorageOptions>>().Value.Directory;
        await File.WriteAllBytesAsync(Path.Combine(directory, row.Sha256 + ".bin"), exe);

        var response = await PublishAsync(client, id);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Windows Installer");
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    /// <summary>
    /// The stored bytes are re-hashed at publish time. A build that was valid at
    /// upload and has since been altered on disk is refused, whatever its row says.
    /// </summary>
    [Fact]
    public async Task A_stored_artifact_modified_after_upload_cannot_be_published()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.68.0"), productVersion: "7.68.0");
        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.68.0")));
        var row = await RowAsync(id);

        // Flip one byte of the stored blob, in place, under its content address.
        var directory = _fixture.Factory.Services.GetRequiredService<IOptions<PackageStorageOptions>>().Value.Directory;
        var path = Path.Combine(directory, row.Sha256 + ".bin");
        File.Exists(path).ShouldBeTrue("the artifact is stored under its hash");
        var stored = await File.ReadAllBytesAsync(path);
        stored[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, stored);

        var response = await PublishAsync(client, id);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    /// <summary>Publishing is audited, with the actor, in every mode.</summary>
    [Fact]
    public async Task Publishing_is_audited()
    {
        var (client, email) = await AdminWithEmailAsync();
        using var _1 = client;
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.69.0"), "7.69.0"), "7.69.0")));

        (await PublishAsync(client, id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var db = _fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.TargetId == id.ToString() && e.Action == "agent_release.published")
            .SingleAsync();
        entry.ActorDisplay.ShouldBe(email);
    }

    /// <summary>The refusal explains itself without leaking anything about the bytes.</summary>
    [Fact]
    public async Task A_refusal_names_the_requirement_not_the_bytes()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.UnsignedMsi(Seed("7.70.0"), "7.70.0");
        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.70.0")));
        var row = await RowAsync(id);

        var directory = _fixture.Factory.Services.GetRequiredService<IOptions<PackageStorageOptions>>().Value.Directory;
        var path = Path.Combine(directory, row.Sha256 + ".bin");
        var stored = await File.ReadAllBytesAsync(path);
        stored[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, stored);

        var body = await (await PublishAsync(client, id)).Content.ReadAsStringAsync();

        body.ShouldContain("SHA-256");
        body.Contains("Authenticode", StringComparison.Ordinal)
            .ShouldBeFalse("Internal refusals never mention a requirement Internal does not have");
        body.ShouldNotContain("0x");
        body.ShouldNotContain("Exception");
    }

    // ---- replacing a draft's artifact ---------------------------------------

    /// <summary>
    /// The path by which an unsigned draft becomes its signed self: same row, new
    /// bytes, hash recomputed by the server over the new bytes.
    /// </summary>
    [Fact]
    public async Task A_drafts_artifact_can_be_replaced_and_the_hash_follows_the_new_bytes()
    {
        using var client = await AdminAsync();
        var unsigned = TestArtifacts.UnsignedMsi(Seed("7.71.0"), "7.71.0");
        var id = await IdOf(await client.PostAsync(Releases, Form(unsigned, "7.71.0")));
        var before = await RowAsync(id);
        before.SignerSubject.ShouldBeNull();

        var signed = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.71.0-signed"), productVersion: "7.71.0");
        var replace = await client.PutAsync(Release(id, "artifact"), Form(signed, "7.71.0").WithStepUp());
        replace.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await RowAsync(id);
        after.Id.ShouldBe(id, "same release, not a second one");
        after.Version.ShouldBe("7.71.0");
        after.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(signed)));
        after.Sha256.ShouldNotBe(before.Sha256, "new bytes, so the hash must follow them");
        after.ContentSizeBytes.ShouldBe(signed.LongLength);
        after.SignerSubject.ShouldBeNull("Internal reads no signature");

        // And now it publishes.
        (await PublishAsync(client, id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>A published release is immutable; its artifact cannot be swapped.</summary>
    [Fact]
    public async Task A_published_releases_artifact_cannot_be_replaced()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(
            Releases, Form(TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.72.0"), productVersion: "7.72.0"), "7.72.0")));
        (await PublishAsync(client, id)).EnsureSuccessStatusCode();
        var published = await RowAsync(id);

        var response = await client.PutAsync(
            Release(id, "artifact"),
            Form(TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.72.0-other"), productVersion: "7.72.0"), "7.72.0").WithStepUp());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RowAsync(id)).Sha256.ShouldBe(published.Sha256, "the published bytes are frozen");
    }

    [Fact]
    public async Task Replacing_an_unknown_releases_artifact_is_not_found()
    {
        using var client = await AdminAsync();

        // Valid step-up fields, so the 404 is about the release and nothing else:
        // the unknown id is answered before any budget is consumed.
        (await client.PutAsync(
            Release(Guid.CreateVersion7(), "artifact"),
            Form(TestArtifacts.UnsignedMsi(Seed("7.73.0"), "7.73.0"), "7.73.0").WithStepUp()))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Replacement requires the same permission as publishing.</summary>
    [Fact]
    public async Task Helpdesk_cannot_replace_an_artifact()
    {
        using var admin = await AdminAsync();
        var id = await IdOf(await admin.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.74.0"), "7.74.0"), "7.74.0")));

        using var helpdesk = _fixture.CreateClientFor(await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail));

        (await helpdesk.PutAsync(Release(id, "artifact"), Form(TestArtifacts.UnsignedMsi(Seed("7.74.0-b"), "7.74.0"), "7.74.0")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
