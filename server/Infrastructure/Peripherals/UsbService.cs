using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Peripherals;

public enum UsbGrantOutcome
{
    Success,
    /// <summary>No such device, or not this organization's, or retired.</summary>
    DeviceNotFound,
    /// <summary>No such USB device on that endpoint.</summary>
    UsbDeviceNotFound,
    /// <summary>The target is neither storage nor a portable device. Policy does not apply to a keyboard.</summary>
    NotStorage,
    /// <summary>Asked-for duration is outside the permitted window.</summary>
    InvalidDuration,
    /// <summary>The requested access level is not one the platform grants.</summary>
    InvalidPolicy,
    /// <summary>
    /// Read-only was asked for on a device that has no read-only mode — a
    /// phone or camera. The endpoint could not enforce it, so it is refused here
    /// rather than recorded and then reported as failed.
    /// </summary>
    ReadOnlyUnsupported,
    /// <summary>A live grant already covers this device.</summary>
    AlreadyGranted,
}

public enum UsbRevokeOutcome
{
    Success,
    NotFound,
    /// <summary>Nothing live to revoke — already expired, rejected or revoked.</summary>
    NotLive,
}

/// <summary>
/// Everything the platform does with USB peripherals: ingesting what endpoints
/// report, granting and revoking temporary access to storage and portable
/// devices, and expiring grants.
/// </summary>
/// <remarks>
/// <para>
/// The security model in one paragraph. USB storage and portable devices
/// (phones, tablets, cameras) are <b>restricted by default</b> — not by a policy
/// that has to be pushed, but because the agent restricts anything it has no
/// live grant for, including on a machine that has never reached the server.
/// Access is granted per exact device instance, always with an absolute
/// deadline, and always by an administrator holding <c>usb.manage</c>. Every
/// grant, revocation and expiry is audited.
/// </para>
/// <para>
/// Policy is delivered as whole state through two channels — a pushed
/// <c>ApplyUsbPolicy</c> task for immediacy, and the response to the agent's own
/// USB report for convergence — which carry identical content built by
/// <see cref="BuildPolicyAsync"/>. Losing both leaves the endpoint restricted.
/// </para>
/// </remarks>
public sealed class UsbService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<UsbService> logger)
{
    /// <summary>
    /// Cap on devices accepted in one report. A machine with more than this many
    /// USB endpoints attached is not a machine, it is a hostile payload.
    /// </summary>
    public const int MaxDevicesPerReport = 256;

    // The audit state columns are jsonb, so these have to be JSON documents
    // rather than bare words; a naked "Restricted" is not valid JSON and the
    // insert would fail at the database, taking the grant down with it.
    private const string RestrictedState = """{"policy":"Restricted"}""";
    private const string ReadOnlyState = """{"policy":"ReadOnly"}""";
    private const string EnabledState = """{"policy":"Enabled"}""";

    /// <summary>The prior-state document for an audit row about a live grant.</summary>
    private static string GrantedState(UsbStoragePolicy policy) =>
        policy == UsbStoragePolicy.Enabled ? EnabledState : ReadOnlyState;

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly DeviceTaskService _taskService = taskService
        ?? throw new ArgumentNullException(nameof(taskService));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<UsbService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    // ---- agent-facing ------------------------------------------------------

    /// <summary>
    /// Applies a whole-state USB report from an endpoint and returns the policy
    /// that endpoint should be enforcing.
    /// </summary>
    /// <remarks>
    /// Deliberately does not create, extend or alter any grant. An agent report
    /// is untrusted input: it can tell the server what hardware it sees and what
    /// it is enforcing, and nothing it says can widen its own access. The only
    /// path to a grant is an administrator with <c>usb.manage</c>.
    /// </remarks>
    public async Task<UsbPolicyResponse> IngestReportAsync(
        Device device,
        UsbReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(report);

        var now = _timeProvider.GetUtcNow();

        var reported = (report.Devices ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d.InstanceId))
            .Take(MaxDevicesPerReport)
            .ToList();

        var known = await _dbContext.UsbDevices
            .Where(d => d.DeviceId == device.Id)
            .ToListAsync(cancellationToken);

        var byInstance = known.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newStorage = 0;
        var newPortable = 0;

        foreach (var entry in reported)
        {
            var instanceId = Truncate(entry.InstanceId.Trim(), 512)!;
            if (!seen.Add(instanceId))
            {
                continue;
            }

            var deviceClass = ParseClass(entry.DeviceClass);

            if (byInstance.TryGetValue(instanceId, out var existing))
            {
                if (entry.IsConnected)
                {
                    existing.Seen(
                        deviceClass,
                        Truncate(entry.Manufacturer, 256),
                        Truncate(entry.Product, 256),
                        Truncate(entry.HardwareIds, 1024),
                        now);
                }
                else
                {
                    existing.Disconnected(now);
                }

                RecordEnforcement(existing, entry, now);
            }
            else
            {
                var created = new UsbDevice(
                    device.OrganizationId,
                    device.Id,
                    instanceId,
                    deviceClass,
                    Truncate(entry.VendorId, 8),
                    Truncate(entry.ProductId, 8),
                    Truncate(entry.SerialNumber, 128),
                    Truncate(entry.Manufacturer, 256),
                    Truncate(entry.Product, 256),
                    Truncate(entry.HardwareIds, 1024),
                    now);

                if (!entry.IsConnected)
                {
                    created.Disconnected(now);
                }

                RecordEnforcement(created, entry, now);
                _dbContext.UsbDevices.Add(created);
                byInstance[instanceId] = created;

                if (created.IsStorage)
                {
                    newStorage++;
                }
                else if (created.IsPortableDevice)
                {
                    newPortable++;
                }
            }
        }

        // Anything the endpoint did not mention is no longer attached. Its policy
        // is preserved (see UsbDevice.Disconnected) so re-plugging cannot be used
        // to shed a restriction.
        foreach (var absent in known.Where(d => d.IsConnected && !seen.Contains(d.InstanceId)))
        {
            absent.Disconnected(now);
        }

        // First sighting of removable storage, or of a phone, on a managed
        // endpoint is a security event in its own right, whatever happens to it
        // afterwards. Two actions rather than one so the two can be reported on
        // separately: "who plugged in a stick" and "who plugged in a phone" are
        // different questions.
        if (newStorage > 0)
        {
            _auditWriter.Stage(
                device.OrganizationId,
                AuditActorType.Agent,
                device.Id,
                device.Hostname,
                action: "usb.storage.connected",
                AuditResult.Success,
                audit => audit
                    .OnDevice(device.Id, device.Hostname)
                    .OnTarget("usb_device", device.Id.ToString(), $"{newStorage} new storage device(s)"));
        }

        if (newPortable > 0)
        {
            _auditWriter.Stage(
                device.OrganizationId,
                AuditActorType.Agent,
                device.Id,
                device.Hostname,
                action: "usb.portable_device.connected",
                AuditResult.Success,
                audit => audit
                    .OnDevice(device.Id, device.Hostname)
                    .OnTarget("usb_device", device.Id.ToString(), $"{newPortable} new portable device(s)"));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await BuildPolicyAsync(device.Id, now, cancellationToken);
    }

    /// <summary>
    /// The authoritative policy for one endpoint: every grant that is live at
    /// <paramref name="now"/>, and nothing else.
    /// </summary>
    /// <remarks>
    /// The single source of both delivery channels, so the pushed task and the
    /// report response can never disagree. Liveness is computed from the clock
    /// rather than from the stored status, so a grant whose deadline has passed
    /// stops being included the instant it lapses — no dependency on the expiry
    /// sweep having run.
    /// </remarks>
    public async Task<UsbPolicyResponse> BuildPolicyAsync(
        Guid deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var grants = await _dbContext.UsbAccessRequests
            .Where(r => r.DeviceId == deviceId
                && r.Status == UsbAccessRequestStatus.Approved
                && r.ExpiresAt != null
                && r.ExpiresAt > now)
            .Select(r => new { r.InstanceId, r.ExpiresAt, r.GrantedPolicy })
            .ToListAsync(cancellationToken);

        return new UsbPolicyResponse(
            grants
                .Select(g => new UsbPolicyGrant(
                    g.InstanceId,
                    g.GrantedPolicy.ToString(),
                    g.ExpiresAt!.Value))
                .ToList(),
            now);
    }

    // ---- administrator-facing ---------------------------------------------

    /// <summary>
    /// Grants temporary access to one USB storage or portable device at the
    /// requested level and pushes the new policy to the endpoint.
    /// </summary>
    /// <remarks>
    /// The caller's <c>usb.manage</c> permission has already been enforced at the
    /// endpoint. This method still re-checks everything about the target that
    /// matters — that it exists, that it belongs to this organization, that
    /// policy applies to it, that the level can be enforced on it, that the
    /// duration is inside the permitted window — because a permission check
    /// answers "may this person act", not "is this action sane".
    /// </remarks>
    public async Task<(UsbGrantOutcome Outcome, UsbAccessRequest? Request)> GrantAsync(
        Guid organizationId,
        Guid deviceId,
        Guid usbDeviceId,
        UsbStoragePolicy policy,
        string justification,
        TimeSpan duration,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        // Restricted is not a grant, and an undefined value is not a policy.
        // Checked before anything else touches the database, so a malformed
        // request cannot get as far as creating a row.
        if (policy == UsbStoragePolicy.Restricted || !Enum.IsDefined(policy))
        {
            return (UsbGrantOutcome.InvalidPolicy, null);
        }

        if (duration < UsbAccessRequest.MinimumDuration || duration > UsbAccessRequest.MaximumDuration)
        {
            return (UsbGrantOutcome.InvalidDuration, null);
        }

        var device = await _dbContext.Devices.SingleOrDefaultAsync(
            d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return (UsbGrantOutcome.DeviceNotFound, null);
        }

        var usb = await _dbContext.UsbDevices.SingleOrDefaultAsync(
            u => u.Id == usbDeviceId && u.DeviceId == deviceId, cancellationToken);

        if (usb is null)
        {
            return (UsbGrantOutcome.UsbDeviceNotFound, null);
        }

        if (!usb.IsRestrictable)
        {
            return (UsbGrantOutcome.NotStorage, null);
        }

        // Refused rather than rounded. Rounding up would hand out write access
        // the administrator did not ask for; rounding down to Restricted would
        // record a grant the endpoint then reports as failed. Neither is what
        // was decided, so nothing is recorded.
        if (policy == UsbStoragePolicy.ReadOnly && !usb.SupportsReadOnly)
        {
            return (UsbGrantOutcome.ReadOnlyUnsupported, null);
        }

        var now = _timeProvider.GetUtcNow();

        var alreadyLive = await _dbContext.UsbAccessRequests.AnyAsync(
            r => r.UsbDeviceId == usb.Id
                && r.Status == UsbAccessRequestStatus.Approved
                && r.ExpiresAt != null
                && r.ExpiresAt > now,
            cancellationToken);

        if (alreadyLive)
        {
            // Not an error to paper over: two overlapping grants would make
            // "when does access end" ambiguous, and silently extending one would
            // turn a time-boxed grant into a renewable one.
            return (UsbGrantOutcome.AlreadyGranted, null);
        }

        var request = UsbAccessRequest.GrantByAdministrator(
            organizationId, deviceId, usb.Id, usb.InstanceId,
            policy, justification, actorId, actorDisplay, duration, now);

        _dbContext.UsbAccessRequests.Add(request);
        usb.Grant(policy, request.ExpiresAt!.Value, now);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: policy == UsbStoragePolicy.Enabled
                ? "usb.access.enable"
                : "usb.access.grant",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("usb_device", usb.Id.ToString(), Describe(usb))
                .Requiring(Permissions.Usb.Manage)
                .WithStateChange(
                    RestrictedState,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        policy = policy.ToString(),
                        instanceId = usb.InstanceId,
                        deviceClass = usb.DeviceClass.ToString(),
                        expiresAt = request.ExpiresAt,
                        justification,
                    })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Policy} USB access granted on {DeviceId} for {InstanceId} ({Class}) until {ExpiresAt} by {Actor}.",
            policy, deviceId, usb.InstanceId, usb.DeviceClass, request.ExpiresAt, actorDisplay);

        await PushPolicyAsync(organizationId, deviceId, actorId, actorDisplay, now, cancellationToken);

        return (UsbGrantOutcome.Success, request);
    }

    /// <summary>Ends a live grant immediately and pushes the narrowed policy.</summary>
    public async Task<UsbRevokeOutcome> RevokeAsync(
        Guid organizationId,
        Guid requestId,
        string? note,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.UsbAccessRequests.SingleOrDefaultAsync(
            r => r.Id == requestId && r.OrganizationId == organizationId, cancellationToken);

        if (request is null)
        {
            return UsbRevokeOutcome.NotFound;
        }

        var now = _timeProvider.GetUtcNow();

        if (!request.TryRevoke(actorId, actorDisplay, note, now))
        {
            return UsbRevokeOutcome.NotLive;
        }

        var usb = await _dbContext.UsbDevices.SingleOrDefaultAsync(
            u => u.Id == request.UsbDeviceId, cancellationToken);

        usb?.Restrict();

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "usb.access.revoke",
            AuditResult.Success,
            audit => audit
                .OnDevice(request.DeviceId, request.DeviceId.ToString())
                .OnTarget("usb_access_request", request.Id.ToString(), request.InstanceId)
                .Requiring(Permissions.Usb.Manage)
                .WithStateChange(GrantedState(request.GrantedPolicy), RestrictedState));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "USB access revoked on {DeviceId} for {InstanceId} by {Actor}.",
            request.DeviceId, request.InstanceId, actorDisplay);

        await PushPolicyAsync(organizationId, request.DeviceId, actorId, actorDisplay, now, cancellationToken);

        return UsbRevokeOutcome.Success;
    }

    /// <summary>
    /// Marks lapsed grants Expired and restricts their devices. Returns how many.
    /// </summary>
    /// <remarks>
    /// This sweep is bookkeeping, not enforcement. Access has already stopped by
    /// the time it runs: the agent restricts the device against its own clock,
    /// and <see cref="BuildPolicyAsync"/> stops publishing the grant the instant
    /// it lapses. If this method never ran again, no endpoint would keep access
    /// past its deadline — only the console would show a stale status.
    /// </remarks>
    public async Task<int> SweepExpiredGrantsAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var lapsed = await _dbContext.UsbAccessRequests
            .Where(r => r.Status == UsbAccessRequestStatus.Approved
                && r.ExpiresAt != null
                && r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (lapsed.Count == 0)
        {
            return 0;
        }

        var usbIds = lapsed.Select(r => r.UsbDeviceId).ToList();
        var usbDevices = await _dbContext.UsbDevices
            .Where(u => usbIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);

        var expired = 0;

        foreach (var request in lapsed)
        {
            if (!request.TryExpire(now))
            {
                continue;
            }

            expired++;

            if (usbDevices.TryGetValue(request.UsbDeviceId, out var usb))
            {
                usb.Restrict();
            }

            _auditWriter.Stage(
                request.OrganizationId,
                AuditActorType.System,
                actorId: null,
                actorDisplay: "expiry sweeper",
                action: "usb.access.expire",
                AuditResult.Success,
                audit => audit
                    .OnDevice(request.DeviceId, request.DeviceId.ToString())
                    .OnTarget("usb_access_request", request.Id.ToString(), request.InstanceId)
                    .WithStateChange(GrantedState(request.GrantedPolicy), RestrictedState));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (expired > 0)
        {
            _logger.LogInformation("USB grant sweep expired {Count} grant(s).", expired);
        }

        return expired;
    }

    /// <summary>
    /// Queues an <c>ApplyUsbPolicy</c> task carrying the endpoint's complete
    /// current policy.
    /// </summary>
    /// <remarks>
    /// Whole state, so this is safe to call at any time and any number of times.
    /// A failure to queue is logged rather than thrown: the grant itself has
    /// already committed, and the endpoint converges on its next USB report
    /// regardless. Losing the push delays access; it never widens it.
    /// </remarks>
    public async Task<DeviceTask?> QueuePolicyPushAsync(
        Guid organizationId,
        Guid deviceId,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var policy = await BuildPolicyAsync(deviceId, now, cancellationToken);

        var payload = new TaskPayloads.ApplyUsbPolicy(
            policy.Grants
                .Select(g => new TaskPayloads.UsbGrant(
                    g.InstanceId,
                    string.Equals(g.Policy, nameof(UsbStoragePolicy.Enabled), StringComparison.OrdinalIgnoreCase)
                        ? TaskPayloads.UsbGrantPolicy.Enabled
                        : TaskPayloads.UsbGrantPolicy.ReadOnly,
                    g.ExpiresAt))
                .ToList(),
            now);

        return await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.ApplyUsbPolicy, payload,
            actorId, actorDisplay, cancellationToken);
    }

    /// <summary>
    /// Best-effort push after a grant or revocation has already committed.
    /// </summary>
    /// <remarks>
    /// Swallows failures on purpose. The decision is durable by the time this
    /// runs, and the endpoint converges on its next USB report, so a failed push
    /// delays access or delays a revocation reaching the machine — but note the
    /// asymmetry that makes this safe: a revocation the agent never receives
    /// still takes effect, because the agent expires the grant against its own
    /// clock. Throwing here would roll back a decision that is already recorded.
    /// </remarks>
    private async Task PushPolicyAsync(
        Guid organizationId,
        Guid deviceId,
        Guid actorId,
        string actorDisplay,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await QueuePolicyPushAsync(organizationId, deviceId, actorId, actorDisplay, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Could not queue ApplyUsbPolicy for device {DeviceId}. The endpoint will converge on its "
                + "next USB report; access is not widened by this failure.",
                deviceId);
        }
    }

    private void RecordEnforcement(UsbDevice usb, UsbDeviceReport entry, DateTimeOffset now)
    {
        var enforced = entry.EnforcedPolicy switch
        {
            null or "" => (UsbStoragePolicy?)null,
            var s when string.Equals(s, nameof(UsbStoragePolicy.Restricted), StringComparison.OrdinalIgnoreCase)
                => UsbStoragePolicy.Restricted,
            var s when string.Equals(s, nameof(UsbStoragePolicy.ReadOnly), StringComparison.OrdinalIgnoreCase)
                => UsbStoragePolicy.ReadOnly,
            var s when string.Equals(s, nameof(UsbStoragePolicy.Enabled), StringComparison.OrdinalIgnoreCase)
                => UsbStoragePolicy.Enabled,
            _ => null,
        };

        usb.ReportEnforcement(enforced, Truncate(entry.EnforcementError, 512), now, ParseStatus(entry.EnforcementStatus));
    }

    /// <summary>
    /// Maps the wire status onto the enum by exact name. Anything else — an
    /// older agent sending nothing, or a value this server does not know — is
    /// null, which the read side renders as applied-but-unverified. A status
    /// can never be promoted to <see cref="UsbEnforcementStatus.Verified"/> by
    /// being unrecognised.
    /// </summary>
    private static UsbEnforcementStatus? ParseStatus(string? value) => value switch
    {
        var s when string.Equals(s, nameof(UsbEnforcementStatus.Verified), StringComparison.OrdinalIgnoreCase)
            => UsbEnforcementStatus.Verified,
        var s when string.Equals(s, nameof(UsbEnforcementStatus.Unverified), StringComparison.OrdinalIgnoreCase)
            => UsbEnforcementStatus.Unverified,
        var s when string.Equals(s, nameof(UsbEnforcementStatus.RequiresRestart), StringComparison.OrdinalIgnoreCase)
            => UsbEnforcementStatus.RequiresRestart,
        var s when string.Equals(s, nameof(UsbEnforcementStatus.Failed), StringComparison.OrdinalIgnoreCase)
            => UsbEnforcementStatus.Failed,
        _ => null,
    };

    private static string Describe(UsbDevice usb) =>
        usb.Product is { Length: > 0 } product
            ? $"{product} ({usb.InstanceId})"
            : usb.InstanceId;

    /// <summary>
    /// Maps the wire class name onto the enum, defaulting to
    /// <see cref="UsbDeviceClass.Unknown"/> for anything unrecognised.
    /// </summary>
    /// <remarks>
    /// Unknown is safe here: only storage and portable devices can be granted
    /// access, so a class the server does not recognise can never be mistaken
    /// for something grantable. A future agent reporting a new class degrades to
    /// "shown but not grantable" rather than to an error. Matched by name only:
    /// a bare number on the wire is not a class.
    /// </remarks>
    private static UsbDeviceClass ParseClass(string? value) =>
        value is { Length: > 0 }
            && !char.IsDigit(value[0])
            && Enum.TryParse<UsbDeviceClass>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed
                : UsbDeviceClass.Unknown;

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
