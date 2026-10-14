namespace EndpointAgent.Core.CompanyPortal;

/// <summary>
/// What answers a Company Portal request once the transport has decoded it and
/// established who is asking.
/// </summary>
/// <remarks>
/// Separated from the pipe so the pipe can be tested against a fake that records
/// what it was handed, and the handler against a fake server without a pipe. The
/// transport owns the two identifying arguments: <paramref name="callerKey"/> is
/// what it can vouch for (the session id, for the pipe) and is what limits are
/// counted against; <paramref name="requestedBy"/> is the user's name as Windows
/// reports it for that session, for the record only.
/// </remarks>
public interface IPortalRequestHandler
{
    /// <param name="request">The decoded request.</param>
    /// <param name="callerKey">What the transport knows the caller by; limits are per key.</param>
    /// <param name="requestedBy">The signed-in user as the transport reports them, for the record.</param>
    Task<PortalResponse> HandleAsync(
        PortalRequest request, string callerKey, string? requestedBy, CancellationToken cancellationToken = default);
}
