using System.Net;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Enabling and disabling a package in the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// "Disable" here means one thing and deliberately only that: the package stops
/// being deployable. Nothing is uninstalled, no device is contacted, and the
/// installed-software inventory is untouched. It is the existing withdraw flag,
/// now reversible.
/// </para>
/// <para>
/// The tests therefore spend as much effort on what must NOT happen -- no task
/// queued, no device software row altered -- as on the state transition itself.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class PackageAvailabilityTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri Withdraw(Guid id) =>
        new($"/admin/v1/packages/{id}/withdraw", UriKind.Relative);

    private static Uri Restore(Guid id) =>
        new($"/admin/v1/packages/{id}/restore", UriKind.Relative);

    private async Task<HttpClient> AdminAsync(string? roleKey = null)
    {
        var email = $"pkg-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(
                r => r.Key == (roleKey ?? SystemRoles.SuperAdministrator));

            var user = new PlatformUser(org.Id, email, "Package Admin");
            user.SetPasswordHash(
                PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var token = SecretGenerator.GenerateSecret();

        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);

            db.AdminSessions.Add(new AdminSession(
                user.Id, SecretGenerator.HashSecret(token), user.SecurityStamp,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
                sourceIp: null, userAgent: "package-availability-tests"));

            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    /// <summary>A package seeded directly; upload is covered by PackageEndpointTests.</summary>
    private async Task<Guid> SeedPackageAsync()
    {
        await using var db = _fixture.CreateDbContext();

        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var user = await db.PlatformUsers.OrderBy(u => u.CreatedAt).FirstAsync();

        var package = new Domain.Software.SoftwarePackage(
            org.Id,
            $"Test App {Guid.CreateVersion7():N}"[..24],
            "1.0.0",
            "Test Publisher",
            Domain.Software.SoftwarePackageType.WindowsInstaller,
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            "app.msi",
            2048,
            $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}",
            requiredSignerSubject: null,
            user.Id,
            user.Email);

        db.SoftwarePackages.Add(package);
        await db.SaveChangesAsync();

        return package.Id;
    }

    private async Task<bool> IsWithdrawnAsync(Guid packageId)
    {
        await using var db = _fixture.CreateDbContext();
        return (await db.SoftwarePackages.AsNoTracking().SingleAsync(p => p.Id == packageId))
            .IsWithdrawn;
    }

    // ---- the transition ----------------------------------------------------

    [Fact]
    public async Task A_new_package_starts_available()
    {
        var packageId = await SeedPackageAsync();

        (await IsWithdrawnAsync(packageId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Disabling_then_enabling_returns_the_package_to_the_catalogue()
    {
        using var client = await AdminAsync();
        var packageId = await SeedPackageAsync();

        (await client.PostAsync(Withdraw(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await IsWithdrawnAsync(packageId)).ShouldBeTrue();

        (await client.PostAsync(Restore(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await IsWithdrawnAsync(packageId)).ShouldBeFalse();
    }

    /// <summary>
    /// Both directions are idempotent, so a double click or a retried request
    /// cannot produce a different outcome from a single one.
    /// </summary>
    [Fact]
    public async Task Repeating_either_transition_is_a_no_op()
    {
        using var client = await AdminAsync();
        var packageId = await SeedPackageAsync();

        // Enabling something already available.
        (await client.PostAsync(Restore(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await IsWithdrawnAsync(packageId)).ShouldBeFalse();

        await client.PostAsync(Withdraw(packageId), null);

        // Disabling something already disabled.
        (await client.PostAsync(Withdraw(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await IsWithdrawnAsync(packageId)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_package_is_not_found()
    {
        using var client = await AdminAsync();

        (await client.PostAsync(Restore(Guid.CreateVersion7()), null)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_caller_cannot_change_availability()
    {
        var packageId = await SeedPackageAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsync(Restore(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync(Withdraw(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        (await IsWithdrawnAsync(packageId)).ShouldBeFalse();
    }

    /// <summary>
    /// Enabling carries the same permission as disabling. Both decide whether a
    /// package may be pushed to machines, so neither is a read-level action.
    /// </summary>
    [Fact]
    public async Task A_role_without_software_deploy_cannot_change_availability()
    {
        var packageId = await SeedPackageAsync();

        using var auditor = await AdminAsync(SystemRoles.Auditor);

        (await auditor.PostAsync(Withdraw(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await auditor.PostAsync(Restore(packageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        (await IsWithdrawnAsync(packageId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_package_in_another_organization_is_not_found()
    {
        Guid foreignPackageId;

        await using (var db = _fixture.CreateDbContext())
        {
            var org = new Organization("Other Org", ("p" + Guid.CreateVersion7().ToString("N"))[..20]);
            db.Organizations.Add(org);
            var user = await db.PlatformUsers.OrderBy(u => u.CreatedAt).FirstAsync();

            var package = new Domain.Software.SoftwarePackage(
                org.Id, "Foreign App", "1.0.0", "Other", Domain.Software.SoftwarePackageType.WindowsInstaller,
                Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
                "foreign.msi", 2048, $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}",
                null, user.Id, user.Email);

            db.SoftwarePackages.Add(package);
            await db.SaveChangesAsync();
            foreignPackageId = package.Id;
        }

        using var client = await AdminAsync();

        (await client.PostAsync(Restore(foreignPackageId), null)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- what must NOT happen ----------------------------------------------

    /// <summary>
    /// The defining constraint. Disabling is a catalogue decision: it must not
    /// contact a device, queue anything, or disturb what is already installed.
    /// </summary>
    [Fact]
    public async Task Changing_availability_never_queues_work_or_touches_installed_software()
    {
        using var client = await AdminAsync();
        var packageId = await SeedPackageAsync();

        int tasksBefore, softwareBefore;
        await using (var db = _fixture.CreateDbContext())
        {
            tasksBefore = await db.DeviceTasks.CountAsync();
            softwareBefore = await db.DeviceSoftware.CountAsync();
        }

        await client.PostAsync(Withdraw(packageId), null);
        await client.PostAsync(Restore(packageId), null);

        await using var after = _fixture.CreateDbContext();

        (await after.DeviceTasks.CountAsync())
            .ShouldBe(tasksBefore, "changing catalogue availability must not queue device work");

        (await after.DeviceSoftware.CountAsync())
            .ShouldBe(softwareBefore, "installed software inventory must be untouched");
    }

    /// <summary>Only the named package moves.</summary>
    [Fact]
    public async Task Only_the_intended_package_is_affected()
    {
        using var client = await AdminAsync();
        var target = await SeedPackageAsync();
        var bystander = await SeedPackageAsync();

        await client.PostAsync(Withdraw(target), null);

        (await IsWithdrawnAsync(target)).ShouldBeTrue();
        (await IsWithdrawnAsync(bystander)).ShouldBeFalse();
    }

    // ---- audit -------------------------------------------------------------

    [Fact]
    public async Task Both_transitions_are_audited_as_distinct_actions()
    {
        using var client = await AdminAsync();
        var packageId = await SeedPackageAsync();

        await client.PostAsync(Withdraw(packageId), null);
        await client.PostAsync(Restore(packageId), null);

        await using var db = _fixture.CreateDbContext();

        var actions = await db.AuditLogEntries
            .Where(a => a.TargetId == packageId.ToString())
            .Select(a => a.Action)
            .ToListAsync();

        actions.ShouldContain("software.package.withdraw");
        actions.ShouldContain("software.package.restore");
    }
}
