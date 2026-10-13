using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The administrator's side of the Company Portal catalog over real HTTP:
/// which approved packages are offered, how they are presented, to whom, and
/// who may change any of that.
/// </summary>
/// <remarks>
/// Changing the catalog is pre-approving an install on every device that can
/// see the entry, so it is gated by <c>software.deploy</c>; reading it needs only
/// <c>software.view</c>. The fixture database is shared by every API test class,
/// so each test registers its own package and acts only on entries it created.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class CatalogEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _groups = new(fixture);

    private static readonly Uri Catalog = new("/admin/v1/catalog/", UriKind.Relative);
    private static readonly Uri Installs = new("/admin/v1/catalog/installs", UriKind.Relative);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static Uri App(Guid id) => new($"/admin/v1/catalog/{id}", UriKind.Relative);

    private static Uri Icon(Guid id) => new($"/admin/v1/catalog/{id}/icon", UriKind.Relative);

    private static Uri Publish(Guid id) => new($"/admin/v1/catalog/{id}/publish", UriKind.Relative);

    private static Uri Unpublish(Guid id) => new($"/admin/v1/catalog/{id}/unpublish", UriKind.Relative);

    private static Uri WithdrawPackage(Guid packageId) => new($"/admin/v1/packages/{packageId}/withdraw", UriKind.Relative);

    private static Uri RestorePackage(Guid packageId) => new($"/admin/v1/packages/{packageId}/restore", UriKind.Relative);

    private static string Unique(string prefix) => $"{prefix} {Guid.CreateVersion7():N}"[..Math.Min(40, prefix.Length + 33)];

    /// <summary>A tiny "PNG": the signature plus a few bytes. The domain checks only the signature.</summary>
    private static byte[] TinyPng() => [.. PngSignature, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private async Task<HttpClient> AdminAsync()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        return _fixture.CreateClientFor(token);
    }

    /// <summary>Registers a deployable package through the real upload endpoint.</summary>
    private static async Task<Guid> RegisterPackageAsync(HttpClient client, string name, string version = "1.0.0")
    {
        var bytes = Encoding.UTF8.GetBytes("msi-" + Guid.CreateVersion7().ToString("N") + new string('z', 300));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var form = new MultipartFormDataContent
        {
            { file, "file", "app.msi" },
            { new StringContent(name), "name" },
            { new StringContent(version), "version" },
            { new StringContent("Contoso"), "publisher" },
            { new StringContent(sha), "sha256" },
            { new StringContent($"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}"), "msiProductCode" },
            { new StringContent("CN=Contoso"), "requiredSignerSubject" },
        };

        var response = await client.PostAsync(new Uri("/admin/v1/packages/", UriKind.Relative), form);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, Guid packageId, string displayName, string visibility = "AllDevices",
        IReadOnlyCollection<Guid>? groupIds = null, bool? publish = null, string? description = null,
        string? category = null) =>
        client.PostAsJsonAsync(Catalog, new
        {
            packageId,
            displayName,
            description,
            category,
            visibility,
            groupIds,
            publish,
        });

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location.ToString().ShouldEndWith($"/admin/v1/catalog/{id}");
        return id;
    }

    private static async Task<JsonElement?> FindAsync(HttpClient client, Guid appId)
    {
        var response = await client.GetAsync(Catalog);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        list.ValueKind.ShouldBe(JsonValueKind.Array);

        foreach (var item in list.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == appId)
            {
                return item;
            }
        }

        return null;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid appId)
    {
        var found = await FindAsync(client, appId);
        found.HasValue.ShouldBeTrue($"catalog app {appId} should be listed");
        return found!.Value;
    }

    private static MultipartFormDataContent IconBody(byte[] bytes, string fileName = "icon.png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static IReadOnlyList<Guid> GroupIdsOf(JsonElement app) =>
        app.GetProperty("groupIds").EnumerateArray().Select(g => g.GetGuid()).ToList();

    // ------------------------------------------------------------ create

    [Fact]
    public async Task A_registered_package_can_be_offered_and_starts_unpublished_without_an_icon()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Create"), "2.3.4");

        var id = await CreatedIdAsync(await CreateAsync(
            client, packageId, "Zoom", description: "Video calls", category: "Communication"));

        var app = await GetAsync(client, id);
        app.GetProperty("packageId").GetGuid().ShouldBe(packageId);
        app.GetProperty("packageVersion").GetString().ShouldBe("2.3.4");
        app.GetProperty("packageWithdrawn").GetBoolean().ShouldBeFalse();
        app.GetProperty("displayName").GetString().ShouldBe("Zoom");
        app.GetProperty("description").GetString().ShouldBe("Video calls");
        app.GetProperty("category").GetString().ShouldBe("Communication");
        app.GetProperty("visibility").GetString().ShouldBe("AllDevices");
        GroupIdsOf(app).ShouldBeEmpty();
        app.GetProperty("isPublished").GetBoolean().ShouldBeFalse();
        app.GetProperty("hasIcon").GetBoolean().ShouldBeFalse();
        app.GetProperty("createdByDisplay").GetString().ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
        app.GetProperty("publishedByDisplay").GetString().ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
    }

    [Fact]
    public async Task An_entry_can_be_published_on_creation()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Create Published"));

        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Published At Birth", publish: true));

        (await GetAsync(client, id)).GetProperty("isPublished").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task The_same_package_cannot_be_offered_twice()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Duplicate"));
        await CreatedIdAsync(await CreateAsync(client, packageId, "First Tile"));

        var duplicate = await CreateAsync(client, packageId, "Second Tile");

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "two tiles installing the same bytes would only confuse the person choosing between them");
    }

    [Fact]
    public async Task An_unknown_package_cannot_be_offered()
    {
        using var client = await AdminAsync();

        var response = await CreateAsync(client, Guid.CreateVersion7(), "Ghost");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_missing_name_or_package_is_a_bad_request()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Invalid"));

        (await CreateAsync(client, packageId, "  ")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreateAsync(client, Guid.Empty, "No Package")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreateAsync(client, packageId, "Bad Visibility", visibility: "Everyone"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Selected_groups_needs_at_least_one_group_of_the_organization()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Groups Empty"));

        (await CreateAsync(client, packageId, "Nobody", visibility: "SelectedGroups"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreateAsync(client, packageId, "Nobody", visibility: "SelectedGroups", groupIds: []))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreateAsync(client, packageId, "Nobody", visibility: "SelectedGroups", groupIds: [Guid.CreateVersion7()]))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a group that is not the organization's is not a group");
    }

    [Fact]
    public async Task Selected_groups_are_echoed_back()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Groups"));
        var sales = await _groups.CreateGroupAsync(client, GroupTestSupport.UniqueName("Sales"));
        var engineering = await _groups.CreateGroupAsync(client, GroupTestSupport.UniqueName("Eng"));

        var id = await CreatedIdAsync(await CreateAsync(
            client, packageId, "Departmental", visibility: "SelectedGroups", groupIds: [sales, engineering, sales]));

        var app = await GetAsync(client, id);
        app.GetProperty("visibility").GetString().ShouldBe("SelectedGroups");
        GroupIdsOf(app).ShouldBe(new[] { sales, engineering }, ignoreOrder: true);
    }

    // ------------------------------------------------------------ update

    [Fact]
    public async Task Updating_changes_the_presentation_and_the_audience()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Update"));
        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Before"));
        var sales = await _groups.CreateGroupAsync(client, GroupTestSupport.UniqueName("Sales"));

        var update = await client.PutAsJsonAsync(App(id), new
        {
            packageId,
            displayName = "After",
            description = "Now with a description",
            category = "Tools",
            visibility = "SelectedGroups",
            groupIds = new[] { sales },
        });

        update.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var app = await GetAsync(client, id);
        app.GetProperty("displayName").GetString().ShouldBe("After");
        app.GetProperty("description").GetString().ShouldBe("Now with a description");
        app.GetProperty("category").GetString().ShouldBe("Tools");
        app.GetProperty("visibility").GetString().ShouldBe("SelectedGroups");
        GroupIdsOf(app).ShouldBe(new[] { sales });

        // Back to everyone drops the links.
        (await client.PutAsJsonAsync(App(id), new { packageId, displayName = "After", visibility = "AllDevices" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        app = await GetAsync(client, id);
        app.GetProperty("visibility").GetString().ShouldBe("AllDevices");
        GroupIdsOf(app).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_package_behind_an_entry_cannot_be_changed()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Swap A"));
        var otherPackageId = await RegisterPackageAsync(client, Unique("Catalog Swap B"));
        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Zoom"));

        var swap = await client.PutAsJsonAsync(App(id), new
        {
            packageId = otherPackageId,
            displayName = "Zoom",
            visibility = "AllDevices",
        });

        swap.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            "a tile called Zoom that starts installing something else is what a user cannot defend against");
        (await GetAsync(client, id)).GetProperty("packageId").GetGuid().ShouldBe(packageId);
    }

    [Fact]
    public async Task An_unknown_entry_is_not_found()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Unknown"));
        var missing = Guid.CreateVersion7();

        (await client.PutAsJsonAsync(App(missing), new { packageId, displayName = "X", visibility = "AllDevices" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(Publish(missing), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(Unpublish(missing), content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(Icon(missing))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PutAsync(Icon(missing), IconBody(TinyPng()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync(Icon(missing))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync(App(missing))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------- publish

    [Fact]
    public async Task Publishing_and_unpublishing_are_reflected()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Publish"));
        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Toggle"));

        (await client.PostAsync(Publish(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var app = await GetAsync(client, id);
        app.GetProperty("isPublished").GetBoolean().ShouldBeTrue();
        app.GetProperty("publishedByDisplay").GetString().ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);

        (await client.PostAsync(Unpublish(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync(client, id)).GetProperty("isPublished").GetBoolean().ShouldBeFalse();
    }

    /// <summary>
    /// Publishing is the approval an install rests on, and a withdrawn package
    /// is one nobody may install: the two cannot be true at once. Setting the
    /// entry up ahead of restoring the package is fine; publishing it is not.
    /// </summary>
    [Fact]
    public async Task Publishing_needs_a_package_that_is_not_withdrawn()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Withdrawn"));
        (await client.PostAsync(WithdrawPackage(packageId), content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Create-and-publish in one go is refused outright...
        var createPublished = await CreateAsync(client, packageId, "Withdrawn Tile", publish: true);
        createPublished.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await createPublished.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!
            .ShouldContain("withdrawn");

        // ...while an unpublished entry over the same package is allowed.
        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Withdrawn Tile"));
        var app = await GetAsync(client, id);
        app.GetProperty("packageWithdrawn").GetBoolean().ShouldBeTrue();
        app.GetProperty("isPublished").GetBoolean().ShouldBeFalse();

        var publish = await client.PostAsync(Publish(id), content: null);
        publish.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!
            .ShouldContain("withdrawn");
        (await GetAsync(client, id)).GetProperty("isPublished").GetBoolean().ShouldBeFalse();

        // Unpublishing never depends on the package: hiding is always allowed.
        (await client.PostAsync(Unpublish(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Restoring the package lifts the refusal.
        (await client.PostAsync(RestorePackage(packageId), content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync(Publish(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        app = await GetAsync(client, id);
        app.GetProperty("packageWithdrawn").GetBoolean().ShouldBeFalse();
        app.GetProperty("isPublished").GetBoolean().ShouldBeTrue();
    }

    // -------------------------------------------------------------- icon

    [Fact]
    public async Task An_icon_round_trips_and_can_be_removed()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Icon"));
        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Iconic"));
        var png = TinyPng();

        (await client.PutAsync(Icon(id), IconBody(png))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync(client, id)).GetProperty("hasIcon").GetBoolean().ShouldBeTrue();

        var get = await client.GetAsync(Icon(id));
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        get.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await get.Content.ReadAsByteArrayAsync()).ShouldBe(png);

        (await client.DeleteAsync(Icon(id))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(Icon(id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(client, id)).GetProperty("hasIcon").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Only_a_png_is_accepted_as_an_icon()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Icon Bad"));
        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Not Iconic"));

        // A JPEG header under a .png name: the bytes decide, not the name.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00 };
        (await client.PutAsync(Icon(id), IconBody(jpeg))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Oversized, with a valid signature: one byte over is caught after the
        // part is read, and a body far over the ceiling is refused on its
        // Content-Length before any of it is read. Both are the same 400.
        var huge = new byte[Domain.Software.CatalogApp.MaxIconBytes + 1];
        PngSignature.CopyTo(huge, 0);
        (await client.PutAsync(Icon(id), IconBody(huge))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var enormous = new byte[4 * Domain.Software.CatalogApp.MaxIconBytes];
        PngSignature.CopyTo(enormous, 0);
        (await client.PutAsync(Icon(id), IconBody(enormous))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Not a multipart upload at all.
        (await client.PutAsJsonAsync(Icon(id), new { file = "x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.GetAsync(Icon(id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(client, id)).GetProperty("hasIcon").GetBoolean().ShouldBeFalse();
    }

    // ------------------------------------------------------------ delete

    [Fact]
    public async Task Deleting_removes_the_entry_from_the_catalog()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, Unique("Catalog Delete"));
        var id = await CreatedIdAsync(await CreateAsync(client, packageId, "Short Lived"));

        (await client.DeleteAsync(App(id))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await FindAsync(client, id)).HasValue.ShouldBeFalse("a deleted entry must not be listed");
        (await client.DeleteAsync(App(id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The package itself is untouched: it can be offered again.
        await CreatedIdAsync(await CreateAsync(client, packageId, "Offered Again"));
    }

    // ---------------------------------------------------------- installs

    [Fact]
    public async Task The_request_history_is_a_page()
    {
        using var client = await AdminAsync();

        var response = await client.GetAsync(new Uri("/admin/v1/catalog/installs?page=1&pageSize=5", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);
        body.GetProperty("items").GetArrayLength().ShouldBeLessThanOrEqualTo(5);
        body.GetProperty("total").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        body.GetProperty("page").GetInt32().ShouldBe(1);
        body.GetProperty("pageSize").GetInt32().ShouldBe(5);

        // Out-of-range paging is clamped, not refused.
        var clamped = await client.GetAsync(new Uri("/admin/v1/catalog/installs?page=0&pageSize=9999", UriKind.Relative));
        clamped.StatusCode.ShouldBe(HttpStatusCode.OK);
        var clampedBody = await clamped.Content.ReadFromJsonAsync<JsonElement>();
        clampedBody.GetProperty("page").GetInt32().ShouldBe(1);
        clampedBody.GetProperty("pageSize").GetInt32().ShouldBe(200);
    }

    // -------------------------------------------------------------- rbac

    [Fact]
    public async Task Software_view_alone_can_read_the_catalog_but_not_change_it()
    {
        using var admin = await AdminAsync();
        var packageId = await RegisterPackageAsync(admin, Unique("Catalog Rbac"));
        var id = await CreatedIdAsync(await CreateAsync(admin, packageId, "Read Only"));

        var email = $"catalog-viewer-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

            var role = Role.CreateCustom(org.Id, $"catalog_viewer_{Guid.CreateVersion7():N}", "Catalog Viewer", "software.view only");
            var view = await db.Permissions.SingleAsync(p => p.Key == Permissions.Software.View);
            role.GrantPermission(view.Id);
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            var user = new PlatformUser(org.Id, email, "Catalog Viewer");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        using var viewer = _fixture.CreateClientFor(await _fixture.SignInAsync(email));

        (await viewer.GetAsync(Catalog)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync(Installs)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync(Icon(id))).StatusCode.ShouldBe(HttpStatusCode.NotFound, "readable, and there is no icon");

        (await CreateAsync(viewer, packageId, "Sneaky")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PutAsJsonAsync(App(id), new { packageId, displayName = "Renamed", visibility = "AllDevices" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync(Publish(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync(Unpublish(id), content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PutAsync(Icon(id), IconBody(TinyPng()))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync(Icon(id))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync(App(id))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Nothing changed.
        var app = await GetAsync(admin, id);
        app.GetProperty("displayName").GetString().ShouldBe("Read Only");
        app.GetProperty("isPublished").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_unauthenticated_caller_sees_nothing()
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(Catalog)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(Installs)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CreateAsync(client, Guid.CreateVersion7(), "Anon")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
