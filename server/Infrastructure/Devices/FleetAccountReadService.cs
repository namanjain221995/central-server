using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Reads the fleet-wide local-account summary: which Windows local account exists on
/// which devices, so an operator can see what is common across the fleet before
/// acting on it with a bulk create, reset or lifecycle operation.
/// </summary>
/// <remarks>
/// <para>
/// Read-only over the same inventory the per-device local-user list serves
/// (<see cref="DeviceLocalUser"/>); nothing here reaches a device. Only ACTIVE
/// devices count — a retired machine's last inventory must not keep an account
/// "existing" on the overview.
/// </para>
/// <para>
/// The read is scoped exactly as the driver-update overview is: a scoped
/// administrator is handed the device ids they may see (null means "all"), and both
/// the account rows and the active-device total are computed over those devices
/// only, so the page can never name a machine outside the caller's scope.
/// </para>
/// <para>
/// Windows local usernames are case-insensitive, so accounts are grouped by
/// lower-cased name — the same match a bulk reset or delete uses to resolve a
/// username per device — while the most common original casing is kept for display.
/// </para>
/// </remarks>
public sealed class FleetAccountReadService(
    EndpointPlatformDbContext dbContext,
    ILogger<FleetAccountReadService> logger)
{
    /// <summary>At most this many distinct account names are returned.</summary>
    private const int MaxAccounts = 200;

    /// <summary>At most this many device rows are returned per account.</summary>
    private const int MaxDeviceRowsPerAccount = 500;

    /// <summary>
    /// Well-known Windows built-in accounts, flagged so the console can set them
    /// apart from accounts an operator created. Presence on every machine is
    /// expected for these; it is signal for everything else.
    /// </summary>
    private static readonly HashSet<string> BuiltInAccountNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator",
        "Guest",
        "DefaultAccount",
        "WDAGUtilityAccount",
    };

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ILogger<FleetAccountReadService> _logger = logger;

    /// <summary>
    /// Every local account reported by an active, in-scope device in the
    /// organization, grouped case-insensitively by name: on how many devices it
    /// exists, on how many it is a local administrator, on how many it is enabled,
    /// and the per-device rows — most widespread account first.
    /// </summary>
    public async Task<FleetAccountSummary> GetSummaryAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var visible = scopedDeviceIds?.ToList();

        var devicesQuery = _dbContext.Devices.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Status == DeviceStatus.Active);
        if (visible is not null)
        {
            devicesQuery = devicesQuery.Where(d => visible.Contains(d.Id));
        }

        // The denominator the console shows "on 3 of 12 devices" against. Counted
        // over the same scope as the rows, so the fraction can never exceed one.
        var activeDevices = await devicesQuery.CountAsync(cancellationToken);

        var rowsQuery =
            from u in _dbContext.DeviceLocalUsers.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on u.DeviceId equals d.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            select new { u.DeviceId, d.Hostname, u.Name, u.Enabled, u.IsLocalAdministrator };

        if (visible is not null)
        {
            rowsQuery = rowsQuery.Where(x => visible.Contains(x.DeviceId));
        }

        var rows = await rowsQuery.ToListAsync(cancellationToken);

        var truncated = false;
        var accounts = new List<FleetAccountRow>();

        foreach (var group in rows.GroupBy(r => r.Name.ToLowerInvariant()))
        {
            // Display the most common original casing; ties break deterministically.
            var displayName = group
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .OrderByDescending(c => c.Count())
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .First().Key;

            // One row per device even if an inventory ever reported two SIDs under
            // the same name on one machine; counts are device counts throughout, so
            // adminOn and enabledOn can never exceed onDevices.
            var devices = group
                .GroupBy(r => r.DeviceId)
                .Select(byDevice => new FleetAccountDeviceRow(
                    byDevice.Key,
                    byDevice.First().Hostname,
                    IsAdmin: byDevice.Any(r => r.IsLocalAdministrator),
                    Enabled: byDevice.Any(r => r.Enabled)))
                .OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.DeviceId)
                .ToList();

            var onDevices = devices.Count;
            var adminOn = devices.Count(d => d.IsAdmin);
            var enabledOn = devices.Count(d => d.Enabled);

            if (devices.Count > MaxDeviceRowsPerAccount)
            {
                // The counts above stay true to the whole fleet; only the listed
                // rows are capped, and the response says so.
                truncated = true;
                devices = devices.Take(MaxDeviceRowsPerAccount).ToList();
            }

            accounts.Add(new FleetAccountRow(
                displayName,
                IsBuiltIn: BuiltInAccountNames.Contains(displayName),
                onDevices,
                adminOn,
                enabledOn,
                devices));
        }

        accounts = accounts
            .OrderByDescending(a => a.OnDevices)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (accounts.Count > MaxAccounts)
        {
            truncated = true;
            accounts = accounts.Take(MaxAccounts).ToList();
        }

        if (truncated)
        {
            _logger.LogWarning(
                "Fleet local-account summary for organization {OrganizationId} was truncated "
                + "(caps: {MaxAccounts} accounts, {MaxDeviceRows} device rows per account).",
                organizationId, MaxAccounts, MaxDeviceRowsPerAccount);
        }

        return new FleetAccountSummary(activeDevices, truncated, accounts);
    }
}

/// <summary>The fleet-wide local-account overview.</summary>
/// <param name="ActiveDevices">Active devices in the caller's scope — the denominator for <c>OnDevices</c>.</param>
/// <param name="Truncated">True when the account or per-account device caps cut the listing short.</param>
public sealed record FleetAccountSummary(
    int ActiveDevices,
    bool Truncated,
    IReadOnlyList<FleetAccountRow> Accounts);

/// <summary>One local account name as seen across the fleet.</summary>
/// <param name="Name">The most common original casing of the (case-insensitive) account name.</param>
/// <param name="IsBuiltIn">A well-known Windows built-in account (Administrator, Guest, …).</param>
/// <param name="OnDevices">Distinct active devices that last reported the account.</param>
/// <param name="AdminOn">Of those, how many report it as a local administrator.</param>
/// <param name="EnabledOn">Of those, how many report it enabled.</param>
public sealed record FleetAccountRow(
    string Name,
    bool IsBuiltIn,
    int OnDevices,
    int AdminOn,
    int EnabledOn,
    IReadOnlyList<FleetAccountDeviceRow> Devices);

/// <summary>The account's state on one device, per that device's last inventory.</summary>
public sealed record FleetAccountDeviceRow(
    Guid DeviceId,
    string Hostname,
    bool IsAdmin,
    bool Enabled);
