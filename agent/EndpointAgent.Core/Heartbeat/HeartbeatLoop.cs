using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Enrollment;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Heartbeat;

/// <summary>
/// The agent's main loop: ensure enrolled, then heartbeat forever.
/// </summary>
/// <remarks>
/// <para>
/// Failure policy, from least to most severe:
/// transient errors back off exponentially with jitter (30s .. 5min) so a fleet
/// does not synchronise into a thundering herd against a recovering server;
/// a 401 discards the credential and re-runs enrollment (which only proceeds if
/// a token is configured); a machine with no identity and no token parks itself
/// and re-checks every 5 minutes so an operator can supply a token without a
/// restart being timing-sensitive.
/// </para>
/// <para>
/// The server's heartbeat response carries the interval to use next, so cadence
/// is tunable fleet-wide without touching endpoints.
/// </para>
/// </remarks>
public sealed class HeartbeatLoop(
    AgentEnrollmentManager enrollmentManager,
    IAgentApiClient apiClient,
    ISystemInfoProvider systemInfoProvider,
    IInventoryCollector inventoryCollector,
    EndpointAgent.Core.Tasks.TaskDispatcher taskDispatcher,
    EndpointAgent.Core.Policies.PolicyRunner policyRunner,
    EndpointAgent.Core.BitLocker.AutomaticEscrowRunner escrowRunner,
    IOptions<AgentOptions> agentOptions,
    IOptions<EnrollmentOptions> enrollmentOptions,
    TimeProvider timeProvider,
    ILogger<HeartbeatLoop> logger)
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ParkedRecheckInterval = TimeSpan.FromMinutes(5);

    private readonly AgentEnrollmentManager _enrollmentManager = enrollmentManager
        ?? throw new ArgumentNullException(nameof(enrollmentManager));

    private readonly IAgentApiClient _apiClient = apiClient
        ?? throw new ArgumentNullException(nameof(apiClient));

    private readonly ISystemInfoProvider _systemInfoProvider = systemInfoProvider
        ?? throw new ArgumentNullException(nameof(systemInfoProvider));

    private readonly IInventoryCollector _inventoryCollector = inventoryCollector
        ?? throw new ArgumentNullException(nameof(inventoryCollector));

    private readonly EndpointAgent.Core.Tasks.TaskDispatcher _taskDispatcher = taskDispatcher
        ?? throw new ArgumentNullException(nameof(taskDispatcher));

    private readonly EndpointAgent.Core.BitLocker.AutomaticEscrowRunner _escrowRunner = escrowRunner
        ?? throw new ArgumentNullException(nameof(escrowRunner));

    private readonly EndpointAgent.Core.Policies.PolicyRunner _policyRunner = policyRunner
        ?? throw new ArgumentNullException(nameof(policyRunner));

    private readonly AgentOptions _agentOptions = agentOptions?.Value
        ?? throw new ArgumentNullException(nameof(agentOptions));

    private readonly EnrollmentOptions _enrollmentOptions = enrollmentOptions?.Value
        ?? throw new ArgumentNullException(nameof(enrollmentOptions));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<HeartbeatLoop> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>The agent build version reported to the server.</summary>
    public string AgentVersion { get; init; } = Core.AgentVersion.Current;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            DeviceCredential? credential;

            try
            {
                credential = await _enrollmentManager.EnsureEnrolledAsync(
                    _enrollmentOptions.Token,
                    AgentVersion,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (credential is null)
            {
                await DelayAsync(ParkedRecheckInterval, stoppingToken);
                continue;
            }

            // The token has served its purpose; drop it from memory so nothing can
            // log or reuse it for the remainder of the process lifetime.
            _enrollmentOptions.Token = null;

            var interval = TimeSpan.FromSeconds(_agentOptions.HeartbeatIntervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                var sentAt = _timeProvider.GetUtcNow();
                var request = new HeartbeatRequest(
                    _systemInfoProvider.GetHostName(),
                    AgentVersion,
                    await _systemInfoProvider.GetOperatingSystemDescriptionAsync(stoppingToken),
                    sentAt,
                    BootTime(sentAt));

                AgentApiResult<HeartbeatResponse> result;

                try
                {
                    result = await _apiClient.HeartbeatAsync(request, credential, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (result.IsSuccess)
                {
                    consecutiveFailures = 0;
                    var response = result.Value!;

                    // Adopt the server-directed cadence.
                    if (response.HeartbeatIntervalSeconds is >= 15 and <= 3600)
                    {
                        interval = TimeSpan.FromSeconds(response.HeartbeatIntervalSeconds);
                    }

                    LogClockSkew(request.AgentTimestamp, response.ServerTime);

                    if (response.InventoryRequested)
                    {
                        await UploadInventoryAsync(credential, stoppingToken);
                    }

                    if (response.TasksPending)
                    {
                        await RunTasksAsync(credential, stoppingToken);
                    }

                    if (response.PoliciesPending)
                    {
                        await RunPoliciesAsync(credential, stoppingToken);
                    }

                    // Automatic recovery-password escrow, on every successful
                    // heartbeat rather than only when inventory is requested.
                    //
                    // The retry schedule lives on the server, so this has to tick
                    // often enough for a one-minute backoff to be honoured -- but
                    // it costs one GET, and that GET is what decides whether
                    // anything happens at all. A device with nothing owed does no
                    // work and never touches BitLocker.
                    await RunAutomaticEscrowAsync(credential, stoppingToken);

                    await DelayAsync(interval, stoppingToken);
                    continue;
                }

                if (result.Status == AgentApiStatus.Unauthorized)
                {
                    await _enrollmentManager.DiscardRejectedCredentialAsync(stoppingToken);
                    break; // Outer loop re-runs enrollment.
                }

                consecutiveFailures++;
                var backoff = ComputeBackoff(consecutiveFailures);

                _logger.LogWarning(
                    "Heartbeat attempt failed ({Status}); {Failures} consecutive failure(s), retrying in {Backoff}.",
                    result.Status,
                    consecutiveFailures,
                    backoff);

                await DelayAsync(backoff, stoppingToken);
            }
        }
    }

    /// <summary>Exponential backoff with full jitter, clamped to [30s, 5min].</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        var exponent = Math.Min(consecutiveFailures, 6); // 2^6 * 30s well past the cap
        var ceilingTicks = Math.Min(
            MaxBackoff.Ticks,
            MinBackoff.Ticks * (1L << exponent));

        // Full jitter: uniform in [MinBackoff, ceiling]. Random.Shared is fine -
        // this is scheduling, not cryptography.
        var jitteredTicks = Random.Shared.NextInt64(MinBackoff.Ticks, ceilingTicks + 1);
        return TimeSpan.FromTicks(jitteredTicks);
    }

    /// <summary>
    /// Collects and uploads a full inventory snapshot. Failure is non-fatal: the
    /// server keeps the request pending, so the next heartbeat retries naturally.
    /// </summary>
    /// <summary>
    /// Claims and runs any queued tasks. Failure is non-fatal: unclaimed tasks stay
    /// queued and the next heartbeat retries, and a per-task failure is reported to
    /// the server rather than crashing the loop.
    /// </summary>
    private async Task RunPoliciesAsync(DeviceCredential credential, CancellationToken stoppingToken)
    {
        try
        {
            await _policyRunner.RunAsync(credential, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Policy evaluation failed; it will be retried on the next heartbeat.");
        }
    }

    private async Task RunTasksAsync(DeviceCredential credential, CancellationToken stoppingToken)
    {
        try
        {
            await _taskDispatcher.RunPendingAsync(credential, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task dispatch failed; queued tasks will be retried on the next heartbeat.");
        }
    }

    /// <summary>
    /// Runs one automatic recovery-password escrow pass.
    /// </summary>
    /// <remarks>
    /// Isolated like the other per-tick activities: an escrow failure must never
    /// stop heartbeats, because a device that stops checking in is a much worse
    /// outcome than one whose key is not yet filed.
    /// </remarks>
    private async Task RunAutomaticEscrowAsync(DeviceCredential credential, CancellationToken stoppingToken)
    {
        try
        {
            await _escrowRunner.RunAsync(credential, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception is logged, not its contents interpreted. Nothing on
            // this path carries a recovery password: the gate returns categories
            // and envelopes, never a plaintext value.
            _logger.LogError(ex, "Automatic recovery-key escrow failed; it will be retried on schedule.");
        }
    }

    private async Task UploadInventoryAsync(DeviceCredential credential, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Server requested an inventory upload; collecting.");

            var report = await _inventoryCollector.CollectAsync(stoppingToken);
            var result = await _apiClient.UploadInventoryAsync(report, credential, stoppingToken);

            if (result.IsSuccess)
            {
                _logger.LogInformation("Inventory uploaded successfully.");
            }
            else
            {
                _logger.LogWarning(
                    "Inventory upload was not accepted ({Status}); the server will re-request it.",
                    result.Status);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Collector faults are already isolated per fact; this is the last
            // line of defence so an unexpected failure cannot kill the loop.
            _logger.LogError(ex, "Inventory collection failed; will retry on the next server request.");
        }
    }

    /// <summary>
    /// When the machine last started: now minus the system tick count, which
    /// Windows resets at every boot and keeps counting through sleep. Rounded
    /// to the second, so it reads the same on every heartbeat of one boot.
    /// </summary>
    private static DateTimeOffset BootTime(DateTimeOffset now)
    {
        var booted = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new DateTimeOffset(booted.Ticks - booted.Ticks % TimeSpan.TicksPerSecond, booted.Offset);
    }

    private void LogClockSkew(DateTimeOffset agentTime, DateTimeOffset serverTime)
    {
        var skew = (agentTime - serverTime).Duration();

        if (skew > TimeSpan.FromMinutes(2))
        {
            _logger.LogWarning(
                "This machine's clock differs from the server by {Skew}. Large skew breaks TLS and "
                + "Kerberos before it breaks this agent - investigate time sync.",
                skew);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
