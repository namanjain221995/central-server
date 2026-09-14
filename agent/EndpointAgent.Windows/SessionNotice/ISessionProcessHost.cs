namespace EndpointAgent.Windows.SessionNotice;

/// <summary>
/// The two Windows calls behind starting the notifier in a signed-in user's
/// session, behind one seam so the launcher's rules can be tested without
/// running as SYSTEM.
/// </summary>
internal interface ISessionProcessHost
{
    /// <summary>
    /// Sessions that may have a signed-in user: every session Windows lists as
    /// active or disconnected. Never session 0, which holds services only.
    /// </summary>
    IReadOnlyList<uint> InteractiveSessions();

    /// <summary>
    /// Starts the executable at <paramref name="imagePath"/>, with no arguments,
    /// in <paramref name="sessionId"/> as the user signed in there.
    /// </summary>
    /// <returns>The new process id, or null when nobody is signed in to that session.</returns>
    int? StartInSession(uint sessionId, string imagePath, string workingDirectory);
}
