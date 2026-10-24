using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Agents;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Step-up on the three dangerous agent-release routes: publish, artifact
/// replace, and update-agent.
/// </summary>
/// <remarks>
/// <para>
/// Each of these puts SYSTEM-level code on the path to the fleet, and each was
/// gated by <c>software.deploy</c> alone — the 1.16.0 accident (a broken build
/// published fleet-wide that uninstalled agents) went through exactly that gap.
/// They now mirror the BitLocker reveal gate: rate limit first, then the
/// caller's own password, then the action, with a justification recorded in the
/// audit trail and every refusal audited too. The refusals are the feature, so
/// most of these tests are refusals.
/// </para>
/// <para>
/// The three routes share ONE per-user window (five actions per fifteen
/// minutes, success does not reset it), so every test mints its own
/// administrator; see <see cref="ReleaseStepUpTestSupport"/>.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class AgentReleaseStepUpTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Releases = new("/admin/v1/agent-releases", UriKind.Relative);

    private static Uri Release(Guid id, string action) =>
        new($"/admin/v1/agent-releases/{id}/{action}", UriKind.Relative);

    private static Uri UpdateAgent(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/actions/update-agent", UriKind.Relative);

    // ---- helpers -----------------------------------------------------------

    private static MultipartFormDataContent UploadForm(byte[] bytes, string version)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { file, "file", $"EndpointPlatformAgent-{version}-x64.msi" },
            { new StringContent(version), "version" },
        };
    }

    /// <summary>Uploads a draft. Registering is deliberately NOT step-up gated.</summary>
    private static async Task<Guid> CreateDraftAsync(HttpClient client, string version)
    {
        var bytes = TestArtifacts.UnsignedMsi($"{version}-{Guid.CreateVersion7():N}", version);
        var response = await client.PostAsync(Releases, UploadForm(bytes, version));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();
        return Guid.Parse(System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("releaseId").GetString()!);
    }

    private async Task<Guid> SeedDeviceAsync(string hostname, string agentVersion = "1.0.0")
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        var token = new EnrollmentToken(
            organizationId, $"stepup-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            Guid.CreateVersion7(), "step-up-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<List<AuditLogEntry>> AuditRowsAsync(string action, string targetId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.Action == action && e.TargetId == targetId)
            .ToListAsync();
    }

    private async Task<int> TaskCountAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId);
    }

    // ---- 400: the fields are not optional ----------------------------------

    [Theory]
    [InlineData("", "a justification", 0)]
    [InlineData("password", "", 1)]
    public async Task Publishing_requires_both_a_password_and_a_justification(string password, string justification, int variant)
    {
        var (client, _) = await _fixture.MintDeployAdminAsync("pub400");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, $"12.1.{variant}");

        var response = await client.PostAsJsonAsync(
            Release(releaseId, "publish"), new { currentPassword = password, justification });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Refused before anything ran: still a draft, and the caller's password
        // value is never echoed back.
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(AdminApiPostgresFixture.Password);
        await using var db = _fixture.CreateDbContext();
        (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
            .Status.ShouldBe(Domain.Agents.AgentReleaseStatus.Draft);
    }

    [Theory]
    [InlineData("", "a justification", 0)]
    [InlineData("password", "", 1)]
    public async Task Replacing_an_artifact_requires_both_fields(string password, string justification, int variant)
    {
        var (client, _) = await _fixture.MintDeployAdminAsync("rep400");
        using var _1 = client;
        var version = $"12.2.{variant}";
        var releaseId = await CreateDraftAsync(client, version);
        var replacement = TestArtifacts.UnsignedMsi($"{version}-b-{Guid.CreateVersion7():N}", version);

        var form = UploadForm(replacement, version);
        form.Add(new StringContent(password), "currentPassword");
        form.Add(new StringContent(justification), "justification");

        var response = await client.PutAsync(Release(releaseId, "artifact"), form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("", "a justification", 0)]
    [InlineData("password", "", 1)]
    public async Task Queueing_an_update_requires_both_fields(string password, string justification, int variant)
    {
        var (client, _) = await _fixture.MintDeployAdminAsync("upd400");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, $"12.3.{variant}");
        (await client.PostAsync(Release(releaseId, "publish"), ReleaseStepUpTestSupport.PublishBody()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var deviceId = await SeedDeviceAsync("STEPUP-400");

        var response = await client.PostAsJsonAsync(
            UpdateAgent(deviceId), new { releaseId, currentPassword = password, justification });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TaskCountAsync(deviceId)).ShouldBe(0, "a refused update must not leave a task behind");
    }

    // ---- 403: the caller's own password, really ----------------------------

    [Fact]
    public async Task Publishing_with_the_wrong_password_is_refused_and_audited()
    {
        var (client, email) = await _fixture.MintDeployAdminAsync("pub403");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.4.0");

        var response = await client.PostAsync(
            Release(releaseId, "publish"),
            ReleaseStepUpTestSupport.PublishBody(currentPassword: "definitely-not-the-password"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
                .Status.ShouldBe(Domain.Agents.AgentReleaseStatus.Draft, "a refused publish changes nothing");
        }

        var denied = (await AuditRowsAsync("agent_release.publish_denied", releaseId.ToString()))
            .ShouldHaveSingleItem();
        denied.Result.ShouldBe(AuditResult.Failure);
        denied.ActorDisplay.ShouldBe(email);
        denied.FailureReason.ShouldBe("Step-up authentication failed.");
        denied.NewState.ShouldNotBeNull();
        denied.NewState!.ShouldContain("step-up authentication failed");

        // Neither password reaches the trail or the response.
        var payload = (denied.NewState ?? "") + (denied.FailureReason ?? "");
        payload.ShouldNotContain("definitely-not-the-password");
        payload.ShouldNotContain(AdminApiPostgresFixture.Password);
    }

    [Fact]
    public async Task Replacing_an_artifact_with_the_wrong_password_is_refused_and_audited()
    {
        var (client, email) = await _fixture.MintDeployAdminAsync("rep403");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.5.0");

        string sha256Before;
        await using (var db = _fixture.CreateDbContext())
        {
            sha256Before = (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId)).Sha256;
        }

        var replacement = TestArtifacts.UnsignedMsi($"12.5.0-b-{Guid.CreateVersion7():N}", "12.5.0");
        var response = await client.PutAsync(
            Release(releaseId, "artifact"),
            UploadForm(replacement, "12.5.0").WithStepUp(currentPassword: "wrong"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
                .Sha256.ShouldBe(sha256Before, "the draft's artifact is untouched");
        }

        var denied = (await AuditRowsAsync("agent_release.artifact_replace_denied", releaseId.ToString()))
            .ShouldHaveSingleItem();
        denied.ActorDisplay.ShouldBe(email);
        denied.FailureReason.ShouldBe("Step-up authentication failed.");
    }

    [Fact]
    public async Task Queueing_an_update_with_the_wrong_password_is_refused_and_audited()
    {
        var (client, email) = await _fixture.MintDeployAdminAsync("upd403");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.6.0");
        (await client.PostAsync(Release(releaseId, "publish"), ReleaseStepUpTestSupport.PublishBody()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var deviceId = await SeedDeviceAsync("STEPUP-403");

        var response = await client.PostAsync(
            UpdateAgent(deviceId),
            ReleaseStepUpTestSupport.UpdateAgentBody(releaseId, currentPassword: "wrong"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TaskCountAsync(deviceId)).ShouldBe(0, "a refused update must not leave a task behind");

        var denied = (await AuditRowsAsync("agent_release.update_agent_denied", releaseId.ToString()))
            .ShouldHaveSingleItem();
        denied.ActorDisplay.ShouldBe(email);
        denied.DeviceId.ShouldBe(deviceId);
        denied.FailureReason.ShouldBe("Step-up authentication failed.");
    }

    // ---- success: the audit carries the justification -----------------------

    [Fact]
    public async Task A_correct_step_up_publishes_and_the_audit_records_the_justification()
    {
        var (client, email) = await _fixture.MintDeployAdminAsync("pubok");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.7.0");
        const string Justification = "ship 12.7.0 to the pilot ring after staged verification";

        var response = await client.PostAsync(
            Release(releaseId, "publish"), ReleaseStepUpTestSupport.PublishBody(justification: Justification));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var published = (await AuditRowsAsync("agent_release.published", releaseId.ToString()))
            .Single(e => e.Result == AuditResult.Success);
        published.ActorDisplay.ShouldBe(email);
        published.NewState.ShouldNotBeNull();
        published.NewState!.ShouldContain(Justification);
        published.NewState.ShouldNotContain(AdminApiPostgresFixture.Password);
    }

    [Fact]
    public async Task A_correct_step_up_replaces_the_artifact_and_the_audit_records_the_justification()
    {
        var (client, _) = await _fixture.MintDeployAdminAsync("repok");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.8.0");
        const string Justification = "swap in the signed build of 12.8.0";

        var replacement = TestArtifacts.UnsignedMsi($"12.8.0-b-{Guid.CreateVersion7():N}", "12.8.0");
        var response = await client.PutAsync(
            Release(releaseId, "artifact"),
            UploadForm(replacement, "12.8.0").WithStepUp(justification: Justification));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var replaced = (await AuditRowsAsync("agent_release.artifact_replaced", releaseId.ToString()))
            .Single(e => e.Result == AuditResult.Success);
        replaced.NewState.ShouldNotBeNull();
        replaced.NewState!.ShouldContain(Justification);

        // The row itself records the new bytes; the audit document is asserted on
        // its stable fields, because it passes through the redactor and a hex
        // hash can - rarely - contain a digit run the redactor treats as secret-shaped.
        replaced.NewState.ShouldContain("sizeBytes");
        await using var db = _fixture.CreateDbContext();
        (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
            .Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(replacement)));
    }

    [Fact]
    public async Task A_correct_step_up_queues_the_update_and_the_audit_records_the_justification()
    {
        var (client, email) = await _fixture.MintDeployAdminAsync("updok");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.9.0");
        (await client.PostAsync(Release(releaseId, "publish"), ReleaseStepUpTestSupport.PublishBody()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var deviceId = await SeedDeviceAsync("STEPUP-OK");
        const string Justification = "roll the pilot device onto 12.9.0 first, fleet after verification";

        var response = await client.PostAsync(
            UpdateAgent(deviceId),
            ReleaseStepUpTestSupport.UpdateAgentBody(releaseId, justification: Justification));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await TaskCountAsync(deviceId)).ShouldBe(1);

        var queued = (await AuditRowsAsync("agent_release.update_agent", releaseId.ToString()))
            .Single(e => e.Result == AuditResult.Success);
        queued.ActorDisplay.ShouldBe(email);
        queued.DeviceId.ShouldBe(deviceId);
        queued.NewState.ShouldNotBeNull();
        queued.NewState!.ShouldContain(Justification);
        queued.NewState.ShouldContain("12.9.0");
    }

    // ---- 429: one budget across all three routes ----------------------------

    /// <summary>
    /// Five gated actions per user per window, shared across publish and
    /// update-agent, and a success does not reset the counter.
    /// </summary>
    /// <remarks>
    /// The "success does not reset" half is the point, exactly as it is for
    /// recovery-key reveals: a limiter that reset on a permitted action would
    /// bound only the refused ones, and it is precisely the permitted actions --
    /// each one SYSTEM-level code headed for a machine -- that need bounding.
    /// </remarks>
    [Fact]
    public async Task A_user_is_limited_to_five_release_actions_per_window()
    {
        var (client, _) = await _fixture.MintDeployAdminAsync("limit");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.10.0");
        var deviceId = await SeedDeviceAsync("STEPUP-LIMIT");

        // Attempt 1: publish. Attempts 2..5: queue updates. All permitted, so
        // success alone demonstrably does not replenish the budget.
        (await client.PostAsync(Release(releaseId, "publish"), ReleaseStepUpTestSupport.PublishBody()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "attempt 1 is within the limit");

        for (var attempt = 2; attempt <= ReleaseActionRateLimiter.MaxAttemptsPerWindow; attempt++)
        {
            (await client.PostAsync(
                    UpdateAgent(deviceId),
                    ReleaseStepUpTestSupport.UpdateAgentBody(releaseId, justification: $"attempt {attempt}")))
                .StatusCode.ShouldBe(HttpStatusCode.Accepted, $"attempt {attempt} should be within the limit");
        }

        var blocked = await client.PostAsync(
            UpdateAgent(deviceId), ReleaseStepUpTestSupport.UpdateAgentBody(releaseId, justification: "attempt 6"));

        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        blocked.Headers.Contains("Retry-After").ShouldBeTrue();

        (await TaskCountAsync(deviceId)).ShouldBe(
            ReleaseActionRateLimiter.MaxAttemptsPerWindow - 1,
            "the refused attempt queued nothing");

        // The refusal is audited, with its reason and no secret.
        var denied = (await AuditRowsAsync("agent_release.update_agent_denied", releaseId.ToString()))
            .ShouldHaveSingleItem();
        denied.FailureReason.ShouldBe("Rate limited (user).");
        denied.NewState.ShouldNotBeNull();
        denied.NewState!.ShouldContain("rate limited");
        ((denied.NewState ?? "") + (denied.FailureReason ?? ""))
            .ShouldNotContain(AdminApiPostgresFixture.Password);
    }

    // ---- permissions unchanged ----------------------------------------------

    /// <summary>
    /// Step-up sits BEHIND the permission, not instead of it: a caller without
    /// software.deploy is refused by RBAC even with a perfectly valid step-up
    /// body, and no release budget or audit denial is spent on them.
    /// </summary>
    [Fact]
    public async Task Helpdesk_is_still_forbidden_even_with_a_valid_step_up_body()
    {
        var (admin, _) = await _fixture.MintDeployAdminAsync("rbac");
        using var _1 = admin;
        var releaseId = await CreateDraftAsync(admin, "12.11.0");
        var deviceId = await SeedDeviceAsync("STEPUP-RBAC");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail);
        using var helpdesk = _fixture.CreateClientFor(token);

        (await helpdesk.PostAsync(Release(releaseId, "publish"), ReleaseStepUpTestSupport.PublishBody()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await helpdesk.PutAsync(
                Release(releaseId, "artifact"),
                UploadForm(TestArtifacts.UnsignedMsi($"12.11.0-b-{Guid.CreateVersion7():N}", "12.11.0"), "12.11.0").WithStepUp()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await helpdesk.PostAsync(UpdateAgent(deviceId), ReleaseStepUpTestSupport.UpdateAgentBody(releaseId)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // RBAC refused before the step-up gate ran: no release denial was audited.
        (await AuditRowsAsync("agent_release.publish_denied", releaseId.ToString())).ShouldBeEmpty();
        (await AuditRowsAsync("agent_release.update_agent_denied", releaseId.ToString())).ShouldBeEmpty();

        await using var db = _fixture.CreateDbContext();
        (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
            .Status.ShouldBe(Domain.Agents.AgentReleaseStatus.Draft);
    }

    /// <summary>Revoke deliberately takes no step-up: withdrawing a build is the safe direction.</summary>
    [Fact]
    public async Task Revoking_still_requires_no_step_up()
    {
        var (client, _) = await _fixture.MintDeployAdminAsync("revoke");
        using var _1 = client;
        var releaseId = await CreateDraftAsync(client, "12.12.0");
        (await client.PostAsync(Release(releaseId, "publish"), ReleaseStepUpTestSupport.PublishBody()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsync(Release(releaseId, "revoke"), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
