using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using EndpointPlatform.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace EndpointPlatform.Migrations;

/// <summary>
/// Standalone migration runner: applies pending migrations, then seeds reference data.
/// </summary>
/// <remarks>
/// <para>
/// Runs as a one-shot job before the APIs start (both API units Require= it, so
/// systemd runs it first and a failure stops them starting on a schema they do
/// not match). Separating it from the API hosts matters for two
/// reasons: schema changes execute exactly once instead of racing between API
/// replicas, and the job can connect as a database owner while the APIs connect as
/// a restricted runtime role that has no DDL rights and cannot UPDATE or DELETE
/// audit records.
/// </para>
/// <para>
/// Exit codes: 0 success, 1 configuration error, 2 migration or seeding failure.
/// </para>
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
        var logger = loggerFactory.CreateLogger("EndpointPlatform.Migrations");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables(prefix: "ENDPOINTPLATFORM_")
                .AddCommandLine(args)
                .Build();

            var databaseOptions = configuration
                .GetSection(DatabaseOptions.SectionName)
                .Get<DatabaseOptions>();

            if (databaseOptions is null || string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
            {
                logger.LogError(
                    "Database:ConnectionString is not configured. Set "
                    + "ENDPOINTPLATFORM_Database__ConnectionString before running the migration job.");
                return 1;
            }

            await using var dbContext = BuildDbContext(databaseOptions, loggerFactory);

            // One-shot operator command: create the first Super Administrator.
            if (args.Contains("bootstrap-admin", StringComparer.OrdinalIgnoreCase))
            {
                var bootstrapper = new AdminBootstrapper(
                    dbContext, loggerFactory.CreateLogger<AdminBootstrapper>());

                return await bootstrapper.RunAsync(
                    configuration["Bootstrap:AdminEmail"],
                    configuration["Bootstrap:AdminPassword"]);
            }

            logger.LogInformation("Applying database migrations...");

            var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToArray();

            if (pending.Length == 0)
            {
                logger.LogInformation("Database schema is already up to date.");
            }
            else
            {
                logger.LogInformation(
                    "Applying {Count} pending migration(s): {Migrations}",
                    pending.Length,
                    string.Join(", ", pending));

                await dbContext.Database.MigrateAsync();
                logger.LogInformation("Migrations applied successfully.");
            }

            // Grants must follow the objects they apply to, so this runs after
            // migrations and before seeding.
            var runtimeRoleName = configuration["Database:RuntimeRoleName"];

            if (string.IsNullOrWhiteSpace(runtimeRoleName))
            {
                logger.LogWarning(
                    "Database:RuntimeRoleName is not configured; runtime grants were not applied. "
                    + "The APIs will connect with whatever privileges their own credential already has.");
            }
            else
            {
                var grantsApplier = new RuntimeGrantsApplier(
                    dbContext,
                    loggerFactory.CreateLogger<RuntimeGrantsApplier>());

                await grantsApplier.ApplyAsync(runtimeRoleName);
            }

            var seeder = new ReferenceDataSeeder(
                dbContext,
                Microsoft.Extensions.Options.Options.Create(
                    configuration.GetSection(SeedOptions.SectionName).Get<SeedOptions>() ?? new SeedOptions()),
                loggerFactory.CreateLogger<ReferenceDataSeeder>());

            await seeder.SeedAsync();

            logger.LogInformation("Migration job completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Migration job failed.");
            return 2;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static EndpointPlatformDbContext BuildDbContext(
        DatabaseOptions databaseOptions,
        ILoggerFactory loggerFactory)
    {
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(databaseOptions.ConnectionString, npgsql =>
            {
                npgsql.CommandTimeout(databaseOptions.CommandTimeoutSeconds);
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
                // The database may still be starting up when this job runs.
                npgsql.EnableRetryOnFailure(
                    databaseOptions.MaxRetryCount,
                    TimeSpan.FromSeconds(databaseOptions.MaxRetryDelaySeconds),
                    errorCodesToAdd: null);
            })
            .UseLoggerFactory(loggerFactory)
            // Seeding writes no audit entries, but the interceptor stays wired so the
            // runner cannot become a back door that mutates them.
            //
            // The group interceptor comes first and is not optional here: the seeder
            // creates the default organization, and it is this interceptor that
            // gives an organization its "All Devices" group. Without it a fresh
            // database would have an organization no device could ever enroll into.
            .AddInterceptors(
                new DeviceGroupAssignmentInterceptor(),
                new AuditableEntityInterceptor(TimeProvider.System),
                new AuditImmutabilityInterceptor())
            .Options;

        return new EndpointPlatformDbContext(options);
    }
}
