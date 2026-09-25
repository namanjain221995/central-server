using System.Reflection;
using EndpointPlatform.Domain.Authorization;

namespace EndpointPlatform.Domain.Tests.Authorization;

public sealed class PermissionCatalogueTests
{
    /// <summary>
    /// Every <c>const string</c> declared under <see cref="Permissions"/> must appear
    /// in <see cref="Permissions.All"/>.
    /// </summary>
    /// <remarks>
    /// Without this, someone can add a permission constant, guard an endpoint with
    /// it, and ship - and because the permission is never seeded, no role can ever
    /// hold it. The endpoint would be unreachable by every user including Super
    /// Administrator, which typically surfaces as a confusing production 403.
    /// </remarks>
    [Fact]
    public void Every_declared_permission_constant_is_present_in_the_catalogue()
    {
        var declared = DeclaredPermissionConstants().ToArray();

        declared.ShouldNotBeEmpty();

        foreach (var (containingType, fieldName, value) in declared)
        {
            Permissions.IsKnown(value).ShouldBeTrue(
                $"Permissions.{containingType}.{fieldName} = \"{value}\" is declared but missing from " +
                "Permissions.All, so it will never be seeded and no role can hold it.");
        }
    }

    [Fact]
    public void Catalogue_contains_no_permission_that_lacks_a_declared_constant()
    {
        var declaredValues = DeclaredPermissionConstants()
            .Select(x => x.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var definition in Permissions.All)
        {
            declaredValues.ShouldContain(
                definition.Key,
                $"'{definition.Key}' is in Permissions.All but has no constant, so authorisation code " +
                "would have to reference it as a magic string.");
        }
    }

    [Fact]
    public void Permission_keys_are_unique()
    {
        var keys = Permissions.All.Select(p => p.Key).ToArray();

        keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(keys.Length);
    }

    [Fact]
    public void Permission_keys_use_lowercase_dotted_notation()
    {
        foreach (var definition in Permissions.All)
        {
            definition.Key.ShouldBe(definition.Key.ToLowerInvariant());
            definition.Key.ShouldContain(".");
            definition.Key.Length.ShouldBeLessThanOrEqualTo(64);
        }
    }

    [Fact]
    public void Destructive_permissions_are_marked_high_risk()
    {
        // High-risk drives extra confirmation in the UI and elevated audit
        // severity, so miscategorising one of these weakens both.
        string[] mustBeHighRisk =
        [
            Permissions.Device.Restart,
            Permissions.Device.Shutdown,
            Permissions.Device.Retire,
            Permissions.LocalUser.Create,
            Permissions.LocalUser.Delete,
            Permissions.LocalUser.Disable,
            Permissions.LocalUser.ResetPassword,
            Permissions.LocalUser.ChangeType,
            Permissions.Group.Manage,
            Permissions.Software.Deploy,
            Permissions.Chrome.Manage,
            Permissions.Task.Execute,
            Permissions.Platform.UserManage,
            Permissions.Platform.RoleManage,
            Permissions.Platform.EnrollmentTokenIssue,
        ];

        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        foreach (var key in mustBeHighRisk)
        {
            catalogue[key].HighRisk.ShouldBeTrue($"'{key}' must be marked high-risk.");
        }
    }

    [Fact]
    public void View_permissions_are_never_high_risk()
    {
        foreach (var definition in Permissions.All.Where(p => p.Key.EndsWith(".view", StringComparison.Ordinal)))
        {
            definition.HighRisk.ShouldBeFalse($"'{definition.Key}' is a read permission and must not be high-risk.");
        }
    }

    private static IEnumerable<(string ContainingType, string FieldName, string Value)> DeclaredPermissionConstants()
    {
        foreach (var nested in typeof(Permissions).GetNestedTypes(BindingFlags.Public))
        {
            foreach (var field in nested.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                {
                    yield return (nested.Name, field.Name, (string)field.GetRawConstantValue()!);
                }
            }
        }
    }
}
