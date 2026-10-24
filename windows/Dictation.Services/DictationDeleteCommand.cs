using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Dictation.Services;

/// <summary>
/// “Scratch that” — the spoken command that removes what Dictation just typed.
/// </summary>
/// <remarks>
/// <para>
/// macOS's <c>DictationDeleteCommand</c>, ported. Windows offers en, zh and
/// auto as dictation languages, so all eight phrases come across rather than
/// the English four.
/// </para>
/// <para>
/// <b>Why this is a hand-written set and an exact match, not a classifier.</b>
/// The cost function is one-sided to the point of not being a trade at all. A
/// MISSED command costs the user four typed words they delete by hand —
/// annoying, over in one keystroke. A FALSE command silently backspaces over
/// text the user meant to keep, in an application Dictation does not own, with no
/// undo stack of its own to appeal to. So the recogniser is deliberately the
/// least clever thing that can work: normalise the utterance, compare it to a
/// short list, require the WHOLE utterance to be nothing but the command.
/// </para>
/// <para>
/// That is why <c>“scratch that whole paragraph”</c> and <c>“let's scratch
/// that idea”</c> are text and not commands. Any extra word on either side and
/// the utterance is content. No regex, no stemming, no synonym expansion, no
/// edit distance — every one of those widens the false-positive surface, which
/// is the one thing this type must not do.
/// </para>
/// <para>
/// It is also what makes the feature explainable in one line in Settings,
/// which renders <see cref="DisplayPhrases"/> read-only rather than a
/// hand-retyped string the code cannot check.
/// </para>
/// </remarks>
public static class DictationDeleteCommand
{
    /// <summary>
    /// The exact set, post-<see cref="Normalize(string?)"/>. <b>Keep it
    /// SHORT</b>: every entry is a phrase the user can no longer dictate as
    /// literal text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The English pair is what every dictation product on the market already
    /// trains people on — “scratch that” is Dragon's, “delete that” is
    /// Apple's — so they cost no new muscle memory. The Chinese entries are
    /// the direct equivalents, and they are here rather than deferred because
    /// Windows ships a zh dictation language and Whisper's zh output for a
    /// two-character command is stable.
    /// </para>
    /// <para>
    /// <b>NOT included, on purpose: “undo”, “no no no”, “oops”, “cancel”.</b>
    /// macOS excludes them and so does this, for the same reason: each is
    /// something a person says mid-sentence, as content, while dictating. A
    /// set that swallowed “oops” would delete the previous line every time
    /// somebody muttered at their own typing.
    /// </para>
    /// <para>
    /// Written out by hand rather than derived from
    /// <see cref="DisplayPhrases"/> by normalising it. Deriving would make the
    /// two lists agree by construction and would also make a typo in the
    /// Settings copy silently change what Dictation matches. Two lists, pinned
    /// against each other by <c>DictationDeleteCommandTests</c>, means a typo
    /// is a red test.
    /// </para>
    /// </remarks>
    public static ImmutableHashSet<string> Phrases { get; } = ImmutableHashSet.Create(
        // Ordinal, and it costs nothing to say so: Normalize has already
        // lowercased, and a case-insensitive comparer over CJK is a question
        // with no meaning.
        StringComparer.Ordinal,
        // en
        "scratchthat",
        "deletethat",
        "scratchthatline",
        "deletethatline",
        // zh
        "删掉",
        "删除",
        "删掉那句",
        "删掉这句");

    /// <summary>
    /// The same phrases as a person says them — what Settings renders.
    /// </summary>
    /// <remarks>
    /// Settings must show what Dictation will actually match, so this list is
    /// pinned against <see cref="Phrases"/> by tests: same count, and every
    /// entry here normalises back into that set. A Settings pane advertising a
    /// phrase the matcher rejects is a feature the user believes in and cannot
    /// use, and it fails silently — they say the words and nothing happens.
    /// </remarks>
    public static ImmutableArray<string> DisplayPhrases { get; } =
    [
        "scratch that",
        "delete that",
        "scratch that line",
        "delete that line",
        "删掉",
        "删除",
        "删掉那句",
        "删掉这句",
    ];

    /// <summary>
    /// Lowercase, then keep only letters and digits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This absorbs the three things ASR reliably varies on for a short
    /// utterance and nothing else: trailing punctuation (“Scratch that.” /
    /// “Scratch that!”), casing, and spacing — Whisper emits Chinese with and
    /// without a leading space depending on what came before it.
    /// </para>
    /// <para>
    /// Unicode-aware by construction: <see cref="char.IsLetter(char)"/> and
    /// <see cref="char.IsDigit(char)"/>, not an ASCII range, so CJK survives
    /// and CJK punctuation (“。” “，”) is stripped exactly like “.” and “,”.
    /// <see cref="string.ToLowerInvariant"/> rather than
    /// <c>ToLower()</c>: a culture-sensitive lowercase makes the phrase list
    /// mean something slightly different on a Turkish machine, and a matcher
    /// whose answer depends on the user's regional settings is not one anybody
    /// can reason about.
    /// </para>
    /// <para>
    /// <b>Known edge, and it is the safe direction.</b> A letter outside the
    /// Basic Multilingual Plane arrives as a surrogate pair, and
    /// <c>char.IsLetter</c> is false for each half, so the pair is dropped
    /// like punctuation rather than kept. No phrase in <see cref="Phrases"/>
    /// lives outside the BMP, so this can only ever absorb a character that
    /// was never going to help a match — the same thing it does to a full
    /// stop.
    /// </para>
    /// </remarks>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lowered = text.ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);

        foreach (var character in lowered)
        {
            if (char.IsLetter(character) || char.IsDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The whole test: is this utterance nothing but a delete phrase?
    /// </summary>
    /// <remarks>
    /// Exact set membership on the normalised WHOLE utterance — never
    /// <c>Contains</c>, never a prefix or suffix test. “delete that file from
    /// the repo” contains a phrase and is plainly a sentence about a file;
    /// anything looser than equality deletes the previous line instead of
    /// typing this one.
    /// </remarks>
    public static bool IsDeleteCommand(string? text) => Phrases.Contains(Normalize(text));

    /// <summary>
    /// Hard ceiling on one undo, in keystrokes.
    /// </summary>
    /// <remarks>
    /// A dictation is one utterance, and 2000 characters is far more than
    /// anyone says in a single push-to-talk press. The bound exists so a
    /// corrupted or absurd insertion record can never become thousands of
    /// synthesised keystrokes hammering an application the user is in the
    /// middle of using. Same number macOS ships.
    /// </remarks>
    public const int MaxBackspaces = 2000;

    /// <summary>
    /// How many backspaces it takes to remove <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Grapheme clusters — NOT <see cref="string.Length"/>.</b> This is the
    /// single line in this file most likely to be “optimised” into a defect.
    /// <c>string.Length</c> is UTF-16 code units, so it says 2 for “🙂” and 8
    /// for “你好世界”; a text field deletes one CLUSTER per backspace, so a
    /// count in code units overruns into text the user wrote by hand, in
    /// somebody else's document. macOS gets this free because Swift's
    /// <c>String.count</c> already is a cluster count, which is exactly why a
    /// naive port of that one expression is wrong on Windows.
    /// </para>
    /// <para>
    /// <see cref="StringInfo.LengthInTextElements"/> is the
    /// <c>TextElementEnumerator</c> walk in one property, and since .NET 5 it
    /// is UAX #29 grapheme segmentation — so a ZWJ emoji sequence counts once
    /// and a decomposed accent counts as its base letter.
    /// </para>
    /// <para>
    /// Spelled out as its own named function, with its cases pinned in tests,
    /// precisely so that a later edit to <c>.Length</c> at the call site
    /// cannot ship unnoticed.
    /// </para>
    /// <para>
    /// The count is honest about being a count: a backspace does not consume
    /// exactly one cluster everywhere. Terminals and terminal-shaped inputs
    /// delete by cluster and are the target case; some rich editors peel one
    /// code point at a time from a ZWJ sequence, and an editor that
    /// autocorrected after the insertion is no longer described by any count.
    /// The mitigation is not a cleverer number — it is that the removed text
    /// stays in dictation history and can be restored.
    /// </para>
    /// </remarks>
    public static int BackspaceCount(string text)
    {
        // Nullable-annotated as non-null, but this decides how many keystrokes
        // go into a stranger's document, so it does not throw on a caller's
        // bad day. Zero is also the honest answer, and DictationUndoPolicy
        // refuses on it.
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return new StringInfo(text).LengthInTextElements;
    }
}
