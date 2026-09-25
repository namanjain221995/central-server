using EndpointPlatform.Domain.Authorization;

namespace EndpointPlatform.Domain.Tests.Authorization;

/// <summary>
/// Locks down what each built-in role may and may not do.
/// </summary>
/// <remarks>
/// These are security tests, not coverage tests. The failure they exist to prevent
/// is a future edit quietly widening a role - adding <c>device.shutdown</c> to
/// Helpdesk, or any mutating permission to Auditor - which would not break a single
/// functional test but would silently change who can take an endpoint offline.
/// </remarks>
public sealed class SystemRoleTests
{
    [Fact]
    public void All_defines_exactly_the_four_documented_roles()
    {
        SystemRoles.All.Keys.ShouldBe(
            [
                SystemRoles.SuperAdministrator,
                SystemRoles.ItAdministrator,
                SystemRoles.Helpdesk,
                SystemRoles.Auditor,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Every_role_grants_only_permissions_that_exist_in_the_catalogue()
    {
        foreach (var (roleKey, definition) in SystemRoles.All)
        {
            foreach (var permissionKey in definition.PermissionKeys)
            {
                Permissions.IsKnown(permissionKey).ShouldBeTrue(
                    $"Role '{roleKey}' grants '{permissionKey}', which is not in the permission catalogue. " +
                    "Seeding would throw at startup.");
            }
        }
    }

    [Fact]
    public void Super_administrator_holds_every_permission()
    {
        var superAdmin = SystemRoles.All[SystemRoles.SuperAdministrator];

        superAdmin.PermissionKeys.ShouldBe(Permissions.AllKeys, ignoreOrder: true);
    }

    /// <summary>
    /// Auditor must be read-only. Rather than listing what it may not have (which a
    /// new permission would silently escape), this asserts the inverse: every
    /// permission Auditor holds must be one that is not marked high-risk and whose
    /// key denotes a read.
    /// </summary>
    [Fact]
    public void Auditor_holds_no_mutating_permission()
    {
        var auditor = SystemRoles.All[SystemRoles.Auditor];
        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        foreach (var key in auditor.PermissionKeys)
        {
            var definition = catalogue[key];

            definition.HighRisk.ShouldBeFalse(
                $"Auditor is a read-only role but holds high-risk permission '{key}'.");

            key.ShouldEndWith(
                ".view",
                Case.Sensitive,
                $"Auditor holds '{key}', which is not a view permission. Auditor must not mutate state.");
        }
    }

    [Fact]
    public void Auditor_can_read_the_audit_log()
    {
        SystemRoles.All[SystemRoles.Auditor].PermissionKeys.ShouldContain(Permissions.Audit.View);
    }

    [Theory]
    [InlineData(Permissions.LocalUser.ChangeType)]
    [InlineData(Permissions.LocalUser.Delete)]
    [InlineData(Permissions.LocalUser.Create)]
    [InlineData(Permissions.Device.Shutdown)]
    [InlineData(Permissions.Device.Retire)]
    [InlineData(Permissions.Group.Manage)]
    [InlineData(Permissions.Software.Deploy)]
    [InlineData(Permissions.Policy.Assign)]
    [InlineData(Permissions.Task.Execute)]
    // Helpdesk can see which USB device is on which machine, because that is
    // half of every "my drive isn't showing up" call. Opening a data path off
    // an endpoint is a security decision and stays with IT Administrator.
    [InlineData(Permissions.Usb.Manage)]
    // Helpdesk can see which extension is in which browser, because that is how
    // "Chrome is broken" calls get diagnosed. Removing or forcing one reaches
    // into every user's web session and stays with IT Administrator.
    [InlineData(Permissions.Chrome.Manage)]
    [InlineData(Permissions.Platform.UserManage)]
    [InlineData(Permissions.Platform.RoleManage)]
    [InlineData(Permissions.Platform.SettingsManage)]
    public void Helpdesk_is_denied_high_impact_permissions(string permissionKey)
    {
        SystemRoles.All[SystemRoles.Helpdesk].PermissionKeys.ShouldNotContain(
            permissionKey,
            $"Helpdesk must not be able to perform '{permissionKey}'.");
    }

    [Theory]
    [InlineData(Permissions.Platform.UserManage)]
    [InlineData(Permissions.Platform.RoleManage)]
    [InlineData(Permissions.Platform.SettingsManage)]
    public void It_administrator_cannot_manage_platform_users_or_roles(string permissionKey)
    {
        // Separation of duties: an IT administrator manages endpoints, not who is
        // allowed to manage endpoints. Granting themselves more access must require
        // a Super Administrator.
        SystemRoles.All[SystemRoles.ItAdministrator].PermissionKeys.ShouldNotContain(permissionKey);
    }

    [Fact]
    public void It_administrator_can_perform_core_endpoint_administration()
    {
        var itAdmin = SystemRoles.All[SystemRoles.ItAdministrator].PermissionKeys;

        itAdmin.ShouldContain(Permissions.Device.View);
        itAdmin.ShouldContain(Permissions.Device.Restart);
        itAdmin.ShouldContain(Permissions.LocalUser.ChangeType);
        itAdmin.ShouldContain(Permissions.Group.Manage);
        itAdmin.ShouldContain(Permissions.Software.Deploy);
        itAdmin.ShouldContain(Permissions.Chrome.Manage);
        itAdmin.ShouldContain(Permissions.Task.Execute);
    }

    /// <summary>
    /// Reading the Chrome inventory is diagnosis, so every operational role holds
    /// it; changing what Chrome runs reaches into every user's browser session, so
    /// only the roles trusted to change the estate hold that.
    /// </summary>
    /// <remarks>
    /// The last assertion is the one that matters over time: it fails the moment
    /// somebody adds a chrome permission without deciding, here, which roles hold
    /// it, rather than letting it inherit a default.
    /// </remarks>
    [Fact]
    public void Chrome_visibility_is_read_only_and_management_stays_with_administrators()
    {
        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        catalogue[Permissions.Chrome.View].HighRisk.ShouldBeFalse();
        catalogue[Permissions.Chrome.Manage].HighRisk.ShouldBeTrue();

        foreach (var role in new[]
                 {
                     SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator,
                     SystemRoles.Helpdesk, SystemRoles.Auditor,
                 })
        {
            SystemRoles.All[role].PermissionKeys.ShouldContain(
                Permissions.Chrome.View, $"Role '{role}' should be able to see the Chrome inventory.");
        }

        var holders = SystemRoles.All
            .Where(r => r.Value.PermissionKeys.Contains(Permissions.Chrome.Manage, StringComparer.Ordinal))
            .Select(r => r.Key)
            .ToArray();

        holders.ShouldBe([SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator], ignoreOrder: true);

        Permissions.AllKeys
            .Where(k => k.StartsWith("chrome.", StringComparison.Ordinal))
            .ShouldBe(
                [Permissions.Chrome.View, Permissions.Chrome.Manage],
                ignoreOrder: true,
                customMessage: "a Chrome permission was added without deciding which roles hold it");
    }

    /// <summary>
    /// Only IT Administrator and Super Administrator may open USB storage access.
    /// </summary>
    /// <remarks>
    /// Stated as a whole-catalogue assertion rather than one negative per role, so
    /// that a role added later cannot pick up <c>usb.manage</c> without this
    /// failing. The permission's entire effect is to make a removable disk
    /// readable on a managed endpoint, which is a data-egress decision.
    /// </remarks>
    [Fact]
    public void Only_administrators_can_grant_usb_storage_access()
    {
        var holders = SystemRoles.All
            .Where(r => r.Value.PermissionKeys.Contains(Permissions.Usb.Manage, StringComparer.Ordinal))
            .Select(r => r.Key)
            .ToArray();

        holders.ShouldBe([SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator], ignoreOrder: true);
    }

    [Fact]
    public void Usb_visibility_is_read_only_and_therefore_safe_for_auditor()
    {
        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        catalogue[Permissions.Usb.View].HighRisk.ShouldBeFalse();
        catalogue[Permissions.Usb.Manage].HighRisk.ShouldBeTrue();

        SystemRoles.All[SystemRoles.Auditor].PermissionKeys.ShouldContain(Permissions.Usb.View);
    }

    /// <summary>
    /// Reading why a device is not working is diagnosis, so every operational role
    /// holds it.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than the USB split above, because the permission grants
    /// nothing but reading -- there is no driver mutation behind it. Changing a
    /// driver will arrive as its own permission, and this test will not grant it.
    /// </remarks>
    [Fact]
    public void Driver_visibility_is_read_only_and_held_by_every_operational_role()
    {
        Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal)[Permissions.Driver.View]
            .HighRisk.ShouldBeFalse();

        foreach (var role in new[]
                 {
                     SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator,
                     SystemRoles.Helpdesk, SystemRoles.Auditor,
                 })
        {
            SystemRoles.All[role].PermissionKeys.ShouldContain(
                Permissions.Driver.View, $"Role '{role}' should be able to see driver health.");
        }
    }

    /// <summary>
    /// Reading encryption state is safe for every operational role; changing it is
    /// not, and no permission that could change it exists yet.
    /// </summary>
    /// <remarks>
    /// The second assertion is the one that matters over time. Encryption, suspension
    /// and above all decryption are separate decisions, and this test fails the moment
    /// somebody adds a bitlocker permission that mutates without also deciding, here,
    /// which roles may hold it.
    /// </remarks>
    [Fact]
    public void Bitlocker_visibility_is_read_only_and_is_the_only_bitlocker_permission()
    {
        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        catalogue[Permissions.BitLocker.View].HighRisk.ShouldBeFalse();

        foreach (var role in new[]
                 {
                     SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator,
                     SystemRoles.Helpdesk, SystemRoles.Auditor,
                 })
        {
            SystemRoles.All[role].PermissionKeys.ShouldContain(
                Permissions.BitLocker.View, $"Role '{role}' should be able to see encryption state.");
        }

        // Every BitLocker permission must be listed here with a deliberate role
        // decision. This assertion has already done its job once: it failed when
        // recovery-key escrow added two permissions, which is exactly the moment
        // somebody has to decide who may hold them rather than inheriting a
        // default.
        Permissions.AllKeys
            .Where(k => k.StartsWith("bitlocker.", StringComparison.Ordinal))
            .ShouldBe(
                [
                    Permissions.BitLocker.RecoveryKeyManage,
                    Permissions.BitLocker.RecoveryKeyRead,
                    Permissions.BitLocker.View,
                ],
                ignoreOrder: true,
                customMessage: "a BitLocker permission was added without deciding which roles hold it");
    }

    /// <summary>
    /// Reading an escrowed recovery password unlocks a machine's disk outright,
    /// so it stays with the roles trusted to change the estate.
    /// </summary>
    [Fact]
    public void Only_administrators_can_escrow_or_reveal_recovery_keys()
    {
        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        catalogue[Permissions.BitLocker.RecoveryKeyRead].HighRisk.ShouldBeTrue();
        catalogue[Permissions.BitLocker.RecoveryKeyManage].HighRisk.ShouldBeTrue();

        foreach (var permission in new[]
                 {
                     Permissions.BitLocker.RecoveryKeyRead,
                     Permissions.BitLocker.RecoveryKeyManage,
                 })
        {
            var holders = SystemRoles.All
                .Where(r => r.Value.PermissionKeys.Contains(permission, StringComparer.Ordinal))
                .Select(r => r.Key)
                .ToArray();

            holders.ShouldBe(
                [SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator],
                ignoreOrder: true,
                customMessage: $"'{permission}' must not reach Helpdesk or Auditor");
        }
    }

    /// <summary>
    /// Seeing that a machine is encrypted must never imply being able to read the
    /// key that decrypts it.
    /// </summary>
    [Fact]
    public void Bitlocker_view_does_not_imply_recovery_key_access()
    {
        foreach (var role in new[] { SystemRoles.Helpdesk, SystemRoles.Auditor })
        {
            var permissions = SystemRoles.All[role].PermissionKeys;

            permissions.ShouldContain(Permissions.BitLocker.View);
            permissions.ShouldNotContain(Permissions.BitLocker.RecoveryKeyRead);
            permissions.ShouldNotContain(Permissions.BitLocker.RecoveryKeyManage);
        }
    }

    /// <summary>
    /// Installing a driver is putting kernel code on a machine, so it stays with the
    /// roles trusted to change the estate rather than the ones trusted to support it.
    /// </summary>
    [Fact]
    public void Only_administrators_can_install_drivers()
    {
        Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal)[Permissions.Driver.Manage]
            .HighRisk.ShouldBeTrue();

        var holders = SystemRoles.All
            .Where(r => r.Value.PermissionKeys.Contains(Permissions.Driver.Manage, StringComparer.Ordinal))
            .Select(r => r.Key)
            .ToArray();

        holders.ShouldBe([SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator], ignoreOrder: true);
    }

    /// <summary>
    /// Seeing a driver fault and being able to replace the driver are separate
    /// grants, and the split must survive future edits to either role.
    /// </summary>
    [Fact]
    public void Helpdesk_can_see_driver_health_but_not_install_drivers()
    {
        var helpdesk = SystemRoles.All[SystemRoles.Helpdesk].PermissionKeys;

        helpdesk.ShouldContain(Permissions.Driver.View);
        helpdesk.ShouldNotContain(Permissions.Driver.Manage);
    }

    [Fact]
    public void No_role_definition_contains_duplicate_permissions()
    {
        foreach (var (roleKey, definition) in SystemRoles.All)
        {
            definition.PermissionKeys.Distinct(StringComparer.Ordinal).Count()
                .ShouldBe(definition.PermissionKeys.Count, $"Role '{roleKey}' lists a permission twice.");
        }
    }
}
