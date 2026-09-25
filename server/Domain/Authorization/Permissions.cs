using System.Collections.Frozen;

namespace EndpointPlatform.Domain.Authorization;

/// <summary>
/// The canonical catalogue of permissions understood by the platform.
/// </summary>
/// <remarks>
/// <para>
/// Authorisation is permission-based, never role-name-based. Nothing outside this
/// file should contain a bare permission string: policies, attributes, seeding and
/// tests all reference these constants, so a typo becomes a compile error instead
/// of a silently-open endpoint.
/// </para>
/// <para>
/// <see cref="All"/> is the source of truth used to seed the <c>permissions</c>
/// table. A permission that exists in the database but not here is stale; the
/// reverse means seeding has not been run.
/// </para>
/// </remarks>
public static class Permissions
{
    public static class Device
    {
        public const string View = "device.view";
        public const string Restart = "device.restart";
        public const string Shutdown = "device.shutdown";
        public const string Lock = "device.lock";
        public const string SignOutUser = "device.sign_out_user";
        public const string Enroll = "device.enroll";
        public const string Retire = "device.retire";
        public const string RefreshInventory = "device.refresh_inventory";
        public const string Rename = "device.rename";
    }

    /// <summary>
    /// USB and peripheral control. Split from <see cref="Device"/> so that
    /// granting someone the ability to restart machines does not also grant them
    /// the ability to open a data path off those machines.
    /// </summary>
    public static class Usb
    {
        /// <summary>See the peripheral inventory and current access states.</summary>
        public const string View = "usb.view";

        /// <summary>Grant, revoke and re-apply USB storage access. Never held by Auditor.</summary>
        public const string Manage = "usb.manage";
    }

    /// <summary>
    /// Device drivers and driver health. Split from <see cref="Device"/> because
    /// reading which machines have a faulted device is a diagnostic activity that
    /// belongs to first-line support, while changing a driver is not.
    /// </summary>
    public static class Driver
    {
        /// <summary>See the driver inventory and each device's health verdict.</summary>
        public const string View = "driver.view";

        /// <summary>
        /// Approve driver packages and install them on endpoints.
        /// </summary>
        /// <remarks>
        /// High risk, and separate from <see cref="View"/>, because a driver runs in
        /// the kernel: this is the most privileged code this platform can put on a
        /// machine. Reading why a device is unhealthy is a support activity; deciding
        /// what kernel code it runs is not.
        /// </remarks>
        public const string Manage = "driver.manage";
    }

    /// <summary>
    /// BitLocker volume encryption. Only the read half exists so far: encrypting,
    /// suspending and above all decrypting a volume are separate decisions that will
    /// arrive as their own permissions rather than being folded into this one.
    /// </summary>
    public static class BitLocker
    {
        /// <summary>See volume encryption state and readiness. Never a recovery key.</summary>
        public const string View = "bitlocker.view";

        /// <summary>
        /// Escrow, replace and delete a stored BitLocker recovery password.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="View"/> because seeing that a key is escrowed
        /// and being able to change what is escrowed are different decisions.
        /// </remarks>
        public const string RecoveryKeyManage = "bitlocker.recovery_key.manage";

        /// <summary>
        /// Retrieve the plaintext of an escrowed recovery password.
        /// </summary>
        /// <remarks>
        /// The most dangerous read in the platform: it hands over the key to a
        /// machine's disk. Deliberately its own permission rather than part of
        /// <see cref="View"/> or <see cref="RecoveryKeyManage"/> -- somebody who
        /// files keys for safekeeping is not necessarily somebody who may read
        /// them back, and every reveal is separately audited and rate limited.
        /// </remarks>
        public const string RecoveryKeyRead = "bitlocker.recovery_key.read";
    }

    public static class LocalUser
    {
        public const string View = "user.view";
        public const string Create = "user.create";
        public const string Delete = "user.delete";
        public const string Disable = "user.disable";
        public const string ResetPassword = "user.reset_password";
        public const string ChangeType = "user.change_type";
        public const string ForcePasswordChange = "user.force_password_change";

        /// <summary>
        /// Grant, approve and revoke temporary local administrator rights.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ChangeType"/>, which permanently changes an
        /// account. This one hands out administrator rights on a deadline, and the
        /// two are different decisions with different blast radii -- somebody
        /// trusted to grant a two-hour window is not necessarily trusted to make an
        /// account an administrator forever, and the reverse is also true.
        /// </remarks>
        public const string Elevate = "localuser.elevate";
    }

    public static class Group
    {
        public const string View = "group.view";
        public const string Manage = "group.manage";
    }

    public static class Software
    {
        public const string View = "software.view";
        public const string Deploy = "software.deploy";
    }

    /// <summary>
    /// Google Chrome on managed endpoints: the installation, its profiles and their
    /// extensions. Split from <see cref="Software"/> because a browser extension is
    /// a different kind of risk from an installed application -- it runs inside the
    /// session that holds the user's web credentials -- and the people who read the
    /// extension inventory are not necessarily the people who deploy packages.
    /// </summary>
    public static class Chrome
    {
        /// <summary>See Chrome installations, profiles and extensions. Reading only.</summary>
        public const string View = "chrome.view";

        /// <summary>
        /// Change what Chrome runs on a device: remove or block extensions, force
        /// an update. High risk because it reaches into every user's browser
        /// session on the machine. Nothing behind it exists yet; the permission is
        /// declared now so the role decision is made once, deliberately, rather
        /// than inherited by whichever phase first needs it.
        /// </summary>
        public const string Manage = "chrome.manage";
    }

    public static class Policy
    {
        public const string View = "policy.view";
        public const string Create = "policy.create";
        public const string Assign = "policy.assign";
    }

    public static class Task
    {
        public const string View = "task.view";
        public const string Execute = "task.execute";
    }

    public static class Audit
    {
        public const string View = "audit.view";
    }

    public static class Platform
    {
        public const string UserView = "platform.user.view";
        public const string UserManage = "platform.user.manage";
        public const string RoleManage = "platform.role.manage";
        public const string EnrollmentTokenView = "platform.enrollment_token.view";
        public const string EnrollmentTokenIssue = "platform.enrollment_token.issue";
        public const string EnrollmentTokenRevoke = "platform.enrollment_token.revoke";
        public const string SettingsManage = "platform.settings.manage";
    }

    /// <summary>Every permission the platform knows about, in seed order.</summary>
    public static readonly FrozenSet<PermissionDefinition> All = new PermissionDefinition[]
    {
        new(Device.View, "Devices", "View devices and device details", HighRisk: false),
        new(Device.Restart, "Devices", "Restart a device", HighRisk: true),
        new(Device.Shutdown, "Devices", "Shut down a device", HighRisk: true),
        new(Device.Lock, "Devices", "Lock a device session", HighRisk: false),
        new(Device.SignOutUser, "Devices", "Sign out the interactive user on a device", HighRisk: true),
        new(Device.Enroll, "Devices", "Approve or complete device enrollment", HighRisk: true),
        new(Device.Retire, "Devices", "Retire a device and revoke its credential", HighRisk: true),
        new(Device.RefreshInventory, "Devices", "Request an out-of-band inventory refresh", HighRisk: false),
        // Labelling only. It cannot rename Windows, move a device between
        // organizations, or alter how the machine authenticates.
        new(Device.Rename, "Devices", "Set the console display name for a device", HighRisk: false),

        new(Usb.View, "Peripherals", "View connected USB devices and their access state", HighRisk: false),
        // High risk on purpose: the only thing this permission can do is open a
        // read path off a removable device that is otherwise closed.
        new(Usb.Manage, "Peripherals", "Grant and revoke temporary USB storage access", HighRisk: true),

        new(Driver.View, "Drivers", "View device drivers and driver health", HighRisk: false),
        new(Driver.Manage, "Drivers", "Approve driver packages and install them on devices", HighRisk: true),

        new(BitLocker.View, "BitLocker", "View volume encryption state and readiness", HighRisk: false),
        new(BitLocker.RecoveryKeyManage, "BitLocker",
            "Escrow, replace and delete stored BitLocker recovery passwords", HighRisk: true),
        new(BitLocker.RecoveryKeyRead, "BitLocker",
            "Reveal a stored BitLocker recovery password", HighRisk: true),

        new(LocalUser.View, "Local accounts", "View Windows local users on a device", HighRisk: false),
        new(LocalUser.Create, "Local accounts", "Create a Windows local user", HighRisk: true),
        new(LocalUser.Delete, "Local accounts", "Delete a Windows local user", HighRisk: true),
        new(LocalUser.Disable, "Local accounts", "Enable or disable a Windows local user", HighRisk: true),
        new(LocalUser.ResetPassword, "Local accounts", "Reset a Windows local user password", HighRisk: true),
        new(LocalUser.ChangeType, "Local accounts", "Change account type (standard/administrator)", HighRisk: true),
        new(LocalUser.ForcePasswordChange, "Local accounts", "Force a password change at next logon", HighRisk: true),
        new(LocalUser.Elevate, "Local accounts", "Grant temporary local administrator rights", HighRisk: true),

        new(Group.View, "Groups", "View device groups and Windows local groups", HighRisk: false),
        new(Group.Manage, "Groups", "Create, modify and delete groups and memberships", HighRisk: true),

        new(Software.View, "Software", "View software inventory", HighRisk: false),
        new(Software.Deploy, "Software", "Deploy approved software packages", HighRisk: true),

        new(Chrome.View, "Chrome", "View Chrome installations, profiles and extensions", HighRisk: false),
        new(Chrome.Manage, "Chrome", "Manage Chrome extensions and updates on devices", HighRisk: true),

        new(Policy.View, "Policies", "View policies and compliance results", HighRisk: false),
        new(Policy.Create, "Policies", "Create and version policies", HighRisk: false),
        new(Policy.Assign, "Policies", "Assign policies to devices and groups", HighRisk: true),

        new(Task.View, "Tasks", "View queued and completed tasks", HighRisk: false),
        new(Task.Execute, "Tasks", "Queue a task for execution on a device", HighRisk: true),

        new(Audit.View, "Audit", "Read the audit log", HighRisk: false),

        new(Platform.UserView, "Platform", "View platform administrator accounts", HighRisk: false),
        new(Platform.UserManage, "Platform", "Create, modify and disable platform administrators", HighRisk: true),
        new(Platform.RoleManage, "Platform", "Assign roles and manage role permissions", HighRisk: true),
        new(Platform.EnrollmentTokenView, "Platform", "View enrollment tokens (never their secret)", HighRisk: false),
        new(Platform.EnrollmentTokenIssue, "Platform", "Issue a new agent enrollment token", HighRisk: true),
        new(Platform.EnrollmentTokenRevoke, "Platform", "Revoke an agent enrollment token", HighRisk: false),
        new(Platform.SettingsManage, "Platform", "Change platform-wide settings", HighRisk: true),
    }.ToFrozenSet();

    public static readonly FrozenSet<string> AllKeys =
        All.Select(p => p.Key).ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string permissionKey) => AllKeys.Contains(permissionKey);
}

/// <summary>
/// Metadata for one permission. <paramref name="HighRisk"/> marks operations that
/// change security posture on an endpoint; the UI requires explicit confirmation
/// for these and the audit log records them at elevated severity.
/// </summary>
public sealed record PermissionDefinition(string Key, string Category, string Description, bool HighRisk);
