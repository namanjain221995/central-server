using System.Runtime.InteropServices;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Drivers;
using EndpointAgent.Windows;
using Microsoft.CSharp.RuntimeBinder;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The per-update reboot read in the WUA install path (agent 1.27.0). The rule:
/// when the late-bound <c>IUpdateInstallationResult.RebootRequired</c> read fails,
/// fall back to the install's AGGREGATE flag, never to false -- so the worst failure
/// mode is the conservative PendingReboot, never a false ReOffered.
/// </summary>
public sealed class WindowsDriverUpdateServiceTests
{
    public static TheoryData<Exception> ReadFailures() => new()
    {
        new RuntimeBinderException("'System.__ComObject' does not contain a definition for 'RebootRequired'"),
        new COMException("RPC failure", unchecked((int)0x800706BA)),
        new InvalidCastException("not a bool"),
    };

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void An_unreadable_per_update_flag_falls_back_to_the_aggregate_when_it_says_reboot(Exception failure)
    {
        WindowsDriverUpdateService.ReadPerUpdateReboot(() => throw failure, aggregateReboot: true).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void An_unreadable_per_update_flag_falls_back_to_the_aggregate_when_it_says_no_reboot(Exception failure)
    {
        WindowsDriverUpdateService.ReadPerUpdateReboot(() => throw failure, aggregateReboot: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_readable_per_update_flag_wins_over_the_aggregate(bool perUpdate, bool aggregate)
    {
        // Per-update precision in a mixed batch: the update's own answer is used.
        WindowsDriverUpdateService.ReadPerUpdateReboot(() => perUpdate, aggregate).ShouldBe(perUpdate);
    }

    [Fact]
    public void An_unexpected_exception_is_not_swallowed_as_a_reboot_answer()
    {
        Should.Throw<OutOfMemoryException>(() =>
            WindowsDriverUpdateService.ReadPerUpdateReboot(() => throw new OutOfMemoryException(), aggregateReboot: true));
    }

    [Fact]
    public void A_binder_failure_on_a_still_offered_update_with_the_aggregate_set_is_pending_reboot_never_re_offered()
    {
        // End to end through the two rules the install applies: the fallback, then the
        // classifier with the machine's own flag clear and the update still offered.
        var itemReboot = WindowsDriverUpdateService.ReadPerUpdateReboot(
            () => throw new RuntimeBinderException("binder"), aggregateReboot: true);
        const bool systemReboot = false;

        DriverUpdateOutcomeClassifier.Classify(2, itemReboot || systemReboot, stillOffered: true)
            .ShouldBe(DriverUpdateOutcomeStatus.PendingReboot);
    }
}
