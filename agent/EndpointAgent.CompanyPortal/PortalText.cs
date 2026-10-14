namespace EndpointAgent.CompanyPortal;

/// <summary>
/// Every word the portal shows, in one place.
/// </summary>
/// <remarks>
/// Nothing from the wire is displayed except an app's name, publisher, version,
/// category and description, and those only as the text of a label that treats
/// them as text. Every status, button and message is one of these constants,
/// chosen by a code path, never by a string that arrived over the pipe.
/// </remarks>
internal static class PortalText
{
    public const string Title = "Company Portal";
    public const string Subtitle = "Apps approved for this device";
    public const string SearchPlaceholder = "Search apps";
    public const string Refresh = "Refresh";

    /// <summary>The heading for apps the catalog gave no category.</summary>
    public const string OtherCategory = "Other";

    public const string Loading = "Loading apps…";
    public const string Empty = "No apps have been published for this device yet.";
    public const string NoMatches = "No apps match your search.";

    public const string Install = "Install";
    public const string Update = "Update";
    public const string Installing = "Installing…";
    public const string Installed = "Installed";

    /// <summary>A state this build does not know. The button is shown, but it does nothing.</summary>
    public const string Unavailable = "Unavailable";

    public const string Connected = "Connected to the company agent.";
    public const string AgentNotRunning = "The company agent is not running on this PC.";
    public const string NotEnrolled = "This device is not enrolled yet.";
    public const string ServerUnavailable = "The company server is not reachable right now.";
    public const string TooManyRequests = "Too many requests — try again in a minute.";
    public const string AgentUntrusted = "The company agent could not be verified.";
    public const string AgentTimeout = "The company agent did not answer in time.";
    public const string AgentUnexpected = "The company agent gave an unexpected answer.";

    public const string InstallStarted = "Installation started. It usually takes a minute or two.";
    public const string InstallInProgress = "This app is already being installed.";
    public const string Refused = "This app is not available for your device.";
}
