using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using EndpointAgent.Core.ScreenLock;
using EndpointAgent.Windows.SessionNotice;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.ScreenLock;

/// <summary>
/// Keeps the lock overlay running in every signed-in session while the device is
/// locked, and takes them all down on release, by starting and stopping one fixed
/// image in each interactive session from the LocalSystem service.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it can start is fixed.</b> The one image name
/// (<see cref="ImageName"/>), resolved inside the service's own directory, with no
/// arguments; the overlay reads what to show from the service over the pipe, never
/// from a command line. The start goes through <see cref="WindowsSessionProcessHost"/>
/// -- the one place in the agent that creates a process -- exactly as the session
/// notifier's does, and ADR-0005's screen-lock amendment records why a second
/// service-launched image is within the rule.
/// </para>
/// <para>
/// <b>The watchdog.</b> <see cref="EnsureOverlayInAllSessions"/> starts the overlay
/// in any session whose overlay is gone -- so killing the overlay process brings it
/// straight back on the next tick. A session that already has a live overlay is
/// left alone; the overlay's own per-session mutex is the final guarantee that
/// there is never more than one, even if a start races a still-living copy.
/// </para>
/// <para>
/// <b>Stopping.</b> <see cref="StopAllOverlays"/> ends every overlay this device is
/// running: the ones this launcher started, and -- belt-and-braces against a start
/// that raced a release -- any other process of the overlay's image in an
/// interactive session. Terminating it releases its input hooks and its
/// <c>BlockInput</c> with it, so the desktop comes back at once.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ScreenLockOverlayLauncher : IScreenLockLauncher
{
    /// <summary>The only thing this launcher starts, reviewed alongside the notifier in AgentSafetyTests.</summary>
    internal const string ImageName = "EndpointAgent.LockScreen.exe";

    private readonly ISessionProcessHost _host;
    private readonly string _installDirectory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, int> _running = [];
    private bool _missingImageLogged;

    public ScreenLockOverlayLauncher(ILogger<ScreenLockOverlayLauncher> logger)
        : this(new WindowsSessionProcessHost(), AppContext.BaseDirectory, logger)
    {
    }

    /// <summary>For tests: a stand-in host, and a directory that may or may not hold the overlay.</summary>
    internal ScreenLockOverlayLauncher(ISessionProcessHost host, string installDirectory, ILogger logger)
    {
        _host = host;
        _installDirectory = installDirectory;
        _logger = logger;
    }

    /// <summary>The overlay's path: the fixed name, beside the service.</summary>
    public string ImagePath => Path.Combine(_installDirectory, ImageName);

    /// <summary>The image name without extension, for matching a running process.</summary>
    private static string ProcessName => Path.GetFileNameWithoutExtension(ImageName);

    /// <inheritdoc />
    public IReadOnlyList<uint> EnsureOverlayInAllSessions()
    {
        var started = new List<uint>();

        if (!File.Exists(ImagePath))
        {
            if (!_missingImageLogged)
            {
                _missingImageLogged = true;
                _logger.LogError(
                    "The lock overlay {Image} is not present beside the service; a locked device would show nothing. " +
                    "The lock is still enforced (input blocked), but with no on-screen explanation.", ImagePath);
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
            _logger.LogWarning(ex, "Could not list interactive sessions; no lock overlay was started this pass.");
            return started;
        }

        foreach (var session in sessions.Distinct())
        {
            if (session == SessionNoticePipe.ServiceSessionId || HasLiveOverlay(session))
            {
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
                started.Add(session);
                _logger.LogInformation("Lock overlay started in session {Session} (process {Pid}).", session, processId);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "The lock overlay could not be started in session {Session}.", session);
            }
        }

        return started;
    }

    /// <inheritdoc />
    public void StopAllOverlays()
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

        // Belt-and-braces: an overlay started in the instant between a release and
        // this call might not be tracked. Sweep every process of the overlay's
        // image, image-guarded, so none is left holding the desktop.
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
            _logger.LogWarning(ex, "Could not enumerate lock-overlay processes to stop them.");
        }
    }

    private bool HasLiveOverlay(uint session)
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
            // A dead overlay (killed by a user, crashed) is gone and must be
            // restarted. A PID reused by a different image is not our overlay.
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
            // No such process any more.
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

            // Image guard against PID reuse: only ever terminate the overlay.
            if (!string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            process.Kill();
            _logger.LogInformation("Lock overlay process {Pid} stopped on release.", process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(ex, "A lock overlay process could not be stopped; it will end on its next poll or at sign-out.");
        }
    }
}
