using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Migrations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>BackfillAllDeviceScope</c>, run against the schema it follows and seeded with
/// every combination of the flag and scope rows an account can have.
/// </summary>
/// <remarks>
/// The migration repairs accounts that account creation left with no scope at all,
/// and must touch nothing else: an account with scope rows was restricted on
/// purpose, and one that already has the flag needs no change. Seeding is raw SQL
/// at the previous head so the test describes the rows exactly as production holds
/// them.
/// </remarks>
public sealed class BackfillAllDeviceScopeMigrationTests : IAsyncLifetime
{
    private const string PreviousHead = "20261027041723_DriverUpdateOutcomes";

    private const string Migration = "20261027184255_BackfillAllDeviceScope";

    private readonly List<TestDatabase> _databases = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- harness

    private async Task<string> NewDatabaseAsync()
    {
        var database = await TestDatabase.CreateAsync("scopebackfill");
        _databases.Add(database);
        return database.ConnectionString;
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

    private static async Task<int> ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static string OrganizationSql(Guid org) => $"""
        INSERT INTO endpoint_platform.organizations (id, name, slug, is_active, created_at, updated_at)
        VALUES ('{org}', 'Org {org:N}', 's{org:N}', true, now(), now());
        """;

    private static string GroupSql(Guid id, Guid org, string name) => $"""
        INSERT INTO endpoint_platform.device_groups (id, organization_id, name, description, type, created_at, updated_at)
        VALUES ('{id}', '{org}', '{name}', 'd', 'Static', now(), now());
        """;

    /// <summary>An account as the old creation path left it, last changed a day ago.</summary>
    private static string UserSql(Guid id, Guid org, bool hasAllDeviceScope, string status = "Active") => $"""
        INSERT INTO endpoint_platform.platform_users
            (id, organization_id, email, normalized_email, display_name, status, security_stamp,
             failed_sign_in_count, has_all_device_scope, is_system_account, must_change_password,
             created_at, updated_at)
        VALUES ('{id}', '{org}', 'u{id:N}@test.local', 'U{id:N}@TEST.LOCAL', 'User', '{status}', '{id:N}',
                0, {(hasAllDeviceScope ? "true" : "false")}, false, false,
                now() - interval '1 day', now() - interval '1 day');
        """;

    private static string ScopeSql(Guid user, Guid group) => $"""
        INSERT INTO endpoint_platform.admin_device_scopes (id, platform_user_id, device_group_id, created_at, updated_at)
        VALUES ('{Guid.CreateVersion7()}', '{user}', '{group}', now(), now());
        """;

    private static Task<bool> FlagAsync(string cs, Guid user) =>
        ScalarAsync<bool>(cs, $"SELECT has_all_device_scope FROM endpoint_platform.platform_users WHERE id = '{user}'");

    private static Task<long> ScopeRowsAsync(string cs, Guid user) =>
        ScalarAsync<long>(cs, $"SELECT count(*) FROM endpoint_platform.admin_device_scopes WHERE platform_user_id = '{user}'");

    private static Task<bool> TouchedAsync(string cs, Guid user) =>
        ScalarAsync<bool>(cs,
            $"SELECT updated_at > now() - interval '1 hour' FROM endpoint_platform.platform_users WHERE id = '{user}'");

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Only_accounts_with_neither_the_flag_nor_scope_rows_gain_all_device_scope()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, PreviousHead);

        Guid org = Guid.CreateVersion7(), sales = Guid.CreateVersion7(), finance = Guid.CreateVersion7();

        // The broken set: created after 2026-08-18 by the Super-Administrator-only rule.
        Guid noScope = Guid.CreateVersion7(), noScopeDisabled = Guid.CreateVersion7();

        // Deliberately restricted: flag off, group rows present. Must stay exactly so.
        Guid restricted = Guid.CreateVersion7(), restrictedTwoGroups = Guid.CreateVersion7();

        // Already whole: flag on, with and without rows.
        Guid allScope = Guid.CreateVersion7(), allScopeWithRows = Guid.CreateVersion7();

        await ExecAsync(cs, string.Concat(
            OrganizationSql(org),
            GroupSql(sales, org, "Sales"),
            GroupSql(finance, org, "Finance"),
            UserSql(noScope, org, hasAllDeviceScope: false),
            UserSql(noScopeDisabled, org, hasAllDeviceScope: false, status: "Disabled"),
            UserSql(restricted, org, hasAllDeviceScope: false),
            UserSql(restrictedTwoGroups, org, hasAllDeviceScope: false),
            UserSql(allScope, org, hasAllDeviceScope: true),
            UserSql(allScopeWithRows, org, hasAllDeviceScope: true),
            ScopeSql(restricted, sales),
            ScopeSql(restrictedTwoGroups, sales),
            ScopeSql(restrictedTwoGroups, finance),
            ScopeSql(allScopeWithRows, finance)));

        await MigrateToAsync(cs, Migration);

        // Repaired: no where-restriction was ever configured for them.
        (await FlagAsync(cs, noScope)).ShouldBeTrue();
        (await FlagAsync(cs, noScopeDisabled)).ShouldBeTrue(
            "the flag is about where, not whether the account may sign in; re-enabling it must not yield an empty console");
        (await TouchedAsync(cs, noScope)).ShouldBeTrue("a changed row records that it changed");

        // Restricted on purpose: untouched, rows and all.
        (await FlagAsync(cs, restricted)).ShouldBeFalse("an account with scope rows was restricted deliberately");
        (await FlagAsync(cs, restrictedTwoGroups)).ShouldBeFalse();
        (await ScopeRowsAsync(cs, restricted)).ShouldBe(1);
        (await ScopeRowsAsync(cs, restrictedTwoGroups)).ShouldBe(2);
        (await TouchedAsync(cs, restricted)).ShouldBeFalse();
        (await TouchedAsync(cs, restrictedTwoGroups)).ShouldBeFalse();

        // Already whole: not even written.
        (await FlagAsync(cs, allScope)).ShouldBeTrue();
        (await FlagAsync(cs, allScopeWithRows)).ShouldBeTrue();
        (await ScopeRowsAsync(cs, allScopeWithRows)).ShouldBe(1);
        (await TouchedAsync(cs, allScope)).ShouldBeFalse();
        (await TouchedAsync(cs, allScopeWithRows)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_backfill_is_idempotent()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, PreviousHead);

        Guid org = Guid.CreateVersion7(), sales = Guid.CreateVersion7();
        Guid noScope = Guid.CreateVersion7(), restricted = Guid.CreateVersion7();

        await ExecAsync(cs, string.Concat(
            OrganizationSql(org),
            GroupSql(sales, org, "Sales"),
            UserSql(noScope, org, hasAllDeviceScope: false),
            UserSql(restricted, org, hasAllDeviceScope: false),
            ScopeSql(restricted, sales)));

        await MigrateToAsync(cs, Migration);

        // A second run -- a re-applied script, a restore replayed onto a migrated
        // database -- finds nothing left to change.
        (await ExecAsync(cs, BackfillAllDeviceScope.BackfillSql)).ShouldBe(0);

        (await FlagAsync(cs, noScope)).ShouldBeTrue();
        (await FlagAsync(cs, restricted)).ShouldBeFalse();
    }
}
