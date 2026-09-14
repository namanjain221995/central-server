using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// The exclusive-groups migration, run against the schema production is on
/// today and seeded with production's actual group topology.
/// </summary>
/// <remarks>
/// <para>
/// Production, read on 2026-09-14: migration head
/// <c>20260914141046_SingleActiveRestartPerDevice</c>; one organization; one
/// group, "MKT", with one device; three more active devices and one retired
/// device in no group; no administrator scope rows; no device in two groups; no
/// group named "All Devices". The first test reproduces that exactly.
/// </para>
/// <para>
/// Each test gets its own database, because they migrate up and down and seed
/// data the others would trip over. Seeding is raw SQL: below this migration
/// the tables are the old shape, which the current entity model no longer
/// describes.
/// </para>
/// </remarks>
public sealed class ExclusiveDeviceGroupsMigrationTests : IAsyncLifetime
{
    /// <summary>Production's migration head when this was written.</summary>
    private const string ProductionHead = "20260914141046_SingleActiveRestartPerDevice";

    private const string Migration = "20260914182707_ExclusiveDeviceGroups";

    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder(PostgresFixture.PostgresImage)
            .WithDatabase("postgres")
            .WithUsername("test_owner")
            .WithPassword("test_owner_password_not_a_real_secret")
            .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    // ---------------------------------------------------------------- harness

    private async Task<string> NewDatabaseAsync()
    {
        var name = "m_" + Guid.CreateVersion7().ToString("N")[..16];
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    private static EndpointPlatformDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
            })
            .Options);

    private static async Task MigrateToAsync(string connectionString, string target)
    {
        await using var db = Context(connectionString);
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> AppliedAsync(string connectionString)
    {
        await using var db = Context(connectionString);
        return (await db.Database.GetAppliedMigrationsAsync()).ToList();
    }

    /// <summary>An organization, an enrollment token and a group-less device, at the old schema.</summary>
    private static string OrganizationSql(Guid org, string slug, Guid token) => $"""
        INSERT INTO endpoint_platform.organizations (id, name, slug, is_active, created_at, updated_at)
        VALUES ('{org}', 'Org {slug}', '{slug}', true, now(), now());
        INSERT INTO endpoint_platform.enrollment_tokens
            (id, organization_id, name, secret_hash, created_by_user_id, created_by_display, expires_at, max_uses, use_count, created_at, updated_at)
        VALUES ('{token}', '{org}', 't', '{token:N}{token:N}', '{Guid.CreateVersion7()}', 'seed', now() + interval '1 hour', 99, 0, now(), now());
        """;

    private static string DeviceSql(Guid id, Guid org, Guid token, string hostname, string status = "Active") => $"""
        INSERT INTO endpoint_platform.devices
            (id, organization_id, hostname, machine_identifier, agent_version, status, enrolled_with_token_id, enrolled_at, last_seen_at, created_at, updated_at)
        VALUES ('{id}', '{org}', '{hostname}', 'm-{id:N}', '1.9.0', '{status}', '{token}', now(), now(), now(), now());
        """;

    private static string GroupSql(Guid id, Guid org, string name) => $"""
        INSERT INTO endpoint_platform.device_groups (id, organization_id, name, description, type, created_at, updated_at)
        VALUES ('{id}', '{org}', '{name}', 'd', 'Static', now(), now());
        """;

    private static string MembershipSql(Guid group, Guid device) => $"""
        INSERT INTO endpoint_platform.device_group_memberships (id, group_id, device_id, created_at, updated_at)
        VALUES ('{Guid.CreateVersion7()}', '{group}', '{device}', now(), now());
        """;

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// Production's own shape: the MKT device stays in MKT, every other device
    /// -- the retired one included -- lands in its organization's "All Devices",
    /// and a policy assigned to MKT still reaches its device.
    /// </summary>
    [Fact]
    public async Task Production_shaped_data_migrates_with_every_device_in_exactly_one_group()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, ProductionHead);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7(), mkt = Guid.CreateVersion7();
        Guid mktDevice = Guid.CreateVersion7(), a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), c = Guid.CreateVersion7(),
             retired = Guid.CreateVersion7();

        // A second organization with no groups at all, to prove the fallback group
        // is per organization and devices never cross into another tenant's.
        Guid otherOrg = Guid.CreateVersion7(), otherToken = Guid.CreateVersion7(), otherDevice = Guid.CreateVersion7();

        Guid policy = Guid.CreateVersion7();

        await ExecAsync(cs, string.Concat(
            OrganizationSql(org, "default", token),
            GroupSql(mkt, org, "MKT"),
            DeviceSql(mktDevice, org, token, "DESKTOP-PJCC143"),
            DeviceSql(a, org, token, "OMDEVSINH-TECHS"),
            DeviceSql(b, org, token, "DESKTOP-JPJ6TKF"),
            DeviceSql(c, org, token, "EXTRA-ACTIVE"),
            DeviceSql(retired, org, token, "AWS-VERIFY-PC", status: "Retired"),
            MembershipSql(mkt, mktDevice),
            OrganizationSql(otherOrg, "other", otherToken),
            DeviceSql(otherDevice, otherOrg, otherToken, "OTHER-TENANT-PC"),
            $"""
            INSERT INTO endpoint_platform.policies (id, organization_id, type, name, description, is_enabled, current_version_number, created_at, updated_at)
            VALUES ('{policy}', '{org}', 'ScreenLockTimeout', 'Lock', 'd', true, 1, now(), now());
            INSERT INTO endpoint_platform.policy_assignments (id, organization_id, policy_id, target_type, target_id, created_at, updated_at)
            VALUES ('{Guid.CreateVersion7()}', '{org}', '{policy}', 'Group', '{mkt}', now(), now());
            """));

        await MigrateToAsync(cs, Migration);

        (await AppliedAsync(cs)).ShouldContain(Migration);

        // Exactly one built-in group per organization.
        (await ScalarAsync<long>(cs, $"SELECT count(*) FROM endpoint_platform.device_groups WHERE is_built_in AND organization_id = '{org}'"))
            .ShouldBe(1);
        (await ScalarAsync<long>(cs, $"SELECT count(*) FROM endpoint_platform.device_groups WHERE is_built_in AND organization_id = '{otherOrg}'"))
            .ShouldBe(1);
        (await ScalarAsync<string>(cs, $"SELECT name FROM endpoint_platform.device_groups WHERE is_built_in AND organization_id = '{org}'"))
            .ShouldBe("All Devices");

        var allDevices = await ScalarAsync<Guid>(cs, $"SELECT id FROM endpoint_platform.device_groups WHERE is_built_in AND organization_id = '{org}'");
        var otherAllDevices = await ScalarAsync<Guid>(cs, $"SELECT id FROM endpoint_platform.device_groups WHERE is_built_in AND organization_id = '{otherOrg}'");

        // The existing membership carried over; everything else fell back.
        (await ScalarAsync<Guid>(cs, $"SELECT device_group_id FROM endpoint_platform.devices WHERE id = '{mktDevice}'")).ShouldBe(mkt);
        foreach (var id in new[] { a, b, c })
        {
            (await ScalarAsync<Guid>(cs, $"SELECT device_group_id FROM endpoint_platform.devices WHERE id = '{id}'")).ShouldBe(allDevices);
        }

        (await ScalarAsync<Guid>(cs, $"SELECT device_group_id FROM endpoint_platform.devices WHERE id = '{retired}'"))
            .ShouldBe(allDevices, "a retired device is still a row and must still have a group");
        (await ScalarAsync<Guid>(cs, $"SELECT device_group_id FROM endpoint_platform.devices WHERE id = '{otherDevice}'"))
            .ShouldBe(otherAllDevices, "a device must fall back to its own organization's group, never another's");

        // No device without a group, and the column now refuses one.
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM endpoint_platform.devices WHERE device_group_id IS NULL")).ShouldBe(0);
        (await ScalarAsync<string>(cs,
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema='endpoint_platform' AND table_name='devices' AND column_name='device_group_id'"))
            .ShouldBe("NO");

        // The old relation is gone; the group-targeted policy still resolves.
        (await ScalarAsync<long>(cs,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema='endpoint_platform' AND table_name='device_group_memberships'"))
            .ShouldBe(0);
        (await ScalarAsync<long>(cs, $"""
            SELECT count(*) FROM endpoint_platform.policy_assignments pa
            JOIN endpoint_platform.devices d ON d.device_group_id = pa.target_id
            WHERE pa.target_type = 'Group' AND d.id = '{mktDevice}'
            """)).ShouldBe(1, "the MKT policy must still reach the MKT device");

        // No device was created or destroyed.
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM endpoint_platform.devices")).ShouldBe(6);
    }

    /// <summary>
    /// The constraints the migration adds are live: a group with devices cannot
    /// be deleted out from under them, a second built-in group cannot be added,
    /// and names collide case-insensitively.
    /// </summary>
    [Fact]
    public async Task The_new_constraints_refuse_what_they_exist_to_refuse()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, ProductionHead);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7(), mkt = Guid.CreateVersion7(), device = Guid.CreateVersion7();
        await ExecAsync(cs, string.Concat(
            OrganizationSql(org, "constraints", token), GroupSql(mkt, org, "MKT"),
            DeviceSql(device, org, token, "PC-1"), MembershipSql(mkt, device)));

        await MigrateToAsync(cs, Migration);

        var fk = await Should.ThrowAsync<PostgresException>(() =>
            ExecAsync(cs, $"DELETE FROM endpoint_platform.device_groups WHERE id = '{mkt}'"));
        fk.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation, "a group still holding devices must not be deletable");

        var secondBuiltIn = await Should.ThrowAsync<PostgresException>(() => ExecAsync(cs, $"""
            INSERT INTO endpoint_platform.device_groups (id, organization_id, name, description, type, is_built_in, created_at, updated_at)
            VALUES ('{Guid.CreateVersion7()}', '{org}', 'Second Fallback', 'd', 'Static', true, now(), now());
            """));
        secondBuiltIn.ConstraintName.ShouldBe("ux_device_groups_one_built_in_per_organization");

        var caseCollision = await Should.ThrowAsync<PostgresException>(() => ExecAsync(cs, GroupSql(Guid.CreateVersion7(), org, "mkt")));
        caseCollision.ConstraintName.ShouldBe("ux_device_groups_organization_id_lower_name");

        var nullGroup = await Should.ThrowAsync<PostgresException>(() =>
            ExecAsync(cs, $"UPDATE endpoint_platform.devices SET device_group_id = NULL WHERE id = '{device}'"));
        nullGroup.SqlState.ShouldBe(PostgresErrorCodes.NotNullViolation);
    }

    /// <summary>
    /// A device in two groups has no single right answer, and choosing one would
    /// silently drop the other group's policies. The migration stops, names the
    /// device, and leaves the schema exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_device_in_two_groups_stops_the_migration_and_changes_nothing()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, ProductionHead);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7(), g1 = Guid.CreateVersion7(), g2 = Guid.CreateVersion7(),
             device = Guid.CreateVersion7();
        await ExecAsync(cs, string.Concat(
            OrganizationSql(org, "twogroups", token), GroupSql(g1, org, "Finance"), GroupSql(g2, org, "Sales"),
            DeviceSql(device, org, token, "SHARED-PC"), MembershipSql(g1, device), MembershipSql(g2, device)));

        var refused = await Should.ThrowAsync<PostgresException>(() => MigrateToAsync(cs, Migration));
        refused.MessageText.ShouldContain("SHARED-PC");
        refused.MessageText.ShouldContain("more than one group");

        await AssertUnmigratedAsync(cs, expectedMemberships: 2);
    }

    [Fact]
    public async Task Group_names_differing_only_by_case_stop_the_migration_and_change_nothing()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, ProductionHead);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7();
        await ExecAsync(cs, string.Concat(
            OrganizationSql(org, "casecollide", token),
            GroupSql(Guid.CreateVersion7(), org, "MKT"), GroupSql(Guid.CreateVersion7(), org, "mkt")));

        var refused = await Should.ThrowAsync<PostgresException>(() => MigrateToAsync(cs, Migration));
        refused.MessageText.ShouldContain("case-insensitively");

        await AssertUnmigratedAsync(cs, expectedMemberships: 0);
    }

    [Fact]
    public async Task A_custom_group_already_named_All_Devices_stops_the_migration_and_changes_nothing()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, ProductionHead);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7();
        await ExecAsync(cs, string.Concat(OrganizationSql(org, "reserved", token), GroupSql(Guid.CreateVersion7(), org, "all devices")));

        var refused = await Should.ThrowAsync<PostgresException>(() => MigrateToAsync(cs, Migration));
        refused.MessageText.ShouldContain("reserved");

        await AssertUnmigratedAsync(cs, expectedMemberships: 0);
    }

    /// <summary>
    /// Rolling back puts custom-group memberships back as rows and removes the
    /// built-in groups, which did not exist before.
    /// </summary>
    [Fact]
    public async Task Rolling_back_restores_memberships_and_removes_the_built_in_groups()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, ProductionHead);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7(), mkt = Guid.CreateVersion7(),
             inMkt = Guid.CreateVersion7(), ungrouped = Guid.CreateVersion7();
        await ExecAsync(cs, string.Concat(
            OrganizationSql(org, "rollback", token), GroupSql(mkt, org, "MKT"),
            DeviceSql(inMkt, org, token, "IN-MKT"), DeviceSql(ungrouped, org, token, "UNGROUPED"),
            MembershipSql(mkt, inMkt)));

        await MigrateToAsync(cs, Migration);
        await MigrateToAsync(cs, ProductionHead);

        (await AppliedAsync(cs)).ShouldNotContain(Migration);
        (await ScalarAsync<long>(cs, $"SELECT count(*) FROM endpoint_platform.device_group_memberships WHERE group_id = '{mkt}' AND device_id = '{inMkt}'"))
            .ShouldBe(1);
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM endpoint_platform.device_group_memberships")).ShouldBe(1,
            "the device that was only ever in the fallback group had no membership row before, and has none after");
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM endpoint_platform.device_groups")).ShouldBe(1,
            "only MKT remains; the built-in group did not exist before the migration");
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM endpoint_platform.devices")).ShouldBe(2);
    }

    private static async Task AssertUnmigratedAsync(string cs, int expectedMemberships)
    {
        (await AppliedAsync(cs)).ShouldNotContain(Migration);
        (await AppliedAsync(cs)).ShouldContain(ProductionHead);

        (await ScalarAsync<long>(cs,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema='endpoint_platform' AND table_name='devices' AND column_name='device_group_id'"))
            .ShouldBe(0, "a refused migration must leave no half-added column behind");
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM endpoint_platform.device_groups WHERE name = 'All Devices'"))
            .ShouldBe(0, "a refused migration must not leave a built-in group behind");
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM endpoint_platform.device_group_memberships"))
            .ShouldBe(expectedMemberships, "a refused migration must not have touched the memberships it refused to map");
    }
}
