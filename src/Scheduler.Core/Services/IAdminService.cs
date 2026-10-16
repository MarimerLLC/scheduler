namespace Scheduler.Core.Services;

/// <summary>Where an organizer entry comes from.</summary>
public enum OrganizerSource
{
    /// <summary><c>Scheduler:Organizers</c>; read-only here.</summary>
    Configuration,
    /// <summary>Added by an admin; can be removed.</summary>
    Granted
}

public sealed record OrganizerEntry(
    string Pattern,
    bool IsDomain,
    OrganizerSource Source,
    string? Note,
    string? GrantedBy,
    string? GrantedAt);

/// <summary>What the caller may do on this server. Lets the UI and agents explain a refusal up front.</summary>
public sealed record CallerAccess(IReadOnlyList<string> Emails, bool CanOrganize, bool IsAdmin);

/// <summary>
/// Server administration: who may organize. Every method except <see cref="GetAccessAsync"/> requires an
/// admin caller (<see cref="SchedulingOptions.Admins"/>) and throws <see cref="SchedulingForbiddenException"/>
/// otherwise. Changes are audited.
/// </summary>
public interface IAdminService
{
    Task<CallerAccess> GetAccessAsync(CallerIdentity caller, CancellationToken cancellationToken);

    /// <summary>Configured entries first, then granted ones, each sorted.</summary>
    Task<IReadOnlyList<OrganizerEntry>> ListOrganizersAsync(CallerIdentity caller, CancellationToken cancellationToken);

    /// <summary>Grants an address or <c>*@domain</c>. Adding an entry that already exists returns it unchanged.</summary>
    Task<OrganizerEntry> AddOrganizerAsync(CallerIdentity caller, string pattern, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a granted entry. Only new events are affected: events the person already organizes stay theirs.
    /// Configured entries can't be removed here.
    /// </summary>
    Task RemoveOrganizerAsync(CallerIdentity caller, string pattern, CancellationToken cancellationToken);
}
