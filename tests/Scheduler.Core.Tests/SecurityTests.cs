using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Core.Tests;

[TestClass]
public sealed class SecurityTests
{
    private static readonly MagicLinks Links = new(MagicLinks.NewKey());

    [TestMethod]
    public void MagicLinkTokenIsStableForTheSameNonce()
    {
        var participant = Guid.CreateVersion7();
        var nonce = MagicLinks.NewNonce();

        // The same link must work in the invitation and in every later email.
        Assert.AreEqual(Links.CreateToken(participant, nonce), Links.CreateToken(participant, nonce));
        Assert.IsTrue(Links.Matches(Links.CreateToken(participant, nonce), participant, nonce));
    }

    [TestMethod]
    public void NewNonceRevokesTheOldLink()
    {
        var participant = Guid.CreateVersion7();
        var token = Links.CreateToken(participant, MagicLinks.NewNonce());

        Assert.IsFalse(Links.Matches(token, participant, MagicLinks.NewNonce()));
    }

    [TestMethod]
    public void TokenDoesNotWorkForAnotherParticipantOrKey()
    {
        var participant = Guid.CreateVersion7();
        var nonce = MagicLinks.NewNonce();
        var token = Links.CreateToken(participant, nonce);

        Assert.IsFalse(Links.Matches(token, Guid.CreateVersion7(), nonce));
        Assert.IsFalse(new MagicLinks(MagicLinks.NewKey()).Matches(token, participant, nonce));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not base64!")]
    [DataRow("c2hvcnQ")]
    public void MalformedTokensDontMatch(string? token)
    {
        Assert.IsFalse(Links.Matches(token, Guid.CreateVersion7(), MagicLinks.NewNonce()));
    }

    [TestMethod]
    public void TokenIsUrlSafe()
    {
        var token = Links.CreateToken(Guid.CreateVersion7(), MagicLinks.NewNonce());

        Assert.AreEqual(43, token.Length);
        Assert.IsTrue(token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), token);
    }

    [TestMethod]
    public void ShortKeyIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MagicLinks(new byte[16]));
    }

    [TestMethod]
    public void ShortCodeUsesCrockfordAlphabet()
    {
        var code = Tokens.NewShortCode();

        Assert.AreEqual(8, code.Length);
        Assert.IsFalse(code.Any(c => c is 'I' or 'L' or 'O' or 'U'), code);
    }

    [TestMethod]
    [DataRow("7KQ2M9XD")]
    [DataRow(" 7kq2m9xd ")]
    [DataRow("[sched:7KQ2M9XD]")]
    [DataRow("sched:7kq2m9xd")]
    public void ShortCodeAcceptsTheFormsAgentsPass(string input)
    {
        Assert.AreEqual("7KQ2M9XD", Tokens.NormalizeShortCode(input));
    }

    [TestMethod]
    public void OrganizerAllowlistMatchesAddressesAndDomains()
    {
        var options = new SchedulingOptions { Organizers = ["rocky@lhotka.net", "*@Marimer.LLC"] };

        Assert.IsTrue(options.IsAllowlistedOrganizer("rocky@lhotka.net"));
        Assert.IsTrue(options.IsAllowlistedOrganizer("anyone@marimer.llc"));
        Assert.IsFalse(options.IsAllowlistedOrganizer("someone@lhotka.net"));
        Assert.IsFalse(options.IsAllowlistedOrganizer("evil@notmarimer.llc"));
    }

    [TestMethod]
    public void EmptyAllowlistAllowsNobodyAndStarAllowsAnyone()
    {
        Assert.IsFalse(new SchedulingOptions().IsAllowlistedOrganizer("rocky@lhotka.net"));
        Assert.IsTrue(new SchedulingOptions { Organizers = ["*"] }.IsAllowlistedOrganizer("rocky@lhotka.net"));
    }

    [TestMethod]
    public void EmailIsTrimmedAndLowerCased()
    {
        Assert.AreEqual("rocky@example.com", EmailAddresses.Normalize("  Rocky@Example.COM "));
    }

    [TestMethod]
    [DataRow("not an address")]
    [DataRow("Rocky <rocky@example.com>")]
    [DataRow("a@example.com, b@example.com")]
    [DataRow("")]
    public void AnythingButOnePlainAddressIsRejected(string email)
    {
        Assert.ThrowsExactly<SchedulingValidationException>(() => EmailAddresses.Normalize(email));
    }
}
