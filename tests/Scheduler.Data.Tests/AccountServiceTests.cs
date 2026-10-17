using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed partial class AccountServiceTests
{
    private Harness _h = null!;
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Pat, signed in on the web with their personal address: addresses are only managed there.</summary>
    private static readonly CallerIdentity Pat = new(null, ["pat@example.com"], Channel.Web);

    /// <summary>Mallory, signed in on the web, who would like Pat's invitations.</summary>
    private static readonly CallerIdentity Mallory = new(null, ["mallory@example.com"], Channel.Web);

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    private AccountService Service => _h.Accounts;

    // ── Adding an address ───────────────────────────────────────────────────

    [TestMethod]
    public async Task AddingAnAddressEmailsItALinkAndStoresOnlyItsHash()
    {
        await Service.RequestAddressAsync(Pat, " Pat@Work.Example ", None);

        var email = (await _h.OutboxAsync()).Single();
        Assert.AreEqual("pat@work.example", email.To);
        Assert.IsNull(email.EventId);
        StringAssert.Contains(email.TextBody, "pat@example.com asked to add pat@work.example");
        var token = TokenIn(email);

        await using var db = await _h.Db.CreateDbContextAsync();
        var verification = await db.EmailVerifications.SingleAsync();
        CollectionAssert.AreEqual(VerificationLinks.Hash(token), verification.TokenHash);
        Assert.AreEqual(EmailVerificationPurpose.AddAddress, verification.Purpose);
        Assert.AreEqual(_h.Clock.GetCurrentInstant() + Duration.FromMinutes(30), verification.ExpiresAt);
        Assert.AreEqual("address.requested", (await _h.AuditAsync()).Single().Action);
    }

    [TestMethod]
    public async Task ConfirmingTheLinkAddsTheAddressToTheAccount()
    {
        await Service.RequestAddressAsync(Pat, "pat@work.example", None);
        var token = TokenIn((await _h.OutboxAsync()).Single());

        Assert.AreEqual(EmailVerificationPurpose.AddAddress, (await Service.FindVerificationAsync(token, None))!.Purpose);
        Assert.AreEqual("pat@work.example", await Service.ConfirmAddressAsync(Pat, token, None));

        var addresses = await Service.ListAddressesAsync(Pat, None);
        CollectionAssert.AreEqual(new[] { "pat@example.com", "pat@work.example" }, addresses.Select(a => a.Email).ToArray());
        Assert.IsTrue(addresses.Single(a => a.Email == "pat@example.com").SignedInWith);
        Assert.IsFalse(addresses.Single(a => a.Email == "pat@work.example").SignedInWith);
        Assert.AreEqual("address.added", (await _h.AuditAsync()).Last().Action);
    }

    [TestMethod]
    public async Task AnAddedAddressSeesItsInvitations()
    {
        var ev = await _h.CreateAsync(Harness.Request(participants: [new("pat@work.example", "Pat")]));
        await Service.RequestAddressAsync(Pat, "pat@work.example", None);
        await Service.ConfirmAddressAsync(Pat, TokenIn((await _h.OutboxAsync()).Last()), None);

        var view = await _h.Service.GetEventAsync(Pat, ev.ShortCode, null, None);

        Assert.AreEqual(ev.Id, view.Id);
    }

    [TestMethod]
    public async Task ALinkOnlyWorksForTheAccountThatAskedForIt()
    {
        await Service.RequestAddressAsync(Mallory, "pat@example.com", None);
        var token = TokenIn((await _h.OutboxAsync()).Single());

        // Pat, signed in as themselves, is tricked into confirming Mallory's link: the address doesn't move.
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.ConfirmAddressAsync(Pat, token, None));

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.IsFalse(await db.UserEmails.AnyAsync(e => e.Email == "pat@example.com"));
        Assert.IsNotNull(await Service.FindVerificationAsync(token, None), "the link is still unused");
    }

    [TestMethod]
    public async Task ALinkWorksOnceAndNotAfterItExpires()
    {
        await Service.RequestAddressAsync(Pat, "pat@work.example", None);
        await Service.RequestAddressAsync(Pat, "pat@home.example", None);
        var outbox = await _h.OutboxAsync();
        var used = TokenIn(outbox[0]);
        var late = TokenIn(outbox[1]);

        await Service.ConfirmAddressAsync(Pat, used, None);
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.ConfirmAddressAsync(Pat, used, None));
        Assert.IsNull(await Service.FindVerificationAsync(used, None));

        _h.Clock.Advance(Duration.FromMinutes(31));
        Assert.IsNull(await Service.FindVerificationAsync(late, None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.ConfirmAddressAsync(Pat, late, None));
        Assert.IsNull(await Service.FindVerificationAsync("not-a-token", None));
    }

    [TestMethod]
    public async Task AnAddressOnAnotherAccountIsNotTaken()
    {
        await _h.AccessTokens.CreateAsync(new CallerIdentity(null, ["pat@work.example"], Channel.Web), "RockBot", 30, None);
        await Service.RequestAddressAsync(Pat, "pat@work.example", None);
        var token = TokenIn((await _h.OutboxAsync()).Single());

        var error = await Assert.ThrowsExactlyAsync<SchedulingConflictException>(() => Service.ConfirmAddressAsync(Pat, token, None));

        StringAssert.Contains(error.Message, "already on another account");
        Assert.IsNotNull(await Service.FindVerificationAsync(token, None), "a refused confirmation doesn't use up the link");
    }

    [TestMethod]
    public async Task AddingAnAddressIsRefusedWhenItIsAlreadyYoursOrSuppressed()
    {
        var own = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RequestAddressAsync(Pat, "pat@example.com", None));
        StringAssert.Contains(own.Message, "already on your account");

        await _h.WithDbAsync(db =>
        {
            db.Suppressions.Add(new EmailSuppression { Email = "gone@example.com", Reason = SuppressionReason.Unsubscribed, At = _h.Clock.GetCurrentInstant() });
            return Task.CompletedTask;
        });
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RequestAddressAsync(Pat, "gone@example.com", None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RequestAddressAsync(Pat, "not an address", None));
        Assert.IsEmpty(await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task AnAddressGetsAtMostThreeLinksAnHourWhoeverAsks()
    {
        await Service.RequestAddressAsync(Pat, "busy@example.com", None);
        await Service.RequestAddressAsync(Mallory, "busy@example.com", None);
        await Service.RequestSignInLinkAsync("busy@example.com", null, "/", None);

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RequestAddressAsync(Pat, "busy@example.com", None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RequestSignInLinkAsync("busy@example.com", null, "/", None));

        _h.Clock.Advance(Duration.FromMinutes(61));
        await Service.RequestAddressAsync(Pat, "busy@example.com", None);
        Assert.HasCount(4, await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task AnAccountAsksForAtMostTenLinksAnHour()
    {
        for (var i = 0; i < VerificationLinks.MaxPerAccountPerHour; i++)
        {
            await Service.RequestAddressAsync(Pat, $"pat{i}@example.com", None);
            _h.Clock.Advance(Duration.FromMinutes(1));
        }

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RequestAddressAsync(Pat, "pat99@example.com", None));
    }

    [TestMethod]
    public async Task AddressesAreManagedOnlyOnTheWebSignedIn()
    {
        var agent = new CallerIdentity(null, ["pat@example.com"], Channel.Mcp);
        var voter = new CallerIdentity(null, ["pat@example.com"], Channel.Web, MagicLinkEventId: Guid.NewGuid());

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.RequestAddressAsync(agent, "pat@work.example", None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.RequestAddressAsync(voter, "pat@work.example", None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.ListAddressesAsync(agent, None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.RemoveAddressAsync(agent, "pat@work.example", None));
    }

    // ── Removing an address ─────────────────────────────────────────────────

    [TestMethod]
    public async Task AnAddressCanBeRemovedButNotTheOneYouSignedInWith()
    {
        await Service.RequestAddressAsync(Pat, "pat@work.example", None);
        await Service.ConfirmAddressAsync(Pat, TokenIn((await _h.OutboxAsync()).Single()), None);

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RemoveAddressAsync(Pat, "pat@example.com", None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.RemoveAddressAsync(Pat, "someone@example.com", None));

        // Signed in with the work address, Pat can remove the personal one.
        var atWork = new CallerIdentity(null, ["pat@work.example"], Channel.Web);
        await Service.RemoveAddressAsync(atWork, "pat@example.com", None);

        CollectionAssert.AreEqual(new[] { "pat@work.example" }, (await Service.ListAddressesAsync(atWork, None)).Select(a => a.Email).ToArray());
        Assert.AreEqual("address.removed", (await _h.AuditAsync()).Last().Action);
    }

    [TestMethod]
    public async Task BeforeThereIsAnAccountTheSignedInAddressIsListed()
    {
        var addresses = await Service.ListAddressesAsync(Pat, None);

        Assert.AreEqual("pat@example.com", addresses.Single().Email);
        Assert.IsNull(addresses.Single().VerifiedAt);
    }

    // ── Signing in by email link ────────────────────────────────────────────

    [TestMethod]
    public async Task ASignInLinkSignsInAsTheAddressOnce()
    {
        await Service.RequestSignInLinkAsync(" Pat@Contoso.com ", "Pat", "/account", None);
        var email = (await _h.OutboxAsync()).Single();
        Assert.AreEqual("pat@contoso.com", email.To);
        var token = TokenIn(email);

        Assert.AreEqual(EmailVerificationPurpose.SignIn, (await Service.FindVerificationAsync(token, None))!.Purpose);
        var signIn = await Service.ConfirmSignInAsync(token, None);

        Assert.AreEqual(new EmailSignIn("pat@contoso.com", "Pat", "/account"), signIn);
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.ConfirmSignInAsync(token, None));
    }

    [TestMethod]
    public async Task ASignInLinkCantAddAnAddressOrTheOtherWayRound()
    {
        await Service.RequestSignInLinkAsync("pat@work.example", null, "/", None);
        await Service.RequestAddressAsync(Pat, "pat@home.example", None);
        var outbox = await _h.OutboxAsync();

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.ConfirmAddressAsync(Pat, TokenIn(outbox[0]), None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.ConfirmSignInAsync(TokenIn(outbox[1]), None));
    }

    [TestMethod]
    public async Task ASignInLinkOnlyReturnsToALocalPath()
    {
        await Service.RequestSignInLinkAsync("pat@contoso.com", null, "https://evil.example/", None);

        var signIn = await Service.ConfirmSignInAsync(TokenIn((await _h.OutboxAsync()).Single()), None);

        Assert.AreEqual("/", signIn.ReturnUrl);
    }

    private static string TokenIn(OutboxEmail email)
    {
        var match = LinkToken().Match(email.TextBody);
        Assert.IsTrue(match.Success, email.TextBody);
        StringAssert.StartsWith(match.Value, Harness.BaseUrl + VerificationLinks.Path);
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"\S+/verify\?t=([A-Za-z0-9_-]+)")]
    private static partial Regex LinkToken();
}
