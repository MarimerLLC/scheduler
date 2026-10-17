using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Mcp;

/// <summary>
/// Server administration over MCP. These tools are listed only to admins (<see cref="HideFromNonAdmins"/>),
/// and every call is checked again by <see cref="IAdminService"/>.
/// </summary>
[McpServerToolType]
public sealed class AdminTools(IAdminService admin, IHttpContextAccessor http)
{
    public const string ListOrganizersName = "list_organizers";
    public const string AddOrganizerName = "add_organizer";
    public const string RemoveOrganizerName = "remove_organizer";

    private static readonly HashSet<string> AdminOnly = [ListOrganizersName, AddOrganizerName, RemoveOrganizerName];

    [McpServerTool(Name = ListOrganizersName, Title = "List organizers", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Admins only: lists who may create scheduling events. Entries are addresses or *@domain. Entries with source 'configuration' are fixed in the server's settings; 'granted' entries were added by an admin and can be removed.")]
    public Task<string> ListOrganizers(CancellationToken cancellationToken = default) =>
        Run(caller => admin.ListOrganizersAsync(caller, cancellationToken));

    [McpServerTool(Name = AddOrganizerName, Title = "Add an organizer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Admins only: lets an address, or everyone at a domain, create scheduling events. Confirm with the user first.")]
    public Task<string> AddOrganizer(
        [Description("An address (bob@example.com) or a whole domain (*@example.com).")] string entry,
        [Description("Optional short reminder of why, at most 280 characters.")] string? note = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => admin.AddOrganizerAsync(caller, entry, note, cancellationToken));

    [McpServerTool(Name = RemoveOrganizerName, Title = "Remove an organizer", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Admins only: stops an address or domain from creating new scheduling events. Events they already organize stay theirs. Entries from the server's configuration can't be removed. Confirm with the user first.")]
    public Task<string> RemoveOrganizer(
        [Description("The entry exactly as list_organizers shows it.")] string entry,
        CancellationToken cancellationToken = default) =>
        Run(async caller =>
        {
            await admin.RemoveOrganizerAsync(caller, entry, cancellationToken);
            return new { removed = entry };
        });

    /// <summary>
    /// A tools/list filter: non-admins don't see the admin tools at all. The list then differs per caller,
    /// so it is marked private: a shared gateway must not serve one user's list to another.
    /// </summary>
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> HideFromNonAdmins(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.CacheScope = CacheScope.Private;

            var services = context.Services!;
            var caller = CallerResolver.FromHttpContext(
                services.GetRequiredService<IHttpContextAccessor>().HttpContext,
                Channel.Mcp);
            var isAdmin = caller is not null && services.GetRequiredService<IOptions<SchedulingOptions>>().Value.IsAdmin(caller);
            if (!isAdmin)
                result.Tools = result.Tools.Where(t => !AdminOnly.Contains(t.Name)).ToList();

            return result;
        };

    private async Task<string> Run<T>(Func<CallerIdentity, Task<T>> action)
    {
        var caller = CallerResolver.FromHttpContext(http.HttpContext, Channel.Mcp)
            ?? throw new McpException("You are not signed in to the scheduling server.");
        try
        {
            return ToolJson.Serialize(await action(caller));
        }
        catch (SchedulingException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
