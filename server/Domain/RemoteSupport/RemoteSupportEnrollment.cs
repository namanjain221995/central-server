using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.RemoteSupport;

/// <summary>
/// One device's enrolment for IT remote support: the platform-managed RustDesk
/// unattended password, sealed. One row per device.
/// </summary>
/// <remarks>
/// <para>
/// <b>This entity cannot hold a plaintext password.</b> The constructor takes an
/// already-sealed value and nothing returns plaintext. Sealing and unsealing happen
/// in the service layer with the recovery-key protector (the symmetric escrow key),
/// which lives only in the Admin API, so the Agent API can never read it -- exactly
/// like <c>BitLockerRecoveryEscrow</c>.
/// </para>
/// <para>
/// The device's RustDesk ID is NOT stored here: it lives on
/// <c>Device.RemoteSupportId</c>, reported on the heartbeat, because it is an
/// identifier an administrator dials rather than a secret, and it changes with the
/// install while the sealed password is what this record exists to keep.
/// </para>
/// </remarks>
public sealed class RemoteSupportEnrollment : Entity
{
    private RemoteSupportEnrollment()
    {
        SealedPassword = null!;
        ConfiguredByDisplay = null!;
    }

    /// <param name="sealedPassword">
    /// The AES-GCM envelope produced by the recovery-key protector. Never the
    /// password itself: this type has no way to seal one, which stops a caller
    /// passing plaintext by mistake.
    /// </param>
    public RemoteSupportEnrollment(
        Guid organizationId,
        Guid deviceId,
        string sealedPassword,
        int keyVersion,
        Guid configuredByUserId,
        string configuredByDisplay,
        DateTimeOffset now)
    {
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        DeviceId = Guard.NotEmpty(deviceId, nameof(deviceId));
        SealedPassword = Guard.NotNullOrWhiteSpace(sealedPassword, nameof(sealedPassword), maxLength: 4096);
        KeyVersion = keyVersion > 0
            ? keyVersion
            : throw new ArgumentOutOfRangeException(nameof(keyVersion), "Key version must be positive.");
        ConfiguredByUserId = Guard.NotEmpty(configuredByUserId, nameof(configuredByUserId));
        ConfiguredByDisplay = Guard.NotNullOrWhiteSpace(configuredByDisplay, nameof(configuredByDisplay), 320);
        ConfiguredAt = now;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>AES-GCM envelope of the RustDesk unattended password. Opaque here, never decrypted by the domain.</summary>
    public string SealedPassword { get; private set; }

    /// <summary>Which escrow key sealed it, so a future re-key can find the rows to re-seal.</summary>
    public int KeyVersion { get; private set; }

    public Guid ConfiguredByUserId { get; private set; }

    public string ConfiguredByDisplay { get; private set; }

    public DateTimeOffset ConfiguredAt { get; private set; }

    /// <summary>How many times the password has been revealed. A cheap misuse signal.</summary>
    public int RevealedCount { get; private set; }

    public DateTimeOffset? LastRevealedAt { get; private set; }

    public Guid? LastRevealedByUserId { get; private set; }

    /// <summary>
    /// Re-seals with a freshly generated password on re-enrolment, superseding the
    /// old sealed value in place (one row per device).
    /// </summary>
    public void Reseal(string sealedPassword, int keyVersion, Guid actorId, string actorDisplay, DateTimeOffset now)
    {
        SealedPassword = Guard.NotNullOrWhiteSpace(sealedPassword, nameof(sealedPassword), maxLength: 4096);
        KeyVersion = keyVersion > 0
            ? keyVersion
            : throw new ArgumentOutOfRangeException(nameof(keyVersion), "Key version must be positive.");
        ConfiguredByUserId = Guard.NotEmpty(actorId, nameof(actorId));
        ConfiguredByDisplay = Guard.NotNullOrWhiteSpace(actorDisplay, nameof(actorDisplay), 320);
        ConfiguredAt = now;
    }

    /// <summary>Records a successful reveal. Does not touch the sealed value.</summary>
    public void RecordReveal(Guid actorId, DateTimeOffset now)
    {
        RevealedCount++;
        LastRevealedAt = now;
        LastRevealedByUserId = actorId;
    }
}
