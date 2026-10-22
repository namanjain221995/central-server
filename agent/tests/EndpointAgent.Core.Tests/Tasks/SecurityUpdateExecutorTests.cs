using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointAgent.Core.Updates;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The two security-update executors: the JSON contract the server and dashboard
/// parse (KB ids and MSRC severity included), the exclusion of previews (the
/// optional "Preview" cumulative updates), the reboot-is-success convention, and
/// the strict gate in front of the install (a bad payload installs nothing and
/// never reaches the device). All exercised with a fake service, so the Windows
/// COM never runs here.
/// </summary>
public sealed class SecurityUpdateExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 30, 0, TimeSpan.Zero);

    private const string IdA = "11111111-1111-1111-1111-111111111111";
    private const string IdB = "22222222-2222-2222-2222-222222222222";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSecurityUpdateService : ISecurityUpdateService
    {
        public SecurityUpdateScanResult ScanResult { get; set; } = SecurityUpdateScanResult.Ok([]);
        public SecurityUpdateInstallResult InstallResult { get; set; } = new(false, [], [], null);

        public int ScanCalls { get; private set; }
        public List<IReadOnlyCollection<string>> InstallCalls { get; } = [];

        public Task<SecurityUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            return Task.FromResult(ScanResult);
        }

        public Task<SecurityUpdateInstallResult> InstallAsync(
            IReadOnlyCollection<string> updateIds, CancellationToken cancellationToken = default)
        {
            InstallCalls.Add(updateIds);
            return Task.FromResult(InstallResult);
        }
    }

    private static ScanSecurityUpdatesExecutor Scanner(FakeSecurityUpdateService service) =>
        new(service, NullLogger<ScanSecurityUpdatesExecutor>.Instance, new FixedClock(Now));

    private static InstallSecurityUpdateExecutor Installer(FakeSecurityUpdateService service) =>
        new(service, NullLogger<InstallSecurityUpdateExecutor>.Instance);

    private static AgentTask ScanTask() => new(Guid.CreateVersion7(), "ScanSecurityUpdates", null);

    private static AgentTask InstallTask(string? payload) => new(Guid.CreateVersion7(), "InstallSecurityUpdate", payload);

    private static string InstallPayload(params string[] ids) =>
        JsonSerializer.Serialize(new { updateIds = ids, issuedAt = "2026-10-23T00:00:00Z" });

    // =====================================================================  SCAN

    [Fact]
    public async Task A_scan_reports_the_offered_updates_in_the_contract_shape()
    {
        var service = new FakeSecurityUpdateService
        {
            ScanResult = SecurityUpdateScanResult.Ok(
            [
                new SecurityUpdateInfo(IdA, 3, "2026-10 Cumulative Update for Windows 11", "KB5044284", "Critical", false, 734_003_200),
                new SecurityUpdateInfo(IdB, 1, "Security Update for Microsoft Defender", "KB5007651,KB5007652", "Important", false, 2_097_152),
            ]),
        };

        var result = await Scanner(service).ExecuteAsync(ScanTask());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("Found 2 security update(s).");

        var root = JsonDocument.Parse(result.ResultJson!).RootElement;
        root.GetProperty("scannedAt").GetDateTimeOffset().ShouldBe(Now);

        var updates = root.GetProperty("updates").EnumerateArray().ToList();
        updates.Count.ShouldBe(2);

        var first = updates[0];
        first.GetProperty("updateId").GetString().ShouldBe(IdA);
        first.GetProperty("revision").GetInt32().ShouldBe(3);
        first.GetProperty("title").GetString().ShouldBe("2026-10 Cumulative Update for Windows 11");
        first.GetProperty("kbIds").GetString().ShouldBe("KB5044284");
        first.GetProperty("severity").GetString().ShouldBe("Critical");
        first.GetProperty("isPreview").GetBoolean().ShouldBeFalse();
        first.GetProperty("sizeBytes").GetInt64().ShouldBe(734_003_200);

        var second = updates[1];
        second.GetProperty("kbIds").GetString().ShouldBe("KB5007651,KB5007652");
        second.GetProperty("severity").GetString().ShouldBe("Important");
    }

    [Fact]
    public async Task A_scan_carries_null_kbIds_and_severity_when_the_update_has_none()
    {
        var service = new FakeSecurityUpdateService
        {
            ScanResult = SecurityUpdateScanResult.Ok(
            [
                new SecurityUpdateInfo(IdA, 1, "Windows Malicious Software Removal Tool", null, null, false, 1_000),
            ]),
        };

        var result = await Scanner(service).ExecuteAsync(ScanTask());

        result.Succeeded.ShouldBeTrue();

        var update = JsonDocument.Parse(result.ResultJson!).RootElement
            .GetProperty("updates").EnumerateArray().ToList().ShouldHaveSingleItem();
        update.GetProperty("kbIds").ValueKind.ShouldBe(JsonValueKind.Null);
        update.GetProperty("severity").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_empty_scan_is_a_success_with_an_empty_list_never_an_error()
    {
        var service = new FakeSecurityUpdateService { ScanResult = SecurityUpdateScanResult.Ok([]) };

        var result = await Scanner(service).ExecuteAsync(ScanTask());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("Found 0 security update(s).");

        var root = JsonDocument.Parse(result.ResultJson!).RootElement;
        root.GetProperty("updates").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_preview_update_is_never_reported_even_if_the_scanner_returns_one()
    {
        // The scanner drops previews at the source; this proves the executor excludes
        // them too, so a preview can never reach the reported list.
        var service = new FakeSecurityUpdateService
        {
            ScanResult = SecurityUpdateScanResult.Ok(
            [
                new SecurityUpdateInfo(IdA, 3, "2026-10 Cumulative Update for Windows 11", "KB5044284", "Critical", false, 1_000),
                new SecurityUpdateInfo(IdB, 1, "2026-10 Cumulative Update Preview for Windows 11", "KB5044380", null, true, 2_000),
            ]),
        };

        var result = await Scanner(service).ExecuteAsync(ScanTask());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("Found 1 security update.");

        var updates = JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("updates").EnumerateArray().ToList();
        updates.ShouldHaveSingleItem().GetProperty("updateId").GetString().ShouldBe(IdA);
    }

    [Fact]
    public async Task A_scan_that_could_not_run_is_an_honest_failure_with_no_result_document()
    {
        var service = new FakeSecurityUpdateService
        {
            ScanResult = SecurityUpdateScanResult.Failure("The Windows Update Agent is not available on this device."),
        };

        var result = await Scanner(service).ExecuteAsync(ScanTask());

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("not available");
        result.ResultJson.ShouldBeNull();
    }

    [Fact]
    public void The_scan_task_type_matches_the_server_enum_name()
    {
        Scanner(new FakeSecurityUpdateService()).TaskType.ShouldBe("ScanSecurityUpdates");
    }

    // ==================================================================  INSTALL

    [Fact]
    public async Task A_valid_install_reports_what_took_in_the_contract_shape()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                RebootRequired: false,
                Installed: [new SecurityUpdateInstallItem(IdA, "Security Update for Microsoft Defender", 2, 0)],
                Failed: []),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA)));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("Installed 1 security update(s).");

        var root = JsonDocument.Parse(result.ResultJson!).RootElement;
        root.GetProperty("rebootRequired").GetBoolean().ShouldBeFalse();
        root.GetProperty("failed").GetArrayLength().ShouldBe(0);

        var installed = root.GetProperty("installed").EnumerateArray().ToList();
        installed.ShouldHaveSingleItem();
        installed[0].GetProperty("updateId").GetString().ShouldBe(IdA);
        installed[0].GetProperty("title").GetString().ShouldBe("Security Update for Microsoft Defender");
        installed[0].GetProperty("resultCode").GetInt32().ShouldBe(2);
        installed[0].GetProperty("hresult").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_required_reboot_is_success_and_says_a_restart_is_required()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                RebootRequired: true,
                Installed: [new SecurityUpdateInstallItem(IdA, "2026-10 Cumulative Update for Windows 11", 2, 0)],
                Failed: []),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA)));

        result.Succeeded.ShouldBeTrue("a pending reboot is a successful install, not a failure");
        (result.Message ?? "").ShouldContain("restart is required");

        JsonDocument.Parse(result.ResultJson!).RootElement
            .GetProperty("rebootRequired").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_partial_install_with_one_success_succeeds_and_names_the_failure()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                RebootRequired: false,
                Installed: [new SecurityUpdateInstallItem(IdA, "Security Update for Microsoft Defender", 2, 0)],
                Failed: [new SecurityUpdateInstallItem(IdB, "2026-10 Cumulative Update for Windows 11", 4, unchecked((int)0x80240022))]),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA, IdB)));

        result.Succeeded.ShouldBeTrue("at least one update installed");
        (result.Message ?? "").ShouldContain("1 failed");

        var failed = JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("failed").EnumerateArray().ToList();
        failed.ShouldHaveSingleItem();
        failed[0].GetProperty("updateId").GetString().ShouldBe(IdB);
        failed[0].GetProperty("resultCode").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task An_install_where_everything_failed_is_reported_as_failure()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                RebootRequired: false,
                Installed: [],
                Failed: [new SecurityUpdateInstallItem(IdA, "2026-10 Cumulative Update for Windows 11", 4, -1)]),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA)));

        result.Succeeded.ShouldBeFalse();
        result.ResultJson.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_install_that_could_not_run_fails_with_the_services_reason()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = SecurityUpdateInstallResult.ForError(
                "Another update operation is in progress or a reboot is pending; try again later."),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("in progress");
    }

    [Fact]
    public async Task An_install_where_nothing_could_be_staged_fails_with_the_breakdown()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = SecurityUpdateInstallResult.ForError(
                "No security updates were installed: 1 no longer offered by Windows Update; 1 preview."),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA, IdB)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("No security updates were installed");
    }

    [Fact]
    public async Task The_requested_ids_reach_the_service_normalised()
    {
        var service = new FakeSecurityUpdateService();

        // Mixed case and braces on the wire; the service must see the canonical form
        // it compares against the WUA identity.
        await Installer(service).ExecuteAsync(InstallTask(InstallPayload("{11111111-1111-1111-1111-111111111111}", "22222222-2222-2222-2222-222222222222".ToUpperInvariant())));

        var received = service.InstallCalls.ShouldHaveSingleItem();
        received.ShouldContain(IdA);
        received.ShouldContain(IdB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]                                   // not an object
    [InlineData("{}")]                                   // no updateIds
    [InlineData("{\"updateIds\":\"nope\"}")]            // not an array
    [InlineData("{\"updateIds\":[]}")]                  // empty array
    [InlineData("{\"updateIds\":[\"not-a-guid\"]}")]    // no parseable guid
    [InlineData("{\"updateIds\":[123]}")]                // wrong element type
    public async Task A_missing_or_malformed_payload_changes_nothing(string? payload)
    {
        var service = new FakeSecurityUpdateService();

        var result = await Installer(service).ExecuteAsync(InstallTask(payload));

        result.Succeeded.ShouldBeFalse();
        service.InstallCalls.ShouldBeEmpty("a bad payload must never reach the device");
    }

    [Fact]
    public void The_install_task_type_matches_the_server_enum_name()
    {
        Installer(new FakeSecurityUpdateService()).TaskType.ShouldBe("InstallSecurityUpdate");
    }

    // ============================================================  preview rule

    [Theory]
    [InlineData("2026-10 Cumulative Update for Windows 11", false, false)]
    [InlineData("2026-10 Cumulative Update Preview for Windows 11", false, true)]
    [InlineData(".NET Framework update BETA", false, true)]
    [InlineData("Ordinary security update", true, true)]      // WUA beta flag wins
    [InlineData("PREVIEW build", false, true)]                // case-insensitive
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    public void The_preview_rule_excludes_beta_and_titled_previews(string? title, bool isBeta, bool expected)
    {
        SecurityUpdatePreview.IsPreview(title, isBeta).ShouldBe(expected);
    }
}
