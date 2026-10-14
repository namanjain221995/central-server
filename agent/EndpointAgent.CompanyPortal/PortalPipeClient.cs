using System.IO.Pipes;
using EndpointAgent.Core.CompanyPortal;
using EndpointAgent.Windows.CompanyPortal;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.CompanyPortal;

/// <summary>Why a request never got an answer from the agent: the pipe side, before any server was asked.</summary>
public enum PortalUnavailableReason
{
    /// <summary>Nobody is serving the pipe, or the connection broke before an answer arrived.</summary>
    AgentNotRunning,

    /// <summary>Something served the pipe, but not from session 0, so it is not the agent. Nothing was sent to it.</summary>
    Untrusted,

    /// <summary>The agent accepted the request and did not answer within the allowed time.</summary>
    Timeout,

    /// <summary>The agent answered with something the protocol does not accept.</summary>
    Malformed,
}

/// <summary>
/// Thrown by <see cref="PortalPipeClient.SendAsync"/> when there is no
/// <see cref="PortalResponse"/> to return. The reason is an enum because the
/// portal has fixed text for each and shows nothing else.
/// </summary>
public sealed class PortalUnavailableException(PortalUnavailableReason reason)
    : Exception(reason.ToString())
{
    public PortalUnavailableReason Reason { get; } = reason;
}

/// <summary>
/// The portal side of the pipe: one request per connection -- connect, write one
/// line, read one line, close.
/// </summary>
/// <remarks>
/// <para>
/// Runs as the signed-in user, so the server at the other end is verified before
/// a byte is sent and the answer is parsed strictly after. A server that is not
/// in session 0 is disconnected without being written to or read from;
/// <see cref="CompanyPortalPipe"/> and <see cref="SessionNoticePipe"/> explain
/// why that is the check that can be made from an ordinary user's process, and
/// the one that matters. A squatter therefore never learns what the portal asks,
/// and the portal never shows what a squatter answers.
/// </para>
/// <para>
/// The pipe's name and the trust rule come from <see cref="CompanyPortalPipe"/>,
/// the same type the service uses, so the two ends cannot drift apart.
/// </para>
/// <para>
/// Every bound is enforced here before parsing: the connection wait, the answer
/// wait, and the length of the answer. A line longer than the protocol allows is
/// not an answer this client was written for, and the connection is dropped
/// rather than read further.
/// </para>
/// </remarks>
public sealed class PortalPipeClient
{
    /// <summary>A service that is running accepts at once; three seconds covers a busy machine.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The service answers only after asking the management server, and its
    /// HTTP call may take the full <c>AgentOptions.RequestTimeoutSeconds</c> (30 s)
    /// before it gives up and says ServerUnavailable. This deadline sits above
    /// that, so an unreachable server is reported as unreachable rather than as
    /// an agent that did not answer.
    /// </summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(35);

    private readonly Func<NamedPipeClientStream, SessionNoticePipe.Trust> _evaluate;
    private readonly string _pipeName;

    public PortalPipeClient()
        : this(CompanyPortalPipe.Name, CompanyPortalPipe.EvaluateServer)
    {
    }

    /// <summary>
    /// For tests: a private pipe name, and a trust decision that can stand in for
    /// session 0 when the test's own server necessarily runs in the test's session.
    /// </summary>
    internal PortalPipeClient(string pipeName, Func<NamedPipeClientStream, SessionNoticePipe.Trust> evaluate)
    {
        _pipeName = pipeName;
        _evaluate = evaluate;
    }

    /// <summary>
    /// Sends one request and returns the service's answer, which may itself be an
    /// error the service chose. Throws <see cref="PortalUnavailableException"/>
    /// when there is no answer to return.
    /// </summary>
    public async Task<PortalResponse> SendAsync(PortalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(ConnectTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // No server, or one that will not have us: either way, not the agent as far as this user can tell.
            throw new PortalUnavailableException(PortalUnavailableReason.AgentNotRunning);
        }

        if (_evaluate(client) != SessionNoticePipe.Trust.Trusted)
        {
            // Not the service. Leave without sending or reading anything.
            throw new PortalUnavailableException(PortalUnavailableReason.Untrusted);
        }

        // One deadline covers the write and the read: an agent that will not take
        // the request is no better than one that will not answer it.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ResponseTimeout);

        byte[] line;
        try
        {
            await client.WriteAsync(CompanyPortalProtocol.EncodeRequest(request), deadline.Token);
            await client.FlushAsync(deadline.Token);

            line = await ReadLineAsync(client, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new PortalUnavailableException(PortalUnavailableReason.Timeout);
        }
        catch (IOException)
        {
            throw new PortalUnavailableException(PortalUnavailableReason.AgentNotRunning);
        }

        if (!CompanyPortalProtocol.TryDecodeResponse(line, out var response) || response is null)
        {
            throw new PortalUnavailableException(PortalUnavailableReason.Malformed);
        }

        return response;
    }

    /// <summary>
    /// Reads up to the first newline, which is the whole answer. The buffer is one
    /// byte larger than the longest line the protocol allows, so an overlong line
    /// is detected as such rather than truncated into something that might parse.
    /// </summary>
    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[CompanyPortalProtocol.MaxResponseBytes + 1];
        var filled = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                // The service closed the pipe without finishing a line.
                throw new PortalUnavailableException(PortalUnavailableReason.Malformed);
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', filled, read);
            filled += read;

            if (newline >= 0)
            {
                return buffer[..newline];
            }

            if (filled > CompanyPortalProtocol.MaxResponseBytes)
            {
                throw new PortalUnavailableException(PortalUnavailableReason.Malformed);
            }

            if (filled == buffer.Length)
            {
                throw new PortalUnavailableException(PortalUnavailableReason.Malformed);
            }
        }
    }
}
