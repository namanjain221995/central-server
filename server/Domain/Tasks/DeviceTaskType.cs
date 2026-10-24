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

    /// <summary>
    /// Abort the restart Windows is counting down on the device
    /// (<c>AbortSystemShutdown</c>). The undo of <see cref="RestartDevice"/>,
    /// for a restart that has already been delivered and accepted.
    /// </summary>
    /// <remarks>
    /// Queued by Restart Management when an administrator cancels after the
    /// countdown has started. The payload names the restart task it undoes; on
    /// success the server marks that restart Cancelled. Needs an agent with the
    /// executor (1.14.0), so the catalogue gates it.
    /// </remarks>
    CancelRestart = 6,

    /// <summary>
    /// Arm a restart for a later moment on the device itself, so it happens at
    /// that moment with or without the network (agent 1.15.0+).
    /// </summary>
    /// <remarks>
    /// Sent by Restart Management as soon as a department restart is scheduled.
    /// The device stores it and, the warning's length before the moment, hands
    /// Windows the countdown exactly as a <see cref="RestartDevice"/> would. Its
    /// result is "armed"; whether the machine restarted is told by the boot time
    /// it reports afterwards. Undone by <see cref="CancelRestart"/>.
    /// </remarks>
    ScheduleRestart = 7,

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

    /// <summary>
    /// Show a short administrator-written announcement on the device's screens
    /// (agent 1.18.0+).
    /// </summary>
    /// <remarks>
    /// Plain text only, server-validated to tight length limits: the payload is a
    /// message to read, never a thing to execute or render as markup. Queued per
    /// target device by the Announcements feature with a day-long lifetime, so a
    /// machine that is offline right now still shows the notice when it next
    /// checks in -- after a day the news is stale and the task expires instead.
    /// </remarks>
    ShowAnnouncement = 51,

    /// <summary>
    /// Put the device behind a persistent full-screen lock until an administrator
    /// releases it or the on-device IT recovery code is entered (agent 1.19.0+).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different thing entirely from <see cref="LockDevice"/>, which merely
    /// locks the interactive session the way Win+L does and is dismissed by the
    /// user's own password. This lock takes over every screen and stays until it
    /// is deliberately lifted, so it carries the message to show and a PBKDF2
    /// verifier of the organization's IT unlock code -- never the code itself, so
    /// a captured task cannot reveal it. Queued per target device with a week of
    /// lifetime, so a machine offline now is still locked when it reconnects
    /// within the week. Gated on the agent that has the lock executor: an older
    /// one would fail the task as unknown, which reads exactly like the lock
    /// failing to engage.
    /// </para>
    /// </remarks>
    EngageScreenLock = 52,

    /// <summary>
    /// Lift a persistent full-screen lock that <see cref="EngageScreenLock"/>
    /// engaged (agent 1.19.0+). The undo half of the lock/unlock pair.
    /// </summary>
    /// <remarks>
    /// Names the lock it releases in its payload, the way
    /// <see cref="CancelRestart"/> names the restart it aborts, so the agent lifts
    /// the right lock and both logs say the same thing. The same week of lifetime
    /// as the engage, so an unlock issued for an offline device still reaches it
    /// on reconnect rather than leaving it locked.
    /// </remarks>
    ReleaseScreenLock = 53,

    /// <summary>
    /// Configure the device's already-installed RustDesk for IT remote support by
    /// setting a platform-managed unattended password, so an administrator can start
    /// a remote-control session (agent 1.20.0+).
    /// </summary>
    /// <remarks>
    /// RustDesk itself is installed separately (via Company Apps); this task only
    /// sets the unattended access on an install that is already present. The password
    /// is delivered as a one-time ephemeral-secret reference and never appears in the
    /// persisted task payload or the audit trail. The device reports its RustDesk ID
    /// on the heartbeat, so no result payload is required.
    /// </remarks>
    ConfigureRemoteSupport = 54,

    /// <summary>
    /// Ask the device to query Windows Update for applicable driver updates and
    /// report the pending list, without installing anything (agent 1.21.0+).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-only on the endpoint: it runs an online Windows Update search for
    /// drivers and returns what it found -- update id, revision, title, class,
    /// provider, date, size and whether the update is a preview -- so the console
    /// can show which machines have driver updates waiting. Nothing is downloaded
    /// or installed. The result replaces the device's pending-driver-update list
    /// wholesale, the same way an inventory section does, so a scan that finds
    /// nothing clears the list rather than leaving stale rows.
    /// </para>
    /// <para>
    /// A short-to-moderate lifetime: a scan that an offline machine finally picks
    /// up hours later is still useful, but a query is cheap to re-issue, so it is
    /// not kept armed for days. Gated on the agent that carries the Windows Update
    /// driver executor; an older one would fail it as an unknown type.
    /// </para>
    /// </remarks>
    ScanDriverUpdates = 55,

    /// <summary>
    /// Install one or more named Windows Update driver updates the device
    /// previously reported as pending (agent 1.21.0+).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different thing from <see cref="InstallDriverPackage"/>, which sideloads an
    /// administrator-approved INF package this platform hosts. This installs updates
    /// that Windows Update itself offers, named by their Windows Update id, so the
    /// endpoint downloads and installs them through the Windows Update Agent exactly
    /// as Settings would. High risk, because a driver runs in the kernel. The result
    /// reports, per update, whether it succeeded and whether a reboot is now pending,
    /// so the console can hand a reboot-pending machine to Restart Management rather
    /// than restarting it blind.
    /// </para>
    /// <para>
    /// A maintenance lifetime: unlike an interactive action, a driver install that
    /// fires when a laptop reappears is still the right thing to do. Gated on the
    /// agent that carries the Windows Update driver executor; an older one would
    /// fail it as an unknown type, which would read exactly like the install failing.
    /// </para>
    /// </remarks>
    InstallDriverUpdate = 56,

    /// <summary>
    /// Open a live IT-support chat window on the device so the person at it can
    /// talk to an administrator (agent 1.22.0+).
    /// </summary>
    /// <remarks>
    /// Names the chat session it belongs to in its payload, together with the
    /// administrator's display name to show in the window. Carries no secret -- the
    /// messages themselves travel over the device-credentialed chat endpoints, not
    /// in the task payload. Gated on the agent that has the chat executor; an older
    /// one would fail the task as an unknown type, which reads exactly like the chat
    /// failing to open.
    /// </remarks>
    StartLiveChat = 57,

    /// <summary>
    /// Close the live IT-support chat window that <see cref="StartLiveChat"/>
    /// opened (agent 1.22.0+). The undo half of the start/end pair.
    /// </summary>
    /// <remarks>
    /// Names the session it closes, the way <see cref="ReleaseScreenLock"/> names
    /// the lock it lifts, so the agent closes exactly the window an administrator
    /// ended and both logs name the same session.
    /// </remarks>
    EndLiveChat = 58,

    /// <summary>
    /// Apply the complete Chrome extension policy for this device (agent 1.23.0+):
    /// which extensions are force-installed, blocked, allowed or removed, enforced
    /// through Chrome's own enterprise <c>ExtensionSettings</c> policy in HKLM.
    /// </summary>
    /// <remarks>
    /// Whole-state, like <see cref="ApplyUsbPolicy"/> and
    /// <see cref="ApplyLocalAdminElevation"/>: every rule the device has travels in
    /// every task, and an extension absent from the payload has NO rule -- the agent
    /// writes exactly the named rules and clears whatever else it had written. That
    /// makes the task idempotent, makes lifting a rule the absence of an entry
    /// rather than a second message that could be lost, and lets a re-send repair
    /// drift. An empty rule list is therefore valid and meaningful: it clears the
    /// platform's extension policy from the device.
    /// </remarks>
    ApplyChromeExtensionPolicy = 59,

    /// <summary>
    /// Ask the device to query Windows Update for applicable security and quality
    /// updates and report the pending list, without installing anything
    /// (agent 1.24.0+).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-only on the endpoint: it runs an online Windows Update search for
    /// software updates and returns what it found -- update id, revision, title,
    /// KB numbers, MSRC severity, size and whether the update is a preview -- so
    /// the console can show which machines have security updates waiting. Nothing
    /// is downloaded or installed. The result replaces the device's
    /// pending-security-update list wholesale, the same way an inventory section
    /// does, so a scan that finds nothing clears the list rather than leaving
    /// stale rows.
    /// </para>
    /// <para>
    /// A short-to-moderate lifetime: a scan that an offline machine finally picks
    /// up hours later is still useful, but a query is cheap to re-issue, so it is
    /// not kept armed for days. Gated on the agent that carries the Windows Update
    /// software executor; an older one would fail it as an unknown type.
    /// </para>
    /// </remarks>
    ScanSecurityUpdates = 60,

    /// <summary>
    /// Install one or more named Windows Update security/quality updates the device
    /// previously reported as pending (agent 1.24.0+).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This installs updates that Windows Update itself offers, named by their
    /// Windows Update id, so the endpoint downloads and installs them through the
    /// Windows Update Agent exactly as Settings would. High risk, because it
    /// changes the operating system itself. The result reports, per update, whether
    /// it succeeded and whether a reboot is now pending, so the console can hand a
    /// reboot-pending machine to Restart Management rather than restarting it blind.
    /// </para>
    /// <para>
    /// A long maintenance lifetime: unlike an interactive action, a security-update
    /// install that fires when a laptop reappears is still the right thing to do,
    /// and a cumulative update is a large download followed by a long install, so
    /// the task is given hours, not minutes. Same agent gate as the scan that
    /// produced the update ids.
    /// </para>
    /// </remarks>
    InstallSecurityUpdate = 61,

    /// <summary>
    /// Remove one named Chrome profile -- Chrome's profile-list entry and its
    /// directory -- from one Windows user's Chrome on the device (agent 1.26.0+).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Destructive and unrecoverable -- a profile directory holds bookmarks,
    /// history and sign-in state -- so the payload pins everything the console
    /// decided on: the Windows user by SID, the profile by its directory key and
    /// by the full path the console recorded, and the signed-in account it
    /// believed was there. The agent re-derives the path for itself and refuses
    /// any mismatch, refuses the "Default" profile outright, and refuses when the
    /// profile's signed-in account no longer matches the console's view (a stale
    /// inventory must not delete a profile that changed hands). A refusal is a
    /// failed task the operator can read, never a partial delete. Chrome being
    /// open is not a refusal since 1.26.0: the task completes as "scheduled" and
    /// the agent persists the removal, carrying it out when Chrome closes for
    /// that user or at the next boot.
    /// </para>
    /// <para>
    /// Gated on the agent that has the removal executor: an older one would fail
    /// the task as unknown, which reads exactly like the removal failing.
    /// </para>
    /// </remarks>
    RemoveChromeProfile = 62,
}
