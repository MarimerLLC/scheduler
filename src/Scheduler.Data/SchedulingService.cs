using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Core.Ranking;
using Scheduler.Core.Security;
using Scheduler.Core.Services;
using Scheduler.Core.Time;

namespace Scheduler.Data;

/// <summary>
/// EF Core implementation of <see cref="ISchedulingService"/>. Each state-changing method applies the change
/// through the domain model, appends an <see cref="AuditEntry"/> and queues its <see cref="OutboxEmail"/> rows,
/// all in one SaveChanges. Rules are specified in docs/design.md.
/// </summary>
public sealed class SchedulingService(
    IDbContextFactory<SchedulerDbContext> dbFactory,
    IClock clock,
    IOptions<SchedulingOptions> options,
    MagicLinks magicLinks,
    IEmailComposer composer) : ISchedulingService
{
    private const int CalendarUidMax = 512;
    private const int ListLimit = 100;

    private SchedulingOptions Options => options.Value;

    public async Task<EventView> CreateEventAsync(CallerIdentity caller, CreateEventRequest request, CancellationToken cancellationToken)
    {
        if (caller.MagicLinkEventId is not null || caller.VerifiedEmails.Count == 0)
            throw new SchedulingForbiddenException("Sign in to create scheduling events.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();

        var account = await FindAccountAsync(db, caller, cancellationToken);
        if (!await OrganizerPolicy.CanOrganizeAsync(db, Options, OwnEmails(caller, account), cancellationToken))
            throw new SchedulingForbiddenException("You aren't on this server's list of organizers. Ask its administrator to add you.");
        account ??= await ProvisionAccountAsync(db, caller, now, cancellationToken);

        if (request.DurationMinutes is < FieldLimits.MinDurationMinutes or > FieldLimits.MaxDurationMinutes)
            throw new SchedulingValidationException(
                $"The meeting length must be {FieldLimits.MinDurationMinutes} to {FieldLimits.MaxDurationMinutes} minutes.");

        var notices = new List<string>();
        var ev = new SchedulingEvent
        {
            ShortCode = await NewShortCodeAsync(db, cancellationToken),
            Title = RequireText(request.Title, FieldLimits.TitleMax, "title"),
            Description = OptionalText(request.Description, FieldLimits.DescriptionMax, "description"),
            Duration = Duration.FromMinutes(request.DurationMinutes),
            OrganizerId = account.Id,
            OrganizerEmail = caller.VerifiedEmails[0],
            OrganizerName = OptionalText(request.OrganizerName, FieldLimits.DisplayNameMax, "organizer name") ?? account.DisplayName,
            OrganizerTimeZone = SlotTimeResolver.GetZone(request.OrganizerTimeZone).Id,
            CreatedAt = now,
            UpdatedAt = now,
        };

        AddSlots(ev, request.Slots, round: 1, now);
        ev.RespondBy = ParseRespondBy(request.RespondBy, ev, now);
        AddParticipants(ev, request.Participants, OwnEmails(caller, account), now, notices);
        await EnforceOrganizerLimitsAsync(db, account, ev.Participants.Count, now, cancellationToken);
        ev.Open();

        db.Events.Add(ev);
        foreach (var participant in ev.Participants)
            QueueEmail(db, ev, participant.Email, composer.Invitation(ev, participant, VotingUrl(ev, participant)), now);
        Audit(db, ev, caller, "event.created", new { slots = ev.Slots.Count, participants = ev.Participants.Count }, now);

        await SaveAsync(db, cancellationToken);
        return ToView(ev, caller, account, viewerTimeZone: null, notices);
    }

    public async Task<IReadOnlyList<EventSummary>> ListMyEventsAsync(CallerIdentity caller, EventListFilter filter, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var emails = OwnEmails(caller, account).ToList();
        var accountId = caller.MagicLinkEventId is null ? account?.Id : null;

        var query = EventsWithDetails(db).AsNoTracking();
        if (caller.MagicLinkEventId is { } linkEventId)
            query = query.Where(e => e.Id == linkEventId);

        query = filter switch
        {
            EventListFilter.NeedsMyVote => query.Where(e => e.State == EventState.Open && e.Participants.Any(p => emails.Contains(p.Email))),
            EventListFilter.Organizing => query.Where(e => e.OrganizerId == accountId),
            _ => query.Where(e => e.OrganizerId == accountId || e.Participants.Any(p => emails.Contains(p.Email))),
        };

        var events = await query
            .OrderByDescending(e => e.CreatedAt)
            .Take(ListLimit)
            .ToListAsync(cancellationToken);

        return events
            .Select(e => new EventSummary(
                e.Id,
                e.ShortCode,
                e.Title,
                e.State,
                IsOrganizer: e.OrganizerId == accountId,
                NeedsMyVote: NeedsVote(e, emails),
                e.RespondBy is { } by ? SlotTimeFormatter.Display(by, e.OrganizerTimeZone) : null))
            .Where(s => filter != EventListFilter.NeedsMyVote || s.NeedsMyVote)
            .OrderByDescending(s => s.NeedsMyVote)
            .ToList();
    }

    public async Task<EventView> GetEventAsync(CallerIdentity caller, string eventRef, string? viewerTimeZone, CancellationToken cancellationToken)
    {
        var zone = string.IsNullOrWhiteSpace(viewerTimeZone) ? null : SlotTimeResolver.GetZone(viewerTimeZone).Id;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: false, cancellationToken);
        Authorize(ev, caller, account);
        return ToView(ev, caller, account, zone, notices: null);
    }

    public async Task<EventView> VoteAsync(CallerIdentity caller, string eventRef, IReadOnlyList<VoteInput> votes, string? note, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        var access = Authorize(ev, caller, account);

        if (access.Me.Count == 0)
            throw new SchedulingValidationException(
                "You're the organizer. The times you proposed already work for you, so organizers don't vote.");
        ev.EnsureAcceptingVotes();

        if (votes is null || votes.Count == 0)
            throw new SchedulingValidationException("Include at least one vote.");
        if (votes.Select(v => v.SlotId).Distinct().Count() != votes.Count)
            throw new SchedulingValidationException("Each slot can appear only once in a vote.");

        var active = ev.ActiveSlots.Select(s => s.Id).ToHashSet();
        foreach (var vote in votes)
        {
            if (!active.Contains(vote.SlotId))
                throw new SchedulingValidationException(
                    $"{vote.SlotId} isn't one of this event's current proposed times. Call get_event for the current slot IDs.");
            if (!Enum.IsDefined(vote.Value))
                throw new SchedulingValidationException("Each vote must be preferred, if_necessary or no.");
        }

        var noteText = note is null ? null : OptionalText(note, FieldLimits.NoteMax, "note");
        var everyoneHadResponded = EveryoneResponded(ev);

        foreach (var participant in access.Me)
        {
            foreach (var input in votes)
            {
                var existing = participant.Votes.FirstOrDefault(v => v.SlotId == input.SlotId);
                if (existing is null)
                {
                    existing = new Vote { ParticipantId = participant.Id, SlotId = input.SlotId };
                    participant.Votes.Add(existing);
                    db.Votes.Add(existing);
                }
                existing.Value = input.Value;
                existing.UpdatedAt = now;
                existing.Channel = caller.Channel;
            }

            if (note is not null)
                participant.Note = noteText;
            UpdateResponded(ev, participant, now);
        }

        Audit(db, ev, caller, "vote.cast", new { slots = votes.Count }, now);
        if (!everyoneHadResponded && EveryoneResponded(ev))
            QueueEmail(db, ev, ev.OrganizerEmail, composer.AllResponded(ev, DashboardUrl(ev)), now, replyToOrganizer: false);

        await SaveAsync(db, cancellationToken);
        return ToView(ev, caller, account, viewerTimeZone: null, notices: null);
    }

    public async Task<EventView> ProposeMoreSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput> slots, string? respondBy, uint expectedVersion, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));
        CheckVersion(db, ev, expectedVersion);

        var rescheduling = ev.State == EventState.Finalized;
        if (ev.State is EventState.Finalized or EventState.Expired)
            ev.Reopen();
        else
            ev.EnsureEditable();

        var round = ev.Slots.Count == 0 ? 1 : ev.Slots.Max(s => s.Round) + 1;
        var added = AddSlots(ev, slots, round, now);
        db.Slots.AddRange(added);

        var notices = new List<string>();
        if (!string.IsNullOrWhiteSpace(respondBy))
        {
            ev.RespondBy = ParseRespondBy(respondBy, ev, now);
        }
        else if (ev.RespondBy is { } deadline && deadline <= now)
        {
            ev.RespondBy = null;
            notices.Add("The previous voting deadline had passed, so it was cleared. Pass respondBy to set a new one.");
        }

        foreach (var participant in ev.Participants)
            UpdateResponded(ev, participant, now);

        Touch(db, ev, now);
        Audit(db, ev, caller, rescheduling ? "event.rescheduling" : "slots.proposed", new { round, slots = added.Count }, now);
        if (ev.State == EventState.Open)
        {
            foreach (var participant in ev.Participants)
                QueueEmail(db, ev, participant.Email, composer.NewSlots(ev, participant, VotingUrl(ev, participant), added, rescheduling), now);
        }

        await SaveAsync(db, cancellationToken);
        return ToView(ev, caller, account, viewerTimeZone: null, notices);
    }

    public async Task<EventView> WithdrawSlotAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));
        CheckVersion(db, ev, expectedVersion);
        ev.EnsureEditable();

        var slot = ev.ActiveSlots.FirstOrDefault(s => s.Id == slotId)
            ?? throw new SchedulingValidationException("That slot isn't one of this event's current proposed times.");
        if (ev.ActiveSlots.Count() == 1)
            throw new SchedulingValidationException("That's the only remaining time. Propose more times first, or cancel the event.");

        slot.WithdrawnAt = now;
        foreach (var participant in ev.Participants)
        {
            foreach (var vote in participant.Votes.Where(v => v.SlotId == slotId).ToList())
            {
                participant.Votes.Remove(vote);
                db.Votes.Remove(vote);
            }
            UpdateResponded(ev, participant, now);
        }

        Touch(db, ev, now);
        Audit(db, ev, caller, "slot.withdrawn", new { slotId }, now);

        await SaveAsync(db, cancellationToken);
        return ToView(ev, caller, account, viewerTimeZone: null, notices: null);
    }

    public async Task<FinalizeResult> FinalizeAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));
        CheckVersion(db, ev, expectedVersion);

        ev.Finalize(slotId, now);
        Touch(db, ev, now);
        var slot = ev.Slots.Single(s => s.Id == slotId);

        Audit(db, ev, caller, "event.finalized", new { slotId }, now);
        foreach (var participant in ev.Participants)
            QueueEmail(db, ev, participant.Email, composer.Finalized(ev, participant, slot), now);

        await SaveAsync(db, cancellationToken);
        return new FinalizeResult(
            ToView(ev, caller, account, viewerTimeZone: null, notices: null),
            SlotTimeFormatter.IsoLocalTime(slot.LocalStart),
            slot.TimeZoneId,
            (int)ev.Duration.TotalMinutes,
            ev.Participants.Select(p => p.Email).Order().ToList());
    }

    public async Task<EventView> CancelAsync(CallerIdentity caller, string eventRef, string? reason, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));

        var reasonText = OptionalText(reason, FieldLimits.NoteMax, "reason");
        var participantsWereNotified = ev.State != EventState.Draft;
        ev.Cancel(now);
        Touch(db, ev, now);

        Audit(db, ev, caller, "event.cancelled", new { reason = reasonText }, now);
        if (participantsWereNotified)
        {
            foreach (var participant in ev.Participants)
                QueueEmail(db, ev, participant.Email, composer.Cancelled(ev, participant, reasonText), now);
        }

        await SaveAsync(db, cancellationToken);
        return ToView(ev, caller, account, viewerTimeZone: null, notices: null);
    }

    public async Task RecordCalendarEventAsync(CallerIdentity caller, string eventRef, string calendarEventUid, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));

        if (ev.State != EventState.Finalized)
            throw new SchedulingConflictException("Finalize the event before recording its calendar event.");

        ev.CalendarEventUid = RequireText(calendarEventUid, CalendarUidMax, "calendar event UID");
        Touch(db, ev, now);
        Audit(db, ev, caller, "calendar.recorded", new { uid = ev.CalendarEventUid }, now);

        await SaveAsync(db, cancellationToken);
    }

    // ── Identity and access ─────────────────────────────────────────────────

    private sealed record Access(bool IsOrganizer, IReadOnlyList<Participant> Me);

    /// <summary>The caller's account: by ID if the identity layer gave one, else by any verified address.</summary>
    internal static async Task<UserAccount?> FindAccountAsync(SchedulerDbContext db, CallerIdentity caller, CancellationToken cancellationToken)
    {
        if (caller.MagicLinkEventId is not null)
            return null;

        if (caller.UserId is { } userId)
            return await db.Users.Include(u => u.Emails).FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        var emails = caller.VerifiedEmails.ToList();
        var ownerId = await db.UserEmails
            .Where(e => emails.Contains(e.Email))
            .Select(e => (Guid?)e.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        return ownerId is null
            ? null
            : await db.Users.Include(u => u.Emails).FirstAsync(u => u.Id == ownerId, cancellationToken);
    }

    /// <summary>
    /// A new account owning the caller's unclaimed verified addresses: on an allowlisted organizer's first event,
    /// or a person's first personal access token. The caller saves it.
    /// </summary>
    internal static async Task<UserAccount> ProvisionAccountAsync(SchedulerDbContext db, CallerIdentity caller, Instant now, CancellationToken cancellationToken)
    {
        var account = new UserAccount { CreatedAt = now };
        var emails = caller.VerifiedEmails.ToList();
        var claimed = await db.UserEmails.Where(e => emails.Contains(e.Email)).Select(e => e.Email).ToListAsync(cancellationToken);
        foreach (var email in emails.Except(claimed))
            account.Emails.Add(new UserEmail { Email = email, UserId = account.Id, VerifiedAt = now });
        db.Users.Add(account);
        return account;
    }

    /// <summary>
    /// Per-organizer limits over a rolling 24 hours, so an organizer's account (or a runaway agent) can't turn
    /// the server into a spam relay. Counts what is in the database, so cancelled events still count.
    /// </summary>
    private async Task EnforceOrganizerLimitsAsync(SchedulerDbContext db, UserAccount account, int invitations, Instant now, CancellationToken cancellationToken)
    {
        var dayAgo = now - Duration.FromDays(1);

        var events = await db.Events.CountAsync(e => e.OrganizerId == account.Id && e.CreatedAt > dayAgo, cancellationToken);
        if (events >= Options.MaxEventsPerDay)
            throw new SchedulingForbiddenException(
                $"You've created {events} events in the last 24 hours, the most this server allows. Try again later.");

        var invited = await db.Participants.CountAsync(
            p => p.InvitedAt > dayAgo && db.Events.Any(e => e.Id == p.EventId && e.OrganizerId == account.Id),
            cancellationToken);
        if (invited + invitations > Options.MaxInvitationsPerDay)
            throw new SchedulingForbiddenException(
                $"This event would invite {invitations} people, and you've invited {invited} in the last 24 hours. " +
                $"This server allows {Options.MaxInvitationsPerDay} invitations a day. Try again later, or invite fewer people.");
    }

    internal static HashSet<string> OwnEmails(CallerIdentity caller, UserAccount? account)
    {
        var emails = caller.VerifiedEmails.ToHashSet(StringComparer.Ordinal);
        if (account is not null)
            emails.UnionWith(account.Emails.Select(e => e.Email));
        return emails;
    }

    private static Access Authorize(SchedulingEvent ev, CallerIdentity caller, UserAccount? account)
    {
        if (caller.MagicLinkEventId is { } linkEventId && linkEventId != ev.Id)
            throw new SchedulingAccessException();

        var isOrganizer = caller.MagicLinkEventId is null && account is not null && account.Id == ev.OrganizerId;
        var emails = OwnEmails(caller, account);
        var me = ev.Participants.Where(p => emails.Contains(p.Email)).ToList();
        if (!isOrganizer && me.Count == 0)
            throw new SchedulingAccessException();

        return new Access(isOrganizer, me);
    }

    private static void RequireOrganizer(Access access)
    {
        if (!access.IsOrganizer)
            throw new SchedulingForbiddenException("Only the organizer can do that.");
    }

    // ── Loading and saving ──────────────────────────────────────────────────

    private static IQueryable<SchedulingEvent> EventsWithDetails(SchedulerDbContext db) =>
        db.Events
            .Include(e => e.Slots)
            .Include(e => e.Participants).ThenInclude(p => p.Votes)
            .AsSplitQuery();

    private static async Task<SchedulingEvent> LoadEventAsync(SchedulerDbContext db, string? eventRef, bool tracking, CancellationToken cancellationToken)
    {
        var query = EventsWithDetails(db);
        if (!tracking)
            query = query.AsNoTracking();

        var reference = eventRef?.Trim() ?? "";
        SchedulingEvent? ev;
        if (Guid.TryParse(reference, out var id))
        {
            ev = await query.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        }
        else
        {
            var code = Tokens.NormalizeShortCode(reference);
            ev = code.Length == 0 ? null : await query.FirstOrDefaultAsync(e => e.ShortCode == code, cancellationToken);
        }

        return ev ?? throw new SchedulingAccessException();
    }

    private static void CheckVersion(SchedulerDbContext db, SchedulingEvent ev, uint expectedVersion)
    {
        if (ev.Version != expectedVersion)
            throw StaleVersion();
        // Also checked by the database at save, in case another change lands in between.
        db.Entry(ev).Property(e => e.Version).OriginalValue = expectedVersion;
    }

    /// <summary>
    /// Marks an organizer-side change. The row is always written, even when nothing else on it changed
    /// (only child rows did, or within the same instant), so its version moves and the database checks it.
    /// </summary>
    private static void Touch(SchedulerDbContext db, SchedulingEvent ev, Instant now)
    {
        ev.UpdatedAt = now;
        db.Entry(ev).Property(e => e.UpdatedAt).IsModified = true;
    }

    private static SchedulingConflictException StaleVersion() =>
        new("This event changed since you last read it. Call get_event again and retry with its new version.");

    private static async Task SaveAsync(SchedulerDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw StaleVersion();
        }
    }

    private static async Task<string> NewShortCodeAsync(SchedulerDbContext db, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = Tokens.NewShortCode();
            if (!await db.Events.AnyAsync(e => e.ShortCode == code, cancellationToken))
                return code;
        }
        throw new InvalidOperationException("Couldn't generate a unique short code.");
    }

    // ── Validation and building ─────────────────────────────────────────────

    private static List<ProposedSlot> AddSlots(SchedulingEvent ev, IReadOnlyList<SlotInput>? inputs, int round, Instant now)
    {
        if (inputs is null || inputs.Count == 0)
            throw new SchedulingValidationException("Propose at least one time.");
        if (ev.ActiveSlots.Count() + inputs.Count > FieldLimits.MaxSlotsPerEvent)
            throw new SchedulingValidationException($"An event can have at most {FieldLimits.MaxSlotsPerEvent} proposed times.");

        var added = new List<ProposedSlot>();
        foreach (var input in inputs)
        {
            var time = SlotTimeResolver.Resolve(input?.Start, input?.TimeZone);
            var display = SlotTimeFormatter.Display(time.Start, time.TimeZoneId);
            if (time.Start <= now)
                throw new SchedulingValidationException($"{display} is in the past.");
            if (ev.ActiveSlots.Any(s => s.Start == time.Start))
                throw new SchedulingValidationException($"{display} is proposed more than once.");

            var slot = new ProposedSlot
            {
                EventId = ev.Id,
                LocalStart = time.LocalStart,
                TimeZoneId = time.TimeZoneId,
                Start = time.Start,
                Round = round,
            };
            ev.Slots.Add(slot);
            added.Add(slot);
        }
        return added;
    }

    private void AddParticipants(SchedulingEvent ev, IReadOnlyList<ParticipantInput>? inputs, HashSet<string> organizerEmails, Instant now, List<string> notices)
    {
        if (inputs is null || inputs.Count == 0)
            throw new SchedulingValidationException("Invite at least one person.");

        var byEmail = new Dictionary<string, Participant>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var email = EmailAddresses.Normalize(input?.Email);
            if (organizerEmails.Contains(email))
            {
                notices.Add($"{email} is you, the organizer, so it wasn't added as a participant.");
                continue;
            }

            var name = OptionalText(input!.DisplayName, FieldLimits.DisplayNameMax, "display name");
            if (byEmail.TryGetValue(email, out var existing))
            {
                if (input.Role == AttendeeRole.Required)
                    existing.Role = AttendeeRole.Required;
                existing.DisplayName ??= name;
                notices.Add($"{email} was listed more than once; it was invited once.");
                continue;
            }

            byEmail[email] = new Participant
            {
                EventId = ev.Id,
                Email = email,
                DisplayName = name,
                Role = input.Role,
                TokenNonce = MagicLinks.NewNonce(),
                InvitedAt = now,
            };
        }

        if (byEmail.Count == 0)
            throw new SchedulingValidationException("Invite at least one person other than yourself.");
        if (byEmail.Count > FieldLimits.MaxParticipantsPerEvent)
            throw new SchedulingValidationException($"An event can have at most {FieldLimits.MaxParticipantsPerEvent} participants.");

        ev.Participants.AddRange(byEmail.Values);
    }

    /// <summary>A deadline as local wall time in the organizer's zone; it must fall before the last proposed time.</summary>
    private static Instant? ParseRespondBy(string? respondBy, SchedulingEvent ev, Instant now)
    {
        if (string.IsNullOrWhiteSpace(respondBy))
            return null;

        var deadline = SlotTimeResolver.Resolve(respondBy, ev.OrganizerTimeZone).Start;
        if (deadline <= now)
            throw new SchedulingValidationException("The voting deadline is in the past.");
        if (deadline >= ev.ActiveSlots.Max(s => s.Start))
            throw new SchedulingValidationException("The voting deadline must be before the last proposed time.");
        return deadline;
    }

    private static string RequireText(string? value, int max, string name) =>
        OptionalText(value, max, name) ?? throw new SchedulingValidationException($"A {name} is required.");

    private static string? OptionalText(string? value, int max, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length > max)
            throw new SchedulingValidationException($"The {name} can be at most {max} characters.");
        return trimmed;
    }

    // ── Voting state ────────────────────────────────────────────────────────

    private static void UpdateResponded(SchedulingEvent ev, Participant participant, Instant now)
    {
        var answeredAll = ev.ActiveSlots.All(s => participant.Votes.Any(v => v.SlotId == s.Id));
        participant.RespondedAt = answeredAll ? participant.RespondedAt ?? now : null;
    }

    /// <summary>Every required participant has answered every slot (every participant, if none is required).</summary>
    private static bool EveryoneResponded(SchedulingEvent ev)
    {
        var required = ev.Participants.Where(p => p.Role == AttendeeRole.Required).ToList();
        var considered = required.Count > 0 ? required : ev.Participants;
        return considered.All(p => p.RespondedAt is not null);
    }

    private static bool NeedsVote(SchedulingEvent ev, IReadOnlyCollection<string> emails) =>
        ev.State == EventState.Open
        && ev.Participants
            .Where(p => emails.Contains(p.Email))
            .Any(p => ev.ActiveSlots.Any(s => p.Votes.All(v => v.SlotId != s.Id)));

    // ── Side effects ────────────────────────────────────────────────────────

    private string VotingUrl(SchedulingEvent ev, Participant participant) =>
        $"{Options.PublicBaseUrl.TrimEnd('/')}/e/{ev.ShortCode}?t={magicLinks.CreateToken(participant.Id, participant.TokenNonce)}";

    private string DashboardUrl(SchedulingEvent ev) => $"{Options.PublicBaseUrl.TrimEnd('/')}/events/{ev.Id}";

    private static void QueueEmail(SchedulerDbContext db, SchedulingEvent ev, string to, ComposedEmail email, Instant now, bool replyToOrganizer = true) =>
        db.Outbox.Add(new OutboxEmail
        {
            EventId = ev.Id,
            To = to,
            ReplyTo = replyToOrganizer ? ev.OrganizerEmail : null,
            Subject = email.Subject,
            TextBody = email.TextBody,
            HtmlBody = email.HtmlBody,
            CreatedAt = now,
            NextAttemptAt = now,
        });

    private static void Audit(SchedulerDbContext db, SchedulingEvent ev, CallerIdentity caller, string action, object detail, Instant now) =>
        db.Audit.Add(new AuditEntry
        {
            EventId = ev.Id,
            At = now,
            Actor = caller.VerifiedEmails.FirstOrDefault(),
            Channel = caller.Channel,
            Action = action,
            Detail = JsonSerializer.Serialize(detail),
        });

    // ── Views ───────────────────────────────────────────────────────────────

    private static EventView ToView(SchedulingEvent ev, CallerIdentity caller, UserAccount? account, string? viewerTimeZone, IReadOnlyList<string>? notices)
    {
        var emails = OwnEmails(caller, account);
        var isOrganizer = caller.MagicLinkEventId is null && account?.Id == ev.OrganizerId;
        var me = ev.Participants.Where(p => emails.Contains(p.Email)).ToHashSet();
        var myVotes = me.SelectMany(p => p.Votes).GroupBy(v => v.SlotId).ToDictionary(g => g.Key, g => g.First().Value);
        var ranked = SlotRanker.Rank(ev).Select((score, index) => (score, rank: index + 1)).ToDictionary(x => x.score.SlotId);

        var slots = ev.ActiveSlots
            .OrderBy(s => s.Start)
            .Select(s =>
            {
                var (score, rank) = ranked[s.Id];
                return new SlotView(
                    s.Id,
                    SlotTimeFormatter.Utc(s.Start),
                    SlotTimeFormatter.Display(s.Start, ev.OrganizerTimeZone),
                    viewerTimeZone is null ? null : SlotTimeFormatter.Display(s.Start, viewerTimeZone),
                    new SlotTally(score.Preferred, score.IfNecessary, score.No, score.Pending),
                    myVotes.TryGetValue(s.Id, out var mine) ? mine : null,
                    isOrganizer ? rank : null,
                    isOrganizer ? score.Viable : null);
            })
            .ToList();

        var participants = ev.Participants
            .OrderBy(p => p.Email, StringComparer.Ordinal)
            .Select(p => isOrganizer || me.Contains(p)
                ? new ParticipantView(p.Email, p.DisplayName, p.Role, p.RespondedAt is not null, p.Note,
                    p.Votes.ToDictionary(v => v.SlotId, v => v.Value))
                : new ParticipantView(p.Email, p.DisplayName, p.Role, null, null, null))
            .ToList();

        return new EventView(
            ev.Id,
            ev.ShortCode,
            ev.Title,
            ev.Description,
            (int)ev.Duration.TotalMinutes,
            ev.State,
            ev.OrganizerEmail,
            ev.OrganizerName,
            ev.OrganizerTimeZone,
            ev.RespondBy is { } by ? SlotTimeFormatter.Display(by, ev.OrganizerTimeZone) : null,
            ev.FinalizedSlotId,
            ev.Version,
            isOrganizer,
            slots,
            participants,
            notices is { Count: > 0 } ? notices : null);
    }
}
