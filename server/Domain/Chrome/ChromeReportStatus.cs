namespace EndpointPlatform.Domain.Chrome;

/// <summary>
/// Whether the endpoint could describe its Chrome installation at all.
/// </summary>
/// <remarks>
/// <para>
/// Carried on the installation row rather than inferred from the profile rows,
/// for the reason BitLocker carries its own availability: an empty profile list
/// could mean Chrome is not installed, or that the agent could read no profile
/// directory, and the two must never be confused. Note that
/// <see cref="NotInstalled"/> may still have profiles beneath it, because Chrome's
/// uninstaller leaves <c>User Data</c> behind by default.
/// </para>
/// <para>
/// Stored as text so reordering the enum can never reinterpret history. The names
/// are the wire contract's status strings exactly, so the Agent API maps them with
/// a case-sensitive parse and nothing else; a test pins the two sets together.
/// </para>
/// </remarks>
public enum ChromeReportStatus
{
    /// <summary>Chrome is installed and the agent read it.</summary>
    Available = 0,

    /// <summary>No installation was found. Profiles may still exist on disk.</summary>
    NotInstalled = 1,

    /// <summary>
    /// The agent's enumeration was incomplete. Whatever it did read is a partial
    /// snapshot and must not replace the last complete one.
    /// </summary>
    Error = 2,
}
