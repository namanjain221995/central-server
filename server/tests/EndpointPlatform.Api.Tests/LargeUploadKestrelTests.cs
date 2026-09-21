using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Infrastructure.Tests.Agents;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Uploads above Kestrel's default 30,000,000-byte request-body cap, over a
/// REAL Kestrel socket.
/// </summary>
/// <remarks>
/// This suite exists because the in-process test server enforces no body-size
/// limit at all: every upload test passed while the very first real agent MSI
/// (29.4 MiB) was refused with 413 in production. Only a genuine Kestrel
/// listener can regress-test this class of bug — and the last test proves the
/// raise is endpoint-local, not a blanket weakening: everything else still
/// refuses oversized bodies with 413.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class LargeUploadKestrelTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    /// <summary>Comfortably past the 30,000,000-byte default; far below both app ceilings.</summary>
    private const int OversizedBytes = 35 * 1024 * 1024;

    private async Task<HttpClient> KestrelAdminAsync()
    {
        var factory = await _fixture.GetKestrelFactoryAsync();
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(3);

        // Through the fixture rather than a login call written out here: sign-in is
        // two steps now that multi-factor is mandatory, and an inline password POST
        // receives a challenge with no sessionToken in it. The token is a Bearer
        // credential against the shared database, so one obtained through the
        // in-process host authenticates against this Kestrel host just as well.
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);

        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    private static (byte[] Bytes, string Sha256) OversizedContent(byte seed)
    {
        // Patterned, not random: fast to build, and content identity is the hash.
        var bytes = new byte[OversizedBytes];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i % 251));
        }

        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static MultipartFormDataContent Multipart(byte[] bytes, params (string Key, string Value)[] fields)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent { { file, "file", "oversized-test.msi" } };
        foreach (var (key, value) in fields)
        {
            form.Add(new StringContent(value), key);
        }

        return form;
    }

    [Fact]
    public async Task An_agent_release_larger_than_the_kestrel_default_uploads_and_round_trips()
    {
        using var client = await KestrelAdminAsync();

        // A real package shape, not patterned bytes: registration now reads the
        // ProductVersion out of the upload, so what goes over the socket must be
        // an MSI that says 98.0.1 -- written the way Windows Installer writes a
        // large one, in 4 KB sectors. 98.x versions are unique, and never the
        // numeric "latest" other tests assert on.
        var bytes = TestArtifacts.OversizedMsi(OversizedBytes, seed: 1, productVersion: "98.0.1");
        bytes.Length.ShouldBeGreaterThan(OversizedBytes, "the point is to exceed the Kestrel default");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var create = await client.PostAsync(
            new Uri("/admin/v1/agent-releases/", UriKind.Relative),
            Multipart(bytes, ("version", "98.0.1"), ("sha256", sha)));

        create.StatusCode.ShouldBe(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());

        // And the platform serves back exactly the bytes it accepted.
        var releaseId = (await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("releaseId").GetString()!;
        // Not published: this test is about the request-body limit and the byte
        // round-trip, and a draft is downloadable by an administrator.
        var downloaded = await client.GetByteArrayAsync(
            new Uri($"/admin/v1/agent-releases/{releaseId}/download", UriKind.Relative));
        Convert.ToHexStringLower(SHA256.HashData(downloaded)).ShouldBe(sha);
    }

    [Fact]
    public async Task A_software_package_larger_than_the_kestrel_default_uploads()
    {
        using var client = await KestrelAdminAsync();
        var (bytes, sha) = OversizedContent(seed: 2);

        var create = await client.PostAsync(
            new Uri("/admin/v1/packages/", UriKind.Relative),
            Multipart(bytes,
                ("name", "Oversized Upload Test"),
                ("version", "98.0.2"),
                ("sha256", sha),
                ("msiProductCode", $"{{{Guid.CreateVersion7().ToString().ToUpperInvariant()}}}")));

        // Anything but 413 proves Kestrel let the body through; Created proves
        // the whole path held. 413 is the regression this test exists to catch.
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Every_other_endpoint_still_refuses_oversized_bodies()
    {
        using var client = await KestrelAdminAsync();

        // An oversized body aimed at a non-upload endpoint: the login route reads
        // JSON and keeps the 30 MB default, so Kestrel must refuse it. Guards
        // against the limit raise ever becoming global by accident.
        using var oversized = new ByteArrayContent(new byte[OversizedBytes]);
        oversized.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Kestrel refuses an oversized body in one of two ways, and which one the
        // client observes is a race it does not control: if the response is written
        // before the client finishes sending, the status is readable as 413; if the
        // connection is torn down first, the write fails with a reset instead. Both
        // are the refusal this test exists to prove, so both are accepted -- but a
        // reset is only accepted together with the liveness check below, so a server
        // that crashed or stopped listening cannot pass as an enforcement.
        HttpStatusCode? status = null;
        try
        {
            status = (await client.PostAsync(
                new Uri("/admin/v1/auth/login", UriKind.Relative), oversized)).StatusCode;
        }
        catch (HttpRequestException)
        {
            // Connection reset mid-body.
        }

        if (status is not null)
        {
            status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
            return;
        }

        // The refusal was a reset. Prove the endpoint is still alive and that it was
        // the size that was rejected: an ordinary small body still reaches handling.
        using var small = JsonContent.Create(new { email = "nobody@test.local", password = "wrong" });

        var after = await client.PostAsync(new Uri("/admin/v1/auth/login", UriKind.Relative), small);

        after.StatusCode.ShouldNotBe(HttpStatusCode.RequestEntityTooLarge);
    }
}
