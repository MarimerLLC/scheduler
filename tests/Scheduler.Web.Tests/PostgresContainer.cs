using Npgsql;
using Testcontainers.PostgreSql;

[assembly: DoNotParallelize]

namespace Scheduler.Web.Tests;

/// <summary>
/// One PostgreSQL container for the whole test run (needs Docker). Each app instance gets its own database
/// on it, migrated by the app at startup (Development).
/// </summary>
[TestClass]
public static class PostgresContainer
{
    private static PostgreSqlContainer? _container;

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();
    }

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public static string NewDatabase() =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Database = $"t_{Guid.NewGuid():N}",
            // Every test has its own database, so its own connection pool. Close idle connections quickly, or
            // finished tests' pools exhaust the server's connections.
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
}
