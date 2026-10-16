using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core.Services;

namespace Scheduler.Data;

/// <summary>
/// EF Core implementation of <see cref="ISchedulingService"/>. Scaffold: the contract and its
/// collaborators are in place; each method's rules are specified in docs/design.md and not yet written.
/// Every state-changing method should, in one SaveChanges: apply the change through the domain model,
/// append an <c>AuditEntry</c>, and add any <c>OutboxEmail</c> rows it causes.
/// </summary>
public sealed class SchedulingService(IDbContextFactory<SchedulerDbContext> dbFactory, IClock clock) : ISchedulingService
{
    // Held for the implementation; referenced here so the scaffold documents its dependencies.
    private readonly IDbContextFactory<SchedulerDbContext> _dbFactory = dbFactory;
    private readonly IClock _clock = clock;

    public Task<EventView> CreateEventAsync(CallerIdentity caller, CreateEventRequest request, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: create_event");

    public Task<IReadOnlyList<EventSummary>> ListMyEventsAsync(CallerIdentity caller, EventListFilter filter, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: list_my_events");

    public Task<EventView> GetEventAsync(CallerIdentity caller, string eventRef, string? viewerTimeZone, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: get_event");

    public Task<EventView> VoteAsync(CallerIdentity caller, string eventRef, IReadOnlyList<VoteInput> votes, string? note, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: vote");

    public Task<EventView> ProposeMoreSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput> slots, uint expectedVersion, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: propose_more_slots");

    public Task<EventView> WithdrawSlotAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: withdraw_slot");

    public Task<FinalizeResult> FinalizeAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: finalize_event");

    public Task<EventView> CancelAsync(CallerIdentity caller, string eventRef, string? reason, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: cancel_event");

    public Task RecordCalendarEventAsync(CallerIdentity caller, string eventRef, string calendarEventUid, CancellationToken cancellationToken) =>
        throw NotYet("§MCP tools: record_calendar_event");

    private static NotImplementedException NotYet(string section) =>
        new($"Not implemented yet. See docs/design.md {section}.");
}
