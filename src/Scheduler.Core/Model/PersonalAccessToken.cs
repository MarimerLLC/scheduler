using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// A bearer token for an agent that can't do an interactive OAuth sign-in (RockBot, scripts). It acts as
/// its account, with all of the account's verified addresses. Only a hash is stored: the token itself is
/// shown once, when it is created. See docs/design.md, "Personal access tokens".
/// </summary>
public sealed class PersonalAccessToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>What the person called it, e.g. "RockBot", so they know which one to revoke.</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 of the token. The token is 256 random bits, so a plain hash is enough.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The token's first characters, safe to show, so a person can tell tokens apart.</summary>
    public required string Prefix { get; set; }

    public Instant CreatedAt { get; set; }
    public Instant ExpiresAt { get; set; }

    /// <summary>Roughly when it was last used; updated at most every few minutes.</summary>
    public Instant? LastUsedAt { get; set; }

    public Instant? RevokedAt { get; set; }

    public bool IsActive(Instant now) => RevokedAt is null && ExpiresAt > now;
}
