using System.IO.Pipes;
using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.Windows.SessionNotice;

/// <summary>
/// The session side of the pipe: connects, verifies the server, and keeps the
/// latest restart notice it has been told about.
/// </summary>
/// <remarks>
/// <para>
/// Runs as the signed-in user, so everything it reads is treated as hostile
/// until the server has been verified, and parsed strictly after. A server that
/// is not in session 0 is disconnected without a single byte being read from it;
/// <see cref="SessionNoticePipe"/> explains why that is the check that can be
/// made, and the one that matters.
/// </para>
/// <para>
/// It only ever records data. There is no code path from anything received to
/// anything executed, opened or displayed as text: a notice is a time and a grace
/// period, and the words the user sees are constants.
/// </para>
/// </remarks>
public sealed class SessionNoticeReader
{
    /// <summary>How far past the longest possible grace period a notice may claim to be.</summary>
    internal static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefusedDelay = TimeSpan.FromSeconds(30);

    private readonly string _pipeName;
    private readonly Func<NamedPipeClientStream, SessionNoticePipe.Trust> _evaluate;
    private readonly TimeProvider _time;
    private RestartNotice? _latest;
    private int _lastTrust = NoTrustYet;

    private const int NoTrustYet = -1;

    public SessionNoticeReader()
        : this(SessionNoticePipe.Name, SessionNoticePipe.EvaluateServer, TimeProvider.System)
    {
    }

    /// <summary>
    /// For tests: a private pipe name, and a trust decision that can stand in for
    /// session 0 when the test's own server necessarily runs in the test's session.
    /// </summary>
    internal SessionNoticeReader(
        string pipeName, Func<NamedPipeClientStream, SessionNoticePipe.Trust> evaluate, TimeProvider time)
    {
        _pipeName = pipeName;
        _evaluate = evaluate;
        _time = time;
    }

    /// <summary>The most recent plausible notice from a trusted server, or null.</summary>
    public RestartNotice? Latest => Volatile.Read(ref _latest);

    /// <summary>How the last server was judged. For diagnostics and tests.</summary>
    public SessionNoticePipe.Trust? LastTrust =>
        Volatile.Read(ref _lastTrust) is var value and not NoTrustYet ? (SessionNoticePipe.Trust)value : null;

    /// <summary>Connects, verifies and reads until cancelled, reconnecting as the service comes and goes.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = ReconnectDelay;
            try
            {
                await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await client.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken);

                var trust = _evaluate(client);
                Volatile.Write(ref _lastTrust, (int)trust);

                if (trust != SessionNoticePipe.Trust.Trusted)
                {
                    // Not the service. Leave without reading anything it offers.
                    delay = RefusedDelay;
                }
                else
                {
                    await ReadAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Service not running, restarting, or the pipe went away.
            }

            try
            {
                await Task.Delay(delay, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads newline-terminated notices. A line longer than the protocol allows
    /// ends the connection: that is not a sender this reader should keep listening to.
    /// </summary>
    internal async Task ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[RestartNoticeProtocol.MaxLineBytes + 1];
        var filled = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                return;
            }

            filled += read;

            int newline;
            while ((newline = Array.IndexOf(buffer, (byte)'\n', 0, filled)) >= 0)
            {
                Accept(buffer.AsSpan(0, newline));

                var rest = filled - newline - 1;
                Array.Copy(buffer, newline + 1, buffer, 0, rest);
                filled = rest;
            }

            if (filled > RestartNoticeProtocol.MaxLineBytes)
            {
                return;
            }
        }
    }

    private void Accept(ReadOnlySpan<byte> line)
    {
        if (RestartNoticeProtocol.TryDecode(line, out var notice)
            && notice is not null
            && IsPlausible(notice, _time.GetUtcNow()))
        {
            Volatile.Write(ref _latest, notice);
        }
    }

    /// <summary>
    /// A notice can only describe a restart that could really be pending: not one
    /// further ahead than the longest grace period the agent ever schedules, and
    /// not one so far past that it would never be shown.
    /// </summary>
    internal static bool IsPlausible(RestartNotice notice, DateTimeOffset now) =>
        notice.RestartAt <= now + TimeSpan.FromSeconds(RestartNoticeProtocol.MaxGraceSeconds) + FutureSkew
        && notice.RestartAt >= now - RestartNoticeView.LingerAfterRestart;
}
