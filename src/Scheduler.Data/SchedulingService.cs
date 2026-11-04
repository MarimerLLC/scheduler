using System.Security.Cryptography;
using System.Text;
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
    IEmailComposer composer,
    IEventChanges changes) : ISchedulingService
{
    private const int CalendarUidMax = 512;
    private const int ListLimit = 100;

    /// <summary>A vote that changes nothing doesn't email another copy of the answers within this long of the last.</summary>
    private static readonly Duration ReceiptRepeatWindow = Duration.FromMinutes(10);

    private SchedulingOptions Options => options.Value;

    public async Task<EventView> CreateEventAsync(CallerIdentity caller, CreateEventRequest request, CancellationToken cancellationToken)
    {
        if (caller.MagicLinkEventId is not null || caller.VerifiedEmails.Count == 0)
            throw new SchedulingForbiddenException("Sign in to create scheduling events.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();

        var account = await FindAccountAsync(db, caller, cancellationToken);
        if (!await OrganizerPolicy.CanOrganizeAsync(db, Options, OwnEmails(caller, account), cancellationToken))
            throw new SchedulingForbiddenException("You aren't on this server's list of organizers. Ask its admins to add you (the request_to_organize tool, or the page /ask-to-organize).");
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
        await AddSuppressionNoticesAsync(db, ev, notices, cancellationToken);
        ev.Open();

        db.Events.Add(ev);
        foreach (var participant in ev.Participants)
            QueueEmail(db, ev, participant.Email, await composer.InvitationAsync(ev, participant, VotingUrl(ev, participant)), now);
        Audit(db, ev, caller, "event.created", new { slots = ev.Slots.Count, participants = ev.Participants.Count }, now);

        await SaveAsync(db, ev, cancellationToken);
        return ToView(ev, caller, account, viewerTimeZone: null, notices);
    }

    public async Task<CallerIdentity?> ResolveVotingLinkAsync(string eventRef, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrWhiteSpace(eventRef))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var code = Tokens.NormalizeShortCode(eventRef);
        var events = Guid.TryParse(eventRef.Trim(), out var id)
            ? db.Events.Where(e => e.Id == id)
            : db.Events.Where(e => e.ShortCode == code);
        var participants = await db.Participants.AsNoTracking()
            .Where(p => events.Any(e => e.Id == p.EventId))
            .Select(p => new { p.Id, p.EventId, p.Email, p.TokenNonce })
            .ToListAsync(cancellationToken);

        // Every participant is checked, so the time taken doesn't say which one (if any) matched.
        CallerIdentity? caller = null;
        foreach (var participant in participants)
        {
            if (magicLinks.Matches(token, participant.Id, participant.TokenNonce))
                caller = new CallerIdentity(null, [participant.Email], Channel.Web, MagicLinkEventId: participant.EventId);
        }
        return caller;
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
                e.RespondBy is { } by ? SlotTimeFormatter.Display(by, e.OrganizerTimeZone) : null,
                Responded: e.OrganizerId == accountId ? e.Participants.Count(p => p.RespondedAt is not null) : null,
                Invited: e.OrganizerId == accountId ? e.Participants.Count : null,
                PendingSuggestions: e.OrganizerId == accountId ? e.Suggestions.Count(s => s.Status == SuggestionStatus.Pending) : null))
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

    public async Task<EventStatusView> GetEventStatusAsync(CallerIdentity caller, string eventRef, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: false, cancellationToken);
        var access = Authorize(ev, caller, account);
        return ToStatus(ev, access.IsOrganizer);
    }

    public async Task<EventView> VoteAsync(CallerIdentity caller, string eventRef, IReadOnlyList<VoteInput> votes, string? note, bool emailReceipt, IReadOnlyCollection<Guid>? unansweredAsNo, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: false, cancellationToken);
        var access = Authorize(ev, caller, account);

        // One person's votes are saved one call at a time: a double submit, or their agent voting while the page saves,
        // would otherwise both find a time unanswered and insert the same answer. This locks the caller's participant
        // rows, not the event's, so the organizer's version doesn't move; then it reads the event again, with whatever
        // the other call saved.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockParticipantsAsync(db, access.Me, cancellationToken);
        ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        access = Authorize(ev, caller, account);

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
                throw new SchedulingValidationException("Each vote must be preferred, yes, if_necessary or no.");
        }

        var noteText = note is null ? null : OptionalText(note, FieldLimits.NoteMax, "note");
        var everyoneHadResponded = EveryoneResponded(ev);
        var alreadyRuledOutEveryTime = access.Me.Where(ev.RulesOutEveryTime).ToHashSet();
        var changed = new HashSet<Participant>();
        var answered = votes.Select(v => v.SlotId).ToHashSet();
        var leftBlank = (unansweredAsNo ?? []).Where(id => active.Contains(id) && !answered.Contains(id)).ToHashSet();
        var savedAsNo = new HashSet<Guid>();

        foreach (var participant in access.Me)
        {
            // A time the form showed and the voter left blank doesn't work for them, unless they've answered it already.
            var blanks = leftBlank
                .Where(id => participant.Votes.All(v => v.SlotId != id))
                .Select(id => new VoteInput(id, VoteValue.No))
                .ToList();
            savedAsNo.UnionWith(blanks.Select(b => b.SlotId));

            foreach (var input in votes.Concat(blanks))
            {
                var existing = participant.Votes.FirstOrDefault(v => v.SlotId == input.SlotId);
                if (existing is null)
                {
                    existing = new Vote { ParticipantId = participant.Id, SlotId = input.SlotId };
                    participant.Votes.Add(existing);
                    db.Votes.Add(existing);
                    changed.Add(participant);
                }
                else if (existing.Value != input.Value)
                {
                    changed.Add(participant);
                }
                existing.Value = input.Value;
                existing.UpdatedAt = now;
                existing.Channel = caller.Channel;
            }

            if (note is not null && participant.Note != noteText)
            {
                participant.Note = noteText;
                changed.Add(participant);
            }
            UpdateResponded(ev, participant, now);
        }

        List<string>? notices = null;
        if (savedAsNo.Count > 0)
        {
            notices = [savedAsNo.Count == 1
                ? "You left 1 time blank, so it's saved as No."
                : $"You left {savedAsNo.Count} times blank, so they're saved as No."];
        }
        var receipts = new List<string>();
        if (emailReceipt)
        {
            notices ??= [];
            foreach (var participant in access.Me)
            {
                if (await QueueVoteReceiptAsync(db, ev, participant, unchanged: !changed.Contains(participant), now, cancellationToken) is { } skipped)
                    notices.Add(skipped);
                else
                {
                    receipts.Add(participant.Email);
                    notices.Add($"A copy of your answers is on its way to {participant.Email}.");
                }
            }
        }

        Audit(db, ev, caller, "vote.cast", new { slots = votes.Count, blankAsNo = savedAsNo.Count, receipt = receipts }, now);
        foreach (var participant in access.Me.Where(p => !alreadyRuledOutEveryTime.Contains(p) && ev.RulesOutEveryTime(p)))
            QueueEmail(db, ev, ev.OrganizerEmail, await composer.CantMakeAnyTimeAsync(ev, participant, DashboardUrl(ev)), now, replyToOrganizer: false);
        if (!everyoneHadResponded && EveryoneResponded(ev))
            QueueEmail(db, ev, ev.OrganizerEmail, await composer.AllRespondedAsync(ev, DashboardUrl(ev)), now, replyToOrganizer: false);

        // Votes don't touch the event row, so there is no version to conflict on. Open pages hear about it once it is
        // committed.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        changes.Publish(ev.Id);
        return ToView(ev, caller, account, viewerTimeZone: null, notices);
    }

    /// <summary>
    /// Queues a copy of <paramref name="participant"/>'s answers, unless the address takes no email from this server, or
    /// the vote changed nothing and the same copy was queued moments ago (a retry). Returns why it wasn't sent, or null.
    /// </summary>
    private async Task<string?> QueueVoteReceiptAsync(SchedulerDbContext db, SchedulingEvent ev, Participant participant, bool unchanged, Instant now, CancellationToken cancellationToken)
    {
        var suppression = await db.Suppressions.AsNoTracking().FirstOrDefaultAsync(s => s.Email == participant.Email, cancellationToken);
        if (suppression is not null)
        {
            return suppression.Reason == SuppressionReason.Unsubscribed
                ? $"{participant.Email} has asked this server not to email it, so no copy of your answers was sent. Your answers are saved."
                : $"Email to {participant.Email} has bounced before, so no copy of your answers was sent. Your answers are saved.";
        }

        var receipt = await composer.VoteReceiptAsync(ev, participant, VotingUrl(ev, participant));
        if (unchanged)
        {
            var since = now - ReceiptRepeatWindow;
            if (await db.Outbox.AnyAsync(o => o.EventId == ev.Id && o.To == participant.Email && o.Subject == receipt.Subject && o.CreatedAt >= since, cancellationToken))
                return $"A copy of these same answers went to {participant.Email} a few minutes ago, so another wasn't sent.";
        }

        QueueEmail(db, ev, participant.Email, receipt, now);
        return null;
    }

    public async Task<EventView> SuggestSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput> slots, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        var access = Authorize(ev, caller, account);

        if (access.Me.Count == 0)
            throw new SchedulingValidationException(
                "You're the organizer. Propose more times instead: suggestions are for the people invited.");
        if (ev.State != EventState.Open)
            throw new SchedulingConflictException(
                $"This event is {ev.State.ToString().ToLowerInvariant()} and isn't taking suggestions.");
        if (slots is null || slots.Count == 0)
            throw new SchedulingValidationException("Suggest at least one time.");
        if (slots.Count > FieldLimits.MaxPendingSuggestionsPerParticipant)
            throw new SchedulingValidationException(
                $"Suggest at most {FieldLimits.MaxPendingSuggestionsPerParticipant} times at once.");

        // One person's suggestions are checked and saved one call at a time: a double submit, or a retry racing the first
        // try, would otherwise both pass the duplicate and limit checks below. This locks the caller's participant rows,
        // not the event's, so the organizer's version doesn't move; then it reads the suggestions saved meanwhile.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockParticipantsAsync(db, access.Me, cancellationToken);
        await db.Suggestions.Where(s => s.EventId == ev.Id).LoadAsync(cancellationToken);

        var mine = access.Me.Select(p => p.Id).ToHashSet();
        var earlier = ev.Suggestions.Where(s => mine.Contains(s.ParticipantId)).ToList();
        var suggester = access.Me.OrderBy(p => p.Email, StringComparer.Ordinal).First();
        var notices = new List<string>();
        var added = new List<SlotSuggestion>();
        var seen = new HashSet<Instant>();
        foreach (var input in slots)
        {
            var time = SlotTimeResolver.Resolve(input?.Start, input?.TimeZone);
            var display = SlotTimeFormatter.Display(time.Start, ev.OrganizerTimeZone);
            if (time.Start <= now)
                throw new SchedulingValidationException($"{display} is in the past.");
            if (!seen.Add(time.Start))
                continue;
            if (ev.ActiveSlots.Any(s => s.Start == time.Start))
            {
                notices.Add($"{display} is already one of the proposed times, so it wasn't suggested. Answer it instead.");
                continue;
            }
            if (earlier.Where(s => s.Start == time.Start).MaxBy(s => s.SuggestedAt) is { } before)
            {
                notices.Add(before.Status switch
                {
                    SuggestionStatus.Pending => $"You already suggested {display}; it's waiting for the organizer.",
                    SuggestionStatus.Dismissed => $"The organizer decided not to add {display}, so it wasn't suggested again.",
                    _ => $"The organizer added {display} and then withdrew it, so it wasn't suggested again.",
                });
                continue;
            }

            added.Add(new SlotSuggestion
            {
                EventId = ev.Id,
                ParticipantId = suggester.Id,
                LocalStart = time.LocalStart,
                TimeZoneId = time.TimeZoneId,
                Start = time.Start,
                SuggestedAt = now,
            });
        }

        var waiting = earlier.Count(s => s.Status == SuggestionStatus.Pending && s.Start > now);
        if (waiting + added.Count > FieldLimits.MaxPendingSuggestionsPerParticipant)
            throw new SchedulingValidationException(
                $"You can have at most {FieldLimits.MaxPendingSuggestionsPerParticipant} suggested times waiting for the organizer, " +
                $"and {waiting} of yours already are. Wait for the organizer to add or dismiss them, or suggest fewer.");

        if (added.Count > 0)
        {
            ev.Suggestions.AddRange(added);
            db.Suggestions.AddRange(added);
            Audit(db, ev, caller, "slots.suggested", new { suggestions = added.Count }, now);
            QueueEmail(db, ev, ev.OrganizerEmail, await composer.SlotsSuggestedAsync(ev, suggester, added, DashboardUrl(ev)), now, replyToOrganizer: false);
            notices.Insert(0, added.Count == 1
                ? "Your suggested time was sent to the organizer, who decides whether to add it to the poll."
                : $"Your {added.Count} suggested times were sent to the organizer, who decides whether to add them to the poll.");
            // Nothing here touches the event row, so there is no version to conflict on. Open pages hear about it once
            // it is committed.
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            changes.Publish(ev.Id);
        }
        return ToView(ev, caller, account, viewerTimeZone: null, notices);
    }

    public async Task<EventView> DismissSuggestionsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<Guid> suggestionIds, uint expectedVersion, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));
        CheckVersion(db, ev, expectedVersion);

        if (suggestionIds is null || suggestionIds.Count == 0)
            throw new SchedulingValidationException("Name at least one suggested time to dismiss.");

        var dismissed = 0;
        foreach (var suggestion in ChooseSuggestions(ev, suggestionIds))
        {
            if (suggestion.Status == SuggestionStatus.Added)
                throw new SchedulingValidationException(
                    $"{SlotTimeFormatter.Display(suggestion.Start, ev.OrganizerTimeZone)} is already on the poll. Withdraw that time instead.");
            if (suggestion.Status == SuggestionStatus.Dismissed)
                continue;
            suggestion.Status = SuggestionStatus.Dismissed;
            suggestion.DecidedAt = now;
            dismissed++;
        }

        if (dismissed > 0)
        {
            Touch(db, ev, now);
            Audit(db, ev, caller, "suggestions.dismissed", new { suggestions = dismissed }, now);
            await SaveAsync(db, ev, cancellationToken);
        }
        return ToView(ev, caller, account, viewerTimeZone: null, notices: null);
    }

    /// <summary>The event's suggestions with these IDs, each once. An ID that isn't one of them is refused.</summary>
    private static List<SlotSuggestion> ChooseSuggestions(SchedulingEvent ev, IReadOnlyList<Guid>? suggestionIds)
    {
        var chosen = new List<SlotSuggestion>();
        foreach (var id in (suggestionIds ?? []).Distinct())
        {
            chosen.Add(ev.Suggestions.FirstOrDefault(s => s.Id == id)
                ?? throw new SchedulingValidationException(
                    $"{id} isn't one of this event's suggested times. Call get_event for the current suggestion IDs."));
        }
        return chosen;
    }

    /// <summary>
    /// Marks the suggestions that the newly added <paramref name="slots"/> put on the poll: the ones the organizer
    /// chose (at the time each resolves to now), and any other waiting suggestion of the same time.
    /// </summary>
    private static void MarkSuggestionsAdded(SchedulingEvent ev, IReadOnlyList<ProposedSlot> slots, IReadOnlyList<(SlotSuggestion Suggestion, SlotTime Time)> chosen, Instant now)
    {
        foreach (var (suggestion, time) in chosen)
        {
            suggestion.Start = time.Start;
            Mark(suggestion, slots.Single(slot => slot.Start == time.Start));
        }
        foreach (var suggestion in ev.Suggestions.Where(s => s.Status == SuggestionStatus.Pending))
        {
            if (slots.FirstOrDefault(slot => slot.Start == suggestion.Start) is { } slot)
                Mark(suggestion, slot);
        }

        void Mark(SlotSuggestion suggestion, ProposedSlot slot)
        {
            suggestion.Status = SuggestionStatus.Added;
            suggestion.SlotId = slot.Id;
            suggestion.DecidedAt = now;
        }
    }

    public async Task<EventView> ProposeMoreSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput>? slots, IReadOnlyList<Guid>? suggestionIds, string? respondBy, uint expectedVersion, CancellationToken cancellationToken)
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

        var suggested = ChooseSuggestions(ev, suggestionIds);
        var alreadyAdded = suggested.FirstOrDefault(s => s.Status == SuggestionStatus.Added);
        if (alreadyAdded is not null)
            throw new SchedulingValidationException(
                $"The suggested time {SlotTimeFormatter.Display(alreadyAdded.Start, ev.OrganizerTimeZone)} was already added to the poll.");

        var round = ev.Slots.Count == 0 ? 1 : ev.Slots.Max(s => s.Round) + 1;
        // The organizer's own times are checked strictly. A suggested time is resolved from its stored local time the way
        // the time-zone job does (seconds and all), since it was valid when suggested. One already in this round (the
        // organizer's own, or two people suggesting the same moment) is proposed once.
        var times = ResolveSlots(slots);
        var chosen = suggested.Select(s => (Suggestion: s, Time: SlotTimeResolver.ResolveStored(s.LocalStart, s.TimeZoneId))).ToList();
        foreach (var (_, time) in chosen)
        {
            if (times.All(t => t.Start != time.Start))
                times.Add(time);
        }
        var added = AddSlots(ev, times, round, now);
        db.Slots.AddRange(added);
        MarkSuggestionsAdded(ev, added, chosen, now);

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
        Audit(db, ev, caller, rescheduling ? "event.rescheduling" : "slots.proposed", new { round, slots = added.Count, suggestions = suggested.Count }, now);
        if (ev.State == EventState.Open)
        {
            foreach (var participant in ev.Participants)
                QueueEmail(db, ev, participant.Email, await composer.NewSlotsAsync(ev, participant, VotingUrl(ev, participant), added, rescheduling), now);
        }

        await SaveAsync(db, ev, cancellationToken);
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

        await SaveAsync(db, ev, cancellationToken);
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
            QueueEmail(db, ev, participant.Email, await composer.FinalizedAsync(ev, participant, slot), now);

        await SaveAsync(db, ev, cancellationToken);
        return new FinalizeResult(
            ToView(ev, caller, account, viewerTimeZone: null, notices: null),
            SlotTimeFormatter.IsoLocalTime(slot.LocalStart),
            slot.TimeZoneId,
            (int)ev.Duration.TotalMinutes,
            ev.Participants.Select(p => p.Email).Order().ToList());
    }

    public async Task<ResendResult> ResendInvitationsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<string>? emails, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));

        if (ev.State != EventState.Open)
            throw new SchedulingConflictException(
                $"This event is {ev.State.ToString().ToLowerInvariant()} and isn't taking votes, so its invitation can't be resent." +
                (ev.State is EventState.Expired or EventState.Finalized ? " Propose more times to reopen voting." : ""));

        var chosen = ChooseParticipants(ev, emails);
        var addresses = chosen.Select(p => p.Email).ToList();
        var suppressed = await db.Suppressions.AsNoTracking()
            .Where(s => addresses.Contains(s.Email))
            .ToDictionaryAsync(s => s.Email, s => s.Reason, cancellationToken);
        var cooldown = Duration.FromMinutes(Math.Max(0, Options.InvitationResendCooldownMinutes));

        var notices = new List<string>();
        var resent = new List<string>();
        foreach (var participant in chosen)
        {
            if (suppressed.TryGetValue(participant.Email, out var reason))
            {
                notices.Add(reason == SuppressionReason.Unsubscribed
                    ? $"{participant.Email} has asked this server not to email them, so the invitation wasn't resent. Tell them about it yourself."
                    : $"Email to {participant.Email} has bounced before, so the invitation wasn't resent. Check the address or tell them yourself.");
                continue;
            }

            var lastSent = participant.InvitationResentAt is { } resentAt && resentAt > participant.InvitedAt ? resentAt : participant.InvitedAt;
            if (now - lastSent < cooldown)
            {
                notices.Add($"{participant.Email} was sent the invitation less than {Describe(cooldown)} ago, so it wasn't sent again. Try again later.");
                continue;
            }

            participant.InvitationResentAt = now;
            QueueEmail(db, ev, participant.Email, await composer.InvitationAsync(ev, participant, VotingUrl(ev, participant)), now);
            resent.Add(participant.Email);
        }

        if (resent.Count > 0)
        {
            Audit(db, ev, caller, "invitations.resent", new { to = resent }, now);
            await SaveAsync(db, ev, cancellationToken);
        }
        return new ResendResult(ToView(ev, caller, account, viewerTimeZone: null, notices), resent);
    }

    /// <summary>The participants named by <paramref name="emails"/>, or everyone if none are named. Each must be invited.</summary>
    private static List<Participant> ChooseParticipants(SchedulingEvent ev, IReadOnlyList<string>? emails)
    {
        var everyone = ev.Participants.OrderBy(p => p.Email, StringComparer.Ordinal).ToList();
        if (emails is null || emails.Count == 0)
            return everyone;

        var wanted = emails.Select(EmailAddresses.Normalize).ToHashSet(StringComparer.Ordinal);
        var unknown = wanted.Where(email => everyone.All(p => p.Email != email)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new SchedulingValidationException(
                $"{string.Join(", ", unknown)} {(unknown.Count == 1 ? "isn't" : "aren't")} invited to this event. " +
                "Only people already invited can be sent the invitation again.");
        return everyone.Where(p => wanted.Contains(p.Email)).ToList();
    }

    /// <summary>"1 hour", "90 minutes".</summary>
    private static string Describe(Duration duration)
    {
        var minutes = (long)duration.TotalMinutes;
        if (minutes >= 60 && minutes % 60 == 0)
            return minutes == 60 ? "1 hour" : $"{minutes / 60} hours";
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    public async Task<EventView> CancelAsync(CallerIdentity caller, string eventRef, string? reason, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await FindAccountAsync(db, caller, cancellationToken);
        var ev = await LoadEventAsync(db, eventRef, tracking: true, cancellationToken);
        RequireOrganizer(Authorize(ev, caller, account));

        var reasonText = RequireText(reason, FieldLimits.NoteMax, "reason");
        var participantsWereNotified = ev.State != EventState.Draft;
        var meetingWasSet = ev.State == EventState.Finalized;
        ev.Cancel(now);
        Touch(db, ev, now);

        Audit(db, ev, caller, "event.cancelled", new { reason = reasonText }, now);
        if (participantsWereNotified)
        {
            foreach (var participant in ev.Participants)
                QueueEmail(db, ev, participant.Email, await composer.CancelledAsync(ev, participant, reasonText, meetingWasSet), now);
        }

        await SaveAsync(db, ev, cancellationToken);
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

        await SaveAsync(db, ev, cancellationToken);
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
    /// A new account owning the caller's verified addresses: on an allowlisted organizer's first event, a person's
    /// first personal access token, or their first added address. It is saved here, on its own, so concurrent first
    /// calls share one account; call it before tracking anything else. If another request claimed one of the
    /// addresses after the caller's lookup, that request's account is returned instead.
    /// </summary>
    internal static async Task<UserAccount> ProvisionAccountAsync(SchedulerDbContext db, CallerIdentity caller, Instant now, CancellationToken cancellationToken)
    {
        var emails = caller.VerifiedEmails.ToList();
        if (!await db.UserEmails.AnyAsync(e => emails.Contains(e.Email), cancellationToken))
        {
            var account = new UserAccount { CreatedAt = now };
            foreach (var email in emails)
                account.Emails.Add(new UserEmail { Email = email, UserId = account.Id, VerifiedAt = now });
            db.Users.Add(account);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return account;
            }
            catch (DbUpdateException)
            {
                // Another request provisioned the same person at the same moment: share its account.
                foreach (var email in account.Emails.ToList())
                    db.Entry(email).State = EntityState.Detached;
                db.Entry(account).State = EntityState.Detached;
                var winner = await FindAccountAsync(db, caller, cancellationToken);
                if (winner is null)
                    throw;
                return winner;
            }
        }

        // A concurrent first call claimed an address after the caller looked: never start a second account.
        return await FindAccountAsync(db, caller, cancellationToken)
            ?? throw new InvalidOperationException("The caller's addresses are claimed, but no account owns them.");
    }

    /// <summary>Tells the organizer which invitees won't get the email, because they unsubscribed or bounced.</summary>
    private static async Task AddSuppressionNoticesAsync(SchedulerDbContext db, SchedulingEvent ev, List<string> notices, CancellationToken cancellationToken)
    {
        var invited = ev.Participants.Select(p => p.Email).ToList();
        var suppressed = await db.Suppressions.AsNoTracking()
            .Where(s => invited.Contains(s.Email))
            .OrderBy(s => s.Email)
            .ToListAsync(cancellationToken);
        foreach (var suppression in suppressed)
            notices.Add(suppression.Reason == SuppressionReason.Unsubscribed
                ? $"{suppression.Email} has asked this server not to email them, so they won't get the invitation. They are still a participant; tell them about it yourself."
                : $"Email to {suppression.Email} has bounced before, so they won't get the invitation. They are still a participant; check the address or tell them yourself.");
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

    internal static IQueryable<SchedulingEvent> EventsWithDetails(SchedulerDbContext db) =>
        db.Events
            .Include(e => e.Slots)
            .Include(e => e.Participants).ThenInclude(p => p.Votes)
            .Include(e => e.Suggestions)
            .AsSplitQuery();

    /// <summary>
    /// Locks <paramref name="participants"/>' rows until the transaction ends, so one person's calls that read and then
    /// insert their own rows (votes, suggestions) take turns. In ID order, so two calls can't deadlock.
    /// </summary>
    private static async Task LockParticipantsAsync(SchedulerDbContext db, IEnumerable<Participant> participants, CancellationToken cancellationToken)
    {
        var ids = participants.Select(p => p.Id).Order().ToArray();
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM \"Participants\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE", cancellationToken);
    }

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

    /// <summary>Saves an event's changes, then tells open pages about them.</summary>
    private async Task SaveAsync(SchedulerDbContext db, SchedulingEvent ev, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw StaleVersion();
        }
        changes.Publish(ev.Id);
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

    private static List<ProposedSlot> AddSlots(SchedulingEvent ev, IReadOnlyList<SlotInput>? inputs, int round, Instant now) =>
        AddSlots(ev, ResolveSlots(inputs), round, now);

    /// <summary>Times given as input, resolved strictly: a time that doesn't exist or happens twice is refused.</summary>
    private static List<SlotTime> ResolveSlots(IReadOnlyList<SlotInput>? inputs) =>
        (inputs ?? []).Select(input => SlotTimeResolver.Resolve(input?.Start, input?.TimeZone)).ToList();

    private static List<ProposedSlot> AddSlots(SchedulingEvent ev, IReadOnlyList<SlotTime> times, int round, Instant now)
    {
        if (times.Count == 0)
            throw new SchedulingValidationException("Propose at least one time.");
        if (ev.ActiveSlots.Count() + times.Count > FieldLimits.MaxSlotsPerEvent)
            throw new SchedulingValidationException($"An event can have at most {FieldLimits.MaxSlotsPerEvent} proposed times.");

        var added = new List<ProposedSlot>();
        foreach (var time in times)
        {
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

    private string VotingUrl(SchedulingEvent ev, Participant participant) => VotingUrl(Options, magicLinks, ev, participant);

    private string DashboardUrl(SchedulingEvent ev) => DashboardUrl(Options, ev);

    internal static string VotingUrl(SchedulingOptions options, MagicLinks links, SchedulingEvent ev, Participant participant) =>
        $"{options.PublicBaseUrl.TrimEnd('/')}/e/{ev.ShortCode}?t={links.CreateToken(participant.Id, participant.TokenNonce)}";

    internal static string DashboardUrl(SchedulingOptions options, SchedulingEvent ev) => $"{options.PublicBaseUrl.TrimEnd('/')}/events/{ev.Id}";

    internal static void QueueEmail(SchedulerDbContext db, SchedulingEvent ev, string to, ComposedEmail email, Instant now, bool replyToOrganizer = true) =>
        db.Outbox.Add(new OutboxEmail
        {
            EventId = ev.Id,
            To = to,
            ReplyTo = replyToOrganizer ? ev.OrganizerEmail : null,
            Subject = email.Subject,
            TextBody = email.TextBody,
            HtmlBody = email.HtmlBody,
            ListUnsubscribeUrl = email.ListUnsubscribeUrl,
            CreatedAt = now,
            NextAttemptAt = now,
        });

    internal static void Audit(SchedulerDbContext db, SchedulingEvent ev, CallerIdentity caller, string action, object detail, Instant now) =>
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

    /// <summary>
    /// The milestones only: participants get state, deadline and finalized slot, so their marker never moves on
    /// someone else's vote; the organizer also gets who has responded and who rules out every time.
    /// </summary>
    private static EventStatusView ToStatus(SchedulingEvent ev, bool isOrganizer)
    {
        var respondBy = ev.RespondBy is { } by ? SlotTimeFormatter.Display(by, ev.OrganizerTimeZone) : null;
        bool? everyoneResponded = isOrganizer ? EveryoneResponded(ev) : null;
        IReadOnlyList<string>? rulesOutEveryTime = isOrganizer
            ? ev.Participants.Where(ev.RulesOutEveryTime).Select(p => p.Email).Order(StringComparer.Ordinal).ToList()
            : null;

        var fields = string.Join('\n',
            ev.State.ToString(),
            // The exact instant, not the display text, which drops seconds a deadline may have.
            ev.RespondBy is { } deadline ? SlotTimeFormatter.Utc(deadline) : "",
            ev.FinalizedSlotId?.ToString() ?? "",
            isOrganizer ? "organizer" : "participant",
            everyoneResponded?.ToString() ?? "",
            rulesOutEveryTime is null ? "" : string.Join(',', rulesOutEveryTime));
        var marker = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fields)))[..16];

        return new EventStatusView(
            ev.Id, ev.ShortCode, ev.State, respondBy, ev.FinalizedSlotId, isOrganizer, everyoneResponded, rulesOutEveryTime, marker);
    }

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
                    new SlotTally(score.Preferred, score.Yes, score.IfNecessary, score.No, score.Pending),
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

        // The organizer sees every suggestion and who made it; a participant sees only their own.
        var suggestions = ev.Suggestions
            .Select(s => (Suggestion: s, By: ev.Participants.FirstOrDefault(p => p.Id == s.ParticipantId)))
            .Where(x => x.By is not null && (isOrganizer || me.Contains(x.By)))
            .OrderBy(x => x.Suggestion.Start)
            .Select(x => new SuggestionView(
                x.Suggestion.Id,
                SlotTimeFormatter.Utc(x.Suggestion.Start),
                SlotTimeFormatter.Display(x.Suggestion.Start, ev.OrganizerTimeZone),
                SlotTimeFormatter.Display(x.Suggestion.Start, x.Suggestion.TimeZoneId),
                x.Suggestion.Status,
                x.By!.Email,
                x.By.DisplayName))
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
            notices is { Count: > 0 } ? notices : null,
            suggestions.Count > 0 ? suggestions : null);
    }
}
