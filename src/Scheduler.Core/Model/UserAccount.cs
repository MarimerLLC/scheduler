using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// A signed-in person. One account can own several verified addresses (work, personal, consulting
/// domain) so an invite to any of them can be answered from one MCP connection or web login.
/// </summary>
public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string? DisplayName { get; set; }
    public Instant CreatedAt { get; set; }

    public List<UserEmail> Emails { get; } = [];
}

/// <summary>A verified address belonging to a <see cref="UserAccount"/>. Each address has one owner.</summary>
public sealed class UserEmail
{
    /// <summary>Normalized (trimmed, lower-case) address.</summary>
    public required string Email { get; set; }
    public Guid UserId { get; set; }
    public Instant VerifiedAt { get; set; }
}
