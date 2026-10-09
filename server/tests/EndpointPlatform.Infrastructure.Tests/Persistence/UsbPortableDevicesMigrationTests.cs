using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Migrations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// Rollback safety of <c>20261009153245_UsbPortableDevices</c> (agent 1.16.0).
/// </summary>
/// <remarks>
/// <para>
/// The schema change is the easy half: one nullable column, which the previous
/// server never maps and therefore never reads or writes. A rollback keeps it.
/// </para>
/// <para>
/// The data is the hard half, found by running the previous server against rows this
/// one wrote: <c>usb_devices.device_class</c> is stored as the enum's name, and EF
/// Core refuses to materialise a name the model does not have. The previous server
/// has no <c>PortableDevice</c>, so every query that loads such a row — the agent's
/// USB report, the console's USB panel, a revoke — fails with a 500 until the rows are
/// relabelled. The rollback runbook therefore carries <see cref="CompensatingUpdate"/>,
/// and the next report from a 1.16.0 agent restores the label once the release is
/// redeployed. See docs/runbooks/usb-portable-devices-rollout.md.
/// </para>
/// </remarks>
public sealed class UsbPortableDevicesMigrationTests : IAsyncLifetime
{
    /// <summary>The newest migration the 1.15.0 server knows.</summary>
    private const string PreviousHead = "20261008192434_DeviceLastBootAt";

    private const string Migration = "20261009153245_UsbPortableDevices";

    /// <summary>The rollback runbook's compensating update, verbatim.</summary>
    public const string CompensatingUpdate =
        "UPDATE endpoint_platform.usb_devices SET device_class = 'Unknown' WHERE device_class = 'PortableDevice'";

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
        var database = await TestDatabase.CreateAsync("usbportable");
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

    private static string SeedSql(Guid org, Guid token, Guid device, Guid usb, string deviceClass) => $"""
        INSERT INTO endpoint_platform.organizations (id, name, slug, is_active, created_at, updated_at)
        VALUES ('{org}', 'Org', 'org-{org:N}', true, now(), now());
        INSERT INTO endpoint_platform.enrollment_tokens
            (id, organization_id, name, secret_hash, created_by_user_id, created_by_display, expires_at, max_uses, use_count, created_at, updated_at)
        VALUES ('{token}', '{org}', 't', '{token:N}{token:N}', '{Guid.CreateVersion7()}', 'seed', now() + interval '1 hour', 9, 0, now(), now());
        INSERT INTO endpoint_platform.device_groups (id, organization_id, name, description, type, created_at, updated_at)
        VALUES ('{org}', '{org}', 'All devices', 'd', 'Static', now(), now());
        INSERT INTO endpoint_platform.devices
            (id, organization_id, hostname, machine_identifier, agent_version, status, enrolled_with_token_id, enrolled_at, last_seen_at,
             device_group_id, created_at, updated_at)
        VALUES ('{device}', '{org}', 'PC-{device:N}', 'm-{device:N}', '1.15.0', 'Active', '{token}', now(), now(),
             '{org}', now(), now());
        INSERT INTO endpoint_platform.usb_devices
            (id, organization_id, device_id, instance_id, device_class, is_connected, first_seen_at, last_seen_at,
             policy, enforced_policy, enforced_at, created_at, updated_at)
        VALUES ('{usb}', '{org}', '{device}', 'USB\VID_0781&PID_5581\{usb:N}', '{deviceClass}', true, now(), now(),
             'ReadOnly', 'ReadOnly', now(), now(), now());
        """;

    // ------------------------------------------------------------------ tests

    [Fact]
    public void The_migration_only_adds_one_nullable_column_with_no_default()
    {
        var operation = new UsbPortableDevices().UpOperations.ShouldHaveSingleItem();

        var add = operation.ShouldBeOfType<AddColumnOperation>();
        add.Schema.ShouldBe("endpoint_platform");
        add.Table.ShouldBe("usb_devices");
        add.Name.ShouldBe("enforcement_status");
        add.IsNullable.ShouldBeTrue();
        add.MaxLength.ShouldBe(16);
        add.DefaultValue.ShouldBeNull();
        add.DefaultValueSql.ShouldBeNull();
        add.ComputedColumnSql.ShouldBeNull();
    }

    [Fact]
    public async Task Rows_written_by_the_previous_server_cross_the_migration_unchanged_and_read_as_unverified()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, PreviousHead);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7(), device = Guid.CreateVersion7(), usb = Guid.CreateVersion7();
        await ExecAsync(cs, SeedSql(org, token, device, usb, "Storage"));

        await MigrateToAsync(cs, Migration);

        (await ScalarAsync<string>(cs,
            $"SELECT device_class || '/' || policy || '/' || enforced_policy FROM endpoint_platform.usb_devices WHERE id = '{usb}'"))
            .ShouldBe("Storage/ReadOnly/ReadOnly");
        (await ScalarAsync<long>(cs,
            "SELECT count(*) FROM endpoint_platform.usb_devices WHERE enforcement_status IS NOT NULL")).ShouldBe(0);

        await using var db = Context(cs);
        var row = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.Id == usb);

        // What an older agent established is shown as applied, never as verified.
        row.EnforcementStatus.ShouldBeNull();
        row.IsPolicyEnforced.ShouldBeTrue();
        row.IsEnforcementVerified.ShouldBeFalse();
    }

    /// <summary>
    /// Pins the hazard the rollback runbook exists for. If this ever stops throwing,
    /// the model has learned to read names it does not know, and the compensating
    /// update can be retired for the releases that follow.
    /// </summary>
    [Fact]
    public async Task A_class_name_the_model_does_not_have_cannot_be_read_back()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, Migration);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7(), device = Guid.CreateVersion7(), usb = Guid.CreateVersion7();
        await ExecAsync(cs, SeedSql(org, token, device, usb, "SomeFutureClass"));

        await using var db = Context(cs);
        var read = () => db.UsbDevices.AsNoTracking().SingleAsync(u => u.Id == usb);

        (await Should.ThrowAsync<InvalidOperationException>(read)).Message.ShouldContain("SomeFutureClass");
    }

    [Fact]
    public async Task The_compensating_update_makes_phone_rows_readable_by_name_and_touches_nothing_else()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, Migration);

        Guid org = Guid.CreateVersion7(), token = Guid.CreateVersion7(), device = Guid.CreateVersion7();
        Guid phone = Guid.CreateVersion7(), stick = Guid.CreateVersion7();
        await ExecAsync(cs, SeedSql(org, token, device, phone, "PortableDevice"));
        await ExecAsync(cs, $"""
            INSERT INTO endpoint_platform.usb_devices
                (id, organization_id, device_id, instance_id, device_class, is_connected, first_seen_at, last_seen_at, policy, created_at, updated_at)
            VALUES ('{stick}', '{org}', '{device}', 'USB\VID_0930&PID_6544\{stick:N}', 'Storage', true, now(), now(), 'Restricted', now(), now());
            UPDATE endpoint_platform.usb_devices SET enforcement_status = 'Verified' WHERE device_id = '{device}';
            """);

        await ExecAsync(cs, CompensatingUpdate);

        (await ScalarAsync<string>(cs,
            $"SELECT string_agg(device_class || '/' || policy || '/' || enforcement_status, ',' ORDER BY device_class) FROM endpoint_platform.usb_devices WHERE device_id = '{device}'"))
            .ShouldBe("Storage/Restricted/Verified,Unknown/ReadOnly/Verified");

        // Running it twice changes nothing: safe to repeat during an incident.
        await ExecAsync(cs, CompensatingUpdate);
        (await ScalarAsync<long>(cs,
            "SELECT count(*) FROM endpoint_platform.usb_devices WHERE device_class = 'PortableDevice'")).ShouldBe(0);

        await using var db = Context(cs);
        var rows = await db.UsbDevices.AsNoTracking().Where(u => u.DeviceId == device).ToListAsync();
        rows.Select(r => r.DeviceClass).ShouldBe([UsbDeviceClass.Unknown, UsbDeviceClass.Storage], ignoreOrder: true);
    }
}
