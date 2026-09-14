using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.SessionNotice;

/// <summary>
/// The session notifier: one per signed-in user, started by Windows at sign-in.
/// </summary>
/// <remarks>
/// Deliberately small. The reader (<see cref="SessionNoticeReader"/>) connects to
/// the service, verifies it and keeps the latest notice; the window
/// (<see cref="NoticeWindow"/>) shows it. Everything that decides what is trusted
/// or what is shown lives in the tested libraries, not here.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// <c>Local\</c> scopes the mutex to this session: one notifier per user, and a
    /// second copy started in the same session simply exits.
    /// </summary>
    private const string SingleInstanceMutex = @"Local\EndpointPlatformAgent.SessionNotice";

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        using var stop = new CancellationTokenSource();
        var reader = new SessionNoticeReader();

        // The reader runs on the thread pool; the window owns this thread and its
        // message loop, and polls the reader's latest notice on a timer. Nothing
        // crosses back from the reader into window code.
        var reading = Task.Run(() => reader.RunAsync(stop.Token));

        var exitCode = NoticeWindow.Run(() => reader.Latest);

        stop.Cancel();
        try
        {
            reading.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        return exitCode;
    }
}
