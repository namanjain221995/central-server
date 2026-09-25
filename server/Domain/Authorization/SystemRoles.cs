using System.Collections.Frozen;

namespace EndpointPlatform.Domain.Authorization;

/// <summary>
/// The four built-in roles and the permissions each one grants.
/// </summary>
/// <remarks>
/// <para>
/// These definitions are the seed data for the <c>roles</c> and
/// <c>role_permissions</c> tables. They are re-applied on every startup so a role
/// cannot drift away from its documented meaning by accident, but built-in roles
/// are never deleted and their permission sets are the only thing reconciled.
/// </para>
/// <para>
/// Note what each role deliberately does NOT get:
/// Helpdesk cannot change account type, delete accounts, deploy software or
/// assign policies. Auditor is strictly read-only — it holds no permission whose
/// name implies mutation. Those omissions are asserted by
/// <c>SystemRoleTests</c> so a future edit cannot quietly widen them.
/// </para>
/// </remarks>
public static class SystemRoles
{
    public const string SuperAdministrator = "super_administrator";
    public const string ItAdministrator = "it_administrator";
    public const string Helpdesk = "helpdesk";
    public const string Auditor = "auditor";

    /// <summary>
    /// Super Administrator holds every permission in the catalogue by definition,
    /// computed rather than listed so a new permission can never be accidentally
    /// withheld from it.
    /// </summary>
    private static IReadOnlyList<string> SuperAdministratorPermissions =>
        Permissions.AllKeys.Order(StringComparer.Ordinal).ToArray();

    private static readonly string[] ItAdministratorPermissions =
    [
        Permissions.Device.View,
        Permissions.Device.Restart,
        Permissions.Device.Shutdown,
        Permissions.Device.Lock,
        Permissions.Device.SignOutUser,
        Permissions.Device.Enroll,
        Permissions.Device.Retire,
        Permissions.Device.RefreshInventory,
        // Not granted to Helpdesk: renaming is harmless to the endpoint but a
        // device that quietly changes name in the console is confusing to
        // everyone else looking at it, so it stays with the role that owns the
        // estate's naming. Auditor is read-only and never gets it.
        Permissions.Device.Rename,
        Permissions.Usb.View,
        Permissions.Usb.Manage,
        Permissions.Driver.View,
        // Deliberately not granted to Helpdesk. Installing a driver is putting kernel
        // code on a machine, which is a change-control decision rather than a support
        // action -- and Helpdesk can already see the fault and escalate it.
        Permissions.Driver.Manage,
        Permissions.BitLocker.View,
        // Deliberately not granted to Helpdesk. A recovery password unlocks a
        // machine's disk outright; filing one and reading one back are both
        // change-control decisions rather than support actions.
        Permissions.BitLocker.RecoveryKeyManage,
        Permissions.BitLocker.RecoveryKeyRead,
        Permissions.LocalUser.View,
        Permissions.LocalUser.Create,
        Permissions.LocalUser.Delete,
        Permissions.LocalUser.Disable,
        Permissions.LocalUser.ResetPassword,
        Permissions.LocalUser.ChangeType,
        // Deliberately not granted to Helpdesk. Handing out administrator rights,
        // even on a deadline, is an authorization decision rather than a support
        // action -- and Helpdesk already holds enough to reset a password and get
        // someone working again without it.
        Permissions.LocalUser.Elevate,
        Permissions.LocalUser.ForcePasswordChange,
        Permissions.Group.View,
        Permissions.Group.Manage,
        Permissions.Software.View,
        Permissions.Software.Deploy,
        Permissions.Chrome.View,
        // Deliberately not granted to Helpdesk. Removing or forcing a browser
        // extension reaches into every user's signed-in web session on the
        // machine, which is a change-control decision rather than a support
        // action -- and Helpdesk can already see the extension and escalate it.
        Permissions.Chrome.Manage,
        Permissions.Policy.View,
        Permissions.Policy.Create,
        Permissions.Policy.Assign,
        Permissions.Task.View,
        Permissions.Task.Execute,
        Permissions.Audit.View,
        Permissions.Platform.EnrollmentTokenView,
        Permissions.Platform.EnrollmentTokenIssue,
        Permissions.Platform.EnrollmentTokenRevoke,
    ];

    private static readonly string[] HelpdeskPermissions =
    [
        Permissions.Device.View,
        Permissions.Device.Restart,
        Permissions.Device.Lock,
        Permissions.Device.RefreshInventory,
        // Helpdesk can see which stick is plugged into which machine — that is
        // half of every "my USB drive isn't showing up" call — but cannot open
        // access to it. Granting a data path off an endpoint is a security
        // decision, so it stays with IT Administrator.
        Permissions.Usb.View,
        // Reading why a device is not working is first-line diagnosis, and the
        // permission grants nothing but reading. Changing a driver will be a
        // separate permission that Helpdesk does not get.
        Permissions.Driver.View,
        // Knowing whether a laptop is encrypted is a support question as often as a
        // security one. Reading it grants nothing: encrypting, suspending and
        // decrypting are separate permissions Helpdesk does not hold.
        Permissions.BitLocker.View,
        Permissions.LocalUser.View,
        Permissions.LocalUser.Disable,
        Permissions.LocalUser.ResetPassword,
        Permissions.LocalUser.ForcePasswordChange,
        Permissions.Group.View,
        Permissions.Software.View,
        // "Which extension broke the browser" is a first-line question, and the
        // permission grants nothing but reading. Changing what Chrome runs is
        // chrome.manage, which Helpdesk does not hold.
        Permissions.Chrome.View,
        Permissions.Policy.View,
        Permissions.Task.View,
    ];

    private static readonly string[] AuditorPermissions =
    [
        Permissions.Device.View,
        // Read-only, like everything else Auditor holds. usb.manage is
        // deliberately absent: it is the one permission in this area that opens
        // a data path, and Auditor mutates nothing.
        Permissions.Usb.View,
        Permissions.Driver.View,
        Permissions.BitLocker.View,
        Permissions.LocalUser.View,
        Permissions.Group.View,
        Permissions.Software.View,
        // Read-only, like everything else Auditor holds: which extensions are on
        // which machines is exactly the kind of fact an audit asks for.
        // chrome.manage is deliberately absent, as every mutating permission is.
        Permissions.Chrome.View,
        Permissions.Policy.View,
        Permissions.Task.View,
        Permissions.Audit.View,
        Permissions.Platform.UserView,
        Permissions.Platform.EnrollmentTokenView,
    ];

    public static FrozenDictionary<string, SystemRoleDefinition> All { get; } =
        new SystemRoleDefinition[]
        {
            new(
                SuperAdministrator,
                "Super Administrator",
                "Unrestricted access to every platform capability, including platform user and role management.",
                SuperAdministratorPermissions),
            new(
                ItAdministrator,
                "IT Administrator",
                "Day-to-day endpoint administration: devices, local accounts, groups, software, policies and tasks.",
                ItAdministratorPermissions),
            new(
                Helpdesk,
                "Helpdesk",
                "First-line support: view everything operational, restart and lock devices, reset and disable local accounts.",
                HelpdeskPermissions),
            new(
                Auditor,
                "Auditor",
                "Strictly read-only access, including the audit log. Holds no permission that mutates state.",
                AuditorPermissions),
        }.ToFrozenDictionary(r => r.Key, StringComparer.Ordinal);
}

public sealed record SystemRoleDefinition(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> PermissionKeys);
