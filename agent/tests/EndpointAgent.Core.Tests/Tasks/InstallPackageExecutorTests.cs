using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The install pipeline's decisions, proven against fakes - never a live install.
/// Covers idempotency, the hash gate, the signature gate, installer failure and
/// post-install verification.
/// </summary>
public sealed class InstallPackageExecutorTests
{
    private const string ProductCode = "{2C4E1D0B-1111-2222-3333-444455556666}";
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("pretend-this-is-an-msi");
    private static readonly string ContentHash = Convert.ToHexStringLower(SHA256.HashData(Content));

    private static string Payload(string sha256) =>
        JsonSerializer.Serialize(new
        {
            packageId = Guid.CreateVersion7(),
            sha256,
            msiProductCode = ProductCode,
            requiredSignerSubject = "CN=Contoso",
            packageName = "Contoso App",
            version = "1.0",
        });

    private static InstallPackageExecutor Build(FakeInstaller installer, FakeApi api) =>
        new(api, new FakeCredentialStore(), installer, NullLogger<InstallPackageExecutor>.Instance);

    [Fact]
    public async Task An_already_installed_product_is_a_success_without_downloading()
    {
        var installer = new FakeInstaller { Installed = true };
        var api = new FakeApi(Content);

        var result = await Build(installer, api).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallPackage", Payload(ContentHash)));

        result.Succeeded.ShouldBeTrue();
        api.DownloadCount.ShouldBe(0);
        installer.InstallCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_happy_path_downloads_verifies_and_installs()
    {
        var installer = new FakeInstaller { Installed = false, InstalledAfterInstall = true };
        var api = new FakeApi(Content);

        var result = await Build(installer, api).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallPackage", Payload(ContentHash)));

        result.Succeeded.ShouldBeTrue();
        api.DownloadCount.ShouldBe(1);
        installer.InstallCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_content_hash_mismatch_refuses_to_install()
    {
        var installer = new FakeInstaller();
        var api = new FakeApi(Content);

        var wrongHash = new string('a', 64);
        var result = await Build(installer, api).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallPackage", Payload(wrongHash)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("content-hash");
        installer.InstallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_rejected_signature_fails_the_task()
    {
        var installer = new FakeInstaller
        {
            Outcome = new PackageInstallOutcome(PackageInstallResult.SignatureRejected, null, "signer mismatch"),
        };
        var api = new FakeApi(Content);

        var result = await Build(installer, api).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallPackage", Payload(ContentHash)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("signature");
    }

    [Fact]
    public async Task An_installer_failure_fails_the_task()
    {
        var installer = new FakeInstaller
        {
            Outcome = new PackageInstallOutcome(PackageInstallResult.InstallFailed, 1603, "fatal"),
        };
        var api = new FakeApi(Content);

        var result = await Build(installer, api).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallPackage", Payload(ContentHash)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("Install failed");
    }

    [Fact]
    public async Task A_reported_success_that_is_not_detectable_afterwards_fails()
    {
        // Installer says OK, but the product is still absent on the post-check.
        var installer = new FakeInstaller { Installed = false, InstalledAfterInstall = false };
        var api = new FakeApi(Content);

        var result = await Build(installer, api).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallPackage", Payload(ContentHash)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("not detectable");
    }

    [Fact]
    public async Task A_transient_download_failure_fails_without_installing()
    {
        var installer = new FakeInstaller();
        var api = new FakeApi(Content) { DownloadStatus = AgentApiStatus.TransientFailure };

        var result = await Build(installer, api).ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "InstallPackage", Payload(ContentHash)));

        result.Succeeded.ShouldBeFalse();
        installer.InstallCount.ShouldBe(0);
    }

    private sealed class FakeInstaller : IPackageInstaller
    {
        public bool Installed { get; set; }
        public bool InstalledAfterInstall { get; set; }
        public int InstallCount { get; private set; }
        public PackageInstallOutcome? Outcome { get; set; }

        public ValueTask<bool> IsProductInstalledAsync(string msiProductCode, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InstallCount > 0 ? InstalledAfterInstall : Installed);

        public ValueTask<PackageInstallOutcome> InstallAsync(
            string msiPath, string? requiredSignerSubject, CancellationToken cancellationToken = default)
        {
            InstallCount++;
            return ValueTask.FromResult(
                Outcome ?? new PackageInstallOutcome(PackageInstallResult.Succeeded, 0, "ok"));
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

        public int DownloadCount { get; private set; }
        public AgentApiStatus DownloadStatus { get; set; } = AgentApiStatus.Success;

        public async Task<AgentApiResult<Unit>> DownloadPackageAsync(
            Guid packageId, Stream destination, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            if (DownloadStatus != AgentApiStatus.Success)
            {
                return new AgentApiResult<Unit>(null, DownloadStatus);
            }

            await destination.WriteAsync(content, cancellationToken);
            return AgentApiResult<Unit>.Success(Unit.Value);
        }

        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(EnrollRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(InventoryReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(UsbReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostTaskResultAsync(Guid id, AgentTaskResult r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(AgentPolicyComplianceReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(
            Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(string secretReference, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EndpointPlatform.Contracts.Agent.AgentUpdateInfo>> GetAgentUpdateInfoAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(Guid releaseId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
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
