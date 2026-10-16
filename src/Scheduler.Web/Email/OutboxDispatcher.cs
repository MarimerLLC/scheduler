using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Data;

namespace Scheduler.Web.Email;

/// <summary>
/// Delivers <see cref="OutboxEmail"/> rows. Each message is saved as soon as it is sent, so a crash
/// mid-batch resends at most one email. Failures back off exponentially and give up after
/// <see cref="MaxAttempts"/>. Assumes a single replica; scaling out needs row claiming
/// (<c>FOR UPDATE SKIP LOCKED</c>). See docs/design.md, "Email".
/// </summary>
internal sealed class OutboxDispatcher(
    IDbContextFactory<SchedulerDbContext> dbFactory,
    IEmailSender sender,
    IClock clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly Duration MaxBackoff = Duration.FromHours(1);
    private const int BatchSize = 20;
    private const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch failed; retrying next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();

        var due = await db.Outbox
            .Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptAt <= now)
            .OrderBy(o => o.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var email in due)
        {
            if (await db.Suppressions.AnyAsync(s => s.Email == email.To, cancellationToken))
            {
                email.Status = OutboxStatus.Suppressed;
            }
            else
            {
                try
                {
                    email.ProviderMessageId = await sender.SendAsync(
                        new OutboundEmail(email.To, email.ReplyTo, email.Subject, email.TextBody, email.HtmlBody, email.ListUnsubscribeUrl),
                        cancellationToken);
                    email.Status = OutboxStatus.Sent;
                    email.SentAt = clock.GetCurrentInstant();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    email.Attempts++;
                    email.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    if (email.Attempts >= MaxAttempts)
                    {
                        email.Status = OutboxStatus.Failed;
                        logger.LogError(ex, "Giving up on outbox email {Id} after {Attempts} attempts", email.Id, email.Attempts);
                    }
                    else
                    {
                        var backoff = Duration.FromMinutes(Math.Pow(2, email.Attempts));
                        email.NextAttemptAt = clock.GetCurrentInstant() + (backoff < MaxBackoff ? backoff : MaxBackoff);
                        logger.LogWarning(ex, "Outbox email {Id} failed (attempt {Attempts}); will retry", email.Id, email.Attempts);
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
