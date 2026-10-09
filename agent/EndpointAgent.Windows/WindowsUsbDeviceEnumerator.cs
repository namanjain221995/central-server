using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// The identity-bearing properties of one devnode, as Windows stores them.
/// </summary>
/// <remarks>
/// Everything here comes from the device's own descriptors or from the driver
/// Windows bound to it — never from the friendly name, which the device chooses
/// for itself.
/// </remarks>
/// <param name="CompatibleIds">Semicolon-joined, in the order Windows lists them.</param>
/// <param name="ClassGuid">The device setup class, when the node has one.</param>
internal readonly record struct UsbNodeTraits(
    string InstanceId,
    string? Service,
    string? Class,
    string? CompatibleIds,
    Guid? ClassGuid = null);

/// <summary>
/// Enumerates USB devices with SetupAPI and classifies them.
/// </summary>
/// <remarks>
/// <para>
/// Enumerates the <c>USB</c> device tree — the physical devices, not their
/// function interfaces — so a stick or a phone appears once, keyed by the
/// instance ID that policy is written against. The <c>&amp;MI_nn</c> interface
/// children of a composite device are folded into their parent: their function
/// drivers decide the parent's class, and the parent is the node that gets
/// disabled.
/// </para>
/// <para>
/// Classification reads the driver service, the setup class and the compatible
/// IDs of the device and of its functions, never the friendly name. Names are
/// chosen by the device itself, so classifying on them would let a stick that
/// calls itself "USB Keyboard" avoid storage policy entirely.
/// </para>
/// <para>
/// A phone is recognised from the same kind of evidence as a stick. Windows
/// binds MTP and PTP devices to the Windows Portable Devices stack (setup class
/// <c>WPD</c>, service <c>WUDFWpdMtp</c>) and records
/// <c>USB\MS_COMP_MTP</c> / <c>USB\MS_COMP_PTP</c> / <c>USB\Class_06</c> in their
/// compatible IDs from the device's own descriptors. None of that is a drive
/// letter, which is why a phone never showed up as storage before: it is not
/// mass storage, it is a portable device, and it is now classified as one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbDeviceEnumerator(ILogger<WindowsUsbDeviceEnumerator> logger)
    : IUsbDeviceEnumerator
{
    /// <summary>
    /// Services that mean "this is removable mass storage".
    /// </summary>
    /// <remarks>
    /// <c>USBSTOR</c> is the classic bulk-only mass storage driver;
    /// <c>UASPStor</c> is USB Attached SCSI, which faster drives bind to
    /// instead. Missing the second one would leave a whole category of USB
    /// disks unrestricted, which is exactly the sort of gap that makes a
    /// control worthless.
    /// </remarks>
    private static readonly HashSet<string> StorageServices =
        new(StringComparer.OrdinalIgnoreCase) { "USBSTOR", "UASPStor" };

    /// <summary>
    /// Services of the Windows Portable Devices stack. <c>WUDFWpdMtp</c> is the
    /// in-box MTP/PTP driver on every supported Windows; the other two are its
    /// predecessors, kept so an old vendor driver cannot slip past.
    /// </summary>
    private static readonly HashSet<string> PortableDeviceServices =
        new(StringComparer.OrdinalIgnoreCase) { "WUDFWpdMtp", "WpdUsb", "WUDFWpdFs" };

    /// <summary>GUID_DEVCLASS_WPD — the setup class Windows gives every portable device.</summary>
    internal static readonly Guid PortableDeviceClassGuid = new("eec5ad98-8080-425f-922a-dabf3de3f69a");

    /// <summary>Hard cap on the devnode walk, so a malformed tree cannot spin a service thread.</summary>
    private const int MaxNodesPerWalk = 256;

    private readonly ILogger<WindowsUsbDeviceEnumerator> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public IReadOnlyList<UsbDeviceInfo> Enumerate()
    {
        var results = new List<UsbDeviceInfo>();

        var set = UsbNative.SetupDiGetClassDevs(
            IntPtr.Zero, "USB", IntPtr.Zero, UsbNative.DIGCF_PRESENT | UsbNative.DIGCF_ALLCLASSES);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            _logger.LogError(
                "SetupDiGetClassDevs failed for the USB enumerator (Win32 {Error}).",
                Marshal.GetLastWin32Error());
            return results;
        }

        // Built lazily, once per enumeration, and only if a device needs it.
        var registered = new RegisteredInterfaceIndex();

        try
        {
            var info = NewInfo();

            for (uint index = 0; UsbNative.SetupDiEnumDeviceInfo(set, index, ref info); index++)
            {
                var instanceId = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_InstanceId);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    info = NewInfo();
                    continue;
                }

                var traits = ReadTraits(set, ref info, instanceId);
                var manufacturer = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Manufacturer);
                var friendlyName = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_FriendlyName)
                    ?? GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_DeviceDesc);
                var hardwareIds = GetStringListProperty(set, ref info, UsbNative.DEVPKEY_Device_HardwareIds);

                var (vendorId, productId, serial) = ParseInstanceId(instanceId);

                results.Add(new UsbDeviceInfo(
                    instanceId,
                    Classify(traits, registered),
                    vendorId,
                    productId,
                    serial,
                    manufacturer,
                    friendlyName,
                    hardwareIds,
                    IsEnabled(instanceId)));

                info = NewInfo();
            }
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }

        return CollapseInterfaces(results);
    }

    /// <summary>
    /// Drops the <c>&amp;MI_nn</c> interface children of composite devices whose
    /// parent is in the list, so one physical device is one entry.
    /// </summary>
    /// <remarks>
    /// Windows enumerates the interfaces of a composite device as devnodes of
    /// their own under the <c>USB</c> enumerator, so without this a phone in
    /// "File transfer + USB debugging" mode is listed three times: the
    /// composite parent, the MTP interface and the ADB interface. Policy is
    /// applied to the parent — disabling it takes every function down at once,
    /// which is the point — and the parent's class is already decided from its
    /// functions, so the children add nothing but a second and third row that
    /// would each be restricted again separately. An interface whose parent is
    /// somehow absent is kept, because it is then the only thing that can be
    /// acted on.
    /// </remarks>
    internal static IReadOnlyList<UsbDeviceInfo> CollapseInterfaces(IReadOnlyList<UsbDeviceInfo> devices)
    {
        var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (!IsInterfaceInstance(device.InstanceId) && ProductKey(device) is { } key)
            {
                physical.Add(key);
            }
        }

        return devices
            .Where(d => !IsInterfaceInstance(d.InstanceId) || ProductKey(d) is not { } key || !physical.Contains(key))
            .ToList();
    }

    /// <summary>True for an interface of a composite device, which Windows names with <c>&amp;MI_</c>.</summary>
    internal static bool IsInterfaceInstance(string instanceId) =>
        instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    private static string? ProductKey(UsbDeviceInfo device) =>
        device.VendorId is { Length: > 0 } && device.ProductId is { Length: > 0 }
            ? ProductKey(device.VendorId, device.ProductId)
            : null;

    private static string ProductKey(string vendorId, string productId) => $"{vendorId}:{productId}";

    /// <summary>
    /// Splits <c>USB\VID_0781&amp;PID_5581\ABC123</c> into its parts.
    /// </summary>
    /// <remarks>
    /// The third segment is only treated as a serial when it is genuinely one.
    /// Windows synthesises an instance segment for devices that expose no
    /// serial — <c>7&amp;2f3c1b2&amp;0&amp;2</c> and similar — which encodes the
    /// port path, not the device. Reporting that as a serial would produce a
    /// grant that follows the USB port: unplug the approved stick, plug in a
    /// different one, and it would inherit the access. The ampersand is the
    /// reliable tell, so a segment containing one yields null.
    /// </remarks>
    internal static (string? VendorId, string? ProductId, string? Serial) ParseInstanceId(string instanceId)
    {
        var parts = instanceId.Split('\\');
        string? vendorId = null;
        string? productId = null;
        string? serial = null;

        if (parts.Length >= 2)
        {
            foreach (var token in parts[1].Split('&'))
            {
                if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                {
                    vendorId = token[4..];
                }
                else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                {
                    productId = token[4..];
                }
            }
        }

        if (parts.Length >= 3 && parts[2].Length > 0 && !parts[2].Contains('&', StringComparison.Ordinal))
        {
            serial = parts[2];
        }

        return (vendorId, productId, serial);
    }

    // ---- classification ----------------------------------------------------

    /// <summary>
    /// Decides what a present device is, from its own traits and the traits of
    /// its functions on the live devnode tree.
    /// </summary>
    internal static UsbClass Classify(
        string instanceId, string? service, string? deviceClass, string? compatibleIds = null) =>
        Classify(new UsbNodeTraits(instanceId, service, deviceClass, compatibleIds), registered: null);

    /// <summary>
    /// Classifies a present device by instance ID, reading its traits from
    /// Windows. Used by the enforcer to decide whether a device it has just
    /// enabled can have disks at all.
    /// </summary>
    internal static UsbClass ClassifyPresentInstance(string instanceId)
    {
        var set = UsbNative.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return UsbClass.Unknown;
        }

        try
        {
            var info = NewInfo();
            if (!UsbNative.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info))
            {
                return UsbClass.Unknown;
            }

            return Classify(ReadTraits(set, ref info, instanceId), new RegisteredInterfaceIndex());
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static UsbClass Classify(UsbNodeTraits device, RegisteredInterfaceIndex? registered)
    {
        // A hub is a hub, decided before anything else and never from what is
        // plugged into it. This ordering is not cosmetic: it is the guard that
        // stops a hub inheriting the class of its children — and it is also why
        // a hub's subtree is never walked at all.
        if (IsHub(device.InstanceId, device.Service))
        {
            return UsbClass.Hub;
        }

        var functions = new List<UsbNodeTraits>();
        CollectDescendantTraits(device.InstanceId, functions);

        var result = ClassifyFrom(device, functions);

        if (result is not (UsbClass.Unknown or UsbClass.Other) || registered is null)
        {
            return result;
        }

        // Nothing conclusive on the live tree. A composite device that has been
        // disabled — or has just been enabled and whose functions have not
        // started yet — has no child devnodes, so its functions are invisible
        // here; but Windows keeps every interface it ever installed for that
        // vendor/product in its device registry, and their recorded traits say
        // what the device is. Without this a restricted composite phone would
        // reappear as an anonymous "Other" and fall out of the policy table, the
        // same defect the compatible-ID rule fixed for sticks.
        var (vendorId, productId, _) = ParseInstanceId(device.InstanceId);
        if (vendorId is null || productId is null)
        {
            return result;
        }

        var stored = registered.InterfacesOf(vendorId, productId);
        return stored.Count == 0 ? result : ClassifyFrom(device, [.. functions, .. stored]);
    }

    /// <summary>
    /// The classification rules, over a device and the function nodes that
    /// belong to it. Pure: no Windows call, so every rule can be tested with
    /// synthetic trees.
    /// </summary>
    /// <remarks>
    /// Storage is checked first and wins over everything else — a composite
    /// device that contains storage <em>is</em> storage as far as this control
    /// is concerned, including a phone in the old USB-mass-storage mode. A
    /// portable device is next: anything that declares MTP, PTP, the still-image
    /// class or the ADB interface, or that Windows has bound to the portable
    /// device stack. Only then the inventory-only classes.
    /// </remarks>
    internal static UsbClass ClassifyFrom(UsbNodeTraits device, IReadOnlyList<UsbNodeTraits> functions)
    {
        if (IsHub(device.InstanceId, device.Service))
        {
            return UsbClass.Hub;
        }

        var nodes = new List<UsbNodeTraits>(functions.Count + 1) { device };
        nodes.AddRange(functions);

        // Compatible IDs come from the device's own descriptors and survive the
        // device being disabled, so they are checked before anything the
        // driver state could have removed.
        if (nodes.Any(n => DeclaresMassStorage(n.CompatibleIds)))
        {
            return UsbClass.Storage;
        }

        var services = nodes
            .Select(n => n.Service)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

        var classes = nodes
            .Select(n => n.Class)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .ToList();

        if (services.Any(StorageServices.Contains)
            || classes.Any(c => c.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbClass.Storage;
        }

        if (nodes.Any(n => DeclaresPortableDevice(n.CompatibleIds)) || nodes.Any(IsPortableDeviceNode))
        {
            return UsbClass.PortableDevice;
        }

        if (classes.Any(c => c.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbClass.Keyboard;
        }

        if (classes.Any(c => c.Equals("Mouse", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbClass.Mouse;
        }

        if (classes.Any(c => c.Equals("Net", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbClass.NetworkAdapter;
        }

        return classes.Count > 0 || services.Count > 0 ? UsbClass.Other : UsbClass.Unknown;
    }

    /// <summary>
    /// True for a hub, from the device's own identity only.
    /// </summary>
    /// <remarks>
    /// Checked before the storage rules and never from descendants, because the
    /// descendant walk reaches every device plugged into a hub. Without this
    /// ordering a hub with a USB stick attached collects <c>USBSTOR</c> from that
    /// stick and classifies as storage — and the agent then restricts the
    /// <em>hub</em>, taking every device on it down with it.
    /// </remarks>
    internal static bool IsHub(string instanceId, string? service) =>
        instanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)
        || (service is { Length: > 0 } && service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the node advertises the USB mass-storage interface class (08)
    /// in its compatible IDs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compatible IDs are written by the bus driver from the device's own
    /// descriptors and stay in the registry whether or not the device is
    /// started. That is what makes this the right signal for a device the agent
    /// has restricted: the driver service is gone and the child devnodes are
    /// gone, but <c>USB\Class_08&amp;SubClass_06&amp;Prot_50</c> remains.
    /// </para>
    /// <para>
    /// A composite device whose storage function sits behind an interface child
    /// advertises <c>USB\COMPOSITE</c> here instead; that case is covered by
    /// applying the same rule to the function nodes, live or recorded.
    /// </para>
    /// </remarks>
    internal static bool DeclaresMassStorage(string? compatibleIds) =>
        CompatibleIdTokens(compatibleIds).Any(id => MatchesClassToken(id, @"USB\Class_08"));

    /// <summary>
    /// True when the node advertises itself as a portable device: MTP or PTP by
    /// Microsoft OS descriptor, the USB still-image class (06, which is PTP),
    /// or the Android ADB interface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are what an Android phone records in each of its USB modes —
    /// <c>USB\MS_COMP_MTP</c> for "File transfer", <c>USB\MS_COMP_PTP</c> and
    /// <c>USB\Class_06&amp;SubClass_01&amp;Prot_01</c> for "Transfer photos",
    /// <c>USB\Class_FF&amp;SubClass_42&amp;Prot_01</c> for USB debugging — and what
    /// an iPhone records (still-image class) for its photo access. ADB is
    /// included because <c>adb push</c>/<c>adb pull</c> move files just as well
    /// as Explorer does.
    /// </para>
    /// <para>
    /// Each match is on a whole class token, as for storage: a rule that could
    /// be widened by appending a character is the wrong shape for something
    /// that decides whether a control applies.
    /// </para>
    /// </remarks>
    internal static bool DeclaresPortableDevice(string? compatibleIds) =>
        CompatibleIdTokens(compatibleIds).Any(IsPortableDeviceCompatibleId);

    private static bool IsPortableDeviceCompatibleId(string id) =>
        MatchesClassToken(id, @"USB\MS_COMP_MTP")
        || MatchesClassToken(id, @"USB\MS_COMP_PTP")
        || MatchesClassToken(id, @"USB\Class_06")
        || id.Equals(@"USB\Class_FF&SubClass_42&Prot_01", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when Windows has bound the node to the portable device stack —
    /// setup class <c>WPD</c>, by GUID or by name, or one of its drivers.
    /// </summary>
    internal static bool IsPortableDeviceNode(UsbNodeTraits node) =>
        node.ClassGuid == PortableDeviceClassGuid
        || (node.Class is { Length: > 0 } c && c.Equals("WPD", StringComparison.OrdinalIgnoreCase))
        || (node.Service is { Length: > 0 } s && PortableDeviceServices.Contains(s));

    private static IEnumerable<string> CompatibleIdTokens(string? compatibleIds) =>
        compatibleIds is { Length: > 0 }
            ? compatibleIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>
    /// One compatible ID, matched on the whole class token.
    /// </summary>
    /// <remarks>
    /// A plain prefix test would also accept <c>USB\Class_080</c>. USB class
    /// codes are two hex digits, so that is not a real device — but a
    /// classification rule that can be widened by appending a character is the
    /// wrong shape for something that decides whether a control applies.
    /// </remarks>
    private static bool MatchesClassToken(string id, string token) =>
        id.Equals(token, StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(token + "&", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the descendant walk may descend from one node into another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk exists to find the function drivers of <em>one physical
    /// device</em> — the <c>USBSTOR</c> node under a stick, the HID node under a
    /// keyboard. It must never cross into a different device, and the place that
    /// happens is a hub, whose children are every other device on the bus.
    /// </para>
    /// <para>
    /// The rule: a child not enumerated by <c>USB</c> belongs to this device
    /// (<c>USBSTOR\...</c>, <c>SCSI\...</c>, <c>HID\...</c>). A child that
    /// <em>is</em> <c>USB</c>-enumerated is another device on the bus — unless it
    /// is an interface of this same composite device, which Windows names with
    /// the same VID and PID plus an <c>&amp;MI_</c> segment.
    /// </para>
    /// </remarks>
    internal static bool MayDescendInto(string parentInstanceId, string childInstanceId)
    {
        if (!childInstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsInterfaceInstance(childInstanceId))
        {
            return false;
        }

        var (parentVid, parentPid, _) = ParseInstanceId(parentInstanceId);
        var (childVid, childPid, _) = ParseInstanceId(childInstanceId);

        return parentVid is not null
            && string.Equals(parentVid, childVid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parentPid, childPid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Walks the live devnode subtree, gathering the traits of this device's functions.</summary>
    private static void CollectDescendantTraits(string instanceId, List<UsbNodeTraits> functions)
    {
        if (UsbNative.CM_Locate_DevNode(out var root, instanceId, 0) != UsbNative.CR_SUCCESS)
        {
            return;
        }

        // Iterative, with a hard node cap. A cycle in the devnode tree should be
        // impossible, but "should be impossible" is a poor reason to let a
        // service thread spin forever inside a driver-supplied structure.
        var pending = new Stack<uint>();
        pending.Push(root);
        var visited = 0;

        while (pending.Count > 0 && visited++ < MaxNodesPerWalk)
        {
            var current = pending.Pop();

            if (UsbNative.CM_Get_Child(out var child, current, 0) == UsbNative.CR_SUCCESS)
            {
                pending.Push(child);

                var sibling = child;
                while (UsbNative.CM_Get_Sibling(out var next, sibling, 0) == UsbNative.CR_SUCCESS)
                {
                    pending.Push(next);
                    sibling = next;
                }
            }

            if (current == root)
            {
                continue;
            }

            var childId = GetDeviceId(current);
            if (childId is null)
            {
                continue;
            }

            // The boundary between "part of this device" and "a different device
            // that happens to hang off it". Crossing it is what made a hub look
            // like storage.
            if (!MayDescendInto(instanceId, childId))
            {
                continue;
            }

            ReadNodeTraits(childId, functions);
        }
    }

    private static void ReadNodeTraits(string instanceId, List<UsbNodeTraits> functions)
    {
        var set = UsbNative.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return;
        }

        try
        {
            var info = NewInfo();

            if (!UsbNative.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info))
            {
                return;
            }

            functions.Add(ReadTraits(set, ref info, instanceId));
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static UsbNodeTraits ReadTraits(IntPtr set, ref UsbNative.SP_DEVINFO_DATA info, string instanceId) =>
        new(
            instanceId,
            GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Service),
            GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Class),
            GetStringListProperty(set, ref info, UsbNative.DEVPKEY_Device_CompatibleIds),
            GetGuidProperty(set, ref info, UsbNative.DEVPKEY_Device_ClassGuid));

    /// <summary>
    /// The interfaces Windows has ever installed for each vendor/product, read
    /// once per enumeration and only if something asks.
    /// </summary>
    /// <remarks>
    /// Built from the same SetupAPI enumeration as the present list, minus the
    /// present-only flag, so it includes devnodes that are not currently
    /// attached or started. Reads nothing beyond service, class and compatible
    /// IDs — the same three traits used everywhere else.
    /// </remarks>
    private sealed class RegisteredInterfaceIndex
    {
        private Dictionary<string, List<UsbNodeTraits>>? _byProduct;

        public IReadOnlyList<UsbNodeTraits> InterfacesOf(string vendorId, string productId)
        {
            _byProduct ??= Build();
            return _byProduct.TryGetValue(ProductKey(vendorId, productId), out var interfaces) ? interfaces : [];
        }

        private static Dictionary<string, List<UsbNodeTraits>> Build()
        {
            var index = new Dictionary<string, List<UsbNodeTraits>>(StringComparer.OrdinalIgnoreCase);

            var set = UsbNative.SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, UsbNative.DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                return index;
            }

            try
            {
                var info = NewInfo();

                for (uint i = 0; UsbNative.SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    var instanceId = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_InstanceId);

                    if (instanceId is { Length: > 0 } && IsInterfaceInstance(instanceId))
                    {
                        var (vendorId, productId, _) = ParseInstanceId(instanceId);
                        if (vendorId is not null && productId is not null)
                        {
                            var key = ProductKey(vendorId, productId);
                            if (!index.TryGetValue(key, out var interfaces))
                            {
                                interfaces = [];
                                index[key] = interfaces;
                            }

                            interfaces.Add(ReadTraits(set, ref info, instanceId));
                        }
                    }

                    info = NewInfo();
                }
            }
            finally
            {
                UsbNative.SetupDiDestroyDeviceInfoList(set);
            }

            return index;
        }
    }

    internal static string? GetDeviceId(uint devInst)
    {
        if (UsbNative.CM_Get_Device_ID_Size(out var length, devInst, 0) != UsbNative.CR_SUCCESS || length == 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        if (UsbNative.CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) != UsbNative.CR_SUCCESS)
        {
            return null;
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    /// <summary>True when Windows has the device started rather than disabled.</summary>
    private static bool IsEnabled(string instanceId)
    {
        if (UsbNative.CM_Locate_DevNode(out var devInst, instanceId, 0) != UsbNative.CR_SUCCESS)
        {
            return false;
        }

        if (UsbNative.CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != UsbNative.CR_SUCCESS)
        {
            return false;
        }

        return (status & UsbNative.DN_HAS_PROBLEM) == 0 || problem != UsbNative.CM_PROB_DISABLED;
    }

    private static UsbNative.SP_DEVINFO_DATA NewInfo() => new()
    {
        CbSize = (uint)Marshal.SizeOf<UsbNative.SP_DEVINFO_DATA>(),
    };

    internal static string? GetStringProperty(
        IntPtr set, ref UsbNative.SP_DEVINFO_DATA info, UsbNative.DEVPROPKEY key)
    {
        UsbNative.SetupDiGetDeviceProperty(
            set, ref info, ref key, out _, null, 0, out var required, 0);

        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!UsbNative.SetupDiGetDeviceProperty(
                set, ref info, ref key, out var type, buffer, required, out _, 0))
        {
            return null;
        }

        if (type != UsbNative.DEVPROP_TYPE_STRING)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0') is { Length: > 0 } value ? value : null;
    }

    /// <summary>Reads a REG_MULTI_SZ-style property and joins it with semicolons.</summary>
    internal static string? GetStringListProperty(
        IntPtr set, ref UsbNative.SP_DEVINFO_DATA info, UsbNative.DEVPROPKEY key)
    {
        UsbNative.SetupDiGetDeviceProperty(
            set, ref info, ref key, out _, null, 0, out var required, 0);

        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!UsbNative.SetupDiGetDeviceProperty(
                set, ref info, ref key, out var type, buffer, required, out _, 0))
        {
            return null;
        }

        if (type is not (UsbNative.DEVPROP_TYPE_STRING_LIST or UsbNative.DEVPROP_TYPE_STRING))
        {
            return null;
        }

        var entries = Encoding.Unicode.GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return entries.Length == 0 ? null : string.Join(';', entries);
    }

    internal static Guid? GetGuidProperty(
        IntPtr set, ref UsbNative.SP_DEVINFO_DATA info, UsbNative.DEVPROPKEY key)
    {
        var buffer = new byte[16];
        if (!UsbNative.SetupDiGetDeviceProperty(
                set, ref info, ref key, out var type, buffer, (uint)buffer.Length, out var required, 0))
        {
            return null;
        }

        return type == UsbNative.DEVPROP_TYPE_GUID && required == 16 ? new Guid(buffer) : null;
    }
}
