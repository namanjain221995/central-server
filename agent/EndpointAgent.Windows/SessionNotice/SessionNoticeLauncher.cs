using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.SessionNotice;

/// <summary>
/// Makes sure every signed-in user has a session notifier, by starting one in
/// each interactive session that has none connected.
/// </summary>
/// <remarks>
/// <para>
/// Windows starts the notifier at sign-in, from the machine Run key. That leaves
/// the user who is <em>already</em> signed in when the agent is installed, or
/// updated, or when its service restarts: until their next sign-in nothing would
/// be there to show a restart. So the service calls this when it starts, and again
/// whenever it has a restart to announce, for any session still without one.
/// </para>
/// <para>
/// <b>What it can start is fixed.</b> The one image name, resolved inside the
/// service's own directory -- Program Files, administrator-only -- with no
/// arguments; there is no parameter through which one could be given. See
/// <see cref="WindowsSessionProcessHost"/> for the call itself and why it is
/// within ADR-0005.
/// </para>
/// <para>
/// <b>One per session.</b> A session that already has a notifier connected is left
/// alone. If two are ever started in the same session -- the Run key and this
/// launcher racing at sign-in, say -- the second finds the session mutex held and
/// exits at once, so the outcome is still one.
/// </para>
/// <para>
/// A failure to start one is logged and changes nothing else: the restart that
/// prompted it, and Windows' own shutdown warning, are unaffected.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionNoticeLauncher
{
    /// <summary>The only thing this launcher starts.</summary>
    internal const string ImageName = "EndpointAgent.SessionNotice.exe";

    private readonly ISessionProcessHost _host;
    private readonly string _installDirectory;
    private readonly ILogger _logger;
    private bool _missingImageLogged;

    public SessionNoticeLauncher(ILogger<SessionNoticeLauncher> logger)
        : this(new WindowsSessionProcessHost(), AppContext.BaseDirectory, logger)
    {
    }

    /// <summary>For tests: a stand-in host, and a directory that may or may not hold the notifier.</summary>
    internal SessionNoticeLauncher(ISessionProcessHost host, string installDirectory, ILogger logger)
    {
        _host = host;
        _installDirectory = installDirectory;
        _logger = logger;
    }

    /// <summary>The notifier's path: the fixed name, beside the service.</summary>
    public string ImagePath => Path.Combine(_installDirectory, ImageName);

    /// <summary>
    /// Starts the notifier in every interactive session not in
    /// <paramref name="sessionsWithNotifier"/>. Never session 0. Returns the
    /// sessions a notifier was started in.
    /// </summary>
    public IReadOnlyList<uint> StartWhereMissing(IReadOnlySet<uint> sessionsWithNotifier)
    {
        ArgumentNullException.ThrowIfNull(sessionsWithNotifier);

        var started = new List<uint>();

        if (!File.Exists(ImagePath))
        {
            // A developer run of the service without a published notifier. Said once.
            if (!_missingImageLogged)
            {
                _missingImageLogged = true;
                _logger.LogWarning(
                    "The session notifier {Image} is not present beside the service; signed-in users will see only " +
                    "Windows' own shutdown warning.", ImagePath);
            }

            return started;
        }

        IReadOnlyList<uint> sessions;
        try
        {
            sessions = _host.InteractiveSessions();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not list interactive sessions; no session notifier was started.");
            return started;
        }

        foreach (var session in sessions.Distinct())
        {
            if (session == SessionNoticePipe.ServiceSessionId || sessionsWithNotifier.Contains(session))
            {
                continue;
            }

            try
            {
                var processId = _host.StartInSession(session, ImagePath, _installDirectory);
                if (processId is null)
                {
                    _logger.LogDebug("Session {Session} has nobody signed in; no notifier started.", session);
                    continue;
                }

                started.Add(session);
                _logger.LogInformation("Session notifier started in session {Session} (process {Pid}).", session, processId);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "The session notifier could not be started in session {Session}.", session);
            }
        }

        return started;
    }
}
