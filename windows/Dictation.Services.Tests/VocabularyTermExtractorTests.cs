using Dictation.Services;

namespace Dictation.Services.Tests;

/// <summary>
/// The tokeniser and the four-way classification — the half of the import that
/// decides what counts as a word at all.
/// </summary>
public class VocabularyTermExtractorTests
{
    /// <summary>
    /// No dictionary, so these tests assert the SCANNER's rules rather than
    /// web2's contents. The sentence-initial rule needs one, and says so.
    /// </summary>
    private static readonly IVocabularyLexicon NoDictionary = VocabularyLexicon.Empty;

    private static List<string> TermsIn(string text, IVocabularyLexicon? lexicon = null) =>
        [.. VocabularyTermExtractor.Extract(text, lexicon ?? NoDictionary).Select(t => t.Text)];

    private static VocabularyTermKind? KindOf(string text, IVocabularyLexicon? lexicon = null) =>
        VocabularyTermExtractor.Extract(text, lexicon ?? NoDictionary)
            .FirstOrDefault(t => t.Text == text.Trim())?.Kind;

    // ─── Tokenising ─────────────────────────────────────────────────────

    [Fact]
    public void A_dot_does_not_split_a_token_so_a_domain_survives_whole()
    {
        Assert.Contains("Cove.ai", TermsIn("we host it at Cove.ai today"));
    }

    [Fact]
    public void A_hyphen_does_not_split_a_token()
    {
        Assert.Contains("Fable-Flow", TermsIn("we tried Fable-Flow last week"));
    }

    [Fact]
    public void An_underscore_and_digits_do_not_split_a_token()
    {
        Assert.Contains("MAX_SIZE", TermsIn("the MAX_SIZE constant"));
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("a;b")]
    [InlineData("a:b")]
    [InlineData("a(b")]
    [InlineData("a[b")]
    [InlineData("a{b")]
    [InlineData("a<b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a|b")]
    [InlineData("a@b")]
    [InlineData("a#b")]
    [InlineData("a&b")]
    [InlineData("a~b")]
    [InlineData("a`b")]
    [InlineData("a“b")]
    public void Every_separator_ends_a_token(string joined)
    {
        var text = joined.Replace("a", "Alpha").Replace("b", "Bravo");

        Assert.Equal(["Alpha", "Bravo"], TermsIn(text));
    }

    [Fact]
    public void A_trailing_full_stop_is_trimmed()
    {
        Assert.Contains("Trovioo", TermsIn("we called Trovioo."));
    }

    [Fact]
    public void A_trailing_full_stop_is_not_trimmed_past_a_domain()
    {
        Assert.Contains("Cove.ai", TermsIn("it lives at Cove.ai."));
    }

    [Fact]
    public void A_highlight_range_measures_the_trimmed_token()
    {
        var term = VocabularyTermExtractor
            .Extract("we called Trovioo.", NoDictionary)
            .Single(t => t.Text == "Trovioo");

        var range = Assert.Single(term.Ranges);
        Assert.Equal(10, range.Start);
        Assert.Equal(7, range.Length);
        Assert.Equal("Trovioo", "we called Trovioo.".Substring(range.Start, range.Length));
    }

    [Fact]
    public void Every_occurrence_keeps_its_own_range_in_document_order()
    {
        var term = VocabularyTermExtractor
            .Extract("Trovioo and then Trovioo again", NoDictionary)
            .Single(t => t.Text == "Trovioo");

        Assert.Equal(2, term.Occurrences);
        Assert.Equal([0, 17], term.Ranges.Select(r => r.Start));
    }

    // ─── Lengths ────────────────────────────────────────────────────────

    [Fact]
    public void A_token_shorter_than_three_characters_is_not_a_candidate()
    {
        Assert.DoesNotContain("Hi", TermsIn("Hi Zupong"));
        Assert.Contains("Zupong", TermsIn("Hi Zupong"));
    }

    [Fact]
    public void A_token_longer_than_the_stores_phrase_limit_is_not_offered()
    {
        // macOS has no maximum at all. Here the destination silently DROPS an
        // over-length line on the next parse, so offering one would mean
        // saying "Added 12 words" and writing 11.
        var tooLong = "Aa" + new string('b', DictationVocabulary.MaxPhraseLength);
        var justFits = "Aa" + new string('b', DictationVocabulary.MaxPhraseLength - 2);

        Assert.DoesNotContain(tooLong, TermsIn($"we use {tooLong} here"));
        Assert.Contains(justFits, TermsIn($"we use {justFits} here"));
    }

    // ─── The four kinds ─────────────────────────────────────────────────

    [Fact]
    public void A_host_name_is_a_domain()
    {
        Assert.Equal(VocabularyTermKind.Domain, KindOf("Cove.ai"));
        Assert.Equal(VocabularyTermKind.Domain, KindOf("mail.google.com"));
    }

    [Theory]
    [InlineData("1.2.3")]      // First part is not a letter.
    [InlineData("a.b")]        // Last part is one character.
    [InlineData("Alpha.")]     // Trimmed to a bare word.
    public void Something_that_is_not_a_host_name_is_not_a_domain(string text)
    {
        Assert.NotEqual(VocabularyTermKind.Domain, KindOf(text));
    }

    [Fact]
    public void A_domain_needs_a_lowercase_last_part()
    {
        // One character tighter than the macOS rule, added on measurement: a
        // scan of this project offered "ArgumentNullException.ThrowIfNull",
        // "string.Empty" and "StringComparison.OrdinalIgnoreCase" in its top
        // ten, all of them DOMAINS under the ported rule and therefore
        // unconditionally distinctive. A top-level domain is lowercase.
        Assert.Equal(VocabularyTermKind.Domain, KindOf("Cove.ai"));
        Assert.NotEqual(VocabularyTermKind.Domain, KindOf("Dictation.Services"));

        // And the cost, stated rather than hidden: a capitalised spelling is
        // not a domain, and the dotted-token rule then discards it instead of
        // letting it through as camel-case. Every real top-level domain in a
        // document is lowercase, and this trade buys back the entire top of a
        // code scan.
        Assert.Empty(TermsIn("we host it at Cove.AI today"));
    }

    [Theory]
    [InlineData("ArgumentNullException.ThrowIfNull")]
    [InlineData("StringComparison.OrdinalIgnoreCase")]
    [InlineData("DictationState.Idle")]
    [InlineData("Dictation.Services")]
    [InlineData("end.Next")]
    public void A_member_access_chain_is_not_a_term_at_all(string token)
    {
        // The second measured departure, and it follows from the first. The
        // tokeniser keeps '.' inside a token for exactly one reason — so
        // "Cove.ai" survives whole — and macOS then lets a non-domain dotted
        // token fall through to the camel test, which it passes. Nothing here
        // is a thing anybody says out loud, and each one would spend one of the
        // vocabulary's 64 slots.
        Assert.Empty(TermsIn($"we call {token} there"));
    }

    [Fact]
    public void A_real_domain_still_survives_beside_the_chains()
    {
        var terms = TermsIn("push Dictation.Services to Cove.ai today");

        Assert.Equal(["Cove.ai"], terms);
    }

    [Fact]
    public void A_short_shouted_token_is_an_acronym()
    {
        Assert.Equal(VocabularyTermKind.Acronym, KindOf("ONNX"));
        Assert.Equal(VocabularyTermKind.Acronym, KindOf("QNN2"));
    }

    [Fact]
    public void A_long_shouted_token_is_camel_rather_than_an_acronym()
    {
        // Seven characters is past the acronym ceiling, and the camel test
        // fires before the letters-only guard — which is the only reason
        // MAX_SIZE classifies at all.
        Assert.Equal(VocabularyTermKind.Camel, KindOf("MAX_SIZE"));
        Assert.Equal(VocabularyTermKind.Camel, KindOf("LONGACR"));
    }

    [Fact]
    public void An_internal_capital_is_camel()
    {
        Assert.Equal(VocabularyTermKind.Camel, KindOf("ArcFace"));
    }

    [Fact]
    public void An_ordinary_capitalised_word_is_proper()
    {
        Assert.Equal(VocabularyTermKind.Proper, KindOf("Trovioo"));

        // A hyphen is allowed inside a proper noun. A second CAPITAL is not
        // what makes it proper — "Zhang-Wei" has an internal capital and
        // classifies as camel, one test down.
        Assert.Equal(VocabularyTermKind.Proper, KindOf("Zhang-wei"));
        Assert.Equal(VocabularyTermKind.Camel, KindOf("Zhang-Wei"));
    }

    [Fact]
    public void A_lowercase_word_is_not_a_candidate_at_all()
    {
        Assert.Empty(TermsIn("this is all ordinary lowercase prose"));
    }

    [Fact]
    public void A_stoplisted_word_is_never_a_proper_noun()
    {
        Assert.Empty(TermsIn("we said Thanks and Regards on Monday in December"));
    }

    // ─── Sentence starts ────────────────────────────────────────────────

    [Fact]
    public void A_capitalised_ordinary_word_at_the_start_of_a_sentence_is_dropped()
    {
        var lexicon = VocabularyLexicon.Of(["nothing"]);

        Assert.DoesNotContain("Nothing", TermsIn("Nothing broke today.", lexicon));
    }

    [Fact]
    public void The_same_word_mid_sentence_is_kept()
    {
        // Mid-sentence a capital IS the signal and no dictionary is consulted.
        var lexicon = VocabularyLexicon.Of(["nothing"]);

        Assert.Contains("Nothing", TermsIn("we found Nothing here", lexicon));
    }

    [Fact]
    public void A_newline_starts_a_sentence_the_way_a_full_stop_does()
    {
        // Without this, the first capitalised word of every paragraph,
        // greeting and signature became somebody's proper noun.
        var lexicon = VocabularyLexicon.Of(["nothing"]);

        Assert.DoesNotContain("Nothing", TermsIn("we shipped it\nNothing broke", lexicon));
    }

    [Fact]
    public void A_word_too_short_to_keep_still_closes_its_sentence()
    {
        var lexicon = VocabularyLexicon.Of(["nothing"]);

        Assert.DoesNotContain("Nothing", TermsIn("ok. Nothing broke", lexicon));
    }

    [Fact]
    public void With_no_dictionary_a_sentence_initial_capital_is_kept()
    {
        // The fail-open direction, seen from the extractor: an extra word in
        // the list is visible and one click from removal, a missing proper
        // noun is the feature not working.
        Assert.Contains("Nothing", TermsIn("Nothing broke today."));
    }

    // ─── Identity and order ─────────────────────────────────────────────

    [Fact]
    public void Casing_is_verbatim_and_two_spellings_are_two_terms()
    {
        var terms = TermsIn("Trovioo and TROVIOO are different here");

        Assert.Contains("Trovioo", terms);
        Assert.Contains("TROVIOO", terms);
    }

    [Fact]
    public void Terms_come_out_by_occurrence_descending()
    {
        var terms = TermsIn("Alpha Bravo Bravo Charlie Charlie Charlie");

        Assert.Equal(["Charlie", "Bravo", "Alpha"], terms);
    }

    [Fact]
    public void Equal_counts_are_broken_by_first_appearance_not_alphabetically()
    {
        // Alphabetical order inside a frequency list reads as meaning that is
        // not there.
        var terms = TermsIn("Zulu Alpha");

        Assert.Equal(["Zulu", "Alpha"], terms);
    }

    [Fact]
    public void The_first_appearance_fixes_the_kind()
    {
        var terms = VocabularyTermExtractor.Extract("ONNX text ONNX", NoDictionary);

        Assert.Equal(VocabularyTermKind.Acronym, terms.Single(t => t.Text == "ONNX").Kind);
    }

    // ─── Unicode ────────────────────────────────────────────────────────

    [Fact]
    public void A_non_ascii_name_is_a_proper_noun_like_any_other()
    {
        Assert.Contains("Zürich", TermsIn("we met in Zürich last spring"));
        Assert.Contains("Ångström", TermsIn("we cited Ångström twice"));
    }

    [Fact]
    public void A_non_ascii_name_keeps_its_exact_spelling()
    {
        var term = VocabularyTermExtractor
            .Extract("we met Søren once", NoDictionary)
            .Single();

        Assert.Equal("Søren", term.Text);
        Assert.Equal(VocabularyTermKind.Proper, term.Kind);
    }

    [Fact]
    public void A_non_bmp_letter_is_classified_as_one_character_not_two_halves()
    {
        // Deseret capital long I (U+10400) is a surrogate pair. Read as two
        // chars it is neither a letter nor a hyphen and the term dies; read as
        // a Rune it is an uppercase letter.
        var deseret = char.ConvertFromUtf32(0x10400) + char.ConvertFromUtf32(0x10428) +
                      char.ConvertFromUtf32(0x10429);

        var terms = TermsIn($"we greeted {deseret} warmly");

        Assert.Contains(deseret, terms);
    }

    [Fact]
    public void A_unicode_space_separates_tokens()
    {
        // An ideographic space is not in macOS's literal separator set, and a
        // token glued across one is a term the user never wrote.
        Assert.Equal(["Alpha", "Bravo"], TermsIn("Alpha　Bravo"));
    }

    // ─── Degenerate input ───────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("....")]
    [InlineData("!!! ??? ...")]
    [InlineData("123 456")]
    public void Nothing_usable_produces_nothing(string text)
    {
        Assert.Empty(TermsIn(text));
    }

    // ─── Normalisation ──────────────────────────────────────────────────

    [Fact]
    public void A_decomposed_accented_name_is_the_same_term_as_the_composed_one()
    {
        // The hole this closes: LooksLikeProper classifies rune by rune and
        // accepts only letters and hyphens, so a combining diaeresis — a mark,
        // not a letter — killed the whole word. Not demoted, not ranked last:
        // absent from Findings, Ranked, Distinctive and every count, with no
        // row hinting that anything was dropped.
        //
        // NFD is not exotic on this input set. macOS-authored .md/.txt/.eml,
        // APFS filename round-trips and Word/Outlook exports all emit it, and
        // a mail reader's charset decode does no normalisation of its own. The words at stake are exactly the class the feature exists
        // to find.
        const string composed = "Z\u00fcrich";
        const string decomposed = "Zu\u0308rich";

        Assert.NotEqual(composed, decomposed);
        Assert.Contains(composed, TermsIn($"we met in {decomposed} last spring"));
    }

    [Theory]
    [InlineData("So\u0308ren", "S\u00f8ren")]
    [InlineData("Mun\u0303oz", "Mu\u00f1oz")]
    [InlineData("Sz\u030ckoda", "\u0160koda")]
    public void Decomposed_names_arrive_under_their_composed_spelling(
        string decomposedSentenceWord,
        string _)
    {
        // The three shapes NFD produces on this input set: a stroked letter
        // that has no decomposition at all (Soren's o-slash is unaffected and
        // is here as the control), a tilde and a caron. What is asserted is
        // the invariant that matters for identity — whatever comes out is
        // composed, so two files spelling one name two ways produce ONE term
        // and spend ONE of sixty-four vocabulary slots.
        var terms = TermsIn($"we cited {decomposedSentenceWord} twice");

        Assert.NotEmpty(terms);
        Assert.All(terms, t => Assert.True(
            t.IsNormalized(System.Text.NormalizationForm.FormC),
            $"'{t}' came out decomposed"));
    }

    [Fact]
    public void One_name_spelled_two_ways_across_two_files_is_one_term()
    {
        // The identity half of the same bug, and the reason composing beats
        // widening the classifier to tolerate combining marks. Term identity
        // is StringComparer.Ordinal, so without normalisation these are two
        // terms, two cloud words, two slots and two ids in the bias trie.
        var terms = VocabularyTermExtractor.Extract(
            "we shipped Z\u00fcrich today\nwe shipped Zu\u0308rich today",
            NoDictionary);

        var zurich = Assert.Single(terms, t => t.Text.StartsWith('Z'));
        Assert.Equal("Z\u00fcrich", zurich.Text);
        Assert.Equal(2, zurich.Occurrences);
    }

    [Fact]
    public void An_unpaired_surrogate_does_not_stop_the_scan()
    {
        // A 512 KB read can end mid-character, and both IsNormalized and
        // Normalize throw ArgumentException on the lone half that leaves
        // behind. The corpus is still perfectly scannable in the form it
        // arrived in, so the failure is caught and the scan continues.
        var terms = TermsIn("we shipped Trovioo today \ud83d");

        Assert.Contains("Trovioo", terms);
    }

    // ─── Control characters ─────────────────────────────────────────────

    [Fact]
    public void An_escape_sequence_does_not_glue_itself_to_the_word_before_it()
    {
        // A saved terminal log or a .md holding pasted coloured CLI output.
        // '[' is a separator and ESC was not, so the token was ArcFace+ESC:
        // classified Camel, unconditionally distinctive, offered, kept — and
        // then refused by the merge into the vocabulary file, which had no
        // sentence for that refusal. "Added 39 words"
        // under a button that said "Add 40 to my vocabulary".
        var terms = TermsIn("we ran ArcFace\u001b[0m twice and ArcFace\u001b[0m again");

        Assert.Contains("ArcFace", terms);
        Assert.All(terms, t => Assert.DoesNotContain(t, c => char.IsControl(c)));
    }

    [Theory]
    [InlineData('\u0001')]
    [InlineData('\u001b')]
    [InlineData('\u007f')]
    [InlineData('\u0085')]
    [InlineData('\u009b')]
    public void Every_control_character_ends_a_token(char control)
    {
        // Not just ESC. C0, DEL, NEL and C1 all reach the store as an
        // unwritable spelling, and the whole of char.IsControl is what the
        // format's own guard checks.
        var terms = TermsIn($"we ran Alpha{control}Bravo today");

        Assert.Contains("Alpha", terms);
        Assert.Contains("Bravo", terms);
    }

    [Fact]
    public void The_stoplist_is_the_size_it_says_it_is()
    {
        // Sixty entries, and the shortness is the design: a long stoplist
        // starts deleting the jargon this feature exists to find.
        Assert.Equal(60, VocabularyStoplist.Count);
    }
}
