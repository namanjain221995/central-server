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

    // ==========================================  1.27.0: never lose a result

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static TaskDispatcher Dispatcher(FakeApi api, InMemoryTaskResultSpool spool) =>
        new(api, [new PingTaskExecutor()], NullLogger<TaskDispatcher>.Instance, spool, new FixedClock(Now));

    [Theory]
    [InlineData(AgentApiStatus.TransientFailure)]
    [InlineData(AgentApiStatus.Unauthorized)]
    public async Task A_result_whose_post_fails_is_spooled_not_lost(AgentApiStatus failure)
    {
        var taskId = Guid.CreateVersion7();
        var api = new FakeApi([new AgentTask(taskId, "Ping", null)]) { PostStatus = _ => failure };
        var spool = new InMemoryTaskResultSpool();

        await Dispatcher(api, spool).RunPendingAsync(Credential);

        var saved = spool.Entries.ShouldHaveSingleItem();
        saved.TaskId.ShouldBe(taskId);
        saved.Result.Message.ShouldBe("pong", "exactly what would have been posted");
        saved.SpooledAt.ShouldBe(Now);
        spool.HasEntries.ShouldBeTrue();
    }

    [Fact]
    public async Task A_result_the_server_answers_404_for_is_not_spooled()
    {
        var api = new FakeApi([new AgentTask(Guid.CreateVersion7(), "Ping", null)]) { PostStatus = _ => AgentApiStatus.NotFound };
        var spool = new InMemoryTaskResultSpool();

        await Dispatcher(api, spool).RunPendingAsync(Credential);

        spool.Entries.ShouldBeEmpty("the server has no such task; there is nobody to deliver it to");
    }

    [Fact]
    public async Task A_result_the_server_rejects_is_not_spooled()
    {
        var api = new FakeApi([new AgentTask(Guid.CreateVersion7(), "Ping", null)]) { PostStatus = _ => AgentApiStatus.Rejected };
        var spool = new InMemoryTaskResultSpool();

        await Dispatcher(api, spool).RunPendingAsync(Credential);

        api.Results.ShouldHaveSingleItem("posted once");
        spool.Entries.ShouldBeEmpty("a 4xx refusal is final; re-posting it every heartbeat for 48h is pointless");
        spool.SaveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_rejected_result_does_not_stop_later_failures_from_spooling()
    {
        var rejected = Guid.CreateVersion7();
        var transient = Guid.CreateVersion7();
        var api = new FakeApi([new AgentTask(rejected, "Ping", null), new AgentTask(transient, "Ping", null)])
        {
            PostStatus = id => id == rejected ? AgentApiStatus.Rejected : AgentApiStatus.TransientFailure,
        };
        var spool = new InMemoryTaskResultSpool();

        await Dispatcher(api, spool).RunPendingAsync(Credential);

        spool.Entries.ShouldHaveSingleItem().TaskId.ShouldBe(transient, "a transient failure still spools exactly as before");
    }

    [Fact]
    public async Task A_delivered_result_is_not_spooled()
    {
        var api = new FakeApi([new AgentTask(Guid.CreateVersion7(), "Ping", null)]);
        var spool = new InMemoryTaskResultSpool();

        await Dispatcher(api, spool).RunPendingAsync(Credential);

        spool.Entries.ShouldBeEmpty();
        spool.SaveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_post_cancelled_by_the_agent_stopping_is_spooled_before_the_cancellation_propagates()
    {
        var taskId = Guid.CreateVersion7();
        using var stopping = new CancellationTokenSource();
        var api = new FakeApi([new AgentTask(taskId, "Ping", null)])
        {
            PostStatus = _ =>
            {
                stopping.Cancel();
                throw new OperationCanceledException(stopping.Token);
            },
        };
        var spool = new InMemoryTaskResultSpool();

        await Should.ThrowAsync<OperationCanceledException>(() => Dispatcher(api, spool).RunPendingAsync(Credential, stopping.Token));

        spool.Entries.ShouldHaveSingleItem().TaskId.ShouldBe(taskId, "an agent update restarts the service; the result must outlive that");
    }

    [Fact]
    public async Task An_empty_spool_flush_is_a_no_op_with_no_request_and_no_read()
    {
        var api = new FakeApi([]);
        var spool = new InMemoryTaskResultSpool();

        await Dispatcher(api, spool).FlushSpooledResultsAsync(Credential);

        api.Results.ShouldBeEmpty();
        spool.LoadCalls.ShouldBe(0, "the in-memory flag answers; the store is not touched");
    }

    [Fact]
    public async Task A_flush_delivers_spooled_results_oldest_first_and_removes_them()
    {
        var older = Guid.CreateVersion7();
        var newer = Guid.CreateVersion7();
        var spool = new InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(newer, new AgentTaskResult(true, "b", null), Now.AddMinutes(-1)));
        await spool.SaveAsync(new SpooledTaskResult(older, new AgentTaskResult(true, "a", "{}"), Now.AddMinutes(-5)));
        var api = new FakeApi([]);

        await Dispatcher(api, spool).FlushSpooledResultsAsync(Credential);

        api.Results.Select(r => r.TaskId).ToArray().ShouldBe([older, newer]);
        api.Results[0].Result.ResultJson.ShouldBe("{}");
        spool.Entries.ShouldBeEmpty();
        spool.HasEntries.ShouldBeFalse();
    }

    [Fact]
    public async Task A_flush_drops_a_result_the_server_answers_404_for()
    {
        var gone = Guid.CreateVersion7();
        var spool = new InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(gone, new AgentTaskResult(true, "x", null), Now.AddMinutes(-1)));
        var api = new FakeApi([]) { PostStatus = _ => AgentApiStatus.NotFound };

        await Dispatcher(api, spool).FlushSpooledResultsAsync(Credential);

        api.Results.ShouldHaveSingleItem();
        spool.Entries.ShouldBeEmpty("404 can never become accepted; keeping it would only take a slot");
    }

    [Fact]
    public async Task A_flush_drops_a_result_older_than_48_hours_without_sending_it()
    {
        var stale = Guid.CreateVersion7();
        var fresh = Guid.CreateVersion7();
        var spool = new InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(stale, new AgentTaskResult(true, "old", null), Now - TimeSpan.FromHours(48) - TimeSpan.FromMinutes(1)));
        await spool.SaveAsync(new SpooledTaskResult(fresh, new AgentTaskResult(true, "new", null), Now - TimeSpan.FromHours(47)));
        var api = new FakeApi([]);

        await Dispatcher(api, spool).FlushSpooledResultsAsync(Credential);

        api.Results.ShouldHaveSingleItem().TaskId.ShouldBe(fresh);
        spool.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transient_failure_keeps_everything_and_stops_the_pass()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var spool = new InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(first, new AgentTaskResult(true, "a", null), Now.AddMinutes(-2)));
        await spool.SaveAsync(new SpooledTaskResult(second, new AgentTaskResult(true, "b", null), Now.AddMinutes(-1)));
        var api = new FakeApi([]) { PostStatus = _ => AgentApiStatus.TransientFailure };

        await Dispatcher(api, spool).FlushSpooledResultsAsync(Credential);

        api.Results.Count.ShouldBe(1, "the server is unreachable; the rest would fail the same way");
        spool.Entries.Count.ShouldBe(2);
        spool.HasEntries.ShouldBeTrue("the next heartbeat tries again");
    }

    [Fact]
    public async Task A_spooled_result_the_server_rejects_is_dropped_and_the_pass_moves_on()
    {
        var refused = Guid.CreateVersion7();
        var accepted = Guid.CreateVersion7();
        var spool = new InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(refused, new AgentTaskResult(true, "a", null), Now.AddMinutes(-2)));
        await spool.SaveAsync(new SpooledTaskResult(accepted, new AgentTaskResult(true, "b", null), Now.AddMinutes(-1)));
        var api = new FakeApi([]) { PostStatus = id => id == refused ? AgentApiStatus.Rejected : AgentApiStatus.Success };

        await Dispatcher(api, spool).FlushSpooledResultsAsync(Credential);

        api.Results.Select(r => r.TaskId).ToArray().ShouldBe([refused, accepted], "the refusal does not stop the pass");
        spool.Entries.ShouldBeEmpty("a 4xx refusal is final; keeping it would re-post it every heartbeat for 48h");
        spool.HasEntries.ShouldBeFalse();
    }

    [Fact]
    public async Task A_spooled_result_the_server_rejects_is_not_posted_again_on_the_next_flush()
    {
        var refused = Guid.CreateVersion7();
        var spool = new InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(refused, new AgentTaskResult(true, "a", null), Now.AddMinutes(-2)));
        var api = new FakeApi([]) { PostStatus = _ => AgentApiStatus.Rejected };
        var dispatcher = Dispatcher(api, spool);

        await dispatcher.FlushSpooledResultsAsync(Credential);
        await dispatcher.FlushSpooledResultsAsync(Credential);

        api.Results.ShouldHaveSingleItem("dropped after the first refusal, not retried every heartbeat");
    }

    [Fact]
    public async Task An_unauthorized_spooled_result_is_kept_for_the_next_heartbeat()
    {
        var waiting = Guid.CreateVersion7();
        var spool = new InMemoryTaskResultSpool();
        await spool.SaveAsync(new SpooledTaskResult(waiting, new AgentTaskResult(true, "a", null), Now.AddMinutes(-2)));
        var api = new FakeApi([]) { PostStatus = _ => AgentApiStatus.Unauthorized };

        await Dispatcher(api, spool).FlushSpooledResultsAsync(Credential);

        spool.Entries.ShouldHaveSingleItem().TaskId.ShouldBe(waiting, "401 means the credential is being replaced, not a final answer");
    }

    [Fact]
    public async Task Without_a_spool_a_failed_post_is_logged_and_dispatch_carries_on()
    {
        var api = new FakeApi([new AgentTask(Guid.CreateVersion7(), "Ping", null), new AgentTask(Guid.CreateVersion7(), "Ping", null)])
        {
            PostStatus = _ => AgentApiStatus.TransientFailure,
        };
        var dispatcher = new TaskDispatcher(api, [new PingTaskExecutor()], NullLogger<TaskDispatcher>.Instance);

        await dispatcher.RunPendingAsync(Credential);
        await dispatcher.FlushSpooledResultsAsync(Credential);

        api.Results.Count.ShouldBe(2, "both tasks still ran and posted once; no spool, no retry");
    }

    /// <summary>The spool contract in memory: oldest first, one entry per task, the in-memory flag.</summary>
    internal sealed class InMemoryTaskResultSpool : ITaskResultSpool
    {
        private readonly List<SpooledTaskResult> _entries = [];

        public IReadOnlyList<SpooledTaskResult> Entries => _entries.OrderBy(e => e.SpooledAt).ToList();

        public int SaveCalls { get; private set; }

        public int LoadCalls { get; private set; }

        public bool HasEntries => _entries.Count > 0;

        public ValueTask SaveAsync(SpooledTaskResult entry, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            _entries.RemoveAll(e => e.TaskId == entry.TaskId);
            _entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SpooledTaskResult>> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return ValueTask.FromResult(Entries);
        }

        public ValueTask RemoveAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            _entries.RemoveAll(e => e.TaskId == taskId);
            return ValueTask.CompletedTask;
        }
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

        /// <summary>What each result post returns; success unless a test says otherwise.</summary>
        public Func<Guid, AgentApiStatus> PostStatus { get; set; } = _ => AgentApiStatus.Success;

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
            var status = PostStatus(taskId);
            return Task.FromResult(new AgentApiResult<Unit>(status == AgentApiStatus.Success ? Unit.Value : null, status));
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
