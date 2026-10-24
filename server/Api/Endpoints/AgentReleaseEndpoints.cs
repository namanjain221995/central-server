using System.ComponentModel.DataAnnotations;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Agents;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Agents;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Agent release management: upload, publish, revoke, list, download — and
/// queueing a device's self-update to a published release.
/// </summary>
/// <remarks>
/// <para>
/// RBAC reuses the software permissions deliberately: publishing an agent build
/// that every endpoint may install IS deploying software fleet-wide, so the
/// roles trusted with <see cref="Permissions.Software.Deploy"/> are exactly the
/// roles trusted here, and viewing/downloading follows
/// <see cref="Permissions.Software.View"/>. The MSI itself is a universal,
/// secret-free binary — Auditor being able to download it discloses nothing.
/// </para>
/// <para>
/// The three actions that put SYSTEM-level code on the path to machines —
/// publish, artifact replacement, and queueing a device self-update — require
/// step-up on top of the permission: the caller re-types their own password and
/// records a justification, exactly like a BitLocker recovery-key reveal, behind
/// the same rate-limit-then-verify order (<see cref="AgentReleaseStepUpService"/>).
/// Revoke deliberately does not: withdrawing a build is the safe direction.
/// </para>
/// </remarks>
public static class AgentReleaseEndpoints
{
    /// <summary>MSI ceiling. The current agent MSI is ~40 MB; 250 MB leaves headroom without inviting abuse.</summary>
    private const long MaxMsiBytes = 250L * 1024 * 1024;

    public static IEndpointRouteBuilder MapAgentReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/agent-releases");

        group.MapGet("/", ListAsync)
            .WithName("ListAgentReleases")
            .RequirePermission(Permissions.Software.View);

        // What kind of deployment this is, so the console can say what publishing
        // requires instead of guessing from a signer column.
        group.MapGet("/policy", PolicyAsync)
            .WithName("GetAgentReleasePolicy")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/latest", LatestAsync)
            .WithName("GetLatestAgentRelease")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/{releaseId:guid}/download", DownloadAsync)
            .WithName("DownloadAgentRelease")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/", CreateAsync)
            .WithName("CreateAgentRelease")
            .RequirePermission(Permissions.Software.Deploy)
            .DisableAntiforgery(); // multipart upload; CSRF is covered by the X-Requested-With gate.

        // The same build, signed. Draft only, hash recomputed by the server.
        // Step-up: the form carries currentPassword and justification.
        group.MapPut("/{releaseId:guid}/artifact", ReplaceArtifactAsync)
            .WithName("ReplaceAgentReleaseArtifact")
            .RequirePermission(Permissions.Software.Deploy)
            .DisableAntiforgery(); // multipart upload; CSRF is covered by the X-Requested-With gate.

        group.MapPost("/{releaseId:guid}/publish", PublishAsync)
            .WithName("PublishAgentRelease")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/{releaseId:guid}/revoke", RevokeAsync)
            .WithName("RevokeAgentRelease")
            .RequirePermission(Permissions.Software.Deploy);

        endpoints.MapPost("/admin/v1/devices/{deviceId:guid}/actions/update-agent", QueueUpdateAsync)
            .WithName("UpdateDeviceAgent")
            .RequirePermission(Permissions.Software.Deploy);

        return endpoints;
    }

    /// <summary>The trust mode releases are published under. Not a secret; not tenant-scoped.</summary>
    private static IResult PolicyAsync(IReleasePublishVerifier verifier) =>
        Results.Ok(new { trustMode = verifier.Mode.ToString() });

    private static async Task<IResult> ListAsync(
        EndpointPlatformDbContext dbContext, CancellationToken cancellationToken)
    {
        var releases = await dbContext.AgentReleases
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                r.Version,
                r.Platform,
                r.Architecture,
                r.FileName,
                r.Sha256,
                r.SignerSubject,
                r.ReleaseNotes,
                r.ContentSizeBytes,
                Status = r.Status.ToString(),
                r.CreatedByDisplay,
                r.CreatedAt,
                r.PublishedAt,
                r.RevokedAt,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(releases);
    }

    private static async Task<IResult> LatestAsync(
        AgentReleaseService releaseService, CancellationToken cancellationToken)
    {
        var latest = await releaseService.GetLatestPublishedAsync(cancellationToken);
        return latest is null
            ? Results.Ok(new { available = false })
            : Results.Ok(new
            {
                available = true,
                releaseId = latest.Id,
                version = latest.Version,
                platform = latest.Platform,
                architecture = latest.Architecture,
                fileName = latest.FileName,
                sha256 = latest.Sha256,
                signerSubject = latest.SignerSubject,
                releaseNotes = latest.ReleaseNotes,
                sizeBytes = latest.ContentSizeBytes,
                publishedAt = latest.PublishedAt,
            });
    }

    private static async Task<IResult> DownloadAsync(
        Guid releaseId,
        HttpContext httpContext,
        AgentReleaseService releaseService,
        AuditWriter auditWriter,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Draft included: an administrator downloading a build to install by hand is
        // not the same act as the platform pushing it onto machines. The agent's own
        // content route keeps using OpenPublishedContentAsync, so what a device will
        // install is unchanged by this.
        var (content, release) = await releaseService.OpenDownloadableContentAsync(releaseId, cancellationToken);
        if (content is null || release is null)
        {
            return Results.NotFound();
        }

        // Who fetched which build matters when a bad MSI is being traced.
        var actor = AdminActor.Required(httpContext.User);
        auditWriter.Stage(
            actor.OrganizationId, AuditActorType.PlatformUser, actor.UserId, actor.Email,
            action: "agent_release.downloaded", AuditResult.Success,
            audit => audit.OnTarget("agent_release", release.Id.ToString(), $"windows/x64 {release.Version}"));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Stream(content, "application/octet-stream", release.FileName);
    }

    private static async Task<IResult> CreateAsync(
        AgentReleaseService releaseService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // Before the form is read: Kestrel's default 30 MB cap otherwise vetoes
        // the upload before the MaxMsiBytes check below ever runs.
        RequestBodyLimits.AllowUploadOf(httpContext, MaxMsiBytes);

        if (!httpContext.Request.HasFormContentType)
        {
            return Results.Problem("Expected a multipart/form-data upload.", statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return Results.Problem("A non-empty 'file' part is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxMsiBytes)
        {
            return Results.Problem("MSI exceeds the maximum allowed size.", statusCode: StatusCodes.Status400BadRequest);
        }

        string? Field(string key) => form.TryGetValue(key, out var v) ? v.ToString() : null;

        var version = Field("version");

        // Optional and advisory. The server hashes the bytes it stores; a declared
        // hash only lets a damaged upload be refused rather than recorded.
        var declaredSha256 = Field("sha256");
        if (string.IsNullOrWhiteSpace(version))
        {
            return Results.Problem("version is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        await using var content = file.OpenReadStream();

        var (release, error) = await releaseService.CreateAsync(
            version!, file.FileName, declaredSha256, Field("releaseNotes"),
            content, actor.UserId, actor.Email, actor.OrganizationId, cancellationToken);

        return release is null
            ? Results.Problem(error ?? "The release could not be created.", statusCode: StatusCodes.Status400BadRequest)
            : Results.Created($"/admin/v1/agent-releases/{release.Id}", new { releaseId = release.Id, release.Version });
    }

    // ----------------------------------------------------------------- step-up

    /// <param name="CurrentPassword">
    /// The caller's own password, re-verified here. Holding the permission proves
    /// what the account may do; this proves the account is still being driven by
    /// the person who signed in.
    /// </param>
    /// <param name="Justification">Recorded in the audit trail. Not optional.</param>
    public sealed record ReleaseStepUpRequest(
        [property: Required, StringLength(256, MinimumLength = 1)] string? CurrentPassword,
        [property: Required, StringLength(500, MinimumLength = 3)] string? Justification);

    /// <summary>Maps a refused step-up gate to its response, mirroring the reveal route.</summary>
    private static IResult? Refusal(ReleaseActionGateResult gate, HttpContext httpContext)
    {
        switch (gate.Outcome)
        {
            case ReleaseActionGateOutcome.RateLimited:
                httpContext.Response.Headers.RetryAfter = gate.RetryAfterSeconds.ToString();
                return Results.Problem(gate.Error, statusCode: StatusCodes.Status429TooManyRequests);

            // Same status and shape as a wrong password on the sign-in path, and
            // for the same reason: the caller learns that it failed, not which
            // check failed.
            case ReleaseActionGateOutcome.StepUpFailed:
                return Results.Problem(gate.Error, statusCode: StatusCodes.Status403Forbidden);

            default:
                return null;
        }
    }

    /// <summary>
    /// Publishes a draft, after step-up. Publishing makes the build installable
    /// fleet-wide, so it is gated like a recovery-key reveal: rate limit, the
    /// caller's own password, and a recorded justification.
    /// </summary>
    private static async Task<IResult> PublishAsync(
        Guid releaseId,
        ReleaseStepUpRequest request,
        HttpContext httpContext,
        AgentReleaseService releaseService,
        AgentReleaseStepUpService stepUp,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var actor = AdminActor.Required(httpContext.User);

        // Looked up before the gate, like an escrow before a reveal: a caller who
        // names a release that does not exist gets 404 without consuming budget,
        // and the denial audit below can name the version it refused.
        var release = await dbContext.AgentReleases
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == releaseId, cancellationToken);
        if (release is null)
        {
            return Results.NotFound();
        }

        var gate = await stepUp.AuthorizeAsync(
            "agent_release.publish_denied", release.Id, $"windows/x64 {release.Version}",
            actor.UserId, actor.Email, actor.OrganizationId,
            request.CurrentPassword!, device: null, cancellationToken);

        if (Refusal(gate, httpContext) is { } refused)
        {
            return refused;
        }

        var outcome = await releaseService.PublishAsync(
            releaseId, actor.UserId, actor.Email, actor.OrganizationId,
            request.Justification!, cancellationToken);

        return ToTransitionResult(outcome);
    }

    /// <summary>Revokes a release. No step-up: withdrawing a build is the safe direction.</summary>
    private static async Task<IResult> RevokeAsync(
        Guid releaseId,
        HttpContext httpContext,
        AgentReleaseService releaseService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var outcome = await releaseService.RevokeAsync(
            releaseId, actor.UserId, actor.Email, actor.OrganizationId, cancellationToken);

        return ToTransitionResult(outcome);
    }

    private static IResult ToTransitionResult(AgentReleaseActionOutcome outcome) => outcome.Result switch
    {
        AgentReleaseActionResult.Success => Results.NoContent(),
        AgentReleaseActionResult.NotFound => Results.NotFound(),

        // The publish gate. 422 rather than 409: the release is in a state that
        // allows publishing, but the artifact itself is not acceptable. The
        // reason is the verifier's own -- it names the check that failed under
        // the configured trust mode, and never the bytes.
        AgentReleaseActionResult.NotVerified => Results.Problem(
            "The release cannot be published: " + (outcome.Reason ?? "its artifact did not pass verification."),
            statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Results.Problem(
            "The release is not in a state that allows this action.",
            statusCode: StatusCodes.Status409Conflict),
    };

    /// <summary>
    /// Replaces a draft's MSI with its signed counterpart, after step-up. The
    /// bytes a published row will point at change here, which is exactly as
    /// consequential as publishing — same gate, carried as form fields
    /// (<c>currentPassword</c>, <c>justification</c>) beside the file.
    /// </summary>
    private static async Task<IResult> ReplaceArtifactAsync(
        Guid releaseId,
        HttpContext httpContext,
        AgentReleaseService releaseService,
        AgentReleaseStepUpService stepUp,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return Results.Problem("Expected a multipart form upload.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Results.Problem("A file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        string? Field(string key) => form.TryGetValue(key, out var v) ? v.ToString() : null;

        var request = new ReleaseStepUpRequest(Field("currentPassword"), Field("justification"));
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var actor = AdminActor.Required(httpContext.User);

        var release = await dbContext.AgentReleases
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == releaseId, cancellationToken);
        if (release is null)
        {
            return Results.NotFound();
        }

        var gate = await stepUp.AuthorizeAsync(
            "agent_release.artifact_replace_denied", release.Id, $"windows/x64 {release.Version}",
            actor.UserId, actor.Email, actor.OrganizationId,
            request.CurrentPassword!, device: null, cancellationToken);

        if (Refusal(gate, httpContext) is { } refused)
        {
            return refused;
        }

        await using var content = file.OpenReadStream();
        var (replaced, error) = await releaseService.ReplaceArtifactAsync(
            releaseId, file.FileName, Field("sha256"), content,
            actor.UserId, actor.Email, actor.OrganizationId, request.Justification!, cancellationToken);

        if (replaced is null && error is null)
        {
            return Results.NotFound();
        }

        if (replaced is null)
        {
            return Results.Problem(error, statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new
        {
            releaseId = replaced.Id,
            sha256 = replaced.Sha256,
            sizeBytes = replaced.ContentSizeBytes,
            signerSubject = replaced.SignerSubject,
        });
    }

    /// <param name="CurrentPassword">Step-up; see <see cref="ReleaseStepUpRequest"/>.</param>
    /// <param name="Justification">Recorded in the audit trail. Not optional.</param>
    public sealed record UpdateAgentRequest(
        Guid ReleaseId,
        [property: Required, StringLength(256, MinimumLength = 1)] string? CurrentPassword,
        [property: Required, StringLength(500, MinimumLength = 3)] string? Justification);

    /// <summary>
    /// Queues a device's self-update to a published release, after step-up. The
    /// server refuses downgrades and same-version updates here, and the agent
    /// independently enforces the same rule — neither side trusts the other to
    /// have checked.
    /// </summary>
    private static async Task<IResult> QueueUpdateAsync(
        Guid deviceId,
        UpdateAgentRequest request,
        HttpContext httpContext,
        AgentReleaseService releaseService,
        AgentReleaseStepUpService stepUp,
        EndpointPlatformDbContext dbContext,
        DeviceTaskService taskService,
        DeviceScopeAuthorizer scope,
        AuditWriter auditWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var actor = AdminActor.Required(httpContext.User);

        // Device scope, checked first and on its own.
        //
        // This route previously filtered on OrganizationId alone, which is a
        // weaker rule than every other device-targeted endpoint applies: an
        // administrator scoped to one group could queue an agent update -- an
        // installer running as SYSTEM -- on any machine in the tenant. Scope is
        // deny-by-default, so an account with no scope rows now reaches nothing.
        //
        // Answered as 404 rather than 403, matching the other device routes: a
        // caller who cannot act on a device is not told it exists.
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var device = await dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId, cancellationToken);
        if (device is null)
        {
            return Results.NotFound();
        }

        // Step-up, before any statement about the release: in-scope callers only
        // get this far, and the attempt is counted whatever comes of it.
        var gate = await stepUp.AuthorizeAsync(
            "agent_release.update_agent_denied", request.ReleaseId, device.Hostname,
            actor.UserId, actor.Email, actor.OrganizationId,
            request.CurrentPassword!, device, cancellationToken);

        if (Refusal(gate, httpContext) is { } refused)
        {
            return refused;
        }

        var release = await dbContext.AgentReleases
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == request.ReleaseId, cancellationToken);
        if (release is null || !release.IsPublished)
        {
            return Results.Problem(
                "The release does not exist or is not published.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (!AgentVersionNumber.IsNewer(release.Version, device.AgentVersion))
        {
            return Results.Problem(
                $"Release {release.Version} is not newer than the installed agent "
                + $"{device.AgentVersion}. Downgrades and same-version reinstalls are not offered.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var task = await taskService.QueueAsync(
            actor.OrganizationId, deviceId, DeviceTaskType.UpdateAgent,
            new TaskPayloads.UpdateAgent(release.Id, release.Version, release.Sha256),
            actor.UserId, actor.Email, cancellationToken);

        if (task is null)
        {
            return Results.NotFound();
        }

        // The step-up's own success entry, beside the generic task.queue audit:
        // it is the one that carries the justification, like a reveal's does.
        // Through the redactor, because the justification is operator free text
        // and the trail is append-only.
        auditWriter.Stage(
            actor.OrganizationId, AuditActorType.PlatformUser, actor.UserId, actor.Email,
            action: "agent_release.update_agent", AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("agent_release", release.Id.ToString(), $"windows/x64 {release.Version}")
                .Requiring(Permissions.Software.Deploy)
                .WithStateChange(null, AuditStateRedactor.Redact(new Dictionary<string, object?>
                {
                    ["releaseId"] = release.Id,
                    ["version"] = release.Version,
                    ["taskId"] = task.Id,
                    ["justification"] = request.Justification,
                })));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Accepted($"/admin/v1/devices/{deviceId}/tasks", new { taskId = task.Id });
    }

    // ------------------------------------------------------------------ shared

    private static IResult? Validate<T>(T request) where T : notnull
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return null;
        }

        // Reports which member failed, never the value it carried -- one of these
        // members is the caller's own password.
        return Results.ValidationProblem(results.ToDictionary(
            v => v.MemberNames.FirstOrDefault() ?? "request",
            v => new[] { v.ErrorMessage ?? "Invalid." }));
    }
}
