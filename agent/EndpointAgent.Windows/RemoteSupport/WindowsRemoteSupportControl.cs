using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.RemoteSupport;

/// <summary>
/// Windows side of IT remote support: configures an already-installed RustDesk with
/// a platform-managed unattended password by running <c>rustdesk.exe --password</c>
/// through a one-shot Task Scheduler entry.
/// </summary>
/// <remarks>
/// <para>
/// This is the same sanctioned mechanism the agent self-update uses (see
/// <see cref="WindowsAgentUpdateLauncher"/>): the agent registers a Task Scheduler
/// entry and the Task Scheduler service -- not the agent -- launches the binary as
/// SYSTEM. The agent itself starts nothing (ADR-0005 upheld). RustDesk is not hosted
/// in a user session; it runs as its own service installed by its MSI, so this slice
/// only sets its password and never adds a launched image to the session host.
/// </para>
/// <para>
/// Unlike the self-update entry, which is time-triggered and left to run detached,
/// this entry is run on demand and waited on: the agent starts it, polls for
/// completion within a bounded timeout, reads the exit code, and deletes the entry.
/// Setting an unattended password is quick and the result matters to the operator,
/// so the task reports true only when RustDesk exited cleanly.
/// </para>
/// <para>
/// The password is a runtime argument, redeemed from a one-time secret, exactly as
/// the self-update launcher passes msiexec arguments built from agent-authored
/// values -- never a free dashboard string. It is validated (non-empty, no control
/// characters) before use and is never written to a log.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteSupportControl(ILogger<WindowsRemoteSupportControl> logger) : IRemoteSupportControl
{
    private const string TaskName = "EndpointPlatformRemoteSupportConfig";

    /// <summary>Overall bound on how long the agent waits for RustDesk to finish.</summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How often the task state is polled while waiting.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    // Task Scheduler state/result constants (COM, late-bound).
    private const int TaskStateQueued = 2;
    private const int TaskStateRunning = 4;
    private const int TaskSchedTaskHasNotRun = 0x00041303; // SCHED_S_TASK_HAS_NOT_RUN

    private readonly ILogger<WindowsRemoteSupportControl> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public Task<RemoteSupportConfigureResult> SetUnattendedPasswordAsync(string password, CancellationToken ct)
    {
        // The password is a runtime argument; it must be a value that can be a single
        // argument safely. Non-empty and free of control characters, nothing more --
        // the platform generates it, it is not a free-form string.
        if (string.IsNullOrEmpty(password) || password.Any(char.IsControl))
        {
            return Task.FromResult(new RemoteSupportConfigureResult(
                false, "The remote support password was empty or contained control characters; nothing was changed."));
        }

        var rustDeskPath = LocateRustDesk();
        if (rustDeskPath is null)
        {
            return Task.FromResult(new RemoteSupportConfigureResult(false, "RustDesk is not installed"));
        }

        // The COM work is synchronous and polls for completion; run it off the caller's
        // thread so the heartbeat loop is not blocked, and honour cancellation in the
        // poll loop.
        return Task.Run(() => Configure(rustDeskPath, password, ct), ct);
    }

    /// <summary>
    /// <c>%ProgramFiles%\RustDesk\rustdesk.exe</c> or
    /// <c>%ProgramFiles(x86)%\RustDesk\rustdesk.exe</c>, or null when neither exists.
    /// </summary>
    private static string? LocateRustDesk()
    {
        foreach (var root in new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
        })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "RustDesk", "rustdesk.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private RemoteSupportConfigureResult Configure(string rustDeskPath, string password, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Quoted so a password with spaces survives as a single argument. The value
        // is never logged.
        var arguments = $"--password \"{password}\"";

        // Task Scheduler COM (Schedule.Service). Late-bound, exactly as the self-update
        // launcher does it: the interop assemblies are not shipped, and the object
        // model has been stable since Vista.
        var scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)
            ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");

        try
        {
            dynamic service = scheduler;
            service.Connect();

            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description =
                "One-shot Endpoint Platform remote-support configuration, registered by the agent.";

            // Principal: SYSTEM at the highest run level. The account is also set by
            // RegisterTaskDefinition below; the run level is set here on the definition.
            definition.Principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST

            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            // The entry is deleted right after it runs; this bound cleans it up even if
            // deletion is interrupted, and caps how long RustDesk may run.
            definition.Settings.DeleteExpiredTaskAfter = "PT0S";
            definition.Settings.ExecutionTimeLimit = "PT2M";

            dynamic action = definition.Actions.Create(0 /* TASK_ACTION_EXEC */);
            action.Path = rustDeskPath;
            action.Arguments = arguments;

            dynamic folder = service.GetFolder("\\");
            dynamic registered = folder.RegisterTaskDefinition(
                TaskName,
                definition,
                6 /* TASK_CREATE_OR_UPDATE */,
                "SYSTEM",
                null,
                5 /* TASK_LOGON_SERVICE_ACCOUNT */,
                null);

            try
            {
                // On-demand start, then wait for it to finish.
                registered.Run(null);
                _logger.LogInformation("Remote-support configuration started via Task Scheduler.");

                return WaitForCompletion(registered, ct);
            }
            finally
            {
                TryDeleteTask(folder);
            }
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(scheduler))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(scheduler);
            }
        }
    }

    /// <summary>
    /// Polls the registered task until it is no longer running, within the timeout,
    /// then maps its last result to an outcome.
    /// </summary>
    private RemoteSupportConfigureResult WaitForCompletion(dynamic registered, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + CompletionTimeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            int state = (int)registered.State;
            if (state is TaskStateRunning or TaskStateQueued)
            {
                Thread.Sleep(PollInterval);
                continue;
            }

            int lastResult = (int)registered.LastTaskResult;

            // Ready again but not yet actually run (the on-demand start has not taken
            // effect): keep waiting, still bounded by the overall deadline.
            if (lastResult == TaskSchedTaskHasNotRun)
            {
                Thread.Sleep(PollInterval);
                continue;
            }

            if (lastResult == 0)
            {
                _logger.LogInformation("Remote-support unattended access configured.");
                return new RemoteSupportConfigureResult(true, "Remote support access configured.");
            }

            _logger.LogWarning("RustDesk configuration exited with code 0x{Code:X8}.", lastResult);
            return new RemoteSupportConfigureResult(
                false, $"RustDesk exited with code 0x{lastResult:X8}; the unattended password may not have been set.");
        }

        _logger.LogWarning("Remote-support configuration did not finish within {Timeout}.", CompletionTimeout);
        return new RemoteSupportConfigureResult(
            false, $"RustDesk did not finish configuring within {CompletionTimeout.TotalSeconds:N0} seconds; nothing was confirmed.");
    }

    private void TryDeleteTask(dynamic folder)
    {
        try
        {
            folder.DeleteTask(TaskName, 0);
        }
        catch (Exception ex)
        {
            // Best-effort: the entry is set to self-delete, so a failure here is logged
            // and tolerated rather than failing the whole operation.
            _logger.LogWarning(ex, "Could not delete the one-shot remote-support task '{TaskName}'.", TaskName);
        }
    }
}
