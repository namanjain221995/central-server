using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.BitLocker;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Enrollment;
using EndpointAgent.Core.Heartbeat;
using EndpointAgent.Core.Policies;
using EndpointAgent.Core.Tasks;
using EndpointAgent.Core.Tests.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Tests.Heartbeat;

/// <summary>
/// The never-lose-a-result retry channel rides the heartbeat tick, not the task path.
/// The regression that matters: the server's <c>TasksPending</c> counts only Queued
/// tasks, so after a Delivered task's result is lost a quiet device is told "no
/// tasks" forever -- a flush placed behind that flag would never fire. These run the
/// real <see cref="HeartbeatLoop"/> for one tick against fakes.
/// </summary>
public sealed class HeartbeatLoopSpoolTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    // No sealing-key fingerprint: automatic escrow is idle without a request.
    private static readonly DeviceCredential Credential =
        new(Guid.CreateVersion7(), new string('a', 32), new string('b', 64));

    [Fact]
    public async Task A_spooled_result_is_delivered_on_a_tick_that_says_no_tasks_are_pending()
    {
        var spooled = Guid.CreateVersion7();
        var spool = new TaskDispatcherTests.InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(spooled, new AgentTaskResult(true, "Installed 1.", "{}"), Now.AddMinutes(-3)));

        using var stopping = new CancellationTokenSource();
        var api = new FakeApi(new HeartbeatResponse(Now, 60, TasksPending: false))
        {
            // Delivering the spooled result is this tick's last network call; end the loop there.
            OnPost = stopping.Cancel,
        };

        await Loop(api, spool).RunAsync(stopping.Token);

        api.Calls.ShouldBe(["heartbeat", $"post:{spooled}"]);
        spool.Entries.ShouldBeEmpty("the lost result landed with nothing else queued");
    }

    [Fact]
    public async Task Spooled_results_land_before_new_work_is_claimed()
    {
        var spooled = Guid.CreateVersion7();
        var spool = new TaskDispatcherTests.InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(spooled, new AgentTaskResult(true, "pong", null), Now.AddMinutes(-1)));

        using var stopping = new CancellationTokenSource();
        var api = new FakeApi(new HeartbeatResponse(Now, 60, TasksPending: true)) { OnClaim = stopping.Cancel };

        await Loop(api, spool).RunAsync(stopping.Token);

        api.Calls.ShouldBe(["heartbeat", $"post:{spooled}", "claim"]);
    }

    [Fact]
    public async Task An_empty_spool_costs_the_tick_nothing()
    {
        var spool = new TaskDispatcherTests.InMemoryTaskResultSpool();

        using var stopping = new CancellationTokenSource();
        var api = new FakeApi(new HeartbeatResponse(Now, 60, TasksPending: true)) { OnClaim = stopping.Cancel };

        await Loop(api, spool).RunAsync(stopping.Token);

        api.Calls.ShouldBe(["heartbeat", "claim"]);
        spool.LoadCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_spool_never_stops_the_heartbeat_tick()
    {
        using var stopping = new CancellationTokenSource();
        var api = new FakeApi(new HeartbeatResponse(Now, 60, TasksPending: true)) { OnClaim = stopping.Cancel };

        await Loop(api, new ThrowingSpool()).RunAsync(stopping.Token);

        // The spool's failure is logged and the tick carries on.
        api.Calls.ShouldBe(["heartbeat", "claim"]);
    }

    private static HeartbeatLoop Loop(FakeApi api, ITaskResultSpool spool)
    {
        var agentOptions = Options.Create(new AgentOptions { ServerBaseUrl = "https://server.example" });
        var systemInfo = new FakeSystemInfo();

        return new HeartbeatLoop(
            new AgentEnrollmentManager(
                api, new FakeCredentialStore(Credential), new FakeEnrollmentStateStore(), systemInfo,
                agentOptions, NullLogger<AgentEnrollmentManager>.Instance),
            api,
            systemInfo,
            new FakeInventory(),
            new TaskDispatcher(api, [new PingTaskExecutor()], NullLogger<TaskDispatcher>.Instance, spool, TimeProvider.System),
            new PolicyRunner(api, new PolicyEvaluator(new FakeScreenLockReader(), NullLogger<PolicyEvaluator>.Instance), NullLogger<PolicyRunner>.Instance),
            new AutomaticEscrowRunner(api, new AutomaticEscrowGate(new FakeRecoveryReader(), NullLogger<AutomaticEscrowGate>.Instance), NullLogger<AutomaticEscrowRunner>.Instance),
            agentOptions,
            Options.Create(new EnrollmentOptions()),
            TimeProvider.System,
            NullLogger<HeartbeatLoop>.Instance);
    }

    private sealed class ThrowingSpool : ITaskResultSpool
    {
        public bool HasEntries => true;
        public ValueTask SaveAsync(SpooledTaskResult entry, CancellationToken cancellationToken = default) => throw new IOException("disk gone");
        public ValueTask<IReadOnlyList<SpooledTaskResult>> LoadAsync(CancellationToken cancellationToken = default) => throw new IOException("disk gone");
        public ValueTask RemoveAsync(Guid taskId, CancellationToken cancellationToken = default) => throw new IOException("disk gone");
    }

    private sealed class FakeCredentialStore(DeviceCredential credential) : IDeviceCredentialStore
    {
        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<DeviceCredential?>(credential);
        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeEnrollmentStateStore : IEnrollmentStateStore
    {
        public ValueTask<PendingEnrollmentState?> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<PendingEnrollmentState?>(null);
        public ValueTask SaveAsync(PendingEnrollmentState state, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeSystemInfo : ISystemInfoProvider
    {
        public string GetHostName() => "test-device";
        public ValueTask<string> GetOperatingSystemDescriptionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult("Windows 11");
        public ValueTask<string> GetMachineIdentifierAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult("machine-id");
    }

    private sealed class FakeInventory : IInventoryCollector
    {
        public ValueTask<InventoryReport> CollectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeScreenLockReader : IScreenLockPolicyReader
    {
        public ValueTask<int?> GetScreenLockTimeoutSecondsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<int?>(null);
    }

    private sealed class FakeRecoveryReader : IRecoveryPasswordReader
    {
        public Task<RecoveryPasswordReadResult> ReadAsync(string volumeDeviceIdentifier, string keyProtectorId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("escrow must stay idle in these tests");
    }

    /// <summary>Records the order of the calls the tick makes; everything off the tick's path throws.</summary>
    private sealed class FakeApi(HeartbeatResponse heartbeat) : IAgentApiClient
    {
        public List<string> Calls { get; } = [];

        public Action? OnPost { get; init; }

        public Action? OnClaim { get; init; }

        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest request, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Calls.Add("heartbeat");
            return Task.FromResult(AgentApiResult<HeartbeatResponse>.Success(heartbeat));
        }

        public Task<AgentApiResult<Unit>> PostTaskResultAsync(Guid taskId, AgentTaskResult result, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Calls.Add($"post:{taskId}");
            OnPost?.Invoke();
            return Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));
        }

        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Calls.Add("claim");
            OnClaim?.Invoke();
            return Task.FromResult(AgentApiResult<AgentTaskListResponse>.Success(new AgentTaskListResponse([])));
        }

        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(EnrollmentRequestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(EnrollmentClaimRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(InventoryReport report, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(UsbReport report, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(AgentPolicyComplianceReport report, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(string secretReference, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadPackageAsync(Guid packageId, Stream destination, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(Guid packageId, Stream destination, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentUpdateInfo>> GetAgentUpdateInfoAsync(DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(Guid releaseId, Stream destination, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<BitLockerEscrowStatusResponse>> GetBitLockerEscrowStatusAsync(DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EscrowRecoveryKeyResponse>> EscrowRecoveryKeyAsync(EscrowRecoveryKeyRequest request, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<CatalogListResponse>> GetCatalogAsync(DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<byte[]>> GetCatalogIconAsync(Guid appId, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<CatalogInstallResponse>> RequestCatalogInstallAsync(Guid appId, CatalogInstallCommand command, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<ChatPollResponse>> PollChatMessagesAsync(Guid sessionId, int afterSeq, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> SendChatMessageAsync(Guid sessionId, string body, DeviceCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
