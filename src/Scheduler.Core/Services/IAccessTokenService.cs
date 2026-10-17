using Scheduler.Core.Model;

namespace Scheduler.Core.Services;

/// <summary>A personal access token as its owner sees it. Times are UTC display strings.</summary>
public sealed record AccessTokenView(
    Guid Id,
    string Name,
    string Prefix,
    string CreatedAt,
    string ExpiresAt,
    string? LastUsedAt,
    AccessTokenStatus Status);

public enum AccessTokenStatus
{
    Active,
    Expired,
    Revoked
}

/// <summary>A new token. <see cref="Token"/> is never available again, so it must be shown to the person now.</summary>
public sealed record CreatedAccessToken(string Token, AccessTokenView View);

/// <summary>
/// Personal access tokens for agents that can't sign in interactively (RockBot, scripts). People manage their
/// own tokens, only from the web UI while signed in; a token can't create, list or revoke tokens. A token acts
/// as its account, with all of the account's verified addresses, until it expires or is revoked. Creating
/// the first token creates the caller's account. Changes are audited. See docs/design.md, "Personal access tokens".
/// </summary>
public interface IAccessTokenService
{
    /// <summary>The caller's tokens, newest first, including expired and revoked ones.</summary>
    Task<IReadOnlyList<AccessTokenView>> ListAsync(CallerIdentity caller, CancellationToken cancellationToken);

    /// <summary>Creates a token that expires after <paramref name="expiresInDays"/> (1 to 365).</summary>
    Task<CreatedAccessToken> CreateAsync(CallerIdentity caller, string name, int expiresInDays, CancellationToken cancellationToken);

    /// <summary>Revokes one of the caller's tokens. Revoking a revoked token does nothing.</summary>
    Task RevokeAsync(CallerIdentity caller, Guid tokenId, CancellationToken cancellationToken);

    /// <summary>
    /// The caller a bearer token stands for, or null if it isn't an active token. Records when it was used.
    /// </summary>
    Task<CallerIdentity?> AuthenticateAsync(string token, Channel channel, CancellationToken cancellationToken);
}
