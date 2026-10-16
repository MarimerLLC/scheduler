using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Core.Tests;

[TestClass]
public sealed class AdminRulesTests
{
    [TestMethod]
    [DataRow("Bob@Example.com", "bob@example.com")]
    [DataRow(" *@Marimer.LLC ", "*@marimer.llc")]
    [DataRow("@marimer.llc", "*@marimer.llc")]
    public void EntriesAreNormalized(string entry, string expected)
    {
        Assert.AreEqual(expected, OrganizerPatterns.Normalize(entry));
    }

    [TestMethod]
    [DataRow("*")]
    [DataRow("")]
    [DataRow("bob")]
    [DataRow("*@")]
    [DataRow("*@localhost")]
    [DataRow("*@*.example.com")]
    [DataRow("bob@*.example.com")]
    [DataRow("Bob <bob@example.com>")]
    public void BadEntriesAreRejected(string entry)
    {
        Assert.ThrowsExactly<SchedulingValidationException>(() => OrganizerPatterns.Normalize(entry));
    }

    [TestMethod]
    public void AnAddressMatchesItselfAndItsDomain()
    {
        CollectionAssert.AreEqual(
            new[] { "bob@marimer.llc", "*@marimer.llc" },
            OrganizerPatterns.CandidatesFor("bob@marimer.llc").ToArray());
    }

    [TestMethod]
    public void AdminsAreConfiguredAddressesSignedInNormally()
    {
        var options = new SchedulingOptions { Admins = ["Admin@Example.com"] };

        Assert.IsTrue(options.IsAdmin(new CallerIdentity(null, ["admin@example.com"], Channel.Mcp)));
        Assert.IsTrue(options.IsAdmin(new CallerIdentity(null, ["other@example.com", "admin@example.com"], Channel.Web)));
        Assert.IsFalse(options.IsAdmin(new CallerIdentity(null, ["bob@example.com"], Channel.Mcp)));
    }

    [TestMethod]
    public void AVotingLinkNeverMakesAnAdmin()
    {
        var options = new SchedulingOptions { Admins = ["admin@example.com"] };

        Assert.IsFalse(options.IsAdmin(new CallerIdentity(null, ["admin@example.com"], Channel.Web, MagicLinkEventId: Guid.NewGuid())));
    }

    [TestMethod]
    public void AdminsDontComeFromDomainsOrWildcards()
    {
        var options = new SchedulingOptions { Admins = ["*@example.com", "*"] };

        Assert.IsFalse(options.IsAdmin(new CallerIdentity(null, ["bob@example.com"], Channel.Mcp)));
    }
}
