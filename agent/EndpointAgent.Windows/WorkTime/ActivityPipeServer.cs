using System.IO.Pipes;
using EndpointAgent.Core.WorkTime;
using EndpointAgent.Windows.CompanyPortal;
using EndpointAgent.Windows.ScreenLock;
using EndpointAgent.Windows.SessionNotice;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.WorkTime;

/// <summary>
/// The service side of the activity pipe: it accepts one sample at a time from an
/// in-session sampler, attributes it to that session's signed-in user, and spools it
/// durably for upload. It treats every byte from the user's process as hostile — the
/// caller's session comes from the pipe driver, not the message.
/// </summary>
public sealed class ActivityPipeServer(
    IActivitySpool spool,
    TimeProvider timeProvider,
    ILogger<ActivityPipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A sample must be close to now: a little future skew, not too old.</summary>
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(8);

    private readonly IActivitySpool _spool = spool;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ActivityPipeServer> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateInstance();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "The activity pipe name is in use or could not be created; retrying.");
                await DelayAsync(RetryDelay, stoppingToken);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await ServeAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex) when (ex is IOException)
            {
                _logger.LogDebug(ex, "An activity pipe connection faulted.");
            }
            finally
            {
                pipe.Dispose();
            }
        }
    }

    private static NamedPipeServerStream CreateInstance() =>
        NamedPipeServerStreamAcl.Create(
            ActivityPipe.Name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: ActivityPipeProtocol.MaxRequestBytes * 2,
            outBufferSize: ActivityPipeProtocol.MaxResponseBytes,
            ScreenLockPipeServer.BuildSecurity());

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(ServeTimeout);

        var sessionId = SessionNoticePipe.ClientSessionId(pipe.SafePipeHandle);

        var line = await ReadLineAsync(pipe, deadline.Token);
        if (!ActivityPipeProtocol.TryDecodeRequest(line, out var atEpochSeconds, out var idleSeconds))
        {
            return;
        }

        if (sessionId is { } session)
        {
            var userName = SessionUser.NameOf(session);
            var sampledAt = DateTimeOffset.FromUnixTimeSeconds(atEpochSeconds);
            var now = _timeProvider.GetUtcNow();

            if (!string.IsNullOrWhiteSpace(userName)
                && sampledAt <= now + MaxFutureSkew
                && sampledAt >= now - MaxAge)
            {
                var minute = new DateTimeOffset(
                    sampledAt.Year, sampledAt.Month, sampledAt.Day, sampledAt.Hour, sampledAt.Minute, 0, TimeSpan.Zero);
                await _spool.AppendAsync(new ActivitySampleDto(userName!, minute, idleSeconds), stoppingToken);
            }
        }

        try
        {
            await pipe.WriteAsync(ActivityPipeProtocol.EncodeAck(), deadline.Token);
            await pipe.FlushAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The sampler will resend; the sample is already spooled.
            _logger.LogDebug(ex, "Could not ack an activity sample.");
        }
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ActivityPipeProtocol.MaxRequestBytes];
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

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
