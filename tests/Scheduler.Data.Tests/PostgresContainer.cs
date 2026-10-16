using Npgsql;
using Testcontainers.PostgreSql;

[assembly: DoNotParallelize]

namespace Scheduler.Data.Tests;

/// <summary>
/// One PostgreSQL container for the whole test run (needs Docker). Each test gets its own database on it,
/// created by applying the real migrations, so the migrations are tested too.
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

    /// <summary>A connection string for a fresh, uniquely named database (created by the first migration).</summary>
    public static string NewDatabase() =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Database = $"t_{Guid.NewGuid():N}",
        }.ConnectionString;
}
