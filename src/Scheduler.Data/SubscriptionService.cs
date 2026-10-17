using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data;

/// <summary>EF Core implementation of <see cref="ISubscriptionService"/>. Changes are audited with no event ID.</summary>
public sealed class SubscriptionService(IDbContextFactory<SchedulerDbContext> dbFactory, IClock clock) : ISubscriptionService
{
    public async Task<bool> IsSuppressedAsync(string email, CancellationToken cancellationToken)
    {
        var address = EmailAddresses.Normalize(email);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Suppressions.AnyAsync(s => s.Email == address, cancellationToken);
    }

    public async Task UnsubscribeAsync(string email, CancellationToken cancellationToken)
    {
        var address = EmailAddresses.Normalize(email);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Suppressions.AnyAsync(s => s.Email == address, cancellationToken))
            return;

        var now = clock.GetCurrentInstant();
        db.Suppressions.Add(new EmailSuppression { Email = address, Reason = SuppressionReason.Unsubscribed, At = now });
        Audit(db, address, "email.unsubscribed", now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A second click (or the mail client's one-click POST) at the same moment is fine; anything else isn't.
            if (!await IsSuppressedAsync(address, cancellationToken))
                throw;
        }
    }

    public async Task ResubscribeAsync(string email, CancellationToken cancellationToken)
    {
        var address = EmailAddresses.Normalize(email);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var suppression = await db.Suppressions.FirstOrDefaultAsync(s => s.Email == address, cancellationToken);
        if (suppression is null)
            return;
        if (suppression.Reason != SuppressionReason.Unsubscribed)
            throw new SchedulingValidationException($"Email to {address} bounced or was reported as spam, so it stays turned off.");

        db.Suppressions.Remove(suppression);
        Audit(db, address, "email.resubscribed", clock.GetCurrentInstant());
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Audit(SchedulerDbContext db, string address, string action, Instant now) =>
        db.Audit.Add(new AuditEntry
        {
            At = now,
            Actor = address,
            Channel = Channel.Email,
            Action = action,
            Detail = JsonSerializer.Serialize(new { email = address }),
        });
}
