using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Windows inventory collection via CIM/WMI and managed networking APIs.
/// </summary>
/// <remarks>
/// <para>
/// Sources, all read-only and all fixed query text (ADR-0005):
/// <c>Win32_BIOS</c> (serial), <c>Win32_ComputerSystem</c> (manufacturer, model,
/// RAM, interactive user), <c>Win32_Processor</c> (CPU name/cores),
/// <c>DriveInfo</c> (volumes), <c>NetworkInterface</c> (adapters — the managed
/// API is preferred over <c>Win32_NetworkAdapterConfiguration</c> because it
/// needs no WMI round-trip and returns typed addresses).
/// </para>
/// <para>
/// Every section is individually fault-isolated: a machine with a broken WMI
/// repository still reports its disks and NICs.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsInventoryCollector(
    ISystemInfoProvider systemInfoProvider,
    ILocalAccountsCollector localAccountsCollector,
    ISoftwareCollector softwareCollector,
    ISecurityPostureCollector securityPostureCollector,
    IServiceProcessCollector serviceProcessCollector,
    IWindowsUpdateCollector windowsUpdateCollector,
    IDriverCollector driverCollector,
    IBitLockerCollector bitLockerCollector,
    IChromeCollector chromeCollector,
    TimeProvider timeProvider,
    ILogger<WindowsInventoryCollector> logger) : IInventoryCollector
{
    private readonly ISystemInfoProvider _systemInfoProvider = systemInfoProvider
        ?? throw new ArgumentNullException(nameof(systemInfoProvider));

    private readonly ILocalAccountsCollector _localAccountsCollector = localAccountsCollector
        ?? throw new ArgumentNullException(nameof(localAccountsCollector));

    private readonly ISoftwareCollector _softwareCollector = softwareCollector
        ?? throw new ArgumentNullException(nameof(softwareCollector));

    private readonly ISecurityPostureCollector _securityPostureCollector = securityPostureCollector
        ?? throw new ArgumentNullException(nameof(securityPostureCollector));

    private readonly IServiceProcessCollector _serviceProcessCollector = serviceProcessCollector
        ?? throw new ArgumentNullException(nameof(serviceProcessCollector));

    private readonly IWindowsUpdateCollector _windowsUpdateCollector = windowsUpdateCollector
        ?? throw new ArgumentNullException(nameof(windowsUpdateCollector));

    private readonly IDriverCollector _driverCollector = driverCollector
        ?? throw new ArgumentNullException(nameof(driverCollector));

    private readonly IBitLockerCollector _bitLockerCollector = bitLockerCollector
        ?? throw new ArgumentNullException(nameof(bitLockerCollector));

    private readonly IChromeCollector _chromeCollector = chromeCollector
        ?? throw new ArgumentNullException(nameof(chromeCollector));

    /// <summary>Cap on the process snapshot carried with inventory.</summary>
    private const int MaxProcessesInInventory = 60;

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<WindowsInventoryCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<InventoryReport> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hardware = CollectHardware();
        var interfaces = CollectNetworkInterfaces();
        var loggedOnUser = CollectLoggedOnUser();

        InventoryLocalAccounts? localAccounts = null;
        try
        {
            localAccounts = await _localAccountsCollector.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fault isolation as everywhere else in this collector: a broken SAM
            // enumeration must not lose the hardware/network snapshot.
            _logger.LogWarning(ex, "Local accounts collection failed; omitting the section this snapshot.");
        }

        IReadOnlyList<InventorySoftware>? software = null;
        try
        {
            software = await _softwareCollector.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Software collection failed; omitting the section this snapshot.");
        }

        InventorySecurityPosture? posture = null;
        try
        {
            posture = await _securityPostureCollector.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Security posture collection failed; omitting the section this snapshot.");
        }

        IReadOnlyList<InventoryService>? services = null;
        IReadOnlyList<InventoryProcess>? processes = null;
        try
        {
            services = await _serviceProcessCollector.CollectServicesAsync(cancellationToken);
            processes = await _serviceProcessCollector.CollectProcessesAsync(MaxProcessesInInventory, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Service/process collection failed; omitting the section this snapshot.");
        }

        InventoryWindowsUpdate? windowsUpdate = null;
        try
        {
            windowsUpdate = await _windowsUpdateCollector.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Windows Update collection failed; omitting the section this snapshot.");
        }

        IReadOnlyList<InventoryDriver>? drivers = null;
        try
        {
            drivers = await _driverCollector.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Driver collection failed; omitting the section this snapshot.");
        }

        InventoryBitLocker? bitLocker = null;
        try
        {
            bitLocker = await _bitLockerCollector.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "BitLocker collection failed; omitting the section this snapshot.");
        }

        InventoryChrome? chrome = null;
        try
        {
            chrome = await _chromeCollector.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chrome collection failed; omitting the section this snapshot.");
        }

        return new InventoryReport(
            hardware,
            interfaces,
            loggedOnUser,
            _timeProvider.GetUtcNow(),
            localAccounts,
            software,
            posture,
            services,
            processes,
            windowsUpdate,
            drivers,
            bitLocker,
            chrome);
    }

    private InventoryHardware CollectHardware()
    {
        string? serial = null;
        string? manufacturer = null;
        string? model = null;
        string? cpuName = null;
        int? physicalCores = null;
        int? logicalProcessors = null;
        long? totalMemory = null;

        TryQuery("SELECT SerialNumber FROM Win32_BIOS", row =>
        {
            serial = CleanString(row["SerialNumber"]);
        });

        TryQuery("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem", row =>
        {
            manufacturer = CleanString(row["Manufacturer"]);
            model = CleanString(row["Model"]);

            if (row["TotalPhysicalMemory"] is ulong bytes and > 0)
            {
                totalMemory = unchecked((long)Math.Min(bytes, long.MaxValue));
            }
        });

        TryQuery("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", row =>
        {
            // Multi-socket machines: keep the first CPU's name, sum the counts.
            cpuName ??= CleanString(row["Name"]);

            if (row["NumberOfCores"] is uint cores)
            {
                physicalCores = (physicalCores ?? 0) + (int)Math.Min(cores, int.MaxValue);
            }

            if (row["NumberOfLogicalProcessors"] is uint logical)
            {
                logicalProcessors = (logicalProcessors ?? 0) + (int)Math.Min(logical, int.MaxValue);
            }
        });

        var disks = CollectDisks();

        return new InventoryHardware(
            serial, manufacturer, model, cpuName, physicalCores, logicalProcessors, totalMemory, disks);
    }

    private List<InventoryDisk> CollectDisks()
    {
        var disks = new List<InventoryDisk>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    // Fixed volumes only: network shares and removable media churn
                    // and say nothing about the machine itself.
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    {
                        continue;
                    }

                    disks.Add(new InventoryDisk(
                        drive.Name.TrimEnd('\\'),
                        drive.DriveFormat,
                        drive.TotalSize,
                        drive.AvailableFreeSpace));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A single unreadable volume (BitLocker-locked, dying disk)
                    // must not lose the rest.
                    _logger.LogDebug(ex, "Skipping unreadable volume {Volume}.", drive.Name);
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Volume enumeration failed; reporting no disks.");
        }

        return disks;
    }

    private List<InventoryNetworkInterface> CollectNetworkInterfaces()
    {
        var interfaces = new List<InventoryNetworkInterface>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var mac = nic.GetPhysicalAddress().ToString();

                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Where(a => !a.Address.IsIPv6LinkLocal)
                    .Select(a => a.Address.ToString())
                    .ToArray();

                interfaces.Add(new InventoryNetworkInterface(
                    nic.Name,
                    string.IsNullOrEmpty(mac) ? null : mac,
                    addresses,
                    nic.OperationalStatus == OperationalStatus.Up));
            }
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "Network interface enumeration failed; reporting none.");
        }

        return interfaces;
    }

    private string? CollectLoggedOnUser()
    {
        string? userName = null;

        // Win32_ComputerSystem.UserName is the interactively logged-on user (the
        // console session). Null when nobody is at the machine, or when the query
        // runs in a session with no interactive user - both legitimate.
        TryQuery("SELECT UserName FROM Win32_ComputerSystem", row =>
        {
            userName = CleanString(row["UserName"]);
        });

        return userName;
    }

    private void TryQuery(string wqlQuery, Action<ManagementBaseObject> handle)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ObjectQuery(wqlQuery));
            using var results = searcher.Get();

            foreach (var row in results)
            {
                using (row)
                {
                    handle(row);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(ex, "Inventory CIM query failed and its facts were skipped: {Query}", wqlQuery);
        }
    }

    private static string? CleanString(object? value)
    {
        var text = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
