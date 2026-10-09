using EndpointPlatform.Domain.Peripherals;

namespace EndpointPlatform.Domain.Tests.Peripherals;

/// <summary>
/// A phone is governed like a stick — restricted first, grantable by an
/// administrator, time-boxed — with one difference the domain has to enforce:
/// it has no read-only mode. And the enforcement report now carries how far
/// the endpoint got, which decides what the console may call protected.
/// </summary>
public sealed class UsbPortableDeviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static UsbDevice Phone(UsbDeviceClass deviceClass = UsbDeviceClass.PortableDevice) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            @"USB\VID_2717&PID_FF40\EXAMPLE0SERIAL01",
            deviceClass,
            "2717",
            "FF40",
            "EXAMPLE0SERIAL01",
            "Xiaomi",
            "Redmi Note 14 Pro 5G",
            @"USB\VID_2717&PID_FF40",
            Now);

    [Fact]
    public void A_portable_device_is_restricted_the_moment_it_is_first_seen()
    {
        var phone = Phone();

        phone.IsPortableDevice.ShouldBeTrue();
        phone.IsStorage.ShouldBeFalse();
        phone.IsRestrictable.ShouldBeTrue();
        phone.SupportsReadOnly.ShouldBeFalse();
        phone.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        phone.HasLiveGrant(Now).ShouldBeFalse();
    }

    [Fact]
    public void A_portable_device_can_be_granted_read_write_access()
    {
        var phone = Phone();

        phone.Grant(UsbStoragePolicy.Enabled, Now.AddHours(1), Now);

        phone.Policy.ShouldBe(UsbStoragePolicy.Enabled);
        phone.HasLiveGrant(Now.AddMinutes(59)).ShouldBeTrue();
        phone.HasLiveGrant(Now.AddMinutes(61)).ShouldBeFalse();
    }

    /// <summary>
    /// Read-only is refused outright, not rounded: up would be write access
    /// nobody asked for, down would be a grant the endpoint cannot honour.
    /// </summary>
    [Fact]
    public void A_portable_device_cannot_be_granted_read_only()
    {
        var phone = Phone();

        Should.Throw<InvalidOperationException>(() => phone.Grant(UsbStoragePolicy.ReadOnly, Now.AddHours(1), Now));

        phone.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        phone.PolicyExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void Restricted_is_still_not_grantable_for_a_portable_device()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => Phone().Grant(UsbStoragePolicy.Restricted, Now.AddHours(1), Now));
    }

    [Fact]
    public void Storage_still_supports_both_levels()
    {
        var stick = Phone(UsbDeviceClass.Storage);
        stick.SupportsReadOnly.ShouldBeTrue();

        stick.Grant(UsbStoragePolicy.ReadOnly, Now.AddHours(1), Now);
        stick.Policy.ShouldBe(UsbStoragePolicy.ReadOnly);

        stick.Restrict();
        stick.Grant(UsbStoragePolicy.Enabled, Now.AddHours(1), Now);
        stick.Policy.ShouldBe(UsbStoragePolicy.Enabled);
    }

    [Theory]
    [InlineData(UsbDeviceClass.Keyboard)]
    [InlineData(UsbDeviceClass.Mouse)]
    [InlineData(UsbDeviceClass.Hub)]
    [InlineData(UsbDeviceClass.NetworkAdapter)]
    [InlineData(UsbDeviceClass.Other)]
    [InlineData(UsbDeviceClass.Unknown)]
    public void Everything_else_is_still_not_restrictable(UsbDeviceClass deviceClass)
    {
        var device = Phone(deviceClass);

        device.IsRestrictable.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => device.Grant(UsbStoragePolicy.Enabled, Now.AddHours(1), Now));
    }

    // ---- how far the endpoint got --------------------------------------------

    [Fact]
    public void A_verified_report_is_enforced_and_verified()
    {
        var phone = Phone();

        phone.ReportEnforcement(UsbStoragePolicy.Restricted, null, Now, UsbEnforcementStatus.Verified);

        phone.IsPolicyEnforced.ShouldBeTrue();
        phone.IsEnforcementVerified.ShouldBeTrue();
        phone.HasEnforcementFailure.ShouldBeFalse();
    }

    /// <summary>
    /// Applied but not read back is enforced as far as the agent knows, and is
    /// never promoted to verified. The note travels in the error field.
    /// </summary>
    [Fact]
    public void An_unverified_report_is_enforced_but_not_verified()
    {
        var phone = Phone();

        phone.ReportEnforcement(
            UsbStoragePolicy.Restricted, "the device state could not be read back", Now,
            UsbEnforcementStatus.Unverified);

        phone.IsPolicyEnforced.ShouldBeTrue();
        phone.IsEnforcementVerified.ShouldBeFalse();
        phone.HasEnforcementFailure.ShouldBeFalse();
        phone.EnforcementError.ShouldBe("the device state could not be read back");
    }

    [Fact]
    public void A_restart_required_report_is_not_enforced()
    {
        var phone = Phone();

        phone.ReportEnforcement(null, "a program is holding the device open", Now, UsbEnforcementStatus.RequiresRestart);

        phone.IsPolicyEnforced.ShouldBeFalse();
        phone.HasEnforcementFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_failed_report_is_not_enforced_even_if_it_names_the_right_policy()
    {
        var phone = Phone();

        phone.ReportEnforcement(UsbStoragePolicy.Restricted, "access denied", Now, UsbEnforcementStatus.Failed);

        phone.IsPolicyEnforced.ShouldBeFalse();
        phone.HasEnforcementFailure.ShouldBeTrue();
    }

    /// <summary>
    /// Reports from agents before 1.16.0 carry no status. For them an error
    /// means failure and no error means applied — and never verified.
    /// </summary>
    [Fact]
    public void A_report_without_a_status_is_read_the_old_way()
    {
        var phone = Phone();

        phone.ReportEnforcement(UsbStoragePolicy.Restricted, null, Now);
        phone.EnforcementStatus.ShouldBeNull();
        phone.IsPolicyEnforced.ShouldBeTrue();
        phone.IsEnforcementVerified.ShouldBeFalse();

        phone.ReportEnforcement(UsbStoragePolicy.Restricted, "access denied", Now);
        phone.IsPolicyEnforced.ShouldBeFalse();
        phone.HasEnforcementFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_later_report_replaces_the_status_rather_than_accumulating_it()
    {
        var phone = Phone();

        phone.ReportEnforcement(null, "a program is holding the device open", Now, UsbEnforcementStatus.RequiresRestart);
        phone.ReportEnforcement(UsbStoragePolicy.Restricted, null, Now.AddMinutes(1), UsbEnforcementStatus.Verified);

        phone.EnforcementStatus.ShouldBe(UsbEnforcementStatus.Verified);
        phone.EnforcementError.ShouldBeNull();
        phone.IsEnforcementVerified.ShouldBeTrue();
    }
}
