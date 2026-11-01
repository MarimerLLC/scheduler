namespace Scheduler.Core.Tests;

[TestClass]
public sealed class RequestNotesTests
{
    [TestMethod]
    [DataRow("I run the Contoso partner program and schedule our monthly syncs.")]
    [DataRow("ASP.NET developer at Contoso; contoso.com is where I work.")]
    [DataRow("Organizer for the Scunthorpe user group.")]
    [DataRow("I'm queer and run our Pride meetup, so I'd like to kill two birds with one stone here.")]
    [DataRow("Café owner from Zürich 👋")]
    [DataRow("e.g. Q4 planning, i.e. the usual!!!")]
    public void OrdinaryNotesPass(string note)
    {
        Assert.AreEqual(note, RequestNotes.Clean(note));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  \n\t ")]
    public void AnEmptyNoteIsNull(string? note)
    {
        Assert.IsNull(RequestNotes.Clean(note));
    }

    [TestMethod]
    public void WhitespaceIsCollapsedToOneLine()
    {
        Assert.AreEqual("Hello there. Second line.", RequestNotes.Clean("  Hello\r\n\r\n  there.\tSecond   line.  "));
    }

    [TestMethod]
    public void LookAlikeCharactersAreStoredAsPlainOnes()
    {
        Assert.AreEqual("Hello fine", RequestNotes.Clean("Ｈｅｌｌｏ ﬁne"));
    }

    [TestMethod]
    public void ANoteOverTheLimitIsRefused()
    {
        Assert.AreEqual(FieldLimits.NoteMax, RequestNotes.Clean(new string('a', 7) + " " + string.Concat(Enumerable.Repeat("word ", 54)) + "ab")!.Length);
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean(string.Concat(Enumerable.Repeat("word ", 57))));
        StringAssert.Contains(ex.Message, "280");
    }

    [TestMethod]
    [DataRow("Cheap deals at https://spam.example")]
    [DataRow("HTTP://SPAM.EXAMPLE")]
    [DataRow("see www.spam.example")]
    [DataRow("go to spam.example/offer now")]
    [DataRow("javascript://alert(1)")]
    public void LinksAreRefused(string note)
    {
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean(note));
        StringAssert.Contains(ex.Message, "links");
    }

    [TestMethod]
    [DataRow("bell\u0007")]
    [DataRow("right‮to left")]
    [DataRow("zero​width")]
    [DataRow("privateuse")]
    [DataRow("broken \uD800 surrogate")]
    public void InvisibleAndControlCharactersAreRefused(string note)
    {
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean(note));
        StringAssert.Contains(ex.Message, "invisible");
    }

    [TestMethod]
    public void StackedAccentsAreRefused()
    {
        Assert.AreEqual("résumé", RequestNotes.Clean("résumé"));
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean("hé̂̃̄llo"));
        StringAssert.Contains(ex.Message, "accents");
    }

    [TestMethod]
    public void LongRunsOfOneCharacterAreRefused()
    {
        Assert.IsNotNull(RequestNotes.Clean("Wow!!!!!!!"));
        Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean("Wow!!!!!!!!"));
    }

    [TestMethod]
    [DataRow("this is shit")]
    [DataRow("THIS IS SHIT")]
    [DataRow("this is s h i t")]
    [DataRow("this is 5h1t")]
    [DataRow("this is shït")]
    [DataRow("this is ｓｈｉｔ")]
    [DataRow("shit.")]
    public void BlockedWordsAreRefusedHoweverTheyAreWritten(string note)
    {
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean(note));
        StringAssert.Contains(ex.Message, "language");
        Assert.DoesNotContain("shit", ex.Message, "the refusal never repeats the word");
    }

    [TestMethod]
    public void ConfiguredTermsAreRefusedAndAllowed()
    {
        string[] blocked = ["crypto giveaway", "pills"];

        Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean("Huge Crypto Giveaway inside", blocked));
        Assert.ThrowsExactly<SchedulingValidationException>(() => RequestNotes.Clean("cheap p1lls", blocked));
        Assert.IsNotNull(RequestNotes.Clean("crypto conference giveaway", blocked), "a phrase matches only as a whole");
        Assert.IsNotNull(RequestNotes.Clean("pills", blocked, allowedTerms: ["pills"]));
        Assert.IsNotNull(RequestNotes.Clean("I run the shit list", allowedTerms: ["shit"]));
    }
}
