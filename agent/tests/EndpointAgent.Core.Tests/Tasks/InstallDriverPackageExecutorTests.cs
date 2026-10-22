using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointAgent.Core;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The driver-installation gates, each proven to fail closed with a fake server and
/// a fake installer.
///
/// The ordering is as much the subject as the individual gates. A refusal that
/// happens after the archive has been unpacked, or after the driver store has been
/// touched, is a materially worse refusal than one that happens before — so the
/// tests assert not only that a bad request is refused but that the installer was
/// never reached.
/// </summary>
public sealed class InstallDriverPackageExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"epa-drvexec-{Guid.CreateVersion7():N}");

    public InstallDriverPackageExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private const string HardwareId = @"PCI\VEN_8086&DEV_1234";
    private const string Signer = "Contoso Corporation";
    private const string InfName = "contoso.inf";

    private static byte[] ArchiveBytes { get; } = BuildArchive();

    private static string ArchiveSha { get; } = Convert.ToHexStringLower(SHA256.HashData(ArchiveBytes));

    private static byte[] BuildArchive()
    {
        using var memory = new MemoryStream();

        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { (InfName, "[Version]"), ("contoso.cat", "cat") })
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    private static AgentTask Task_(object payload) => new(
        Guid.CreateVersion7(), "InstallDriverPackage",
        JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static object Payload(
        string? sha = null,
        string hardwareId = HardwareId,
        string? expectedVersion = "2.0.0.0",
        string? expectedProvider = "Contoso",
        bool allowDowngrade = false,
        DateTimeOffset? issuedAt = null,
        string signer = Signer,
        string infFileName = InfName) => new
        {
            packageId = Guid.CreateVersion7(),
            sha256 = sha ?? ArchiveSha,
            infFileName,
            hardwareId,
            requiredSignerSubject = signer,
            expectedProvider,
            expectedDriverVersion = expectedVersion,
            allowDowngrade,
            packageName = "Contoso NIC 2.0",
            issuedAt = issuedAt ?? Clock.Now,
        };

    private static InstallDriverPackageExecutor Executor(FakeApi api, FakeInstaller installer) =>
        new(api, new FakeCredentialStore(), installer, Clock.Instance,
            NullLogger<InstallDriverPackageExecutor>.Instance);

    private static string Outcome(AgentTaskResult result) =>
        JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcome").GetString()!;

    // ---- the happy path ----------------------------------------------------

    [Fact]
    public async Task A_valid_package_is_downloaded_verified_and_installed()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeTrue();
        installer.InstallCount.ShouldBe(1);
        Outcome(result).ShouldBe("Verified");

        // The INF handed to Windows came out of the extraction directory, not the
        // payload: the payload names a file, it does not supply a path.
        installer.LastInfPath.ShouldNotBeNull();
        Path.GetFileName(installer.LastInfPath).ShouldBe(InfName);
    }

    /// <summary>
    /// The evidence is per instance, because one hardware id can match several
    /// devices and one of them failing is not averaged away by the others.
    /// </summary>
    [Fact]
    public async Task Every_affected_instance_appears_in_the_evidence()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller { Instances = ["INST\\1", "INST\\2", "INST\\3"] };

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        var evidence = JsonDocument.Parse(result.ResultJson!).RootElement;

        evidence.GetProperty("instanceCount").GetInt32().ShouldBe(3);
        evidence.GetProperty("verifiedCount").GetInt32().ShouldBe(3);
        evidence.GetProperty("instances").GetArrayLength().ShouldBe(3);
    }

    /// <summary>
    /// A restart is a successful outcome, reported as its own state. Marking it
    /// failed would invite a retry of a correct installation; marking it Verified
    /// would claim a driver is active when it is not.
    /// </summary>
    [Fact]
    public async Task A_reboot_requirement_is_success_but_never_reported_as_verified()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller { Result = DriverInstallResult.PendingReboot };

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeTrue();
        Outcome(result).ShouldBe("PendingReboot");
        Outcome(result).ShouldNotBe("Verified");
        result.Message!.ShouldContain("restart");
    }

    /// <summary>
    /// The failure a return-value check would call success. Windows accepted the
    /// call; the endpoint does not show the driver.
    /// </summary>
    [Fact]
    public async Task Verification_failure_is_reported_as_failure_despite_the_api_succeeding()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller { Result = DriverInstallResult.VerificationFailed };

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        Outcome(result).ShouldBe("VerificationFailed");
    }

    /// <summary>
    /// A hardware id matching two devices where only one takes the driver. The task
    /// must fail, and the evidence must name which device failed -- an operator
    /// cannot act on "one of them broke".
    /// </summary>
    [Fact]
    public async Task A_partially_successful_installation_fails_and_names_the_failing_instance()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller
        {
            Instances = ["INST\\good", "INST\\bad"],
            FailingInstances = ["INST\\bad"],
        };

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        Outcome(result).ShouldBe("VerificationFailed");

        var evidence = JsonDocument.Parse(result.ResultJson!).RootElement;

        evidence.GetProperty("instanceCount").GetInt32().ShouldBe(2);
        evidence.GetProperty("verifiedCount").GetInt32().ShouldBe(1);

        var instances = evidence.GetProperty("instances").EnumerateArray().ToList();

        instances.Single(i => i.GetProperty("instanceId").GetString() == "INST\\good")
            .GetProperty("verified").GetBoolean().ShouldBeTrue();

        var bad = instances.Single(i => i.GetProperty("instanceId").GetString() == "INST\\bad");
        bad.GetProperty("verified").GetBoolean().ShouldBeFalse();
        bad.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The withdrawal race: the package was approved when the task was queued and
    /// withdrawn before the endpoint acted. The server stops serving the archive, so
    /// the download is refused and nothing is extracted or installed.
    /// </summary>
    [Fact]
    public async Task A_package_withdrawn_after_queueing_is_refused_with_no_driver_store_mutation()
    {
        var api = new FakeApi(ArchiveBytes) { DownloadStatus = AgentApiStatus.Rejected };
        var installer = new FakeInstaller();

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("refused");

        // The download was attempted and refused; nothing beyond it ran.
        api.DownloadCount.ShouldBe(1);
        installer.InstallCount.ShouldBe(0);
    }

    // ---- gate 1: freshness -------------------------------------------------

    /// <summary>
    /// A captured payload replayed later is refused on its own age, independently of
    /// the server's task state machine.
    /// </summary>
    [Fact]
    public async Task A_stale_instruction_is_refused_before_anything_is_downloaded()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        var stale = Clock.Now - InstallDriverPackageExecutor.MaximumInstructionAge - TimeSpan.FromMinutes(1);

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload(issuedAt: stale)));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("fresh");

        api.DownloadCount.ShouldBe(0);
        installer.InstallCount.ShouldBe(0);
        installer.FindCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_instruction_dated_in_the_future_is_refused()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer)
            .ExecuteAsync(Task_(Payload(issuedAt: Clock.Now.AddHours(2))));

        result.Succeeded.ShouldBeFalse();
        api.DownloadCount.ShouldBe(0);
    }

    /// <summary>A modest clock difference between server and endpoint is normal.</summary>
    [Fact]
    public async Task A_small_clock_skew_is_tolerated()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer)
            .ExecuteAsync(Task_(Payload(issuedAt: Clock.Now.AddMinutes(2))));

        result.Succeeded.ShouldBeTrue();
    }

    // ---- gate 2: hardware match --------------------------------------------

    [Fact]
    public async Task A_package_for_absent_hardware_is_refused_before_download()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller { Instances = [] };

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        Outcome(result).ShouldBe("HardwareMismatch");

        api.DownloadCount.ShouldBe(0);
        installer.InstallCount.ShouldBe(0);
    }

    // ---- gate 3: downgrade -------------------------------------------------

    [Fact]
    public async Task A_downgrade_is_refused_by_default_before_anything_is_touched()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller { InstalledVersion = "3.0.0.0" };

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload(expectedVersion: "2.0.0.0")));

        result.Succeeded.ShouldBeFalse();
        Outcome(result).ShouldBe("DowngradeRefused");

        api.DownloadCount.ShouldBe(0);
        installer.InstallCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_explicitly_authorized_downgrade_proceeds()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller { InstalledVersion = "3.0.0.0" };

        var result = await Executor(api, installer)
            .ExecuteAsync(Task_(Payload(expectedVersion: "2.0.0.0", allowDowngrade: true)));

        result.Succeeded.ShouldBeTrue();
        installer.InstallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Installing_the_same_or_a_newer_version_is_not_a_downgrade()
    {
        foreach (var installed in new[] { "2.0.0.0", "1.0.0.0" })
        {
            var api = new FakeApi(ArchiveBytes);
            var installer = new FakeInstaller { InstalledVersion = installed };

            var result = await Executor(api, installer).ExecuteAsync(Task_(Payload(expectedVersion: "2.0.0.0")));

            result.Succeeded.ShouldBeTrue($"installing 2.0.0.0 over {installed} is not a downgrade");
        }
    }

    // ---- gate 4: content hash ----------------------------------------------

    /// <summary>
    /// The hash gate protects the extraction gate. Bytes that fail it are never
    /// unpacked, so a malicious archive never gets the chance to be malicious.
    /// </summary>
    [Fact]
    public async Task Content_that_fails_the_hash_pin_is_never_extracted_or_installed()
    {
        var api = new FakeApi(Encoding.UTF8.GetBytes("substituted content"));
        var installer = new FakeInstaller();

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("content-hash");
        installer.InstallCount.ShouldBe(0);
    }

    // ---- gate 5: archive ---------------------------------------------------

    [Fact]
    public async Task An_archive_missing_the_named_inf_is_refused_without_installing()
    {
        var bytes = BuildArchiveWith("something-else.inf");

        var api = new FakeApi(bytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer)
            .ExecuteAsync(Task_(Payload(sha: Convert.ToHexStringLower(SHA256.HashData(bytes)))));

        result.Succeeded.ShouldBeFalse();
        Outcome(result).ShouldBe("ArchiveRejected");
        installer.InstallCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_archive_that_is_not_a_zip_is_refused_without_installing()
    {
        var bytes = Encoding.UTF8.GetBytes("definitely not a zip archive at all");

        var api = new FakeApi(bytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer)
            .ExecuteAsync(Task_(Payload(sha: Convert.ToHexStringLower(SHA256.HashData(bytes)))));

        result.Succeeded.ShouldBeFalse();
        Outcome(result).ShouldBe("ArchiveRejected");
        installer.InstallCount.ShouldBe(0);
    }

    // ---- gates 6 and 7: signature and signer -------------------------------

    [Theory]
    [InlineData(DriverInstallResult.SignatureRejected)]
    [InlineData(DriverInstallResult.SignerMismatch)]
    public async Task A_signature_refusal_is_reported_as_failure(DriverInstallResult refusal)
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller { Result = refusal };

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        Outcome(result).ShouldBe(refusal.ToString());
    }

    /// <summary>
    /// The signer pin travels to the installer unchanged. If the executor dropped or
    /// defaulted it, the endpoint would silently accept any trusted signature.
    /// </summary>
    [Fact]
    public async Task The_signer_pin_reaches_the_installer()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        installer.LastSigner.ShouldBe(Signer);
    }

    // ---- payload validation ------------------------------------------------

    [Fact]
    public async Task A_payload_with_no_signer_pin_is_malformed_rather_than_permissive()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload(signer: "")));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("incomplete");
        installer.InstallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("{}")]
    public async Task A_malformed_payload_is_refused(string payloadJson)
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer)
            .ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallDriverPackage", payloadJson));

        result.Succeeded.ShouldBeFalse();
        installer.InstallCount.ShouldBe(0);
    }

    // ---- download failures -------------------------------------------------

    [Theory]
    [InlineData(AgentApiStatus.Unauthorized)]
    [InlineData(AgentApiStatus.Rejected)]
    [InlineData(AgentApiStatus.TransientFailure)]
    public async Task A_failed_download_installs_nothing(AgentApiStatus status)
    {
        var api = new FakeApi(ArchiveBytes) { DownloadStatus = status };
        var installer = new FakeInstaller();

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.Succeeded.ShouldBeFalse();
        installer.InstallCount.ShouldBe(0);
    }

    // ---- evidence hygiene --------------------------------------------------

    /// <summary>
    /// The result document is persisted server-side and shown to operators, so it
    /// must carry evidence and nothing else.
    /// </summary>
    [Fact]
    public async Task The_evidence_carries_no_credential_material()
    {
        var api = new FakeApi(ArchiveBytes);
        var installer = new FakeInstaller();

        var result = await Executor(api, installer).ExecuteAsync(Task_(Payload()));

        result.ResultJson.ShouldNotBeNull();
        result.ResultJson!.ShouldNotContain(FakeCredentialStore.Secret);
        result.ResultJson.ShouldNotContain(FakeCredentialStore.KeyId);
    }

    private static byte[] BuildArchiveWith(string infName)
    {
        using var memory = new MemoryStream();

        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(infName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("[Version]");
        }

        return memory.ToArray();
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class Clock : TimeProvider
    {
        public static readonly Clock Instance = new();
        public static DateTimeOffset Now { get; } = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeInstaller : IDriverInstaller
    {
        public DriverInstallResult Result { get; init; } = DriverInstallResult.Verified;
        public IReadOnlyList<string> Instances { get; init; } = ["PCI\\VEN_8086&DEV_1234\\3&11583659&0&10"];
        public string? InstalledVersion { get; init; } = "1.0.0.0";

        /// <summary>Instances that do not take the driver, for the mixed-result case.</summary>
        public IReadOnlyList<string> FailingInstances { get; init; } = [];

        public int FindCount { get; private set; }
        public int InstallCount { get; private set; }
        public string? LastInfPath { get; private set; }
        public string? LastSigner { get; private set; }

        public ValueTask<IReadOnlyList<(string InstanceId, string? DriverVersion)>> FindMatchingInstancesAsync(
            string hardwareId, CancellationToken cancellationToken = default)
        {
            FindCount++;

            return ValueTask.FromResult<IReadOnlyList<(string, string?)>>(
                Instances.Select(i => (i, InstalledVersion)).ToList());
        }

        public ValueTask<DriverInstallOutcome> InstallAsync(
            string infPath, string hardwareId, string requiredSignerSubject,
            string? expectedVersion, string? expectedProvider, CancellationToken cancellationToken = default)
        {
            InstallCount++;
            LastInfPath = infPath;
            LastSigner = requiredSignerSubject;

            // Gates that refuse before installation report no instances at all.
            if (Result is DriverInstallResult.SignatureRejected or DriverInstallResult.SignerMismatch
                or DriverInstallResult.HardwareMismatch or DriverInstallResult.InstallFailed)
            {
                return ValueTask.FromResult(new DriverInstallOutcome(Result, [], Result.ToString()));
            }

            var instances = Instances
                .Select(i =>
                {
                    var failed = FailingInstances.Contains(i, StringComparer.OrdinalIgnoreCase)
                        || Result == DriverInstallResult.VerificationFailed;

                    return new DriverInstanceVerification(
                        i, !failed, expectedVersion, expectedProvider, "oem42.inf", failed ? 10 : 0,
                        failed ? "did not take the driver" : null);
                })
                .ToList();

            // The real installer routes through the same shared rule, so the mixed
            // case is decided here exactly as it would be on a real machine.
            return ValueTask.FromResult(DriverInstallOutcome.FromVerifications(
                instances, rebootRequired: Result == DriverInstallResult.PendingReboot));
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

        private readonly byte[] _content = content;

        public AgentApiStatus DownloadStatus { get; init; } = AgentApiStatus.Success;
        public int DownloadCount { get; private set; }

        public async Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(
            Guid packageId, Stream destination, DeviceCredential credential,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;

            if (DownloadStatus != AgentApiStatus.Success)
            {
                return new AgentApiResult<Unit>(null, DownloadStatus);
            }

            await destination.WriteAsync(_content, cancellationToken);
            return AgentApiResult<Unit>.Success(Unit.Value);
        }

        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(
            EnrollmentRequestRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(
            EnrollmentClaimRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(
            EnrollRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(
            HeartbeatRequest r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(
            InventoryReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(
            UsbReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(
            DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostTaskResultAsync(
            Guid id, AgentTaskResult r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(
            DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(
            AgentPolicyComplianceReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadPackageAsync(
            Guid p, Stream d, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(
            string s, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentUpdateInfo>> GetAgentUpdateInfoAsync(
            DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(
            Guid r, Stream d, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialStore : IDeviceCredentialStore
    {
        public const string KeyId = "test-key-id";
        public const string Secret = "test-credential-secret";

        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DeviceCredential?>(new DeviceCredential(Guid.CreateVersion7(), KeyId, Secret));

        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
