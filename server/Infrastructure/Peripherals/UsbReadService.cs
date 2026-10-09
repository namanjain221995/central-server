using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Peripherals;

/// <param name="IsRestrictable">
/// True when access policy applies: storage or a portable device (phone,
/// tablet, camera). Everything else is inventory.
/// </param>
/// <param name="SupportsReadOnly">
/// True when a read-only grant can be enforced. Storage only; a phone is
/// restricted or enabled, nothing between.
/// </param>
/// <param name="Policy">
/// What the console has decided: <c>Restricted</c>, <c>ReadOnly</c> or
/// <c>Enabled</c>.
/// </param>
/// <param name="EnforcementState">
/// What the endpoint is actually doing about it, as one of <c>Enforced</c>
/// (applied and verified against Windows), <c>Applied</c> (the agent reported
/// success but could not, or does not, verify it), <c>Pending</c>,
/// <c>Drifted</c>, <c>RequiresRestart</c>, <c>Failed</c> or
/// <c>NotApplicable</c>. Kept distinct from <paramref name="Policy"/> so the UI
/// cannot imply a control that is not in place — an offline machine shows
/// Pending, not Enforced.
/// </param>
/// <param name="SerialNumber">Null when the device does not expose one. Never fabricated.</param>
public sealed record UsbDeviceView(
    Guid Id,
    string InstanceId,
    string DeviceClass,
    bool IsStorage,
    bool IsPortableDevice,
    bool IsRestrictable,
    bool SupportsReadOnly,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    string? Manufacturer,
    string? Product,
    bool IsConnected,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DisconnectedAt,
    string Policy,
    DateTimeOffset? PolicyExpiresAt,
    string EnforcementState,
    DateTimeOffset? EnforcedAt,
    string? EnforcementError,
    Guid? LiveRequestId);

public sealed record UsbAccessRequestView(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    Guid UsbDeviceId,
    string InstanceId,
    string? Product,
    string Status,
    string Source,

    /// <summary>The access level granted: <c>ReadOnly</c> or <c>Enabled</c>.</summary>
    string GrantedPolicy,

    string Justification,
    DateTimeOffset RequestedAt,
    string? DecidedByDisplay,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ExpiresAt,
    string? DecisionNote,
    bool IsLive);

/// <summary>
/// Read-side projections for the peripheral console. Query-only: nothing here
/// changes policy, so a view can never be the thing that grants access.
/// </summary>
public sealed class UsbReadService(EndpointPlatformDbContext dbContext, TimeProvider timeProvider)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Every USB device an endpoint has reported, connected first.</summary>
    public async Task<IReadOnlyList<UsbDeviceView>> ListForDeviceAsync(
        Guid organizationId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var devices = await _dbContext.UsbDevices
            .AsNoTracking()
            .Where(u => u.DeviceId == deviceId && u.OrganizationId == organizationId)
            .OrderByDescending(u => u.IsConnected)
            .ThenBy(u => u.DeviceClass)
            .ThenByDescending(u => u.LastSeenAt)
            .ToListAsync(cancellationToken);

        var liveByUsbId = await _dbContext.UsbAccessRequests
            .AsNoTracking()
            .Where(r => r.DeviceId == deviceId
                && r.Status == UsbAccessRequestStatus.Approved
                && r.ExpiresAt != null
                && r.ExpiresAt > now)
            .Select(r => new { r.UsbDeviceId, r.Id })
            .ToDictionaryAsync(r => r.UsbDeviceId, r => r.Id, cancellationToken);

        return devices.Select(u => new UsbDeviceView(
            u.Id,
            u.InstanceId,
            u.DeviceClass.ToString(),
            u.IsStorage,
            u.IsPortableDevice,
            u.IsRestrictable,
            u.SupportsReadOnly,
            u.VendorId,
            u.ProductId,
            u.SerialNumber,
            u.Manufacturer,
            u.Product,
            u.IsConnected,
            u.FirstSeenAt,
            u.LastSeenAt,
            u.DisconnectedAt,
            u.Policy.ToString(),
            u.PolicyExpiresAt,
            DescribeEnforcement(u),
            u.EnforcedAt,
            u.EnforcementError,
            liveByUsbId.TryGetValue(u.Id, out var requestId) ? requestId : null))
            .ToList();
    }

    /// <summary>
    /// The fleet-wide access ledger: live grants first, then recent history.
    /// </summary>
    public async Task<IReadOnlyList<UsbAccessRequestView>> ListRequestsAsync(
        Guid organizationId,
        bool liveOnly,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var query =
            from r in _dbContext.UsbAccessRequests.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on r.DeviceId equals d.Id
            where r.OrganizationId == organizationId
            select new { Request = r, d.Hostname, d.DisplayName };

        if (liveOnly)
        {
            query = query.Where(x =>
                x.Request.Status == UsbAccessRequestStatus.Approved
                && x.Request.ExpiresAt != null
                && x.Request.ExpiresAt > now);
        }

        var rows = await query
            .OrderByDescending(x => x.Request.RequestedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        var usbIds = rows.Select(x => x.Request.UsbDeviceId).Distinct().ToList();
        var products = await _dbContext.UsbDevices
            .AsNoTracking()
            .Where(u => usbIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Product })
            .ToDictionaryAsync(u => u.Id, u => u.Product, cancellationToken);

        return rows.Select(x => new UsbAccessRequestView(
            x.Request.Id,
            x.Request.DeviceId,
            x.DisplayName ?? x.Hostname,
            x.Request.UsbDeviceId,
            x.Request.InstanceId,
            products.TryGetValue(x.Request.UsbDeviceId, out var product) ? product : null,
            x.Request.Status.ToString(),
            x.Request.Source.ToString(),
            x.Request.GrantedPolicy.ToString(),
            x.Request.Justification,
            x.Request.RequestedAt,
            x.Request.DecidedByDisplay,
            x.Request.DecidedAt,
            x.Request.ExpiresAt,
            x.Request.DecisionNote,
            x.Request.IsLive(now)))
            .ToList();
    }

    /// <summary>
    /// Turns the decided/reported pair into one word an operator can act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinctions that matter: <c>Pending</c> means the endpoint has not
    /// told us anything yet — it may be offline, or the policy may still be in
    /// flight. <c>Drifted</c> means it told us it is enforcing something other
    /// than what was asked, which on a Windows box usually means a local
    /// administrator re-enabled the device by hand. <c>RequiresRestart</c>
    /// means Windows accepted the change for the next boot, so the control is
    /// not in place yet and a restart is what fixes it. Collapsing any of these
    /// into "not enforced" would hide the one that needs investigating.
    /// </para>
    /// <para>
    /// <c>Enforced</c> is reserved for a state the endpoint has verified against
    /// Windows. An agent that reported success without verifying — every agent
    /// before 1.16.0, or one whose read-back failed — gets <c>Applied</c>, so
    /// the console never calls a device protected on the strength of a call
    /// having returned.
    /// </para>
    /// </remarks>
    internal static string DescribeEnforcement(UsbDevice usb)
    {
        if (!usb.IsRestrictable)
        {
            return "NotApplicable";
        }

        if (usb.EnforcementStatus == UsbEnforcementStatus.RequiresRestart)
        {
            return "RequiresRestart";
        }

        if (usb.HasEnforcementFailure)
        {
            return "Failed";
        }

        if (usb.EnforcedPolicy is null)
        {
            return "Pending";
        }

        if (usb.EnforcedPolicy != usb.Policy)
        {
            return "Drifted";
        }

        return usb.EnforcementStatus == UsbEnforcementStatus.Verified ? "Enforced" : "Applied";
    }
}
