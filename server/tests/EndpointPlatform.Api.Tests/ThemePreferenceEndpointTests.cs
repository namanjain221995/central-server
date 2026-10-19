using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The console theme as an account preference: saved against the signed-in
/// administrator, returned with their identity on every later session, and
/// impossible to set for anyone else.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ThemePreferenceEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Me = new("/admin/v1/auth/me", UriKind.Relative);
    private static readonly Uri Theme = new("/admin/v1/auth/me/theme", UriKind.Relative);

    private async Task<HttpClient> ClientAsync(string email) =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(email));

    private static async Task<string?> ThemeOfAsync(HttpClient client)
    {
        var me = await (await client.GetAsync(Me)).Content.ReadFromJsonAsync<JsonElement>();
        return me.GetProperty("themePreference").GetString();
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, string? theme) =>
        client.PutAsJsonAsync(Theme, new { theme });

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("system")]
    public async Task A_choice_is_saved_and_returned_with_the_identity(string theme)
    {
        var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await SetAsync(client, theme)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ThemeOfAsync(client)).ShouldBe(theme);
    }

    [Fact]
    public async Task The_choice_comes_back_in_a_new_session_after_signing_in_again()
    {
        var first = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        (await SetAsync(first, "light")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A separate sign-in: a different session token, as on another device.
        var second = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await ThemeOfAsync(second)).ShouldBe("light");
    }

    [Fact]
    public async Task One_administrators_choice_never_changes_anothers()
    {
        var itAdmin = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        var auditor = await ClientAsync(AdminApiPostgresFixture.AuditorEmail);

        (await SetAsync(auditor, "dark")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SetAsync(itAdmin, "light")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ThemeOfAsync(auditor)).ShouldBe("dark");
        (await ThemeOfAsync(itAdmin)).ShouldBe("light");

        // And in the database, exactly those two rows hold those two values.
        await using var db = _fixture.CreateDbContext();
        (await db.PlatformUsers.SingleAsync(u => u.NormalizedEmail == AdminApiPostgresFixture.AuditorEmail.ToUpperInvariant()))
            .ThemePreference.ShouldBe("dark");
        (await db.PlatformUsers.SingleAsync(u => u.NormalizedEmail == AdminApiPostgresFixture.ItAdminEmail.ToUpperInvariant()))
            .ThemePreference.ShouldBe("light");
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("Dark")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Anything_but_the_three_values_is_refused_and_changes_nothing(string? theme)
    {
        var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);
        (await SetAsync(client, "dark")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SetAsync(client, theme)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await ThemeOfAsync(client)).ShouldBe("dark");
    }

    [Fact]
    public async Task Setting_a_theme_needs_a_session()
    {
        using var anonymous = _fixture.Factory.CreateClient();

        var response = await anonymous.PutAsJsonAsync(Theme, new { theme = "light" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
