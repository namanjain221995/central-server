using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Chat;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The start and end executors: the strict gate in front of the chat manager (a bad
/// payload or an expired start opens nothing) and the honest result behind it (an
/// end of a device with no open chat still succeeds), plus the manager surface the
/// pipe depends on.
/// </summary>
public sealed class LiveChatExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SessionId = Guid.Parse("0199d100-0000-7000-8000-00000000c0de");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeLauncher : ILiveChatWindowLauncher
    {
        public IReadOnlyList<uint> Sessions { get; set; } = [1];
        public int StopCalls { get; private set; }

        public IReadOnlyList<uint> EnsureWindowInAllSessions() => Sessions;
        public void StopAllWindows() => StopCalls++;
    }

    private sealed class FakeCredentialStore(bool enrolled = true) : IDeviceCredentialStore
    {
        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(enrolled ? new DeviceCredential(Guid.CreateVersion7(), "key", "secret") : null);

        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<bool> HasCredentialAsync(CancellationToken ct = default) => ValueTask.FromResult(enrolled);
        public ValueTask ClearAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private static ChatManager NewManager(FakeLauncher? launcher = null, FakeApi? api = null) =>
        new(launcher ?? new FakeLauncher(), api ?? new FakeApi(), new FakeCredentialStore(),
            NullLogger<ChatManager>.Instance, new FixedClock(Now));

    private static AgentTask StartTask(string? payload, DateTimeOffset? expiresAt = null) =>
        new(Guid.CreateVersion7(), "StartLiveChat", payload, expiresAt);

    private static string StartPayload(Guid? sessionId = null, string adminDisplay = "Asha (IT)") =>
        JsonSerializer.Serialize(new { sessionId = sessionId ?? SessionId, adminDisplay });

    private static StartLiveChatExecutor Start(ChatManager manager) =>
        new(manager, NullLogger<StartLiveChatExecutor>.Instance, new FixedClock(Now));

    private static EndLiveChatExecutor End(ChatManager manager) =>
        new(manager, NullLogger<EndLiveChatExecutor>.Instance);

    // --------------------------------------------------------------- start

    [Fact]
    public async Task A_valid_payload_opens_the_chat_and_reports_the_window_shown()
    {
        var launcher = new FakeLauncher { Sessions = [1, 2] };
        var manager = NewManager(launcher);

        var result = await Start(manager).ExecuteAsync(StartTask(StartPayload()));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldNotBeNull().ShouldContain("2 session");
        manager.CurrentSessionId.ShouldBe(SessionId);
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("sessions").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task With_nobody_signed_in_the_start_still_succeeds_and_says_so()
    {
        var launcher = new FakeLauncher { Sessions = [] };
        var manager = NewManager(launcher);

        var result = await Start(manager).ExecuteAsync(StartTask(StartPayload()));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldNotBeNull().ShouldContain("no interactive user");
    }

    [Fact]
    public async Task An_expired_start_opens_nothing()
    {
        var manager = NewManager();

        var result = await Start(manager).ExecuteAsync(StartTask(StartPayload(), expiresAt: Now.AddSeconds(-1)));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldNotBeNull().ShouldContain("expired");
        manager.CurrentSessionId.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"adminDisplay\":\"Asha\"}")]                                  // no sessionId
    [InlineData("{\"sessionId\":\"0199d100-0000-7000-8000-00000000c0de\"}")]    // no adminDisplay
    public async Task A_missing_or_malformed_payload_opens_nothing(string? payload)
    {
        var manager = NewManager();

        var result = await Start(manager).ExecuteAsync(StartTask(payload));

        result.Succeeded.ShouldBeFalse();
        manager.CurrentSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task An_admin_name_out_of_bounds_opens_nothing()
    {
        var manager = NewManager();

        var tooLong = await Start(manager).ExecuteAsync(StartTask(StartPayload(adminDisplay: new string('x', ChatText.MaxAdminDisplayChars + 1))));
        tooLong.Succeeded.ShouldBeFalse();

        var control = await Start(manager).ExecuteAsync(StartTask(StartPayload(adminDisplay: "line\u0007bell")));
        control.Succeeded.ShouldBeFalse();

        manager.CurrentSessionId.ShouldBeNull();
    }

    // ----------------------------------------------------------------- end

    [Fact]
    public async Task End_closes_an_open_chat_and_stops_the_window()
    {
        var launcher = new FakeLauncher();
        var manager = NewManager(launcher);
        await Start(manager).ExecuteAsync(StartTask(StartPayload()));

        var result = await End(manager).ExecuteAsync(
            new AgentTask(Guid.CreateVersion7(), "EndLiveChat", JsonSerializer.Serialize(new { sessionId = SessionId })));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldNotBeNull().ShouldContain("ended");
        manager.CurrentSessionId.ShouldBeNull();
        launcher.StopCalls.ShouldBeGreaterThan(0);
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcome").GetString().ShouldBe("Closed");
    }

    [Fact]
    public async Task End_of_a_device_with_no_open_chat_still_succeeds_and_says_so()
    {
        var manager = NewManager();

        var result = await End(manager).ExecuteAsync(
            new AgentTask(Guid.CreateVersion7(), "EndLiveChat", JsonSerializer.Serialize(new { sessionId = SessionId })));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldNotBeNull().ShouldContain("no chat window was open");
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcome").GetString().ShouldBe("NotOpen");
    }

    [Fact]
    public async Task End_tolerates_a_missing_payload_and_still_closes_the_active_chat()
    {
        var manager = NewManager();
        await Start(manager).ExecuteAsync(StartTask(StartPayload()));

        var result = await End(manager).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "EndLiveChat", null));

        result.Succeeded.ShouldBeTrue();
        manager.CurrentSessionId.ShouldBeNull();
    }

    // ------------------------------------------------- manager / pipe surface

    [Fact]
    public async Task Poll_reflects_the_open_chat_then_the_close()
    {
        var manager = NewManager();

        manager.Poll(0).Active.ShouldBe(false, "no chat is open yet");

        await manager.StartAsync(SessionId, "Asha (IT)");
        var open = manager.Poll(0);
        open.Active.ShouldBe(true);
        open.AdminDisplay.ShouldBe("Asha (IT)");

        await manager.EndAsync(SessionId);
        manager.Poll(0).Active.ShouldBe(false);
    }

    [Fact]
    public async Task A_relayed_message_is_posted_to_the_server_only_while_a_chat_is_open()
    {
        var api = new FakeApi();
        var manager = NewManager(api: api);

        (await manager.RelayEmployeeMessageAsync("before")).ShouldBeFalse("no open chat, nothing to post");
        api.Sent.ShouldBeEmpty();

        await manager.StartAsync(SessionId, "Asha (IT)");
        (await manager.RelayEmployeeMessageAsync("my printer is offline")).ShouldBeTrue();
        api.Sent.ShouldHaveSingleItem();
        api.Sent[0].SessionId.ShouldBe(SessionId);
        api.Sent[0].Body.ShouldBe("my printer is offline");
    }

    [Fact]
    public async Task The_window_reporting_closed_ends_the_chat_and_stops_the_window()
    {
        var launcher = new FakeLauncher();
        var manager = NewManager(launcher);
        await manager.StartAsync(SessionId, "Asha (IT)");

        manager.ReportClosed();

        manager.CurrentSessionId.ShouldBeNull();
        launcher.StopCalls.ShouldBeGreaterThan(0);
    }

    // --------------------------------------------------------------- fake api

    /// <summary>
    /// The two chat calls, scripted; every other call throws, so a test that strays
    /// off the chat path fails loudly instead of quietly succeeding.
    /// </summary>
    private sealed class FakeApi : IAgentApiClient
    {
        public List<(Guid SessionId, string Body)> Sent { get; } = [];
        public AgentApiResult<ChatPollResponse> Poll { get; set; } =
            AgentApiResult<ChatPollResponse>.Success(new ChatPollResponse(true, []));
        public AgentApiResult<Unit> SendResult { get; set; } = AgentApiResult<Unit>.Success(Unit.Value);

        public Task<AgentApiResult<ChatPollResponse>> PollChatMessagesAsync(
            Guid sessionId, int afterSeq, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(Poll);

        public Task<AgentApiResult<Unit>> SendChatMessageAsync(
            Guid sessionId, string body, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Sent.Add((sessionId, body));
            return Task.FromResult(SendResult);
        }

        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(EnrollRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(EnrollmentRequestRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(EnrollmentClaimRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(InventoryReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(UsbReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostTaskResultAsync(Guid id, AgentTaskResult r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(AgentPolicyComplianceReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(string secretReference, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadPackageAsync(Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentUpdateInfo>> GetAgentUpdateInfoAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(Guid releaseId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<BitLockerEscrowStatusResponse>> GetBitLockerEscrowStatusAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EscrowRecoveryKeyResponse>> EscrowRecoveryKeyAsync(EscrowRecoveryKeyRequest r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<CatalogListResponse>> GetCatalogAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<byte[]>> GetCatalogIconAsync(Guid appId, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<CatalogInstallResponse>> RequestCatalogInstallAsync(Guid appId, CatalogInstallCommand command, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
