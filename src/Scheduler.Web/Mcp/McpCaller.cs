using ModelContextProtocol;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Mcp;

/// <summary>
/// What every MCP tool and resource does around its service call: resolve the caller from the request, return
/// the result as <see cref="ToolJson"/>, and turn a <see cref="SchedulingException"/> into an
/// <see cref="McpException"/> so its message reaches the agent.
/// </summary>
internal static class McpCaller
{
    public const string NotSignedIn = "You are not signed in to the scheduling server.";

    public static async Task<string> RunAsync<T>(HttpContext? http, Func<CallerIdentity, Task<T>> action)
    {
        var caller = CallerResolver.FromHttpContext(http, Channel.Mcp) ?? throw new McpException(NotSignedIn);
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
