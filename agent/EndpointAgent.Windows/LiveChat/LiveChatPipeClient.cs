using System.IO.Pipes;
using EndpointAgent.Core.Chat;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.Windows.LiveChat;

/// <summary>
/// The chat window's side of the live-chat pipe: one request per connection --
/// connect, verify the server, write one line, read one line, close.
/// </summary>
/// <remarks>
/// <para>
/// Runs as the signed-in user, so the server at the other end is verified before a
/// byte is sent and the answer is parsed strictly after. A server not in session 0
/// is disconnected without being written to or read from, so a squatter never
/// learns what the person types and the window never shows a squatter's words
/// (<see cref="LiveChatPipe"/> and <see cref="SessionNoticePipe"/> explain why that
/// is the check that can be made from a user's process, and the one that matters).
/// </para>
/// <para>
/// Every bound is enforced here before parsing: the connect wait, the answer wait,
/// and the length of the answer. A <c>null</c> poll answer means the service could
/// not be reached or trusted; the window treats a run of those as "the service is
/// gone" and closes, so there is never an orphan window after the agent stops.
/// </para>
/// </remarks>
public sealed class LiveChatPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<NamedPipeClientStream, SessionNoticePipe.Trust> _evaluate;

    public LiveChatPipeClient()
        : this(LiveChatPipe.Name, LiveChatPipe.EvaluateServer)
    {
    }

    /// <summary>For tests: a private pipe name and a trust decision that can stand in for session 0.</summary>
    internal LiveChatPipeClient(string pipeName, Func<NamedPipeClientStream, SessionNoticePipe.Trust> evaluate)
    {
        _pipeName = pipeName;
        _evaluate = evaluate;
    }

    /// <summary>
    /// Asks the service for the messages after <paramref name="afterSeq"/> and
    /// whether the chat is still open, or null when the service could not be
    /// reached, is not trusted, or did not answer something the protocol accepts.
    /// </summary>
    public Task<ChatPipeResponse?> PollAsync(int afterSeq, CancellationToken cancellationToken) =>
        ExchangeAsync(ChatPipeRequest.Poll(afterSeq), cancellationToken);

    /// <summary>
    /// Sends a line the person typed. True when the server took it, false when it
    /// refused, null when the service could not be reached or did not answer.
    /// </summary>
    public async Task<bool?> SendAsync(string body, CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(ChatPipeRequest.Send(body), cancellationToken);
        return response?.Accepted;
    }

    /// <summary>Tells the service the window is closing. Best-effort; a failure is ignored.</summary>
    public async Task ReportClosedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExchangeAsync(ChatPipeRequest.Closed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<ChatPipeResponse?> ExchangeAsync(ChatPipeRequest request, CancellationToken cancellationToken)
    {
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
            return null;
        }

        if (_evaluate(client) != SessionNoticePipe.Trust.Trusted)
        {
            // Not the service. Leave without sending or reading anything.
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ResponseTimeout);

        byte[] line;
        try
        {
            await client.WriteAsync(LiveChatPipeProtocol.EncodeRequest(request), deadline.Token);
            await client.FlushAsync(deadline.Token);
            line = await ReadLineAsync(client, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return null;
        }

        return LiveChatPipeProtocol.TryDecodeResponse(line, out var response) ? response : null;
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[LiveChatPipeProtocol.MaxResponseBytes + 1];
        var filled = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                // Closed without a complete line; a decode of what we have will fail.
                return buffer[..filled];
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', filled, read);
            filled += read;

            if (newline >= 0)
            {
                return buffer[..newline];
            }

            if (filled >= buffer.Length)
            {
                // Longer than any answer the protocol allows: not an answer we were written for.
                return buffer;
            }
        }
    }
}
