using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.FileCollection;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The file-collection executor's payload handling: it accepts a well-formed request
/// and queues it, and refuses anything missing a transfer id, recipient key, user SID,
/// a recognised folder or a positive size ceiling.
/// </summary>
public sealed class CollectFilesExecutorTests
{
    private static CollectFilesExecutor NewExecutor()
    {
        var worker = new CollectFilesWorker(
            new EmptyResolver(),
            new NoopApiClient(),
            new NullCredentialStore(),
            NullLogger<CollectFilesWorker>.Instance);
        return new CollectFilesExecutor(worker, NullLogger<CollectFilesExecutor>.Instance);
    }

    private static Task<AgentTaskResult> RunAsync(string? payload) =>
        NewExecutor().ExecuteAsync(new AgentTask(Guid.NewGuid(), "CollectFiles", payload));

    [Fact]
    public async Task A_well_formed_request_is_accepted_and_started()
    {
        var payload =
            """
            {"transferId":"abc","recipientPublicKey":"BASE64KEY","userSid":"S-1-5-21-1-2-3-1001",
             "rootKeys":["Desktop","Downloads"],"maxBytes":1000}
            """;

        var result = await RunAsync(payload);

        result.Succeeded.ShouldBeTrue();
        result.Message!.ShouldContain("started");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    // Missing recipient key.
    [InlineData("""{"transferId":"a","userSid":"S-1-5-21-1-2-3-1001","rootKeys":["Desktop"],"maxBytes":10}""")]
    // No recognised folder.
    [InlineData("""{"transferId":"a","recipientPublicKey":"k","userSid":"S-1-5-21-1-2-3-1001","rootKeys":["Windows"],"maxBytes":10}""")]
    // Non-positive ceiling.
    [InlineData("""{"transferId":"a","recipientPublicKey":"k","userSid":"S-1-5-21-1-2-3-1001","rootKeys":["Desktop"],"maxBytes":0}""")]
    public async Task A_malformed_or_incomplete_request_is_refused(string payload)
    {
        var result = await RunAsync(payload);
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_the_three_known_folders_survive_parsing()
    {
        // "Windows" is dropped; "desktop" matches case-insensitively and is kept.
        var payload =
            """
            {"transferId":"a","recipientPublicKey":"k","userSid":"S-1-5-21-1-2-3-1001",
             "rootKeys":["Windows","desktop","desktop"],"maxBytes":10}
            """;

        var result = await RunAsync(payload);
        result.Succeeded.ShouldBeTrue();
    }

    private sealed class EmptyResolver : IUserFolderResolver
    {
        public IReadOnlyList<ResolvedFolder> Resolve(string userSid, IReadOnlyList<string> rootKeys) => [];
    }

    private sealed class NullCredentialStore : IDeviceCredentialStore
    {
        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DeviceCredential?>(null);

        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NoopApiClient : IFileCollectionApiClient
    {
        public Task<AgentApiResult<Unit>> PostFileCollectionHeaderAsync(
            string transferId, AgentFileCollectionHeader header, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));

        public Task<AgentApiResult<Unit>> UploadFileCollectionChunkAsync(
            string transferId, long seq, byte[] frame, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));

        public Task<AgentApiResult<Unit>> PostFileCollectionResultAsync(
            string transferId, AgentFileCollectionResult result, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));
    }
}
