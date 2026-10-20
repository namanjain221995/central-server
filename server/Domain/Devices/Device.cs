using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// A managed Windows endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Created exclusively by successful enrollment — there is no "add device" form,
/// because a device row without a credentialled agent behind it would be
/// indistinguishable from a dead one.
/// </para>
/// <para>
/// Online/offline is deliberately NOT a stored flag. It is derived from
/// <see cref="LastSeenAt"/> versus the heartbeat interval at query time, so a
/// crashed agent cannot leave a device marked "online" forever, and the
/// definition of "online" can be tuned without a data migration.
/// </para>
/// </remarks>
public sealed class Device : AuditableEntity
{
    private Device()
    {
        Hostname = null!;
        MachineIdentifier = null!;
        AgentVersion = null!;
    }

    public Device(
        Guid organizationId,
        string hostname,
        string machineIdentifier,
        string agentVersion,
        string? operatingSystem)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), maxLength: 253);
        MachineIdentifier = Guard.NotNullOrWhiteSpace(machineIdentifier, nameof(machineIdentifier), maxLength: 128);
        AgentVersion = Guard.NotNullOrWhiteSpace(agentVersion, nameof(agentVersion), maxLength: 64);
        OperatingSystem = Guard.OptionalMaxLength(operatingSystem, 256);
        Status = DeviceStatus.Active;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// The one group this device belongs to. Never empty once persisted: the
    /// column is a non-null foreign key, and a new device is placed in its
    /// organization's "All Devices" group when it is first saved.
    /// </summary>
    /// <remarks>
    /// A property of the device rather than a row in a membership table, so that
    /// "in no group" and "in two groups" cannot be written at all -- not merely
    /// rejected by application code that every write path would have to remember
    /// to call.
    /// </remarks>
    public Guid DeviceGroupId { get; private set; }

    /// <summary>Hostname as reported by the agent. Display data, not identity.</summary>
    public string Hostname { get; private set; }

    /// <summary>
    /// An administrator's own label for this machine — "TAM0149", "HR-Laptop-01".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Console-side only. Renaming a device here does not touch the Windows
    /// computer name, and the agent keeps reporting the real hostname on every
    /// heartbeat. The two are stored separately precisely so that an estate can
    /// be labelled to match how the organization actually refers to its machines
    /// without anyone having to rename Windows to make the console readable.
    /// </para>
    /// <para>
    /// Null means "no label" and <see cref="Name"/> falls back to
    /// <see cref="Hostname"/>. Blank input normalises to null rather than to an
    /// empty string, so clearing the field restores the hostname instead of
    /// leaving a device with no name at all.
    /// </para>
    /// </remarks>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// What a human should be shown for this device: the administrator's label if
    /// one is set, otherwise the machine's real hostname. Never empty.
    /// </summary>
    public string Name => DisplayName ?? Hostname;

    /// <summary>
    /// Stable machine identifier (SMBIOS UUID where available) used to detect
    /// re-enrollment of a known machine. NOT a secret and NOT authentication —
    /// it is spoofable by design and treated purely as a dedup hint.
    /// </summary>
    public string MachineIdentifier { get; private set; }

    public string AgentVersion { get; private set; }

    public string? OperatingSystem { get; private set; }

    public DeviceStatus Status { get; private set; }

    /// <summary>Set on enrollment and on every authenticated heartbeat.</summary>
    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>
    /// When the machine last started, on the server's clock: the uptime the agent
    /// reported, subtracted from the heartbeat's receive time. Null until an
    /// agent that reports it (1.15.0+) checks in. Tells whether a restart that
    /// was due actually happened, including one carried out while offline.
    /// </summary>
    public DateTimeOffset? LastBootAt { get; private set; }

    /// <summary>Interactive user reported by the last inventory, e.g. <c>DOMAIN\jsmith</c>.</summary>
    public string? LoggedOnUser { get; private set; }

    /// <summary>
    /// Whether the agent said, on its last heartbeat, that it is currently showing
    /// a persistent full-screen lock. Null until an agent that reports it (1.19.0+)
    /// checks in.
    /// </summary>
    /// <remarks>
    /// A reported FACT, stored like <see cref="LastSeenAt"/> -- not the device's
    /// "locked" status, which stays derived (from the engage/release tasks combined
    /// with this flag), the same way <see cref="IsOnline"/> is derived from
    /// <see cref="LastSeenAt"/>. Its one job is to let a lock cleared on the machine
    /// itself, with the IT unlock code, show as released in the console even though
    /// no release task was queued: the agent reports false and the derived status
    /// follows. See the screen-lock service's derivation rule.
    /// </remarks>
    public bool? ScreenLockedReported { get; private set; }

    /// <summary>
    /// The device's RustDesk ID for IT remote support, as the agent last read it
    /// from the installed RustDesk's own configuration. Null until an agent that
    /// reports it (1.20.0+) checks in with RustDesk installed and registered. An
    /// identifier an administrator dials, never a secret; the unattended password is
    /// sealed separately in the device's remote-support enrolment.
    /// </summary>
    public string? RemoteSupportId { get; private set; }

    /// <summary>
    /// When an administrator last asked for a fresh inventory. The next heartbeat
    /// response tells the agent to upload one; comparing against
    /// <see cref="InventoryCollectedAt"/> decides whether the request is still
    /// outstanding. Pull-based on purpose: the server never connects to agents.
    /// </summary>
    public DateTimeOffset? InventoryRequestedAt { get; private set; }

    /// <summary>Server receive time of the most recent inventory upload.</summary>
    public DateTimeOffset? InventoryCollectedAt { get; private set; }

    /// <summary>The enrollment token that admitted this device, for audit lineage.</summary>
    public Guid EnrolledWithTokenId { get; private set; }

    public DateTimeOffset EnrolledAt { get; private set; }

    public bool IsRetired => Status == DeviceStatus.Retired;

    public static Device Enroll(
        Guid organizationId,
        string hostname,
        string machineIdentifier,
        string agentVersion,
        string? operatingSystem,
        Guid enrollmentTokenId,
        DateTimeOffset now)
    {
        var device = new Device(organizationId, hostname, machineIdentifier, agentVersion, operatingSystem)
        {
            EnrolledWithTokenId = Guard.NotEmpty(enrollmentTokenId),
            EnrolledAt = now,
            LastSeenAt = now,
        };

        return device;
    }

    /// <summary>Applies an authenticated heartbeat.</summary>
    /// <param name="uptime">
    /// How long the machine had been running when it sent the heartbeat, or null
    /// when the agent did not say. Measured on the agent alone, so the boot time
    /// derived from it is on the server's clock whatever the device's clock reads.
    /// </param>
    /// <param name="screenLocked">
    /// Whether the agent is currently showing a persistent full-screen lock, or null
    /// when it did not say (an agent before 1.19.0). Null leaves the last reported
    /// value untouched -- an older agent must not wipe a truth a newer one reported
    /// -- while true or false records the agent's current account, which is what
    /// lets a lock cleared on the machine with the IT unlock code show as released.
    /// </param>
    /// <param name="remoteSupportId">
    /// The device's RustDesk ID for remote support, or null when the agent did not
    /// say. Null leaves the last reported value untouched, mirroring screenLocked.
    /// </param>
    public void RecordHeartbeat(
        string hostname, string agentVersion, string? operatingSystem, DateTimeOffset now,
        TimeSpan? uptime = null, bool? screenLocked = null, string? remoteSupportId = null)
    {
        // A retired device's heartbeats are rejected upstream; guard here too so a
        // coding error cannot quietly resurrect one.
        if (IsRetired)
        {
            throw new InvalidOperationException(
                $"Device {Id} is retired and cannot record heartbeats.");
        }

        if (uptime is { } running && running >= TimeSpan.Zero && running <= TimeSpan.FromDays(400))
        {
            LastBootAt = now - running;
        }

        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), maxLength: 253);
        AgentVersion = Guard.NotNullOrWhiteSpace(agentVersion, nameof(agentVersion), maxLength: 64);

        if (operatingSystem is not null)
        {
            OperatingSystem = Guard.OptionalMaxLength(operatingSystem, 256);
        }

        // Only overwrite when the agent actually reported it. A null means "this
        // build does not say", not "unlocked", so an old agent heartbeating must
        // not clear what a newer one last told us.
        if (screenLocked is not null)
        {
            ScreenLockedReported = screenLocked;
        }

        // Only overwrite when the agent reported a RustDesk ID; a null or blank means
        // "this build or install does not say", not "no RustDesk".
        if (!string.IsNullOrWhiteSpace(remoteSupportId))
        {
            RemoteSupportId = Guard.OptionalMaxLength(remoteSupportId.Trim(), 64);
        }

        LastSeenAt = now;
    }

    /// <summary>
    /// Re-enrollment of a machine that already has a device record (e.g. after an
    /// OS reinstall): refresh the reported facts and the admitting token, keep the
    /// device id and its history.
    /// </summary>
    public void ReEnroll(
        string hostname,
        string agentVersion,
        string? operatingSystem,
        Guid enrollmentTokenId,
        DateTimeOffset now)
    {
        if (IsRetired)
        {
            throw new InvalidOperationException(
                $"Device {Id} is retired; re-enrollment requires an administrator to reactivate it first.");
        }

        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), maxLength: 253);
        AgentVersion = Guard.NotNullOrWhiteSpace(agentVersion, nameof(agentVersion), maxLength: 64);
        OperatingSystem = Guard.OptionalMaxLength(operatingSystem, 256);
        EnrolledWithTokenId = Guard.NotEmpty(enrollmentTokenId);
        EnrolledAt = now;
        LastSeenAt = now;
    }

    /// <summary>
    /// Sets or clears the administrator's label for this device. Pass null or
    /// blank to clear it and fall back to the reported hostname.
    /// </summary>
    /// <remarks>
    /// Touches <see cref="DisplayName"/> and nothing else. <see cref="Hostname"/>,
    /// <see cref="MachineIdentifier"/>, the device id and the enrollment lineage
    /// are all left exactly as they were — renaming is a labelling operation, not
    /// a change of identity, and nothing about how this machine authenticates or
    /// how re-enrollment resolves it may move as a side effect.
    /// </remarks>
    public void Rename(string? displayName) =>
        DisplayName = Guard.OptionalMaxLength(displayName, 128);

    /// <summary>Marks an administrator's request for a fresh inventory.</summary>
    public void RequestInventoryRefresh(DateTimeOffset now) => InventoryRequestedAt = now;

    /// <summary>True when the agent should be told to upload inventory.</summary>
    public bool IsInventoryRefreshPending =>
        InventoryCollectedAt is null
        || (InventoryRequestedAt is { } requested && requested > InventoryCollectedAt);

    /// <summary>Applies the device-level facts carried by an inventory upload.</summary>
    public void RecordInventory(string? loggedOnUser, DateTimeOffset now)
    {
        if (IsRetired)
        {
            throw new InvalidOperationException($"Device {Id} is retired and cannot record inventory.");
        }

        LoggedOnUser = Guard.OptionalMaxLength(loggedOnUser, 256);
        InventoryCollectedAt = now;
    }

    /// <summary>
    /// Places the device in a group, leaving whichever group it was in. One
    /// column changes, so a move is atomic by construction and cannot leave the
    /// device in both groups or in neither.
    /// </summary>
    /// <remarks>
    /// Whether the caller may make this move is not decided here. Group
    /// membership is an administrator's device scope, so moving a device changes
    /// who can act on it; the service authorizes both ends before calling this.
    /// Retired devices may be moved too: group membership is organizational
    /// bookkeeping, independent of whether the device is managed.
    /// </remarks>
    public void MoveToGroup(Guid deviceGroupId) => DeviceGroupId = Guard.NotEmpty(deviceGroupId);

    public void Retire() => Status = DeviceStatus.Retired;

    /// <summary>
    /// Returns a retired device to service so it can enroll again. Offboarding
    /// revokes the device's credentials; reactivation does not restore them - the
    /// machine must re-enroll to obtain a fresh credential.
    /// </summary>
    public void Reactivate() => Status = DeviceStatus.Active;

    /// <summary>Whether the device counts as online given the staleness threshold.</summary>
    public bool IsOnline(DateTimeOffset now, TimeSpan staleAfter) =>
        Status == DeviceStatus.Active
        && LastSeenAt is { } lastSeen
        && now - lastSeen <= staleAfter;
}
