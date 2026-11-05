using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Diagnostics;

/// <summary>
/// Reports how much disk the server's database is using, broken down by table, so an
/// administrator can see what is growing — for instance the activity samples that
/// work-time tracking produces.
/// </summary>
public sealed class ServerStorageReadService(EndpointPlatformDbContext dbContext)
{
    private const int MaxTables = 50;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;

    public async Task<ServerStorageSummary> GetAsync(CancellationToken cancellationToken = default)
    {
        var databaseBytes = await _dbContext.Database
            .SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"")
            .FirstAsync(cancellationToken);

        // pg_total_relation_size includes the table, its indexes and TOAST; n_live_tup is
        // the planner's row estimate. Both are read-only catalog queries, no user input.
        var rows = await _dbContext.Database
            .SqlQuery<TableSizeRow>($@"
                SELECT relname AS ""Name"",
                       pg_total_relation_size(quote_ident(schemaname) || '.' || quote_ident(relname)) AS ""TotalBytes"",
                       COALESCE(n_live_tup, 0) AS ""RowEstimate""
                FROM pg_stat_user_tables
                WHERE schemaname = 'endpoint_platform'
                ORDER BY pg_total_relation_size(quote_ident(schemaname) || '.' || quote_ident(relname)) DESC
                LIMIT 50")
            .ToListAsync(cancellationToken);

        var tables = rows
            .Take(MaxTables)
            .Select(r => new TableStorage(r.Name, r.TotalBytes, r.RowEstimate))
            .ToList();

        return new ServerStorageSummary(databaseBytes, tables);
    }

    private sealed record TableSizeRow(string Name, long TotalBytes, long RowEstimate);
}

/// <summary>The server database's storage, by table.</summary>
/// <param name="DatabaseBytes">Total size of the database on disk.</param>
/// <param name="Tables">The largest tables, biggest first.</param>
public sealed record ServerStorageSummary(long DatabaseBytes, IReadOnlyList<TableStorage> Tables);

/// <summary>One table's storage.</summary>
/// <param name="Name">The table name (snake_case).</param>
/// <param name="TotalBytes">Table + indexes + TOAST, in bytes.</param>
/// <param name="RowEstimate">The planner's estimated live row count.</param>
public sealed record TableStorage(string Name, long TotalBytes, long RowEstimate);
