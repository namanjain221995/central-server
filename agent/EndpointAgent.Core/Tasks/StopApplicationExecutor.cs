using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Stops a named installed application, resolving its processes here and now.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this executor is <em>when</em> it looks. The server knows
/// only what inventory last told it, and inventory is collected on request rather
/// than continuously -- a process list measured on this fleet was ninety minutes
/// old. A pid chosen from that is a guess about a machine that has since carried
/// on: the process may have exited, restarted under a new pid, or had its pid
/// reused by something unrelated. Enumerating on the endpoint closes that gap to
/// the width of this method.
/// </para>
/// <para>
/// The task names an application and an install directory, never a pid or an
/// image name. Those are derived here from live state, so nothing upstream --
/// including a browser -- can choose which process gets terminated.
/// </para>
/// <para>
/// Termination itself goes through the same <see cref="IServiceProcessControl"/>
/// used by the existing per-process task, so the refusal to touch pids 0 and 4
/// and the image-name re-check at kill time both still apply. This adds a way to
/// decide <em>which</em> pids; it does not add a way to kill.
/// </para>
/// <para>
/// The looking and the stopping are <see cref="ApplicationStopper"/>. What this
/// executor owns is the meaning of the report: "not running" is the state the
/// operator wanted, and "nothing could be stopped" is a failure.
/// </para>
/// </remarks>
public sealed class StopApplicationExecutor(
    IServiceProcessCollector collector,
    IServiceProcessControl control,
    ILogger<StopApplicationExecutor> logger) : ITaskExecutor
{
    private readonly ApplicationStopper _stopper = new(collector, control, logger);

    public string TaskType => "StopApplication";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task.PayloadJson))
        {
            return new AgentTaskResult(false, "Missing stop-application payload.", null);
        }

        string applicationName;
        string installLocation;
        try
        {
            using var doc = JsonDocument.Parse(task.PayloadJson);
            applicationName = doc.RootElement.GetProperty("applicationName").GetString() ?? "";
            installLocation = doc.RootElement.GetProperty("installLocation").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new AgentTaskResult(false, "Malformed stop-application payload.", null);
        }

        if (string.IsNullOrWhiteSpace(applicationName) || string.IsNullOrWhiteSpace(installLocation))
        {
            return new AgentTaskResult(false, "Stop-application payload is incomplete.", null);
        }

        // Re-checked here rather than trusted from the task. The server applies
        // the same rule, but this is the side that terminates, so this is the
        // side that must be sure.
        if (!ApplicationProcessMatcher.CanResolve(installLocation))
        {
            return new AgentTaskResult(
                false, $"'{applicationName}' has no usable install location; nothing was stopped.", null);
        }

        var report = await _stopper.StopAsync(applicationName, installLocation, cancellationToken);

        if (report.WasNotRunning)
        {
            // Not a failure. The application is installed but not running, which
            // is the state the operator wanted; saying so beats reporting an
            // error they would then investigate.
            return new AgentTaskResult(true, $"'{applicationName}' is not running.", null);
        }

        if (report.Stopped == 0)
        {
            return new AgentTaskResult(
                false,
                $"'{applicationName}' could not be stopped; its processes ended or changed before they could be.",
                null);
        }

        var suffix = report.NotTerminated.Count > 0 ? $" {report.NotTerminated.Count} had already ended." : "";
        return new AgentTaskResult(
            true, $"'{applicationName}' stopped: {report.Stopped} process(es) terminated.{suffix}", null);
    }
}
