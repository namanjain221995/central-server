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
/// <para>
/// An install does not relay WUA's claim unverified (agent 1.27.0): after
/// <c>Install()</c> it re-runs the same search in the same session and classifies
/// every requested update -- Installed, InstalledUnverified, PendingReboot,
/// ReOffered, Failed, NotOffered, SkippedPreview or EulaDeclined -- by the rules in
/// <see cref="DriverUpdateOutcomeClassifier"/>. That search's offered set rides back
/// as the result's <c>postScan</c>, so one install round-trip refreshes the server's
/// pending list. Still WUA COM only; nothing is launched.
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

    // OperationResultCode (per-update and overall install result). Success (2, and
    // 3 = succeeded with errors) is DriverUpdateOutcomeClassifier.IsSuccess; this is
    // what an unreadable per-update result is taken to be.
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
            // Nothing was searched, so nothing is known about any requested update --
            // claiming "not offered" here would be a guess. The error says why.
            return DriverUpdateInstallResult.ForError("The Windows Update Agent is not available on this device.");
        }

        // Every requested update's fate, decided as the install proceeds. Declared out
        // here so an operation-level failure part-way still reports what is already
        // known instead of discarding it (Cause G: nothing the operator selected may
        // silently vanish from the result).
        var outcomes = new List<DriverUpdateOutcome>();

        // Updates handed to the download/install pipeline whose outcome is not yet
        // decided; if the operation throws, these are what it took down with it.
        var staged = new List<(string Id, string Title)>();

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
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                // A repeat of an id already handled is not a second update.
                if (!wanted.Contains(normId) || !matched.Add(normId))
                {
                    continue;
                }

                var title = Truncate(TryString(() => (string?)update.Title) ?? "(untitled driver update)");
                if (DriverUpdatePreview.IsPreview(title, TryBool(() => (bool)update.IsBeta)))
                {
                    // The platform excludes previews from installation as well as scanning.
                    outcomes.Add(NotInstalled(normId, title, DriverUpdateOutcomeStatus.SkippedPreview));
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
                        outcomes.Add(NotInstalled(normId, title, DriverUpdateOutcomeStatus.EulaDeclined));
                        continue;
                    }
                }

                downloadColl.Add(update);
                selected.Add(update);
                staged.Add((normId, title));
            }

            // Requested, but not in Windows Update's offered set at all: already
            // installed meanwhile, or no longer applicable. Itemized, never dropped.
            outcomes.AddRange(DriverUpdateOutcomeClassifier.NotOffered(requestedIds, matched));

            if (selected.Count == 0)
            {
                // The operator asked for updates and none could be staged, so this is a
                // reported failure with the reason -- never the silent "Installed 0"
                // success that left updates pending forever. The breakdown is what tells
                // the operator (and the task result) why nothing happened.
                return DriverUpdateInstallResult.ForError(
                    $"No driver updates were installed: {DescribeNothingStaged(outcomes)}.", outcomes);
            }

            var selectedMeta = staged.ToList();

            dynamic downloader = s.CreateUpdateDownloader();
            downloader.Updates = downloadColl;
            dynamic downloadResult = downloader.Download();

            dynamic installColl = NewUpdateColl();
            var installMeta = new List<(string Id, string Title)>();
            for (var i = 0; i < selected.Count; i++)
            {
                if (TryBool(() => (bool)selected[i].IsDownloaded))
                {
                    installColl.Add(selected[i]);
                    installMeta.Add(selectedMeta[i]);
                    continue;
                }

                // Not downloaded: it never reaches the installer. Before 1.27.0 it
                // silently fell out of the result; now it is a Failed outcome carrying
                // the download's own per-update code when WUA will give it.
                var (downloadCode, downloadHResult) = ReadOperationResult((object)downloadResult, i);
                outcomes.Add(new DriverUpdateOutcome(
                    selectedMeta[i].Id, selectedMeta[i].Title, downloadCode, downloadHResult,
                    RebootRequired: false, DriverUpdateOutcomeStatus.Failed));
                staged.Remove(selectedMeta[i]);
            }

            if (installMeta.Count == 0)
            {
                return DriverUpdateInstallResult.ForError(
                    "The selected driver updates could not be downloaded; nothing was installed.", outcomes);
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
                // Downloaded but never handed to the installer: not installed, and the
                // error says why. No code was produced, so none is invented.
                foreach (var meta in installMeta)
                {
                    outcomes.Add(NotInstalled(meta.Id, meta.Title, DriverUpdateOutcomeStatus.Failed));
                }

                return DriverUpdateInstallResult.ForError(
                    "Another update operation is in progress or a reboot is pending; try again later.", outcomes);
            }

            dynamic installResult = installer.Install();

            // From here on nothing may throw away the install's result: every read is
            // guarded, and the verification pass degrades to "unverified", never to an
            // error.
            staged.Clear();

            var aggregateReboot = TryBool(() => (bool)installResult.RebootRequired);

            // The machine-wide flag (Microsoft.Update.SystemInfo). Read unconditionally:
            // it is half of the PendingReboot rule for every update, not only a fallback.
            var systemReboot = ReadSystemRebootRequired();

            // The legacy aggregate key: the install's flag, else the system-wide one --
            // the same value the pre-1.27.0 code reported.
            var rebootRequired = aggregateReboot || systemReboot;

            var installed = new List<DriverUpdateInstallItem>();
            var failed = new List<DriverUpdateInstallItem>();
            var installerResults = new List<(DriverUpdateInstallItem Item, bool ItemReboot)>(installMeta.Count);

            for (var i = 0; i < installMeta.Count; i++)
            {
                var resultCode = OperationResultFailed;
                var hresult = 0;

                // Per-update reboot flag; until (unless) it is read, the aggregate
                // stands in -- never false (contract section A).
                var itemReboot = aggregateReboot;
                try
                {
                    dynamic perUpdate = installResult.GetUpdateResult(i);
                    resultCode = (int)perUpdate.ResultCode;
                    hresult = (int)perUpdate.HResult;

                    // IUpdateInstallationResult documents exactly three properties:
                    // HResult, ResultCode and RebootRequired. This reads the third.
                    itemReboot = ReadPerUpdateReboot(() => (bool)perUpdate.RebootRequired, aggregateReboot, _logger);
                }
                catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException)
                {
                    _logger.LogDebug(ex, "Could not read a per-update install result; treating it as failed.");
                }

                var item = new DriverUpdateInstallItem(installMeta[i].Id, installMeta[i].Title, resultCode, hresult);
                if (DriverUpdateOutcomeClassifier.IsSuccess(resultCode))
                {
                    installed.Add(item);
                }
                else
                {
                    failed.Add(item);
                }

                installerResults.Add((item, itemReboot));
            }

            // Did each "succeeded" update actually leave the offered set? The same
            // searcher, the same criteria, the same session -- the only question the
            // operator cares about, asked where it is cheapest to answer.
            var (stillOffered, postScan) = VerifyOfferedSet((object)searcher);

            foreach (var (item, itemReboot) in installerResults)
            {
                bool? offeredAfter = stillOffered is null ? null : stillOffered.Contains(item.UpdateId);
                var status = DriverUpdateOutcomeClassifier.Classify(
                    item.ResultCode, itemReboot || systemReboot, offeredAfter);

                outcomes.Add(new DriverUpdateOutcome(
                    item.UpdateId, item.Title, item.ResultCode, item.HResult, itemReboot, status));
            }

            _logger.LogInformation(
                "Driver update install: {Installed} installed, {Failed} failed, reboot required {Reboot}, verified {Verified}.",
                installed.Count, failed.Count, rebootRequired, postScan is not null);

            return new DriverUpdateInstallResult(rebootRequired, installed, failed, null, outcomes, postScan);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Driver update install failed (0x{HResult:X8}).", ex.HResult);
            return DriverUpdateInstallResult.ForError(DescribeComError(ex), WithStagedFailed(outcomes, staged, ex.HResult));
        }
        catch (RuntimeBinderException ex)
        {
            _logger.LogWarning(ex, "Driver update install failed binding a WUA member.");
            return DriverUpdateInstallResult.ForError(
                "The Windows Update Agent returned an unexpected shape during the driver install.",
                WithStagedFailed(outcomes, staged, hresult: null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Driver update install failed.");
            return DriverUpdateInstallResult.ForError(
                "The driver update install could not be completed.", WithStagedFailed(outcomes, staged, hresult: null));
        }
        finally
        {
            ReleaseComObject(session);
        }
    }

    /// <summary>
    /// Re-runs the install's own search and returns what is still offered (every
    /// readable identity, previews included, for classification) and the scan-shaped
    /// list (previews excluded, exactly what <see cref="ScanCore"/> reports).
    /// </summary>
    /// <remarks>
    /// Both null when the search cannot be trusted: it failed, or an entry's identity
    /// could not be read -- an unreadable entry might be one of ours, and calling an
    /// update "no longer offered" on that basis would be a lie. Never throws: the
    /// install has already happened, and nothing here may turn its result into an
    /// error.
    /// </remarks>
    private (IReadOnlySet<string>? StillOffered, IReadOnlyList<DriverUpdateInfo>? PostScan) VerifyOfferedSet(object searcherObject)
    {
        try
        {
            dynamic searcher = searcherObject;
            dynamic verify = searcher.Search(SearchCriteria);
            dynamic found = verify.Updates;
            int count = (int)found.Count;

            var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var postScan = new List<DriverUpdateInfo>(count);

            for (var i = 0; i < count; i++)
            {
                dynamic update = found.Item(i);

                // Throws (and so fails the whole verification) when unreadable.
                var rawId = (string?)update.Identity.UpdateID;
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    continue;
                }

                offered.Add(NormalizeId(rawId));

                DriverUpdateInfo? info = ReadUpdate(update);
                if (info is not null && !info.IsPreview)
                {
                    postScan.Add(info);
                }
            }

            return (offered, postScan);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-install verification search failed; driver update outcomes are reported unverified.");
            return (null, null);
        }
    }

    /// <summary>
    /// The per-update reboot flag, or <paramref name="aggregateReboot"/> when the
    /// late-bound read fails -- never false by default, so the worst failure mode is
    /// the conservative PendingReboot, never a false ReOffered.
    /// </summary>
    internal static bool ReadPerUpdateReboot(Func<bool> read, bool aggregateReboot, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            return read();
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException)
        {
            logger?.LogDebug(ex, "Per-update RebootRequired unavailable; using the aggregate flag.");
            return aggregateReboot;
        }
    }

    /// <summary>A requested update that never reached an operation producing a result code.</summary>
    private static DriverUpdateOutcome NotInstalled(string id, string title, DriverUpdateOutcomeStatus status) =>
        new(id, title, ResultCode: null, HResult: null, RebootRequired: false, status);

    /// <summary>
    /// The outcomes so far, plus every still-staged update as Failed: the operation
    /// threw before the installer produced a per-update result for it, so it was not
    /// installed by this task. The HRESULT is the operation's, when it has one.
    /// </summary>
    private static IReadOnlyList<DriverUpdateOutcome> WithStagedFailed(
        List<DriverUpdateOutcome> outcomes, List<(string Id, string Title)> staged, int? hresult)
    {
        var all = new List<DriverUpdateOutcome>(outcomes.Count + staged.Count);
        all.AddRange(outcomes);
        foreach (var (id, title) in staged)
        {
            all.Add(new DriverUpdateOutcome(id, title, ResultCode: null, hresult, RebootRequired: false, DriverUpdateOutcomeStatus.Failed));
        }

        return all;
    }

    /// <summary>A per-update download result's code and HRESULT, or nulls when WUA will not say.</summary>
    private (int? ResultCode, int? HResult) ReadOperationResult(object operationResultObject, int index)
    {
        try
        {
            dynamic operationResult = operationResultObject;
            dynamic perUpdate = operationResult.GetUpdateResult(index);
            return ((int)perUpdate.ResultCode, (int)perUpdate.HResult);
        }
        catch (Exception ex) when (ex is RuntimeBinderException or COMException or InvalidCastException)
        {
            _logger.LogDebug(ex, "Could not read a per-update download result.");
            return (null, null);
        }
    }

    /// <summary>Why nothing could be staged, from the outcomes already itemized.</summary>
    private static string DescribeNothingStaged(IReadOnlyList<DriverUpdateOutcome> outcomes)
    {
        var notOffered = outcomes.Count(o => o.Status == DriverUpdateOutcomeStatus.NotOffered);
        var preview = outcomes.Count(o => o.Status == DriverUpdateOutcomeStatus.SkippedPreview);
        var eula = outcomes.Count(o => o.Status == DriverUpdateOutcomeStatus.EulaDeclined);

        var reasons = new List<string>();
        if (notOffered > 0)
        {
            reasons.Add($"{notOffered} no longer offered by Windows Update");
        }

        if (preview > 0)
        {
            reasons.Add($"{preview} preview");
        }

        if (eula > 0)
        {
            reasons.Add($"{eula} with a licence that could not be accepted");
        }

        return reasons.Count > 0
            ? string.Join("; ", reasons)
            : "none were applicable on this device";
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
