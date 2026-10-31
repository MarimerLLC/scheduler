using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Core.Time;

namespace Scheduler.Data;

/// <summary>EF Core implementation of <see cref="IAdminService"/>. Admin actions are audited with no event ID.</summary>
public sealed class AdminService(
    IDbContextFactory<SchedulerDbContext> dbFactory,
    IClock clock,
    IOptions<SchedulingOptions> options,
    IEmailComposer composer) : IAdminService
{
    /// <summary>Admin timestamps are shown in UTC: the list isn't tied to any one person's zone.</summary>
    private const string DisplayZone = "Etc/UTC";

    private SchedulingOptions Options => options.Value;

    private Duration Cooldown => Duration.FromDays(Options.OrganizerRequestCooldownDays);

    public async Task<CallerAccess> GetAccessAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        var emails = SchedulingService.OwnEmails(caller, account);
        var canOrganize = caller.MagicLinkEventId is null
            && await OrganizerPolicy.CanOrganizeAsync(db, Options, emails, cancellationToken);
        var request = caller.MagicLinkEventId is null && !canOrganize
            ? await CurrentRequestAsync(db, emails, clock.GetCurrentInstant(), cancellationToken)
            : null;
        return new CallerAccess(emails.Order().ToList(), canOrganize, Options.IsAdmin(caller), request is null ? null : ToView(request));
    }

    // ── Requests to organize ────────────────────────────────────────────────

    public async Task<OrganizerRequestView> RequestToOrganizeAsync(CallerIdentity caller, string? email, string? note, CancellationToken cancellationToken)
    {
        if (caller.MagicLinkEventId is not null || caller.VerifiedEmails.Count == 0)
            throw new SchedulingForbiddenException("Sign in to ask to organize. A voting link can only answer its own invitation.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        var emails = SchedulingService.OwnEmails(caller, account);

        string address;
        if (string.IsNullOrWhiteSpace(email))
            address = caller.VerifiedEmails[0];
        else
        {
            address = EmailAddresses.Normalize(email);
            if (!emails.Contains(address))
                throw new SchedulingValidationException(
                    $"{address} isn't one of your verified addresses here. Ask for one of: {string.Join(", ", emails.Order())}.");
        }

        if (await OrganizerPolicy.CanOrganizeAsync(db, Options, emails, cancellationToken))
            throw new SchedulingValidationException("You can already create scheduling events here.");

        var now = clock.GetCurrentInstant();
        var current = await CurrentRequestAsync(db, emails, now, cancellationToken);
        if (current is { Status: OrganizerRequestStatus.Pending })
            return ToView(current);
        if (current is { Status: OrganizerRequestStatus.Declined, DecidedAt: { } declinedAt })
            throw new SchedulingForbiddenException(
                $"An admin declined your request on {Display(declinedAt)}. You can ask again from {Display(declinedAt + Cooldown)}.");

        var cleaned = RequestNotes.Clean(note, Options.BlockedNoteTerms, Options.AllowedNoteTerms);

        var waiting = await db.OrganizerRequests.CountAsync(r => r.Status == OrganizerRequestStatus.Pending, cancellationToken);
        if (waiting >= Options.MaxPendingOrganizerRequests)
            throw new SchedulingValidationException(
                "Too many requests to organize are waiting for this server's admins right now. Try again in a few days.");

        var request = new OrganizerRequest { Email = address, Note = cleaned, CreatedAt = now };
        db.OrganizerRequests.Add(request);
        // The note isn't audited: audit rows are kept, and the note is a stranger's text.
        Audit(db, caller, "organizer.requested", new { requestId = request.Id, email = address }, now);

        // Only the first waiting request emails the admins, so a burst of requests can't flood their inboxes.
        if (waiting == 0)
        {
            var reviewUrl = $"{Options.PublicBaseUrl.TrimEnd('/')}/admin/organizers";
            var message = await composer.OrganizerRequestWaitingAsync(address, reviewUrl);
            foreach (var admin in Options.Admins.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct())
                QueueEmail(db, admin, message, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same address asked at the same moment (one waiting request per address, by a unique index).
            await using var retry = await dbFactory.CreateDbContextAsync(cancellationToken);
            var winner = await CurrentRequestAsync(retry, [address], now, cancellationToken);
            if (winner is null)
                throw;
            return ToView(winner);
        }
        return ToView(request);
    }

    public async Task<IReadOnlyList<OrganizerRequestView>> ListOrganizerRequestsAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var requests = await db.OrganizerRequests.AsNoTracking()
            .Where(r => r.Status == OrganizerRequestStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return requests.Select(ToView).ToList();
    }

    public Task<OrganizerRequestView> ApproveOrganizerRequestAsync(CallerIdentity caller, Guid requestId, CancellationToken cancellationToken) =>
        DecideAsync(caller, requestId, OrganizerRequestStatus.Approved, cancellationToken);

    public Task<OrganizerRequestView> DeclineOrganizerRequestAsync(CallerIdentity caller, Guid requestId, CancellationToken cancellationToken) =>
        DecideAsync(caller, requestId, OrganizerRequestStatus.Declined, cancellationToken);

    private async Task<OrganizerRequestView> DecideAsync(
        CallerIdentity caller, Guid requestId, OrganizerRequestStatus decision, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var admin = caller.VerifiedEmails[0];
        var now = clock.GetCurrentInstant();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the decision first, so two admins deciding at once can't both act on it.
        var claimed = await db.OrganizerRequests
            .Where(r => r.Id == requestId && r.Status == OrganizerRequestStatus.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, decision)
                .SetProperty(r => r.DecidedBy, admin)
                .SetProperty(r => r.DecidedAt, now), cancellationToken);

        var request = await db.OrganizerRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new SchedulingValidationException("There's no request to organize with that ID.");
        if (claimed == 0)
            throw new SchedulingConflictException(
                $"That request was already {request.Status.ToString().ToLowerInvariant()} by {request.DecidedBy}.");

        if (decision == OrganizerRequestStatus.Approved)
        {
            if (await db.OrganizerGrants.FindAsync([request.Email], cancellationToken) is null)
            {
                var grantNote = request.Note is null ? "By request" : $"By request: {request.Note}";
                var grant = new OrganizerGrant
                {
                    Pattern = request.Email,
                    Note = grantNote.Length > FieldLimits.NoteMax ? grantNote[..(FieldLimits.NoteMax - 1)] + "…" : grantNote,
                    GrantedBy = admin,
                    GrantedAt = now,
                };
                db.OrganizerGrants.Add(grant);
                Audit(db, caller, "organizer.added", new { pattern = grant.Pattern, requestId }, now);
            }
            Audit(db, caller, "organizer.request.approved", new { requestId, email = request.Email }, now);
            QueueEmail(db, request.Email,
                await composer.OrganizerRequestApprovedAsync(request.Email, $"{Options.PublicBaseUrl.TrimEnd('/')}/events/new"), now);
        }
        else
        {
            Audit(db, caller, "organizer.request.declined", new { requestId, email = request.Email }, now);
            QueueEmail(db, request.Email, await composer.OrganizerRequestDeclinedAsync(request.Email, Display(now + Cooldown)), now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(request);
    }

    /// <summary>The caller's waiting request, or the latest one declined too recently to ask again.</summary>
    private async Task<OrganizerRequest?> CurrentRequestAsync(
        SchedulerDbContext db, IReadOnlyCollection<string> emails, Instant now, CancellationToken cancellationToken)
    {
        var addresses = emails.ToList();
        var declinedSince = now - Cooldown;
        return await db.OrganizerRequests.AsNoTracking()
            .Where(r => addresses.Contains(r.Email)
                && (r.Status == OrganizerRequestStatus.Pending
                    || (r.Status == OrganizerRequestStatus.Declined && r.DecidedAt > declinedSince)))
            .OrderBy(r => r.Status == OrganizerRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private OrganizerRequestView ToView(OrganizerRequest request) =>
        new(request.Id,
            request.Email,
            request.Note,
            request.Status,
            Display(request.CreatedAt),
            request.DecidedBy,
            request.DecidedAt is { } decided ? Display(decided) : null,
            request is { Status: OrganizerRequestStatus.Declined, DecidedAt: { } declined } ? Display(declined + Cooldown) : null);

    private static string Display(Instant instant) => SlotTimeFormatter.Display(instant, DisplayZone);

    private static void QueueEmail(SchedulerDbContext db, string to, ComposedEmail email, Instant now) =>
        db.Outbox.Add(new OutboxEmail
        {
            To = to,
            Subject = email.Subject,
            TextBody = email.TextBody,
            HtmlBody = email.HtmlBody,
            CreatedAt = now,
            NextAttemptAt = now,
        });

    // ── The organizer list ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<OrganizerEntry>> ListOrganizersAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var grants = await db.OrganizerGrants.AsNoTracking().ToListAsync(cancellationToken);

        var configured = Options.Organizers
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0)
            .Distinct()
            .Order()
            .Select(p => new OrganizerEntry(p, OrganizerPatterns.IsDomain(p), OrganizerSource.Configuration, null, null, null));

        return configured.Concat(grants.OrderBy(g => g.Pattern, StringComparer.Ordinal).Select(ToEntry)).ToList();
    }

    public async Task<OrganizerEntry> AddOrganizerAsync(CallerIdentity caller, string pattern, string? note, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var normalized = OrganizerPatterns.Normalize(pattern);
        var noteText = note?.Trim();
        if (noteText is { Length: > FieldLimits.NoteMax })
            throw new SchedulingValidationException($"The note can be at most {FieldLimits.NoteMax} characters.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.OrganizerGrants.FindAsync([normalized], cancellationToken);
        if (existing is not null)
            return ToEntry(existing);

        var now = clock.GetCurrentInstant();
        var grant = new OrganizerGrant
        {
            Pattern = normalized,
            Note = string.IsNullOrEmpty(noteText) ? null : noteText,
            GrantedBy = caller.VerifiedEmails[0],
            GrantedAt = now,
        };
        db.OrganizerGrants.Add(grant);
        Audit(db, caller, "organizer.added", new { pattern = normalized, note = grant.Note }, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToEntry(grant);
    }

    public async Task RemoveOrganizerAsync(CallerIdentity caller, string pattern, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var normalized = pattern?.Trim() == "*" ? "*" : OrganizerPatterns.Normalize(pattern);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var grant = await db.OrganizerGrants.FindAsync([normalized], cancellationToken);
        if (grant is null)
        {
            if (Options.Organizers.Any(p => string.Equals(p.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
                throw new SchedulingValidationException(
                    $"{normalized} comes from the server's configuration (Scheduler:Organizers) and can only be removed there.");
            throw new SchedulingValidationException($"{normalized} isn't on the organizer list.");
        }

        var now = clock.GetCurrentInstant();
        db.OrganizerGrants.Remove(grant);
        Audit(db, caller, "organizer.removed", new { pattern = normalized }, now);
        await db.SaveChangesAsync(cancellationToken);
    }

    private void RequireAdmin(CallerIdentity caller)
    {
        if (!Options.IsAdmin(caller))
            throw new SchedulingForbiddenException("Only a server admin can manage organizers.");
    }

    private static OrganizerEntry ToEntry(OrganizerGrant grant) =>
        new(grant.Pattern, OrganizerPatterns.IsDomain(grant.Pattern), OrganizerSource.Granted, grant.Note, grant.GrantedBy,
            SlotTimeFormatter.Display(grant.GrantedAt, DisplayZone));

    private static void Audit(SchedulerDbContext db, CallerIdentity caller, string action, object detail, Instant now) =>
        db.Audit.Add(new AuditEntry
        {
            At = now,
            Actor = caller.VerifiedEmails.FirstOrDefault(),
            Channel = caller.Channel,
            Action = action,
            Detail = JsonSerializer.Serialize(detail),
        });
}
