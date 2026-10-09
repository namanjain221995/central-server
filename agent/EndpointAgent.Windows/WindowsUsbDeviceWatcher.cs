using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Raises an event when a USB device arrives or is removed.
/// </summary>
/// <remarks>
/// <para>
/// The primary mechanism is <c>CM_Register_Notification</c> from CfgMgr32: a
/// push notification for arrival and removal of the <c>GUID_DEVINTERFACE_USB_DEVICE</c>
/// interface, which every USB device that is not a hub exposes the moment it
/// enumerates — before any function driver loads, so a phone is reported the
/// instant it is attached whatever mode it is in. It is the documented way for
/// a process without a window to receive PnP notifications, it works from a
/// service in Session 0, and it polls nothing: the kernel calls back.
/// </para>
/// <para>
/// If registration fails — it should not, on any supported Windows — the
/// watcher falls back to WMI <c>__InstanceCreationEvent</c> /
/// <c>__InstanceDeletionEvent</c> over <c>Win32_PnPEntity</c>, which WMI
/// implements by polling the PnP tree at the interval given in the query. That
/// was the original mechanism; it is kept only as the fallback because it
/// costs a sweep of every PnP entity on the machine every second.
/// </para>
/// <para>
/// Either way this is latency, not enforcement. If neither mechanism can start,
/// the agent falls back to its periodic reconcile, so a device still becomes
/// restricted; it simply takes until the next sweep rather than a second or
/// two. Nothing is left permanently unmanaged by this class failing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbDeviceWatcher(ILogger<WindowsUsbDeviceWatcher> logger) : IUsbDeviceWatcher
{
    private readonly ILogger<WindowsUsbDeviceWatcher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    // Held in a field so the garbage collector cannot reclaim the delegate
    // while the kernel still holds the function pointer it was marshalled to.
    private UsbNative.CM_NOTIFY_CALLBACK? _callback;
    private IntPtr _notification;

    private ManagementEventWatcher? _arrival;
    private ManagementEventWatcher? _removal;
    private bool _disposed;

    public event EventHandler<UsbChangeKind>? Changed;

    /// <summary>True while the kernel notification registration is in place (not the WMI fallback).</summary>
    internal bool UsesPnpNotifications => _notification != IntPtr.Zero;

    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (TryStartPnpNotifications())
        {
            return true;
        }

        return TryStartWmiFallback();
    }

    private bool TryStartPnpNotifications()
    {
        var filter = UsbNative.CM_NOTIFY_FILTER.ForDeviceInterface(UsbNative.GuidDevInterfaceUsbDevice);
        var callback = new UsbNative.CM_NOTIFY_CALLBACK(OnNotification);

        var result = UsbNative.CM_Register_Notification(ref filter, IntPtr.Zero, callback, out var notification);

        if (result != UsbNative.CR_SUCCESS)
        {
            _logger.LogWarning(
                "CM_Register_Notification failed (CONFIGRET 0x{Result:X}); falling back to WMI device events.",
                result);
            return false;
        }

        _callback = callback;
        _notification = notification;

        _logger.LogInformation("Watching for USB device arrival and removal (PnP notifications).");
        return true;
    }

    /// <summary>
    /// The kernel's callback. Runs on a thread the system owns, so it must never
    /// throw and must never block.
    /// </summary>
    private uint OnNotification(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        try
        {
            if (ChangeKindOf(action) is { } kind)
            {
                _logger.LogDebug(
                    "USB device {Kind}: {Interface}", kind, SymbolicLinkOf(eventData, eventDataSize) ?? "(unknown)");

                Changed?.Invoke(this, kind);
            }
        }
        catch (Exception ex)
        {
            // A handler throwing on the kernel's callback thread would take the
            // process down. The reconcile it was meant to trigger still happens
            // on the timer.
            _logger.LogError(ex, "A USB device-change handler threw.");
        }

        // ERROR_SUCCESS. Anything else would end the registration.
        return 0;
    }

    /// <summary>Maps a CM_NOTIFY_ACTION onto the two events this watcher reports.</summary>
    internal static UsbChangeKind? ChangeKindOf(uint action) => action switch
    {
        UsbNative.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL => UsbChangeKind.Arrived,
        UsbNative.CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL => UsbChangeKind.Removed,
        _ => null,
    };

    /// <summary>
    /// Reads the symbolic link out of a device-interface CM_NOTIFY_EVENT_DATA,
    /// for logging. Returns null rather than reading past the buffer when the
    /// event is too short to carry one.
    /// </summary>
    internal static string? SymbolicLinkOf(IntPtr eventData, uint eventDataSize)
    {
        const int offset = UsbNative.CM_NOTIFY_EVENT_DATA_SYMBOLIC_LINK_OFFSET;

        if (eventData == IntPtr.Zero || eventDataSize <= offset + sizeof(char))
        {
            return null;
        }

        var characters = (int)((eventDataSize - offset) / sizeof(char));
        var link = Marshal.PtrToStringUni(eventData + offset, characters);
        var terminator = link.IndexOf('\0', StringComparison.Ordinal);

        return terminator switch
        {
            0 => null,
            < 0 => link,
            _ => link[..terminator],
        };
    }

    private bool TryStartWmiFallback()
    {
        try
        {
            _arrival = Subscribe("__InstanceCreationEvent", UsbChangeKind.Arrived);
            _removal = Subscribe("__InstanceDeletionEvent", UsbChangeKind.Removed);

            _logger.LogInformation("Watching for USB device arrival and removal (WMI fallback).");
            return true;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not subscribe to USB device notifications. USB policy will still be applied on the "
                + "periodic reconcile, with a longer delay before a newly attached device is restricted.");

            StopWmi();
            return false;
        }
    }

    private ManagementEventWatcher Subscribe(string eventClass, UsbChangeKind kind)
    {
        // Scoped to PnP entities whose device id starts with USB\, so the agent
        // is not woken by every driver event on the machine. Constant query
        // text: nothing from runtime is interpolated into it (ADR-0005).
        var query = new WqlEventQuery(
            $"SELECT * FROM {eventClass} WITHIN 1 "
            + "WHERE TargetInstance ISA 'Win32_PnPEntity' "
            + "AND TargetInstance.PNPDeviceID LIKE 'USB\\\\%'");

        var watcher = new ManagementEventWatcher(query);

        watcher.EventArrived += (_, _) =>
        {
            try
            {
                Changed?.Invoke(this, kind);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A USB device-change handler threw.");
            }
        };

        watcher.Start();
        return watcher;
    }

    private void StopPnpNotifications()
    {
        if (_notification == IntPtr.Zero)
        {
            return;
        }

        // Blocks until any in-flight callback has returned, which is why it is
        // never called from the callback itself.
        UsbNative.CM_Unregister_Notification(_notification);
        _notification = IntPtr.Zero;
        _callback = null;
    }

    private void StopWmi()
    {
        foreach (var watcher in new[] { _arrival, _removal })
        {
            if (watcher is null)
            {
                continue;
            }

            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch (ManagementException)
            {
                // Already gone; nothing useful to do while shutting down.
            }
        }

        _arrival = null;
        _removal = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopPnpNotifications();
        StopWmi();
        _disposed = true;
    }
}
