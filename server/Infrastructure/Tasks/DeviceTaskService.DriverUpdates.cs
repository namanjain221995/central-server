using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Tasks;

/// <summary>
/// The driver-update half of task completion: remembering what each install did,
/// reconciling that memory against every fresh offered list, and queueing the
/// follow-up scans that make the pending list converge on the truth by itself.
/// </summary>
/// <remarks>
/// <para>
/// The pending list (<see cref="DeviceDriverUpdate"/>) is only ever what the last
/// scan said. On its own it cannot tell "never tried" from "installed and came
/// back" from "installed, waiting for a restart" -- and before this, nothing ever
/// refreshed it after an install unless the operator's browser tab happened to
/// still be open. Three things fix that, all here:
/// </para>
/// <list type="bullet">
/// <item>every install result, success or failure, is remembered per update
/// (<see cref="DeviceDriverUpdateInstallRecord"/>), including results from agents
/// older than 1.27.0;</item>
/// <item>every scan-shaped list (a scan, or the verification list a 1.27.0 install
/// carries) is followed by a reconcile that moves those memories to what Windows
/// now says;</item>
/// <item>the server queues the follow-up scan itself whenever an install leaves the
/// list stale: after a result without a verification list, after a reboot-pending
/// result that lands only after the restart, after a claimed install expires
/// silent, and -- level-triggered, from the heartbeat, for up to a week -- whenever
/// a device still holds a question only a scan can answer (a restart nothing has
/// scanned since, a silent install nothing has scanned since, or an unverified
/// install nothing has scanned since).</item>
/// </list>
/// <para>
/// Every follow-up is deduplicated against the scans already asked for, but a scan
/// only counts if it can actually see what needs seeing: a Queued scan always can
/// (it will run after now); a Delivered one only if it was claimed after the point
/// that needs observing (the restart, or the install). A scan claimed earlier and
/// then killed -- by that very restart, or by an agent update restarting the service
/// -- stays Delivered until its lifetime runs out and must not stand in for one that
/// can see the answer.
/// </para>
/// </remarks>
public sealed partial class DeviceTaskService
{
    /// <summary>
    /// The least time between two follow-up scans the heartbeat probe queues for the
    /// same open question on one device, measured from when the last scan asked for
    /// after the point to observe was last active (asked for, claimed, or ended). A
    /// device whose scans keep failing (Windows Update broken, the agent crashing
    /// mid-scan) is asked again at this pace, not on every heartbeat -- and a scan
    /// that ran long holds the next one for this long after it ended, not after it
    /// was asked for.
    /// </summary>
    public static readonly TimeSpan AutoFollowUpScanInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far back the heartbeat probe looks for a question only a scan can answer.
    /// Each question is dated by its own evidence -- the restart for a reboot-pending
    /// install, the delivery for a silent install, the attempt for an unverified one
    /// -- and once that is older than this the probe gives the question up: the
    /// install memory keeps whatever status it had, and the operator can still scan.
    /// </summary>
    /// <remarks>
    /// Two jobs, one bound. Auto-rescans end (a device whose scans can never succeed
    /// stops being asked after a week, not never), and every query the probe runs on
    /// a heartbeat is range-limited in SQL to the last week of evidence instead of
    /// the device's whole task history -- tasks are never deleted.
    /// </remarks>
    public static readonly TimeSpan FollowUpLookback = TimeSpan.FromDays(7);

    /// <summary>
    /// How long after an install was confirmed the same update coming back (same or
    /// lower revision) still reads as "Windows re-offers what it said it installed".
    /// Later than this it is a fresh offer -- a rollback, a reimage, a driver the user
    /// removed months on -- and the stale memory is dropped so the update is plainly
    /// actionable again, instead of install-all skipping it for good.
    /// </summary>
    public static readonly TimeSpan InstalledReOfferWindow = TimeSpan.FromDays(7);

    /// <summary>Appended to the requester's name on a scan the server queued on their behalf.</summary>
    public const string AutoRescanSuffix = " · auto-rescan";

    /// <summary>What the expiry of a silent, claimed driver install says when its verification scan was queued.</summary>
    internal const string VerificationScanQueuedNote = "a verification scan has been queued";

    /// <summary>... when a scan that will see the outcome was already queued or running.</summary>
    internal const string VerificationScanAlreadyPendingNote = "a verification scan was already queued or running";

    /// <summary>... when the platform could not queue one (device retired, agent too old, database refused or unreachable).</summary>
    internal const string VerificationScanNotQueuedNote =
        "no verification scan could be queued, so scan the device once it is back";

    /// <summary>Most per-update outcomes one install result may report, to bound a hostile result.</summary>
    private const int MaxInstallOutcomesPerResult = 500;

    /// <summary>What an attempt to queue a follow-up scan came to.</summary>
    internal enum FollowUpScanOutcome
    {
        /// <summary>A new scan was queued.</summary>
        Queued,

        /// <summary>A scan that will see the outcome was already queued or running; nothing new was queued.</summary>
        AlreadyPending,

        /// <summary>The device cannot accept a scan, or the database refused it.</summary>
        NotQueued,
    }

    /// <summary>
    /// A follow-up scan an install result calls for: it must have been claimed after
    /// <paramref name="ObserveAfter"/> to answer the question.
    /// </summary>
    private readonly record struct FollowUpScanNeed(DateTimeOffset ObserveAfter, string Why);

    /// <summary>The open question that defines the heartbeat probe's point to observe.</summary>
    private enum FollowUpQuestion
    {
        /// <summary>(a) A restart reboot-pending installs were waiting for.</summary>
        Restart,

        /// <summary>(b) A driver install that went silent after delivery.</summary>
        SilentInstall,

        /// <summary>(c) An install remembered as unverified.</summary>
        Unverified,
    }

    /// <summary>
    /// Queues a driver-update scan when the device holds a question only a scan can
    /// answer and nothing has answered it -- level-triggered, so a missed moment is
    /// picked up on the next heartbeat instead of never. Called from the agent
    /// heartbeat after the new boot time is saved.
    /// </summary>
    /// <returns>The queued scan, or null when nothing was queued.</returns>
    /// <remarks>
    /// <para>The three questions, each dated by its own evidence:</para>
    /// <list type="bullet">
    /// <item>(a) a reboot-pending install record attempted before the device's
    /// current boot time, and no successful scan claimed after that boot -- the
    /// restart happened and its outcome has not been observed. Dated by the boot.</item>
    /// <item>(b) a driver install that expired after delivery (the device went silent
    /// mid-install) with no successful scan claimed after that delivery -- the scan
    /// the expiry queued often expires unused when the device is offline longer than
    /// a scan's half-hour lifetime. Dated by the delivery.</item>
    /// <item>(c) an install remembered as unverified ("Verifying...") with no
    /// successful scan claimed after its attempt -- the one follow-up its result
    /// queued can likewise expire unclaimed, and install-all skips such a record, so
    /// without this it would stay "Verifying..." for good. Dated by the attempt.</item>
    /// </list>
    /// <para>
    /// One scan claimed after the LATEST open point answers every open question, so
    /// that latest point is what the follow-up must post-date.
    /// </para>
    /// <para>
    /// Level, not edge: the old probe fired once, on the heartbeat whose boot time
    /// jumped. Anything that missed that one heartbeat -- a result spooled through
    /// the restart, the scan killed by it, a database blip in the probe -- left the
    /// install saying "Needs restart" until the next restart.
    /// </para>
    /// <para>
    /// Given up after <see cref="FollowUpLookback"/>: a question whose evidence is
    /// older than that is not asked (its record keeps its status; the operator can
    /// still scan). That bound is written into the SQL, so every task and record
    /// the probe reads is range-limited to the last week of evidence.
    /// </para>
    /// <para>
    /// Cost on an ordinary heartbeat: ONE round trip -- EXISTS / MAX lookups on the
    /// install records' (device, status) index and on the tasks' (device, status)
    /// index, each bounded by the lookback. Only when a question is open do further
    /// queries run.
    /// </para>
    /// <para>
    /// Bounded: nothing is queued while a scan that will see the answer is pending
    /// (Queued, or Delivered after the point to observe), nor within
    /// <see cref="AutoFollowUpScanInterval"/> of the last activity -- asked for,
    /// claimed, or ended -- of a scan asked for after that point. So a device whose
    /// scans keep failing is asked once per half hour, not every heartbeat, and a scan
    /// that ran long (slow search, then failed or expired) holds the next one for half
    /// an hour after it ENDED rather than letting it be queued back-to-back. A scan
    /// asked for BEFORE the point never holds the follow-up back: it could not have
    /// seen a restart that happened after it, and a fresh restart is new evidence to
    /// act on at once.
    /// </para>
    /// <para>
    /// Acts on behalf of whoever asked for the install that defines the latest point,
    /// like every other follow-up.
    /// </para>
    /// </remarks>
    public async Task<DeviceTask?> QueueDriverFollowUpScanIfNeededAsync(
        Guid organizationId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - FollowUpLookback;

        // Every "has a scan answered it since" subquery also carries the cutoff
        // itself: implied by "after the point" (each point is after the cutoff), but
        // spelled out so the range is a literal bound the planner can use.
        var probe = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => new
            {
                d.LastBootAt,

                // (a) dated by the restart.
                RebootUnobserved = d.LastBootAt != null
                    && d.LastBootAt > cutoff
                    && _dbContext.DeviceDriverUpdateInstallRecords.Any(r =>
                        r.DeviceId == d.Id
                        && r.Status == DriverUpdateInstallStatus.PendingReboot
                        && r.LastAttemptAt < d.LastBootAt)
                    && !_dbContext.DeviceTasks.Any(s =>
                        s.DeviceId == d.Id
                        && s.Status == DeviceTaskStatus.Succeeded
                        && s.Type == DeviceTaskType.ScanDriverUpdates
                        && s.DeliveredAt > cutoff
                        && s.DeliveredAt > d.LastBootAt),

                // (b) dated by the silent install's delivery.
                SilentInstallAt = _dbContext.DeviceTasks
                    .Where(t => t.DeviceId == d.Id
                                && t.Status == DeviceTaskStatus.Expired
                                && t.Type == DeviceTaskType.InstallDriverUpdate
                                && t.DeliveredAt > cutoff
                                && !_dbContext.DeviceTasks.Any(s =>
                                    s.DeviceId == d.Id
                                    && s.Status == DeviceTaskStatus.Succeeded
                                    && s.Type == DeviceTaskType.ScanDriverUpdates
                                    && s.DeliveredAt > cutoff
                                    && s.DeliveredAt > t.DeliveredAt))
                    .Max(t => t.DeliveredAt),

                // (c) dated by the unverified attempt.
                UnverifiedAt = _dbContext.DeviceDriverUpdateInstallRecords
                    .Where(r => r.DeviceId == d.Id
                                && r.Status == DriverUpdateInstallStatus.InstalledUnverified
                                && r.LastAttemptAt > cutoff
                                && !_dbContext.DeviceTasks.Any(s =>
                                    s.DeviceId == d.Id
                                    && s.Status == DeviceTaskStatus.Succeeded
                                    && s.Type == DeviceTaskType.ScanDriverUpdates
                                    && s.DeliveredAt > cutoff
                                    && s.DeliveredAt > r.LastAttemptAt))
                    .Max(r => (DateTimeOffset?)r.LastAttemptAt),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (probe is null)
        {
            return null;
        }

        // One scan answers every open question if it runs after the latest point.
        // Ties go to the restart, then the silent install: the order the questions
        // were added in, and any of them names a requester for the same moment.
        (FollowUpQuestion Question, DateTimeOffset? At)[] points =
        [
            (FollowUpQuestion.Restart, probe.RebootUnobserved ? probe.LastBootAt : null),
            (FollowUpQuestion.SilentInstall, probe.SilentInstallAt),
            (FollowUpQuestion.Unverified, probe.UnverifiedAt),
        ];

        FollowUpQuestion? question = null;
        var observeAfter = default(DateTimeOffset);
        foreach (var (candidate, point) in points)
        {
            if (point is { } at && (question is null || at > observeAfter))
            {
                question = candidate;
                observeAfter = at;
            }
        }

        if (question is null)
        {
            return null;
        }

        var since = now - AutoFollowUpScanInterval;
        var held = await _dbContext.DeviceTasks.AsNoTracking().AnyAsync(
            t => t.DeviceId == deviceId
                 && t.Type == DeviceTaskType.ScanDriverUpdates
                 && (t.Status == DeviceTaskStatus.Queued
                     || (t.Status == DeviceTaskStatus.Delivered && t.DeliveredAt > observeAfter)
                     || (t.CreatedAt > observeAfter
                         && (t.CreatedAt >= since || t.DeliveredAt >= since || t.CompletedAt >= since))),
            cancellationToken);

        if (held)
        {
            _logger.LogDebug(
                "Driver-update follow-up for device {DeviceId} held: a scan that will see it is pending, or one was active in the last {Minutes} minutes.",
                deviceId, AutoFollowUpScanInterval.TotalMinutes);
            return null;
        }

        DeviceTask? install;
        string why;
        switch (question)
        {
            case FollowUpQuestion.Restart:
                // The most recent reboot-pending attempt from before this boot whose
                // task still exists: its requester is who the scan is on behalf of.
                install = await (
                        from r in _dbContext.DeviceDriverUpdateInstallRecords.AsNoTracking()
                        join t in _dbContext.DeviceTasks.AsNoTracking() on r.LastTaskId equals (Guid?)t.Id
                        where r.DeviceId == deviceId
                              && r.Status == DriverUpdateInstallStatus.PendingReboot
                              && r.LastAttemptAt < observeAfter
                              && t.DeviceId == deviceId
                              && t.OrganizationId == organizationId
                        orderby r.LastAttemptAt descending
                        select t)
                    .FirstOrDefaultAsync(cancellationToken);
                why = "the device restarted with reboot-pending driver installs and nothing has scanned since";
                break;

            case FollowUpQuestion.SilentInstall:
                install = await _dbContext.DeviceTasks.AsNoTracking()
                    .Where(t => t.DeviceId == deviceId
                                && t.OrganizationId == organizationId
                                && t.Type == DeviceTaskType.InstallDriverUpdate
                                && t.Status == DeviceTaskStatus.Expired
                                && t.DeliveredAt == observeAfter)
                    .FirstOrDefaultAsync(cancellationToken);
                why = "a driver install went silent after delivery and nothing has scanned since";
                break;

            default:
                // The install whose unverified attempt is the latest point.
                install = await (
                        from r in _dbContext.DeviceDriverUpdateInstallRecords.AsNoTracking()
                        join t in _dbContext.DeviceTasks.AsNoTracking() on r.LastTaskId equals (Guid?)t.Id
                        where r.DeviceId == deviceId
                              && r.Status == DriverUpdateInstallStatus.InstalledUnverified
                              && r.LastAttemptAt == observeAfter
                              && t.DeviceId == deviceId
                              && t.OrganizationId == organizationId
                        select t)
                    .FirstOrDefaultAsync(cancellationToken);
                why = "a driver install is still unverified and nothing has scanned since it ran";
                break;
        }

        if (install is null)
        {
            // Debug, not warning: the probe is level-triggered, and a warning here
            // would repeat on every heartbeat for as long as the state lasts.
            _logger.LogDebug(
                "Device {DeviceId} needs a driver-update follow-up scan ({Why}), but the install that asked for it is gone; none queued.",
                deviceId, why);
            return null;
        }

        var (_, scan) = await QueueFollowUpScanCoreAsync(install, why, cancellationToken);
        return scan;
    }

    /// <summary>
    /// Remembers what an <c>InstallDriverUpdate</c> result says happened to each
    /// update, and -- when the result carries the verification list a 1.27.0 agent
    /// takes after installing -- replaces the pending list with it and reconciles.
    /// </summary>
    /// <returns>
    /// Null when the result itself settled everything a scan could (it carried a fresh
    /// offered list and nothing waits on a restart that has already happened);
    /// otherwise the follow-up scan it calls for and the point that scan must post-date.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Two things call for the follow-up: no verification list (the pending list
    /// still describes the machine before the install -- observe after the install),
    /// and a reboot-pending record whose attempt predates the device's current boot
    /// time. The second is the result that lands AFTER the restart (spooled through
    /// it, or accepted late): the heartbeat that saw the restart came first and found
    /// nothing reboot-pending, so without this the record would say "Needs restart"
    /// until the next restart. That follow-up must observe after the restart.
    /// </para>
    /// <para>
    /// A result from an agent before 1.27.0 has no per-update classification, only
    /// <c>installed[]</c>, <c>failed[]</c> and one aggregate reboot flag. Its
    /// installed updates are remembered as reboot-pending when that flag is set and
    /// as unverified otherwise -- never as re-offered: a reboot-pending update may
    /// legitimately still be offered until the restart, so that judgement waits for
    /// a scan taken after one.
    /// </para>
    /// <para>
    /// The pending list is NOT reconciled against a result without a verification
    /// list: it is the pre-install list, every just-installed update is still on
    /// it, and reading that as "still offered" would brand every install re-offered.
    /// The follow-up scan reconciles instead.
    /// </para>
    /// <para>
    /// Agent-supplied and untrusted, like a scan: items without an update id are
    /// dropped, lengths are bounded, the first of any duplicate wins, the count is
    /// capped, and a body that will not parse changes nothing and is logged rather
    /// than failing the completion that carried it.
    /// </para>
    /// </remarks>
    private async Task<FollowUpScanNeed?> ApplyDriverUpdateInstallAsync(
        DeviceTask install, Guid deviceId, string? resultJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The attempt is placed at the moment the device claimed the install, on the
        // server's clock: it is what a later boot time is compared against, and a
        // result that limps in late (after a restart, through the agent's spool)
        // must not move the attempt to after that restart.
        var attemptedAt = install.DeliveredAt ?? now;
        var noFreshList = new FollowUpScanNeed(attemptedAt, "an install result arrived without a fresh offered list");

        DriverUpdateInstallReport? report;
        try
        {
            report = string.IsNullOrWhiteSpace(resultJson)
                ? null
                : JsonSerializer.Deserialize<DriverUpdateInstallReport>(resultJson, JsonOptions);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Driver-update install {TaskId} for device {DeviceId} returned an unreadable result; install memory and pending list left unchanged.",
                install.Id, deviceId);
            return noFreshList;
        }

        if (report is null)
        {
            _logger.LogInformation(
                "Driver-update install {TaskId} for device {DeviceId} returned no result body; nothing to remember.",
                install.Id, deviceId);
            return noFreshList;
        }

        var outcomes = ReadInstallOutcomes(report, install.Id);

        // The pending rows as they stood before this result: the revision each
        // attempted update was offered at, and a title when the agent sent none.
        var pendingBefore = (await _dbContext.DeviceDriverUpdates.AsNoTracking()
                .Where(u => u.DeviceId == deviceId)
                .Select(u => new { u.UpdateId, u.Revision, u.Title })
                .ToListAsync(cancellationToken))
            .GroupBy(u => u.UpdateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var records = await _dbContext.DeviceDriverUpdateInstallRecords
            .Where(r => r.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        var byUpdateId = records
            .GroupBy(r => r.UpdateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var outcome in outcomes)
        {
            pendingBefore.TryGetValue(outcome.UpdateId, out var offered);

            // Titles: the pending row's is Windows Update's own and wins. The agent's
            // is used only to start a record the server has no title for; it never
            // overwrites a stored one, because for an update the agent could not find
            // it may be a placeholder (1.27.0 sends one for "not offered").
            if (byUpdateId.TryGetValue(outcome.UpdateId, out var record))
            {
                // A failure with no result code never reached Windows Update's
                // installer -- in practice the installer refused to start because a
                // restart is already owed (0x80240016 "install in progress" /
                // reboot pending), which is exactly the state a reboot-pending record
                // describes. It says nothing about the update, so it must not
                // overwrite the one memory that tells the operator a restart will
                // finish the earlier install. The record is left COMPLETELY untouched
                // -- not even attempts / last attempt / last task: moving the attempt
                // time forward could put it after the restart that is about to
                // happen, and then the post-restart scan would no longer be allowed
                // to judge the record (stuck "Needs restart" until yet another
                // restart); and counting an attempt that never reached the installer
                // would overstate what was tried. The task row and its audit entry
                // keep the refused attempt on the record.
                if (record.Status == DriverUpdateInstallStatus.PendingReboot
                    && outcome.Status == DriverUpdateInstallStatus.Failed
                    && outcome.ResultCode is null)
                {
                    _logger.LogDebug(
                        "Driver-update install {TaskId} for device {DeviceId}: {UpdateId} failed before reaching the installer; its reboot-pending memory is kept.",
                        install.Id, deviceId, outcome.UpdateId);
                    continue;
                }
            }
            else
            {
                record = new DeviceDriverUpdateInstallRecord(
                    deviceId, outcome.UpdateId, offered?.Revision, offered?.Title ?? outcome.Title ?? outcome.UpdateId, attemptedAt);
                _dbContext.DeviceDriverUpdateInstallRecords.Add(record);
                records.Add(record);
                byUpdateId[outcome.UpdateId] = record;
            }

            record.RecordAttempt(
                outcome.Status, outcome.ResultCode, outcome.Hresult, install.Id,
                offered?.Title, offered?.Revision, attemptedAt);
        }

        _logger.LogInformation(
            "Driver-update install {TaskId} for device {DeviceId}: {Count} outcome(s) remembered{Verification}.",
            install.Id, deviceId, outcomes.Count,
            report.PostScan is null ? "; no verification list, a follow-up scan is needed" : "");

        if (report.PostScan is not null)
        {
            var pending = await ReplacePendingRowsAsync(deviceId, report.PostScan, now, cancellationToken);

            // The verification list was taken at the end of this very install, so it
            // is observed "at" the attempt -- never after a restart that may have
            // happened before the result arrived -- and it may judge this install's
            // own records at that equal time (nothing else may).
            await ReconcileInstallRecordsAsync(
                deviceId, pending, observedAt: attemptedAt, now, records, ownInstallTaskId: install.Id, cancellationToken);
        }

        // A reboot-pending record attempted before the device's current boot time is
        // waiting on a restart that has ALREADY happened: this result landed after it
        // (or an earlier one is still unanswered). Only a scan claimed after that
        // restart can settle it, whatever list this result carried.
        var lastBootAt = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.LastBootAt)
            .SingleOrDefaultAsync(cancellationToken);

        if (lastBootAt is { } bootedAt
            && records.Any(r => r.Status == DriverUpdateInstallStatus.PendingReboot && r.LastAttemptAt < bootedAt))
        {
            // After the restart AND after this install: one scan then answers both.
            return new FollowUpScanNeed(
                bootedAt > attemptedAt ? bootedAt : attemptedAt,
                "a reboot-pending install's result arrived after the device had already restarted");
        }

        return report.PostScan is null ? noFreshList : null;
    }

    /// <summary>
    /// Moves the device's install memories to what a fresh offered list says.
    /// </summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="pending">The offered list just applied.</param>
    /// <param name="observedAt">
    /// When that list was true, on the server's clock: the claim time of the scan,
    /// or of the install whose verification list it is. A list observed before an
    /// attempt says nothing about that attempt and is not used to judge it -- and
    /// neither is one observed at the SAME instant, unless it is the install's own
    /// verification list judging that install's own records: a scan and an install
    /// claimed in one batch share a claim time, and nothing says which ran first.
    /// </param>
    /// <param name="now">Confirmation time for anything found installed.</param>
    /// <param name="records">The device's records when already loaded in this unit of work.</param>
    /// <param name="ownInstallTaskId">
    /// The install whose verification list this is, or null for a scan. Only its own
    /// records may be judged at an equal time.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <para>The rules, per record whose last attempt the list post-dates:</para>
    /// <list type="bullet">
    /// <item>Installed / unverified / re-offered, and no longer offered: installed,
    /// confirmed now (kept as history).</item>
    /// <item>Unverified, and offered again: re-offered -- it came back.</item>
    /// <item>Installed, and offered again within <see cref="InstalledReOfferWindow"/>
    /// of its confirmation: re-offered -- it came back. Later than that it is a fresh
    /// offer (a rollback, a reimage, a driver removed months on), not the install
    /// failing to stick: the stale memory is removed so the update reads as plainly
    /// actionable again and install-all stops skipping it. The install's history
    /// stays in its task row and audit trail.</item>
    /// <item>Reboot-pending: untouched until the device has restarted since the
    /// attempt AND the list was taken after that restart. Then gone is installed and
    /// still offered is re-offered: it survived a restart, and retrying cannot help.
    /// Before the restart, neither presence (a reboot-pending update may still be
    /// offered) nor absence (the restart is still owed, and the console must keep
    /// saying so) is an answer.</item>
    /// <item>Failed: left as it is.</item>
    /// </list>
    /// <para>
    /// "Offered" means offered at the revision that was attempted or lower. The same
    /// update id at a higher revision is a new offer that superseded the attempted
    /// one -- the attempted revision is gone -- not the attempt coming back.
    /// </para>
    /// </remarks>
    private async Task ReconcileInstallRecordsAsync(
        Guid deviceId,
        IReadOnlyList<DeviceDriverUpdate> pending,
        DateTimeOffset observedAt,
        DateTimeOffset now,
        IReadOnlyList<DeviceDriverUpdateInstallRecord>? records,
        Guid? ownInstallTaskId,
        CancellationToken cancellationToken)
    {
        records ??= await _dbContext.DeviceDriverUpdateInstallRecords
            .Where(r => r.DeviceId == deviceId)
            .ToListAsync(cancellationToken);

        if (records.Count == 0)
        {
            return;
        }

        var lastBootAt = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.LastBootAt)
            .SingleOrDefaultAsync(cancellationToken);

        var offeredRevision = pending
            .GroupBy(u => u.UpdateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Revision, StringComparer.OrdinalIgnoreCase);

        var flipped = 0;
        var forgotten = 0;
        foreach (var record in records)
        {
            // Strictly after the attempt -- or, for an install's own verification
            // list, at the attempt for that install's own records (see observedAt).
            var postDates = observedAt > record.LastAttemptAt
                || (ownInstallTaskId is { } own && record.LastTaskId == own && observedAt == record.LastAttemptAt);

            if (!postDates || record.Status == DriverUpdateInstallStatus.Failed)
            {
                continue;
            }

            var stillOffered = offeredRevision.TryGetValue(record.UpdateId, out var revision)
                && (record.Revision is null || revision <= record.Revision);
            var before = record.Status;

            switch (record.Status)
            {
                case DriverUpdateInstallStatus.PendingReboot:
                    var restartedSince = lastBootAt is { } bootedAt
                        && bootedAt > record.LastAttemptAt
                        && bootedAt < observedAt;
                    if (!restartedSince)
                    {
                        break;
                    }

                    if (stillOffered)
                    {
                        record.MarkReOffered();
                    }
                    else
                    {
                        record.ConfirmInstalled(now);
                    }

                    break;

                case DriverUpdateInstallStatus.Installed:
                    if (!stillOffered)
                    {
                        record.ConfirmInstalled(now);
                    }
                    else if (observedAt - (record.ConfirmedAt ?? record.LastAttemptAt) <= InstalledReOfferWindow)
                    {
                        record.MarkReOffered();
                    }
                    else
                    {
                        // Long after the install was confirmed: a fresh offer, not a
                        // re-offer. Forget the stale memory rather than brand it.
                        _dbContext.DeviceDriverUpdateInstallRecords.Remove(record);
                        forgotten++;
                    }

                    break;

                case DriverUpdateInstallStatus.InstalledUnverified:
                    if (stillOffered)
                    {
                        record.MarkReOffered();
                    }
                    else
                    {
                        record.ConfirmInstalled(now);
                    }

                    break;

                case DriverUpdateInstallStatus.ReOffered:
                    if (!stillOffered)
                    {
                        record.ConfirmInstalled(now);
                    }

                    break;
            }

            if (record.Status != before)
            {
                flipped++;
            }
        }

        if (flipped > 0 || forgotten > 0)
        {
            _logger.LogInformation(
                "Driver-update reconcile for device {DeviceId}: {Count} install record(s) changed state; {Forgotten} stale installed record(s) dropped as offered afresh.",
                deviceId, flipped, forgotten);
        }
    }

    /// <summary>
    /// Queues a <c>ScanDriverUpdates</c> on behalf of whoever asked for
    /// <paramref name="install"/>, unless the device already has a scan that will see
    /// what happened after <paramref name="observeAfter"/>: one still Queued (it runs
    /// after now), or one Delivered after that point. A scan Delivered at or before it
    /// does not count -- claimed before the install ran or before the restart, and
    /// quite possibly killed by it, it cannot answer the question even if it is still
    /// "in flight" on paper. Never throws (short of cancellation): it runs after the
    /// work that prompted it has been committed, and must not undo or fail it.
    /// </summary>
    private async Task<(FollowUpScanOutcome Outcome, DeviceTask? Scan)> TryQueueFollowUpScanAsync(
        DeviceTask install, DateTimeOffset observeAfter, string why, CancellationToken cancellationToken)
    {
        var deviceId = install.DeviceId;

        bool alreadyAsked;
        try
        {
            alreadyAsked = await _dbContext.DeviceTasks.AnyAsync(
                t => t.DeviceId == deviceId
                     && t.Type == DeviceTaskType.ScanDriverUpdates
                     && (t.Status == DeviceTaskStatus.Queued
                         || (t.Status == DeviceTaskStatus.Delivered && t.DeliveredAt > observeAfter)),
                cancellationToken);
        }
        catch (Exception ex) when (IsFollowUpFailure(ex, cancellationToken))
        {
            return FollowUpNotQueued(ex, deviceId, why);
        }

        if (alreadyAsked)
        {
            _logger.LogDebug(
                "Follow-up driver-update scan for device {DeviceId} skipped ({Why}): one that will see it is already queued or in flight.",
                deviceId, why);
            return (FollowUpScanOutcome.AlreadyPending, null);
        }

        return await QueueFollowUpScanCoreAsync(install, why, cancellationToken);
    }

    /// <summary>
    /// Queues the follow-up scan itself, with no deduplication -- the callers decide
    /// whether one is needed. Never throws, short of cancellation: a database refusal
    /// and any other failure (a dropped connection, a timeout) alike come back as
    /// <see cref="FollowUpScanOutcome.NotQueued"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Goes through <see cref="QueueAsync"/>, so the catalogue's agent gate, the
    /// scan's lifetime and the audit entry all apply exactly as for a scan an
    /// operator queues. <see cref="QueueAsync"/> performs no permission check of its
    /// own (callers check at the endpoint), so nothing about authorization changes
    /// for human callers. The scan is the platform verifying an install it already
    /// carried out -- read-only on the endpoint, and a lesser authority
    /// (<c>driver.view</c>) than the <c>driver.manage</c> the install required -- and
    /// it is attributed to the install's requester with an "auto-rescan" suffix, so
    /// the audit trail reads as what it is. Restart Management's dispatch queues on
    /// the scheduler's behalf the same way.
    /// </para>
    /// <para>
    /// Every caller runs it after committing the work that prompted it -- a task
    /// result, a sweep's expiries, a heartbeat -- so an exception escaping here would
    /// turn committed work into a 500 (the agent then drops a result the server
    /// already recorded) or abandon the rest of a sweep's batch. A follow-up that
    /// could not be queued is picked up again by the heartbeat probe.
    /// </para>
    /// </remarks>
    private async Task<(FollowUpScanOutcome Outcome, DeviceTask? Scan)> QueueFollowUpScanCoreAsync(
        DeviceTask install, string why, CancellationToken cancellationToken)
    {
        var deviceId = install.DeviceId;

        try
        {
            var payload = new TaskPayloads.ScanDriverUpdates(_timeProvider.GetUtcNow());
            var scan = await QueueAsync(
                install.OrganizationId, deviceId, DeviceTaskType.ScanDriverUpdates, payload,
                install.CreatedByUserId, AutoRescanDisplay(install.CreatedByDisplay), cancellationToken);

            if (scan is null)
            {
                _logger.LogInformation(
                    "Follow-up driver-update scan for device {DeviceId} not queued ({Why}): the device cannot accept it.",
                    deviceId, why);
                return (FollowUpScanOutcome.NotQueued, null);
            }

            _logger.LogInformation(
                "Follow-up driver-update scan {ScanId} queued for device {DeviceId}: {Why}.", scan.Id, deviceId, why);
            return (FollowUpScanOutcome.Queued, scan);
        }
        catch (Exception ex) when (IsFollowUpFailure(ex, cancellationToken))
        {
            return FollowUpNotQueued(ex, deviceId, why);
        }
    }

    /// <summary>
    /// Whether an exception from a follow-up is one to absorb: anything except the
    /// caller's own cancellation -- the same rule the heartbeat applies to the probe.
    /// </summary>
    private static bool IsFollowUpFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// A follow-up that failed after the work that prompted it was committed: nothing
    /// it half-staged may ride along on a later save, so the tracker is cleared (as
    /// the heartbeat does), and the failure is logged rather than thrown.
    /// </summary>
    private (FollowUpScanOutcome Outcome, DeviceTask? Scan) FollowUpNotQueued(Exception exception, Guid deviceId, string why)
    {
        _dbContext.ChangeTracker.Clear();
        _logger.LogWarning(exception,
            "Follow-up driver-update scan for device {DeviceId} could not be queued ({Why}); the work that called for it is already saved.",
            deviceId, why);
        return (FollowUpScanOutcome.NotQueued, null);
    }

    /// <summary>
    /// Words the expiry of a silent, claimed driver install from what actually
    /// happened to its verification scan -- queued, already pending, or not queued --
    /// instead of always claiming one was queued. Best effort: a late result that
    /// landed in between is the truth now and wins (the note is then skipped).
    /// </summary>
    private async Task NoteExpiryFollowUpAsync(Guid installId, FollowUpScanOutcome outcome, CancellationToken cancellationToken)
    {
        var note = outcome switch
        {
            FollowUpScanOutcome.Queued => VerificationScanQueuedNote,
            FollowUpScanOutcome.AlreadyPending => VerificationScanAlreadyPendingNote,
            _ => VerificationScanNotQueuedNote,
        };

        try
        {
            // Tracked: returns the instance the sweep expired when it is still
            // attached, or reloads it after a refused queue cleared the tracker.
            var install = await _dbContext.DeviceTasks.SingleOrDefaultAsync(t => t.Id == installId, cancellationToken);
            if (install is null || !install.NoteExpiryFollowUp(note))
            {
                return;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _dbContext.ChangeTracker.Clear();
            _logger.LogInformation(ex,
                "Expired driver install {TaskId}: its follow-up note was not saved (it changed meanwhile, e.g. a late result landed).",
                installId);
        }
        catch (Exception ex) when (IsFollowUpFailure(ex, cancellationToken))
        {
            // Any other failure (a dropped connection, a timeout) costs this one note,
            // never the rest of the sweep's batch: the expiry itself is committed.
            _dbContext.ChangeTracker.Clear();
            _logger.LogWarning(ex,
                "Expired driver install {TaskId}: its follow-up note could not be saved.", installId);
        }
    }

    /// <summary>"requester · auto-rescan", within the 320 characters a task's creator display allows.</summary>
    internal static string AutoRescanDisplay(string requestedBy)
    {
        var name = string.IsNullOrWhiteSpace(requestedBy) ? "platform" : requestedBy.Trim();
        var room = 320 - AutoRescanSuffix.Length;
        return (name.Length > room ? name[..room] : name) + AutoRescanSuffix;
    }

    /// <summary>
    /// The per-update outcomes an install result reports, validated: the 1.27.0
    /// classified <c>outcomes[]</c> when present, otherwise the legacy
    /// <c>installed[]</c> / <c>failed[]</c> split mapped with the reboot carve-out.
    /// </summary>
    private List<InstallOutcome> ReadInstallOutcomes(DriverUpdateInstallReport report, Guid taskId)
    {
        var outcomes = new List<InstallOutcome>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capped = false;

        void Add(string? updateId, string? title, DriverUpdateInstallStatus status, int? resultCode, long? hresult)
        {
            if (outcomes.Count >= MaxInstallOutcomesPerResult)
            {
                capped = true;
                return;
            }

            // The update id is Windows Update's GUID for the update: absent or too
            // long to be one is a malformed item, and a repeat keeps the first.
            if (string.IsNullOrWhiteSpace(updateId) || updateId.Trim().Length > 64 || !seen.Add(updateId.Trim()))
            {
                return;
            }

            var cleanTitle = string.IsNullOrWhiteSpace(title) ? null : Truncate(title.Trim(), 512);
            if (string.IsNullOrWhiteSpace(cleanTitle))
            {
                cleanTitle = null;
            }
            outcomes.Add(new InstallOutcome(updateId.Trim(), cleanTitle, status, resultCode, ToHresult(hresult)));
        }

        if (report.Outcomes is { } classified)
        {
            // A null element ("outcomes":[null]) is skipped: the deserializer does
            // not enforce the list's nullability.
            foreach (var item in classified)
            {
                if (item is not null && MapOutcome(item) is { } status)
                {
                    Add(item.UpdateId, item.Title, status, item.ResultCode, item.Hresult);
                }
            }
        }
        else
        {
            // Legacy (agent before 1.27.0): one aggregate reboot flag for the batch.
            // When it is set, every installed update is treated as waiting for the
            // restart -- conservative by design: the worst it does is ask for a
            // restart that one update did not need; it can never brand a healthy
            // reboot-pending driver as one the platform cannot complete.
            var installedAs = report.RebootRequired == true
                ? DriverUpdateInstallStatus.PendingReboot
                : DriverUpdateInstallStatus.InstalledUnverified;

            // Null elements are skipped, as in the classified list.
            foreach (var item in report.Installed ?? [])
            {
                if (item is not null)
                {
                    Add(item.UpdateId, item.Title, installedAs, item.ResultCode, item.Hresult);
                }
            }

            foreach (var item in report.Failed ?? [])
            {
                if (item is not null)
                {
                    Add(item.UpdateId, item.Title, DriverUpdateInstallStatus.Failed, item.ResultCode, item.Hresult);
                }
            }
        }

        if (capped)
        {
            _logger.LogWarning(
                "Driver-update install {TaskId} reported more than {Max} outcomes; the rest were dropped.",
                taskId, MaxInstallOutcomesPerResult);
        }

        return outcomes;
    }

    /// <summary>
    /// The install memory for one classified outcome, or null when nothing reached
    /// Windows Update's installer (not offered, a skipped preview) or the status is
    /// unknown -- there is no attempt to remember.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on <c>status</c>. The per-item <c>rebootRequired</c> is the update's
    /// OWN flag, while the agent also files an update under PendingReboot because
    /// of the system-wide reboot flag -- so a PendingReboot outcome may carry
    /// <c>rebootRequired: false</c> and is still reboot-pending.
    /// </para>
    /// <para>
    /// The boolean only ever makes the answer more conservative: an outcome whose
    /// own flag says a restart is required is remembered as reboot-pending even if
    /// its status claims installed or re-offered. The agent never sends that
    /// combination; the server still must not be the one to brand a reboot-pending
    /// update re-offered before a restart.
    /// </para>
    /// <para>
    /// A declined licence is a failure to install as far as the operator is
    /// concerned, so it is remembered as one. A failure with no result code (the
    /// installer was busy, or threw before installing) is still a failure, stored
    /// with whatever codes are present.
    /// </para>
    /// </remarks>
    private DriverUpdateInstallStatus? MapOutcome(DriverUpdateInstallOutcomeItem item)
    {
        var rebootRequired = item.RebootRequired == true;

        switch (item.Status?.Trim())
        {
            case var s when Is(s, "Installed"):
                return rebootRequired ? DriverUpdateInstallStatus.PendingReboot : DriverUpdateInstallStatus.Installed;
            case var s when Is(s, "InstalledUnverified"):
                return rebootRequired ? DriverUpdateInstallStatus.PendingReboot : DriverUpdateInstallStatus.InstalledUnverified;
            case var s when Is(s, "PendingReboot"):
                return DriverUpdateInstallStatus.PendingReboot;
            case var s when Is(s, "ReOffered"):
                return rebootRequired ? DriverUpdateInstallStatus.PendingReboot : DriverUpdateInstallStatus.ReOffered;
            case var s when Is(s, "Failed") || Is(s, "EulaDeclined"):
                return DriverUpdateInstallStatus.Failed;
            case var s when Is(s, "NotOffered") || Is(s, "SkippedPreview"):
                return null;
            default:
                _logger.LogWarning(
                    "Driver-update install outcome for {UpdateId} has an unknown status {Status}; ignored.",
                    item.UpdateId, item.Status);
                return null;
        }

        static bool Is(string? value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An HRESULT as the signed 32-bit value Windows uses, whichever way the agent
    /// wrote it (signed, or unsigned like 0x80240017 = 2149842967). Anything outside
    /// 32 bits is not an HRESULT and is dropped.
    /// </summary>
    private static int? ToHresult(long? value) => value switch
    {
        null => null,
        >= int.MinValue and <= int.MaxValue => (int)value.Value,
        > int.MaxValue and <= uint.MaxValue => unchecked((int)(uint)value.Value),
        _ => null,
    };

    private sealed record InstallOutcome(
        string UpdateId, string? Title, DriverUpdateInstallStatus Status, int? ResultCode, int? Hresult);
}
