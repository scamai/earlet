using Dictation.Services;

namespace Dictation.Services.Tests;

/// <summary>
/// The distinctiveness gate: the shipped dictionary, the crude stemmer that
/// makes it usable, the identifier denylist, and the fail-open branch nobody
/// exercises by accident.
/// </summary>
public class VocabularyLexiconTests
{
    // ─── The shipped dictionary ─────────────────────────────────────────

    [Fact]
    public void The_word_list_ships_inside_the_assembly()
    {
        // macOS gets a 235k-word lexicon free at /usr/share/dict/words.
        // Windows ships no word list at any path, so this one is embedded —
        // and an installer that can drop a file beside the exe can also fail
        // to, which would make the gate fail open on some machines and not
        // others. An embedded resource cannot be absent from an assembly that
        // loaded.
        var names = typeof(VocabularyLexicon).Assembly.GetManifestResourceNames();

        Assert.Contains(VocabularyLexicon.WordListResourceName, names);
    }

    [Fact]
    public void The_word_list_is_found_by_its_fixed_name_not_the_namespace()
    {
        // The csproj gives the resource a fixed LogicalName. Without it the
        // id would be the root namespace plus the folder path
        // ("Dictation.Services.Resources.web2-words.txt.gz"), which moves on
        // every project rename, and a lookup that misses loads an empty
        // dictionary without a word of complaint (VocabularyLexicon fails
        // open). So the name is pinned, the assembly must carry exactly that
        // one resource, and the lookup must open it.
        Assert.Equal("web2-words.txt.gz", VocabularyLexicon.WordListResourceName);

        var assembly = typeof(VocabularyLexicon).Assembly;
        Assert.Equal([VocabularyLexicon.WordListResourceName], assembly.GetManifestResourceNames());

        using var stream = assembly.GetManifestResourceStream(VocabularyLexicon.WordListResourceName);
        Assert.NotNull(stream);
        Assert.True(VocabularyLexicon.Shared.WordCount > 0);
    }

    [Fact]
    public void The_shipped_dictionary_is_the_size_it_is_supposed_to_be()
    {
        // 235,642 curated lines of web2, which lowercase down to 234,122 distinct
        // words. Pinned exactly: this is the artifact the gate's behaviour is
        // defined by, and a resource that silently changed would change which
        // terms every user is offered.
        Assert.Equal(234_122, VocabularyLexicon.Shared.WordCount);
    }

    [Theory]
    [InlineData("Applications")]
    [InlineData("Logs")]
    [InlineData("Users")]
    [InlineData("Resources")]
    [InlineData("Defaults")]
    public void The_five_recorded_regressions_are_treated_as_ordinary_words(string word)
    {
        // These five are why the stemmer exists. web2 is base-form sparse — it
        // has "application" and "log" but not their plurals — and a bare
        // lookup let all five through as if they were somebody's jargon,
        // visible at the top of a real scan of this repository.
        Assert.True(VocabularyLexicon.Shared.IsCommonWord(word));
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Date")]
    [InlineData("NOT")]
    [InlineData("See")]
    [InlineData("Nothing")]
    [InlineData("Every")]
    public void The_measured_top_false_positives_are_ordinary_words(string word)
    {
        // Before the dictionary gate, a real scan of this repository put
        // "String" (4,173 occurrences), "Date", "NOT", "Published" and
        // "SwiftUI" at the very top, and "String" alone would have been the
        // largest word in the cloud.
        Assert.True(VocabularyLexicon.Shared.IsCommonWord(word));
    }

    [Theory]
    [InlineData("Trovioo")]
    [InlineData("Zupong")]
    [InlineData("ArcFace")]
    [InlineData("Velonym")]
    public void A_real_name_is_not_an_ordinary_word(string word)
    {
        Assert.False(VocabularyLexicon.Shared.IsCommonWord(word));
    }

    [Fact]
    public void The_dictionary_is_case_insensitive()
    {
        Assert.True(VocabularyLexicon.Shared.IsCommonWord("STRING"));
        Assert.True(VocabularyLexicon.Shared.IsCommonWord("string"));
    }

    // ─── The three stemmer rules ────────────────────────────────────────

    [Fact]
    public void Ies_folds_back_to_y()
    {
        Assert.True(VocabularyLexicon.Shared.IsCommonWord("Libraries"));
    }

    [Theory]
    [InlineData("Batches")]
    [InlineData("Branches")]
    public void Es_is_dropped(string word)
    {
        Assert.True(VocabularyLexicon.Shared.IsCommonWord(word));
    }

    [Fact]
    public void A_trailing_s_is_dropped()
    {
        Assert.True(VocabularyLexicon.Shared.IsCommonWord("Logs"));
    }

    [Fact]
    public void The_stemmer_only_ever_consults_the_dictionary_it_never_invents_a_match()
    {
        // The hazard a stemmer brings is folding a real name onto a real word.
        // These three rules can only ever ANSWER with something the dictionary
        // already contains, so the plural of a private name stays private.
        Assert.False(VocabularyLexicon.Shared.IsCommonWord("Trovioos"));
        Assert.False(VocabularyLexicon.Shared.IsCommonWord("Zupongs"));
    }

    [Fact]
    public void The_stemmer_is_not_what_decides_Miles()
    {
        // Worth pinning because it is easy to get the reasoning backwards. A
        // real stemmer was rejected for dropping "Miles" to "Mile" — and the
        // drop-s rule here reaches "arie" too. What saves it is not the
        // stemmer, it is the dictionary: web2 contains "miles" EXACTLY, so the
        // very first lookup answers and no suffix rule ever runs. Swap in a
        // word list without that entry and this behaviour changes, which is
        // exactly why the shipped list is pinned by size above.
        Assert.True(VocabularyLexicon.Of(["miles"]).IsCommonWord("Miles"));
        Assert.True(VocabularyLexicon.Shared.IsCommonWord("Miles"));
    }

    // ─── Fail open ──────────────────────────────────────────────────────

    [Fact]
    public void With_no_dictionary_nothing_is_an_ordinary_word()
    {
        // The direction is deliberate and this is the branch nobody exercises
        // by accident: an extra word in the list is visible and one click from
        // removal, a missing proper noun is the feature not working.
        Assert.Equal(0, VocabularyLexicon.Empty.WordCount);
        Assert.False(VocabularyLexicon.Empty.IsCommonWord("string"));
        Assert.False(VocabularyLexicon.Empty.IsCommonWord("Monday"));
    }

    [Fact]
    public void With_no_dictionary_an_ordinary_word_is_still_offered()
    {
        Assert.True(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Proper, "Nothing", VocabularyLexicon.Empty));
    }

    // ─── The identifier denylist ────────────────────────────────────────

    [Theory]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("Api")]
    [InlineData("CPU")]
    [InlineData("pdf")]
    [InlineData("SwiftUI")]
    [InlineData("react")]
    [InlineData("Google")]
    [InlineData("Zoom")]
    public void A_name_every_recogniser_already_knows_is_on_the_denylist(string token)
    {
        // The licence for an entry is narrow and falsifiable: "a general
        // recogniser already transcribes this correctly, so it cannot
        // demonstrate anything". Six known acronyms — JSON, API, CPU, PDF and
        // friends — went straight back to the top of a real scan when this was
        // missing, because none of them is in an English dictionary.
        Assert.True(VocabularyLanguageIdentifiers.Contains(token));
    }

    [Theory]
    [InlineData("Trovioo")]
    [InlineData("Zupong")]
    [InlineData("ArcFace")]
    public void A_private_name_is_not_on_the_denylist(string token)
    {
        // The opposite failure would be worse than the one it prevents: this
        // list is not "words we find unhelpful".
        Assert.False(VocabularyLanguageIdentifiers.Contains(token));
    }

    [Fact]
    public void The_denylist_stays_a_denylist_rather_than_a_second_stoplist()
    {
        // A soft bound with a reason: about 110 entries in the ported list,
        // plus a .NET group this port added because it reads .cs files. If
        // this ever needs raising past 200, the entries being added are
        // probably not clearing the licence.
        Assert.InRange(VocabularyLanguageIdentifiers.Count, 100, 200);
    }

    // ─── The gate itself, per kind ──────────────────────────────────────

    [Fact]
    public void A_domain_skips_the_dictionary()
    {
        // Nothing else looks like a host name and no recogniser spells one
        // right — not even one built entirely out of words it knows.
        Assert.True(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Domain, "orange.com", VocabularyLexicon.Shared));
        Assert.True(VocabularyLexicon.Shared.IsCommonWord("orange"));
    }

    [Theory]
    [InlineData("github.com")]
    [InlineData("google.com")]
    [InlineData("docs.google.com")]
    public void A_domain_does_not_skip_the_identifier_list(string domain)
    {
        // The previous version of this test was named "a domain is always
        // distinctive" and asserted exactly this defect with google.com.
        // "google" and "github" are explicit VocabularyLanguageIdentifiers
        // entries — a general recogniser spells both correctly — and yet the
        // Domain arm returned true unconditionally, so an ordinary email body
        // offered docs.google.com and github.com as distinctive. Domain is the
        // first ranking key and FoundInMail the second, so they outranked
        // every real name a code or documents scan had found.
        Assert.False(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Domain, domain, VocabularyLexicon.Shared));
    }

    [Theory]
    [InlineData("trovioo.com")]
    [InlineData("Cove.ai")]
    [InlineData("mailer.example.com")]
    public void A_domain_whose_name_is_not_on_the_identifier_list_survives(string domain)
    {
        // Non-vacuity for the pair above: the guard asks about the registrable
        // label only, so a subdomain and a top-level domain cannot demote a
        // host name on their own.
        Assert.True(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Domain, domain, VocabularyLexicon.Shared));
    }

    [Fact]
    public void A_camel_token_is_trusted_without_a_dictionary_lookup()
    {
        // An internal capital is itself the evidence: "SeeSaw" is not the
        // English word "seesaw", whatever the dictionary says about the
        // letters.
        var lexicon = VocabularyLexicon.Of(["seesaw"]);

        Assert.True(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Camel, "SeeSaw", lexicon));
        Assert.False(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Proper, "Seesaw", lexicon));
    }

    [Fact]
    public void A_camel_token_is_still_checked_against_the_identifier_list()
    {
        Assert.False(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Camel, "SwiftUI", VocabularyLexicon.Empty));
    }

    [Theory]
    [InlineData(VocabularyTermKind.Acronym, "NOT")]
    [InlineData(VocabularyTermKind.Proper, "See")]
    public void An_acronym_or_a_proper_noun_that_is_ordinary_English_is_not_offered(
        VocabularyTermKind kind,
        string text)
    {
        Assert.False(VocabularyDistinctiveness.IsDistinctive(kind, text, VocabularyLexicon.Shared));
    }

    [Theory]
    [InlineData(VocabularyTermKind.Acronym, "QNN")]
    [InlineData(VocabularyTermKind.Proper, "Trovioo")]
    [InlineData(VocabularyTermKind.Camel, "ArcFace")]
    [InlineData(VocabularyTermKind.Domain, "Cove.ai")]
    public void A_real_term_of_any_kind_is_offered(VocabularyTermKind kind, string text)
    {
        Assert.True(VocabularyDistinctiveness.IsDistinctive(kind, text, VocabularyLexicon.Shared));
    }

    [Fact]
    public void The_six_curated_terms_the_gate_also_demotes_are_the_ones_a_model_gets_right()
    {
        // Measured against the owner's real 32-term vocabulary: the system
        // dictionary removes 15 of the 24 loudest code-scan false positives,
        // and the curated terms it also demotes are exactly the ones a general
        // model already transcribes correctly. Those terms are still RANKED —
        // nothing is discarded, only demoted.
        Assert.False(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Proper, "Echo", VocabularyLexicon.Shared));
        Assert.False(VocabularyDistinctiveness.IsDistinctive(
            VocabularyTermKind.Proper, "Whisper", VocabularyLexicon.Shared));
    }
}
