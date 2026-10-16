using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Core.Time;

namespace Scheduler.Data;

/// <summary>EF Core implementation of <see cref="IAdminService"/>. Admin actions are audited with no event ID.</summary>
public sealed class AdminService(
    IDbContextFactory<SchedulerDbContext> dbFactory,
    IClock clock,
    IOptions<SchedulingOptions> options) : IAdminService
{
    /// <summary>Admin timestamps are shown in UTC: the list isn't tied to any one person's zone.</summary>
    private const string DisplayZone = "Etc/UTC";

    private SchedulingOptions Options => options.Value;

    public async Task<CallerAccess> GetAccessAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        var emails = SchedulingService.OwnEmails(caller, account);
        var canOrganize = caller.MagicLinkEventId is null
            && await OrganizerPolicy.CanOrganizeAsync(db, Options, emails, cancellationToken);
        return new CallerAccess(emails.Order().ToList(), canOrganize, Options.IsAdmin(caller));
    }

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
