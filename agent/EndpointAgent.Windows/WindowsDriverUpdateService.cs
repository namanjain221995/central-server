using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Drivers;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Scans for and installs optional Windows Update <b>driver</b> updates through the
/// Windows Update Agent (WUA) COM API.
/// </summary>
/// <remarks>
/// <para>
/// Late-bound COM (<c>Microsoft.Update.Session</c>), exactly as
/// <see cref="WindowsUpdateCollector"/> reads update history: no interop assembly is
/// shipped, and the object model has been stable since Vista. The agent launches
/// nothing -- it drives WUA in-process and WUA does the work (ADR-0005). The one
/// difference from the history read is deliberate: this performs an ONLINE search
/// (<c>Online=true</c>) forced at the public Windows Update service
/// (<c>ServerSelection=ssWindowsUpdate</c>), so optional drivers show up even where a
/// WSUS redirect would otherwise hide them. That search is slow and network-bound,
/// which is why it is an on-demand task and never on the inventory path.
/// </para>
/// <para>
/// WUA's Search / Download / Install calls are synchronous and block, so every one
/// runs on a dedicated background MTA thread rather than the heartbeat loop or the
/// thread pool. Expected Windows failures -- the update service stopped, a rejected
/// search, an install already in progress, a pending reboot -- are turned into an
/// unsuccessful result with a clear, secret-free message rather than thrown.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDriverUpdateService(ILogger<WindowsDriverUpdateService> logger) : IDriverUpdateService
{
    private const string ProgIdSession = "Microsoft.Update.Session";
    private const string ProgIdUpdateColl = "Microsoft.Update.UpdateColl";
    private const string ProgIdSystemInfo = "Microsoft.Update.SystemInfo";

    private const string ClientApplicationId = "Techsara-EPP-Agent";

    /// <summary>Pending, not-yet-installed, non-hidden driver updates.</summary>
    private const string SearchCriteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    /// <summary><c>ssWindowsUpdate</c>: force the public Windows Update service, bypassing a WSUS redirect.</summary>
    private const int ServerSelectionWindowsUpdate = 2;

    // OperationResultCode (per-update and overall install result).
    private const int OperationResultSucceeded = 2;
    private const int OperationResultSucceededWithErrors = 3;
    private const int OperationResultFailed = 4;

    private const int MaxTitleLength = 400;

    private readonly ILogger<WindowsDriverUpdateService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public Task<DriverUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
        RunOnWorkerAsync(() => ScanCore(cancellationToken), cancellationToken);

    public Task<DriverUpdateInstallResult> InstallAsync(
        IReadOnlyCollection<string> updateIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateIds);
        return RunOnWorkerAsync(() => InstallCore(updateIds, cancellationToken), cancellationToken);
    }

    // ----------------------------------------------------------------- scan

    private DriverUpdateScanResult ScanCore(CancellationToken cancellationToken)
    {
        var sessionType = Type.GetTypeFromProgID(ProgIdSession);
        if (sessionType is null)
        {
            return DriverUpdateScanResult.Failure("The Windows Update Agent is not available on this device.");
        }

        object? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            session = Activator.CreateInstance(sessionType);
            dynamic s = session!;
            s.ClientApplicationID = ClientApplicationId;

            dynamic searcher = s.CreateUpdateSearcher();
            searcher.Online = true;
            searcher.ServerSelection = ServerSelectionWindowsUpdate;

            dynamic searchResult = searcher.Search(SearchCriteria);
            dynamic found = searchResult.Updates;
            int count = (int)found.Count;

            var updates = new List<DriverUpdateInfo>(count);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = ReadUpdate(found.Item(i));

                // Previews are dropped at the source; the executor enforces it again.
                if (info is not null && !info.IsPreview)
                {
                    updates.Add(info);
                }
            }

            _logger.LogInformation("Driver update scan found {Count} offered update(s).", updates.Count);
            return DriverUpdateScanResult.Ok(updates);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Driver update scan failed (0x{HResult:X8}).", ex.HResult);
            return DriverUpdateScanResult.Failure(DescribeComError(ex));
        }
        catch (RuntimeBinderException ex)
        {
            _logger.LogWarning(ex, "Driver update scan failed binding a WUA member.");
            return DriverUpdateScanResult.Failure(
                "The Windows Update Agent returned an unexpected shape during the driver scan.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Driver update scan failed.");
            return DriverUpdateScanResult.Failure("The driver update scan could not be completed.");
        }
        finally
        {
            ReleaseComObject(session);
        }
    }

    private DriverUpdateInfo? ReadUpdate(dynamic update)
    {
        try
        {
            var rawId = (string?)update.Identity.UpdateID;
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return null;
            }

            var title = TryString(() => (string?)update.Title) ?? "(untitled driver update)";
            var isPreview = DriverUpdatePreview.IsPreview(title, TryBool(() => (bool)update.IsBeta));

            return new DriverUpdateInfo(
                NormalizeId(rawId),
                TryInt(() => (int)update.Identity.RevisionNumber),
                Truncate(title),
                TryString(() => (string?)update.DriverClass),
                TryString(() => (string?)update.DriverProvider),
                TryDate(() => (DateTime)update.DriverVerDate),
                isPreview,
                TryLong(() => Convert.ToInt64((object)update.MaxDownloadSize)));
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException)
        {
            _logger.LogDebug(ex, "Skipping an unreadable driver update.");
            return null;
        }
    }

    // -------------------------------------------------------------- install

    private DriverUpdateInstallResult InstallCore(IReadOnlyCollection<string> requestedIds, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(requestedIds, StringComparer.OrdinalIgnoreCase);

        var sessionType = Type.GetTypeFromProgID(ProgIdSession);
        if (sessionType is null)
        {
            return DriverUpdateInstallResult.ForError("The Windows Update Agent is not available on this device.");
        }

        object? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            session = Activator.CreateInstance(sessionType);
            dynamic s = session!;
            s.ClientApplicationID = ClientApplicationId;

            dynamic searcher = s.CreateUpdateSearcher();
            searcher.Online = true;
            searcher.ServerSelection = ServerSelectionWindowsUpdate;

            dynamic searchResult = searcher.Search(SearchCriteria);
            dynamic found = searchResult.Updates;
            int count = (int)found.Count;

            dynamic downloadColl = NewUpdateColl();
            var selected = new List<dynamic>();
            var selectedMeta = new List<(string Id, string Title)>();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedPreview = 0;
            var skippedEula = 0;

            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dynamic update = found.Item(i);
                var rawId = (string?)update.Identity.UpdateID;
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    continue;
                }

                var normId = NormalizeId(rawId);
                if (!wanted.Contains(normId))
                {
                    continue;
                }

                matched.Add(normId);

                var title = TryString(() => (string?)update.Title) ?? "(untitled driver update)";
                if (DriverUpdatePreview.IsPreview(title, TryBool(() => (bool)update.IsBeta)))
                {
                    // The platform excludes previews from installation as well as scanning.
                    skippedPreview++;
                    continue;
                }

                // Deliberately NOT skipped when InstallationBehavior.CanRequestUserInput
                // is set: the installer below runs with ForceQuiet, so WUA installs these
                // unattended without ever showing a prompt. Skipping them is exactly what
                // made an install of OEM driver/firmware updates -- which commonly set
                // that flag -- report "Installed 0" while leaving the updates pending.
                if (!TryBool(() => (bool)update.EulaAccepted))
                {
                    try
                    {
                        update.AcceptEula();
                    }
                    catch (Exception ex) when (ex is RuntimeBinderException or COMException)
                    {
                        _logger.LogDebug(ex, "Could not accept the EULA for a driver update; skipping it.");
                        skippedEula++;
                        continue;
                    }
                }

                downloadColl.Add(update);
                selected.Add(update);
                selectedMeta.Add((normId, Truncate(title)));
            }

            if (selected.Count == 0)
            {
                // The operator asked for updates and none could be staged, so this is a
                // reported failure with the reason -- never the silent "Installed 0"
                // success that left updates pending forever. The breakdown is what tells
                // the operator (and the task result) why nothing happened.
                var notOffered = wanted.Count - matched.Count;
                var reasons = new List<string>();
                if (notOffered > 0)
                {
                    reasons.Add($"{notOffered} no longer offered by Windows Update");
                }

                if (skippedPreview > 0)
                {
                    reasons.Add($"{skippedPreview} preview");
                }

                if (skippedEula > 0)
                {
                    reasons.Add($"{skippedEula} with a licence that could not be accepted");
                }

                var detail = reasons.Count > 0
                    ? string.Join("; ", reasons)
                    : "none were applicable on this device";
                return DriverUpdateInstallResult.ForError($"No driver updates were installed: {detail}.");
            }

            dynamic downloader = s.CreateUpdateDownloader();
            downloader.Updates = downloadColl;
            downloader.Download();

            dynamic installColl = NewUpdateColl();
            var installMeta = new List<(string Id, string Title)>();
            for (var i = 0; i < selected.Count; i++)
            {
                if (TryBool(() => (bool)selected[i].IsDownloaded))
                {
                    installColl.Add(selected[i]);
                    installMeta.Add(selectedMeta[i]);
                }
            }

            if (installMeta.Count == 0)
            {
                return DriverUpdateInstallResult.ForError(
                    "The selected driver updates could not be downloaded; nothing was installed.");
            }

            dynamic installer = s.CreateUpdateInstaller();
            installer.ClientApplicationID = ClientApplicationId;
            installer.AllowSourcePrompts = false;

            // Suppress any installer UI so updates that MAY request user input install
            // unattended under LocalSystem instead of being skipped. ForceQuiet lives on
            // IUpdateInstaller2 (Windows 7+); set it defensively through the late-bound
            // object in case a host ever narrows the type.
            try
            {
                installer.ForceQuiet = true;
            }
            catch (Exception ex) when (ex is RuntimeBinderException or COMException)
            {
                _logger.LogDebug(ex, "Could not set ForceQuiet on the update installer; continuing.");
            }

            installer.Updates = installColl;

            if (TryBool(() => (bool)installer.IsBusy))
            {
                return DriverUpdateInstallResult.ForError(
                    "Another update operation is in progress or a reboot is pending; try again later.");
            }

            dynamic installResult = installer.Install();
            var rebootRequired = TryBool(() => (bool)installResult.RebootRequired);

            var installed = new List<DriverUpdateInstallItem>();
            var failed = new List<DriverUpdateInstallItem>();

            for (var i = 0; i < installMeta.Count; i++)
            {
                var resultCode = OperationResultFailed;
                var hresult = 0;
                try
                {
                    dynamic perUpdate = installResult.GetUpdateResult(i);
                    resultCode = (int)perUpdate.ResultCode;
                    hresult = (int)perUpdate.HResult;
                }
                catch (Exception ex) when (ex is RuntimeBinderException or COMException)
                {
                    _logger.LogDebug(ex, "Could not read a per-update install result; treating it as failed.");
                }

                var item = new DriverUpdateInstallItem(installMeta[i].Id, installMeta[i].Title, resultCode, hresult);
                if (resultCode is OperationResultSucceeded or OperationResultSucceededWithErrors)
                {
                    installed.Add(item);
                }
                else
                {
                    failed.Add(item);
                }
            }

            if (!rebootRequired)
            {
                rebootRequired = ReadSystemRebootRequired();
            }

            _logger.LogInformation(
                "Driver update install: {Installed} installed, {Failed} failed, reboot required {Reboot}.",
                installed.Count, failed.Count, rebootRequired);

            return new DriverUpdateInstallResult(rebootRequired, installed, failed, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Driver update install failed (0x{HResult:X8}).", ex.HResult);
            return DriverUpdateInstallResult.ForError(DescribeComError(ex));
        }
        catch (RuntimeBinderException ex)
        {
            _logger.LogWarning(ex, "Driver update install failed binding a WUA member.");
            return DriverUpdateInstallResult.ForError(
                "The Windows Update Agent returned an unexpected shape during the driver install.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Driver update install failed.");
            return DriverUpdateInstallResult.ForError("The driver update install could not be completed.");
        }
        finally
        {
            ReleaseComObject(session);
        }
    }

    private bool ReadSystemRebootRequired()
    {
        var type = Type.GetTypeFromProgID(ProgIdSystemInfo);
        if (type is null)
        {
            return false;
        }

        object? systemInfo = null;
        try
        {
            systemInfo = Activator.CreateInstance(type);
            return (bool)((dynamic)systemInfo!).RebootRequired;
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException)
        {
            _logger.LogDebug(ex, "Could not read the system reboot-required flag.");
            return false;
        }
        finally
        {
            ReleaseComObject(systemInfo);
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Runs the synchronous, blocking WUA work on a dedicated background MTA thread
    /// and surfaces its result (or cancellation, or an unexpected fault) as a task.
    /// </summary>
    private static Task<T> RunOnWorkerAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(work());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "epp-wua-driver",
        };

        // A new thread already defaults to MTA; set it explicitly so the apartment the
        // WUA objects live on is a documented choice, not an inherited default.
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();

        return completion.Task;
    }

    private static dynamic NewUpdateColl()
    {
        var type = Type.GetTypeFromProgID(ProgIdUpdateColl)
            ?? throw new InvalidOperationException("Microsoft.Update.UpdateColl is unavailable.");
        return Activator.CreateInstance(type)!;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    private static string DescribeComError(COMException ex) => unchecked((uint)ex.HResult) switch
    {
        // WU_E_INSTALL_NOT_ALLOWED / another operation in progress.
        0x80240016 or 0x80240009 => "Another update operation is in progress or a reboot is pending; try again later.",
        // WU_E_INVALID_CRITERIA.
        0x80240032 => "The Windows Update Agent rejected the driver search criteria.",
        _ => $"The Windows Update Agent reported an error (0x{unchecked((uint)ex.HResult):X8}).",
    };

    private static string NormalizeId(string raw) =>
        Guid.TryParse(raw, out var guid) ? guid.ToString() : raw.Trim().ToLowerInvariant();

    private static string Truncate(string value) =>
        value.Length > MaxTitleLength ? value[..MaxTitleLength] : value;

    private static bool TryBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static int TryInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException)
        {
            return 0;
        }
    }

    private static long TryLong(Func<long> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException or OverflowException or FormatException)
        {
            return 0;
        }
    }

    private static string? TryString(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? TryDate(Func<DateTime> read)
    {
        try
        {
            var date = read();
            return date == default ? null : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException)
        {
            return null;
        }
    }
}
