using System.IO.Pipes;
using EndpointAgent.Core.WorkTime;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.Windows.WorkTime;

/// <summary>
/// The in-session side of the activity pipe: sends one sample and reads the service's
/// ack. It refuses to send to anything but the service (session 0), and treats a failed
/// send as "not delivered" so the sampler keeps the sample and retries.
/// </summary>
public sealed class ActivityPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);

    private readonly string _pipeName;

    public ActivityPipeClient(string? pipeName = null) => _pipeName = pipeName ?? ActivityPipe.Name;

    /// <summary>Sends one sample. True when the service acked it; false when it could not be delivered.</summary>
    public async Task<bool> ReportAsync(long atEpochSeconds, int idleSeconds, CancellationToken cancellationToken)
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
            return false;
        }

        if (ActivityPipe.EvaluateServer(client) != SessionNoticePipe.Trust.Trusted)
        {
            return false;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ResponseTimeout);

        try
        {
            await client.WriteAsync(ActivityPipeProtocol.EncodeRequest(atEpochSeconds, idleSeconds), deadline.Token);
            await client.FlushAsync(deadline.Token);
            var line = await ReadLineAsync(client, deadline.Token);
            return ActivityPipeProtocol.TryDecodeAck(line);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ActivityPipeProtocol.MaxResponseBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, 1), cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer[filled] == (byte)'\n')
            {
                return buffer[..filled];
            }
            filled += read;
        }
        return buffer[..filled];
    }
}
