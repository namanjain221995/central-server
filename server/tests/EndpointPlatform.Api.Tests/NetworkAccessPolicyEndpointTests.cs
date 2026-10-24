using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The platform-wide network access policy over real HTTP: who may read and write
/// it, that the self-lockout guard refuses a save that would cut the caller off,
/// that a save persists, audits and applies on the very next request, and the
/// middleware's whole decision table - blocked network refused before
/// authentication, loopback and allowed ranges passing, health probes exempt, a
/// missing address failing closed.
/// </summary>
/// <remarks>
/// The policy is a platform-wide singleton in a database every API test shares, so
/// each test that enables it disables it again in a <c>finally</c> - always through
/// the API itself (with a loopback caller, which the policy can never block), so
/// the enforcement cache is invalidated along with the row.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class NetworkAccessPolicyEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Route = new("/admin/v1/settings/network", UriKind.Relative);

    private sealed record PolicyResponse(
        bool Enabled,
        string[] AllowedCidrs,
        DateTimeOffset? UpdatedAtUtc,
        string? UpdatedBy,
        string CallerIp);

    // ---------------------------------------------------------------- helpers

    private async Task<HttpClient> SuperAdminAsync() =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail));

    private static void PresentAs(HttpClient client, string? address)
    {
        client.DefaultRequestHeaders.Remove(AdminApiPostgresFixture.ClientIpHeader);
        if (address is not null)
        {
            client.DefaultRequestHeaders.Add(AdminApiPostgresFixture.ClientIpHeader, address);
        }
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, bool enabled, params string[] cidrs) =>
        client.PutAsJsonAsync(Route, new { enabled, allowedCidrs = cidrs });

    /// <summary>Disables the policy through the API as a loopback caller, which it can never block.</summary>
    private static async Task DisableAsync(HttpClient client)
    {
        PresentAs(client, "127.0.0.1");
        var response = await PutAsync(client, enabled: false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task RemovePolicyRowAsync()
    {
        await using var db = _fixture.CreateDbContext();
        await db.NetworkAccessPolicies
            .Where(p => p.Id == NetworkAccessPolicy.SingletonId)
            .ExecuteDeleteAsync();
    }

    // ------------------------------------------------------------- permission

    [Fact]
    public async Task Reading_and_writing_the_policy_require_platform_settings_manage()
    {
        // The IT administrator runs endpoints, not the platform: separation of
        // duties keeps the console's own perimeter with the Super Administrator.
        using var itAdmin = _fixture.CreateClientFor(
            await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail));

        (await itAdmin.GetAsync(Route)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(itAdmin, enabled: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var helpdesk = _fixture.CreateClientFor(
            await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail));

        (await helpdesk.GetAsync(Route)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------- read

    [Fact]
    public async Task A_never_configured_policy_reads_as_disabled_and_reports_the_callers_address()
    {
        await RemovePolicyRowAsync();
        using var client = await SuperAdminAsync();

        PresentAs(client, "203.0.113.7");
        var response = await client.GetFromJsonAsync<PolicyResponse>(Route);

        response!.Enabled.ShouldBeFalse();
        response.AllowedCidrs.ShouldBeEmpty();
        response.UpdatedAtUtc.ShouldBeNull();
        response.UpdatedBy.ShouldBeNull();
        response.CallerIp.ShouldBe("203.0.113.7");
    }

    [Fact]
    public async Task The_callers_address_is_normalized_and_unknown_when_absent()
    {
        using var client = await SuperAdminAsync();

        // IPv4-mapped IPv6 - what a dual-stack socket reports for an IPv4 caller -
        // reads back as the IPv4 form the ranges will be compared against.
        PresentAs(client, "::ffff:203.0.113.7");
        (await client.GetFromJsonAsync<PolicyResponse>(Route))!.CallerIp.ShouldBe("203.0.113.7");

        // The in-process test server has no remote address at all.
        PresentAs(client, null);
        (await client.GetFromJsonAsync<PolicyResponse>(Route))!.CallerIp.ShouldBe("unknown");
    }

    // ------------------------------------------------------------- validation

    [Fact]
    public async Task A_bad_request_is_refused_before_anything_is_written()
    {
        using var client = await SuperAdminAsync();
        PresentAs(client, "203.0.113.7");

        var garbage = await PutAsync(client, enabled: true, "203.0.113.7", "banana");
        garbage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await garbage.Content.ReadAsStringAsync()).ShouldContain("banana");

        var emptyEnable = await PutAsync(client, enabled: true);
        emptyEnable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var tooMany = await PutAsync(client, enabled: true, Enumerable.Range(0, NetworkAccessPolicy.MaxEntries + 1)
            .Select(i => $"10.{i / 256}.{i % 256}.0/24")
            .Append("203.0.113.7")
            .ToArray());
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------ self-lockout guard

    [Fact]
    public async Task Saving_a_policy_that_would_lock_the_caller_out_is_refused_naming_their_address()
    {
        using var client = await SuperAdminAsync();
        PresentAs(client, "203.0.113.7");

        var response = await PutAsync(client, enabled: true, "10.0.0.0/8");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("203.0.113.7");
        body.ShouldContain("lock you out");
    }

    [Fact]
    public async Task Enabling_without_a_determinable_caller_address_is_refused()
    {
        using var client = await SuperAdminAsync();
        PresentAs(client, null);

        var response = await PutAsync(client, enabled: true, "10.0.0.0/8");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("could not be determined");
    }

    [Fact]
    public async Task A_loopback_caller_may_save_any_policy()
    {
        using var client = await SuperAdminAsync();
        PresentAs(client, "127.0.0.1");

        try
        {
            // 10/8 does not contain 127.0.0.1, but loopback is the break-glass and
            // is never subject to the policy, so the guard does not apply.
            var response = await PutAsync(client, enabled: true, "10.0.0.0/8");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await DisableAsync(client);
        }
    }

    // ------------------------------------------------------------- happy path

    [Fact]
    public async Task Saving_persists_normalizes_audits_and_reads_back()
    {
        using var client = await SuperAdminAsync();
        PresentAs(client, "203.0.113.7");
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        try
        {
            var response = await PutAsync(client, enabled: true, "203.0.113.0/24", "10.0.0.1");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var saved = (await response.Content.ReadFromJsonAsync<PolicyResponse>())!;
            saved.Enabled.ShouldBeTrue();
            saved.AllowedCidrs.ShouldBe(new[] { "203.0.113.0/24", "10.0.0.1/32" });
            saved.UpdatedAtUtc.ShouldNotBeNull();
            saved.UpdatedAtUtc.Value.ShouldBeGreaterThan(before);
            saved.UpdatedBy.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
            saved.CallerIp.ShouldBe("203.0.113.7");

            // Persisted, not just echoed.
            var read = await client.GetFromJsonAsync<PolicyResponse>(Route);
            read!.Enabled.ShouldBeTrue();
            read.AllowedCidrs.ShouldBe(new[] { "203.0.113.0/24", "10.0.0.1/32" });

            await using var db = _fixture.CreateDbContext();

            var row = await db.NetworkAccessPolicies
                .AsNoTracking()
                .SingleAsync(p => p.Id == NetworkAccessPolicy.SingletonId);
            row.Enabled.ShouldBeTrue();
            row.AllowedCidrs.ShouldBe(new[] { "203.0.113.0/24", "10.0.0.1/32" });
            row.UpdatedByEmail.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);

            // The change is on the record: who, what action, and both states.
            var audit = await db.AuditLogEntries
                .AsNoTracking()
                .Where(a => a.Action == "platform.network_policy.update" && a.OccurredAt >= before)
                .OrderByDescending(a => a.OccurredAt)
                .FirstAsync();
            audit.ActorDisplay.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
            audit.TargetType.ShouldBe("network_access_policy");
            audit.NewState.ShouldNotBeNull();
            audit.NewState.ShouldContain("203.0.113.0/24");
            audit.PreviousState.ShouldNotBeNull();
        }
        finally
        {
            await DisableAsync(client);
        }
    }

    // ------------------------------------------------- enforcement, full stack

    [Fact]
    public async Task An_enabled_policy_blocks_disallowed_networks_before_authentication()
    {
        using var admin = await SuperAdminAsync();
        PresentAs(admin, "203.0.113.7");

        try
        {
            (await PutAsync(admin, enabled: true, "203.0.113.0/24")).StatusCode.ShouldBe(HttpStatusCode.OK);

            // Anonymous client, no credentials at all: the refusal happens before
            // authentication could have.
            using var outsider = _fixture.Factory.CreateClient();
            PresentAs(outsider, "198.51.100.9");

            var blocked = await outsider.GetAsync(new Uri("/", UriKind.Relative));
            blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            blocked.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            (await blocked.Content.ReadAsStringAsync())
                .ShouldContain("This console is restricted to approved networks.");

            // A blocked network cannot even see the login form.
            var login = await outsider.PostAsJsonAsync(
                new Uri("/admin/v1/auth/login", UriKind.Relative),
                new { email = AdminApiPostgresFixture.SuperAdminEmail, password = AdminApiPostgresFixture.Password });
            login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            // No address at all fails CLOSED: behind the reverse proxy it is never
            // missing, so an absent address is not a caller the policy can vouch for.
            PresentAs(outsider, null);
            (await outsider.GetAsync(new Uri("/", UriKind.Relative)))
                .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            // Health probes bypass by path, whatever network they probe from.
            PresentAs(outsider, "198.51.100.9");
            (await outsider.GetAsync(new Uri("/health/ready", UriKind.Relative)))
                .StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
            (await outsider.GetAsync(new Uri("/health/live", UriKind.Relative)))
                .StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await DisableAsync(admin);
        }
    }

    [Fact]
    public async Task Allowed_ranges_loopback_and_ipv4_mapped_callers_pass_an_enabled_policy()
    {
        using var admin = await SuperAdminAsync();
        PresentAs(admin, "203.0.113.7");

        try
        {
            (await PutAsync(admin, enabled: true, "203.0.113.0/24")).StatusCode.ShouldBe(HttpStatusCode.OK);

            using var caller = _fixture.Factory.CreateClient();
            var root = new Uri("/", UriKind.Relative);

            // In range.
            PresentAs(caller, "203.0.113.50");
            (await caller.GetAsync(root)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // The same caller as a dual-stack socket reports it.
            PresentAs(caller, "::ffff:203.0.113.50");
            (await caller.GetAsync(root)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // Loopback, both families: the break-glass is never blocked.
            PresentAs(caller, "127.0.0.1");
            (await caller.GetAsync(root)).StatusCode.ShouldBe(HttpStatusCode.OK);
            PresentAs(caller, "::1");
            (await caller.GetAsync(root)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // An IPv6 caller against an IPv4-only range is simply not matched.
            PresentAs(caller, "2001:db8::1");
            (await caller.GetAsync(root)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await DisableAsync(admin);
        }
    }

    [Fact]
    public async Task Disabling_the_policy_applies_on_the_very_next_request()
    {
        using var admin = await SuperAdminAsync();
        PresentAs(admin, "203.0.113.7");

        try
        {
            (await PutAsync(admin, enabled: true, "203.0.113.0/24")).StatusCode.ShouldBe(HttpStatusCode.OK);

            using var outsider = _fixture.Factory.CreateClient();
            PresentAs(outsider, "198.51.100.9");
            (await outsider.GetAsync(new Uri("/", UriKind.Relative)))
                .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            // The save invalidates the enforcement cache - no fifteen-second wait.
            await DisableAsync(admin);

            (await outsider.GetAsync(new Uri("/", UriKind.Relative)))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await DisableAsync(admin);
        }
    }
}
