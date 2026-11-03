using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Scheduler.Web.Mcp;

/// <summary>
/// Asks the calling agent (and through it, the user) for something a tool call left out, using MCP Multi
/// Round-Trip Requests (MRTR, SEP-2322): the tool ends the call with an input-required result holding one
/// elicitation form, and the client calls the tool again with the same arguments plus the answer. The server
/// keeps nothing between the two calls, so this works over the stateless transport and on any replica.
/// </summary>
/// <remarks>
/// Only clients that negotiated MRTR and declared form elicitation are asked (<see cref="CanAsk"/>). Every other
/// client gets the tool's usual behavior: a validation error for a missing required value, nothing for a missing
/// optional one. A call asks at most once: the retry carries the answer, and the tool acts on it or explains what
/// is still missing, but never asks again. See docs/design.md, "Asking for what's missing".
/// </remarks>
internal sealed class McpQuestion
{
    /// <summary>The key of the one input request a call can carry.</summary>
    internal const string Key = "scheduler";

    private readonly Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> _fields = [];
    private readonly List<string> _required = [];

    public bool IsEmpty => _fields.Count == 0;

    /// <summary>True if the client of this call can answer an MRTR elicitation form.</summary>
    public static bool CanAsk(RequestContext<CallToolRequestParams> context) =>
        context.Server.IsMrtrSupported
        // MRTR clients declare their capabilities on each request (SEP-2575). On the stateless transport SDK 2.2.0
        // leaves Server.ClientCapabilities null and puts them on the request's context.
        && (context.JsonRpcRequest?.Context?.ClientCapabilities ?? context.Server.ClientCapabilities)?.Elicitation is { } elicitation
        // An empty elicitation capability means form mode, for clients written before url mode existed.
        && (elicitation.Form is not null || elicitation.Url is null);

    /// <summary>The answer to this tool's question, if this call is the retry that carries it.</summary>
    public static ElicitResult? AnswerTo(RequestContext<CallToolRequestParams> context) =>
        context.Params?.InputResponses is { } responses && responses.TryGetValue(Key, out var response)
            ? response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)
            : null;

    /// <summary>The trimmed text the user gave for a field, or null if they declined, skipped it or left it blank.</summary>
    public static string? TextOf(ElicitResult answer, string field) =>
        answer.IsAccepted
        && answer.Content?.TryGetValue(field, out var value) == true
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    public McpQuestion Text(string field, string title, string description, int maxLength, bool required)
    {
        _fields[field] = new ElicitRequestParams.StringSchema
        {
            Title = title,
            Description = description,
            MinLength = required ? 1 : null,
            MaxLength = maxLength,
        };
        if (required)
            _required.Add(field);
        return this;
    }

    /// <summary>The exception that ends this call with the question; the SDK turns it into the input-required result.</summary>
    public InputRequiredException Ask(string message) => new(
        new Dictionary<string, InputRequest>
        {
            [Key] = InputRequest.ForElicitation(new ElicitRequestParams
            {
                Mode = "form",
                Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = _fields, Required = _required },
            }),
        },
        requestState: null);
}
