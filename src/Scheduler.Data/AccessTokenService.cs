using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Data;

/// <summary>EF Core implementation of <see cref="IAccessTokenService"/>. Token changes are audited with no event ID.</summary>
public sealed class AccessTokenService(IDbContextFactory<SchedulerDbContext> dbFactory, IClock clock) : IAccessTokenService
{
    /// <summary>Last-used times are only written when they are at least this stale, so a busy agent doesn't write on every call.</summary>
    private static readonly Duration LastUsedResolution = Duration.FromMinutes(5);

    /// <summary>Token times carry the year: a token can outlive the current one.</summary>
    private static readonly InstantPattern DisplayPattern = InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm 'UTC'");

    public async Task<IReadOnlyList<AccessTokenView>> ListAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        RequireSignedInOnWeb(caller);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        if (account is null)
            return [];

        var now = clock.GetCurrentInstant();
        var tokens = await db.PersonalAccessTokens.AsNoTracking()
            .Where(t => t.UserId == account.Id)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        return tokens.Select(t => ToView(t, now)).ToList();
    }

    public async Task<CreatedAccessToken> CreateAsync(CallerIdentity caller, string name, int expiresInDays, CancellationToken cancellationToken)
    {
        RequireSignedInOnWeb(caller);
        var label = name?.Trim() ?? "";
        if (label.Length == 0)
            throw new SchedulingValidationException("Give the token a name, such as the agent that will use it.");
        if (label.Length > AccessTokens.NameMax || label.Any(char.IsControl))
            throw new SchedulingValidationException($"A token name can be at most {AccessTokens.NameMax} characters, on one line.");
        if (expiresInDays is < 1 or > AccessTokens.MaxLifetimeDays)
            throw new SchedulingValidationException($"A token must expire within 1 to {AccessTokens.MaxLifetimeDays} days.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken)
            ?? await SchedulingService.ProvisionAccountAsync(db, caller, now, cancellationToken);

        var active = await db.PersonalAccessTokens.CountAsync(t => t.UserId == account.Id && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken);
        if (active >= AccessTokens.MaxActivePerAccount)
            throw new SchedulingValidationException(
                $"You already have {AccessTokens.MaxActivePerAccount} active tokens. Revoke one you no longer use first.");

        var secret = AccessTokens.New();
        var token = new PersonalAccessToken
        {
            UserId = account.Id,
            Name = label,
            TokenHash = AccessTokens.Hash(secret),
            Prefix = AccessTokens.DisplayPrefix(secret),
            CreatedAt = now,
            ExpiresAt = now + Duration.FromDays(expiresInDays),
        };
        db.PersonalAccessTokens.Add(token);
        Audit(db, caller, "token.created", new { id = token.Id, name = label, expiresAt = token.ExpiresAt.ToString() }, now);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedAccessToken(secret, ToView(token, now));
    }

    public async Task RevokeAsync(CallerIdentity caller, Guid tokenId, CancellationToken cancellationToken)
    {
        RequireSignedInOnWeb(caller);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        var token = account is null
            ? null
            : await db.PersonalAccessTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == account.Id, cancellationToken);
        if (token is null)
            throw new SchedulingValidationException("You have no token with that ID.");
        if (token.RevokedAt is not null)
            return;

        var now = clock.GetCurrentInstant();
        token.RevokedAt = now;
        Audit(db, caller, "token.revoked", new { id = token.Id, name = token.Name }, now);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CallerIdentity?> AuthenticateAsync(string token, Channel channel, CancellationToken cancellationToken)
    {
        if (!AccessTokens.IsWellFormed(token))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hash = AccessTokens.Hash(token);
        var now = clock.GetCurrentInstant();
        var match = await db.PersonalAccessTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.UserId, t.TokenHash, t.ExpiresAt, t.RevokedAt, t.LastUsedAt })
            .FirstOrDefaultAsync(cancellationToken);
        // The index lookup already matched the hash; compare again in constant time all the same.
        if (match is null || !CryptographicOperations.FixedTimeEquals(match.TokenHash, hash)
            || match.RevokedAt is not null || match.ExpiresAt <= now)
            return null;

        var emails = await db.UserEmails
            .Where(e => e.UserId == match.UserId)
            .OrderBy(e => e.Email)
            .Select(e => e.Email)
            .ToListAsync(cancellationToken);
        if (emails.Count == 0)
            return null;

        if (match.LastUsedAt is null || now - match.LastUsedAt.Value >= LastUsedResolution)
            await db.PersonalAccessTokens.Where(t => t.Id == match.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), cancellationToken);

        return new CallerIdentity(match.UserId, emails, channel);
    }

    /// <summary>Tokens are managed by a person in the browser, never by an agent holding a token.</summary>
    private static void RequireSignedInOnWeb(CallerIdentity caller)
    {
        if (caller.Channel != Channel.Web || caller.MagicLinkEventId is not null || caller.VerifiedEmails.Count == 0)
            throw new SchedulingForbiddenException("Personal access tokens can only be managed on the web, signed in.");
    }

    private static AccessTokenView ToView(PersonalAccessToken token, Instant now) =>
        new(token.Id,
            token.Name,
            token.Prefix,
            DisplayPattern.Format(token.CreatedAt),
            DisplayPattern.Format(token.ExpiresAt),
            token.LastUsedAt is { } used ? DisplayPattern.Format(used) : null,
            token.RevokedAt is not null ? AccessTokenStatus.Revoked
                : token.ExpiresAt <= now ? AccessTokenStatus.Expired
                : AccessTokenStatus.Active);

    private static void Audit(SchedulerDbContext db, CallerIdentity caller, string action, object detail, Instant now) =>
        db.Audit.Add(new AuditEntry
        {
            At = now,
            Actor = caller.VerifiedEmails.FirstOrDefault(),
            Channel = caller.Channel,
            Action = action,
            Detail = JsonSerializer.Serialize(detail),
        });
}
