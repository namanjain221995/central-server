using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// What happens if somebody rolls back the automatic-escrow migration.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately on its own database rather than the shared fixture, because the
/// test migrates the schema down and back: doing that to a database other tests are
/// using would break them in ways that look like unrelated failures.
/// </para>
/// <para>
/// The property under test is that a rollback cannot quietly destroy recovery
/// credentials. Dropping <c>seal_scheme</c> would leave hybrid-sealed ciphertext in
/// place with nothing left to say how to open it -- intact-looking and
/// unrecoverable -- so the migration refuses rather than proceeding.
/// </para>
/// </remarks>
public sealed class AutomaticEscrowRollbackTests : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PreviousMigration = "20260828161432_BitLockerRecoveryEscrow";

    private const string Volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    private TestDatabase? _database;

    private string DatabaseConnectionString =>
        _database?.ConnectionString ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync("rollback");

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private EndpointPlatformDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(DatabaseConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
            })
            // Devices now require a group; this is what gives the seeded
            // organization its "All Devices" and puts the device in it.
            .AddInterceptors(new Infrastructure.Persistence.Interceptors.DeviceGroupAssignmentInterceptor())
            .Options;

        return new EndpointPlatformDbContext(options);
    }

    private static async Task<Device> SeedDeviceAsync(EndpointPlatformDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var slug = ("rb" + Guid.CreateVersion7().ToString("N"))[..20];

        var org = new Organization("Rollback Org", slug);
        db.Organizations.Add(org);

        var token = new Domain.Enrollment.EnrollmentToken(
            org.Id, "t", new string('a', 64), Guid.CreateVersion7(), "a@b", now.AddHours(1), 5);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "RB-PC", "m-" + Guid.CreateVersion7().ToString("N"), "1.3.0", null, token.Id, now);

        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return device;
    }

    private static Task RollBackAsync(EndpointPlatformDbContext db) =>
        db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreviousMigration);

    /// <summary>
    /// The whole story in one test, because the two halves only mean something
    /// together: the rollback is refused precisely while refusing protects
    /// something, and permitted the moment it does not.
    /// </summary>
    [Fact]
    public async Task Rollback_is_refused_while_automatic_escrows_exist_and_permitted_once_they_are_gone()
    {
        await using var db = CreateDbContext();
        var device = await SeedDeviceAsync(db);
        var now = DateTimeOffset.UtcNow;

        // A manual row, which a rollback can carry safely, and an automatic one,
        // which it cannot.
        var manual = new BitLockerRecoveryEscrow(
            device.OrganizationId, device.Id, Volume, Guid.CreateVersion7().ToString(), "C:",
            "aes-envelope", 1, Guid.CreateVersion7(), "admin@test.local", now);

        var automatic = BitLockerRecoveryEscrow.Automatic(
            device.OrganizationId, device.Id, Volume, Guid.CreateVersion7().ToString(), "D:",
            "hybrid-envelope", 1, "RB-PC (agent)", now);

        db.BitLockerRecoveryEscrows.AddRange(manual, automatic);
        await db.SaveChangesAsync();

        // ---- refused, and nothing changed ---------------------------------
        var refusal = await Should.ThrowAsync<Exception>(() => RollBackAsync(db));

        refusal.ToString().ShouldContain("Cannot roll back AutomaticBitLockerEscrow");

        // EF runs a migration in a transaction, so the abort must leave the schema
        // exactly as it was -- not half reverted.
        (await ColumnExistsAsync(db, "bitlocker_recovery_escrows", "seal_scheme")).ShouldBeTrue();
        (await ColumnExistsAsync(db, "bitlocker_recovery_escrows", "origin")).ShouldBeTrue();
        (await ColumnExistsAsync(db, "agent_credentials", "sealing_key_fingerprint")).ShouldBeTrue();
        (await TableExistsAsync(db, "bitlocker_escrow_attempts")).ShouldBeTrue();

        // And above all: the credential it refused to endanger is still there.
        await using (var verify = CreateDbContext())
        {
            var survivor = await verify.BitLockerRecoveryEscrows
                .SingleAsync(e => e.Id == automatic.Id);

            survivor.SealedRecoveryPassword.ShouldBe("hybrid-envelope");
            survivor.SealScheme.ShouldBe(BitLockerSealScheme.HybridRsaV1);
        }

        // ---- and permitted once the operator has dealt with them -----------
        // Removing the row is the deliberate data decision the guard exists to
        // force. The migration never makes it on anyone's behalf.
        await using (var cleanup = CreateDbContext())
        {
            cleanup.BitLockerRecoveryEscrows.Remove(
                await cleanup.BitLockerRecoveryEscrows.SingleAsync(e => e.Id == automatic.Id));

            await cleanup.SaveChangesAsync();
        }

        await using (var rollback = CreateDbContext())
        {
            await RollBackAsync(rollback);

            (await ColumnExistsAsync(rollback, "bitlocker_recovery_escrows", "seal_scheme")).ShouldBeFalse();
            (await TableExistsAsync(rollback, "bitlocker_escrow_attempts")).ShouldBeFalse();
        }

        // The manual escrow survived the reversal intact, which is the other half
        // of what "defensible rollback" has to mean.
        await using (var verify = CreateDbContext())
        {
            var remaining = await verify.Database
                .SqlQuery<string>($"""
                    SELECT sealed_recovery_password AS "Value"
                    FROM endpoint_platform.bitlocker_recovery_escrows
                    """)
                .ToListAsync();

            remaining.ShouldHaveSingleItem().ShouldBe("aes-envelope");
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        EndpointPlatformDbContext db, string table, string column)
    {
        var found = await db.Database
            .SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM information_schema.columns
                WHERE table_schema = 'endpoint_platform'
                  AND table_name = {table}
                  AND column_name = {column}
                """)
            .ToListAsync();

        return found.Count > 0;
    }

    private static async Task<bool> TableExistsAsync(EndpointPlatformDbContext db, string table)
    {
        var found = await db.Database
            .SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM information_schema.tables
                WHERE table_schema = 'endpoint_platform' AND table_name = {table}
                """)
            .ToListAsync();

        return found.Count > 0;
    }
}
