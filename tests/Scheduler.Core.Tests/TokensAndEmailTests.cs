using Scheduler.Core;
using Scheduler.Core.Security;

namespace Scheduler.Core.Tests;

[TestClass]
public sealed class TokensAndEmailTests
{
    [TestMethod]
    public void MagicTokenMatchesOnlyItsOwnHash()
    {
        var token = Tokens.NewMagicToken();
        var hash = Tokens.Hash(token);

        Assert.IsTrue(Tokens.Matches(token, hash));
        Assert.IsFalse(Tokens.Matches(Tokens.NewMagicToken(), hash));
    }

    [TestMethod]
    public void MagicTokenIsUrlSafe()
    {
        var token = Tokens.NewMagicToken();

        Assert.AreEqual(43, token.Length);
        Assert.IsTrue(token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), token);
    }

    [TestMethod]
    public void ShortCodeUsesCrockfordAlphabet()
    {
        var code = Tokens.NewShortCode();

        Assert.AreEqual(8, code.Length);
        Assert.IsFalse(code.Any(c => c is 'I' or 'L' or 'O' or 'U'), code);
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
