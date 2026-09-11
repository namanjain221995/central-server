namespace EndpointPlatform.Domain.Tasks;

/// <summary>
/// The closed set of task types the platform can queue for an endpoint.
/// </summary>
/// <remarks>
/// <para>
/// This enum is the whole point of the task system: there is no
/// "run arbitrary command" member and never will be (spec rule 17, ADR-0010).
/// Every type maps to a specific, reviewed agent-side executor with a typed,
/// validated payload. Adding remote capability means adding a member here plus
/// its executor plus its tests - a deliberate, reviewable act.
/// </para>
/// <para>
/// Stored as text in PostgreSQL so reordering can never reinterpret history.
/// </para>
/// </remarks>
public enum DeviceTaskType
{
    /// <summary>Benign no-op used to prove the task pipeline end to end. Executor just echoes.</summary>
    Ping = 0,

    /// <summary>Ask the agent to collect and upload a fresh inventory now.</summary>
    RefreshInventory = 1,

    /// <summary>Restart the machine (with a grace period).</summary>
    RestartDevice = 2,

    /// <summary>Shut the machine down (with a grace period).</summary>
    ShutdownDevice = 3,

    /// <summary>Lock the interactive session.</summary>
    LockDevice = 4,

    /// <summary>Sign out the interactive user.</summary>
    SignOutUser = 5,

    /// <summary>Start/stop/restart a named Windows service (Phase 9).</summary>
    ControlService = 20,

    /// <summary>Terminate a process by PID with an expected-image guard (Phase 9).</summary>
    TerminateProcess = 21,

    /// <summary>
    /// Apply the complete USB storage access policy for this device (Milestone 11).
    /// </summary>
    /// <remarks>
    /// Deliberately declarative and whole-state rather than one task per
    /// grant/revoke. The payload carries every live grant the device should
    /// honour; anything absent from it is restricted. That makes the task
    /// idempotent, makes revocation the absence of an entry rather than a second
    /// message that could be lost, and lets a re-send repair drift without the
    /// server having to reason about what the endpoint currently believes.
    /// </remarks>
    ApplyUsbPolicy = 22,

    /// <summary>
    /// Stop a named installed application, resolving its processes on the endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="TerminateProcess"/>, which names one process id the
    /// console already knows. This names an <em>application</em> and leaves the
    /// resolution to the agent, which enumerates processes as they are at the
    /// moment of execution.
    /// </para>
    /// <para>
    /// That separation exists because the console's process list is a snapshot,
    /// and inventory is collected on request rather than continuously -- one
    /// measured on this fleet was ninety minutes old. Resolving a pid server-side
    /// from a snapshot that old means naming a process that has very likely
    /// exited, restarted under a new pid, or had its pid reused. The endpoint is
    /// the only place where "what is running" and "terminate it" can happen
    /// without a gap in between.
    /// </para>
    /// </remarks>
    StopApplication = 23,

    // 24 was RemoveApplication, withdrawn with the application-uninstall feature.
    // Not reused: the number appears in historic device_tasks rows and in audit
    // entries copied from them, and giving it to a different task would make
    // those records read as something they never were.

    /// <summary>Install an approved, hash-verified package (Phase 11).</summary>
    InstallPackage = 30,

    /// <summary>
    /// Update the agent itself to an approved published release (Milestone 10).
    /// Deliberately NOT an InstallPackage: the package path installs in-process,
    /// and an in-process install of the agent's own MSI would be killed when the
    /// upgrade stops the very service running it. This type routes to an
    /// executor that verifies everything first and then hands the install to
    /// Windows in a way that survives the agent's own shutdown.
    /// </summary>
    UpdateAgent = 31,

    // Local Windows account management (Phase 4 write side). Each acts on a real
    // local user/group through account-management APIs on the agent - never a shell
    // (ADR-0005). Targets are identified by SID (names are renameable).

    /// <summary>Create a local Windows user. Password delivered out-of-band by secret reference.</summary>
    CreateLocalUser = 40,

    /// <summary>Delete a local Windows user (by SID).</summary>
    DeleteLocalUser = 41,

    /// <summary>Enable a local Windows user (clear the account-disabled flag).</summary>
    EnableLocalUser = 42,

    /// <summary>Disable a local Windows user (set the account-disabled flag).</summary>
    DisableLocalUser = 43,

    /// <summary>Reset a local user's password. Secret delivered out-of-band by reference.</summary>
    ResetLocalUserPassword = 44,

    /// <summary>Force a local user to change their password at next logon.</summary>
    ForceLocalUserPasswordChange = 45,

    /// <summary>Promote/demote a local user by adding/removing BUILTIN\Administrators membership.</summary>
    ChangeLocalUserType = 46,

    /// <summary>Add a local user (by SID) to a local group (by SID).</summary>
    AddLocalUserToGroup = 47,

    /// <summary>Remove a local user (by SID) from a local group (by SID).</summary>
    RemoveLocalUserFromGroup = 48,

    /// <summary>
    /// The endpoint's complete set of live administrator elevations.
    /// </summary>
    /// <remarks>
    /// Whole state, like <see cref="ApplyUsbPolicy"/>: the payload carries every
    /// elevation that is currently authorized, and an account absent from it must
    /// not remain elevated. There is deliberately no "remove elevation" task --
    /// revocation is the absence of an entry rather than a second message that
    /// could be lost while an endpoint is offline.
    /// </remarks>
    ApplyLocalAdminElevation = 49,

    /// <summary>
    /// Install an approved, hash- and signature-verified driver package
    /// (Milestone 13).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT an <see cref="InstallPackage"/>. That path installs a single
    /// MSI through the Windows Installer service; a driver is an archive of files
    /// installed into the driver store through a different API, and it lands in the
    /// kernel rather than in Program Files. Sharing the executor would mean one code
    /// path deciding at runtime which set of gates applies, and the more dangerous of
    /// the two would be the one chosen by a mistake.
    /// </remarks>
    InstallDriverPackage = 50,
}
