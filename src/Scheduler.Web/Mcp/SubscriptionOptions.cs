namespace Scheduler.Web.Mcp;

/// <summary>The "Scheduler:Subscriptions" configuration section: limits on <c>subscriptions/listen</c> streams.</summary>
internal sealed class SubscriptionOptions
{
    public const string SectionName = "Scheduler:Subscriptions";

    /// <summary>
    /// How long one listen stream stays open before the server ends it and the client reconnects (which also
    /// refreshes its bearer token). Below the idle cutoff of the proxies in front of the server, since a quiet
    /// stream sends nothing; see docs/design.md, "Push updates".
    /// </summary>
    public double StreamMinutes { get; set; } = 25;

    /// <summary>Open listen streams per caller (account, or address).</summary>
    public int MaxStreamsPerCaller { get; set; } = 5;

    /// <summary>Resource URIs one listen stream may ask for.</summary>
    public int MaxUrisPerStream { get; set; } = 50;
}
