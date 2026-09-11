using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// The process-stopping step behind Force Stop: enumerate every process now,
/// match the ones under the application's install directory, terminate them
/// through the guarded control, and report what happened.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="StopApplicationExecutor"/> because deciding which
/// pids belong to an application is a different job from deciding what the
/// answer means, and it is the half worth testing on its own.
/// </para>
/// <para>
/// This decides <em>which</em> pids; it does not add a way to kill. Termination
/// goes through <see cref="IServiceProcessControl"/>, so the refusal to touch
/// pids 0 and 4 and the image-name re-check at kill time both still apply. The
/// executor decides what the report means: "not running" is the desired state,
/// and "matched, but nothing could be stopped" is a failure.
/// </para>
/// </remarks>
public sealed class ApplicationStopper(
    IServiceProcessCollector collector,
    IServiceProcessControl control,
    ILogger logger)
{
    /// <summary>
    /// Stops every process of the application installed under
    /// <paramref name="installLocation"/>, which the caller has already found
    /// usable with <see cref="ApplicationProcessMatcher.CanResolve"/>.
    /// </summary>
    public async Task<ApplicationStopReport> StopAsync(
        string applicationName, string installLocation, CancellationToken cancellationToken = default)
    {
        // Every process, not the inventory summary. The summary is the largest
        // few hundred by working set, and a helper that fell below that line
        // would be left running while the application was reported stopped --
        // which is what a 10,000 "limit" against a 500-capped method silently
        // allowed until it was measured on a machine with 541 processes.
        var running = await collector.CollectAllProcessesAsync(cancellationToken);

        var matches = ApplicationProcessMatcher.Match(
            installLocation,
            running.Select(p => new RunningProcess(p.ProcessId, p.Name, p.ExecutablePath)),
            protectedDirectory: AppContext.BaseDirectory);

        // What was seen and what was chosen, before anything is terminated. An
        // investigation of "stopped, but still running" needs exactly these two
        // facts and nothing had recorded them. Pids and image names only -- the
        // same as the per-kill entries -- never executable paths.
        logger.LogInformation(
            "Application {Application}: {Enumerated} process(es) enumerated, {Matched} under the install directory: {Pids}",
            applicationName, running.Count, matches.Count,
            matches.Count == 0 ? "none" : string.Join(", ", matches.Select(m => $"{m.ProcessId} ({m.ImageName})")));

        if (matches.Count == 0)
        {
            return new ApplicationStopReport(running.Count, 0, 0, []);
        }

        var stopped = 0;
        var notTerminated = new List<string>();

        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Terminates every process the application owns. An application
                // is not one process -- a browser is a parent and many children --
                // and stopping only the first would leave it running while
                // reporting success.
                await control.TerminateProcessAsync(match.ProcessId, match.ImageName, cancellationToken);
                stopped++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Expected and benign: the process exited between enumeration and
                // termination, or its pid was reused and the image guard refused.
                // Both mean "not that process any more", not "the machine is
                // broken", so the rest of the application is still stopped.
                logger.LogInformation(
                    "Process {Pid} ({Image}) was not terminated: {Reason}",
                    match.ProcessId, match.ImageName, ex.Message);
                notTerminated.Add(match.ImageName);
            }
        }

        if (stopped > 0)
        {
            logger.LogWarning(
                "Application {Application}: {Stopped} process(es) terminated by an authorized task.",
                applicationName, stopped);
        }

        return new ApplicationStopReport(running.Count, matches.Count, stopped, notTerminated);
    }
}

/// <summary>What the stop step found and did.</summary>
/// <param name="Enumerated">How many processes the machine had when it looked.</param>
/// <param name="Matched">How many of them belonged to the application.</param>
/// <param name="Stopped">How many were terminated.</param>
/// <param name="NotTerminated">
/// Image names of matched processes that had ended, or changed identity, before
/// they could be terminated.
/// </param>
public sealed record ApplicationStopReport(
    int Enumerated, int Matched, int Stopped, IReadOnlyList<string> NotTerminated)
{
    /// <summary>Nothing of the application was running when it looked.</summary>
    public bool WasNotRunning => Matched == 0;
}
