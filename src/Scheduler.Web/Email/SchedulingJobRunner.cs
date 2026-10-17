using Scheduler.Core.Services;

namespace Scheduler.Web.Email;

/// <summary>
/// Runs <see cref="ISchedulingJobs"/> on their schedule (docs/design.md, "Background jobs"): the tzdb check once at
/// startup, deadlines every 5 minutes, reminders every 15, the retention purge daily. Each pass gets its own scope,
/// and a failing pass is logged and retried on the next tick. Assumes a single replica.
/// </summary>
internal sealed class SchedulingJobRunner(IServiceScopeFactory scopes, ILogger<SchedulingJobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunAsync("tzdb check", (jobs, ct) => jobs.RecomputeSlotTimesAsync(ct), "Recomputed {Count} slot times for new time zone rules", stoppingToken);

        await Task.WhenAll(
            EveryAsync(TimeSpan.FromMinutes(5), "deadline", (jobs, ct) => jobs.ExpireOverdueEventsAsync(ct), "Expired {Count} events past their deadline", stoppingToken),
            EveryAsync(TimeSpan.FromMinutes(15), "reminders", (jobs, ct) => jobs.SendRemindersAsync(ct), "Queued {Count} reminders", stoppingToken),
            EveryAsync(TimeSpan.FromDays(1), "retention purge", (jobs, ct) => jobs.PurgeAsync(ct), "Purged {Count} closed events and stale drafts", stoppingToken));
    }

    private async Task EveryAsync(TimeSpan interval, string name, Func<ISchedulingJobs, CancellationToken, Task<int>> job, string message, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await RunAsync(name, job, message, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(string name, Func<ISchedulingJobs, CancellationToken, Task<int>> job, string message, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var count = await job(scope.ServiceProvider.GetRequiredService<ISchedulingJobs>(), stoppingToken);
            if (count > 0)
                logger.LogInformation(message, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The {Job} job failed; retrying next time", name);
        }
    }
}
