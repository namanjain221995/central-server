using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

public sealed class TaskDispatcherTests
{
    private static readonly DeviceCredential Credential =
        new(Guid.CreateVersion7(), new string('a', 32), new string('b', 64));

    [Fact]
    public async Task A_claimed_task_is_executed_and_its_result_reported()
    {
        var api = new FakeApi([new AgentTask(Guid.CreateVersion7(), "Ping", null)]);
        var dispatcher = new TaskDispatcher(api, [new PingTaskExecutor()], NullLogger<TaskDispatcher>.Instance);

        await dispatcher.RunPendingAsync(Credential);

        api.Results.Count.ShouldBe(1);
        api.Results[0].Result.Succeeded.ShouldBeTrue();
        api.Results[0].Result.Message.ShouldBe("pong");
    }

    [Fact]
    public async Task An_unknown_task_type_is_reported_as_failed_not_thrown()
    {
        var api = new FakeApi([new AgentTask(Guid.CreateVersion7(), "MysteryTask", null)]);
        var dispatcher = new TaskDispatcher(api, [new PingTaskExecutor()], NullLogger<TaskDispatcher>.Instance);

        await dispatcher.RunPendingAsync(Credential);

        api.Results.Single().Result.Succeeded.ShouldBeFalse();
        api.Results.Single().Result.Message!.ShouldContain("Unsupported");
    }

    [Fact]
    public async Task A_throwing_executor_is_reported_as_a_failure_and_does_not_block_others()
    {
        var t1 = Guid.CreateVersion7();
        var t2 = Guid.CreateVersion7();
        var api = new FakeApi([new AgentTask(t1, "Boom", null), new AgentTask(t2, "Ping", null)]);
        var dispatcher = new TaskDispatcher(
            api, [new ThrowingExecutor(), new PingTaskExecutor()], NullLogger<TaskDispatcher>.Instance);

        await dispatcher.RunPendingAsync(Credential);

        api.Results.Count.ShouldBe(2);
        api.Results.Single(r => r.TaskId == t1).Result.Succeeded.ShouldBeFalse();
        api.Results.Single(r => r.TaskId == t2).Result.Succeeded.ShouldBeTrue("the good task still runs");
    }

    [Fact]
    public async Task No_tasks_means_no_result_calls()
    {
        var api = new FakeApi([]);
        var dispatcher = new TaskDispatcher(api, [new PingTaskExecutor()], NullLogger<TaskDispatcher>.Instance);

        await dispatcher.RunPendingAsync(Credential);

        api.Results.ShouldBeEmpty();
    }

    private sealed class ThrowingExecutor : ITaskExecutor
    {
        public string TaskType => "Boom";
        public Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class FakeApi(IReadOnlyList<AgentTask> tasks) : IAgentApiClient
    {
        // Automatic escrow is not exercised by these tests. Throwing rather than
        // returning a bland success keeps them honest: if one of them ever starts
        // reaching this path, it fails loudly instead of quietly appearing to escrow.
        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.BitLockerEscrowStatusResponse>>
            GetBitLockerEscrowStatusAsync(DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.CatalogListResponse>> GetCatalogAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<byte[]>> GetCatalogIconAsync(
            Guid appId, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.ChatPollResponse>> PollChatMessagesAsync(
            Guid sessionId, int afterSeq, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<Unit>> SendChatMessageAsync(
            Guid sessionId, string body, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.CatalogInstallResponse>> RequestCatalogInstallAsync(
            Guid appId, EndpointPlatform.Contracts.Agent.CatalogInstallCommand command, DeviceCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyResponse>>
            EscrowRecoveryKeyAsync(
                EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyRequest request,
                DeviceCredential credential,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(
            EnrollmentRequestRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<EnrollmentRequestResponse>.Success(
                new EnrollmentRequestResponse("pending", 30)));

        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(
            EnrollmentClaimRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<EnrollmentClaimResponse>.Success(
                new EnrollmentClaimResponse("pending", null, null, null, false, 30)));

        public List<(Guid TaskId, AgentTaskResult Result)> Results { get; } = [];

        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(

            UsbReport report, DeviceCredential credential, CancellationToken cancellationToken = default) =>

            Task.FromResult(AgentApiResult<UsbPolicyResponse>.Transient());


        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<AgentTaskListResponse>.Success(new AgentTaskListResponse(tasks)));

        public Task<AgentApiResult<Unit>> PostTaskResultAsync(
            Guid taskId, AgentTaskResult result, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Results.Add((taskId, result));
            return Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));
        }

        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(EnrollRequest r, CancellationToken c = default) =>
            throw new NotSupportedException();
        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest r, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(InventoryReport r, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(AgentPolicyComplianceReport r, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<Unit>> DownloadPackageAsync(Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(
            Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(string secretReference, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.AgentUpdateInfo>> GetAgentUpdateInfoAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(Guid releaseId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
