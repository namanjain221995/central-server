using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Tasks;

/// <summary>
/// The security-update half of task completion: remembering what each install did,
/// reconciling that memory against every fresh offered list, and queueing the
/// follow-up scans that make the pending list converge on the truth by itself.
/// </summary>
/// <remarks>
/// <para>
/// The twin of the driver-update half (<c>DeviceTaskService.DriverUpdates.cs</c>),
/// rule for rule. The pending list (<see cref="DeviceSecurityUpdate"/>) is only ever
/// what the last scan said. On its own it cannot tell "never tried" from "installed
/// and came back" from "installed, waiting for a restart" -- and before this,
/// nothing ever refreshed it after an install unless the operator's browser tab
/// happened to still be open. Three things fix that, all here:
/// </para>
/// <list type="bullet">
/// <item>every install result, success or failure, is remembered per update
/// (<see cref="DeviceSecurityUpdateInstallRecord"/>), including results from agents
/// older than 1.28.0;</item>
/// <item>every scan-shaped list (a scan, or the verification list a 1.28.0 install
/// carries) is followed by a reconcile that moves those memories to what Windows
/// now says;</item>
/// <item>the server queues the follow-up scan itself whenever an install leaves the
/// list stale: after a result without a verification list, after a reboot-pending
/// result that lands only after the restart, after a claimed install expires
/// silent, and -- level-triggered, from the heartbeat, for up to a week -- whenever
/// a device still holds a question only a scan can answer.</item>
/// </list>
/// <para>
/// Security updates almost always want a restart: PendingReboot is the expected
/// outcome of a cumulative update, and the restart question (a) is the one this
/// half answers most. The bounds -- <see cref="AutoFollowUpScanInterval"/>,
/// <see cref="FollowUpLookback"/>, <see cref="InstalledReOfferWindow"/> -- are the
/// driver half's, shared rather than duplicated, so the two features can never
/// disagree on how patient the platform is.
/// </para>
/// <para>
/// Every follow-up is deduplicated against the security scans already asked for, but
/// a scan only counts if it can actually see what needs seeing: a Queued scan always
/// can; a Delivered one only if it was claimed after the point that needs observing.
/// </para>
/// </remarks>
public sealed partial class DeviceTaskService
{
    /// <summary>
    /// Queues a security-update scan when the device holds a question only a scan can
    /// answer and nothing has answered it -- level-triggered, so a missed moment is
    /// picked up on the next heartbeat instead of never. Called from the agent
    /// heartbeat after the new boot time is saved, after (and independently of) the
    /// driver probe.
    /// </summary>
    /// <returns>The queued scan, or null when nothing was queued.</returns>
    /// <remarks>
    /// <para>The three questions, each dated by its own evidence:</para>
    /// <list type="bullet">
    /// <item>(a) a reboot-pending install record attempted before the device's
    /// current boot time, and no successful security scan claimed after that boot --
    /// the restart happened and its outcome has not been observed. Dated by the boot.</item>
    /// <item>(b) a security install that expired after delivery (the device went
    /// silent mid-install) with no successful security scan claimed after that
    /// delivery. Dated by the delivery.</item>
    /// <item>(c) an install remembered as unverified ("Verifying...") with no
    /// successful security scan claimed after its attempt. Dated by the attempt.</item>
    /// </list>
    /// <para>
    /// One scan claimed after the LATEST open point answers every open question, so
    /// that latest point is what the follow-up must post-date.
    /// </para>
    /// <para>
    /// Given up after <see cref="FollowUpLookback"/>: a question whose evidence is
    /// older than that is not asked (its record keeps its status; the operator can
    /// still scan). That bound is written into the SQL, so every task and record the
    /// probe reads is range-limited to the last week of evidence.
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
    /// claimed, or ended -- of a scan asked for after that point. A scan asked for
    /// BEFORE the point never holds the follow-up back.
    /// </para>
    /// <para>
    /// Acts on behalf of whoever asked for the install that defines the latest point,
    /// like every other follow-up.
    /// </para>
    /// </remarks>
    public async Task<DeviceTask?> QueueSecurityFollowUpScanIfNeededAsync(
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
                    && _dbContext.DeviceSecurityUpdateInstallRecords.Any(r =>
                        r.DeviceId == d.Id
                        && r.Status == SecurityUpdateInstallStatus.PendingReboot
                        && r.LastAttemptAt < d.LastBootAt)
                    && !_dbContext.DeviceTasks.Any(s =>
                        s.DeviceId == d.Id
                        && s.Status == DeviceTaskStatus.Succeeded
                        && s.Type == DeviceTaskType.ScanSecurityUpdates
                        && s.DeliveredAt > cutoff
                        && s.DeliveredAt > d.LastBootAt),

                // (b) dated by the silent install's delivery.
                SilentInstallAt = _dbContext.DeviceTasks
                    .Where(t => t.DeviceId == d.Id
                                && t.Status == DeviceTaskStatus.Expired
                                && t.Type == DeviceTaskType.InstallSecurityUpdate
                                && t.DeliveredAt > cutoff
                                && !_dbContext.DeviceTasks.Any(s =>
                                    s.DeviceId == d.Id
                                    && s.Status == DeviceTaskStatus.Succeeded
                                    && s.Type == DeviceTaskType.ScanSecurityUpdates
                                    && s.DeliveredAt > cutoff
                                    && s.DeliveredAt > t.DeliveredAt))
                    .Max(t => t.DeliveredAt),

                // (c) dated by the unverified attempt.
                UnverifiedAt = _dbContext.DeviceSecurityUpdateInstallRecords
                    .Where(r => r.DeviceId == d.Id
                                && r.Status == SecurityUpdateInstallStatus.InstalledUnverified
                                && r.LastAttemptAt > cutoff
                                && !_dbContext.DeviceTasks.Any(s =>
                                    s.DeviceId == d.Id
                                    && s.Status == DeviceTaskStatus.Succeeded
                                    && s.Type == DeviceTaskType.ScanSecurityUpdates
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
        // Ties go to the restart, then the silent install, as for drivers.
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
                 && t.Type == DeviceTaskType.ScanSecurityUpdates
                 && (t.Status == DeviceTaskStatus.Queued
                     || (t.Status == DeviceTaskStatus.Delivered && t.DeliveredAt > observeAfter)
                     || (t.CreatedAt > observeAfter
                         && (t.CreatedAt >= since || t.DeliveredAt >= since || t.CompletedAt >= since))),
            cancellationToken);

        if (held)
        {
            _logger.LogDebug(
                "Security-update follow-up for device {DeviceId} held: a scan that will see it is pending, or one was active in the last {Minutes} minutes.",
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
                        from r in _dbContext.DeviceSecurityUpdateInstallRecords.AsNoTracking()
                        join t in _dbContext.DeviceTasks.AsNoTracking() on r.LastTaskId equals (Guid?)t.Id
                        where r.DeviceId == deviceId
                              && r.Status == SecurityUpdateInstallStatus.PendingReboot
                              && r.LastAttemptAt < observeAfter
                              && t.DeviceId == deviceId
                              && t.OrganizationId == organizationId
                        orderby r.LastAttemptAt descending
                        select t)
                    .FirstOrDefaultAsync(cancellationToken);
                why = "the device restarted with reboot-pending security installs and nothing has scanned since";
                break;

            case FollowUpQuestion.SilentInstall:
                install = await _dbContext.DeviceTasks.AsNoTracking()
                    .Where(t => t.DeviceId == deviceId
                                && t.OrganizationId == organizationId
                                && t.Type == DeviceTaskType.InstallSecurityUpdate
                                && t.Status == DeviceTaskStatus.Expired
                                && t.DeliveredAt == observeAfter)
                    .FirstOrDefaultAsync(cancellationToken);
                why = "a security install went silent after delivery and nothing has scanned since";
                break;

            default:
                // The install whose unverified attempt is the latest point.
                install = await (
                        from r in _dbContext.DeviceSecurityUpdateInstallRecords.AsNoTracking()
                        join t in _dbContext.DeviceTasks.AsNoTracking() on r.LastTaskId equals (Guid?)t.Id
                        where r.DeviceId == deviceId
                              && r.Status == SecurityUpdateInstallStatus.InstalledUnverified
                              && r.LastAttemptAt == observeAfter
                              && t.DeviceId == deviceId
                              && t.OrganizationId == organizationId
                        select t)
                    .FirstOrDefaultAsync(cancellationToken);
                why = "a security install is still unverified and nothing has scanned since it ran";
                break;
        }

        if (install is null)
        {
            // Debug, not warning: the probe is level-triggered, and a warning here
            // would repeat on every heartbeat for as long as the state lasts.
            _logger.LogDebug(
                "Device {DeviceId} needs a security-update follow-up scan ({Why}), but the install that asked for it is gone; none queued.",
                deviceId, why);
            return null;
        }

        var (_, scan) = await QueueSecurityFollowUpScanCoreAsync(install, why, cancellationToken);
        return scan;
    }

    /// <summary>
    /// Remembers what an <c>InstallSecurityUpdate</c> result says happened to each
    /// update, and -- when the result carries the verification list a 1.28.0 agent
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
    /// time -- the result that lands AFTER the restart (spooled through it, or
    /// accepted late). That follow-up must observe after the restart.
    /// </para>
    /// <para>
    /// A result from an agent before 1.28.0 has no per-update classification, only
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
    private async Task<FollowUpScanNeed?> ApplySecurityUpdateInstallAsync(
        DeviceTask install, Guid deviceId, string? resultJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The attempt is placed at the moment the device claimed the install, on the
        // server's clock: it is what a later boot time is compared against, and a
        // result that limps in late (after a restart, through the agent's spool)
        // must not move the attempt to after that restart.
        var attemptedAt = install.DeliveredAt ?? now;
        var noFreshList = new FollowUpScanNeed(attemptedAt, "an install result arrived without a fresh offered list");

        SecurityUpdateInstallReport? report;
        try
        {
            report = string.IsNullOrWhiteSpace(resultJson)
                ? null
                : JsonSerializer.Deserialize<SecurityUpdateInstallReport>(resultJson, JsonOptions);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Security-update install {TaskId} for device {DeviceId} returned an unreadable result; install memory and pending list left unchanged.",
                install.Id, deviceId);
            return noFreshList;
        }

        if (report is null)
        {
            _logger.LogInformation(
                "Security-update install {TaskId} for device {DeviceId} returned no result body; nothing to remember.",
                install.Id, deviceId);
            return noFreshList;
        }

        var outcomes = ReadSecurityInstallOutcomes(report, install.Id);

        // The pending rows as they stood before this result: the revision each
        // attempted update was offered at, and a title when the agent sent none.
        var pendingBefore = (await _dbContext.DeviceSecurityUpdates.AsNoTracking()
                .Where(u => u.DeviceId == deviceId)
                .Select(u => new { u.UpdateId, u.Revision, u.Title })
                .ToListAsync(cancellationToken))
            .GroupBy(u => u.UpdateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var records = await _dbContext.DeviceSecurityUpdateInstallRecords
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
            // it may be a placeholder.
            if (byUpdateId.TryGetValue(outcome.UpdateId, out var record))
            {
                // A failure with no result code never reached Windows Update's
                // installer -- in practice the installer refused to start because a
                // restart is already owed, which is exactly the state a
                // reboot-pending record describes. It says nothing about the update,
                // so the record is left COMPLETELY untouched (not even attempts /
                // last attempt / last task), for the same reasons as for drivers: the
                // post-restart scan must still be allowed to judge it, and an attempt
                // that never reached the installer is not an attempt. The task row
                // and its audit entry keep the refused attempt on the record.
                if (record.Status == SecurityUpdateInstallStatus.PendingReboot
                    && outcome.Status == SecurityUpdateInstallStatus.Failed
                    && outcome.ResultCode is null)
                {
                    _logger.LogDebug(
                        "Security-update install {TaskId} for device {DeviceId}: {UpdateId} failed before reaching the installer; its reboot-pending memory is kept.",
                        install.Id, deviceId, outcome.UpdateId);
                    continue;
                }
            }
            else
            {
                record = new DeviceSecurityUpdateInstallRecord(
                    deviceId, outcome.UpdateId, offered?.Revision, offered?.Title ?? outcome.Title ?? outcome.UpdateId, attemptedAt);
                _dbContext.DeviceSecurityUpdateInstallRecords.Add(record);
                records.Add(record);
                byUpdateId[outcome.UpdateId] = record;
            }

            record.RecordAttempt(
                outcome.Status, outcome.ResultCode, outcome.Hresult, install.Id,
                offered?.Title, offered?.Revision, attemptedAt);
        }

        _logger.LogInformation(
            "Security-update install {TaskId} for device {DeviceId}: {Count} outcome(s) remembered{Verification}.",
            install.Id, deviceId, outcomes.Count,
            report.PostScan is null ? "; no verification list, a follow-up scan is needed" : "");

        if (report.PostScan is not null)
        {
            var pending = await ReplaceSecurityPendingRowsAsync(deviceId, report.PostScan, now, cancellationToken);

            // The verification list was taken at the end of this very install, so it
            // is observed "at" the attempt -- never after a restart that may have
            // happened before the result arrived -- and it may judge this install's
            // own records at that equal time (nothing else may).
            await ReconcileSecurityInstallRecordsAsync(
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
            && records.Any(r => r.Status == SecurityUpdateInstallStatus.PendingReboot && r.LastAttemptAt < bootedAt))
        {
            // After the restart AND after this install: one scan then answers both.
            return new FollowUpScanNeed(
                bootedAt > attemptedAt ? bootedAt : attemptedAt,
                "a reboot-pending install's result arrived after the device had already restarted");
        }

        return report.PostScan is null ? noFreshList : null;
    }

    /// <summary>
    /// Moves the device's security install memories to what a fresh offered list says.
    /// </summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="pending">The offered list just applied.</param>
    /// <param name="observedAt">
    /// When that list was true, on the server's clock: the claim time of the scan,
    /// or of the install whose verification list it is. A list observed before an
    /// attempt says nothing about that attempt and is not used to judge it -- and
    /// neither is one observed at the SAME instant, unless it is the install's own
    /// verification list judging that install's own records.
    /// </param>
    /// <param name="now">Confirmation time for anything found installed.</param>
    /// <param name="records">The device's records when already loaded in this unit of work.</param>
    /// <param name="ownInstallTaskId">
    /// The install whose verification list this is, or null for a scan. Only its own
    /// records may be judged at an equal time.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <para>The rules, per record whose last attempt the list post-dates -- the
    /// driver reconcile's, unchanged:</para>
    /// <list type="bullet">
    /// <item>Installed / unverified / re-offered, and no longer offered: installed,
    /// confirmed now (kept as history).</item>
    /// <item>Unverified, and offered again: re-offered -- it came back.</item>
    /// <item>Installed, and offered again within <see cref="InstalledReOfferWindow"/>
    /// of its confirmation: re-offered. Later than that it is a fresh offer, and the
    /// stale memory is removed so the update reads as plainly actionable again.</item>
    /// <item>Reboot-pending: untouched until the device has restarted since the
    /// attempt AND the list was taken after that restart. Then gone is installed and
    /// still offered is re-offered: it survived a restart, and retrying cannot help.</item>
    /// <item>Failed: left as it is.</item>
    /// </list>
    /// <para>
    /// "Offered" means offered at the revision that was attempted or lower. The same
    /// update id at a higher revision is a new offer that superseded the attempted
    /// one, not the attempt coming back.
    /// </para>
    /// </remarks>
    private async Task ReconcileSecurityInstallRecordsAsync(
        Guid deviceId,
        IReadOnlyList<DeviceSecurityUpdate> pending,
        DateTimeOffset observedAt,
        DateTimeOffset now,
        IReadOnlyList<DeviceSecurityUpdateInstallRecord>? records,
        Guid? ownInstallTaskId,
        CancellationToken cancellationToken)
    {
        records ??= await _dbContext.DeviceSecurityUpdateInstallRecords
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

            if (!postDates || record.Status == SecurityUpdateInstallStatus.Failed)
            {
                continue;
            }

            var stillOffered = offeredRevision.TryGetValue(record.UpdateId, out var revision)
                && (record.Revision is null || revision <= record.Revision);
            var before = record.Status;

            switch (record.Status)
            {
                case SecurityUpdateInstallStatus.PendingReboot:
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

                case SecurityUpdateInstallStatus.Installed:
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
                        _dbContext.DeviceSecurityUpdateInstallRecords.Remove(record);
                        forgotten++;
                    }

                    break;

                case SecurityUpdateInstallStatus.InstalledUnverified:
                    if (stillOffered)
                    {
                        record.MarkReOffered();
                    }
                    else
                    {
                        record.ConfirmInstalled(now);
                    }

                    break;

                case SecurityUpdateInstallStatus.ReOffered:
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
                "Security-update reconcile for device {DeviceId}: {Count} install record(s) changed state; {Forgotten} stale installed record(s) dropped as offered afresh.",
                deviceId, flipped, forgotten);
        }
    }

    /// <summary>
    /// Queues a <c>ScanSecurityUpdates</c> on behalf of whoever asked for
    /// <paramref name="install"/>, unless the device already has a security scan that
    /// will see what happened after <paramref name="observeAfter"/>: one still Queued,
    /// or one Delivered after that point. A scan Delivered at or before it does not
    /// count. Never throws (short of cancellation): it runs after the work that
    /// prompted it has been committed, and must not undo or fail it.
    /// </summary>
    private async Task<(FollowUpScanOutcome Outcome, DeviceTask? Scan)> TryQueueSecurityFollowUpScanAsync(
        DeviceTask install, DateTimeOffset observeAfter, string why, CancellationToken cancellationToken)
    {
        var deviceId = install.DeviceId;

        bool alreadyAsked;
        try
        {
            alreadyAsked = await _dbContext.DeviceTasks.AnyAsync(
                t => t.DeviceId == deviceId
                     && t.Type == DeviceTaskType.ScanSecurityUpdates
                     && (t.Status == DeviceTaskStatus.Queued
                         || (t.Status == DeviceTaskStatus.Delivered && t.DeliveredAt > observeAfter)),
                cancellationToken);
        }
        catch (Exception ex) when (IsFollowUpFailure(ex, cancellationToken))
        {
            return SecurityFollowUpNotQueued(ex, deviceId, why);
        }

        if (alreadyAsked)
        {
            _logger.LogDebug(
                "Follow-up security-update scan for device {DeviceId} skipped ({Why}): one that will see it is already queued or in flight.",
                deviceId, why);
            return (FollowUpScanOutcome.AlreadyPending, null);
        }

        return await QueueSecurityFollowUpScanCoreAsync(install, why, cancellationToken);
    }

    /// <summary>
    /// Queues the follow-up security scan itself, with no deduplication -- the callers
    /// decide whether one is needed. Never throws, short of cancellation.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="QueueAsync"/>, so the catalogue's agent gate, the
    /// scan's lifetime and the audit entry all apply exactly as for a scan an
    /// operator queues. The scan is the platform verifying an install it already
    /// carried out -- read-only on the endpoint, and a lesser authority
    /// (<c>update.view</c>) than the <c>update.manage</c> the install required -- and
    /// it is attributed to the install's requester with an "auto-rescan" suffix.
    /// </remarks>
    private async Task<(FollowUpScanOutcome Outcome, DeviceTask? Scan)> QueueSecurityFollowUpScanCoreAsync(
        DeviceTask install, string why, CancellationToken cancellationToken)
    {
        var deviceId = install.DeviceId;

        try
        {
            var payload = new TaskPayloads.ScanSecurityUpdates(_timeProvider.GetUtcNow());
            var scan = await QueueAsync(
                install.OrganizationId, deviceId, DeviceTaskType.ScanSecurityUpdates, payload,
                install.CreatedByUserId, AutoRescanDisplay(install.CreatedByDisplay), cancellationToken);

            if (scan is null)
            {
                _logger.LogInformation(
                    "Follow-up security-update scan for device {DeviceId} not queued ({Why}): the device cannot accept it.",
                    deviceId, why);
                return (FollowUpScanOutcome.NotQueued, null);
            }

            _logger.LogInformation(
                "Follow-up security-update scan {ScanId} queued for device {DeviceId}: {Why}.", scan.Id, deviceId, why);
            return (FollowUpScanOutcome.Queued, scan);
        }
        catch (Exception ex) when (IsFollowUpFailure(ex, cancellationToken))
        {
            return SecurityFollowUpNotQueued(ex, deviceId, why);
        }
    }

    /// <summary>
    /// A security follow-up that failed after the work that prompted it was committed:
    /// the tracker is cleared, and the failure is logged rather than thrown.
    /// </summary>
    private (FollowUpScanOutcome Outcome, DeviceTask? Scan) SecurityFollowUpNotQueued(Exception exception, Guid deviceId, string why)
    {
        _dbContext.ChangeTracker.Clear();
        _logger.LogWarning(exception,
            "Follow-up security-update scan for device {DeviceId} could not be queued ({Why}); the work that called for it is already saved.",
            deviceId, why);
        return (FollowUpScanOutcome.NotQueued, null);
    }

    /// <summary>
    /// Words the expiry of a silent, claimed security install from what actually
    /// happened to its verification scan -- queued, already pending, or not queued.
    /// Best effort: a late result that landed in between is the truth now and wins.
    /// </summary>
    private async Task NoteSecurityExpiryFollowUpAsync(Guid installId, FollowUpScanOutcome outcome, CancellationToken cancellationToken)
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
                "Expired security install {TaskId}: its follow-up note was not saved (it changed meanwhile, e.g. a late result landed).",
                installId);
        }
        catch (Exception ex) when (IsFollowUpFailure(ex, cancellationToken))
        {
            // Any other failure costs this one note, never the rest of the sweep's
            // batch: the expiry itself is committed.
            _dbContext.ChangeTracker.Clear();
            _logger.LogWarning(ex,
                "Expired security install {TaskId}: its follow-up note could not be saved.", installId);
        }
    }

    /// <summary>
    /// The per-update outcomes a security install result reports, validated: the
    /// 1.28.0 classified <c>outcomes[]</c> when present, otherwise the legacy
    /// <c>installed[]</c> / <c>failed[]</c> split mapped with the reboot carve-out.
    /// </summary>
    private List<SecurityInstallOutcome> ReadSecurityInstallOutcomes(SecurityUpdateInstallReport report, Guid taskId)
    {
        var outcomes = new List<SecurityInstallOutcome>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capped = false;

        void Add(string? updateId, string? title, SecurityUpdateInstallStatus status, int? resultCode, long? hresult)
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
            outcomes.Add(new SecurityInstallOutcome(updateId.Trim(), cleanTitle, status, resultCode, ToHresult(hresult)));
        }

        if (report.Outcomes is { } classified)
        {
            // A null element ("outcomes":[null]) is skipped: the deserializer does
            // not enforce the list's nullability.
            foreach (var item in classified)
            {
                if (item is not null && MapSecurityOutcome(item) is { } status)
                {
                    Add(item.UpdateId, item.Title, status, item.ResultCode, item.Hresult);
                }
            }
        }
        else
        {
            // Legacy (agent 1.24.0-1.27.0): one aggregate reboot flag for the batch.
            // When it is set, every installed update is treated as waiting for the
            // restart -- conservative by design: the worst it does is ask for a
            // restart that one update did not need; it can never brand a healthy
            // reboot-pending update as one the platform cannot complete.
            var installedAs = report.RebootRequired == true
                ? SecurityUpdateInstallStatus.PendingReboot
                : SecurityUpdateInstallStatus.InstalledUnverified;

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
                    Add(item.UpdateId, item.Title, SecurityUpdateInstallStatus.Failed, item.ResultCode, item.Hresult);
                }
            }
        }

        if (capped)
        {
            _logger.LogWarning(
                "Security-update install {TaskId} reported more than {Max} outcomes; the rest were dropped.",
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
    /// Keyed on <c>status</c>, exactly as for drivers: a PendingReboot outcome may
    /// carry <c>rebootRequired: false</c> (filed there by the system-wide flag) and
    /// is still reboot-pending, and the per-item flag only ever makes the answer more
    /// conservative. A declined licence is remembered as a failure.
    /// </remarks>
    private SecurityUpdateInstallStatus? MapSecurityOutcome(SecurityUpdateInstallOutcomeItem item)
    {
        var rebootRequired = item.RebootRequired == true;

        switch (item.Status?.Trim())
        {
            case var s when Is(s, "Installed"):
                return rebootRequired ? SecurityUpdateInstallStatus.PendingReboot : SecurityUpdateInstallStatus.Installed;
            case var s when Is(s, "InstalledUnverified"):
                return rebootRequired ? SecurityUpdateInstallStatus.PendingReboot : SecurityUpdateInstallStatus.InstalledUnverified;
            case var s when Is(s, "PendingReboot"):
                return SecurityUpdateInstallStatus.PendingReboot;
            case var s when Is(s, "ReOffered"):
                return rebootRequired ? SecurityUpdateInstallStatus.PendingReboot : SecurityUpdateInstallStatus.ReOffered;
            case var s when Is(s, "Failed") || Is(s, "EulaDeclined"):
                return SecurityUpdateInstallStatus.Failed;
            case var s when Is(s, "NotOffered") || Is(s, "SkippedPreview"):
                return null;
            default:
                _logger.LogWarning(
                    "Security-update install outcome for {UpdateId} has an unknown status {Status}; ignored.",
                    item.UpdateId, item.Status);
                return null;
        }

        static bool Is(string? value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SecurityInstallOutcome(
        string UpdateId, string? Title, SecurityUpdateInstallStatus Status, int? ResultCode, int? Hresult);
}
