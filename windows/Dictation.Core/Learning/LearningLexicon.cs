using System.Collections.Frozen;
using Dictation.Services;

namespace Dictation.Core.Learning;

/// <summary>
/// The ordinary-word test the learning rules ask (LEARNING.md §2).
/// </summary>
/// <remarks>
/// A token is ordinary when its lowercase form, cut at the first apostrophe and
/// split on <c>-</c>, <c>.</c> and <c>_</c>, has every part in the word list,
/// directly, after one of three plural rules, or with an -ed or -ing ending
/// dropped; a part with no letter is a number and always ordinary.
/// <b>An unavailable word list fails CLOSED</b>: every token is ordinary, so
/// nothing is distinctive and no alias is learned. That is the opposite of the import lexicon's direction,
/// on purpose: a missing word there is one click from removal, a wrong alias
/// here rewrites every later dictation.
/// </remarks>
public interface ILearningLexicon
{
    /// <summary>False when the word list could not be read.</summary>
    bool IsAvailable { get; }

    /// <summary>Is this token ordinary? True for everything when
    /// <see cref="IsAvailable"/> is false.</summary>
    bool IsOrdinary(string token);
}

/// <summary>
/// The §2 rules over a base-word test the subclass supplies.
/// </summary>
public abstract class LearningLexicon : ILearningLexicon
{
    private static readonly char[] PartSeparators = ['-', '.', '_'];

    /// <summary>A doubled final consonant before -ed or -ing
    /// (<c>stopped</c>, <c>running</c>).</summary>
    private const string DoubledConsonants = "bcdfghjklmnpqrstvwxyz";

    /// <summary>The shortest base a verb ending may leave (§2).</summary>
    private const int MinVerbBase = 3;

    private static readonly string[] VerbEndings = ["ed", "ing"];

    /// <inheritdoc />
    public abstract bool IsAvailable { get; }

    /// <inheritdoc />
    public bool IsOrdinary(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!IsAvailable)
        {
            return true;
        }

        var word = LearningText.Lower(token);
        for (var i = 0; i < word.Length; i++)
        {
            if (LearningText.IsApostrophe(word[i]))
            {
                if (i > 0)
                {
                    word = word[..i];
                }

                break;
            }
        }

        // A part with no letter ("10", "4417", "1-800") is ordinary: a number
        // on its own is never vocabulary, so a fix with no letter (a PIN, a
        // phone number) is never stored, prompted or rewritten. A number beside
        // a distinctive word can be ("Miles 44171234", after a second
        // dictation; LEARNING.md §2).
        var parts = word.Split(PartSeparators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || parts.All(p => !LearningText.HasLetter(p) || IsBaseWord(p) || IsVerbForm(p));
    }

    /// <summary>
    /// <c>changed</c>, <c>running</c>, <c>tried</c>: a verb ending dropped. The
    /// base is the stem, the stem plus <c>e</c> (<c>chang|ed</c>), the stem
    /// less one of a doubled final consonant (<c>runn|ing</c>) or, before -ed,
    /// the stem with its final <c>i</c> turned back into <c>y</c>
    /// (<c>tri|ed</c>). It is looked up with the plural rules and must have at
    /// least 3 code points, so <c>Ying</c> and <c>Xing</c> are not read as a
    /// letter plus -ing. An excluded name is checked first, as in
    /// <see cref="IsBaseWord"/>: <c>Downing</c> must not fold to <c>down</c>.
    /// </summary>
    private bool IsVerbForm(string part)
    {
        if (IsExcludedName(part))
        {
            return false;
        }

        foreach (var suffix in VerbEndings)
        {
            if (!part.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // The endings are ASCII, so cutting UTF-16 units cuts code points.
            var stem = part[..^suffix.Length];
            var bases = new List<string> { stem, stem + "e" };
            if (stem.Length >= 2 && stem[^1] == stem[^2] && DoubledConsonants.Contains(stem[^1]))
            {
                bases.Add(stem[..^1]);
            }

            if (suffix == "ed" && stem.EndsWith('i'))
            {
                bases.Add(stem[..^1] + "y");
            }

            if (bases.Any(b => LearningText.Length(b) >= MinVerbBase && IsBaseWord(b)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Is this lowercase part in the list, directly or as a plural
    /// (<c>-ies</c>→<c>-y</c>, drop <c>-es</c>, drop <c>-s</c>)? Also asked
    /// about the base a verb ending leaves (<c>changed</c> → <c>change</c>).
    /// </summary>
    protected abstract bool IsBaseWord(string part);

    /// <summary>
    /// Is this lowercase part a capitalized-only name the list leaves out
    /// (<c>Downing</c>)? Such a part is never made ordinary by an ending. A list
    /// the caller declares has no names to leave out.
    /// </summary>
    protected virtual bool IsExcludedName(string part) => false;
}

/// <summary>A lexicon over an explicit word list: the golden cases' lexicon,
/// and tests.</summary>
public sealed class WordListLexicon : LearningLexicon
{
    private readonly FrozenSet<string> _words;

    /// <param name="words">Base forms; trimmed and lowercased on the way in,
    /// blanks dropped.</param>
    public WordListLexicon(IEnumerable<string?> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        _words = words
            .Where(w => !string.IsNullOrEmpty(w) && LearningText.Strip(w).Length > 0)
            .Select(w => LearningText.Lower(LearningText.Strip(w!)))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override bool IsAvailable => _words.Count > 0;

    /// <inheritdoc />
    protected override bool IsBaseWord(string part)
    {
        if (_words.Contains(part))
        {
            return true;
        }

        if (part.EndsWith("ies", StringComparison.Ordinal) && part.Length > 3
            && _words.Contains(string.Concat(part.AsSpan(0, part.Length - 3), "y")))
        {
            return true;
        }

        if (part.EndsWith("es", StringComparison.Ordinal) && part.Length > 2 && _words.Contains(part[..^2]))
        {
            return true;
        }

        return part.EndsWith('s') && part.Length > 1 && _words.Contains(part[..^1]);
    }
}

/// <summary>
/// The shipped lexicon: Webster's Second (<c>web2</c>), the word list the
/// Windows build embeds in <c>Dictation.Services</c> and macOS reads from
/// <c>/usr/share/dict/web2</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VocabularyLexicon.IsCommonWord"/> applies the same three plural
/// rules, and is asked about LOWERCASED parts only; the -ed/-ing endings are
/// <see cref="LearningLexicon"/>'s own. Given a token with no lowercase letter
/// it also runs a wider "shouted word" stemmer (<c>NONINFRINGEMENT</c> →
/// <c>infringement</c>) that is not part of the learning contract; a lowercase
/// part never reaches it.
/// </para>
/// <para>
/// <see cref="VocabularyLexicon"/> fails OPEN when its resource cannot be read
/// (an empty list, every word uncommon). An empty list here is
/// <see cref="IsAvailable"/> false, which the learning rules turn into fail
/// closed.
/// </para>
/// </remarks>
public sealed class WebsterLexicon : LearningLexicon
{
    private static readonly Lazy<WebsterLexicon> SharedLexicon =
        new(() => new WebsterLexicon(VocabularyLexicon.SharedForLearning), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly VocabularyLexicon _words;

    public WebsterLexicon(VocabularyLexicon words)
    {
        _words = words ?? throw new ArgumentNullException(nameof(words));
    }

    /// <summary>Over the embedded <c>web2</c> list, read once on first use.</summary>
    public static WebsterLexicon Shared => SharedLexicon.Value;

    /// <inheritdoc />
    public override bool IsAvailable => _words.WordCount > 0;

    /// <inheritdoc />
    protected override bool IsBaseWord(string part) => _words.IsCommonWord(part);

    /// <inheritdoc />
    protected override bool IsExcludedName(string part) => _words.IsLearningExcludedName(part);
}
