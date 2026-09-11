using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using EndpointPlatform.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.DependencyInjection;

/// <summary>
/// Registers persistence, caching and the health checks shared by both API hosts.
/// </summary>
/// <remarks>
/// <para>
/// Both the Admin API and the Agent API call this. It contains no endpoint routing
/// and no authentication scheme, so sharing it does not blur the trust boundary
/// between them: each host configures its own authentication and its own endpoints.
/// </para>
/// <para>
/// Configuration is deliberately read lazily, through <see cref="IOptions{T}"/>, at
/// the moment a dependency is first used - never captured at registration time.
/// Registration-time snapshots break the two consumers that layer configuration in
/// after <c>Program.cs</c> has run: <c>WebApplicationFactory</c> in integration
/// tests, and any secret store that hydrates configuration late.
/// </para>
/// </remarks>
public static class InfrastructureServiceCollectionExtensions
{
    public const string ReadinessTag = "ready";
    public const string LivenessTag = "live";

    public static IServiceCollection AddEndpointPlatformInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddStronglyTypedOptions(configuration);

        // TimeProvider is injected everywhere instead of DateTimeOffset.UtcNow so
        // that time-dependent behaviour (token expiry, heartbeat staleness,
        // lockouts) is testable without waiting for the wall clock.
        services.TryAddSingletonTimeProvider();

        services.AddPersistence(environment);
        services.AddRedis();
        services.AddPlatformHealthChecks();

        return services;
    }

    private static IServiceCollection AddStronglyTypedOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ValidateOnStart turns a misconfiguration into a startup failure with a
        // readable message, rather than a NullReferenceException on first request.
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SeedOptions>()
            .Bind(configuration.GetSection(SeedOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AgentServerOptions>()
            .Bind(configuration.GetSection(AgentServerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<Software.PackageStorageOptions>()
            .Bind(configuration.GetSection(Software.PackageStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<Security.SecretProtectionOptions>()
            .Bind(configuration.GetSection(Security.SecretProtectionOptions.SectionName));

        // Internal (the default) needs nothing configured. Public without a
        // publisher is a gate with nothing to compare against, and is refused at
        // startup rather than discovered at the first publish.
        services.AddOptions<Agents.AgentReleaseOptions>()
            .Bind(configuration.GetSection(Agents.AgentReleaseOptions.SectionName))
            .Validate(o => o.IsValid,
                "AgentReleases:TrustMode is Public but AgentReleases:ExpectedSignerSubject is not set. "
                + "Public mode requires the publisher every release must be signed by.")
            .ValidateOnStart();


        return services;
    }

    private static IServiceCollection TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        return services;
    }

    private static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        services.AddSingleton<AuditImmutabilityInterceptor>();
        services.AddSingleton<AuditableEntityInterceptor>();

        // The (serviceProvider, builder) overload runs when a DbContext is first
        // resolved, not at registration - which is what lets the options carry
        // configuration applied after Program.cs.
        services.AddDbContext<EndpointPlatformDbContext>((serviceProvider, builder) =>
        {
            var databaseOptions = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            builder.UseNpgsql(databaseOptions.ConnectionString, npgsql =>
            {
                npgsql.CommandTimeout(databaseOptions.CommandTimeoutSeconds);
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
                npgsql.EnableRetryOnFailure(
                    databaseOptions.MaxRetryCount,
                    TimeSpan.FromSeconds(databaseOptions.MaxRetryDelaySeconds),
                    errorCodesToAdd: null);
            });

            builder.AddInterceptors(
                serviceProvider.GetRequiredService<AuditableEntityInterceptor>(),
                serviceProvider.GetRequiredService<AuditImmutabilityInterceptor>());

            // Parameter values include password hashes, enrollment secrets and
            // personal data. Refuse to enable this outside Development even if the
            // configuration asks for it - a leaked staging log is a real breach.
            if (databaseOptions.EnableSensitiveDataLogging)
            {
                if (!environment.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        "Database:EnableSensitiveDataLogging may only be enabled in the Development " +
                        "environment. It writes query parameter values - including credential material - " +
                        "to the application log.");
                }

                builder.EnableSensitiveDataLogging();
                builder.EnableDetailedErrors();
            }
        });

        services.AddScoped<ReferenceDataSeeder>();

        // Application services. Scoped: they hold the scoped DbContext.
        services.AddHttpContextAccessor();
        services.AddScoped<Auditing.AuditWriter>();
        services.AddScoped<Enrollment.EnrollmentTokenService>();
        services.AddScoped<Enrollment.AgentEnrollmentService>();
        services.AddScoped<Enrollment.AgentAuthenticationService>();
        services.AddScoped<Devices.DeviceReadService>();
        services.AddScoped<Devices.DeviceInventoryService>();
        services.AddScoped<Tasks.DeviceTaskService>();
        services.AddScoped<Policies.PolicyService>();
        services.AddScoped<Groups.DeviceGroupService>();
        services.AddScoped<Devices.SoftwareReadService>();
        services.AddScoped<Devices.SecurityReadService>();
        services.AddScoped<Devices.UpdateReadService>();
        services.AddScoped<Devices.DeviceLifecycleService>();
        services.AddScoped<Reporting.ReportReadService>();
        services.AddScoped<Devices.LocalAccountManagementService>();
        services.AddScoped<Security.DeviceScopeAuthorizer>();
        services.AddSingleton<Security.ISecretProtector, Security.AesGcmSecretProtector>();
        services.AddScoped<Security.EphemeralSecretStore>();

        // Holds enrollment requests between an agent asking and an administrator
        // deciding. Redis-backed like the ephemeral secret store, for the same
        // reasons: short TTL, atomic single-use redemption, no cleanup job.
        services.AddScoped<Enrollment.PendingEnrollmentStore>();

        // Turns an administrator's decision into an enrollment the EXISTING pipeline
        // completes, rather than a second enrollment implementation.
        services.AddScoped<Enrollment.EnrollmentApprovalService>();
        services.AddSingleton<Software.IPackageContentStore, Software.FileSystemPackageContentStore>();
        services.AddScoped<Software.SoftwarePackageService>();
        services.AddScoped<Software.SoftwareDeploymentService>();
        services.AddScoped<Software.SoftwareDeploymentReadService>();
        services.AddScoped<Software.ApplicationForceStopService>();
        services.AddScoped<Drivers.DriverPackageService>();
        services.AddScoped<Agents.AgentReleaseService>();
        // System trust only. The chain policy is a seam for tests to trust an
        // in-memory CA; nothing registers a wider policy in production.
        services.AddSingleton<Agents.IAuthenticodeChainPolicy, Agents.SystemTrustChainPolicy>();
        services.AddSingleton<Agents.IAuthenticodeVerifier, Agents.AuthenticodeVerifier>();

        // The publish gate. Owns the trust mode; consults the Authenticode verifier
        // above only in Public mode.
        services.AddSingleton<Agents.IReleasePublishVerifier, Agents.ReleasePublishVerifier>();
        services.AddScoped<Peripherals.UsbService>();
        services.AddScoped<Peripherals.UsbReadService>();
        services.AddScoped<Identity.LocalAdminElevationService>();

        return services;
    }

    private static IServiceCollection AddRedis(this IServiceCollection services)
    {
        // One multiplexer per process: it is thread-safe, multiplexes all commands
        // over a small number of sockets, and creating more than one is the classic
        // way to exhaust connections under load. The factory lambda runs on first
        // resolve, so the options are read lazily like everything else.
        services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
        {
            var redisOptions = serviceProvider.GetRequiredService<IOptions<RedisOptions>>().Value;

            var config = ConfigurationOptions.Parse(redisOptions.ConnectionString);
            config.AbortOnConnectFail = redisOptions.AbortOnConnectFail;
            config.ConnectTimeout = redisOptions.ConnectTimeoutMs;
            config.ClientName = redisOptions.InstanceName.TrimEnd(':');
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddStackExchangeRedisCache(_ => { });

        // Point the distributed cache at the singleton multiplexer registered above
        // instead of letting it open a second set of connections.
        services.AddOptions<RedisCacheOptions>()
            .Configure<IServiceProvider>((cacheOptions, serviceProvider) =>
            {
                var redisOptions = serviceProvider.GetRequiredService<IOptions<RedisOptions>>().Value;

                cacheOptions.InstanceName = redisOptions.InstanceName;
                cacheOptions.ConnectionMultiplexerFactory = () =>
                    Task.FromResult(serviceProvider.GetRequiredService<IConnectionMultiplexer>());
            });

        return services;
    }

    private static IServiceCollection AddPlatformHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            // Liveness: is the process itself up? Deliberately has no dependency
            // checks - an orchestrator must not restart a healthy process just
            // because the database is briefly unavailable.
            .AddCheck("self", () => HealthCheckResult.Healthy("Process is running."), tags: [LivenessTag])

            // Readiness: can this instance actually serve requests?
            // Postgres is required; Redis degrades rather than fails, because the
            // platform can still serve reads and record audit entries without it.
            // Both resolve their connection details per probe, never at startup.
            .AddNpgSql(
                connectionStringFactory: static sp =>
                    sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString,
                healthQuery: "SELECT 1;",
                name: "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: [ReadinessTag],
                timeout: TimeSpan.FromSeconds(5))
            .AddRedis(
                connectionMultiplexerFactory: static sp => sp.GetRequiredService<IConnectionMultiplexer>(),
                name: "redis",
                failureStatus: HealthStatus.Degraded,
                tags: [ReadinessTag],
                timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
