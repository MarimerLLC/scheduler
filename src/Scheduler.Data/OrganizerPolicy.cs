using Microsoft.EntityFrameworkCore;
using Scheduler.Core;
using Scheduler.Core.Services;

namespace Scheduler.Data;

/// <summary>Who may create events: a configured entry or an admin's grant, for any of the caller's addresses.</summary>
internal static class OrganizerPolicy
{
    public static async Task<bool> CanOrganizeAsync(
        SchedulerDbContext db, SchedulingOptions options, IReadOnlyCollection<string> emails, CancellationToken cancellationToken)
    {
        if (emails.Any(options.IsAllowlistedOrganizer))
            return true;

        var candidates = emails.SelectMany(OrganizerPatterns.CandidatesFor).Distinct().ToList();
        return candidates.Count > 0
            && await db.OrganizerGrants.AnyAsync(g => candidates.Contains(g.Pattern), cancellationToken);
    }
}
