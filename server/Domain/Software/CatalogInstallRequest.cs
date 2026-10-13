using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Software;

/// <summary>What the server decided when a user asked for an app.</summary>
public enum CatalogInstallOutcome
{
    /// <summary>An InstallPackage task was queued; progress is the task's.</summary>
    Queued = 0,

    /// <summary>The device already reports the app; nothing was sent.</summary>
    AlreadyInstalled = 1,

    /// <summary>An install of the same package was already on its way to the device.</summary>
    AlreadyInProgress = 2,

    /// <summary>
    /// The device may not have this app: unpublished, not visible to its group,
    /// package withdrawn, or an agent too old for the task. One value on purpose,
    /// so the portal cannot be used to probe what exists.
    /// </summary>
    Refused = 3,
}

/// <summary>
/// One request from the Company Portal: who asked for which app on which device,
/// and what came of it.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="SoftwareDeployment"/>, which records an
/// administrator's decision. A self-service request is the user's, and the
/// questions asked of it are different: "who asked for Zoom", "what did the
/// portal refuse". Refusals are recorded too -- a run of them from one machine
/// is worth noticing. Names are copied, not joined, so the row still reads the
/// same after the app is removed from the catalog.
/// </remarks>
public sealed class CatalogInstallRequest : AuditableEntity
{
    private CatalogInstallRequest()
    {
    }

    public CatalogInstallRequest(
        Guid organizationId,
        Guid deviceId,
        Guid catalogAppId,
        string appName,
        string? requestedBy,
        CatalogInstallOutcome outcome,
        Guid? taskId)
    {
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        DeviceId = Guard.NotEmpty(deviceId, nameof(deviceId));
        CatalogAppId = Guard.NotEmpty(catalogAppId, nameof(catalogAppId));
        AppName = Guard.NotNullOrWhiteSpace(appName, nameof(appName), maxLength: 128);
        RequestedBy = Guard.OptionalMaxLength(requestedBy, 256, nameof(requestedBy));
        Outcome = outcome;
        TaskId = taskId;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>Not a foreign key: the row outlives the catalog entry.</summary>
    public Guid CatalogAppId { get; private set; }

    public string AppName { get; private set; } = null!;

    /// <summary>
    /// The signed-in Windows user as the agent reports it. Informational: the
    /// device credential is what authenticated the request, and the device's
    /// group is what authorized it. A user name the portal could invent decides
    /// nothing.
    /// </summary>
    public string? RequestedBy { get; private set; }

    public CatalogInstallOutcome Outcome { get; private set; }

    public Guid? TaskId { get; private set; }
}
