namespace Scheduler.Core.Services;

/// <summary>
/// Whether the server may email an address. Unsubscribing adds a suppression that the outbox respects for
/// every email; only an unsubscribe can be undone this way, not a bounce or a complaint. Whoever holds an
/// address's unsubscribe link may use it, so these take an address the web app read from a link, not a caller.
/// Changes are audited. See docs/design.md, "Email".
/// </summary>
public interface ISubscriptionService
{
    /// <summary>Whether the address is suppressed for any reason.</summary>
    Task<bool> IsSuppressedAsync(string email, CancellationToken cancellationToken);

    /// <summary>Stops all email to the address. Unsubscribing twice does nothing.</summary>
    Task UnsubscribeAsync(string email, CancellationToken cancellationToken);

    /// <summary>Undoes an unsubscribe. Bounced and complained addresses stay suppressed.</summary>
    Task ResubscribeAsync(string email, CancellationToken cancellationToken);
}
