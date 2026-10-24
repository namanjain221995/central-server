namespace EndpointPlatform.Domain.Enrollment;

/// <summary>
/// Where an enrollment request stands between an agent asking and a credential
/// being handed over.
/// </summary>
public enum PendingEnrollmentStatus
{
    /// <summary>Waiting for an administrator to approve or reject it.</summary>
    Pending = 0,

    /// <summary>An administrator approved it; the agent may now claim its credential.</summary>
    Approved = 1,

    /// <summary>An administrator refused it. No credential is ever issued for this request.</summary>
    Rejected = 2,
}

/// <summary>
/// What kind of admission is being asked for.
/// </summary>
/// <remarks>
/// Both kinds share one record shape and one store, because they share one
/// decision surface (the Pending Enrollments page) and one lifecycle
/// (pending → approved/rejected → gone). What differs is the stake: an
/// <see cref="Enrollment"/> admits an unknown machine, while a
/// <see cref="ReEnrollment"/> hands an EXISTING device's identity — its queued
/// tasks and device-bound secrets — to the requester and revokes the credential
/// the real machine is holding. That is why re-enrollment approval carries extra
/// checks (device scope, organization) that plain enrollment does not.
/// </remarks>
public enum PendingEnrollmentKind
{
    /// <summary>A machine with no active device record asking to be managed.</summary>
    /// <remarks>Zero so records written before the kind existed deserialize as this.</remarks>
    Enrollment = 0,

    /// <summary>
    /// A caller presenting a valid enrollment token whose machine identifier
    /// matches an ACTIVE device. Completing it revokes that device's credentials
    /// and hands its identity to the caller, so it waits for approval.
    /// </summary>
    ReEnrollment = 1,
}

/// <summary>
/// A machine asking to be managed, before it has any credential.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately NOT a database entity. A pending request is short-lived,
/// carries no authority, and an unauthenticated endpoint creates it —
/// so persisting it in PostgreSQL would let anyone on the internet write rows into
/// the managed estate's own database and would leave expiry as a cleanup job
/// somebody has to remember. Redis expires it automatically and forgets it for free.
/// </para>
/// <para>
/// A pending request grants nothing. It cannot run tasks, read inventory, or reach
/// any authenticated endpoint. Until an administrator approves it, the only thing it
/// can do is be polled for by the agent that created it.
/// </para>
/// </remarks>
/// <param name="MachineIdentifier">
/// SMBIOS system UUID. Not a secret and not an authenticator — it exists so an
/// approved re-enrolment resolves to the SAME device record rather than a duplicate.
/// </param>
/// <param name="Hostname">Reported hostname, shown to the approving administrator.</param>
/// <param name="OperatingSystem">Reported OS description, shown for recognition.</param>
/// <param name="AgentVersion">Reported agent version, shown for recognition.</param>
/// <param name="RequestedAt">When the agent asked.</param>
/// <param name="Status">Current state.</param>
/// <param name="OrganizationId">
/// For a plain enrollment: set at approval time from the approving administrator's
/// organization — the agent never supplies this, because an unauthenticated caller
/// must not be able to choose which tenant it joins. For a re-enrollment: set at
/// CREATION time from the presented token's organization, which is already proven
/// by the token itself; the approval checks it against the approver's.
/// </param>
/// <param name="SealedTokenSecret">
/// Set at approval time of a plain enrollment. The sealed secret of a single-use
/// enrollment token minted server-side, which the claim step feeds to the existing
/// enrollment path. Never leaves the server and never reaches the agent. Always
/// null for a re-enrollment — the agent already holds a token; approval only
/// unlocks it.
/// </param>
/// <param name="ApprovedBy">Display name of the approving administrator, for audit.</param>
/// <param name="Kind">What is being asked for; see <see cref="PendingEnrollmentKind"/>.</param>
/// <param name="ExistingDeviceId">
/// Re-enrollment only: the ACTIVE device whose identity would be handed over.
/// </param>
/// <param name="ExistingDeviceHostname">
/// Re-enrollment only: that device's hostname as its row read when the agent last
/// knocked — a recognition aid for the approver, refreshed on every retry.
/// </param>
/// <param name="TokenId">Re-enrollment only: the enrollment token presented, for audit lineage.</param>
public sealed record PendingEnrollment(
    string MachineIdentifier,
    string Hostname,
    string? OperatingSystem,
    string AgentVersion,
    DateTimeOffset RequestedAt,
    PendingEnrollmentStatus Status,
    Guid? OrganizationId = null,
    string? SealedTokenSecret = null,
    string? ApprovedBy = null,
    PendingEnrollmentKind Kind = PendingEnrollmentKind.Enrollment,
    Guid? ExistingDeviceId = null,
    string? ExistingDeviceHostname = null,
    Guid? TokenId = null)
{
    /// <summary>How long a plain enrollment request survives without a decision.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a PENDING re-enrollment survives since the agent last knocked.
    /// </summary>
    /// <remarks>
    /// Much longer than <see cref="Lifetime"/> on purpose. A plain request is
    /// re-created by the agent every 30 seconds, so 15 minutes loses nothing. A
    /// re-enrolling agent retries its token only every 5 minutes and may have been
    /// reinstalled overnight; the request must still be on the Pending
    /// Enrollments page when an administrator arrives in the morning. Refreshed on
    /// every retry, so it expires a day after the agent GAVE UP, not a day after
    /// it first asked.
    /// </remarks>
    public static readonly TimeSpan ReEnrollPendingLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a DECIDED re-enrollment record lives.
    /// </summary>
    /// <remarks>
    /// Approved: the agent retries within 5 minutes, so an hour is a wide margin;
    /// after that an unclaimed approval dies rather than waiting as a standing
    /// identity handover. Rejected: the same hour is the cooling-off period during
    /// which the still-knocking agent is refused WITHOUT re-creating a pending
    /// request — an administrator's "no" must stick longer than one retry cycle.
    /// After it expires the next retry re-pends, which is honest: the machine is
    /// still asking, and silence would hide that.
    /// </remarks>
    public static readonly TimeSpan ReEnrollDecisionLifetime = TimeSpan.FromHours(1);
}
