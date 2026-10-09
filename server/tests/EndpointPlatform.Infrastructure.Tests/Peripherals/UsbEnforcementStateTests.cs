using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Infrastructure.Peripherals;
using Shouldly;
using Xunit;

namespace EndpointPlatform.Infrastructure.Tests.Peripherals;

/// <summary>
/// The one word the console shows for enforcement, derived from the decided
/// policy, the reported policy and how far the agent said it got. Pure, so it
/// runs without a database.
/// </summary>
public sealed class UsbEnforcementStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static UsbDevice Device(UsbDeviceClass deviceClass = UsbDeviceClass.PortableDevice) =>
        new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), @"USB\VID_2717&PID_FF40\EXAMPLE0SERIAL01",
            deviceClass, "2717", "FF40", "EXAMPLE0SERIAL01", "Xiaomi", "Redmi Note 14 Pro 5G", null, Now);

    [Fact]
    public void Nothing_reported_yet_is_pending()
    {
        UsbReadService.DescribeEnforcement(Device()).ShouldBe("Pending");
    }

    [Fact]
    public void Only_a_verified_state_is_enforced()
    {
        var verified = Device();
        verified.ReportEnforcement(UsbStoragePolicy.Restricted, null, Now, UsbEnforcementStatus.Verified);
        UsbReadService.DescribeEnforcement(verified).ShouldBe("Enforced");

        var unverified = Device();
        unverified.ReportEnforcement(UsbStoragePolicy.Restricted, "could not read back", Now, UsbEnforcementStatus.Unverified);
        UsbReadService.DescribeEnforcement(unverified).ShouldBe("Applied");

        var olderAgent = Device();
        olderAgent.ReportEnforcement(UsbStoragePolicy.Restricted, null, Now);
        UsbReadService.DescribeEnforcement(olderAgent).ShouldBe("Applied");
    }

    [Fact]
    public void A_restart_deferred_change_is_its_own_state()
    {
        var device = Device();
        device.ReportEnforcement(null, "held open", Now, UsbEnforcementStatus.RequiresRestart);

        UsbReadService.DescribeEnforcement(device).ShouldBe("RequiresRestart");
    }

    [Fact]
    public void A_failure_is_failed_whatever_policy_it_names()
    {
        var withStatus = Device();
        withStatus.ReportEnforcement(UsbStoragePolicy.Restricted, "read-only unavailable", Now, UsbEnforcementStatus.Failed);
        UsbReadService.DescribeEnforcement(withStatus).ShouldBe("Failed");

        var olderAgent = Device();
        olderAgent.ReportEnforcement(null, "access denied", Now);
        UsbReadService.DescribeEnforcement(olderAgent).ShouldBe("Failed");
    }

    [Fact]
    public void A_different_reported_policy_is_drifted_even_when_verified()
    {
        var device = Device();
        device.ReportEnforcement(UsbStoragePolicy.Enabled, null, Now, UsbEnforcementStatus.Verified);

        UsbReadService.DescribeEnforcement(device).ShouldBe("Drifted");
    }

    [Theory]
    [InlineData(UsbDeviceClass.Keyboard)]
    [InlineData(UsbDeviceClass.Mouse)]
    [InlineData(UsbDeviceClass.Hub)]
    [InlineData(UsbDeviceClass.NetworkAdapter)]
    [InlineData(UsbDeviceClass.Other)]
    [InlineData(UsbDeviceClass.Unknown)]
    public void Policy_does_not_apply_to_anything_else(UsbDeviceClass deviceClass)
    {
        var device = Device(deviceClass);
        device.ReportEnforcement(UsbStoragePolicy.Restricted, null, Now, UsbEnforcementStatus.Verified);

        UsbReadService.DescribeEnforcement(device).ShouldBe("NotApplicable");
    }
}
