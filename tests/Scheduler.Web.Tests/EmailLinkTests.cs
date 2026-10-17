using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Data;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Tests;

/// <summary>Proving an address by the link emailed to it: signing in when the provider didn't verify it, and adding it to an account.</summary>
[TestClass]
public sealed partial class EmailLinkTests
{
    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task An_unverified_address_signs_in_through_the_link_emailed_to_it()
    {
        var browser = _app.CreateBrowser();

        // The provider (here the development sign-in, pretending) returns an address it didn't verify.
        var response = await browser.Http.PostAsync(DevSignIn.SignInPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "pat@contoso.com",
            ["unverified"] = "true",
            ["returnUrl"] = "/account",
        }));
        var offer = SchedulerApp.Browser.RedirectOf(response);
        Assert.AreEqual(EmailLinkSignIn.Path, offer.AbsolutePath);
        Assert.IsFalse(response.Headers.Contains("Set-Cookie"), "not signed in yet");
        Assert.IsEmpty(await _app.OutboxAsync(), "nothing is sent until the person asks");

        response = await browser.SubmitFormAsync(offer.PathAndQuery);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "We sent a link to <strong>pat@contoso.com</strong>");
        var link = LinkIn((await _app.OutboxAsync()).Single(), "pat@contoso.com");

        // A mail scanner fetching the link changes nothing; the person's button press signs them in.
        var phone = _app.CreateBrowser();
        await phone.Http.GetAsync(link);
        response = await phone.SubmitFormAsync(link);
        Assert.AreEqual("/account", SchedulerApp.Browser.RedirectOf(response).AbsolutePath);
        StringAssert.Contains(string.Join(";", response.Headers.GetValues("Set-Cookie")), "scheduler.auth=");

        response = await _app.CreateBrowser().Http.GetAsync(link);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "expired or has already been used");
    }

    [TestMethod]
    public async Task A_tampered_sign_in_offer_is_refused()
    {
        var response = await _app.CreateBrowser().Http.GetAsync(EmailLinkSignIn.Path + "?p=forged");

        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "This sign-in has expired");
    }

    [TestMethod]
    public async Task A_link_adds_the_address_to_the_account_signed_in_on_the_browser_that_opens_it()
    {
        await WithAccountsAsync(accounts => accounts.RequestAddressAsync(new CallerIdentity(null, ["pat@example.com"], Channel.Web), "pat@work.example", CancellationToken.None));
        var link = LinkIn((await _app.OutboxAsync()).Single(), "pat@work.example");

        // Not signed in: the page asks the person to sign in to the account first, and offers no button.
        var browser = _app.CreateBrowser();
        var page = await (await browser.Http.GetAsync(link)).Content.ReadAsStringAsync();
        StringAssert.Contains(page, "sign in to that account");
        Assert.DoesNotContain("_handler", page);

        await SignInAsync(browser, "pat@example.com");
        var response = await browser.SubmitFormAsync(link);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Added pat@work.example to your account.");

        await using var db = await _app.Services.GetRequiredService<IDbContextFactory<SchedulerDbContext>>().CreateDbContextAsync();
        var owner = await db.UserEmails.SingleAsync(e => e.Email == "pat@example.com");
        Assert.AreEqual(owner.UserId, (await db.UserEmails.SingleAsync(e => e.Email == "pat@work.example")).UserId);
    }

    [TestMethod]
    public async Task Someone_elses_link_does_not_add_your_address_to_their_account()
    {
        await WithAccountsAsync(accounts => accounts.RequestAddressAsync(new CallerIdentity(null, ["mallory@example.com"], Channel.Web), "pat@example.com", CancellationToken.None));
        var link = LinkIn((await _app.OutboxAsync()).Single(), "pat@example.com");

        var browser = _app.CreateBrowser();
        await SignInAsync(browser, "pat@example.com");
        var response = await browser.SubmitFormAsync(link);

        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "adds the address to a different account");
    }

    private static async Task SignInAsync(SchedulerApp.Browser browser, string email) =>
        await browser.Http.PostAsync(DevSignIn.SignInPath, new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }));

    private async Task WithAccountsAsync(Func<IAccountService, Task> action)
    {
        using var scope = _app.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IAccountService>());
    }

    /// <summary>The link in an email, as a path on this app.</summary>
    private static string LinkIn(OutboxEmail email, string to)
    {
        Assert.AreEqual(to, email.To);
        var match = Link().Match(email.TextBody);
        Assert.IsTrue(match.Success, email.TextBody);
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"https?://[^/\s]+(/verify\?t=[A-Za-z0-9_-]+)")]
    private static partial Regex Link();
}
