using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Boots the real Admin API host in-process for smoke and security tests.
/// </summary>
/// <remarks>
/// <para>
/// Supplies syntactically valid but unreachable connection strings. That is
/// deliberate: these tests cover host wiring, routing, response headers and CORS,
/// none of which should touch PostgreSQL or Redis. Any test here that started
/// passing only because a database happened to be running would be testing the
/// developer's machine rather than the application.
/// </para>
/// <para>
/// Behaviour that genuinely needs a database is tested against a real PostgreSQL
/// server in EndpointPlatform.Infrastructure.Tests.
/// </para>
/// </remarks>
public sealed class AdminApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Port 1 is reserved and never listening, so a stray connection attempt fails fast.</summary>
    /// <summary>A throwaway 32-byte key. Seals nothing real.</summary>
    private const string TestEscrowKey = "dGVzdC1lc2Nyb3cta2V5LTMyLWJ5dGVzLWxvbmchISE=";

    /// <summary>Fixed test key for sealing TOTP secrets. Seals nothing real.</summary>
    private const string TestMfaKey = "dGVzdC1tZmEta2V5LTMyLWJ5dGVzLWxvbmchISEhISE=";

    private const string UnreachablePostgres =
        "Host=127.0.0.1;Port=1;Database=unreachable_by_design;Username=none;Password=none";

    private const string UnreachableRedis = "127.0.0.1:1,abortConnect=false,connectTimeout=100";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        // UseSetting, not ConfigureAppConfiguration: with minimal hosting, factory
        // ConfigureAppConfiguration callbacks are applied AFTER Program.cs has run,
        // so anything Program.cs reads during registration (the CORS allow-list)
        // would not see them. UseSetting values are present from the start.
        builder.UseSetting("Database:ConnectionString", UnreachablePostgres);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Database:EnableSensitiveDataLogging", "false");
        builder.UseSetting("Redis:ConnectionString", UnreachableRedis);
        builder.UseSetting("Redis:InstanceName", "endpointplatform:test:");
        builder.UseSetting("Redis:AbortOnConnectFail", "false");
        builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");

        // Mandatory for the Admin API since recovery-key escrow shipped: the
        // options are validated on start, so without a key the host refuses to
        // build and every test in the suite fails at construction. A fixed test
        // key is fine here and is not a secret - it seals nothing real.
        builder.UseSetting("RecoveryEscrow:Key", TestEscrowKey);
        builder.UseSetting("RecoveryEscrow:KeyVersion", "1");

        // Same reason: Mfa options are validated on start, so the Admin API
        // refuses to build without a TOTP sealing key.
        builder.UseSetting("Mfa:TotpKey", TestMfaKey);
    }
}

/// <summary>
/// All Admin API test classes share ONE factory through this collection.
/// </summary>
/// <remarks>
/// Class fixtures would give each test class its own factory, and two factories
/// resolving the same entry point concurrently race inside
/// <c>HostFactoryResolver</c>'s process-wide diagnostic listener, failing
/// intermittently with "The entry point exited without ever building an IHost".
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AdminApiCollection : ICollectionFixture<AdminApiFactory>
{
    public const string Name = "admin-api";
}
