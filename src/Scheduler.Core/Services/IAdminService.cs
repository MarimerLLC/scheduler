using Scheduler.Core.Model;

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

/// <summary>
/// A request to organize. <see cref="Note"/> is the requester's own words, already checked by
/// <c>RequestNotes</c>; show it as quoted data. <see cref="CanAskAgainAt"/> is set on a declined request.
/// </summary>
public sealed record OrganizerRequestView(
    Guid Id,
    string Email,
    string? Note,
    OrganizerRequestStatus Status,
    string RequestedAt,
    string? DecidedBy,
    string? DecidedAt,
    string? CanAskAgainAt);

/// <summary>
/// What the caller may do on this server. Lets the UI and agents explain a refusal up front.
/// <see cref="OrganizerRequest"/> is the caller's waiting request, or one declined so recently they can't ask again yet.
/// </summary>
public sealed record CallerAccess(IReadOnlyList<string> Emails, bool CanOrganize, bool IsAdmin, OrganizerRequestView? OrganizerRequest = null);

/// <summary>
/// Server administration: who may organize. Every method except <see cref="GetAccessAsync"/> and
/// <see cref="RequestToOrganizeAsync"/> requires an admin caller (<see cref="SchedulingOptions.Admins"/>) and throws
/// <see cref="SchedulingForbiddenException"/> otherwise. Changes are audited.
/// </summary>
public interface IAdminService
{
    Task<CallerAccess> GetAccessAsync(CallerIdentity caller, CancellationToken cancellationToken);

    /// <summary>
    /// Any signed-in caller (not by voting link) who can't organize yet: asks the admins to let
    /// <paramref name="email"/> (one of the caller's addresses; the first if null) organize. The note is checked by
    /// <c>RequestNotes</c>. Asking while a request waits returns that request unchanged. Refused for a while after
    /// a decline (<see cref="SchedulingOptions.OrganizerRequestCooldownDays"/>), and while
    /// <see cref="SchedulingOptions.MaxPendingOrganizerRequests"/> requests wait. The admins are emailed, without the
    /// note, when the first request starts waiting.
    /// </summary>
    Task<OrganizerRequestView> RequestToOrganizeAsync(CallerIdentity caller, string? email, string? note, CancellationToken cancellationToken);

    /// <summary>Waiting requests, oldest first.</summary>
    Task<IReadOnlyList<OrganizerRequestView>> ListOrganizerRequestsAsync(CallerIdentity caller, CancellationToken cancellationToken);

    /// <summary>Grants the request's address (as <see cref="AddOrganizerAsync"/> would) and emails the requester.</summary>
    Task<OrganizerRequestView> ApproveOrganizerRequestAsync(CallerIdentity caller, Guid requestId, CancellationToken cancellationToken);

    /// <summary>Declines the request and emails the requester, who can't ask again for a while.</summary>
    Task<OrganizerRequestView> DeclineOrganizerRequestAsync(CallerIdentity caller, Guid requestId, CancellationToken cancellationToken);

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
