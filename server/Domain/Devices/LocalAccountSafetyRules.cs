namespace EndpointPlatform.Domain.Devices;

/// <summary>A minimal projection of a local account for the safety rules.</summary>
public sealed record LocalAccountView(string Sid, string Username, bool Enabled, bool IsAdministrator);

/// <summary>
/// Domain safety rules that stop a local-account mutation from locking the
/// organization out of a managed endpoint — or from quietly handing an operator
/// control of its built-in Administrator.
/// </summary>
/// <remarks>
/// <para>
/// These are pure, side-effect-free predicates over a snapshot of the device's
/// local accounts. The Admin API runs them as a fast pre-check against the last
/// reported inventory; the agent re-checks the same invariants against LIVE
/// Windows state at execution time, because inventory can be stale. Both layers
/// enforce them — the UI is never the boundary. (The agent-side live re-check for
/// the password-reset, force-change and enable rules rides the next agent release;
/// until then the server pre-check is the enforced boundary for those three.)
/// </para>
/// <para>
/// Three protections:
/// <list type="bullet">
///   <item><b>Protected account</b>: the platform never touches the built-in
///   Administrator (RID 500) in ANY direction — it cannot be deleted, disabled,
///   enabled, have its password reset, or be forced to change it. Enable was
///   deliberately unguarded at first; the 2026-10 security audit showed
///   enable-then-reset composes into a silent takeover of RID 500, so the
///   hands-off rule is now symmetric.</item>
///   <item><b>Last administrator</b>: deleting, disabling, or demoting the last
///   <em>enabled</em> member of the local Administrators group is refused — that is
///   the classic self-lockout.</item>
///   <item><b>Administrator password equivalence</b>: resetting (or forcing a
///   change of) an administrator account's password to a value the caller chose is
///   equivalent to granting that caller administrator access, so it demands the
///   change-account-type authority on top of the reset permission. That rule needs
///   the caller's permissions, which these pure predicates never see, so it is
///   enforced by <c>LocalAccountManagementService</c> alongside the rules here.</item>
/// </list>
/// </para>
/// </remarks>
public static class LocalAccountSafetyRules
{
    /// <summary>The built-in Administrator account always has RID 500 (its SID ends "-500").</summary>
    public static bool IsBuiltInAdministrator(string sid) =>
        sid.EndsWith("-500", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a refusal reason, or null when the delete is permitted.</summary>
    public static string? ValidateDelete(string targetSid, IReadOnlyCollection<LocalAccountView> users)
    {
        if (IsBuiltInAdministrator(targetSid))
        {
            return "The built-in Administrator account is protected and cannot be deleted.";
        }

        return LastAdminGuard(targetSid, users, "deleting");
    }

    /// <summary>Returns a refusal reason, or null when the disable is permitted.</summary>
    public static string? ValidateDisable(string targetSid, IReadOnlyCollection<LocalAccountView> users)
    {
        if (IsBuiltInAdministrator(targetSid))
        {
            return "The built-in Administrator account is protected and cannot be disabled.";
        }

        return LastAdminGuard(targetSid, users, "disabling");
    }

    /// <summary>Returns a refusal reason, or null when the enable is permitted.</summary>
    /// <remarks>
    /// Enabling can only ever add an administrator, never remove the last one, so this
    /// rule needs no account snapshot — RID 500 is recognisable from the SID alone.
    /// </remarks>
    public static string? ValidateEnable(string targetSid) =>
        IsBuiltInAdministrator(targetSid)
            ? "The built-in Administrator account is protected and cannot be enabled."
            : null;

    /// <summary>Returns a refusal reason, or null when the password reset is permitted.</summary>
    public static string? ValidateResetPassword(string targetSid) =>
        IsBuiltInAdministrator(targetSid)
            ? "The built-in Administrator account is protected and its password cannot be reset."
            : null;

    /// <summary>Returns a refusal reason, or null when the forced password change is permitted.</summary>
    public static string? ValidateForcePasswordChange(string targetSid) =>
        IsBuiltInAdministrator(targetSid)
            ? "The built-in Administrator account is protected and cannot be forced to change its password."
            : null;

    /// <summary>
    /// Returns a refusal reason, or null when the demotion (remove from Administrators)
    /// is permitted.
    /// </summary>
    public static string? ValidateDemote(string targetSid, IReadOnlyCollection<LocalAccountView> users) =>
        LastAdminGuard(targetSid, users, "demoting");

    private static string? LastAdminGuard(
        string targetSid, IReadOnlyCollection<LocalAccountView> users, string verb)
    {
        var target = users.FirstOrDefault(
            u => string.Equals(u.Sid, targetSid, StringComparison.OrdinalIgnoreCase));

        // If the target is not a currently-enabled administrator, the operation cannot
        // reduce the enabled-admin count to zero. If we cannot see the target at all,
        // defer to the agent's authoritative live re-check rather than guess.
        if (target is null || !target.IsAdministrator || !target.Enabled)
        {
            return null;
        }

        var otherEnabledAdmins = users.Count(u =>
            u.IsAdministrator
            && u.Enabled
            && !string.Equals(u.Sid, targetSid, StringComparison.OrdinalIgnoreCase));

        return otherEnabledAdmins == 0
            ? $"Refusing: {verb} '{target.Username}' would leave this device with no enabled administrator."
            : null;
    }
}
