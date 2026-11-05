using System.Runtime.InteropServices;

namespace EndpointAgent.Windows.WorkTime;

/// <summary>
/// Runs inside the signed-in user's session and, once a minute, measures how long since
/// the last keyboard or mouse input (never what it was) and sends it to the service.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>GetLastInputInfo</c> — a presence reading, not a hook: it captures no
/// keystrokes, no mouse positions, no content. It must run in-session because a service
/// in session 0 cannot see the interactive session's input.
/// </para>
/// <para>
/// Nothing is lost if the service is briefly down (its own upgrade): unsent samples are
/// kept, each with its real minute, and resent on the next tick. This process outlives a
/// service restart, so its small buffer bridges the gap; the service then spools to disk.
/// </para>
/// </remarks>
public sealed class SessionActivitySampler
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>At most this many unsent minutes are held in memory (~3 days for one user).</summary>
    private const int MaxBuffered = 3 * 24 * 60;

    private readonly ActivityPipeClient _client;
    private readonly TimeProvider _time;
    private readonly Queue<(long At, int Idle)> _unsent = new();

    public SessionActivitySampler(ActivityPipeClient? client = null, TimeProvider? timeProvider = null)
    {
        _client = client ?? new ActivityPipeClient();
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var minute = (_time.GetUtcNow().ToUnixTimeSeconds() / 60) * 60;
                Enqueue(minute, CurrentIdleSeconds());
                await DrainAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Best-effort telemetry: a bad tick must never stop the sampler.
            }
        }
    }

    private void Enqueue(long at, int idleSeconds)
    {
        if (_unsent.Count >= MaxBuffered)
        {
            _unsent.Dequeue();
        }
        _unsent.Enqueue((at, idleSeconds));
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (_unsent.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var (at, idle) = _unsent.Peek();
            if (await _client.ReportAsync(at, idle, cancellationToken))
            {
                _unsent.Dequeue();
            }
            else
            {
                break; // the service is unreachable; keep everything and retry next tick
            }
        }
    }

    private static int CurrentIdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        // 32-bit tick arithmetic, unchecked, so the ~49-day wrap is handled.
        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.dwTime);
        return (int)(idleMilliseconds / 1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);
}
