using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// How long a device's inventory may age before the platform asks for a fresh
/// one on its own, without an administrator pressing Refresh.
/// </summary>
/// <remarks>
/// <para>
/// The agent uploads inventory only when the server asks; it keeps no schedule of
/// its own. Without this sweep a machine's software, Chrome and local-account
/// facts would be exactly as old as the last manual refresh, and a console that
/// looks current but is weeks stale is worse than one that admits it. Daily is
/// the default because inventory is bulky and changes slowly: once a day keeps
/// the picture honest without making every endpoint re-enumerate its disk each
/// hour.
/// </para>
/// <para>
/// Zero disables the automatic refresh; the manual Refresh button still works.
/// The ceiling of 720 hours (30 days) exists so a typo cannot quietly turn
/// "daily" into "never".
/// </para>
/// </remarks>
public sealed class InventoryRefreshOptions
{
    public const string SectionName = "Inventory";

    /// <summary>
    /// Hours after a device's last inventory upload before the sweep asks it for
    /// a new one. 0 disables the sweep.
    /// </summary>
    [Range(0, 720)]
    public int RefreshAfterHours { get; init; } = 24;
}
