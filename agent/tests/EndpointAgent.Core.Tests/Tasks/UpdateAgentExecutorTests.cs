using System.Security.Cryptography;
using EndpointAgent.Core;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The self-update gates, each proven to fail closed with a fake server and a
/// fake launcher — no gate ever reaches the launcher unless every earlier gate
/// passed, and a refused update leaves zero installs scheduled.
/// </summary>
public sealed class UpdateAgentExecutorTests : IDisposable
{
    /// <summary>
    /// The staging directory these tests hand the executor, instead of the
    /// machine-wide one.
    /// </summary>
    /// <remarks>
    /// Not cosmetic isolation. The executor stages its download in the state
    /// directory, so pointing it at the real <c>ProgramData</c> folder made
    /// every gate past the download unreachable on any machine where that
    /// folder exists but the test process cannot write to it: the write threw,
    /// was caught as "could not be stored locally", and the hash and signature
    /// assertions below never ran. A temp root per test class is the same shape
    /// <see cref="InstallDriverPackageExecutorTests"/> already uses.
    /// </remarks>
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"epa-updexec-{Guid.CreateVersion7():N}");

    public UpdateAgentExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // Recursive: a successful update also writes an update-backup
                // subdirectory when it snapshots enrollment state.
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static readonly Guid ReleaseId = Guid.CreateVersion7();

    private static byte[] MsiBytes { get; } = System.Text.Encoding.UTF8.GetBytes(
        "fake-msi-content-" + new string('m', 4096));

    private static string MsiSha { get; } = Convert.ToHexStringLower(SHA256.HashData(MsiBytes));

    /// <summary>A version far above any real agent build, so "newer" holds.</summary>
    private const string NewerVersion = "999.0.0";

    private static AgentTask Task_(object payload) => new(
        Guid.CreateVersion7(), "UpdateAgent",
        System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web)));

    private static AgentUpdateInfo Offer(
        string? version = NewerVersion, string? sha = null, string arch = "x64", string? signer = null) =>
        new(true, ReleaseId, version, arch, $"agent-{version}.msi", sha ?? MsiSha, signer, MsiBytes.Length);

    private UpdateAgentExecutor Executor(FakeApi api, FakeLauncher launcher) => new(
        api, new FakeCredentialStore(), launcher,
        Options.Create(new AgentOptions
        {
            ServerBaseUrl = "https://server.invalid",
            StateDirectory = _root,
        }),
        NullLogger<UpdateAgentExecutor>.Instance);

    [Fact]
    public async Task A_valid_update_downloads_verifies_and_schedules_the_install()
    {
        var api = new FakeApi(MsiBytes) { Info = Offer() };
        var launcher = new FakeLauncher();

        var task = Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha });
        var result = await Executor(api, launcher).ExecuteAsync(task);

        result.Succeeded.ShouldBeTrue();
        // "Started", never "succeeded": the running process cannot witness the outcome.
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("started");
        launcher.ScheduledPath.ShouldNotBeNull();
        File.Exists(launcher.ScheduledPath).ShouldBeTrue();
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(launcher.ScheduledPath!)))
            .ShouldBe(MsiSha);

        // The MSI install log goes to the machine-wide log directory, never the
        // configured state directory. Pinned here because it looks like an
        // oversight: msiexec /l*v fails 1622 against a directory nobody creates,
        // and only the installer and Serilog create that one.
        // Named per task: msiexec /l*v overwrites, so a shared name kept only
        // the last of several attempts.
        Path.GetFileName(launcher.ScheduledLogPath!).ShouldBe($"agent-update-{NewerVersion}-{task.TaskId:N}.msi.log");
        Path.GetDirectoryName(launcher.ScheduledLogPath!).ShouldBe(AgentPaths.LogDirectory);

        File.Delete(launcher.ScheduledPath!);
    }

    /// <summary>
    /// The snapshot the executor writes is the one the next service start reads.
    /// </summary>
    /// <remarks>
    /// The round trip, not just the write. These two halves live in different
    /// classes and resolve the state directory separately, so agreement between
    /// them is an assumption worth proving: if it broke, an updated device would
    /// come back as a stranger and re-enrol, and nothing would have reported an
    /// error. Untestable until the executor honoured a configurable state
    /// directory -- the test could not populate the real one.
    /// </remarks>
    [Fact]
    public async Task The_identity_snapshot_it_writes_is_the_one_the_new_service_restores()
    {
        // The file that IS this device's identity, as the credential store writes it.
        var live = Path.Combine(_root, "device-credential.bin");
        await File.WriteAllBytesAsync(live, [1, 2, 3, 4]);

        var api = new FakeApi(MsiBytes) { Info = Offer() };
        var launcher = new FakeLauncher();

        (await Executor(api, launcher).ExecuteAsync(
            Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha })))
            .Succeeded.ShouldBeTrue();

        File.Exists(Path.Combine(_root, UpdateAgentExecutor.BackupDirectoryName, "device-credential.bin"))
            .ShouldBeTrue("the executor did not snapshot the credential before handing over");

        // What a destructive major-upgrade uninstall does to the live state.
        File.Delete(live);

        AgentStateRestore.RestoreIfNeeded(NullLogger.Instance, _root);

        File.Exists(live).ShouldBeTrue("identity was not restored, so the device would re-enrol as a stranger");
        File.ReadAllBytes(live).ShouldBe([1, 2, 3, 4]);
    }

    /// <summary>
    /// The executor stages where it is configured to, not in the machine-wide
    /// ProgramData folder.
    /// </summary>
    /// <remarks>
    /// Guards the regression this fix exists for. If the executor goes back to
    /// reading the static <see cref="AgentPaths.StateDirectory"/> directly, this
    /// fails on every machine -- rather than only on one where ProgramData
    /// happens to be unwritable, which is how the original defect stayed hidden
    /// while presenting as four unrelated gate failures.
    /// </remarks>
    [Fact]
    public async Task The_download_is_staged_in_the_configured_state_directory()
    {
        var api = new FakeApi(MsiBytes) { Info = Offer() };
        var launcher = new FakeLauncher();

        var result = await Executor(api, launcher).ExecuteAsync(
            Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha }));

        result.Succeeded.ShouldBeTrue();
        launcher.ScheduledPath.ShouldNotBeNull();
        Path.GetFullPath(launcher.ScheduledPath!)
            .StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue($"staged at {launcher.ScheduledPath}, outside the configured state directory");
    }

    [Fact]
    public async Task A_payload_the_server_does_not_corroborate_is_refused()
    {
        // The server offers a different release id than the task names.
        var api = new FakeApi(MsiBytes)
        {
            Info = new AgentUpdateInfo(true, Guid.CreateVersion7(), NewerVersion, "x64", "a.msi", MsiSha, null, 1),
        };
        var launcher = new FakeLauncher();

        var result = await Executor(api, launcher).ExecuteAsync(
            Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha }));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("does not match");
        launcher.ScheduledPath.ShouldBeNull();
        api.DownloadCount.ShouldBe(0); // refused before a single byte moved
    }

    [Fact]
    public async Task A_downgrade_or_same_version_is_refused()
    {
        foreach (var version in new[] { "0.0.1", AgentVersion.Current })
        {
            var api = new FakeApi(MsiBytes) { Info = Offer(version) };
            var launcher = new FakeLauncher();

            var result = await Executor(api, launcher).ExecuteAsync(
                Task_(new { releaseId = ReleaseId, version, sha256 = MsiSha }));

            result.Succeeded.ShouldBeFalse();
            result.Message!.ShouldContain("not newer");
            launcher.ScheduledPath.ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_wrong_architecture_is_refused()
    {
        var api = new FakeApi(MsiBytes) { Info = Offer(arch: "arm64") };
        var launcher = new FakeLauncher();

        var result = await Executor(api, launcher).ExecuteAsync(
            Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha }));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("arm64");
        launcher.ScheduledPath.ShouldBeNull();
    }

    [Fact]
    public async Task Corrupted_bytes_fail_the_hash_gate_and_nothing_is_scheduled()
    {
        // The server's metadata promises MsiSha but serves different bytes —
        // a tampered store, a truncated stream, or a corrupt disk all land here.
        var corrupted = System.Text.Encoding.UTF8.GetBytes("not-the-promised-bytes");
        var api = new FakeApi(corrupted) { Info = Offer() };
        var launcher = new FakeLauncher();

        var result = await Executor(api, launcher).ExecuteAsync(
            Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha }));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("content-hash");
        launcher.ScheduledPath.ShouldBeNull();
        launcher.VerifiedPath.ShouldBeNull(); // signature never even consulted
        // The bytes that failed the hash were never promoted to the real name,
        // and the .partial was cleaned up: nothing installable survives.
        Directory.GetFileSystemEntries(_root).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_signature_check_discards_the_file_and_refuses()
    {
        var api = new FakeApi(MsiBytes) { Info = Offer(signer: "CN=Endpoint Platform") };
        var launcher = new FakeLauncher { SignatureError = "Authenticode verification failed (0x800B0100)." };

        var result = await Executor(api, launcher).ExecuteAsync(
            Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha }));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("Signature verification failed");
        launcher.ScheduledPath.ShouldBeNull();
        // The rejected MSI must not linger where anything could install it later.
        File.Exists(launcher.VerifiedPath!).ShouldBeFalse();
        // Stronger than the line above, which only covers the one name the fake
        // happened to record: no installable artifact of any name survived.
        Directory.GetFileSystemEntries(_root).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_interrupted_download_refuses_without_touching_the_current_install()
    {
        var api = new FakeApi(MsiBytes) { Info = Offer(), DownloadFails = true };
        var launcher = new FakeLauncher();

        var result = await Executor(api, launcher).ExecuteAsync(
            Task_(new { releaseId = ReleaseId, version = NewerVersion, sha256 = MsiSha }));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("untouched");
        launcher.ScheduledPath.ShouldBeNull();
        // The documented claim for this path -- "partial file deleted, current
        // install untouched" -- asserted rather than assumed.
        Directory.GetFileSystemEntries(_root).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_payload_is_refused_before_any_network_traffic()
    {
        var api = new FakeApi(MsiBytes) { Info = Offer() };
        var launcher = new FakeLauncher();

        var result = await Executor(api, launcher).ExecuteAsync(
            new AgentTask(Guid.CreateVersion7(), "UpdateAgent", """{"releaseId":"not-a-guid"}"""));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("Malformed");
        api.InfoCount.ShouldBe(0);
    }

    [Fact]
    public void Version_comparison_is_numeric_and_fails_closed()
    {
        UpdateAgentExecutor.IsStrictlyNewer("1.0.10", "1.0.9").ShouldBeTrue();
        UpdateAgentExecutor.IsStrictlyNewer("1.0.9", "1.0.10").ShouldBeFalse();
        UpdateAgentExecutor.IsStrictlyNewer("2.0.0", "1.9.9").ShouldBeTrue();
        UpdateAgentExecutor.IsStrictlyNewer("1.1.0", "1.1.0").ShouldBeFalse();
        UpdateAgentExecutor.IsStrictlyNewer("garbage", "1.0.0").ShouldBeFalse();
        UpdateAgentExecutor.IsStrictlyNewer("2.0.0", "garbage").ShouldBeFalse();
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeLauncher : IAgentUpdateLauncher
    {
        public string? SignatureError { get; set; }
        public string? VerifiedPath { get; private set; }
        public string? ScheduledPath { get; private set; }
        public string? ScheduledLogPath { get; private set; }

        public ValueTask<string?> VerifySignatureAsync(
            string msiPath, string? requiredSignerSubject, CancellationToken cancellationToken = default)
        {
            VerifiedPath = msiPath;
            // Mirrors the real launcher's policy: a null signer means the release
            // was published unsigned and the gate is skipped by declaration.
            return ValueTask.FromResult(requiredSignerSubject is null ? null : SignatureError);
        }

        public ValueTask ScheduleInstallAsync(
            string msiPath, string installLogPath, CancellationToken cancellationToken = default)
        {
            ScheduledPath = msiPath;
            ScheduledLogPath = installLogPath;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApi(byte[] content) : IAgentApiClient
    {
        // Automatic escrow is not exercised by these tests. Throwing rather than
        // returning a bland success keeps them honest: if one of them ever starts
        // reaching this path, it fails loudly instead of quietly appearing to escrow.
        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.BitLockerEscrowStatusResponse>>
            GetBitLockerEscrowStatusAsync(DeviceCredential credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyResponse>>
            EscrowRecoveryKeyAsync(
                EndpointPlatform.Contracts.Agent.EscrowRecoveryKeyRequest request,
                DeviceCredential credential,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AgentUpdateInfo? Info { get; set; }
        public bool DownloadFails { get; set; }
        public int InfoCount { get; private set; }
        public int DownloadCount { get; private set; }

        public Task<AgentApiResult<AgentUpdateInfo>> GetAgentUpdateInfoAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            InfoCount++;
            return Task.FromResult(Info is null
                ? new AgentApiResult<AgentUpdateInfo>(null, AgentApiStatus.TransientFailure)
                : AgentApiResult<AgentUpdateInfo>.Success(Info));
        }

        public async Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(
            Guid releaseId, Stream destination, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            if (DownloadFails)
            {
                return new AgentApiResult<Unit>(null, AgentApiStatus.TransientFailure);
            }

            await destination.WriteAsync(content, cancellationToken);
            return AgentApiResult<Unit>.Success(Unit.Value);
        }

        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(EnrollmentRequestRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(EnrollmentClaimRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(EnrollRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(InventoryReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(UsbReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostTaskResultAsync(Guid id, AgentTaskResult r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(AgentPolicyComplianceReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(string secretReference, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadPackageAsync(Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(
            Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentialStore : IDeviceCredentialStore
    {
        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DeviceCredential?>(new DeviceCredential(Guid.CreateVersion7(), "key", "secret"));

        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
