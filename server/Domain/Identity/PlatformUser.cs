using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// A human administrator of the platform who signs in to the Admin API.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately named <c>PlatformUser</c>, not <c>User</c>. "User" is ambiguous in
/// this product because the platform also manages Windows <em>local</em> user
/// accounts on endpoints, which are an entirely different thing with a different
/// trust model. Keeping the names distinct prevents the two from ever being
/// conflated in code, in the API surface or in an audit record.
/// </para>
/// <para>
/// This type never holds a plaintext password. <see cref="PasswordHash"/> holds an
/// encoded hash produced by the credential hasher in the infrastructure layer, and
/// it is excluded from serialisation and from logging.
/// </para>
/// </remarks>
public sealed class PlatformUser : AuditableEntity
{
    private readonly List<PlatformUserRole> _roles = [];

    private PlatformUser()
    {
        Email = null!;
        NormalizedEmail = null!;
        DisplayName = null!;
        SecurityStamp = null!;
    }

    public PlatformUser(Guid organizationId, string email, string displayName)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Email = Guard.NotNullOrWhiteSpace(email, nameof(email), maxLength: 254);
        NormalizedEmail = Email.ToUpperInvariant();
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, nameof(displayName), maxLength: 200);
        Status = PlatformUserStatus.Invited;
        SecurityStamp = Guid.CreateVersion7().ToString("N");
    }

    public Guid OrganizationId { get; private set; }

    public Organization? Organization { get; private set; }

    public string Email { get; private set; }

    /// <summary>Upper-invariant form of <see cref="Email"/>, used for the uniqueness index and lookups.</summary>
    public string NormalizedEmail { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>Encoded password hash. Never a plaintext password, never logged, never serialised.</summary>
    public string? PasswordHash { get; private set; }

    public DateTimeOffset? PasswordUpdatedAt { get; private set; }

    /// <summary>
    /// Rotated whenever credentials or role assignments change. Issued access tokens
    /// carry the stamp so that a disabled or re-permissioned account's outstanding
    /// tokens stop validating immediately instead of at natural expiry.
    /// </summary>
    public string SecurityStamp { get; private set; }

    public PlatformUserStatus Status { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public int FailedSignInCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// When the most recent failed sign-in happened, or null if the counter is clear.
    /// </summary>
    /// <remarks>
    /// Exists so <see cref="FailedSignInCount"/> can decay. Without it the counter
    /// is write-only and can never be told how old it is. Cleared wherever the
    /// count is cleared, so the two can never disagree.
    /// </remarks>
    public DateTimeOffset? LastFailedSignInAt { get; private set; }

    /// <summary>
    /// A built-in account created by seeding rather than by an administrator.
    /// System accounts cannot be deleted, only disabled.
    /// </summary>
    public bool IsSystemAccount { get; private set; }

    /// <summary>
    /// Whether this administrator's authority spans every device in the organization.
    /// </summary>
    /// <remarks>
    /// Deny-by-default: a new administrator has this false and no
    /// <see cref="AdminDeviceScope"/> rows, so their permissions reach no device until
    /// scope is granted explicitly. "No scope" therefore means "nothing", never
    /// "everything" — the inverse would make every future account silently omnipotent.
    /// </remarks>
    public bool HasAllDeviceScope { get; private set; }

    /// <summary>
    /// Whether this administrator must replace their password before they may do
    /// anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set when an administrator is created with, or reset to, a password the SERVER
    /// generated and displayed exactly once. Until it is cleared the account can
    /// authenticate and change its password, and nothing else — enforced server-side,
    /// because the temporary password is a fully valid credential and a caller holding
    /// it can obtain a bearer token without ever loading the dashboard.
    /// </para>
    /// <para>
    /// Deliberately NOT part of <see cref="SetPasswordHash"/>, in either direction.
    /// That method also runs on a successful sign-in whenever the stored hash needs
    /// rehashing, so clearing the flag there would let a plain sign-in with the
    /// generated password silently satisfy the requirement — leaving a new
    /// administrator holding a credential that was shown on someone's screen.
    /// </para>
    /// <para>
    /// <see cref="PlatformUserStatus.Invited"/> cannot carry this meaning:
    /// <see cref="SetPasswordHash"/> promotes Invited to Active as soon as the
    /// generated password is stored, so the marker would evaporate on creation.
    /// </para>
    /// </remarks>
    public bool MustChangePassword { get; private set; }

    /// <summary>
    /// The sealed TOTP secret behind this administrator's authenticator app, or
    /// null if they have not started enrolling.
    /// </summary>
    /// <remarks>
    /// Always ciphertext. The domain never holds the Base32 secret - sealing and
    /// unsealing belong to <c>ITotpSecretProtector</c> in the infrastructure layer,
    /// under a key the Agent API is forbidden to hold.
    /// </remarks>
    public string? TotpSealedSecret { get; private set; }

    /// <summary>
    /// When the administrator proved they could produce a code, or null while
    /// enrolment is still unconfirmed.
    /// </summary>
    /// <remarks>
    /// The distinction matters: a secret that has been issued but never confirmed
    /// must NOT be treated as a second factor. Otherwise an interrupted enrolment
    /// would leave an account that demands a code nobody can generate.
    /// </remarks>
    public DateTimeOffset? TotpConfirmedAt { get; private set; }

    /// <summary>The last time step accepted for this account.</summary>
    /// <remarks>
    /// Replay prevention. A code stays valid for about ninety seconds, so without
    /// recording which step was used, a code read over somebody's shoulder - or
    /// captured in a screenshot - can be used again inside its own window. Every
    /// verification must be strictly greater than this.
    /// </remarks>
    public long? TotpLastCounter { get; private set; }

    /// <summary>Whether this account has a usable second factor.</summary>
    public bool HasConfirmedMfa => TotpSealedSecret is not null && TotpConfirmedAt is not null;

    /// <summary>Stores a freshly issued, not-yet-proven TOTP secret.</summary>
    /// <remarks>
    /// Leaves <see cref="TotpConfirmedAt"/> null on purpose. Re-enrolling resets
    /// the counter, because the new secret's time steps are unrelated to the old
    /// one's and carrying the old high-water mark would reject valid codes until
    /// real time caught up with it.
    /// </remarks>
    public void BeginMfaEnrolment(string sealedSecret)
    {
        TotpSealedSecret = Guard.NotNullOrWhiteSpace(sealedSecret, nameof(sealedSecret), maxLength: 512);
        TotpConfirmedAt = null;
        TotpLastCounter = null;
    }

    /// <summary>Records that the administrator produced a valid code.</summary>
    public void ConfirmMfaEnrolment(DateTimeOffset now, long counter)
    {
        if (TotpSealedSecret is null)
        {
            throw new InvalidOperationException("There is no enrolment to confirm.");
        }

        TotpConfirmedAt = now;
        TotpLastCounter = counter;
    }

    /// <summary>Records a counter as spent, so the same code cannot be used twice.</summary>
    public void RecordMfaCounter(long counter) => TotpLastCounter = counter;

    /// <summary>
    /// Removes the second factor entirely, returning the account to enrolment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used when somebody loses their phone and has no recovery codes left, and
    /// the caller must hold <c>Platform.UserManage</c>. The security stamp is
    /// rotated so every session the account holds dies immediately - if the reset
    /// is happening because the account may be compromised, leaving its sessions
    /// alive would defeat the point.
    /// </para>
    /// <para>
    /// Enrolment is mandatory, so the account is not left without a second factor:
    /// it is left needing to enrol again at the next sign-in.
    /// </para>
    /// </remarks>
    public void ResetMfa()
    {
        TotpSealedSecret = null;
        TotpConfirmedAt = null;
        TotpLastCounter = null;
        RotateSecurityStamp();
    }

    /// <summary>Requires a password change before this account may be used further.</summary>
    public void RequirePasswordChange() => MustChangePassword = true;

    /// <summary>
    /// Records that the required change has happened. Called only from the
    /// change-password path, never from <see cref="SetPasswordHash"/> — see the note
    /// on <see cref="MustChangePassword"/>.
    /// </summary>
    public void CompleteRequiredPasswordChange() => MustChangePassword = false;

    /// <summary>Grants authority over every device in the organization.</summary>
    public void GrantAllDeviceScope() => HasAllDeviceScope = true;

    /// <summary>Revokes organization-wide authority, leaving only explicit group scopes.</summary>
    public void RevokeAllDeviceScope() => HasAllDeviceScope = false;

    public IReadOnlyCollection<PlatformUserRole> Roles => _roles.AsReadOnly();

    public void MarkAsSystemAccount() => IsSystemAccount = true;

    /// <summary>
    /// Stores an already-hashed credential. The domain never sees the plaintext, so
    /// there is no code path here that could accidentally persist or log one.
    /// </summary>
    public void SetPasswordHash(string encodedHash, DateTimeOffset now)
    {
        PasswordHash = Guard.NotNullOrWhiteSpace(encodedHash, nameof(encodedHash), maxLength: 512);
        PasswordUpdatedAt = now;
        FailedSignInCount = 0;
        LastFailedSignInAt = null;
        LockedUntil = null;
        RotateSecurityStamp();

        if (Status == PlatformUserStatus.Invited)
        {
            Status = PlatformUserStatus.Active;
        }
    }

    public void RecordSuccessfulSignIn(DateTimeOffset now)
    {
        LastLoginAt = now;
        FailedSignInCount = 0;
        LastFailedSignInAt = null;
        LockedUntil = null;
    }

    /// <summary>
    /// Counts a failed sign-in and locks the account once the threshold is reached
    /// inside <paramref name="decayWindow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The decay window and the expiry reset below are a denial-of-service fix,
    /// not housekeeping.</b> Previously the counter never forgot and the status was
    /// never cleared, so five failures locked the account for fifteen minutes and
    /// then left it at <c>Locked</c> with the count still at five. The next single
    /// failure re-locked it immediately. One wrong password every fifteen minutes -
    /// trivial to automate - kept a named administrator locked out permanently, and
    /// on an internet-facing console the e-mail address needed to aim it is simply
    /// the username.
    /// </para>
    /// <para>
    /// <b>The window alone does not close that hole</b>, which is worth stating
    /// because it looks like it should. An attacker can still send the whole
    /// threshold in a burst inside the window and repeat once the lockout expires.
    /// What closes it is the caller: <c>AdminAuthService</c> does not call this
    /// method at all once the originating address is throttled, so an attacker who
    /// keeps failing stops being able to drive this counter. The two parts are one
    /// mechanism; changing either without the other reopens the hole.
    /// </para>
    /// <para>
    /// Lockout deliberately remains a real refusal rather than a delay. It is the
    /// backstop for a distributed attempt, where no single address trips the
    /// per-address throttle.
    /// </para>
    /// </remarks>
    public void RecordFailedSignIn(
        DateTimeOffset now, int lockoutThreshold, TimeSpan lockoutDuration, TimeSpan decayWindow)
    {
        // Old failures are forgotten. Without this the counter is a ratchet: it
        // only ever moves toward locked, so an account accumulates failures across
        // months of ordinary typos and locks on an unrelated one.
        if (LastFailedSignInAt is { } last && now - last > decayWindow)
        {
            FailedSignInCount = 0;
        }

        // An expired lockout is over, so stop describing the account as locked AND
        // forgive the failures that caused it. Clearing the status alone is not
        // enough - and getting that wrong is what the ratchet actually was. The
        // count would still sit at the threshold, so the very next failure would
        // re-lock immediately and the fifteen-minute penalty would become
        // permanent. Serving the lockout clears the debt.
        //
        // Only Locked is cleared: a Disabled account must never be revived by
        // someone typing a wrong password at it.
        if (Status == PlatformUserStatus.Locked && (LockedUntil is null || LockedUntil <= now))
        {
            Status = PasswordHash is null ? PlatformUserStatus.Invited : PlatformUserStatus.Active;
            LockedUntil = null;
            FailedSignInCount = 0;
        }

        LastFailedSignInAt = now;
        FailedSignInCount++;

        if (FailedSignInCount >= lockoutThreshold)
        {
            Status = PlatformUserStatus.Locked;
            LockedUntil = now + lockoutDuration;
        }
    }

    public bool IsLockedOut(DateTimeOffset now) =>
        Status == PlatformUserStatus.Locked && LockedUntil is { } until && until > now;

    public void Disable()
    {
        Status = PlatformUserStatus.Disabled;
        RotateSecurityStamp();
    }

    public void Enable()
    {
        Status = PasswordHash is null ? PlatformUserStatus.Invited : PlatformUserStatus.Active;
        FailedSignInCount = 0;
        LastFailedSignInAt = null;
        LockedUntil = null;
        RotateSecurityStamp();
    }

    public void AssignRole(Guid roleId)
    {
        Guard.NotEmpty(roleId);

        if (_roles.Any(r => r.RoleId == roleId))
        {
            return;
        }

        _roles.Add(new PlatformUserRole(Id, roleId));
        RotateSecurityStamp();
    }

    public void RemoveRole(Guid roleId)
    {
        var removed = _roles.RemoveAll(r => r.RoleId == roleId);

        if (removed > 0)
        {
            RotateSecurityStamp();
        }
    }

    private void RotateSecurityStamp() => SecurityStamp = Guid.CreateVersion7().ToString("N");
}
