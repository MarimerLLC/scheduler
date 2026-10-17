using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;
using Scheduler.Core.Time;

namespace Scheduler.Data;

/// <summary>
/// EF Core implementation of <see cref="ISchedulingJobs"/>. Changes are audited as the system and their emails
/// go through the outbox in the same SaveChanges, as for <see cref="SchedulingService"/>.
/// </summary>
public sealed class SchedulingJobs(
    IDbContextFactory<SchedulerDbContext> dbFactory,
    IClock clock,
    IOptions<SchedulingOptions> options,
    MagicLinks magicLinks,
    IEmailComposer composer,
    IEventChanges changes) : ISchedulingJobs
{
    /// <summary>Events handled per pass; the next pass picks up the rest.</summary>
    private const int BatchSize = 100;

    private static readonly CallerIdentity System = new(null, [], Channel.System);

    private SchedulingOptions Options => options.Value;

    public async Task<int> ExpireOverdueEventsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        List<Guid> due;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            due = await db.Events
                .Where(e => e.State == EventState.Open && e.RespondBy != null && e.RespondBy <= now)
                .OrderBy(e => e.RespondBy)
                .Select(e => e.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        var expired = 0;
        foreach (var id in due)
        {
            // One context per event: an organizer editing it at the same moment only skips that one.
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var ev = await SchedulingService.EventsWithDetails(db).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            if (ev is not { State: EventState.Open, RespondBy: { } deadline } || deadline > now)
                continue;

            ev.Expire(now);
            ev.UpdatedAt = now;
            SchedulingService.Audit(db, ev, System, "event.expired", new { respondBy = deadline.ToString() }, now);
            SchedulingService.QueueEmail(db, ev, ev.OrganizerEmail,
                await composer.DeadlinePassedAsync(ev, SchedulingService.DashboardUrl(Options, ev)), now, replyToOrganizer: false);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue;
            }
            changes.Publish(ev.Id);
            expired++;
        }
        return expired;
    }

    public async Task<int> SendRemindersAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var lead = Duration.FromHours(Options.ReminderLeadHours);
        var horizon = now + lead;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var events = await SchedulingService.EventsWithDetails(db)
            .Where(e => e.State == EventState.Open && e.RespondBy != null && e.RespondBy > now && e.RespondBy <= horizon
                && e.Participants.Any(p => p.RespondedAt == null))
            .OrderBy(e => e.RespondBy)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var ev in events)
        {
            // The reminder window opens `lead` before the deadline. Someone invited after it opened has just had
            // the invitation; someone reminded before it opened was reminded about an earlier deadline.
            var windowOpened = ev.RespondBy!.Value - lead;
            var due = ev.Participants
                .Where(p => p.RespondedAt is null && p.InvitedAt < windowOpened && (p.LastRemindedAt is null || p.LastRemindedAt < windowOpened))
                .ToList();
            if (due.Count == 0)
                continue;

            foreach (var participant in due)
            {
                SchedulingService.QueueEmail(db, ev, participant.Email,
                    await composer.ReminderAsync(ev, participant, SchedulingService.VotingUrl(Options, magicLinks, ev, participant)), now);
                participant.LastRemindedAt = now;
            }
            SchedulingService.Audit(db, ev, System, "reminders.sent", new { participants = due.Count }, now);
            sent += due.Count;
        }

        await db.SaveChangesAsync(cancellationToken);
        return sent;
    }

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var closedBefore = now - Duration.FromDays(Options.RetentionDays);
        var draftsBefore = now - Duration.FromDays(Options.DraftRetentionDays);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.Events
            .Where(e => (e.State != EventState.Open && e.State != EventState.Draft && e.ClosedAt != null && e.ClosedAt < closedBefore)
                || (e.State == EventState.Draft && e.UpdatedAt < draftsBefore))
            .Select(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return 0;

        // Slots, participants and votes go with the event (cascading foreign keys); audit and outbox rows
        // only carry the event's ID, so they are deleted by it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Audit.Where(a => a.EventId != null && ids.Contains(a.EventId.Value)).ExecuteDeleteAsync(cancellationToken);
        await db.Outbox.Where(o => o.EventId != null && ids.Contains(o.EventId.Value)).ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.Events.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<int> RecomputeSlotTimesAsync(CancellationToken cancellationToken)
    {
        var version = DateTimeZoneProviders.Tzdb.VersionId;
        var now = clock.GetCurrentInstant();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == ServerSetting.TzdbVersion, cancellationToken);
        if (setting?.Value == version)
            return 0;

        var slots = await db.Slots
            .Where(s => s.Start > now && db.Events.Any(e => e.Id == s.EventId && e.State != EventState.Cancelled))
            .ToListAsync(cancellationToken);
        var moved = new HashSet<Guid>();
        var count = 0;
        foreach (var slot in slots)
        {
            // The local time is the source of truth. Leniently: a time the new rules skip moves forward, and an
            // ambiguous one takes the earlier offset, rather than losing the slot.
            var start = SlotTimeResolver.GetZone(slot.TimeZoneId).AtLeniently(slot.LocalStart).ToInstant();
            if (start == slot.Start)
                continue;
            slot.Start = start;
            moved.Add(slot.EventId);
            count++;
        }

        if (setting is null)
            db.ServerSettings.Add(new ServerSetting { Key = ServerSetting.TzdbVersion, Value = version, UpdatedAt = now });
        else
        {
            setting.Value = version;
            setting.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var eventId in moved)
            changes.Publish(eventId);
        return count;
    }
}
