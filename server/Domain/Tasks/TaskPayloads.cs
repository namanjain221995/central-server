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
}
