using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>A value the server keeps for itself between runs, such as the tzdb version it last applied.</summary>
public sealed class ServerSetting
{
    public const string TzdbVersion = "tzdb.version";

    public required string Key { get; set; }
    public required string Value { get; set; }
    public Instant UpdatedAt { get; set; }
}
