namespace EndpointAgent.CompanyPortal;

/// <summary>
/// The Company Portal: the window a signed-in user opens to see the apps
/// approved for this device and ask for one to be installed.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. The pipe client (<see cref="PortalPipeClient"/>) connects
/// to the service, verifies it and exchanges one line each way; the form
/// (<see cref="MainForm"/>) shows what came back. The wire format and its strict
/// parser live in the tested Core library, not here.
/// </para>
/// <para>
/// It takes no arguments and reads none: however it is started, it does the same
/// thing. It has no privileges beyond the user it runs as, holds no credential,
/// and never reaches the server -- the service does that, for this device only.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// <c>Local\</c> scopes the mutex to this session: one portal per user, and a
    /// second copy started in the same session simply exits.
    /// </summary>
    private const string SingleInstanceMutex = @"Local\EndpointPlatformAgent.CompanyPortal";

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        Application.Run(form);
        return 0;
    }
}
