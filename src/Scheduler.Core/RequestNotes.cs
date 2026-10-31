using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Scheduler.Core;

/// <summary>
/// Cleans and checks the note someone sends with a request to organize. Anyone who can sign in can write one,
/// and an admin reads it (on the web page, or through their agent), so it is held to stricter rules than the
/// notes organizers and participants write: one line, at most <see cref="FieldLimits.NoteMax"/> characters, no
/// links, no invisible or control characters, no stacked accents, no long runs of one character, and no word on
/// the block list. A refusal names the rule but never the word. See docs/design.md, "Requests to organize".
/// </summary>
public static partial class RequestNotes
{
    /// <summary>Same character this many times in a row is junk ("!!!!!!!!", "aaaaaaaa").</summary>
    public const int MaxRepeatedCharacter = 7;

    /// <summary>Combining marks after one letter; more is "zalgo" text meant to spill over the page.</summary>
    public const int MaxCombiningMarks = 2;

    /// <summary>
    /// Words on the package's list that are ordinary, or that name who someone is, and must not get a note refused.
    /// Extend it with <c>Scheduler:AllowedNoteTerms</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAllowedTerms =
    [
        "balls", "bloody", "bugger", "bum", "butt", "crap", "damn", "dick", "drunk", "fanny", "fart", "god",
        "hell", "homo", "kill", "knob", "lesbian", "lmao", "naked", "queer", "screw", "sex", "strip", "suck",
        "sucks", "weed", "willy", "beaver",
    ];

    private static readonly ProfanityFilter.ProfanityFilter BuiltIn = CreateBuiltIn();

    /// <summary>
    /// Returns the note as it will be stored (normalized, whitespace collapsed to single spaces, trimmed), or null
    /// for an empty note. Throws <see cref="SchedulingValidationException"/> if a rule refuses it.
    /// </summary>
    /// <param name="blockedTerms">More words or phrases to refuse (<c>Scheduler:BlockedNoteTerms</c>).</param>
    /// <param name="allowedTerms">More words not to refuse (<c>Scheduler:AllowedNoteTerms</c>).</param>
    public static string? Clean(string? note, IEnumerable<string>? blockedTerms = null, IEnumerable<string>? allowedTerms = null)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;

        // Checked before normalizing too: normalizing throws on broken UTF-16.
        CheckCharacters(note);
        // NFKC folds look-alikes such as full-width letters and ligatures into plain ones, so they are stored and
        // checked as what they look like.
        var text = note.Normalize(NormalizationForm.FormKC);
        CheckCharacters(text);
        text = Whitespace().Replace(text, " ").Trim();
        if (text.Length == 0)
            return null;
        if (text.Length > FieldLimits.NoteMax)
            throw new SchedulingValidationException($"The note can be at most {FieldLimits.NoteMax} characters.");
        if (Link().IsMatch(text))
            throw new SchedulingValidationException("The note can't contain links or web addresses.");
        if (Repeated().IsMatch(text))
            throw new SchedulingValidationException("The note repeats one character too many times in a row.");
        if (ContainsBlockedTerm(text, blockedTerms, allowedTerms))
            throw new SchedulingValidationException(
                "The note contains language this server doesn't accept. Reword it, or send the request without a note.");

        return text;
    }

    private static void CheckCharacters(string text)
    {
        var marks = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar)
                throw Invisible();

            var category = Rune.GetUnicodeCategory(rune);
            switch (category)
            {
                case UnicodeCategory.Control when !Rune.IsWhiteSpace(rune):
                case UnicodeCategory.Format:            // zero-width spaces and joiners, bidi overrides
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.OtherNotAssigned:
                    throw Invisible();
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                    if (++marks > MaxCombiningMarks)
                        throw new SchedulingValidationException("The note has too many accents stacked on one letter.");
                    break;
                default:
                    marks = 0;
                    break;
            }
        }

        static SchedulingValidationException Invisible() =>
            new("The note can't contain invisible or control characters.");
    }

    private static bool ContainsBlockedTerm(string text, IEnumerable<string>? blockedTerms, IEnumerable<string>? allowedTerms)
    {
        var allowed = new HashSet<string>(
            (allowedTerms ?? []).Select(t => Fold(t).Trim()).Where(t => t.Length > 0),
            StringComparer.Ordinal);
        var blocked = (blockedTerms ?? []).Select(t => (IReadOnlyList<string>)Words(Fold(t))).Where(w => w.Count > 0).ToList();

        var folded = Fold(text);
        // Checked as written and with digits read as letters ("5h1t"), and with spaced-out letters joined ("s h i t").
        foreach (var candidate in new[] { folded, Leet(folded) }.Distinct())
        {
            var words = Words(candidate);
            if (Matches(words, allowed, blocked) || Matches(JoinSingleLetters(words), allowed, blocked))
                return true;
        }
        return false;
    }

    private static bool Matches(IReadOnlyList<string> words, HashSet<string> allowed, List<IReadOnlyList<string>> blocked)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (!allowed.Contains(word) && BuiltIn.IsProfanity(word))
                return true;
            if (i + 1 < words.Count && !allowed.Contains($"{word} {words[i + 1]}") && BuiltIn.IsProfanity($"{word} {words[i + 1]}"))
                return true;
        }

        foreach (var phrase in blocked)
        {
            for (var i = 0; i + phrase.Count <= words.Count; i++)
            {
                if (!allowed.Contains(string.Join(' ', phrase)) && phrase.Select((w, j) => words[i + j] == w).All(x => x))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Lower case without accents, so "Ünïcödé" is checked as "unicode".</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(c);
        }
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Leet(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' or '@' => 'a',
                '5' or '$' => 's',
                '7' => 't',
                _ => chars[i],
            };
        }
        return new string(chars);
    }

    private static List<string> Words(string text) => Letters().Matches(text).Select(m => m.Value).ToList();

    /// <summary>"s h i t happens" becomes "shit happens": runs of three or more single letters are joined.</summary>
    private static List<string> JoinSingleLetters(IReadOnlyList<string> words)
    {
        var joined = new List<string>(words.Count);
        var run = new StringBuilder();
        foreach (var word in words)
        {
            if (word.Length == 1)
            {
                run.Append(word);
                continue;
            }
            Flush();
            joined.Add(word);
        }
        Flush();
        return joined;

        void Flush()
        {
            if (run.Length >= 3)
                joined.Add(run.ToString());
            else
                joined.AddRange(run.ToString().Select(c => c.ToString()));
            run.Clear();
        }
    }

    private static ProfanityFilter.ProfanityFilter CreateBuiltIn()
    {
        var filter = new ProfanityFilter.ProfanityFilter();
        foreach (var term in DefaultAllowedTerms)
            filter.AllowList.Add(term);
        return filter;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>A scheme (<c>https://</c>, <c>ftp://</c>), <c>www.</c>, or a host followed by a path (<c>example.com/x</c>).</summary>
    [GeneratedRegex(@"[a-z][a-z0-9+.\-]*://|\bwww\.|\b[a-z0-9\-]+(\.[a-z0-9\-]+)*\.[a-z]{2,}/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    /// <summary>One character more than <see cref="MaxRepeatedCharacter"/> times in a row.</summary>
    [GeneratedRegex(@"(\S)\1{7,}")]
    private static partial Regex Repeated();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Letters();
}
