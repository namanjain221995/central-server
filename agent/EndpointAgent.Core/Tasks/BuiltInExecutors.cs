using System.ComponentModel;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>Benign executor that proves the pipeline end to end.</summary>
public sealed class PingTaskExecutor : ITaskExecutor
{
    public string TaskType => "Ping";

    public Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentTaskResult(true, "pong", null));
}

/// <summary>Collects and uploads a fresh inventory in response to a task.</summary>
public sealed class RefreshInventoryTaskExecutor(
    IInventoryCollector collector,
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore) : ITaskExecutor
{
    private readonly IInventoryCollector _collector = collector;
    private readonly IAgentApiClient _apiClient = apiClient;
    private readonly IDeviceCredentialStore _credentialStore = credentialStore;

    public string TaskType => "RefreshInventory";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return new AgentTaskResult(false, "No device credential available.", null);
        }

        var report = await _collector.CollectAsync(cancellationToken);
        var result = await _apiClient.UploadInventoryAsync(report, credential, cancellationToken);

        return result.IsSuccess
            ? new AgentTaskResult(true, "Inventory uploaded.", null)
            : new AgentTaskResult(false, $"Inventory upload failed ({result.Status}).", null);
    }
}

/// <summary>Base for the power/session control executors; parses the shared payload.</summary>
public abstract class DeviceControlTaskExecutor(IDeviceControl deviceControl, ILogger logger) : ITaskExecutor
{
    protected IDeviceControl DeviceControl { get; } = deviceControl;
    protected ILogger Logger { get; } = logger;

    public abstract string TaskType { get; }

    public abstract Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default);

    /// <summary>Parses the grace/message payload; defaults to a 30s grace when absent.</summary>
    protected static (int GraceSeconds, string? Message) ParseGrace(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return (30, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var grace = root.TryGetProperty("graceSeconds", out var g) && g.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 0, 3600)
                : 30;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (grace, message);
        }
        catch (JsonException)
        {
            return (30, null);
        }
    }
}

/// <summary>
/// Restarts the device after the grace period the task names, through the
/// Windows shutdown API -- which owns the countdown from the moment this
/// returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The countdown starts here, on the device, when the task is executed.</b>
/// That is the whole timer: <c>InitiateSystemShutdownEx</c> is handed the grace
/// period and Windows counts it down itself, surviving anything that happens to
/// this process in between. The agent never sleeps on a timer of its own -- a
/// second scheduler would have to agree with the first, and would be lost if
/// the service restarted mid-wait. The result is reported as soon as Windows has
/// accepted the request, which is before the restart happens; "Succeeded" means
/// scheduled and accepted, never "the machine has restarted", and the message
/// says exactly when Windows will act.
/// </para>
/// <para>
/// <b>Three refusals, all honest.</b> A task whose server deadline has passed
/// is refused without touching the machine: the server never hands out an
/// expired task, so reaching this means a long stall or a skewed clock, and a
/// restart nobody is expecting is worse than one that has to be re-issued. A
/// Windows refusal is reported as the failure it is, with the Win32 error --
/// never as success because the call was attempted. And a restart or shutdown
/// already in progress (<c>ERROR_SHUTDOWN_IN_PROGRESS</c>) is a distinct
/// failure, because the timing this task asked for was not applied.
/// </para>
/// </remarks>
public sealed class RestartTaskExecutor(
    IDeviceControl deviceControl,
    ILogger<RestartTaskExecutor> logger,
    TimeProvider? timeProvider = null,
    IRestartNotifier? notifier = null)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    /// <summary>Windows: a system shutdown has already been scheduled.</summary>
    internal const int ErrorShutdownInProgress = 1115;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IRestartNotifier _notifier = notifier ?? NullRestartNotifier.Instance;

    public override string TaskType => "RestartDevice";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();

        if (task.ExpiresAt is { } deadline && now >= deadline)
        {
            Logger.LogWarning(
                "Restart task {TaskId} refused: its deadline {Deadline:u} passed before execution (now {Now:u}).",
                task.TaskId, deadline, now);
            return new AgentTaskResult(
                false,
                $"Restart refused: the task expired at {deadline:u} before the device executed it; nothing was restarted.",
                ResultJson(0, null, "Expired"));
        }

        var (grace, message) = ParseGrace(task.PayloadJson);

        try
        {
            await DeviceControl.RestartAsync(grace, message, cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorShutdownInProgress)
        {
            Logger.LogWarning(
                "Restart task {TaskId} not applied: a restart or shutdown is already in progress on this device.",
                task.TaskId);
            return new AgentTaskResult(
                false,
                "Restart not applied: a restart or shutdown is already in progress on this device.",
                ResultJson(grace, null, "AlreadyInProgress", ex.NativeErrorCode));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // The API said no. Reporting the attempt as success would tell an
            // operator the machine is about to restart when Windows has already
            // declined to do so.
            Logger.LogError(ex, "Restart task {TaskId} failed: Windows refused the shutdown request.", task.TaskId);
            var code = (ex as Win32Exception)?.NativeErrorCode;
            return new AgentTaskResult(
                false,
                $"Restart failed: {ex.Message.Trim().TrimEnd('.')}.",
                ResultJson(grace, null, "Failed", code));
        }

        // Windows counts the grace period itself from the moment the call
        // returned, so the time it will act is this, not anything the server
        // computed when the task was queued.
        var restartAt = now.AddSeconds(grace);

        Logger.LogWarning(
            "Restart task {TaskId} accepted by Windows: the device restarts at {RestartAt:u} ({Grace}s grace).",
            task.TaskId, restartAt, grace);

        // Only now, with the restart accepted, is there anything true to tell the
        // user -- and only the time. The notice is a courtesy on top of Windows'
        // own warning: if it cannot be delivered the restart still happens and is
        // still reported exactly as it is, so a failure here is logged and nothing
        // more.
        try
        {
            _notifier.RestartScheduled(new SessionNotice.RestartNotice(restartAt, grace));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Restart task {TaskId}: the session notice could not be sent.", task.TaskId);
        }

        return new AgentTaskResult(
            true,
            grace == 0
                ? "Restart accepted by Windows: the device is restarting now."
                : $"Restart accepted by Windows: the device restarts at {restartAt:u}, {grace}s from when it received the task.",
            ResultJson(grace, restartAt, "Scheduled"));
    }

    /// <summary>
    /// The structured result the console reads: what was asked for, when
    /// Windows will act, and which of the three outcomes this was.
    /// </summary>
    private static string ResultJson(int graceSeconds, DateTimeOffset? restartAt, string outcome, int? code = null) =>
        JsonSerializer.Serialize(new
        {
            graceSeconds,
            restartAt,
            outcome,
            code,
        });
}

public sealed class ShutdownTaskExecutor(IDeviceControl deviceControl, ILogger<ShutdownTaskExecutor> logger)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    public override string TaskType => "ShutdownDevice";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var (grace, message) = ParseGrace(task.PayloadJson);
        await DeviceControl.ShutdownAsync(grace, message, cancellationToken);
        return new AgentTaskResult(true, $"Shutdown scheduled in {grace}s.", null);
    }
}

public sealed class LockTaskExecutor(IDeviceControl deviceControl, ILogger<LockTaskExecutor> logger)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    public override string TaskType => "LockDevice";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        await DeviceControl.LockAsync(cancellationToken);
        return new AgentTaskResult(true, "Workstation locked.", null);
    }
}

public sealed class SignOutTaskExecutor(IDeviceControl deviceControl, ILogger<SignOutTaskExecutor> logger)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    public override string TaskType => "SignOutUser";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        await DeviceControl.SignOutAsync(cancellationToken);
        return new AgentTaskResult(true, "Interactive user signed out.", null);
    }
}

/// <summary>Starts/stops/restarts a Windows service in response to a ControlService task.</summary>
public sealed class ControlServiceTaskExecutor(
    IServiceProcessControl control,
    Microsoft.Extensions.Logging.ILogger<ControlServiceTaskExecutor> logger) : ITaskExecutor
{
    public string TaskType => "ControlService";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task.PayloadJson))
        {
            return new AgentTaskResult(false, "Missing service-control payload.", null);
        }

        string serviceName;
        string action;
        try
        {
            using var doc = JsonDocument.Parse(task.PayloadJson);
            serviceName = doc.RootElement.GetProperty("serviceName").GetString() ?? "";
            action = doc.RootElement.GetProperty("action").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new AgentTaskResult(false, "Malformed service-control payload.", null);
        }

        switch (action)
        {
            case "Start": await control.StartServiceAsync(serviceName, cancellationToken); break;
            case "Stop": await control.StopServiceAsync(serviceName, cancellationToken); break;
            case "Restart": await control.RestartServiceAsync(serviceName, cancellationToken); break;
            default: return new AgentTaskResult(false, $"Unknown service action '{action}'.", null);
        }

        logger.LogInformation("Service {Service} {Action} completed.", serviceName, action);
        return new AgentTaskResult(true, $"Service '{serviceName}' {action.ToLowerInvariant()} completed.", null);
    }
}

/// <summary>Terminates a process (with an expected-image guard) for a TerminateProcess task.</summary>
public sealed class TerminateProcessTaskExecutor(
    IServiceProcessControl control,
    Microsoft.Extensions.Logging.ILogger<TerminateProcessTaskExecutor> logger) : ITaskExecutor
{
    public string TaskType => "TerminateProcess";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task.PayloadJson))
        {
            return new AgentTaskResult(false, "Missing terminate-process payload.", null);
        }

        int pid;
        string expectedImage;
        try
        {
            using var doc = JsonDocument.Parse(task.PayloadJson);
            pid = doc.RootElement.GetProperty("processId").GetInt32();
            expectedImage = doc.RootElement.GetProperty("expectedImageName").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new AgentTaskResult(false, "Malformed terminate-process payload.", null);
        }

        await control.TerminateProcessAsync(pid, expectedImage, cancellationToken);
        logger.LogInformation("Process {Pid} ({Image}) terminated.", pid, expectedImage);
        return new AgentTaskResult(true, $"Process {pid} ({expectedImage}) terminated.", null);
    }
}
