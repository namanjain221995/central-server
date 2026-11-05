using System.Buffers;
using System.Text.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.FileCollection;
using Microsoft.AspNetCore.Mvc;

namespace EndpointPlatform.AgentApi.Endpoints;

/// <summary>
/// The device side of a file collection: the agent publishes its key material, then
/// uploads ciphertext frames, then reports the result, all under one transfer id.
/// </summary>
/// <remarks>
/// Every route authenticates the device and refuses a transfer that is not bound to
/// it, so a stolen credential cannot feed, or interfere with, another machine's
/// collection. The server only relays the bytes — it cannot read them.
/// </remarks>
public static class AgentFileCollectionEndpoints
{
    private static readonly TimeSpan BackpressureWait = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAgentFileCollectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(AgentProtocol.RoutePrefix);
        var baseRoute = AgentProtocol.Routes.FileCollections + "/{transferId}";

        group.MapPost(baseRoute + AgentProtocol.Routes.FileCollectionHeaderSuffix, PostHeaderAsync)
            .WithName("AgentFileCollectionHeader");
        group.MapPost(baseRoute + AgentProtocol.Routes.FileCollectionChunkSuffix, PostChunkAsync)
            .WithName("AgentFileCollectionChunk");
        group.MapPost(baseRoute + AgentProtocol.Routes.FileCollectionCompleteSuffix, PostCompleteAsync)
            .WithName("AgentFileCollectionComplete");

        return endpoints;
    }

    private static async Task<IResult> PostHeaderAsync(
        string transferId,
        [FromBody] AgentFileCollectionHeader header,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        FileCollectionRelay relay,
        CancellationToken cancellationToken)
    {
        var gate = await AuthoriseAsync(transferId, protocolVersion, credentialHeader, authenticationService, relay);
        if (gate is not null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(header.EphemeralPublicKey) || header.EphemeralPublicKey.Length > 512
            || string.IsNullOrWhiteSpace(header.Salt) || header.Salt.Length > 256)
        {
            return Results.Problem(title: "Malformed collection header.", statusCode: StatusCodes.Status400BadRequest);
        }

        await relay.SetHeaderAsync(transferId, JsonSerializer.Serialize(header, JsonOptions));
        return Results.NoContent();
    }

    private static async Task<IResult> PostChunkAsync(
        string transferId,
        [FromQuery] long? seq,
        HttpRequest request,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        FileCollectionRelay relay,
        CancellationToken cancellationToken)
    {
        var gate = await AuthoriseAsync(transferId, protocolVersion, credentialHeader, authenticationService, relay);
        if (gate is not null)
        {
            return gate;
        }

        if (seq is null || seq < 0)
        {
            return Results.Problem(title: "A non-negative frame sequence is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var frame = await ReadBoundedAsync(request.Body, FileCollectionProtocol.MaxFrameBytes, cancellationToken);
        if (frame is null)
        {
            return Results.Problem(title: "Frame exceeds the maximum size.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (frame.Length == 0)
        {
            return Results.Problem(title: "Empty frame.", statusCode: StatusCodes.Status400BadRequest);
        }

        var written = await relay.TryWriteFrameAsync(transferId, seq.Value, frame, BackpressureWait, cancellationToken);
        return written switch
        {
            FrameWrite.Written => Results.NoContent(),
            // The window is full: the browser is draining slower than the device
            // uploads. The agent retries the same frame shortly.
            FrameWrite.Backpressure => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            // The browser went away or the transfer expired: the agent stops.
            _ => Results.StatusCode(StatusCodes.Status409Conflict),
        };
    }

    private static async Task<IResult> PostCompleteAsync(
        string transferId,
        [FromBody] AgentFileCollectionResult result,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        FileCollectionRelay relay,
        CancellationToken cancellationToken)
    {
        var gate = await AuthoriseAsync(transferId, protocolVersion, credentialHeader, authenticationService, relay);
        if (gate is not null)
        {
            return gate;
        }

        if (result.Message is { Length: > 1024 })
        {
            return Results.Problem(title: "Result message too long.", statusCode: StatusCodes.Status400BadRequest);
        }

        await relay.CompleteAsync(transferId, JsonSerializer.Serialize(result, JsonOptions), result.Succeeded);
        return Results.NoContent();
    }

    /// <summary>
    /// Shared gate: protocol version, device authentication, and that this transfer is
    /// bound to the authenticated device. Returns a failing result to short-circuit on,
    /// or null when the caller may proceed.
    /// </summary>
    private static async Task<IResult?> AuthoriseAsync(
        string transferId,
        int? protocolVersion,
        string? credentialHeader,
        AgentAuthenticationService authenticationService,
        FileCollectionRelay relay)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        var binding = await relay.GetBindingAsync(transferId);
        if (binding is null
            || binding.Value.DeviceId != auth.Device!.Id
            || binding.Value.OrganizationId != auth.Device!.OrganizationId)
        {
            // Unknown, expired, or another device's transfer: all look the same to a
            // device that should not be touching it.
            return Results.NotFound();
        }

        return null;
    }

    /// <summary>
    /// Reads the request body into memory up to <paramref name="max"/> bytes, returning
    /// null if it would exceed that — so an oversized frame is refused, not buffered.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await body.ReadAsync(rented, cancellationToken)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    return null;
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return buffer.ToArray();
    }
}
