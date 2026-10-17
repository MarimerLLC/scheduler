using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Scheduler.Core.Services;

namespace Scheduler.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database and the scheduling service. A context factory, not a scoped DbContext:
    /// a Blazor Server circuit lives for minutes or hours, far too long for one context, so every
    /// operation creates and disposes its own.
    /// </summary>
    public static IServiceCollection AddSchedulerData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<SchedulerDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime()));

        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddScoped<ISchedulingService, SchedulingService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IAccessTokenService, AccessTokenService>();
        return services;
    }
}
