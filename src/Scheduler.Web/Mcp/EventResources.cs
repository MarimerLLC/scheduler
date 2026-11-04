using System.ComponentModel;
using ModelContextProtocol.Server;
using Scheduler.Core.Services;

namespace Scheduler.Web.Mcp;

/// <summary>
/// Events as MCP resources, so an agent can watch them with <c>subscriptions/listen</c>
/// (<see cref="EventSubscriptions"/>). Thin like the tools: same caller, same service calls, same access checks.
/// See docs/design.md, "Push updates".
/// </summary>
[McpServerResourceType]
public sealed class EventResources(ISchedulingService scheduling, IHttpContextAccessor http)
{
    [McpServerResource(UriTemplate = EventResourceUri.EventTemplate, Name = "event", Title = "Scheduling event", MimeType = "application/json")]
    [Description("One event, exactly as get_event returns it. Only its organizer and participants can read it. It changes on every vote and organizer edit; watch the /status resource instead to hear only about milestones.")]
    public Task<string> Event(string shortCode, CancellationToken cancellationToken = default) =>
        McpCaller.RunAsync(http.HttpContext, caller => scheduling.GetEventAsync(caller, shortCode, viewerTimeZone: null, cancellationToken));

    [McpServerResource(UriTemplate = EventResourceUri.StatusTemplate, Name = "event_status", Title = "Scheduling event milestones", MimeType = "application/json")]
    [Description("An event's milestones: its state, voting deadline and finalized slot, and for the organizer whether every required person has responded and who rules out every time. marker changes exactly when one of them does. Only its organizer and participants can read it.")]
    public Task<string> Status(string shortCode, CancellationToken cancellationToken = default) =>
        McpCaller.RunAsync(http.HttpContext, caller => scheduling.GetEventStatusAsync(caller, shortCode, cancellationToken));
}
