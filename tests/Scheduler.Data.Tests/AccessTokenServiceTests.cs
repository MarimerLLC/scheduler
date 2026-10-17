using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class AccessTokenServiceTests
{
    private Harness _h = null!;
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Pat, signed in on the web: tokens are only managed there.</summary>
    private static readonly CallerIdentity Pat = new(null, ["pat@example.com"], Channel.Web);

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    private AccessTokenService Service => _h.AccessTokens;

    [TestMethod]
    public async Task ANewTokenIsShownOnceAndOnlyItsHashIsStored()
    {
        var created = await Service.CreateAsync(Pat, "  RockBot  ", 90, None);

        Assert.IsTrue(AccessTokens.IsWellFormed(created.Token));
        Assert.AreEqual("RockBot", created.View.Name);
        Assert.AreEqual(created.Token[..14], created.View.Prefix);
        Assert.AreEqual("2026-10-16 15:00 UTC", created.View.CreatedAt);
        Assert.AreEqual("2027-01-14 15:00 UTC", created.View.ExpiresAt);
        Assert.IsNull(created.View.LastUsedAt);
        Assert.AreEqual(AccessTokenStatus.Active, created.View.Status);

        await using var db = await _h.Db.CreateDbContextAsync();
        var stored = await db.PersonalAccessTokens.SingleAsync();
        CollectionAssert.AreEqual(AccessTokens.Hash(created.Token), stored.TokenHash);
        Assert.IsFalse(stored.Prefix.Length >= created.Token.Length, "the full token is never stored");
    }

    [TestMethod]
    public async Task TheFirstTokenCreatesTheAccount()
    {
        await Service.CreateAsync(Pat, "RockBot", 30, None);

        await using var db = await _h.Db.CreateDbContextAsync();
        var account = await db.Users.Include(u => u.Emails).SingleAsync();
        Assert.AreEqual("pat@example.com", account.Emails.Single().Email);
        Assert.AreEqual(account.Id, (await db.PersonalAccessTokens.SingleAsync()).UserId);
    }

    [TestMethod]
    public async Task ATokenActsAsItsAccountWithEveryVerifiedAddress()
    {
        var created = await Service.CreateAsync(Pat, "RockBot", 30, None);
        await _h.WithDbAsync(async db =>
        {
            var account = await db.Users.SingleAsync();
            db.UserEmails.Add(new UserEmail { Email = "pat@work.example", UserId = account.Id, VerifiedAt = _h.Clock.GetCurrentInstant() });
        });

        var caller = await Service.AuthenticateAsync(created.Token, Channel.Mcp, None);

        Assert.IsNotNull(caller);
        Assert.IsNotNull(caller.UserId);
        Assert.AreEqual(Channel.Mcp, caller.Channel);
        CollectionAssert.AreEqual(new[] { "pat@example.com", "pat@work.example" }, caller.VerifiedEmails.ToArray());
    }

    [TestMethod]
    public async Task ATokenCanReadAndVoteOnInvitationsToItsAddress()
    {
        var created = await Service.CreateAsync(new CallerIdentity(null, ["bob@example.com"], Channel.Web), "RockBot", 30, None);
        var ev = await _h.CreateAsync();

        var bob = (await Service.AuthenticateAsync(created.Token, Channel.Mcp, None))!;
        var voted = await _h.VoteAsync(bob, ev, (0, VoteValue.Preferred));

        Assert.AreEqual(VoteValue.Preferred, voted.Slots[0].MyVote);
    }

    [TestMethod]
    public async Task UnknownMalformedExpiredAndRevokedTokensAreRefused()
    {
        var created = await Service.CreateAsync(Pat, "RockBot", 30, None);
        var revoked = await Service.CreateAsync(Pat, "Old script", 30, None);
        await Service.RevokeAsync(Pat, revoked.View.Id, None);

        Assert.IsNull(await Service.AuthenticateAsync(AccessTokens.New(), Channel.Mcp, None), "unknown");
        Assert.IsNull(await Service.AuthenticateAsync("sched_pat_short", Channel.Mcp, None), "malformed");
        Assert.IsNull(await Service.AuthenticateAsync(created.Token + "x", Channel.Mcp, None), "altered");
        Assert.IsNull(await Service.AuthenticateAsync(revoked.Token, Channel.Mcp, None), "revoked");
        Assert.IsNotNull(await Service.AuthenticateAsync(created.Token, Channel.Mcp, None), "still active");

        _h.Clock.Advance(Duration.FromDays(30));
        Assert.IsNull(await Service.AuthenticateAsync(created.Token, Channel.Mcp, None), "expired");
        Assert.AreEqual(AccessTokenStatus.Expired, (await Service.ListAsync(Pat, None)).Single(t => t.Name == "RockBot").Status);
    }

    [TestMethod]
    public async Task LastUsedIsRecordedButNotOnEveryCall()
    {
        var created = await Service.CreateAsync(Pat, "RockBot", 30, None);

        await Service.AuthenticateAsync(created.Token, Channel.Mcp, None);
        Assert.AreEqual("2026-10-16 15:00 UTC", (await Service.ListAsync(Pat, None)).Single().LastUsedAt);

        _h.Clock.Advance(Duration.FromMinutes(2));
        await Service.AuthenticateAsync(created.Token, Channel.Mcp, None);
        Assert.AreEqual("2026-10-16 15:00 UTC", (await Service.ListAsync(Pat, None)).Single().LastUsedAt);

        _h.Clock.Advance(Duration.FromMinutes(4));
        await Service.AuthenticateAsync(created.Token, Channel.Mcp, None);
        Assert.AreEqual("2026-10-16 15:06 UTC", (await Service.ListAsync(Pat, None)).Single().LastUsedAt);
    }

    [TestMethod]
    public async Task TokensAreListedNewestFirstAndOnlyToTheirOwner()
    {
        await Service.CreateAsync(Pat, "First", 30, None);
        _h.Clock.Advance(Duration.FromMinutes(1));
        await Service.CreateAsync(Pat, "Second", 30, None);
        await Service.CreateAsync(new CallerIdentity(null, ["bob@example.com"], Channel.Web), "Bob's", 30, None);

        CollectionAssert.AreEqual(new[] { "Second", "First" }, (await Service.ListAsync(Pat, None)).Select(t => t.Name).ToArray());
        Assert.AreEqual(0, (await Service.ListAsync(new CallerIdentity(null, ["carol@example.com"], Channel.Web), None)).Count);
    }

    [TestMethod]
    public async Task OnlyTheOwnerCanRevokeAndRevokingTwiceIsHarmless()
    {
        var created = await Service.CreateAsync(Pat, "RockBot", 30, None);
        var bob = new CallerIdentity(null, ["bob@example.com"], Channel.Web);
        await Service.CreateAsync(bob, "Bob's", 30, None);

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RevokeAsync(bob, created.View.Id, None));
        Assert.IsNotNull(await Service.AuthenticateAsync(created.Token, Channel.Mcp, None));

        await Service.RevokeAsync(Pat, created.View.Id, None);
        await Service.RevokeAsync(Pat, created.View.Id, None);
        Assert.AreEqual(AccessTokenStatus.Revoked, (await Service.ListAsync(Pat, None)).Single().Status);
    }

    [TestMethod]
    public async Task TokensAreManagedOnlyOnTheWebBySignedInPeople()
    {
        var byAgent = new CallerIdentity(null, ["pat@example.com"], Channel.Mcp);
        var byLink = new CallerIdentity(null, ["pat@example.com"], Channel.Web, MagicLinkEventId: Guid.NewGuid());

        foreach (var caller in new[] { byAgent, byLink })
        {
            await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.CreateAsync(caller, "x", 30, None));
            await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.ListAsync(caller, None));
            await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.RevokeAsync(caller, Guid.NewGuid(), None));
        }
    }

    [TestMethod]
    public async Task NameAndLifetimeAreChecked()
    {
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.CreateAsync(Pat, " ", 30, None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.CreateAsync(Pat, new string('x', 61), 30, None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.CreateAsync(Pat, "two\nlines", 30, None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.CreateAsync(Pat, "RockBot", 0, None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.CreateAsync(Pat, "RockBot", 366, None));

        Assert.AreEqual("2027-10-16 15:00 UTC", (await Service.CreateAsync(Pat, "RockBot", 365, None)).View.ExpiresAt);
    }

    [TestMethod]
    public async Task ActiveTokensAreCappedPerAccount()
    {
        var tokens = new List<CreatedAccessToken>();
        for (var i = 0; i < AccessTokens.MaxActivePerAccount; i++)
            tokens.Add(await Service.CreateAsync(Pat, $"Token {i}", 30, None));

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.CreateAsync(Pat, "One too many", 30, None));

        await Service.RevokeAsync(Pat, tokens[0].View.Id, None);
        await Service.CreateAsync(Pat, "Replacement", 30, None);
    }

    [TestMethod]
    public async Task CreatingAndRevokingAreAudited()
    {
        var created = await Service.CreateAsync(Pat, "RockBot", 30, None);
        await Service.RevokeAsync(Pat, created.View.Id, None);

        var audit = await _h.AuditAsync();
        CollectionAssert.AreEqual(new[] { "token.created", "token.revoked" }, audit.Select(a => a.Action).ToArray());
        Assert.IsTrue(audit.All(a => a.Actor == "pat@example.com" && a.EventId is null && a.Channel == Channel.Web));
        StringAssert.Contains(audit[0].Detail, "RockBot");
        Assert.IsFalse(audit.Any(a => a.Detail!.Contains(created.Token)), "the token itself is never logged");
    }
}
