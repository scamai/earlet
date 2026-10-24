using System.Globalization;
using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The spoken delete command: what matches, what must never match, and how
/// many keystrokes it takes to undo.
/// </summary>
/// <remarks>
/// <para>
/// Ported from macOS's <c>runDictationVoiceDeleteSelfCheck</c>, which runs
/// these same properties on every <c>--self-check</c> there. Every assertion
/// in this file is one a wrong answer turns into deleted user text inside an
/// application Dictation does not own, which is why the near-miss half is longer
/// than the match half.
/// </para>
/// <para>
/// The grapheme block is the load-bearing one. Swift's <c>String.count</c> is
/// a cluster count for free, so macOS's version of this feature never had to
/// state the property; C#'s <c>string.Length</c> is UTF-16 code units and a
/// transliterated port silently overruns. These cases exist so that edit
/// fails.
/// </para>
/// </remarks>
public sealed class DictationDeleteCommandTests
{
    // ── The phrases, as people actually say them ─────────────────────────────

    [Theory]
    // en, and the four forms ASR realistically hands over: bare, sentence
    // case with a full stop, shouted with surrounding whitespace, and with a
    // comma Whisper inserted from the prosody.
    [InlineData("scratch that")]
    [InlineData("Scratch that.")]
    [InlineData("  SCRATCH THAT!  ")]
    [InlineData("Scratch, that")]
    [InlineData("delete that")]
    [InlineData("Delete that?")]
    [InlineData("scratch that line")]
    [InlineData("Scratch that line.")]
    [InlineData("delete that line")]
    [InlineData("DELETE THAT LINE")]
    // zh. The trailing "。" must be stripped exactly like ".", and the leading
    // space is real — Whisper emits zh with and without one depending on the
    // preceding context.
    [InlineData("删掉")]
    [InlineData("删掉。")]
    [InlineData(" 删除 ")]
    [InlineData("删除，")]
    [InlineData("删掉那句")]
    [InlineData("删掉那句。")]
    [InlineData("删掉这句")]
    public void A_bare_delete_phrase_is_a_command(string utterance)
    {
        Assert.True(DictationDeleteCommand.IsDeleteCommand(utterance));
    }

    // ── The half that matters more ───────────────────────────────────────────

    [Theory]
    // One extra word on EITHER side and the utterance is content, not a
    // command. Each of these is a sentence a person genuinely dictates;
    // matching any of them eats the previous line instead of typing this one.
    [InlineData("scratch that whole paragraph")]
    [InlineData("let's scratch that idea")]
    [InlineData("delete that file from the repo")]
    [InlineData("please delete that")]
    [InlineData("scratch that line about the budget")]
    [InlineData("删掉这个文件")]
    [InlineData("把它删掉")]
    // The words macOS deliberately leaves out of the set, and the reason is
    // the same on Windows: every one of them is something a person says
    // mid-sentence, at their own typing, while dictating something else.
    [InlineData("undo")]
    [InlineData("no no no")]
    [InlineData("oops")]
    [InlineData("cancel")]
    // Fragments. "scratch" alone is a word about scratching; "that" alone is
    // half of everything.
    [InlineData("scratch")]
    [InlineData("delete")]
    [InlineData("that")]
    [InlineData("删")]
    // Nothing at all. An empty or whitespace-only utterance normalises to the
    // empty string, which must not be in the set.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    // Punctuation only — normalises to empty by the same route.
    [InlineData("...")]
    [InlineData("。")]
    public void Anything_that_is_not_exactly_a_phrase_is_not_a_command(string utterance)
    {
        Assert.False(DictationDeleteCommand.IsDeleteCommand(utterance));
    }

    [Fact]
    public void Null_is_not_a_command()
    {
        // A transcript that came back null is the shape of a failed
        // recognition, and a failed recognition must not delete anything.
        Assert.False(DictationDeleteCommand.IsDeleteCommand(null));
        Assert.Equal(string.Empty, DictationDeleteCommand.Normalize(null));
    }

    // ── Normalisation ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Hello, World! 123", "helloworld123")]
    [InlineData("  Scratch  That.  ", "scratchthat")]
    [InlineData("删掉。", "删掉")]
    [InlineData("删除，", "删除")]
    [InlineData("", "")]
    [InlineData("!!!", "")]
    public void Normalize_lowercases_and_keeps_only_letters_and_digits(string input, string expected)
    {
        Assert.Equal(expected, DictationDeleteCommand.Normalize(input));
    }

    [Fact]
    public void Normalize_keeps_digits_rather_than_treating_them_as_punctuation()
    {
        // Non-vacuity for the rule above: a "letters only" implementation
        // passes every other case in this file, because no phrase has a digit
        // in it. This is the only test that would notice.
        Assert.Equal("abc123", DictationDeleteCommand.Normalize("abc-123"));
    }

    // ── Settings and the matcher cannot drift ────────────────────────────────

    [Fact]
    public void Every_phrase_Settings_shows_is_a_phrase_the_matcher_accepts()
    {
        // A Settings pane advertising a phrase the matcher rejects is a
        // feature the user believes in and cannot use, and it fails silently:
        // they say the words and nothing happens.
        foreach (var shown in DictationDeleteCommand.DisplayPhrases)
        {
            Assert.True(
                DictationDeleteCommand.IsDeleteCommand(shown),
                $"Settings shows \"{shown}\" but the matcher would not accept it.");
        }
    }

    [Fact]
    public void The_display_list_and_the_match_set_are_the_same_length()
    {
        // The other direction of the pin above. Every display phrase matching
        // is satisfied by a display list of one; this is what catches a phrase
        // added to the matcher and never shown to anybody.
        Assert.Equal(
            DictationDeleteCommand.Phrases.Count,
            DictationDeleteCommand.DisplayPhrases.Length);
    }

    [Fact]
    public void Eight_phrases_ship_four_English_and_four_Chinese()
    {
        // Non-vacuity for both pins: two empty lists agree perfectly. macOS
        // ships eight and Windows offers en, zh and auto, so all eight come
        // across.
        Assert.Equal(8, DictationDeleteCommand.Phrases.Count);
        Assert.Equal(8, DictationDeleteCommand.DisplayPhrases.Length);
    }

    [Fact]
    public void Every_entry_in_the_match_set_is_already_normalised()
    {
        // The set is compared against Normalize's OUTPUT, so an entry with a
        // space or a capital in it can never be reached — a dead phrase that
        // reads as a live one. This is the only thing that would say so.
        foreach (var phrase in DictationDeleteCommand.Phrases)
        {
            Assert.Equal(phrase, DictationDeleteCommand.Normalize(phrase));
        }
    }

    // ── Counting keystrokes: grapheme clusters, never string.Length ──────────

    [Theory]
    [InlineData("hello", 5)]
    // 4 clusters, 12 UTF-8 bytes. A count in BYTES — the other tempting
    // wrong answer, and the one a caller reaching for Encoding.UTF8 lands on
    // — says 12 and eats eight characters of the user's own text after ours.
    [InlineData("你好世界", 4)]
    // 1 cluster, 2 UTF-16 code units (one surrogate pair).
    [InlineData("\U0001F642", 1)]
    // A ZWJ family sequence: 1 cluster, 5 scalars, 8 UTF-16 code units. The
    // joiners are written as escapes rather than pasted in: a zero-width
    // joiner is invisible in an editor, and one lost to a copy/paste turns
    // this case into three separate emoji that legitimately count 3.
    [InlineData("\U0001F469\u200D\U0001F469\u200D\U0001F467", 1)]
    // Precomposed and decomposed forms of the same word must agree. The
    // decomposed one is 5 scalars and 4 clusters, and it is what a
    // macOS-originated path or an NFD-normalising editor hands over.
    [InlineData("café", 4)]
    [InlineData("cafe\u0301", 4)]
    // A newline is a keystroke like any other; the insertion put it there.
    [InlineData("a\nb", 3)]
    [InlineData("", 0)]
    public void BackspaceCount_counts_grapheme_clusters(string text, int expected)
    {
        Assert.Equal(expected, DictationDeleteCommand.BackspaceCount(text));
    }

    [Fact]
    public void BackspaceCount_is_not_string_Length()
    {
        // Stated as its own assertion rather than left implicit in the table
        // above, because "optimise this to .Length" is the specific edit this
        // whole function exists to stop.
        //
        // Note WHICH inputs carry it. The four-ideograph Chinese row in the
        // table above is four UTF-16 units as well as four clusters — every
        // one of those ideographs is a single BMP char — so a .Length
        // implementation passes that row untouched. Only text outside the BMP
        // separates the two counts, which is why the emoji rows are in the
        // table and why they are the ones repeated here.
        const string smiley = "\U0001F642";
        const string zwj = "\U0001F469\u200D\U0001F469\u200D\U0001F467";

        Assert.Equal(2, smiley.Length);
        Assert.Equal(8, zwj.Length);
        Assert.NotEqual(smiley.Length, DictationDeleteCommand.BackspaceCount(smiley));
        Assert.NotEqual(zwj.Length, DictationDeleteCommand.BackspaceCount(zwj));

        // And it IS the documented mechanism, not a coincidence of some other
        // count that happens to agree on these two.
        Assert.Equal(
            new StringInfo(zwj).LengthInTextElements,
            DictationDeleteCommand.BackspaceCount(zwj));
    }

    // ── The ceiling ──────────────────────────────────────────────────────────

    [Fact]
    public void The_ceiling_is_two_thousand_keystrokes()
    {
        // Pinned at the number macOS ships. It is far more than anyone says in
        // one push-to-talk press, and it is what stops a corrupted record
        // becoming thousands of synthesised keystrokes in a live application.
        Assert.Equal(2000, DictationDeleteCommand.MaxBackspaces);
    }

    [Fact]
    public void A_record_at_the_ceiling_still_counts_exactly()
    {
        var text = new string('x', DictationDeleteCommand.MaxBackspaces);

        Assert.Equal(DictationDeleteCommand.MaxBackspaces, DictationDeleteCommand.BackspaceCount(text));
    }
}
