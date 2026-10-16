using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// An email waiting to be sent. Written in the same transaction as the state change that caused it,
/// so a change can't commit while its notification is lost; a background worker delivers it.
/// See docs/design.md, "Email".
/// </summary>
public sealed class OutboxEmail
{
    public long Id { get; set; }
    public Guid? EventId { get; set; }

    public required string To { get; set; }

    /// <summary>The organizer's address, so replies reach a human instead of a no-reply box.</summary>
    public string? ReplyTo { get; set; }

    public required string Subject { get; set; }
    public required string TextBody { get; set; }
    public string? HtmlBody { get; set; }

    /// <summary>HTTPS one-click unsubscribe URL (RFC 8058) for the List-Unsubscribe header.</summary>
    public string? ListUnsubscribeUrl { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public Instant CreatedAt { get; set; }
    public Instant NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public Instant? SentAt { get; set; }
    public string? LastError { get; set; }
    public string? ProviderMessageId { get; set; }
}

/// <summary>An address the server must not email.</summary>
public sealed class EmailSuppression
{
    public required string Email { get; set; }
    public SuppressionReason Reason { get; set; }
    public Instant At { get; set; }
}
