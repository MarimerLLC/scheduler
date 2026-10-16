using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Scheduler.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without running the web host. The connection
/// string is a placeholder: generating a migration doesn't connect. Override with SCHEDULER_DB to apply one.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SchedulerDbContext>
{
    public SchedulerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SCHEDULER_DB")
            ?? "Host=localhost;Database=schedulerdb;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<SchedulerDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime())
            .Options;

        return new SchedulerDbContext(options);
    }
}
