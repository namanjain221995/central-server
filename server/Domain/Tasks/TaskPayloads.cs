namespace EndpointPlatform.Domain.Tasks;

/// <summary>
/// Typed payloads for task types that carry parameters. Serialised to the task's
/// <c>PayloadJson</c>. Payload-free types (Ping, Lock, RefreshInventory) have none.
/// </summary>
public static class TaskPayloads
{
    /// <param name="GraceSeconds">Delay before the action, giving the user warning time.</param>
    /// <param name="Message">Optional message shown to the interactive user.</param>
    public sealed record RestartOrShutdown(int GraceSeconds, string? Message);

    /// <summary>
    /// Abort the countdown that <paramref name="RestartTaskId"/> started. The id
    /// is for the record -- Windows has at most one pending shutdown, and the
    /// agent aborts whatever it is -- and <paramref name="RequestedBy"/> is who
    /// asked, so the agent's log and the task row say the same thing.
    /// </summary>
    public sealed record CancelRestart(Guid RestartTaskId, string RequestedBy);

    /// <summary>
    /// A restart the device arms and carries out itself. <paramref name="RestartAt"/>
    /// is server time; the agent measures it against the server time it was
    /// handed the task at, so its own clock does not matter.
    /// </summary>
    public sealed record ScheduleRestart(DateTimeOffset RestartAt, int WarningSeconds, string Message);

    /// <remarks>
    /// Serialised BY NAME, never by number. The agent's executor reads the wire
    /// value as a string ("Start"/"Stop"/"Restart") and matches it exactly; the
    /// default Web serializer writes enums as integers, which the agent rejects
    /// as a malformed payload — found live against a real endpoint, not in
    /// review. The converter rides on the type so no call site can forget it.
    /// </remarks>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum ServiceAction
    {
        Start = 0,
        Stop = 1,
        Restart = 2,
    }

    /// <param name="ServiceName">Windows service short name (validated against a safe pattern).</param>
    public sealed record ControlService(string ServiceName, ServiceAction Action);

    /// <summary>
    /// The approved release an agent should update itself to.
    /// </summary>
    /// <remarks>
    /// Advisory, not authoritative: the agent re-fetches the release metadata
    /// over its own authenticated channel and refuses when this payload and the
    /// server disagree, so a payload alone can never choose what gets installed.
    /// </remarks>
    /// <param name="ReleaseId">The published release row.</param>
    /// <param name="Version">Expected version, cross-checked by the agent.</param>
    /// <param name="Sha256">Expected content hash, cross-checked by the agent.</param>
    public sealed record UpdateAgent(Guid ReleaseId, string Version, string Sha256);

    // ---- USB storage access (Milestone 11) ----

    /// <summary>
    /// One live grant: this exact hardware may be read until this instant.
    /// </summary>
    /// <param name="InstanceId">
    /// Windows device instance ID, e.g. <c>USB\VID_0781&amp;PID_5581\ABC123</c>. The
    /// enforcement key. Friendly names are never used to match, because a stick
    /// chooses its own.
    /// </param>
    /// <param name="Policy">
    /// Always <c>ReadOnly</c> today — the enum has no writable member, so a
    /// grant cannot express write access even if a payload were tampered with.
    /// </param>
    /// <param name="ExpiresAt">
    /// Absolute UTC deadline. The agent enforces this against its own clock and
    /// restricts the device when it passes, so a grant lapses on schedule even
    /// if the machine never reaches the server again.
    /// </param>
    public sealed record UsbGrant(string InstanceId, UsbGrantPolicy Policy, DateTimeOffset ExpiresAt);

    /// <remarks>
    /// Serialised by name, like <see cref="ServiceAction"/> and for the same
    /// reason: an ordinal on the wire is one reordering away from silently
    /// meaning something else, and the agent refuses any value that is not one
    /// of these exact strings.
    /// </remarks>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum UsbGrantPolicy
    {
        /// <summary>Readable, not writable. Windows itself refuses the writes.</summary>
        ReadOnly = 0,

        /// <summary>
        /// Ordinary read/write access for the life of the grant.
        /// </summary>
        /// <remarks>
        /// The widest value the protocol can carry. It is still time-boxed —
        /// there is no member meaning "permanently trusted" — so the endpoint
        /// returns to Restricted when the deadline passes, with or without
        /// further contact from the server.
        /// </remarks>
        Enabled = 1,
    }

    /// <summary>
    /// The complete USB storage policy for one endpoint.
    /// </summary>
    /// <remarks>
    /// Whole-state, not a delta: every storage device the endpoint sees that is
    /// not named in <paramref name="Grants"/> is restricted. An empty list is
    /// therefore a valid and meaningful payload — it means "restrict everything"
    /// — and it is also what an endpoint falls back to on its own if it never
    /// receives this task, so the failure mode of the whole channel is the safe
    /// state.
    /// </remarks>
    /// <param name="Grants">Every live grant. Absent device instance IDs are restricted.</param>
    /// <param name="IssuedAt">
    /// When the server built this policy. The agent keeps the newest and ignores
    /// an older one arriving late, so a delayed task cannot resurrect a grant
    /// that has since been revoked.
    /// </param>
    public sealed record ApplyUsbPolicy(IReadOnlyList<UsbGrant> Grants, DateTimeOffset IssuedAt);

    /// <summary>One account authorized to hold local administrator rights.</summary>
    /// <param name="Sid">
    /// The account's Windows SID. The identity enforcement is matched on, because a
    /// local account can be renamed and a rename must not retarget an elevation.
    /// </param>
    /// <param name="ExpiresAt">
    /// Absolute deadline. The endpoint compares this against its own clock, which is
    /// what makes an elevation lapse on time on a machine that never hears from the
    /// server again.
    /// </param>
    public sealed record LocalAdminElevationGrant(string Sid, DateTimeOffset ExpiresAt);

    /// <summary>
    /// The complete set of live administrator elevations for one endpoint.
    /// </summary>
    /// <remarks>
    /// Whole-state, not a delta: an account absent from <paramref name="Elevations"/>
    /// must not remain elevated. An empty list is therefore valid and meaningful --
    /// it means "nobody is authorized" -- and it is also what an endpoint converges
    /// to on its own when a grant lapses, so the failure mode of the whole channel is
    /// the narrow state.
    /// </remarks>
    /// <param name="IssuedAt">
    /// When the server built this set. The agent keeps the newest and ignores an
    /// older one arriving late, so a task queued before a revocation cannot reinstate
    /// the access it removed.
    /// </param>
    public sealed record ApplyLocalAdminElevation(
        IReadOnlyList<LocalAdminElevationGrant> Elevations,
        DateTimeOffset IssuedAt);

    /// <param name="ProcessId">PID to terminate.</param>
    /// <param name="ExpectedImageName">
    /// Executable name the PID must currently have (e.g. <c>notepad.exe</c>). Guards
    /// against a PID being reused by the OS between listing and termination.
    /// </param>
    public sealed record TerminateProcess(int ProcessId, string ExpectedImageName);

    /// <summary>
    /// Stop a named installed application. The endpoint resolves the processes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carries no process id and no image name, deliberately. Those are the
    /// agent's to determine from live state at execution time; a pid chosen here
    /// would already be a guess about a machine this server last heard from
    /// minutes or hours ago.
    /// </para>
    /// <para>
    /// <paramref name="InstallLocation"/> is the directory this platform recorded
    /// for the application in its own inventory -- server-derived, never supplied
    /// by a client. It is what the agent matches running executables against. The
    /// agent re-applies its own safety rules to it rather than trusting it: a
    /// broad or system root is refused on the endpoint too.
    /// </para>
    /// </remarks>
    public sealed record StopApplication(
        string ApplicationName, string? Publisher, string InstallLocation);

    /// <summary>
    /// Everything the agent needs to install an approved package, self-contained so
    /// the install decision never depends on a second lookup. The agent downloads
    /// the package content, verifies it against <paramref name="Sha256"/> and
    /// <paramref name="RequiredSignerSubject"/>, and installs it through the Windows
    /// Installer service only if both pins hold.
    /// </summary>
    /// <param name="PackageId">Package to download from the Agent API.</param>
    /// <param name="Sha256">Lowercase-hex content hash the downloaded bytes must match.</param>
    /// <param name="MsiProductCode">
    /// MSI ProductCode (braced GUID). Backs idempotency (already-installed is a
    /// success) and post-install verification.
    /// </param>
    /// <param name="RequiredSignerSubject">
    /// Substring the Authenticode signer subject must contain, or null to accept any
    /// trusted signature. Never accepts an unsigned file.
    /// </param>
    /// <param name="PackageName">Display name, for logging and the result message.</param>
    /// <param name="Version">Display version.</param>
    public sealed record InstallPackage(
        Guid PackageId,
        string Sha256,
        string MsiProductCode,
        string? RequiredSignerSubject,
        string PackageName,
        string Version);


    // ---- Driver installation (Milestone 13-3) ----
    //
    // Everything the endpoint needs to decide, on its own, whether to install: the
    // content pin, the signer pin, what the package claims to drive, and what must
    // be observable afterwards. Nothing here is a secret -- the hash is an integrity
    // pin, not a credential -- so the payload is safe in the task audit record that
    // DeviceTaskService writes for every queued task.

    /// <param name="PackageId">Approved package to download from the Agent API. Never a URL.</param>
    /// <param name="Sha256">
    /// Lowercase-hex hash the downloaded archive must match. Checked before a single
    /// entry is extracted, so tampered bytes are never unpacked.
    /// </param>
    /// <param name="InfFileName">Bare INF name inside the archive. Resolved beneath the extraction directory.</param>
    /// <param name="HardwareId">
    /// What the package drives. The endpoint refuses to touch the driver store unless
    /// a present device actually matches this.
    /// </param>
    /// <param name="RequiredSignerSubject">
    /// Substring the catalogue signer's subject must contain. Mandatory for drivers:
    /// a trusted signature alone is not enough for kernel code.
    /// </param>
    /// <param name="ExpectedProvider">Driver provider that must be observable afterwards, when known.</param>
    /// <param name="ExpectedDriverVersion">Driver version that must be observable afterwards, when known.</param>
    /// <param name="AllowDowngrade">
    /// Whether the endpoint may install over a newer driver. False by default and
    /// carried explicitly, so a downgrade is always a decision somebody made and the
    /// audit records which one.
    /// </param>
    /// <param name="IssuedAt">
    /// When the server issued this instruction. The endpoint refuses a payload older
    /// than its freshness window, so a captured task cannot be replayed into an
    /// install months later.
    /// </param>
    public sealed record InstallDriverPackage(
        Guid PackageId,
        string Sha256,
        string InfFileName,
        string HardwareId,
        string RequiredSignerSubject,
        string? ExpectedProvider,
        string? ExpectedDriverVersion,
        bool AllowDowngrade,
        string PackageName,
        DateTimeOffset IssuedAt);

    // ---- Local Windows account management (Phase 4 write side) ----
    //
    // Targets are identified by SID (the stable key; names are renameable), with the
    // last-known name carried alongside purely for logging/result messages. NO payload
    // ever carries a password: secrets travel by short-lived, one-time reference that
    // the agent redeems out-of-band from the server, so the plaintext never enters the
    // persisted PayloadJson (see docs/adr and the ephemeral-secret store).

    /// <param name="Username">Account (SAM) name to create.</param>
    /// <param name="FullName">Optional display name.</param>
    /// <param name="Description">Optional account description.</param>
    /// <param name="SecretRef">One-time reference the agent redeems to obtain the initial password.</param>
    /// <param name="Enabled">Whether the account is enabled on creation.</param>
    /// <param name="MustChangePasswordAtNextLogon">Force a password change at first logon.</param>
    /// <param name="Administrator">
    /// When true the agent adds the new account to BUILTIN\Administrators and verifies
    /// the membership before reporting success. The server has already checked that the
    /// operator holds the permission to grant it; this flag only carries the decision.
    /// </param>
    /// <param name="AdditionalGroups">
    /// Local groups to join after creation, already validated server-side against the
    /// permitted-groups allow-list. Never contains a protected group.
    /// </param>
    /// <param name="ProfileKey">The baseline this request came from, for the audit record.</param>
    public sealed record CreateLocalUser(
        string Username,
        string? FullName,
        string? Description,
        string SecretRef,
        bool Enabled,
        bool MustChangePasswordAtNextLogon,
        bool Administrator,
        IReadOnlyList<string> AdditionalGroups,
        string ProfileKey);

    /// <summary>Target a single local user by SID (name carried for logging only).</summary>
    public sealed record LocalUserTarget(string Sid, string Username);

    /// <param name="Sid">Target user SID.</param>
    /// <param name="Username">Last-known name (logging).</param>
    /// <param name="Enabled">Desired enabled state.</param>
    public sealed record SetLocalUserEnabled(string Sid, string Username, bool Enabled);

    /// <param name="Sid">Target user SID.</param>
    /// <param name="Username">Last-known name (logging).</param>
    /// <param name="SecretRef">One-time reference the agent redeems to obtain the new password.</param>
    public sealed record ResetLocalUserPassword(string Sid, string Username, string SecretRef);

    /// <param name="Sid">Target user SID.</param>
    /// <param name="Username">Last-known name (logging).</param>
    /// <param name="Administrator">
    /// True = add to BUILTIN\Administrators (promote); false = remove (demote). The agent
    /// changes real Windows group membership; this is never a database-only flag.
    /// </param>
    public sealed record ChangeLocalUserType(string Sid, string Username, bool Administrator);

    /// <param name="GroupSid">Target local group SID (e.g. S-1-5-32-544).</param>
    /// <param name="GroupName">Last-known group name (logging; agent resolves the SID to the localized name).</param>
    /// <param name="MemberSid">SID of the account to add/remove.</param>
    /// <param name="MemberName">Last-known member name (logging).</param>
    public sealed record LocalGroupMembership(
        string GroupSid, string GroupName, string MemberSid, string MemberName);

    // ---- Remote support (RustDesk) ----

    /// <summary>
    /// Configure an already-installed RustDesk for IT remote support by setting its
    /// unattended password. The password never rides in the payload: it travels as a
    /// one-time ephemeral-secret reference the agent redeems out-of-band (the same
    /// mechanism as <see cref="CreateLocalUser"/>), so the plaintext never enters the
    /// persisted PayloadJson or the audit trail.
    /// </summary>
    /// <param name="PasswordSecretRef">One-time reference the agent redeems to obtain the unattended password.</param>
    public sealed record ConfigureRemoteSupport(string PasswordSecretRef);

    // ---- Announcements ----

    /// <summary>
    /// A short administrator-written notice the agent shows on the device's
    /// screens. Plain text, already trimmed and length-checked server-side
    /// (title 80, message 500); the agent displays it verbatim and never
    /// interprets it.
    /// </summary>
    /// <param name="AnnouncementId">
    /// The announcement row this task came from. It travels with the notice over
    /// the session-notice pipe, and the agent keys dismissal on it: an
    /// announcement the user has dismissed is not shown again for the same id,
    /// however many tasks re-deliver it. It also makes the agent's log and the
    /// server's history name the same thing.
    /// </param>
    public sealed record ShowAnnouncement(Guid AnnouncementId, string Title, string Message);

    // ---- Screen lock ----

    /// <summary>
    /// A PBKDF2-SHA256 verifier of the organization's IT unlock code, so the agent
    /// can check a code entered on the locked machine itself without the plaintext
    /// ever leaving the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole reason the payload is safe to persist and to deliver over
    /// the task channel: it is a one-way verifier (base64 <paramref name="Salt"/>
    /// and <paramref name="Hash"/>), not the code. The agent derives PBKDF2 over
    /// what the operator typed with the same <paramref name="Algo"/>,
    /// <paramref name="Iterations"/> and salt and compares in constant time; it can
    /// confirm the right code but can never recover it, and neither can anyone who
    /// reads a captured task row.
    /// </para>
    /// <para>
    /// <paramref name="Algo"/> is always <c>pbkdf2-sha256</c> today and travels
    /// explicitly so the parameters can be raised later without the agent having to
    /// guess which construction an older lock used.
    /// </para>
    /// </remarks>
    public sealed record ScreenLockVerifier(string Algo, int Iterations, string Salt, string Hash);

    /// <summary>
    /// Engage a persistent full-screen lock. Plain text, already trimmed and
    /// length-checked server-side (message 500, contact line 200); the agent shows
    /// it verbatim and never interprets it.
    /// </summary>
    /// <param name="LockId">
    /// The screen-lock row this task came from. It travels with the lock and the
    /// agent keys the lock on it, so a matching <see cref="ReleaseScreenLock"/>
    /// lifts exactly this lock and both logs name the same thing.
    /// </param>
    /// <param name="Message">What to show on the locked screen (why it is locked).</param>
    /// <param name="ContactLine">
    /// An optional second line -- who to call to get it unlocked -- or null when the
    /// administrator gave none.
    /// </param>
    /// <param name="Unlock">
    /// The verifier of the IT unlock code the agent accepts on the device itself.
    /// Never the code (see <see cref="ScreenLockVerifier"/>).
    /// </param>
    public sealed record EngageScreenLock(Guid LockId, string Message, string? ContactLine, ScreenLockVerifier Unlock);

    /// <summary>
    /// Lift the lock that <see cref="EngageScreenLock"/> engaged. Names the lock by
    /// id so the agent releases exactly the one an administrator chose to lift.
    /// </summary>
    public sealed record ReleaseScreenLock(Guid LockId);

    // ---- Windows Update driver updates ----
    //
    // Two tasks, matching the two things an operator does from the console: find out
    // what driver updates a machine has waiting, and install the ones they chose. Both
    // carry only an issue time, never a credential -- the endpoint talks to Windows
    // Update over its own channel, so there is nothing here that is a secret and the
    // payloads are safe in the task audit record.

    /// <summary>
    /// Ask the device to query Windows Update for applicable driver updates and
    /// report the pending list, installing nothing.
    /// </summary>
    /// <param name="IssuedAt">
    /// When the server issued the scan. Carried so the endpoint can refuse a payload
    /// older than its freshness window and so the result and the request line up in
    /// the log; a scan is otherwise parameterless.
    /// </param>
    public sealed record ScanDriverUpdates(DateTimeOffset IssuedAt);

    /// <summary>
    /// Install the named Windows Update driver updates. Each id is a Windows Update
    /// update id the device previously reported as pending from a
    /// <see cref="ScanDriverUpdates"/> scan.
    /// </summary>
    /// <remarks>
    /// The server never sends a preview/beta update here: the console auto-selects
    /// only non-preview updates, and a caller naming one explicitly is still just
    /// naming an id the device decides for itself whether to act on. The endpoint
    /// re-queries Windows Update and installs only updates it still offers, so a
    /// captured task cannot install something the machine no longer considers
    /// applicable.
    /// </remarks>
    /// <param name="UpdateIds">
    /// The Windows Update update ids (GUID strings) to install. Never empty; the
    /// server refuses an empty selection before queuing.
    /// </param>
    /// <param name="IssuedAt">
    /// When the server issued the install. The endpoint refuses a payload older than
    /// its freshness window, so a captured task cannot be replayed into an install
    /// long afterwards.
    /// </param>
    public sealed record InstallDriverUpdate(IReadOnlyList<string> UpdateIds, DateTimeOffset IssuedAt);

    // ---- Windows Update security updates ----
    //
    // Two tasks, matching the two things an operator does from the console: find out
    // what security/quality updates a machine has waiting, and install the ones they
    // chose. Both carry only an issue time, never a credential -- the endpoint talks
    // to Windows Update over its own channel, so there is nothing here that is a
    // secret and the payloads are safe in the task audit record.

    /// <summary>
    /// Ask the device to query Windows Update for applicable security and quality
    /// updates and report the pending list, installing nothing.
    /// </summary>
    /// <param name="IssuedAt">
    /// When the server issued the scan. Carried so the endpoint can refuse a payload
    /// older than its freshness window and so the result and the request line up in
    /// the log; a scan is otherwise parameterless.
    /// </param>
    public sealed record ScanSecurityUpdates(DateTimeOffset IssuedAt);

    /// <summary>
    /// Install the named Windows Update security/quality updates. Each id is a
    /// Windows Update update id the device previously reported as pending from a
    /// <see cref="ScanSecurityUpdates"/> scan.
    /// </summary>
    /// <remarks>
    /// The server never sends a preview update here: the console auto-selects only
    /// non-preview updates, and a caller naming one explicitly is still just naming
    /// an id the device decides for itself whether to act on. The endpoint
    /// re-queries Windows Update and installs only updates it still offers, so a
    /// captured task cannot install something the machine no longer considers
    /// applicable.
    /// </remarks>
    /// <param name="UpdateIds">
    /// The Windows Update update ids (GUID strings) to install. Never empty; the
    /// server refuses an empty selection before queuing.
    /// </param>
    /// <param name="IssuedAt">
    /// When the server issued the install. The endpoint refuses a payload older than
    /// its freshness window, so a captured task cannot be replayed into an install
    /// long afterwards.
    /// </param>
    public sealed record InstallSecurityUpdate(IReadOnlyList<string> UpdateIds, DateTimeOffset IssuedAt);

    // ---- Live chat support ----
    //
    // Carry no secret: the chat messages themselves travel over the device-
    // credentialed chat endpoints, not in a task payload. These two tasks only tell
    // the endpoint to open and close the chat window for a named session.

    /// <summary>
    /// Open the live IT-support chat window on the device for a session.
    /// </summary>
    /// <param name="SessionId">
    /// The chat session this window belongs to. It keys the window, so the matching
    /// <see cref="EndLiveChat"/> closes exactly this one and the agent fetches
    /// messages for the right session.
    /// </param>
    /// <param name="AdminDisplay">
    /// What the window header shows as who the person is talking to. The server
    /// sends the generic employee-facing label ("IT Support"), never the operator's
    /// sign-in email -- that is an internal identifier with no business on an
    /// employee's screen; the real actor lives on the session row and in the audit
    /// trail. Not a secret and not an authenticator.
    /// </param>
    public sealed record StartLiveChat(Guid SessionId, string AdminDisplay);

    /// <summary>
    /// Close the live chat window that <see cref="StartLiveChat"/> opened. Names the
    /// session by id so the agent closes exactly the one an administrator ended.
    /// </summary>
    public sealed record EndLiveChat(Guid SessionId);

    // ---- Chrome extension control ----
    //
    // One whole-state task, like ApplyUsbPolicy: every rule the device has travels
    // in every task, and an extension absent from the payload has no rule. Nothing
    // here is a secret -- an extension id and a mode name -- so the payload is safe
    // in the task audit record that DeviceTaskService writes for every queued task.

    /// <summary>
    /// One rule: what Chrome must do about one extension on this device.
    /// </summary>
    /// <param name="ExtensionId">
    /// Chrome's 32-character identifier (letters a-p). The enforcement key the
    /// agent writes the policy under; names are never used to match.
    /// </param>
    /// <param name="Mode">
    /// Exactly one of the strings <c>ForceInstalled</c>, <c>Blocked</c>,
    /// <c>Allowed</c> or <c>Removed</c> -- the names of
    /// <c>Domain.Chrome.ChromeExtensionRuleMode</c>, written by the server from
    /// that enum and matched exactly by the agent. A string on the wire for the
    /// reason <see cref="ServiceAction"/> documents: an ordinal is one reordering
    /// away from silently meaning something else, and the agent drops any rule
    /// whose mode is not one of these exact strings.
    /// </param>
    public sealed record ChromeExtensionRulePayload(string ExtensionId, string Mode);

    /// <summary>
    /// The complete Chrome extension policy for one endpoint.
    /// </summary>
    /// <remarks>
    /// Whole-state, not a delta: the agent writes exactly <paramref name="Rules"/>
    /// into Chrome's <c>ExtensionSettings</c> policy and removes every entry it had
    /// written that is no longer named. An empty list is therefore a valid and
    /// meaningful payload -- it clears the platform's extension policy from the
    /// device -- and it is what lifting the last rule produces.
    /// </remarks>
    /// <param name="Rules">Every rule the device has. Absent extension ids have no rule.</param>
    /// <param name="IssuedAt">
    /// When the server built this policy. The agent keeps the newest and ignores an
    /// older one arriving late, so a delayed task cannot resurrect a rule that has
    /// since been lifted.
    /// </param>
    public sealed record ApplyChromeExtensionPolicy(
        IReadOnlyList<ChromeExtensionRulePayload> Rules,
        DateTimeOffset IssuedAt);
}
