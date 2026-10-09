using System.Runtime.InteropServices;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Phones, cameras and the other devices Windows reaches through MTP/PTP are
/// classified as portable devices, from the same kind of evidence that
/// identifies a stick as storage.
/// </summary>
/// <remarks>
/// <para>
/// The failure these pin: an Android phone in "File transfer" mode never
/// appears as a disk. Windows binds it to the Windows Portable Devices stack
/// (setup class <c>WPD</c>, service <c>WUDFWpdMtp</c>) and records
/// <c>USB\MS_COMP_MTP</c> in its compatible IDs. The classifier knew none of
/// those, so the phone landed on <c>Other</c> — inventory only, never
/// restricted — and the user could copy files both ways with the agent
/// watching.
/// </para>
/// <para>
/// The fixture values below are the stored properties of a real phone (a
/// Xiaomi Redmi Note 14 Pro 5G) read from the development machine's device
/// registry with <c>Get-PnpDeviceProperty</c>, in both of its USB modes. They
/// are what Windows actually writes, not an approximation.
/// </para>
/// </remarks>
public sealed class WindowsUsbPortableDeviceTests
{
    private const string PhoneMtpId = @"USB\VID_2717&PID_FF40\EXAMPLE0SERIAL01";
    private const string PhonePtpId = @"USB\VID_2717&PID_FF10\EXAMPLE0SERIAL01";
    private const string CompositePhoneId = @"USB\VID_18D1&PID_4EE2\R58M1234ABC";

    /// <summary>Compatible IDs Windows recorded for the phone in "File transfer" (MTP) mode.</summary>
    private const string MtpCompatibleIds =
        @"USB\MS_COMP_MTP;USB\COMPAT_VID_2717&Class_06&SubClass_01&Prot_01;USB\COMPAT_VID_2717&Class_06&SubClass_01;"
        + @"USB\COMPAT_VID_2717&Class_06;USB\Class_06&SubClass_01&Prot_01;USB\Class_06&SubClass_01;USB\Class_06";

    /// <summary>Compatible IDs Windows recorded for the same phone in "Transfer photos" (PTP) mode.</summary>
    private const string PtpCompatibleIds =
        @"USB\MS_COMP_PTP;USB\COMPAT_VID_2717&Class_06&SubClass_01&Prot_01;USB\COMPAT_VID_2717&Class_06&SubClass_01;"
        + @"USB\COMPAT_VID_2717&Class_06;USB\Class_06&SubClass_01&Prot_01;USB\Class_06&SubClass_01;USB\Class_06";

    private static readonly Guid WpdClass = new("eec5ad98-8080-425f-922a-dabf3de3f69a");

    private static UsbNodeTraits Node(
        string instanceId, string? service = null, string? deviceClass = null, string? compatibleIds = null,
        Guid? classGuid = null) =>
        new(instanceId, service, deviceClass, compatibleIds, classGuid);

    // ---- a phone as Windows actually records it ----------------------------

    [Fact]
    public void A_phone_in_file_transfer_mode_is_a_portable_device()
    {
        WindowsUsbDeviceEnumerator
            .Classify(PhoneMtpId, "WUDFWpdMtp", "WPD", MtpCompatibleIds)
            .ShouldBe(UsbClass.PortableDevice);
    }

    [Fact]
    public void A_phone_in_photo_transfer_mode_is_a_portable_device()
    {
        WindowsUsbDeviceEnumerator
            .Classify(PhonePtpId, "WUDFWpdMtp", "WPD", PtpCompatibleIds)
            .ShouldBe(UsbClass.PortableDevice);
    }

    /// <summary>
    /// A restricted phone still classifies as a portable device.
    /// </summary>
    /// <remarks>
    /// Disabling the devnode unloads the driver, so the service and class are
    /// no longer the evidence to rely on — the compatible IDs are, exactly as
    /// for a restricted stick. Without this a phone would be restricted once,
    /// reappear as "Other", drop out of the policy table, and never be
    /// grantable again.
    /// </remarks>
    [Theory]
    [InlineData(MtpCompatibleIds)]
    [InlineData(PtpCompatibleIds)]
    [InlineData(@"USB\MS_COMP_MTP")]
    [InlineData(@"USB\Class_06&SubClass_01&Prot_01")]
    public void A_restricted_phone_is_still_a_portable_device_by_its_compatible_ids(string compatibleIds)
    {
        WindowsUsbDeviceEnumerator
            .Classify(PhoneMtpId, null, null, compatibleIds)
            .ShouldBe(UsbClass.PortableDevice);
    }

    [Fact]
    public void The_portable_device_stack_is_recognised_by_class_guid_alone()
    {
        // A node whose class GUID is WPD but whose class name and service were
        // not readable — the GUID is the authoritative identity of the class.
        WindowsUsbDeviceEnumerator
            .ClassifyFrom(Node(PhoneMtpId, classGuid: WpdClass), [])
            .ShouldBe(UsbClass.PortableDevice);
    }

    [Theory]
    [InlineData("WUDFWpdMtp")]
    [InlineData("wudfwpdmtp")]
    [InlineData("WpdUsb")]
    [InlineData("WUDFWpdFs")]
    public void The_portable_device_drivers_are_recognised_by_service(string service)
    {
        WindowsUsbDeviceEnumerator
            .Classify(PhoneMtpId, service, null, null)
            .ShouldBe(UsbClass.PortableDevice);
    }

    // ---- the compatible-ID rule --------------------------------------------

    [Theory]
    [InlineData(@"USB\MS_COMP_MTP")]
    [InlineData(@"USB\MS_COMP_PTP")]
    [InlineData(@"USB\MS_COMP_MTP&MS_SUBCOMP_0000")]
    [InlineData(@"USB\Class_06")]
    [InlineData(@"USB\Class_06&SubClass_01")]
    [InlineData(@"USB\Class_06&SubClass_01&Prot_01")]
    [InlineData(@"USB\Class_FF&SubClass_42&Prot_01")]
    [InlineData(@"USB\COMPOSITE;USB\Class_FF&SubClass_42&Prot_01")]
    [InlineData(@" usb\ms_comp_mtp ; USB\Class_FF&SubClass_FF&Prot_00")]
    public void Mtp_ptp_still_image_and_adb_declare_a_portable_device(string compatibleIds)
    {
        WindowsUsbDeviceEnumerator.DeclaresPortableDevice(compatibleIds).ShouldBeTrue();
    }

    /// <summary>
    /// Nothing else is read as a portable device — in particular not a
    /// webcam (video class 0E), a printer (07), a HID device (03), the
    /// composite marker, a fastboot interface, or a class token with an extra
    /// character appended.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    [InlineData(@"USB\COMPOSITE")]
    [InlineData(@"USB\DevClass_00&SubClass_00&Prot_00;USB\COMPOSITE")]
    [InlineData(@"USB\Class_0E&SubClass_03&Prot_00")]
    [InlineData(@"USB\Class_07&SubClass_01&Prot_02")]
    [InlineData(@"USB\Class_03&SubClass_01&Prot_01")]
    [InlineData(@"USB\Class_08&SubClass_06&Prot_50")]
    [InlineData(@"USB\Class_060")]
    [InlineData(@"USB\Class_FF&SubClass_42&Prot_03")]
    [InlineData(@"USB\Class_FF&SubClass_42")]
    [InlineData(@"USB\MS_COMP_WINUSB")]
    [InlineData(@"USB\MS_COMP_MTPX")]
    [InlineData(@"USB\COMPAT_VID_2717&Class_06")]
    [InlineData(@"MS_COMP_MTP")]
    public void Nothing_else_declares_a_portable_device(string? compatibleIds)
    {
        WindowsUsbDeviceEnumerator.DeclaresPortableDevice(compatibleIds).ShouldBeFalse();
    }

    // ---- composite phones --------------------------------------------------

    /// <summary>
    /// A phone with USB debugging on is a composite device: the MTP function and
    /// the ADB function sit on interface children, and the parent's own
    /// compatible IDs say only "composite". The parent is what gets disabled,
    /// so the parent is what has to classify as a portable device.
    /// </summary>
    [Fact]
    public void A_composite_phone_is_classified_from_its_functions()
    {
        var parent = Node(CompositePhoneId, "usbccgp", "USB", @"USB\DevClass_00&SubClass_00&Prot_00;USB\COMPOSITE");
        var mtp = Node(@"USB\VID_18D1&PID_4EE2&MI_00\7&1A2B3C&0&0000", "WUDFWpdMtp", "WPD", @"USB\MS_COMP_MTP", WpdClass);
        var adb = Node(@"USB\VID_18D1&PID_4EE2&MI_01\7&1A2B3C&0&0001", "WinUSB", "USBDevice", @"USB\Class_FF&SubClass_42&Prot_01");

        WindowsUsbDeviceEnumerator.ClassifyFrom(parent, [mtp, adb]).ShouldBe(UsbClass.PortableDevice);
        WindowsUsbDeviceEnumerator.ClassifyFrom(parent, [adb]).ShouldBe(UsbClass.PortableDevice);
        WindowsUsbDeviceEnumerator.ClassifyFrom(parent, [mtp]).ShouldBe(UsbClass.PortableDevice);

        // With no functions visible at all — the parent disabled, or just
        // enabled and not yet started — the parent alone is inconclusive. The
        // enumerator then consults the interfaces Windows has recorded for the
        // product, which is the same call with the stored traits supplied.
        WindowsUsbDeviceEnumerator.ClassifyFrom(parent, []).ShouldBe(UsbClass.Other);
    }

    /// <summary>
    /// An iPhone presents a still-image (PTP) interface for photo access and a
    /// vendor-specific one for iTunes. The PTP interface is enough.
    /// </summary>
    [Fact]
    public void An_iphone_is_classified_from_its_still_image_interface()
    {
        var parent = Node(@"USB\VID_05AC&PID_12A8\00008030001234567890ABCD", "usbccgp", "USB", @"USB\COMPOSITE");
        var ptp = Node(@"USB\VID_05AC&PID_12A8&MI_00\8&2B3C4D&0&0000", "WUDFWpdMtp", "WPD",
            @"USB\Class_06&SubClass_01&Prot_01;USB\Class_06&SubClass_01;USB\Class_06", WpdClass);
        var mux = Node(@"USB\VID_05AC&PID_12A8&MI_01\8&2B3C4D&0&0001", "WinUSB", "USBDevice",
            @"USB\Class_FF&SubClass_FE&Prot_02");

        WindowsUsbDeviceEnumerator.ClassifyFrom(parent, [ptp, mux]).ShouldBe(UsbClass.PortableDevice);
    }

    // ---- precedence ---------------------------------------------------------

    /// <summary>
    /// Storage wins over portable: a phone in the old USB-mass-storage mode, or
    /// a composite that carries both, is storage — it can be read-only, and it
    /// is restricted either way.
    /// </summary>
    [Fact]
    public void Storage_wins_over_portable_device()
    {
        WindowsUsbDeviceEnumerator
            .Classify(PhoneMtpId, "USBSTOR", "WPD", @"USB\Class_08&SubClass_06&Prot_50;USB\MS_COMP_MTP")
            .ShouldBe(UsbClass.Storage);

        var parent = Node(CompositePhoneId, "usbccgp", "USB", @"USB\COMPOSITE");
        var storage = Node(@"USB\VID_18D1&PID_4EE2&MI_00\7&1&0&0000", null, "USB", @"USB\Class_08&SubClass_06&Prot_50");
        var mtp = Node(@"USB\VID_18D1&PID_4EE2&MI_01\7&1&0&0001", "WUDFWpdMtp", "WPD", @"USB\MS_COMP_MTP", WpdClass);

        WindowsUsbDeviceEnumerator.ClassifyFrom(parent, [mtp, storage]).ShouldBe(UsbClass.Storage);
    }

    /// <summary>
    /// A hub is a hub, whatever is plugged into it — a phone included. The
    /// subtree of a hub is every device on the bus, and the guard that stopped
    /// a hub becoming "storage" stops it becoming "portable" for the same reason.
    /// </summary>
    [Fact]
    public void A_hub_with_a_phone_plugged_in_is_still_a_hub()
    {
        var hub = Node(@"USB\ROOT_HUB30\4&3AF0ECE5&0&0", "USBHUB3", "USB");
        var phone = Node(PhoneMtpId, "WUDFWpdMtp", "WPD", MtpCompatibleIds, WpdClass);

        WindowsUsbDeviceEnumerator.ClassifyFrom(hub, [phone]).ShouldBe(UsbClass.Hub);
        WindowsUsbDeviceEnumerator.Classify(@"USB\VID_05E3&PID_0608\5&ABC&0&1", "USBHUB3", "USB", @"USB\MS_COMP_MTP")
            .ShouldBe(UsbClass.Hub);
    }

    /// <summary>
    /// Conflicting metadata resolves towards the control, never away from it: a
    /// device whose setup class says keyboard but whose descriptors declare MTP
    /// is a portable device, for the same reason a "keyboard" that binds
    /// USBSTOR is storage.
    /// </summary>
    [Fact]
    public void Conflicting_metadata_resolves_towards_the_control()
    {
        WindowsUsbDeviceEnumerator
            .Classify(PhoneMtpId, "kbdhid", "Keyboard", @"USB\MS_COMP_MTP")
            .ShouldBe(UsbClass.PortableDevice);
    }

    // ---- everything else is untouched ---------------------------------------

    /// <summary>
    /// The devices that must never be restricted still are not: input devices,
    /// network adapters, webcams, printers, audio, biometrics. A UVC webcam in
    /// particular is a camera that is <em>not</em> a portable device — it has no
    /// storage behind it — and must not be caught by the still-image rule.
    /// </summary>
    [Theory]
    [InlineData("kbdhid", "Keyboard", @"USB\Class_03&SubClass_01&Prot_01", UsbClass.Keyboard)]
    [InlineData("mouhid", "Mouse", @"USB\Class_03&SubClass_01&Prot_02", UsbClass.Mouse)]
    [InlineData("rndismp", "Net", @"USB\Class_E0&SubClass_01&Prot_03", UsbClass.NetworkAdapter)]
    [InlineData("usbvideo", "Camera", @"USB\Class_0E&SubClass_03&Prot_00;USB\Class_0E&SubClass_03;USB\Class_0E", UsbClass.Other)]
    [InlineData("usbprint", "USB", @"USB\Class_07&SubClass_01&Prot_02", UsbClass.Other)]
    [InlineData("usbaudio", "MEDIA", @"USB\Class_01&SubClass_01&Prot_00", UsbClass.Other)]
    [InlineData("WUDFRd", "Biometric", @"USB\Class_FF&SubClass_00&Prot_00", UsbClass.Other)]
    [InlineData("WINUSB", "USBDevice", @"USB\MS_COMP_WINUSB", UsbClass.Other)]
    public void Peripherals_that_must_never_be_restricted_keep_their_class(
        string service, string deviceClass, string compatibleIds, UsbClass expected)
    {
        WindowsUsbDeviceEnumerator
            .Classify(@"USB\VID_0000&PID_0000\NOTAREALDEVICE", service, deviceClass, compatibleIds)
            .ShouldBe(expected);
    }

    [Fact]
    public void A_device_with_nothing_to_go_on_is_still_unknown()
    {
        WindowsUsbDeviceEnumerator
            .ClassifyFrom(Node(@"USB\VID_0000&PID_0000\NOTAREALDEVICE"), [])
            .ShouldBe(UsbClass.Unknown);
    }

    /// <summary>
    /// The friendly name is not an input to classification at all. A device can
    /// call itself anything; the traits a device is classified on have no field
    /// for it.
    /// </summary>
    [Fact]
    public void The_friendly_name_plays_no_part_in_classification()
    {
        typeof(UsbNodeTraits).GetProperties().Select(p => p.Name)
            .ShouldBe(["InstanceId", "Service", "Class", "CompatibleIds", "ClassGuid"], ignoreOrder: true);
    }

    // ---- one physical device, one entry ---------------------------------------

    /// <summary>
    /// Interface children of a composite device are folded into their parent.
    /// </summary>
    /// <remarks>
    /// Without this a phone in "File transfer + USB debugging" mode is three
    /// rows — the composite parent, the MTP interface, the ADB interface — and
    /// each of the three would be restricted on its own, while the only node
    /// whose disable takes the whole phone down is the parent.
    /// </remarks>
    [Fact]
    public void Interface_children_are_folded_into_their_composite_parent()
    {
        var parent = Device(CompositePhoneId, UsbClass.PortableDevice, "18D1", "4EE2");
        var mtp = Device(@"USB\VID_18D1&PID_4EE2&MI_00\7&1A2B3C&0&0000", UsbClass.PortableDevice, "18D1", "4EE2");
        var adb = Device(@"USB\VID_18D1&PID_4EE2&MI_01\7&1A2B3C&0&0001", UsbClass.Other, "18D1", "4EE2");
        var hub = Device(@"USB\ROOT_HUB30\4&3AF0ECE5&0&0", UsbClass.Hub, null, null);
        var keyboard = Device(@"USB\VID_046D&PID_C31C\5&12345&0&1", UsbClass.Keyboard, "046D", "C31C");

        var collapsed = WindowsUsbDeviceEnumerator.CollapseInterfaces([mtp, parent, adb, hub, keyboard]);

        collapsed.Select(d => d.InstanceId).ShouldBe(
            [CompositePhoneId, hub.InstanceId, keyboard.InstanceId], ignoreOrder: true);
    }

    [Fact]
    public void An_interface_whose_parent_is_absent_is_kept()
    {
        var orphan = Device(@"USB\VID_18D1&PID_4EE2&MI_00\7&1A2B3C&0&0000", UsbClass.PortableDevice, "18D1", "4EE2");
        var other = Device(@"USB\VID_9999&PID_1111\SERIAL", UsbClass.Other, "9999", "1111");

        WindowsUsbDeviceEnumerator.CollapseInterfaces([orphan, other])
            .Select(d => d.InstanceId)
            .ShouldBe([orphan.InstanceId, other.InstanceId], ignoreOrder: true);
    }

    private static UsbDeviceInfo Device(string instanceId, UsbClass usbClass, string? vendorId, string? productId) =>
        new(instanceId, usbClass, vendorId, productId, null, null, null, null, IsEnabled: true);

    // ---- against this machine ------------------------------------------------

    /// <summary>
    /// On real hardware: no interface child is listed beside its parent, every
    /// portable device is restrictable, and enumeration is still well formed.
    /// Read-only SetupAPI queries, safe anywhere.
    /// </summary>
    [Fact]
    public void Real_enumeration_lists_each_physical_device_once()
    {
        var devices = new WindowsUsbDeviceEnumerator(NullLogger<WindowsUsbDeviceEnumerator>.Instance).Enumerate();

        var physical = devices
            .Where(d => !WindowsUsbDeviceEnumerator.IsInterfaceInstance(d.InstanceId))
            .Where(d => d.VendorId is not null && d.ProductId is not null)
            .Select(d => $"{d.VendorId}:{d.ProductId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices.Where(d => WindowsUsbDeviceEnumerator.IsInterfaceInstance(d.InstanceId)))
        {
            physical.ShouldNotContain($"{device.VendorId}:{device.ProductId}",
                $"{device.InstanceId} is an interface of a device that is itself listed");
        }

        foreach (var device in devices.Where(d => d.Class == UsbClass.PortableDevice))
        {
            device.Class.IsRestrictable().ShouldBeTrue();
            device.Class.SupportsReadOnly().ShouldBeFalse();
        }
    }

    // ---- the watcher ---------------------------------------------------------

    [Theory]
    [InlineData(0u, UsbChangeKind.Arrived)]
    [InlineData(1u, UsbChangeKind.Removed)]
    [InlineData(2u, null)]
    [InlineData(uint.MaxValue, null)]
    public void Only_interface_arrival_and_removal_are_reported(uint action, UsbChangeKind? expected)
    {
        WindowsUsbDeviceWatcher.ChangeKindOf(action).ShouldBe(expected);
    }

    /// <summary>
    /// The symbolic link is read out of the event buffer without running past
    /// it, whether or not the string is terminated.
    /// </summary>
    [Fact]
    public void The_symbolic_link_is_read_within_the_event_buffer()
    {
        const string link = @"\\?\USB#VID_2717&PID_FF40#EXAMPLE0SERIAL01#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";
        const int header = 24;

        var payload = System.Text.Encoding.Unicode.GetBytes(link + "\0");
        var buffer = Marshal.AllocHGlobal(header + payload.Length);
        try
        {
            Marshal.Copy(new byte[header], 0, buffer, header);
            Marshal.Copy(payload, 0, buffer + header, payload.Length);

            var size = (uint)(header + payload.Length);
            WindowsUsbDeviceWatcher.SymbolicLinkOf(buffer, size).ShouldBe(link);

            // Unterminated: an event whose size ends mid-string yields exactly the
            // characters inside the buffer and nothing beyond it.
            WindowsUsbDeviceWatcher.SymbolicLinkOf(buffer, header + 10 * sizeof(char)).ShouldBe(link[..10]);

            WindowsUsbDeviceWatcher.SymbolicLinkOf(buffer, header).ShouldBeNull();
            WindowsUsbDeviceWatcher.SymbolicLinkOf(IntPtr.Zero, size).ShouldBeNull();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The notification registration itself works on this machine: the filter
    /// structure is laid out as cfgmgr32 expects, the registration is accepted,
    /// and it can be withdrawn. Registering for notifications changes nothing.
    /// </summary>
    [Fact]
    public void Pnp_notifications_can_be_registered_and_unregistered()
    {
        using var watcher = new WindowsUsbDeviceWatcher(NullLogger<WindowsUsbDeviceWatcher>.Instance);

        watcher.TryStart().ShouldBeTrue();
        watcher.UsesPnpNotifications.ShouldBeTrue("the WMI fallback should not have been needed");
    }
}
