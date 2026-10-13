namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// Response body for <c>GET /agent/v1/catalog</c>: the Company Portal apps this
/// device may offer its user, each with its state on this device.
/// </summary>
/// <remarks>
/// Only what the portal needs to draw a tile and decide whether to show
/// "Install". Nothing about the package behind the tile -- no file name, no
/// hash, no product code -- crosses here. The device learns those the only way
/// it ever does: from the InstallPackage task the server queues after it has
/// decided the install is allowed.
/// </remarks>
public sealed record CatalogListResponse(IReadOnlyList<CatalogEntry> Apps);

/// <summary>One app on the portal.</summary>
/// <param name="Id">The catalog app id. The only thing an install request names.</param>
/// <param name="DisplayName">What the tile is called.</param>
/// <param name="Description">A line or two under the name, or null.</param>
/// <param name="Category">Grouping on the portal, or null.</param>
/// <param name="HasIcon">Whether <c>GET /agent/v1/catalog/{id}/icon</c> returns a PNG.</param>
/// <param name="Version">The version the install would put on the machine.</param>
/// <param name="Publisher">The package's publisher, or null.</param>
/// <param name="UpdatedAt">When the entry last changed; a cache key for the icon.</param>
/// <param name="State">
/// <c>Available</c>, <c>UpdateAvailable</c>, <c>Installing</c> or <c>Installed</c>,
/// as a string: an unrecognised value should be shown as unavailable rather than
/// mapped to whichever member sits at that ordinal.
/// </param>
public sealed record CatalogEntry(
    Guid Id,
    string DisplayName,
    string? Description,
    string? Category,
    bool HasIcon,
    string Version,
    string? Publisher,
    DateTimeOffset UpdatedAt,
    string State);

/// <summary>
/// Request body for <c>POST /agent/v1/catalog/{id}/install</c>.
/// </summary>
/// <param name="RequestedBy">
/// The signed-in Windows user, for the record. Informational only: the server
/// authorises on the device credential and the device's group, never on this.
/// </param>
public sealed record CatalogInstallCommand(string? RequestedBy);

/// <summary>
/// Response body for <c>POST /agent/v1/catalog/{id}/install</c>.
/// </summary>
/// <param name="Outcome">
/// <c>Queued</c>, <c>AlreadyInstalled</c>, <c>AlreadyInProgress</c> or <c>Refused</c>.
/// Refused covers every reason the device may not have the app; the portal shows
/// one message for all of them.
/// </param>
/// <param name="TaskId">The queued InstallPackage task, when the outcome is Queued.</param>
public sealed record CatalogInstallResponse(string Outcome, Guid? TaskId);
