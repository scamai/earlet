using System.Globalization;
using System.Text;

namespace Dictation.Services;

/// <summary>
/// Which trailing token the caption shows while the recogniser is still
/// mid-word on it.
/// </summary>
/// <remarks>
/// See <see cref="DictationPreviewCaption.DefaultPartialWordPolicy"/> for the
/// argument. The short version: this cannot be answered from the string
/// alone, because <see cref="DictationPreviewAccumulator"/> trims every reply
/// before handing it back, so the one signal that would prove a token is
/// finished — a trailing space the recogniser has moved past — is gone by the
/// time this class ever sees the text. Both members below are honest about
/// that; neither is a detector in disguise.
/// </remarks>
public enum DictationCaptionPartialWordPolicy
{
    /// <summary>
    /// Always show the trailing token, including while it is still growing.
    /// </summary>
    IncludeTrailingFragment,

    /// <summary>
    /// Never show the trailing token. It only enters the caption once a
    /// further token starts after it — at which point it is no longer
    /// trailing, and something newer has taken its place as the one being
    /// withheld.
    /// </summary>
    HoldUntilWordBoundary,
}

/// <summary>
/// Windows the live dictation preview down to the few trailing words the HUD
/// pill has room for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate type from the accumulator.</b>
/// <see cref="DictationPreviewAccumulator"/> answers "what has the recogniser
/// said so far", and that string is allowed to be a whole paragraph — it only
/// ever grows for the length of a hold. The pill cannot draw a paragraph: the
/// owner now asks for a denser seven-word ticker, and the indicator window
/// (<c>DictationHudWindow</c>) draws whatever this hands it in the pill's title
/// place, cut at its start to the width the pill already has. Two different jobs — accumulate everything, then decide what of it is
/// worth putting on screen this instant — and this class is only the second
/// one. It never mutates or remembers anything; call it again on the next
/// accumulated string and it re-derives the window from scratch.
/// </para>
/// <para>
/// <b>CJK gets its own budget, not a word count.</b> "Last 5 words" has no
/// meaning for <c>我觉得没问题</c> — there are no spaces to count words with,
/// so a naive whitespace split returns either the whole string (one "word")
/// or nothing, depending on which way the bug happens to fall. Chinese and
/// English dictation are both in scope (<c>Dictation.Core</c>'s
/// <c>SpeechLanguage</c>), so this class tracks a SEPARATE budget for CJK
/// content, measured in characters: see
/// <see cref="DefaultCjkCharacterBudget"/> for where the number comes from.
/// Mixed text (<c>我觉得 QNN 没问题</c>) is handled by
/// treating each whitespace- or script-delimited run as its own segment, CJK
/// or not, and walking the trailing segments backward until EITHER budget
/// would be exceeded for the type it governs — so a caption can legitimately
/// end up with, say, 5 trailing English words plus a short trailing Chinese
/// clause, rather than forcing one axis to eat the other.
/// </para>
/// <para>
/// <b>The window is always a contiguous trailing slice.</b> The walk stops
/// the FIRST time it cannot add the next segment back — it never skips an
/// over-budget segment to keep reaching further back for a cheaper one. A
/// caption with a hole in the middle ("...world QNN 没问题" missing "hello")
/// would read as more broken than a caption that is simply shorter.
/// </para>
/// <para>
/// <b>Whole words, never whole words cut in half.</b> A non-CJK segment is
/// all-or-nothing: either the full word fits in the remaining word budget, or
/// the window stops before it. Slicing "truly" down to "tru" to save space
/// would put a different, wrong-looking word on screen, which is worse than
/// showing one fewer word. A CJK segment does not carry that constraint —
/// there is no "whole word" to preserve — so it may be cut at any character
/// boundary the remaining budget lands on, always keeping the TRAILING
/// characters (the ones closest to what was just said) and always cutting at
/// a <see cref="StringInfo"/> text-element boundary, never mid-cluster.
/// </para>
/// <para>
/// Pure and synchronous, like <see cref="DictationPreviewAccumulator"/>: no
/// field, no worker, nothing that outlives one call. Never throws — a caption
/// window is cosmetic, and a HUD that crashed the capture thread over a
/// malformed hypothesis string would be a worse defect than a wrong caption.
/// </para>
/// </remarks>
public static class DictationPreviewCaption
{
    /// <summary>
    /// The trailing word count for Latin/space-delimited text: 7.
    /// </summary>
    /// <remarks>
    /// Owner revision 2026-08-23: the smaller 13.5px ticker may carry more
    /// cadence context while it scrolls inside the same clipped viewport.
    /// </remarks>
    public const int DefaultWordWindow = 7;

    /// <summary>
    /// The trailing character count for CJK text: 16.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calibrated to the same denser rolling ticker as the seven-word Latin
    /// window. It may exceed the viewport because the viewport now scrolls;
    /// the budget bounds disclosure and keeps motion legible.
    /// </para>
    /// <code>
    ///   7 words  x 5 chars/word   = 35 Latin-character-widths
    ///     (the "5 characters (incl. the following space) = 1 word" count is
    ///      the standard typing-speed convention, not a measurement of this
    ///      corpus's actual average word length — it is used here only
    ///      because it is the same convention "words per minute" already
    ///      commits this product's vocabulary to elsewhere)
    ///   1 CJK glyph  ~= 2x the advance width of a Latin lowercase letter
    ///      (true of the fonts this HUD ships with: Segoe UI's CJK fallback,
    ///      like almost every CJK face, draws full-width — one em per glyph
    ///      against roughly half an em for a Latin lowercase letter)
    ///   budget = 35 Latin-character-widths / 2 = 17.5; use 16 so the ticker
    ///      has a little breathing room at the trailing edge
    /// </code>
    /// <para>
    /// This is a real number to revisit against an actual rendered caption
    /// box once one exists — it is a deliberate estimate, not a pixel
    /// measurement, and is named here specifically so it can be replaced with
    /// one edit if a real measurement disagrees with it.
    /// </para>
    /// </remarks>
    public const int DefaultCjkCharacterBudget = 16;

    /// <summary>
    /// Which trailing token the caption shows while it may still be growing:
    /// always include it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The honest alternative is <see cref="DictationCaptionPartialWordPolicy.HoldUntilWordBoundary"/>
    /// — never show the trailing token, so it only appears once a later one
    /// has started after it. That reads as more polished (nothing on screen
    /// ever un-types a letter) but has a cost this feature cannot absorb:
    /// <see cref="DictationPreviewSession"/> exists so the user has "something
    /// to look at while [the accurate transcript] is still impossible", and
    /// holding the trailing token means the caption is EMPTY for the entire
    /// first word of every hold, and empty again for the first word after
    /// every endpoint. A pill that is blank through most of a short dictation
    /// — the common case, per the accumulator's own corpus, where holds run
    /// 6–28 s and are mostly one or two segments — fails the job harder than
    /// one trailing token occasionally growing letter by letter.
    /// </para>
    /// <para>
    /// Held here as a real, flippable choice rather than a hard-coded branch:
    /// pass <see cref="DictationCaptionPartialWordPolicy.HoldUntilWordBoundary"/>
    /// to <see cref="Window"/> to get the steadier, one-token-behind caption
    /// instead, with no code change beyond the call site.
    /// </para>
    /// </remarks>
    public const DictationCaptionPartialWordPolicy DefaultPartialWordPolicy =
        DictationCaptionPartialWordPolicy.IncludeTrailingFragment;

    /// <summary>
    /// Window the accumulator's full text down to the trailing slice the pill
    /// should draw.
    /// </summary>
    /// <param name="text">
    /// The accumulator's <c>Text</c> (or any other string) — full, possibly
    /// paragraph-length. <c>null</c> or empty returns empty.
    /// </param>
    /// <param name="wordWindow">
    /// Max trailing whole words to keep from non-CJK stretches of the text.
    /// See <see cref="DefaultWordWindow"/>. Non-positive values are treated as
    /// the default rather than producing an empty or unbounded window.
    /// </param>
    /// <param name="cjkCharacterBudget">
    /// Max trailing characters to keep from CJK stretches of the text. See
    /// <see cref="DefaultCjkCharacterBudget"/>. Non-positive values are
    /// treated as the default.
    /// </param>
    /// <param name="partialWordPolicy">
    /// See <see cref="DefaultPartialWordPolicy"/>.
    /// </param>
    /// <returns>
    /// The trailing window, with internal whitespace runs collapsed to a
    /// single space and no leading or trailing whitespace. Never null, never
    /// throws.
    /// </returns>
    public static string Window(
        string? text,
        int wordWindow = DefaultWordWindow,
        int cjkCharacterBudget = DefaultCjkCharacterBudget,
        DictationCaptionPartialWordPolicy partialWordPolicy = DefaultPartialWordPolicy)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // The compact preview is feedback, not the transcript of record. The
        // streaming model can emit a bare "You" from the first low-context
        // slice before it has enough speech to form a phrase. Leaving that
        // single pronoun parked in the island for the entire next decode gap
        // looks like a speaker label or a broken UI state. Hold only this
        // observed singleton; as soon as it grows into a real phrase ("you
        // can", "you are", ...), the normal rolling window displays it. The
        // final recognizer and inserted text are untouched.
        if (IsStandaloneYou(text))
        {
            return string.Empty;
        }

        // A caller passing 0 or a negative budget almost certainly means "I
        // did not configure this", not "show nothing" — an unbounded or
        // literally-empty caption from a bad argument would be a stranger
        // failure than silently falling back to the shipped default.
        if (wordWindow <= 0)
        {
            wordWindow = DefaultWordWindow;
        }

        if (cjkCharacterBudget <= 0)
        {
            cjkCharacterBudget = DefaultCjkCharacterBudget;
        }

        List<Segment> segments;
        try
        {
            segments = BuildSegments(text);
        }
        catch
        {
            // Never throws into the caller, full stop (see class remarks) —
            // a caption is cosmetic. Nothing in BuildSegments is known to
            // throw on any input, StringInfo included, but the rule is
            // "never", not "never as far as we have tested".
            return string.Empty;
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        if (partialWordPolicy == DictationCaptionPartialWordPolicy.HoldUntilWordBoundary)
        {
            // Drop the trailing segment unconditionally — see
            // DefaultPartialWordPolicy's remarks. There is no signal here
            // that tells us it is actually finished, so "hold" means "always
            // withhold whichever segment is currently last", which this
            // implements by simply not offering it to the walk below.
            segments.RemoveAt(segments.Count - 1);

            if (segments.Count == 0)
            {
                // The only segment so far was the one just withheld. Nothing
                // is "one word behind" yet because nothing has finished —
                // an empty caption is the correct, documented consequence,
                // not a bug.
                return string.Empty;
            }
        }

        // Walk the trailing segments backward, spending each type's own
        // budget, and stop the first time the next segment cannot be added —
        // never skip over one to reach a cheaper segment further back. See
        // the class remarks on contiguity.
        var selected = new List<(IReadOnlyList<string> Elements, bool SpaceBefore)>();
        var wordUnitsUsed = 0;
        var cjkCharsUsed = 0;

        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var segment = segments[i];

            if (segment.IsCjk)
            {
                var remaining = cjkCharacterBudget - cjkCharsUsed;
                if (remaining <= 0)
                {
                    break;
                }

                if (segment.Elements.Count <= remaining)
                {
                    selected.Add((segment.Elements, segment.SpaceBefore));
                    cjkCharsUsed += segment.Elements.Count;
                    continue;
                }

                // The run is longer than what is left of the budget: keep
                // only the TRAILING `remaining` characters (closest to what
                // was just said) and stop — this segment is now cut open, so
                // nothing earlier can be attached to it without leaving a
                // hole in the middle of the caption.
                var tail = segment.Elements
                    .Skip(segment.Elements.Count - remaining)
                    .ToArray();
                selected.Add((tail, segment.SpaceBefore));
                break;
            }

            // Whole words only — see the class remarks on why a word is
            // never itself sliced.
            if (wordUnitsUsed >= wordWindow)
            {
                break;
            }

            selected.Add((segment.Elements, segment.SpaceBefore));
            wordUnitsUsed++;
        }

        selected.Reverse();

        var builder = new StringBuilder();
        for (var i = 0; i < selected.Count; i++)
        {
            // No leading space ever, regardless of what the original text
            // had before this segment — it may not even be the same segment
            // any more (a truncated CJK run has no original "before" left).
            if (i > 0 && selected[i].SpaceBefore)
            {
                builder.Append(' ');
            }

            foreach (var element in selected[i].Elements)
            {
                builder.Append(element);
            }
        }

        return builder.ToString();
    }

    private static bool IsStandaloneYou(string text)
    {
        var token = text.Trim().Trim('.', ',', '!', '?', ';', ':', '…');
        return string.Equals(token, "you", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One maximal run of same-script, non-whitespace text: a Latin "word",
    /// or a run of consecutive CJK characters.
    /// </summary>
    /// <param name="Elements">
    /// The run's own text elements (<see cref="StringInfo"/> grapheme
    /// clusters), in order. Kept as a list rather than a joined string so a
    /// CJK run can be trimmed to its trailing characters without re-slicing a
    /// raw <c>string</c> and risking a cut through a surrogate pair or a
    /// combining mark.
    /// </param>
    /// <param name="IsCjk">Whether this run is CJK (character-budgeted) or not (word-budgeted).</param>
    /// <param name="SpaceBefore">
    /// Whether whitespace separated this run from the previous one in the
    /// SOURCE text. False for a run that is glued directly to the previous
    /// one with no whitespace between them (a script boundary with no space,
    /// e.g. "QNN没问题") — reconstructing a space there would print a space
    /// the speaker/recogniser never produced.
    /// </param>
    private readonly record struct Segment(IReadOnlyList<string> Elements, bool IsCjk, bool SpaceBefore);

    /// <summary>
    /// Split text into <see cref="Segment"/>s: whitespace runs collapse to
    /// segment boundaries and are dropped, and a script change (CJK &lt;-&gt;
    /// non-CJK) with no intervening whitespace is ALSO a boundary, so a glued
    /// mixed token like "QNN没问题" still separates into a word segment and a
    /// CJK segment rather than being misclassified as one or the other.
    /// </summary>
    /// <remarks>
    /// Walks <see cref="StringInfo.GetTextElementEnumerator(string)"/> one
    /// grapheme cluster at a time rather than one <c>char</c> — the only way
    /// to classify and later slice this text without a chance of cutting a
    /// surrogate pair (an emoji) or a combining-mark cluster (a base letter
    /// plus its accent) in half. See requirement 6 in the spec this class was
    /// built against.
    /// </remarks>
    private static List<Segment> BuildSegments(string text)
    {
        var segments = new List<Segment>();
        List<string>? current = null;
        var currentIsCjk = false;
        var currentSpaceBefore = false;
        var sawWhitespaceSinceLastSegment = false;

        void Flush()
        {
            if (current is not null)
            {
                segments.Add(new Segment(current, currentIsCjk, currentSpaceBefore));
                current = null;
            }
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (element.Length == 0)
            {
                // Not known to happen, but a zero-length element would silently
                // never advance anything downstream if it were ever added to a
                // run; skipping it costs nothing and keeps this unconditionally
                // safe.
                continue;
            }

            if (IsWhitespaceElement(element))
            {
                Flush();
                sawWhitespaceSinceLastSegment = true;
                continue;
            }

            var elementIsCjk = IsCjkElement(element);

            if (current is null)
            {
                current = new List<string>(4) { element };
                currentIsCjk = elementIsCjk;
                currentSpaceBefore = sawWhitespaceSinceLastSegment;
                sawWhitespaceSinceLastSegment = false;
            }
            else if (elementIsCjk == currentIsCjk)
            {
                current.Add(element);
            }
            else
            {
                // Script boundary, no whitespace between: end the run so far
                // and start the next one glued directly onto it.
                Flush();
                current = new List<string>(4) { element };
                currentIsCjk = elementIsCjk;
                currentSpaceBefore = false;
                sawWhitespaceSinceLastSegment = false;
            }
        }

        Flush();
        return segments;
    }

    /// <summary>
    /// Whether a text element is whitespace: tabs, newlines, ordinary spaces,
    /// and the CJK full-width space (U+3000) alike, so a CJK sentence typed
    /// with full-width punctuation splits on its own spacing the same way a
    /// Latin one splits on ASCII spaces.
    /// </summary>
    private static bool IsWhitespaceElement(string element) => Rune.IsWhiteSpace(FirstRune(element));

    /// <summary>
    /// Whether a text element's base character falls in a CJK Unicode block:
    /// Han ideographs (the common case — Chinese, this project's other
    /// supported language), Hiragana/Katakana, Hangul, CJK punctuation and
    /// full-width forms, and the supplementary-plane Han extensions. Broader
    /// than "Chinese" on purpose: a script boundary is still the right place
    /// to split even for a language Dictation does not yet decode, and a narrower
    /// check would silently mis-budget the day one is added.
    /// </summary>
    private static bool IsCjkElement(string element) => IsCjkScalar(FirstRune(element).Value);

    private static bool IsCjkScalar(int c) =>
        (c >= 0x2E80 && c <= 0x2EFF)    // CJK Radicals Supplement
        || (c >= 0x2F00 && c <= 0x2FDF) // Kangxi Radicals
        || (c >= 0x3000 && c <= 0x303F) // CJK Symbols and Punctuation (、。「」etc. — U+3000 itself is whitespace and is never reached here, IsWhitespaceElement is checked first)
        || (c >= 0x3040 && c <= 0x309F) // Hiragana
        || (c >= 0x30A0 && c <= 0x30FF) // Katakana
        || (c >= 0x3100 && c <= 0x312F) // Bopomofo
        || (c >= 0x3200 && c <= 0x33FF) // Enclosed CJK Letters/Months, CJK Compatibility
        || (c >= 0x3400 && c <= 0x4DBF) // CJK Unified Ideographs Extension A
        || (c >= 0x4E00 && c <= 0x9FFF) // CJK Unified Ideographs (the common Han block)
        || (c >= 0xAC00 && c <= 0xD7A3) // Hangul Syllables
        || (c >= 0xF900 && c <= 0xFAFF) // CJK Compatibility Ideographs
        || (c >= 0xFF00 && c <= 0xFFEF) // Halfwidth and Fullwidth Forms
        || (c >= 0x20000 && c <= 0x3FFFF); // CJK Unified Ideographs Extensions B and beyond (supplementary planes)

    /// <summary>
    /// The first Unicode scalar value of a text element, decoded defensively.
    /// </summary>
    /// <remarks>
    /// A text element from <see cref="StringInfo"/> is ordinarily one valid
    /// scalar (or a base character plus combining marks, in which case only
    /// the base matters for script classification). A lone surrogate — not
    /// something this recogniser is expected to emit, but not something this
    /// class is allowed to throw on either — is not a valid scalar; it is
    /// reported as <see cref="Rune.ReplacementChar"/>, which is neither
    /// whitespace nor CJK and so is simply treated as an ordinary word
    /// character rather than crashing the caption.
    /// </remarks>
    private static Rune FirstRune(string element) =>
        Rune.TryGetRuneAt(element, 0, out var rune) ? rune : Rune.ReplacementChar;
}
