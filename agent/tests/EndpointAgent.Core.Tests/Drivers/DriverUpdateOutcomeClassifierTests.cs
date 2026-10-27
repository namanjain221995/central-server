using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Drivers;

namespace EndpointAgent.Core.Tests.Drivers;

/// <summary>
/// The classification table every installed driver update goes through (agent
/// 1.27.0, driver-update-truth contract section A). The distinctions that matter:
/// a reboot-pending update is never branded ReOffered; an unverifiable install is
/// never claimed Installed; and nothing the operator requested disappears.
/// </summary>
public sealed class DriverUpdateOutcomeClassifierTests
{
    private const string IdA = "11111111-1111-1111-1111-111111111111";
    private const string IdB = "22222222-2222-2222-2222-222222222222";
    private const string IdC = "33333333-3333-3333-3333-333333333333";

    [Theory]
    // Succeeded, no restart: the verification search decides.
    [InlineData(2, false, false, DriverUpdateOutcomeStatus.Installed)]
    [InlineData(3, false, false, DriverUpdateOutcomeStatus.Installed)]
    [InlineData(2, false, true, DriverUpdateOutcomeStatus.ReOffered)]
    [InlineData(3, false, true, DriverUpdateOutcomeStatus.ReOffered)]
    // Succeeded, verification unavailable: unverified, never a guess either way.
    [InlineData(2, false, null, DriverUpdateOutcomeStatus.InstalledUnverified)]
    [InlineData(3, false, null, DriverUpdateOutcomeStatus.InstalledUnverified)]
    // Succeeded with a restart required: PendingReboot whatever the search said --
    // still offered is NOT re-offered until a post-reboot scan says so.
    [InlineData(2, true, true, DriverUpdateOutcomeStatus.PendingReboot)]
    [InlineData(2, true, false, DriverUpdateOutcomeStatus.PendingReboot)]
    [InlineData(2, true, null, DriverUpdateOutcomeStatus.PendingReboot)]
    [InlineData(3, true, true, DriverUpdateOutcomeStatus.PendingReboot)]
    // Anything but success is Failed, reboot flag and search notwithstanding.
    [InlineData(4, false, false, DriverUpdateOutcomeStatus.Failed)]
    [InlineData(4, true, true, DriverUpdateOutcomeStatus.Failed)]
    [InlineData(5, false, null, DriverUpdateOutcomeStatus.Failed)]
    [InlineData(0, false, true, DriverUpdateOutcomeStatus.Failed)]
    [InlineData(1, false, false, DriverUpdateOutcomeStatus.Failed)]
    public void Each_installer_result_lands_in_exactly_one_status(
        int resultCode, bool rebootRequired, bool? stillOffered, DriverUpdateOutcomeStatus expected)
    {
        DriverUpdateOutcomeClassifier.Classify(resultCode, rebootRequired, stillOffered).ShouldBe(expected);
    }

    [Fact]
    public void A_still_offered_update_with_any_reboot_flag_is_pending_reboot_never_re_offered()
    {
        // The trap the asymmetry exists to avoid: a healthy driver awaiting its
        // restart is still offered by the search, and must not read "cannot complete".
        foreach (var resultCode in new[] { 2, 3 })
        {
            DriverUpdateOutcomeClassifier.Classify(resultCode, rebootRequired: true, stillOffered: true)
                .ShouldNotBe(DriverUpdateOutcomeStatus.ReOffered);
        }
    }

    [Fact]
    public void Requested_updates_the_search_did_not_find_are_itemized_as_not_offered_in_request_order()
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IdB.ToUpperInvariant() };

        var outcomes = DriverUpdateOutcomeClassifier.NotOffered([IdC, IdB, IdA, IdC], matched);

        // Unmatched ids, in request order, each once; matching is case-insensitive.
        outcomes.Select(o => o.UpdateId).ToArray().ShouldBe([IdC, IdA]);
        outcomes.ShouldAllBe(o => o.Status == DriverUpdateOutcomeStatus.NotOffered);
        outcomes.ShouldAllBe(o => o.ResultCode == null && o.HResult == null, "it never reached the installer");
        outcomes.ShouldAllBe(o => !o.RebootRequired);
        outcomes.ShouldAllBe(o => !string.IsNullOrWhiteSpace(o.Title), "a strict reader must never trip on an empty title");
    }

    [Fact]
    public void Nothing_is_not_offered_when_every_request_matched()
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IdA, IdB };

        DriverUpdateOutcomeClassifier.NotOffered([IdA, IdB], matched).ShouldBeEmpty();
    }

    [Fact]
    public void The_wire_names_are_exactly_the_contract_strings()
    {
        Enum.GetValues<DriverUpdateOutcomeStatus>()
            .Select(DriverUpdateOutcomeStatusNames.ToWire)
            .ShouldBe(
                ["Installed", "InstalledUnverified", "PendingReboot", "ReOffered", "Failed", "NotOffered", "SkippedPreview", "EulaDeclined"],
                ignoreOrder: true);
    }

    [Fact]
    public void A_legacy_result_is_accounted_for_conservatively_never_installed_or_re_offered()
    {
        var noReboot = new DriverUpdateInstallResult(
            RebootRequired: false,
            Installed: [new DriverUpdateInstallItem(IdA, "Intel Ethernet", 2, 0)],
            Failed: [new DriverUpdateInstallItem(IdB, "Realtek Audio", 4, unchecked((int)0x80240022))]);

        var outcomes = DriverUpdateOutcomeClassifier.FromLegacy(noReboot);
        outcomes.Single(o => o.UpdateId == IdA).Status.ShouldBe(DriverUpdateOutcomeStatus.InstalledUnverified);
        outcomes.Single(o => o.UpdateId == IdB).Status.ShouldBe(DriverUpdateOutcomeStatus.Failed);
        outcomes.Single(o => o.UpdateId == IdB).HResult.ShouldBe(unchecked((int)0x80240022));

        var withReboot = noReboot with { RebootRequired = true };
        DriverUpdateOutcomeClassifier.FromLegacy(withReboot)
            .Single(o => o.UpdateId == IdA).Status.ShouldBe(DriverUpdateOutcomeStatus.PendingReboot);
    }
}
