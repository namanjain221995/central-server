namespace EndpointAgent.LiveChat;

/// <summary>
/// The live IT-support chat window: one per signed-in user, started by the
/// LocalSystem agent service while a chat is open.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. It asks the service, over a named pipe, for the
/// administrator's display name and the messages, and whether the chat is still
/// open; it draws them as plain text and sends back only what the person types.
/// Everything that decides what is trusted, or what the words mean, lives in the
/// tested libraries; this process draws the window.
/// </para>
/// <para>
/// It takes no arguments and reads none for WHAT to show. Whoever starts it, it
/// does the same thing: contact the service and show the chat.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// <c>Local\</c> scopes the mutex to this session: one chat window per user, and
    /// a second copy started in the same session simply exits.
    /// </summary>
    private const string SingleInstanceMutex = @"Local\EndpointPlatformAgent.LiveChat";

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var form = new ChatWindow();
        Application.Run(form);
        return 0;
    }
}
