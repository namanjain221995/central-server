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
                Failed: [],
                Outcomes: [Outcome(IdA, "Security Update for Microsoft Defender", UpdateOutcomeStatus.Installed)],
                PostScan: []),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA)));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("Installed 1.");

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

        // A service result with no outcomes (the pre-1.28.0 shape) is accounted for
        // conservatively: installed + reboot is PendingReboot.
        result.Message.ShouldBe("1 needs a restart to finish.");

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

    // ==========================================  1.28.0: outcomes, verified, postScan

    private const string IdC = "33333333-3333-3333-3333-333333333333";
    private const string IdD = "44444444-4444-4444-4444-444444444444";

    private static UpdateOutcome Outcome(
        string id, string title, UpdateOutcomeStatus status, int? resultCode = 2, int? hresult = 0, bool reboot = false) =>
        new(id, title, resultCode, hresult, reboot, status);

    private static SecurityUpdateInfo Info(string id, string title, bool preview = false) =>
        new(id, 1, title, "KB5044284", "Important", preview, 1_000);

    private static IReadOnlyList<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToList();

    [Fact]
    public async Task The_install_result_is_a_superset_with_every_old_key_unchanged_and_the_new_keys_exactly_named()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                RebootRequired: true,
                Installed: [new SecurityUpdateInstallItem(IdA, "2026-10 Cumulative Update for Windows 11", 2, 0)],
                Failed: [new SecurityUpdateInstallItem(IdB, "Security Update for Microsoft Defender", 4, unchecked((int)0x80240022))],
                Outcomes:
                [
                    Outcome(IdA, "2026-10 Cumulative Update for Windows 11", UpdateOutcomeStatus.PendingReboot, reboot: true),
                    Outcome(IdB, "Security Update for Microsoft Defender", UpdateOutcomeStatus.Failed, 4, unchecked((int)0x80240022)),
                ],
                PostScan: [Info(IdA, "2026-10 Cumulative Update for Windows 11")]),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA, IdB)));
        var root = JsonDocument.Parse(result.ResultJson!).RootElement;

        // The pre-1.28.0 keys first, then the three appended -- nothing renamed or removed.
        PropertyNames(root).ShouldBe(["rebootRequired", "installed", "failed", "outcomes", "verified", "postScan"]);

        // The old keys carry exactly what they always did.
        root.GetProperty("rebootRequired").GetBoolean().ShouldBeTrue();
        var installed = root.GetProperty("installed").EnumerateArray().ShouldHaveSingleItem();
        PropertyNames(installed).ShouldBe(["updateId", "title", "resultCode", "hresult"]);
        installed.GetProperty("updateId").GetString().ShouldBe(IdA);
        var failed = root.GetProperty("failed").EnumerateArray().ShouldHaveSingleItem();
        PropertyNames(failed).ShouldBe(["updateId", "title", "resultCode", "hresult"]);
        failed.GetProperty("hresult").GetInt32().ShouldBe(unchecked((int)0x80240022));

        // The new keys, exact names and values.
        var outcomes = root.GetProperty("outcomes").EnumerateArray().ToList();
        outcomes.Count.ShouldBe(2);
        PropertyNames(outcomes[0]).ShouldBe(["updateId", "title", "resultCode", "hresult", "rebootRequired", "status"]);
        outcomes[0].GetProperty("updateId").GetString().ShouldBe(IdA);
        outcomes[0].GetProperty("title").GetString().ShouldBe("2026-10 Cumulative Update for Windows 11");
        outcomes[0].GetProperty("resultCode").GetInt32().ShouldBe(2);
        outcomes[0].GetProperty("hresult").GetInt32().ShouldBe(0);
        outcomes[0].GetProperty("rebootRequired").GetBoolean().ShouldBeTrue();
        outcomes[0].GetProperty("status").GetString().ShouldBe("PendingReboot");
        outcomes[1].GetProperty("status").GetString().ShouldBe("Failed");

        root.GetProperty("verified").GetBoolean().ShouldBeTrue();
        root.GetProperty("postScan").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task The_post_scan_is_byte_for_byte_the_document_a_scan_reports()
    {
        IReadOnlyList<SecurityUpdateInfo> offered =
        [
            new SecurityUpdateInfo(IdA, 3, "2026-10 Cumulative Update for Windows 11", "KB5044284", "Critical", false, 734_003_200),
            new SecurityUpdateInfo(IdB, 1, "Windows Malicious Software Removal Tool", null, null, false, 0),
            new SecurityUpdateInfo(IdC, 2, "2026-10 Cumulative Update Preview for Windows 11", "KB5044380", null, true, 2_000),
        ];

        var scan = await Scanner(new FakeSecurityUpdateService { ScanResult = SecurityUpdateScanResult.Ok(offered) })
            .ExecuteAsync(ScanTask());

        var installService = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                false, [new SecurityUpdateInstallItem(IdD, "Some security update", 2, 0)], [],
                Outcomes: [Outcome(IdD, "Some security update", UpdateOutcomeStatus.Installed)],
                PostScan: offered),
        };
        var install = await new InstallSecurityUpdateExecutor(
                installService, NullLogger<InstallSecurityUpdateExecutor>.Instance, new FixedClock(Now))
            .ExecuteAsync(InstallTask(InstallPayload(IdD)));

        var postScan = JsonDocument.Parse(install.ResultJson!).RootElement.GetProperty("postScan");

        // Same code path, same clock: the server can feed postScan to its scan parser unchanged.
        postScan.GetRawText().ShouldBe(scan.ResultJson);

        // The item shape is the scan's, KB ids and severity included (null when none).
        var items = postScan.GetProperty("updates").EnumerateArray().ToList();
        PropertyNames(items[0]).ShouldBe(["updateId", "revision", "title", "kbIds", "severity", "isPreview", "sizeBytes"]);
        items[1].GetProperty("kbIds").ValueKind.ShouldBe(JsonValueKind.Null);
        items[1].GetProperty("severity").ValueKind.ShouldBe(JsonValueKind.Null);

        // Previews are excluded from postScan exactly as from a scan.
        items.Select(u => u.GetProperty("updateId").GetString()).ToArray().ShouldBe([IdA, IdB]);
    }

    [Fact]
    public async Task Both_the_scan_and_the_post_scan_are_capped_at_the_servers_500()
    {
        var many = Enumerable.Range(0, 600)
            .Select(i => Info(new Guid(i, 0, 0, new byte[8]).ToString(), $"Security update {i}"))
            .ToList();

        var scan = await Scanner(new FakeSecurityUpdateService { ScanResult = SecurityUpdateScanResult.Ok(many) })
            .ExecuteAsync(ScanTask());
        JsonDocument.Parse(scan.ResultJson!).RootElement.GetProperty("updates").GetArrayLength().ShouldBe(500);

        var install = await Installer(new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                false, [new SecurityUpdateInstallItem(IdA, "x", 2, 0)], [],
                Outcomes: [Outcome(IdA, "x", UpdateOutcomeStatus.Installed)],
                PostScan: many),
        }).ExecuteAsync(InstallTask(InstallPayload(IdA)));
        JsonDocument.Parse(install.ResultJson!).RootElement
            .GetProperty("postScan").GetProperty("updates").GetArrayLength().ShouldBe(500);
    }

    [Fact]
    public async Task A_failed_verification_is_reported_unverified_with_no_post_scan_never_a_claim()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                false, [new SecurityUpdateInstallItem(IdA, "Security Update for Microsoft Defender", 2, 0)], [],
                Outcomes: [Outcome(IdA, "Security Update for Microsoft Defender", UpdateOutcomeStatus.InstalledUnverified)],
                PostScan: null),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA)));
        var root = JsonDocument.Parse(result.ResultJson!).RootElement;

        root.GetProperty("verified").GetBoolean().ShouldBeFalse();
        root.TryGetProperty("postScan", out var postScan).ShouldBeTrue("the key is always present");
        postScan.ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("outcomes").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("status").GetString().ShouldBe("InstalledUnverified");
        result.Message.ShouldBe("1 reported installed but could not be verified.");
    }

    [Fact]
    public async Task The_message_is_honest_per_outcome_class()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                true,
                [
                    new SecurityUpdateInstallItem(IdA, "A", 2, 0), new SecurityUpdateInstallItem(IdB, "B", 2, 0),
                    new SecurityUpdateInstallItem(IdC, "C", 2, 0), new SecurityUpdateInstallItem(IdD, "D", 2, 0),
                ],
                [],
                Outcomes:
                [
                    Outcome(IdA, "A", UpdateOutcomeStatus.Installed),
                    Outcome(IdB, "B", UpdateOutcomeStatus.PendingReboot, reboot: true),
                    Outcome(IdC, "C", UpdateOutcomeStatus.ReOffered),
                    Outcome(IdD, "D", UpdateOutcomeStatus.ReOffered),
                ],
                PostScan: [Info(IdC, "C"), Info(IdD, "D")]),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA, IdB, IdC, IdD)));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe(
            "Installed 1; 1 needs a restart to finish; 2 were re-offered by Windows Update and cannot be completed by the platform.");
    }

    [Fact]
    public async Task A_cumulative_update_awaiting_its_restart_is_pending_reboot_and_the_task_succeeds()
    {
        // The common security case: WUA installs the cumulative update, it needs a
        // restart, and the verification search still offers it until then. That is
        // PendingReboot and a success -- never ReOffered, never a failure.
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                true,
                [new SecurityUpdateInstallItem(IdA, "2026-10 Cumulative Update for Windows 11", 2, 0)],
                [],
                Outcomes: [Outcome(IdA, "2026-10 Cumulative Update for Windows 11", UpdateOutcomeStatus.PendingReboot, reboot: true)],
                PostScan: [Info(IdA, "2026-10 Cumulative Update for Windows 11")]),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA)));

        result.Succeeded.ShouldBeTrue("a pending restart is a successful install, not a failure");
        result.Message.ShouldBe("1 needs a restart to finish.");

        var root = JsonDocument.Parse(result.ResultJson!).RootElement;
        root.GetProperty("rebootRequired").GetBoolean().ShouldBeTrue();
        var outcome = root.GetProperty("outcomes").EnumerateArray().ShouldHaveSingleItem();
        outcome.GetProperty("status").GetString().ShouldBe("PendingReboot");
        outcome.GetProperty("rebootRequired").GetBoolean().ShouldBeTrue();
        root.GetProperty("verified").GetBoolean().ShouldBeTrue();
        root.GetProperty("postScan").GetProperty("updates").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("updateId").GetString().ShouldBe(IdA);
    }

    [Fact]
    public async Task A_requested_update_windows_no_longer_offers_is_itemized_even_when_others_install()
    {
        // Before 1.28.0 the operator selected two, the result said "Installed 1", and
        // nothing accounted for the second.
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                false, [new SecurityUpdateInstallItem(IdA, "Security Update for Microsoft Defender", 2, 0)], [],
                Outcomes:
                [
                    Outcome(IdA, "Security Update for Microsoft Defender", UpdateOutcomeStatus.Installed),
                    .. UpdateOutcomeClassifier.NotOffered(
                        [IdA, IdB], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IdA }),
                ],
                PostScan: []),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA, IdB)));

        result.Message.ShouldBe("Installed 1; 1 not offered by Windows Update.");
        var notOffered = JsonDocument.Parse(result.ResultJson!).RootElement.GetProperty("outcomes")
            .EnumerateArray().Single(o => o.GetProperty("updateId").GetString() == IdB);
        notOffered.GetProperty("status").GetString().ShouldBe("NotOffered");
        notOffered.GetProperty("resultCode").ValueKind.ShouldBe(JsonValueKind.Null);
        notOffered.GetProperty("hresult").ValueKind.ShouldBe(JsonValueKind.Null);
        notOffered.GetProperty("rebootRequired").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_install_that_could_not_run_still_itemizes_every_requested_update()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = SecurityUpdateInstallResult.ForError(
                "No security updates were installed: 1 no longer offered by Windows Update; 1 preview; 1 with a licence that could not be accepted.",
                [
                    Outcome(IdA, UpdateOutcomeClassifier.NotOfferedTitle, UpdateOutcomeStatus.NotOffered, null, null),
                    Outcome(IdB, "2026-10 Cumulative Update Preview for Windows 11", UpdateOutcomeStatus.SkippedPreview, null, null),
                    Outcome(IdC, "Some licensed update", UpdateOutcomeStatus.EulaDeclined, null, null),
                ]),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA, IdB, IdC)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldStartWith("No security updates were installed");

        var root = JsonDocument.Parse(result.ResultJson!).RootElement;
        root.GetProperty("installed").GetArrayLength().ShouldBe(0, "the legacy keys keep their pre-1.28.0 meaning on error");
        root.GetProperty("failed").GetArrayLength().ShouldBe(0);
        root.GetProperty("verified").GetBoolean().ShouldBeFalse();
        root.GetProperty("postScan").ValueKind.ShouldBe(JsonValueKind.Null);

        var statuses = root.GetProperty("outcomes").EnumerateArray().Select(o => o.GetProperty("status").GetString()).ToArray();
        statuses.ShouldBe(["NotOffered", "SkippedPreview", "EulaDeclined"]);
        root.GetProperty("outcomes").EnumerateArray()
            .ShouldAllBe(o => o.GetProperty("resultCode").ValueKind == JsonValueKind.Null
                && o.GetProperty("hresult").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_service_result_without_outcomes_is_accounted_for_conservatively_on_the_wire()
    {
        var service = new FakeSecurityUpdateService
        {
            InstallResult = new SecurityUpdateInstallResult(
                RebootRequired: false,
                Installed: [new SecurityUpdateInstallItem(IdA, "Security Update for Microsoft Defender", 2, 0)],
                Failed: [new SecurityUpdateInstallItem(IdB, "2026-10 Cumulative Update for Windows 11", 4, -1)]),
        };

        var result = await Installer(service).ExecuteAsync(InstallTask(InstallPayload(IdA, IdB)));
        var root = JsonDocument.Parse(result.ResultJson!).RootElement;

        var statuses = root.GetProperty("outcomes").EnumerateArray()
            .ToDictionary(o => o.GetProperty("updateId").GetString()!, o => o.GetProperty("status").GetString());
        statuses[IdA].ShouldBe("InstalledUnverified", "nothing verified it, so it is never claimed Installed");
        statuses[IdB].ShouldBe("Failed");
        root.GetProperty("verified").GetBoolean().ShouldBeFalse();
        root.GetProperty("postScan").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void A_legacy_security_result_is_accounted_for_conservatively_never_installed_or_re_offered()
    {
        // The security overload of the shared classifier's legacy mapping: the same
        // rule the driver path uses, and the one the server applies to a pre-1.28.0
        // security result.
        var noReboot = new SecurityUpdateInstallResult(
            RebootRequired: false,
            Installed: [new SecurityUpdateInstallItem(IdA, "Security Update for Microsoft Defender", 2, 0)],
            Failed: [new SecurityUpdateInstallItem(IdB, "2026-10 Cumulative Update for Windows 11", 4, unchecked((int)0x80240022))]);

        var outcomes = UpdateOutcomeClassifier.FromLegacy(noReboot);
        outcomes.Single(o => o.UpdateId == IdA).Status.ShouldBe(UpdateOutcomeStatus.InstalledUnverified);
        outcomes.Single(o => o.UpdateId == IdB).Status.ShouldBe(UpdateOutcomeStatus.Failed);
        outcomes.Single(o => o.UpdateId == IdB).HResult.ShouldBe(unchecked((int)0x80240022));
        outcomes.Single(o => o.UpdateId == IdB).RebootRequired.ShouldBeFalse();

        var withReboot = noReboot with { RebootRequired = true };
        var pending = UpdateOutcomeClassifier.FromLegacy(withReboot).Single(o => o.UpdateId == IdA);
        pending.Status.ShouldBe(UpdateOutcomeStatus.PendingReboot);
        pending.RebootRequired.ShouldBeTrue();
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
