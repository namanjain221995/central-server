using System.Net;
using System.Net.Http.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Communication;

/// <summary>
/// HTTP implementation of <see cref="IAgentApiClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// The credential is attached per request and never stored on the
/// <see cref="HttpClient"/>'s default headers, so a client instance can never
/// leak a credential into a request that should not carry one (enrollment).
/// </para>
/// <para>
/// Log discipline: URLs and status codes are logged; header values, bodies and
/// credential material never are.
/// </para>
/// </remarks>
public sealed class AgentApiClient(HttpClient httpClient, ILogger<AgentApiClient> logger) : IAgentApiClient
{
    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly ILogger<AgentApiClient> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<AgentApiResult<EnrollResponse>> EnrollAsync(
        EnrollRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, request.AgentVersion);

        return await SendAsync<EnrollResponse>(message, "enroll", cancellationToken);
    }

    public async Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(
        EnrollmentRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.EnrollRequest)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, request.AgentVersion);

        return await SendAsync<EnrollmentRequestResponse>(message, "enroll-request", cancellationToken);
    }

    public async Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(
        EnrollmentClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.EnrollClaim)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);

        // The request secret is in this body. SendAsync must never log request
        // content, which it does not - it logs status codes and the operation name.
        return await SendAsync<EnrollmentClaimResponse>(message, "enroll-claim", cancellationToken);
    }

    public async Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(
        HeartbeatRequest request,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Heartbeat)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, request.AgentVersion);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<HeartbeatResponse>(message, "heartbeat", cancellationToken);
    }

    public async Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(
        InventoryReport report,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Inventory)
        {
            Content = JsonContent.Create(report),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<InventoryResponse>(message, "inventory", cancellationToken);
    }

    public async Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(
        UsbReport report,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Usb)
        {
            Content = JsonContent.Create(report),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<UsbPolicyResponse>(message, "usb report", cancellationToken);
    }

    public async Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Tasks);

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<AgentTaskListResponse>(message, "claim-tasks", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> PostTaskResultAsync(
        Guid taskId,
        AgentTaskResult result,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AgentProtocol.RoutePrefix}{AgentProtocol.Routes.Tasks}/{taskId}{AgentProtocol.Routes.TaskResultSuffix}")
        {
            Content = JsonContent.Create(result),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendNoContentAsync(message, "task-result", cancellationToken);
    }

    public async Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(
        DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var message = new HttpRequestMessage(HttpMethod.Get, AgentProtocol.RoutePrefix + AgentProtocol.Routes.Policies);
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());
        return await SendAsync<AgentPolicyListResponse>(message, "get-policies", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> PostComplianceAsync(
        AgentPolicyComplianceReport report, DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(credential);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Policies + AgentProtocol.Routes.PolicyComplianceSuffix)
        {
            Content = JsonContent.Create(report),
        };
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());
        return await SendNoContentAsync(message, "post-compliance", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> DownloadPackageAsync(
        Guid packageId, Stream destination, DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Packages + "/" + packageId + AgentProtocol.Routes.PackageContentSuffix);
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent package download failed to reach the server: {Reason}", Describe(ex));
            return AgentApiResult<Unit>.Transient();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return AgentApiResult<Unit>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return AgentApiResult<Unit>.Rejected();
            }

            if (!response.IsSuccessStatusCode)
            {
                return AgentApiResult<Unit>.Transient();
            }

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await source.CopyToAsync(destination, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _logger.LogWarning("Agent package download stream failed: {Reason}", Describe(ex));
                return AgentApiResult<Unit>.Transient();
            }

            return AgentApiResult<Unit>.Success(Unit.Value);
        }
    }

    public async Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(
        Guid packageId, Stream destination, DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.DriverPackages + "/" + packageId
                + AgentProtocol.Routes.PackageContentSuffix);

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await StreamToAsync(message, destination, "driver package", cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="message"/> and copies the response body to
    /// <paramref name="destination"/>, classifying every failure the same way the
    /// software-package download does.
    /// </summary>
    private async Task<AgentApiResult<Unit>> StreamToAsync(
        HttpRequestMessage message, Stream destination, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent {What} download failed to reach the server: {Reason}", what, Describe(ex));
            return AgentApiResult<Unit>.Transient();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return AgentApiResult<Unit>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return AgentApiResult<Unit>.Rejected();
            }

            if (!response.IsSuccessStatusCode)
            {
                return AgentApiResult<Unit>.Transient();
            }

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await source.CopyToAsync(destination, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _logger.LogWarning("Agent {What} download stream failed: {Reason}", what, Describe(ex));
                return AgentApiResult<Unit>.Transient();
            }

            return AgentApiResult<Unit>.Success(Unit.Value);
        }
    }

    public async Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(
        string secretReference, DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, AgentProtocol.RoutePrefix + AgentProtocol.Routes.SecretRedeem)
        {
            Content = JsonContent.Create(new RedeemSecretRequest(secretReference)),
        };
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<RedeemSecretResponse>(message, "redeem-secret", cancellationToken);
    }

    public async Task<AgentApiResult<CatalogListResponse>> GetCatalogAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Catalog);

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<CatalogListResponse>(message, "catalog", cancellationToken);
    }

    public async Task<AgentApiResult<byte[]>> GetCatalogIconAsync(
        Guid appId,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Catalog + "/" + appId + AgentProtocol.Routes.CatalogIconSuffix);

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        // The same download envelope as packages, into a buffer that refuses to
        // grow past the server's icon ceiling: a body that turns out larger is
        // not an icon and is reported as rejected, not as something to retry.
        using var buffer = new BoundedMemoryStream(CompanyPortal.CompanyPortalProtocol.MaxIconBytes);
        var result = await StreamToAsync(message, buffer, "catalog icon", cancellationToken);

        if (buffer.Exceeded)
        {
            return AgentApiResult<byte[]>.Rejected();
        }

        if (!result.IsSuccess)
        {
            return new AgentApiResult<byte[]>(null, result.Status);
        }

        return buffer.Length == 0
            ? AgentApiResult<byte[]>.Rejected()
            : AgentApiResult<byte[]>.Success(buffer.ToArray());
    }

    public async Task<AgentApiResult<CatalogInstallResponse>> RequestCatalogInstallAsync(
        Guid appId,
        CatalogInstallCommand command,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Catalog + "/" + appId + AgentProtocol.Routes.CatalogInstallSuffix)
        {
            Content = JsonContent.Create(command),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<CatalogInstallResponse>(message, "catalog install", cancellationToken);
    }

    public async Task<AgentApiResult<ChatPollResponse>> PollChatMessagesAsync(
        Guid sessionId,
        int afterSeq,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var after = afterSeq < 0 ? 0 : afterSeq;
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AgentProtocol.RoutePrefix}{AgentProtocol.Routes.Chat}/{sessionId}{AgentProtocol.Routes.ChatMessagesSuffix}?after={after}");

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<ChatPollResponse>(message, "chat poll", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> SendChatMessageAsync(
        Guid sessionId,
        string body,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AgentProtocol.RoutePrefix}{AgentProtocol.Routes.Chat}/{sessionId}{AgentProtocol.Routes.ChatMessagesSuffix}")
        {
            Content = JsonContent.Create(new ChatSendRequest(body)),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendNoContentAsync(message, "chat send", cancellationToken);
    }

    /// <summary>
    /// A memory stream with a ceiling. A write that would pass it fails with an
    /// <see cref="IOException"/>, which the download envelope treats as a failed
    /// stream, and <see cref="Exceeded"/> lets the caller tell that apart from a
    /// dropped connection.
    /// </summary>
    private sealed class BoundedMemoryStream(int ceiling) : MemoryStream
    {
        public bool Exceeded { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Check(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        private void Check(int incoming)
        {
            if (Length + incoming > ceiling)
            {
                Exceeded = true;
                throw new IOException("The response is larger than the ceiling for this content.");
            }
        }
    }

    private static void AddProtocolHeaders(HttpRequestMessage message, string agentVersion)
    {
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        message.Headers.Add(AgentProtocol.Headers.AgentVersion, agentVersion);
    }

    public async Task<AgentApiResult<EndpointPlatform.Contracts.Agent.AgentUpdateInfo>> GetAgentUpdateInfoAsync(
        DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.AgentUpdate + "/latest");
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<EndpointPlatform.Contracts.Agent.AgentUpdateInfo>(
            message, "update-info", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(
        Guid releaseId, Stream destination, DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.AgentUpdate + "/" + releaseId + "/content");
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent update download failed to reach the server: {Reason}", Describe(ex));
            return AgentApiResult<Unit>.Transient();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return AgentApiResult<Unit>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return AgentApiResult<Unit>.Rejected();
            }

            if (!response.IsSuccessStatusCode)
            {
                return AgentApiResult<Unit>.Transient();
            }

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await source.CopyToAsync(destination, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // An interrupted download leaves only a partial temp file the
                // executor deletes; nothing about the current install is touched.
                _logger.LogWarning("Agent update download stream failed: {Reason}", Describe(ex));
                return AgentApiResult<Unit>.Transient();
            }

            return AgentApiResult<Unit>.Success(Unit.Value);
        }
    }

    private async Task<AgentApiResult<T>> SendAsync<T>(
        HttpRequestMessage message,
        string operation,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Shutdown, not a failure.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent {Operation} request failed to reach the server: {Reason}",
                operation, Describe(ex));
            return AgentApiResult<T>.Transient();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

                if (body is null)
                {
                    _logger.LogWarning("Agent {Operation} returned success with an empty body.", operation);
                    return AgentApiResult<T>.Transient();
                }

                return AgentApiResult<T>.Success(body);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Agent {Operation} was refused: credential not accepted (401).", operation);
                return AgentApiResult<T>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _logger.LogWarning(
                    "Agent {Operation} was rejected by the server: HTTP {StatusCode}.",
                    operation,
                    (int)response.StatusCode);
                return AgentApiResult<T>.Rejected();
            }

            _logger.LogWarning(
                "Agent {Operation} failed with server error HTTP {StatusCode}; will retry.",
                operation,
                (int)response.StatusCode);
            return AgentApiResult<T>.Transient();
        }
    }

    private async Task<AgentApiResult<Unit>> SendNoContentAsync(
        HttpRequestMessage message,
        string operation,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent {Operation} request failed to reach the server: {Reason}",
                operation, Describe(ex));
            return AgentApiResult<Unit>.Transient();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return AgentApiResult<Unit>.Success(Unit.Value);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return AgentApiResult<Unit>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _logger.LogWarning("Agent {Operation} rejected: HTTP {Status}.", operation, (int)response.StatusCode);
                return AgentApiResult<Unit>.Rejected();
            }

            return AgentApiResult<Unit>.Transient();
        }
    }


    public async Task<AgentApiResult<BitLockerEscrowStatusResponse>> GetBitLockerEscrowStatusAsync(
        DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get, AgentProtocol.RoutePrefix + AgentProtocol.Routes.BitLockerEscrowStatus);

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<BitLockerEscrowStatusResponse>(
            message, "bitlocker-escrow-status", cancellationToken);
    }

    public async Task<AgentApiResult<EscrowRecoveryKeyResponse>> EscrowRecoveryKeyAsync(
        EscrowRecoveryKeyRequest request,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, AgentProtocol.RoutePrefix + AgentProtocol.Routes.BitLockerEscrow)
        {
            // The sealed envelope. Opaque to this client, to the transport, and to
            // the server process that receives it.
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<EscrowRecoveryKeyResponse>(
            message, "bitlocker-escrow", cancellationToken);
    }

    /// <summary>
    /// The exception type and message, followed by each inner cause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transport failures were logged as <c>ex.GetType().Name</c> alone, which for a
    /// transport error is almost never the useful part: every TLS failure, DNS
    /// failure, refused connection and timeout arrives as the same
    /// <c>HttpRequestException</c>, and the reason lives one or two levels down in
    /// the inner chain. An untrusted certificate chain reported as
    /// "HttpRequestException" is indistinguishable from the server being switched
    /// off, which is what made one endpoint failing to enrol take a full
    /// investigation to explain.
    /// </para>
    /// <para>
    /// <b>Safe to log.</b> These messages describe the transport -- the host, the
    /// certificate problem, the socket error -- and never the request body or
    /// headers, which is where this client carries enrollment secrets, credentials
    /// and sealed envelopes. Nothing here reaches into <see cref="HttpRequestMessage.Content"/>.
    /// The chain is bounded so a self-referencing or pathological exception cannot
    /// produce an unbounded log line.
    /// </para>
    /// </remarks>
    private static string Describe(Exception exception)
    {
        const int MaxDepth = 4;
        const int MaxMessageLength = 300;

        var parts = new List<string>(MaxDepth);
        var current = exception;

        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            var message = current.Message ?? string.Empty;
            if (message.Length > MaxMessageLength)
            {
                message = message[..MaxMessageLength] + "...";
            }

            parts.Add($"{current.GetType().Name}: {message}");
            current = current.InnerException;
        }

        return string.Join(" -> ", parts);
    }
}
