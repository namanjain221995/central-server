using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Tells the signed-in user about a restart the server has scheduled - and about
/// its cancellation - through whatever means the platform provides.
/// </summary>
/// <remarks>
/// A courtesy on top of Windows' own shutdown warning. Nothing in the restart
/// itself depends on it: if a notice cannot be delivered the restart still
/// happens and is still reported exactly as it is.
/// </remarks>
public interface IRestartNotifier
{
    void RestartScheduled(RestartNotice notice);

    /// <summary>
    /// The restart the user was told about will not happen. Shown briefly so a
    /// user who read "restarting in 4 minutes" is not left waiting for it.
    /// </summary>
    void RestartCancelled();
}

public sealed class NullRestartNotifier : IRestartNotifier
{
    public static readonly NullRestartNotifier Instance = new();

    public void RestartScheduled(RestartNotice notice)
    {
    }

    public void RestartCancelled()
    {
    }
}
