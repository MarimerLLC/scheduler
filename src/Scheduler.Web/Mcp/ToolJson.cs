using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scheduler.Core;
using Scheduler.Core.Model;

namespace Scheduler.Web.Mcp;

/// <summary>
/// The JSON shape tools return: camelCase properties, enums as snake_case strings
/// (<c>if_necessary</c>, not <c>1</c>), nulls omitted to keep results compact for agents.
/// </summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Readable output for agents: characters like + and ' stay literal instead of becoming JSON escape
        // sequences. Safe because tool results are never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static VoteValue ParseVote(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "preferred" => VoteValue.Preferred,
        "if_necessary" => VoteValue.IfNecessary,
        "no" => VoteValue.No,
        _ => throw new SchedulingValidationException($"'{value}' isn't a vote. Use preferred, if_necessary or no.")
    };

    public static Guid ParseId(string? value, string what) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new SchedulingValidationException($"'{value}' isn't a valid {what} ID.");
}
