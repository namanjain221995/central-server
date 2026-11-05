using System.Buffers.Binary;
using System.Text;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.FileCollection;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Collect files from a managed device: start a transfer, then stream the encrypted
/// archive straight to the operator's browser.
/// </summary>
/// <remarks>
/// <para>
/// The bytes are end-to-end encrypted between the agent and the browser and are only
/// relayed here — never stored. The start call re-confirms the operator with a
/// password step-up and records the download in the audit log; the stream call then
/// pulls the bounded ciphertext frames the device is uploading and writes them to the
/// response as a simple length-prefixed framing (a 4-byte big-endian length then that
/// many bytes; a zero-length frame ends the stream). The first frame is the agent's
/// key material; every frame after it is ciphertext only the browser can read.
/// </para>
/// <para>
/// All three routes require <see cref="Permissions.Files.Collect"/> and are scoped to
/// a device the caller may act on, so a transfer cannot be started, streamed or
/// inspected for a machine outside the operator's reach.
/// </para>
/// </remarks>
public static class FileCollectionEndpoints
{
    private static readonly TimeSpan HeaderWait = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FrameIdleTimeout = TimeSpan.FromSeconds(180);

    public static IEndpointRouteBuilder MapFileCollectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}/file-collections")
            .RequirePermission(Permissions.Files.Collect);

        group.MapPost("/", StartAsync).WithName("StartFileCollection");
        group.MapPost("/list", StartListAsync).WithName("StartFileCollectionListing");
        group.MapGet("/{transferId}/stream", StreamAsync).WithName("StreamFileCollection");
        group.MapGet("/{transferId}/status", StatusAsync).WithName("GetFileCollectionStatus");

        return endpoints;
    }

    public sealed record StartFileCollectionRequest(
        string? RecipientPublicKey,
        string? UserSid,
        IReadOnlyList<string>? RootKeys,
        string? CurrentPassword,
        string? Justification,
        IReadOnlyList<string>? SelectedPaths = null);

    public sealed record StartFileListingRequest(
        string? RecipientPublicKey,
        string? UserSid,
        IReadOnlyList<string>? RootKeys);

    private static async Task<IResult> StartAsync(
        Guid deviceId,
        StartFileCollectionRequest request,
        FileCollectionService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.StartAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email,
            request.RecipientPublicKey, request.UserSid, request.RootKeys, request.SelectedPaths,
            request.CurrentPassword, request.Justification, cancellationToken);

        return ToResult(result, "Could not start the collection.");
    }

    private static async Task<IResult> StartListAsync(
        Guid deviceId,
        StartFileListingRequest request,
        FileCollectionService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.StartListAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email,
            request.RecipientPublicKey, request.UserSid, request.RootKeys, cancellationToken);

        return ToResult(result, "Could not start the listing.");
    }

    private static IResult ToResult(FileCollectionStartResult result, string genericError) =>
        result.Status switch
        {
            FileCollectionStartStatus.Started => Results.Ok(new { transferId = result.TransferId }),
            FileCollectionStartStatus.BadRequest =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),
            FileCollectionStartStatus.StepUpFailed =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden),
            FileCollectionStartStatus.NotFound => Results.NotFound(),
            FileCollectionStartStatus.AgentUnavailable =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(genericError, statusCode: StatusCodes.Status500InternalServerError),
        };

    private static async Task<IResult> StreamAsync(
        Guid deviceId,
        string transferId,
        FileCollectionRelay relay,
        DeviceScopeAuthorizer scope,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var binding = await relay.GetBindingAsync(transferId);
        if (binding is null
            || binding.Value.OrganizationId != actor.OrganizationId
            || binding.Value.DeviceId != deviceId
            || !await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        // Resolve the key material before committing to a 200 stream, so a device that
        // never starts the collection surfaces as an error rather than an empty file.
        var header = await relay.WaitForHeaderAsync(transferId, HeaderWait, cancellationToken);
        if (header is null)
        {
            return Results.Problem(
                "The device did not begin the collection in time.", statusCode: StatusCodes.Status504GatewayTimeout);
        }

        var logger = loggerFactory.CreateLogger("FileCollectionStream");

        return Results.Stream(async stream =>
        {
            try
            {
                // Frame 0: the agent's key material (JSON). Frames 1..N: ciphertext.
                await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(header), cancellationToken);

                long seq = 0;
                while (true)
                {
                    var read = await relay.ReadFrameAsync(transferId, seq, FrameIdleTimeout, cancellationToken);
                    if (read.Kind == FrameRead.ReadKind.Data)
                    {
                        await WriteFrameAsync(stream, read.Bytes!, cancellationToken);
                        await relay.AckFrameAsync(transferId, seq);
                        seq++;
                        continue;
                    }

                    // EndOfStream or Failed: the terminator (a zero-length frame) ends
                    // the framing. The browser confirms success or truncation via the
                    // status route, which carries the agent's own result.
                    await WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty, cancellationToken);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // The browser went away: tell the agent to stop uploading.
                await relay.AbortAsync(transferId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "File-collection stream {TransferId} faulted.", transferId);
                await relay.AbortAsync(transferId);
            }
        }, "application/octet-stream");
    }

    private static async Task<IResult> StatusAsync(
        Guid deviceId,
        string transferId,
        FileCollectionRelay relay,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var binding = await relay.GetBindingAsync(transferId);
        if (binding is null
            || binding.Value.OrganizationId != actor.OrganizationId
            || binding.Value.DeviceId != deviceId
            || !await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var resultJson = await relay.GetResultAsync(transferId);
        return resultJson is null
            ? Results.NoContent()
            : Results.Content(resultJson, "application/json");
    }

    /// <summary>
    /// Writes one length-prefixed frame: a 4-byte big-endian length then the payload.
    /// A zero-length frame is the end-of-stream terminator.
    /// </summary>
    private static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }
}
