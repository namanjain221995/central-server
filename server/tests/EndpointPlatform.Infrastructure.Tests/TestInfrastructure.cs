using Npgsql;

namespace EndpointPlatform.Infrastructure.Tests;

/// <summary>
/// A throwaway database on a locally installed PostgreSQL server, created for one
/// fixture and dropped when that fixture is disposed.
/// </summary>
/// <remarks>
/// <para>
/// A real server, not an in-memory or SQLite substitute. The behaviour these suites
/// test - triggers, <c>jsonb</c>, <c>inet</c>, partial indexes, role privileges,
/// migrations run up and down - does not exist in a fake provider, so testing
/// against one would prove nothing about what actually runs.
/// </para>
/// <para>
/// The server is whatever <see cref="PostgresVariable"/> points at: an ADMIN
/// connection string for a role that may <c>CREATE DATABASE</c> (a superuser, or a
/// role with CREATEDB). Every fixture gets a database of its own with a unique
/// name, so suites run in parallel and a crashed run cannot poison the next one.
/// Use PostgreSQL 17, the version the platform is deployed on.
/// </para>
/// <para>
/// Linked into the Admin API and Agent API test projects as well (see their
/// project files), so there is one definition of how a test reaches PostgreSQL.
/// </para>
/// </remarks>
public sealed class TestDatabase : IAsyncDisposable
{
    /// <summary>
    /// Environment variable holding the admin connection string, for example
    /// <c>Host=127.0.0.1;Port=5432;Username=postgres;Password=...</c>.
    /// </summary>
    public const string PostgresVariable = "ENDPOINTPLATFORM_TEST_POSTGRES";

    private readonly string _adminConnectionString;
    private bool _dropped;

    private TestDatabase(string adminConnectionString, string name, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        Name = name;
        ConnectionString = connectionString;
    }

    /// <summary>The generated database name.</summary>
    public string Name { get; }

    /// <summary>Connection string for the throwaway database, as the admin role.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Creates an empty database. <paramref name="purpose"/> only makes the name
    /// recognisable in <c>\l</c> output; uniqueness comes from the generated suffix.
    /// </summary>
    public static async Task<TestDatabase> CreateAsync(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (purpose.Length > 24 || purpose.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
        {
            throw new ArgumentException(
                "A test database purpose is at most 24 characters of a-z, 0-9 and '_'.", nameof(purpose));
        }

        var adminConnectionString = ReadAdminConnectionString();

        // Restricted to [a-z0-9_] by the check above and the hex suffix, so the
        // name is safe to place in a statement; it is still quoted as an identifier.
        var name = $"ept_{purpose}_{Guid.CreateVersion7():N}"[..Math.Min(63, 4 + purpose.Length + 1 + 32)];

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();

            // template0 so UTF8 is accepted whatever encoding this server's
            // template1 happens to have.
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING 'UTF8'", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }
            .ConnectionString;

        return new TestDatabase(adminConnectionString, name, connectionString);
    }

    /// <summary>Drops the database, terminating any connection still open to it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_dropped)
        {
            return;
        }

        _dropped = true;

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();

        // WITH (FORCE): the hosts under test keep pooled connections open, and a
        // plain DROP DATABASE refuses while any session is connected.
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private static string ReadAdminConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(PostgresVariable);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{PostgresVariable} is not set. These tests run against a real, locally installed "
                + "PostgreSQL 17. Set it to an admin connection string for a role that may CREATE DATABASE, "
                + "for example \"Host=127.0.0.1;Port=5432;Username=postgres;Password=...\". "
                + "See docs/development.md.");
        }

        var builder = new NpgsqlConnectionStringBuilder(value);

        // The maintenance database. Without this Npgsql would default to a database
        // named after the user, which need not exist.
        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            builder.Database = "postgres";
        }

        return builder.ConnectionString;
    }
}

/// <summary>
/// The locally installed Redis the Admin API suite uses, so the ephemeral-secret
/// path (create user / reset password) is exercised end to end.
/// </summary>
/// <remarks>
/// Shared, not per-fixture: every key the platform writes is addressed by a random
/// reference or carries the host's own instance prefix, so runs cannot collide.
/// Needs Redis 6.2 or later, because redemption uses GETDEL.
/// </remarks>
public static class TestRedis
{
    /// <summary>
    /// Environment variable holding a StackExchange.Redis connection string, for
    /// example <c>127.0.0.1:6379,password=...</c>.
    /// </summary>
    public const string RedisVariable = "ENDPOINTPLATFORM_TEST_REDIS";

    public static string ConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(RedisVariable);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"{RedisVariable} is not set. These tests run against a real, locally installed Redis "
                    + "(6.2 or later). Set it to a StackExchange.Redis connection string, for example "
                    + "\"127.0.0.1:6379,password=...\". See docs/development.md.");
            }

            return value;
        }
    }
}
