using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scheduler.Core.Email;
using Scheduler.Core.Security;
using Scheduler.Core.Services;
using Scheduler.Data;

namespace Scheduler.Web;

internal static class SchedulerSetup
{
    /// <summary>Scheduling options, voting-link signing, the email composer, and the database.</summary>
    public static void AddScheduler(this WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("schedulerdb")
            ?? throw new InvalidOperationException("Connection string 'schedulerdb' is not configured.");
        builder.Services.AddSchedulerData(connectionString);

        builder.Services.Configure<SchedulingOptions>(builder.Configuration.GetSection(SchedulingOptions.SectionName));
        builder.Services.AddSingleton(sp => new MagicLinks(MagicLinkKey(
            sp.GetRequiredService<IOptions<SchedulingOptions>>().Value,
            builder.Environment,
            sp.GetRequiredService<ILogger<MagicLinks>>())));
        builder.Services.AddSingleton<IEmailComposer>(sp => new PlainTextEmailComposer(
            sp.GetRequiredService<IOptions<SchedulingOptions>>().Value.PublicBaseUrl.TrimEnd('/') + "/mcp"));
    }

    /// <summary>
    /// Development only: applies migrations at startup so <c>dotnet run</c> under Aspire just works.
    /// Other environments run a migration bundle before rollout (docs/design.md, "Deployment").
    /// </summary>
    public static async Task MigrateDevelopmentDatabaseAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        var factory = app.Services.GetRequiredService<IDbContextFactory<SchedulerDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    private static byte[] MagicLinkKey(SchedulingOptions options, IHostEnvironment environment, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.MagicLinkKey))
            return Convert.FromBase64String(options.MagicLinkKey);

        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "Scheduler:MagicLinkKey is not configured. Set it to a base64 value of at least 32 random bytes.");

        logger.LogWarning("Scheduler:MagicLinkKey is not set; using a random key. Voting links stop working when the app restarts.");
        return MagicLinks.NewKey();
    }
}
