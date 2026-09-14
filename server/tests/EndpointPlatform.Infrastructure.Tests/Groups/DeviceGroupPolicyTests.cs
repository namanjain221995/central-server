using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Groups;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Policies;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Groups;

/// <summary>
/// Group membership + group-targeted policy resolution against real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceGroupPolicyTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static AuditWriter Audit(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, TimeProvider.System, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static DeviceGroupService Groups(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, Audit(db), TimeProvider.System, new DeviceScopeAuthorizer(db),
            Options.Create(new AgentServerOptions()));

    /// <summary>An organization, an all-device-scope administrator and two devices in "All Devices".</summary>
    private static async Task<(Organization Org, PlatformUser Admin, Device First, Device Second)> SeedAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db, string firstHostname, string secondHostname)
    {
        var org = new Organization("G", ("g" + Guid.CreateVersion7().ToString("N"))[..18]);
        db.Organizations.Add(org);

        var admin = new PlatformUser(org.Id, $"admin-{Guid.CreateVersion7():N}@test.local", "Admin");
        admin.GrantAllDeviceScope();
        db.PlatformUsers.Add(admin);

        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var first = Device.Enroll(org.Id, firstHostname, "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, DateTimeOffset.UtcNow);
        var second = Device.Enroll(org.Id, secondHostname, "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.AddRange(first, second);
        await db.SaveChangesAsync();

        return (org, admin, first, second);
    }

    [Fact]
    public async Task A_group_targeted_policy_is_effective_for_group_members_only()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, admin, inGroup, outGroup) = await SeedAsync(db, "IN", "OUT");

        var created = await Groups(db).CreateAsync(org.Id, admin.Id, admin.Email, "Finance", "d", [inGroup.Id]);
        created.Status.ShouldBe(GroupChangeStatus.Ok);
        var groupId = created.GroupId!.Value;

        var policyService = new PolicyService(db, Audit(db), TimeProvider.System);
        var policy = await policyService.CreateAsync(
            org.Id, PolicyType.ScreenLockTimeout, "Lock", "d", """{"maxTimeoutSeconds":600}""",
            admin.Id, "admin", CancellationToken.None);

        // Assign to the GROUP, not the device.
        db.PolicyAssignments.Add(new PolicyAssignment(org.Id, policy.Id, PolicyAssignmentTarget.Group, groupId));
        await db.SaveChangesAsync();

        var forMember = await policyService.GetEffectivePoliciesAsync(inGroup.Id, CancellationToken.None);
        var forNonMember = await policyService.GetEffectivePoliciesAsync(outGroup.Id, CancellationToken.None);

        forMember.ShouldHaveSingleItem().Policy.Id.ShouldBe(policy.Id);
        forNonMember.ShouldBeEmpty("a group-targeted policy must not reach non-members");
    }

    [Fact]
    public async Task Removing_a_member_removes_the_group_policy_from_it()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, admin, device, _) = await SeedAsync(db, "D", "OTHER");

        var groupId = (await Groups(db).CreateAsync(org.Id, admin.Id, admin.Email, "HR", "d", [device.Id])).GroupId!.Value;

        var policyService = new PolicyService(db, Audit(db), TimeProvider.System);
        var policy = await policyService.CreateAsync(org.Id, PolicyType.ScreenLockTimeout, "L", "d",
            """{"maxTimeoutSeconds":300}""", admin.Id, "admin", CancellationToken.None);
        db.PolicyAssignments.Add(new PolicyAssignment(org.Id, policy.Id, PolicyAssignmentTarget.Group, groupId));
        await db.SaveChangesAsync();

        (await policyService.GetEffectivePoliciesAsync(device.Id, CancellationToken.None)).Count.ShouldBe(1);

        var removed = await Groups(db).RemoveDevicesAsync(org.Id, admin.Id, admin.Email, groupId, [device.Id]);
        removed.Devices.ShouldHaveSingleItem().Outcome.ShouldBe(GroupDeviceOutcome.Moved);

        (await policyService.GetEffectivePoliciesAsync(device.Id, CancellationToken.None))
            .ShouldBeEmpty("removing the device from the group removes the group's policy from it");
    }

    /// <summary>
    /// Exclusive membership changes one thing about group policy: moving a
    /// device takes it out of the old group's policies and into the new one's.
    /// </summary>
    [Fact]
    public async Task Moving_a_device_swaps_one_groups_policy_for_the_others()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, admin, device, _) = await SeedAsync(db, "MOVER", "OTHER");
        var service = Groups(db);

        var finance = (await service.CreateAsync(org.Id, admin.Id, admin.Email, "Finance", null, [device.Id])).GroupId!.Value;
        var sales = (await service.CreateAsync(org.Id, admin.Id, admin.Email, "Sales", null, [])).GroupId!.Value;

        var policyService = new PolicyService(db, Audit(db), TimeProvider.System);
        var financePolicy = await policyService.CreateAsync(org.Id, PolicyType.ScreenLockTimeout, "F", "d",
            """{"maxTimeoutSeconds":300}""", admin.Id, "admin", CancellationToken.None);
        var salesPolicy = await policyService.CreateAsync(org.Id, PolicyType.ScreenLockTimeout, "S", "d",
            """{"maxTimeoutSeconds":900}""", admin.Id, "admin", CancellationToken.None);
        db.PolicyAssignments.Add(new PolicyAssignment(org.Id, financePolicy.Id, PolicyAssignmentTarget.Group, finance));
        db.PolicyAssignments.Add(new PolicyAssignment(org.Id, salesPolicy.Id, PolicyAssignmentTarget.Group, sales));
        await db.SaveChangesAsync();

        (await policyService.GetEffectivePoliciesAsync(device.Id, CancellationToken.None))
            .ShouldHaveSingleItem().Policy.Id.ShouldBe(financePolicy.Id);

        await service.AddDevicesAsync(org.Id, admin.Id, admin.Email, sales, [device.Id]);

        (await policyService.GetEffectivePoliciesAsync(device.Id, CancellationToken.None))
            .ShouldHaveSingleItem("a device in exactly one group receives exactly one group's policies")
            .Policy.Id.ShouldBe(salesPolicy.Id);
    }
}
