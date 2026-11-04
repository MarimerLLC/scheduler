namespace Scheduler.Web.Mcp;

/// <summary>The "Scheduler:Subscriptions" configuration section: limits on <c>subscriptions/listen</c> streams.</summary>
internal sealed class SubscriptionOptions
{
    public const string SectionName = "Scheduler:Subscriptions";

    /// <summary>
    /// How long one listen stream stays open before the server ends it and the client reconnects (which also
    /// refreshes its bearer token, checked only when a stream opens). Well under the 1-hour access token lifetime,
    /// and under the idle cutoff of the proxies in front of the server (the Tailscale Funnel showed none in four
    /// hours); see docs/design.md, "Push updates".
    /// </summary>
    public double StreamMinutes { get; set; } = 25;

    /// <summary>Open listen streams per caller (account, or address) that watch at least one event.</summary>
    public int MaxStreamsPerCaller { get; set; } = 5;

    /// <summary>
    /// Open listen streams per caller that watch nothing. Claude Code holds one per connected session (it asks only
    /// for list_changed), so this is larger; they do no work but each holds a request open.
    /// </summary>
    public int MaxIdleStreamsPerCaller { get; set; } = 20;

    /// <summary>Resource URIs one listen stream may ask for.</summary>
    public int MaxUrisPerStream { get; set; } = 50;
}
