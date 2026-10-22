using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using EndpointAgent.Core.Chat;
using EndpointAgent.Windows.SessionNotice;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.LiveChat;

/// <summary>
/// Shows the chat window in every signed-in session while a chat is open, and
/// takes them all down when it ends, by starting and stopping one fixed image in
/// each interactive session from the LocalSystem service.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it can start is fixed.</b> The one image name
/// (<see cref="ImageName"/>), resolved inside the service's own directory, with no
/// arguments; the window reads the session id, the admin name and the messages from
/// the service over the pipe, never from a command line. The start goes through
/// <see cref="WindowsSessionProcessHost"/> -- the one place in the agent that
/// creates a process -- exactly as the notifier's and the lock overlay's do, and
/// ADR-0005's live-chat amendment records why a third service-launched image is
/// within the rule.
/// </para>
/// <para>
/// <b>Reports who can see it.</b> <see cref="EnsureWindowInAllSessions"/> starts the
/// window in any session whose window is gone, and returns every interactive
/// session that now has one -- newly started or already running -- so the manager
/// can tell the administrator whether anyone was signed in to see it. A session that
/// already has a live window is left alone; the window's own per-session mutex is
/// the final guarantee that there is never more than one.
/// </para>
/// <para>
/// <b>Stopping.</b> <see cref="StopAllWindows"/> ends every chat window this device
/// is running: the ones this launcher started, and -- belt-and-braces against a
/// start that raced an end -- any other process of the window's image in an
/// interactive session.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LiveChatWindowLauncher : ILiveChatWindowLauncher
{
    /// <summary>The only thing this launcher starts, reviewed alongside the notifier and overlay in AgentSafetyTests.</summary>
    internal const string ImageName = "EndpointAgent.LiveChat.exe";

    private readonly ISessionProcessHost _host;
    private readonly string _installDirectory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, int> _running = [];
    private bool _missingImageLogged;

    public LiveChatWindowLauncher(ILogger<LiveChatWindowLauncher> logger)
        : this(new WindowsSessionProcessHost(), AppContext.BaseDirectory, logger)
    {
    }

    /// <summary>For tests: a stand-in host, and a directory that may or may not hold the window.</summary>
    internal LiveChatWindowLauncher(ISessionProcessHost host, string installDirectory, ILogger logger)
    {
        _host = host;
        _installDirectory = installDirectory;
        _logger = logger;
    }

    /// <summary>The window's path: the fixed name, beside the service.</summary>
    public string ImagePath => Path.Combine(_installDirectory, ImageName);

    /// <summary>The image name without extension, for matching a running process.</summary>
    private static string ProcessName => Path.GetFileNameWithoutExtension(ImageName);

    /// <inheritdoc />
    public IReadOnlyList<uint> EnsureWindowInAllSessions()
    {
        var covered = new List<uint>();

        if (!File.Exists(ImagePath))
        {
            if (!_missingImageLogged)
            {
                _missingImageLogged = true;
                _logger.LogError(
                    "The chat window {Image} is not present beside the service; a chat cannot be shown on this device.", ImagePath);
            }

            return covered;
        }

        IReadOnlyList<uint> sessions;
        try
        {
            sessions = _host.InteractiveSessions();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not list interactive sessions; no chat window was started this pass.");
            return covered;
        }

        foreach (var session in sessions.Distinct())
        {
            if (session == SessionNoticePipe.ServiceSessionId)
            {
                continue;
            }

            if (HasLiveWindow(session))
            {
                // Already showing in this session; it still counts as covered.
                covered.Add(session);
                continue;
            }

            try
            {
                var processId = _host.StartInSession(session, ImagePath, _installDirectory);
                if (processId is null)
                {
                    // Nobody signed in to that session right now.
                    Forget(session);
                    continue;
                }

                Remember(session, processId.Value);
                covered.Add(session);
                _logger.LogInformation("Chat window started in session {Session} (process {Pid}).", session, processId);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "The chat window could not be started in session {Session}.", session);
            }
        }

        return covered;
    }

    /// <inheritdoc />
    public void StopAllWindows()
    {
        List<int> tracked;
        lock (_gate)
        {
            tracked = [.. _running.Values];
            _running.Clear();
        }

        foreach (var pid in tracked)
        {
            TryKill(pid);
        }

        // Belt-and-braces: a window started in the instant between an end and this
        // call might not be tracked. Sweep every process of the window's image,
        // image-guarded, so none is left as an orphan.
        try
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    TryKill(process);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _logger.LogWarning(ex, "Could not enumerate chat-window processes to stop them.");
        }
    }

    private bool HasLiveWindow(uint session)
    {
        int pid;
        lock (_gate)
        {
            if (!_running.TryGetValue(session, out pid))
            {
                return false;
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited
                || !string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)
                || (uint)process.SessionId != session)
            {
                Forget(session);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            Forget(session);
            return false;
        }
    }

    private void Remember(uint session, int pid)
    {
        lock (_gate)
        {
            _running[session] = pid;
        }
    }

    private void Forget(uint session)
    {
        lock (_gate)
        {
            _running.Remove(session);
        }
    }

    private void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            TryKill(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            // Image guard against PID reuse: only ever terminate the chat window.
            if (!string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            process.Kill();
            _logger.LogInformation("Chat window process {Pid} stopped.", process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(ex, "A chat window process could not be stopped; it will end on its next poll.");
        }
    }
}
