using System.IO.Pipes;
using EndpointAgent.Core.ScreenLock;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.Windows.ScreenLock;

/// <summary>
/// The overlay's side of the screen-lock pipe: one request per connection --
/// connect, verify the server, write one line, read one line, close.
/// </summary>
/// <remarks>
/// <para>
/// Runs as the signed-in user, so the server at the other end is verified before a
/// byte is sent and the answer is parsed strictly after. A server not in session 0
/// is disconnected without being written to or read from, so a squatter never
/// learns the code a user types and the overlay never shows a squatter's words
/// (<see cref="ScreenLockPipe"/> and <see cref="SessionNoticePipe"/> explain why
/// that is the check that can be made from a user's process, and the one that
/// matters).
/// </para>
/// <para>
/// Every bound is enforced here before parsing: the connect wait, the answer wait,
/// and the length of the answer.
/// </para>
/// </remarks>
public sealed class ScreenLockPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<NamedPipeClientStream, SessionNoticePipe.Trust> _evaluate;

    public ScreenLockPipeClient()
        : this(ScreenLockPipe.Name, ScreenLockPipe.EvaluateServer)
    {
    }

    /// <summary>For tests: a private pipe name and a trust decision that can stand in for session 0.</summary>
    internal ScreenLockPipeClient(string pipeName, Func<NamedPipeClientStream, SessionNoticePipe.Trust> evaluate)
    {
        _pipeName = pipeName;
        _evaluate = evaluate;
    }

    /// <summary>
    /// Asks the service what to show and whether the device is still locked, or null
    /// when the service could not be reached, is not trusted, or did not answer
    /// something the protocol accepts. The overlay keeps the lock up on a null --
    /// an unreachable service never means "unlocked".
    /// </summary>
    public Task<LockPipeResponse?> ShowAsync(CancellationToken cancellationToken) =>
        ExchangeAsync(LockPipeRequest.Show, cancellationToken);

    /// <summary>
    /// Submits a candidate unlock code. True when the service released the device,
    /// false when it rejected the code, null when the service could not be reached
    /// or did not give a verification answer -- in which case the overlay stays up.
    /// </summary>
    public async Task<bool?> SubmitAsync(string code, CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(LockPipeRequest.SubmitCode(code), cancellationToken);
        return response?.Unlocked;
    }

    private async Task<LockPipeResponse?> ExchangeAsync(LockPipeRequest request, CancellationToken cancellationToken)
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
            await client.WriteAsync(ScreenLockPipeProtocol.EncodeRequest(request), deadline.Token);
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

        return ScreenLockPipeProtocol.TryDecodeResponse(line, out var response) ? response : null;
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ScreenLockPipeProtocol.MaxResponseBytes + 1];
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
