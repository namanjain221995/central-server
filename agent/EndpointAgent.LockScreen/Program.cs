namespace EndpointAgent.LockScreen;

/// <summary>
/// The screen-lock overlay: one per signed-in user, started by the LocalSystem
/// agent service while the device is locked.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. It asks the service, over a named pipe, what to show and
/// whether the device is still locked, and it blocks input until the service says
/// the lock has been released -- either by a server task or by the correct unlock
/// code an IT operator types here after pressing Ctrl+Alt+U. Everything that
/// decides what is trusted, or what the words mean, lives in the tested libraries;
/// this process draws the overlay and runs the input hooks.
/// </para>
/// <para>
/// It takes no arguments and reads none for WHAT to show. Whoever starts it, it
/// does the same thing: contact the service and lock the screen.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// <c>Local\</c> scopes the mutex to this session: one overlay per user, and a
    /// second copy the watchdog started in the same session simply exits. This is
    /// the final guarantee that there is never more than one overlay in a session.
    /// </summary>
    private const string SingleInstanceMutex = @"Local\EndpointPlatformAgent.LockScreen";

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var controller = new LockOverlayController();
        controller.Run();
        return 0;
    }
}
