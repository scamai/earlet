using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;

namespace Dictation.Services;

/// <summary>
/// What kind of thing a candidate term looked like.
/// </summary>
/// <remarks>
/// The kind is not decoration: it selects which distinctiveness test the term
/// has to pass (<see cref="VocabularyDistinctiveness"/>), and it is fixed by
/// the term's FIRST appearance and never revisited.
/// </remarks>
public enum VocabularyTermKind
{
    /// <summary>Looks like a host name: <c>example.ai</c>.</summary>
    Domain,

    /// <summary>Short and shouted: <c>ONNX</c>, <c>QNN</c>.</summary>
    Acronym,

    /// <summary>An internal capital: <c>ElmTree</c>, <c>MAX_SIZE</c>.</summary>
    Camel,

    /// <summary>An ordinary capitalised word: <c>Trovioo</c>.</summary>
    Proper,
}

/// <summary>
/// Where one occurrence of a term sat in the text that was scanned.
/// </summary>
/// <param name="Start">UTF-16 offset into the scanned string.</param>
/// <param name="Length">Length in UTF-16 code units, AFTER trailing-dot trimming.</param>
/// <remarks>
/// <para>
/// <b>The offsets are into the NORMALISED corpus, not into the bytes the file
/// held.</b> <see cref="VocabularyTermExtractor.Extract"/> composes its input to
/// <see cref="System.Text.NormalizationForm.FormC"/> before scanning (see
/// <see cref="VocabularyTermExtractor.NormaliseCorpus(string)"/>), so a
/// decomposed <c>u</c> +
/// combining diaeresis in the file is one <c>ü</c> here and every offset after
/// it is one shorter. A highlighter must therefore run over the same normalised
/// string, which is what
/// <see cref="VocabularyTermExtractor.NormaliseCorpus(string)"/> is public for.
/// </para>
/// <para>
/// UTF-16 offsets rather than macOS's <c>Range&lt;String.Index&gt;</c> over
/// grapheme clusters, because that is the coordinate system .NET strings are
/// already in and the one a WPF <c>Run</c> / <c>TextPointer</c> highlight
/// consumes natively. This is one of the few places the port is strictly
/// easier than the original.
/// </para>
/// <para>
/// The length is measured from the TRIMMED token, not from where the scan
/// stopped, so highlighting <c>end.</c> underlines <c>end</c>.
/// </para>
/// </remarks>
public readonly record struct VocabularyTermRange(int Start, int Length);

/// <summary>
/// One candidate term and everywhere it appeared in one source's corpus.
/// </summary>
/// <param name="Text">
/// The token exactly as it appeared the first time in the NORMALISED corpus.
/// Case, hyphens and dots are verbatim; the only thing done to the text is
/// Unicode composition, which happens once for the whole corpus before the
/// scan — see <see cref="VocabularyTermExtractor.NormaliseCorpus(string)"/> and
/// the remarks below.
/// </param>
/// <param name="Kind">Fixed by the first appearance.</param>
/// <param name="Occurrences">How many times it appeared.</param>
/// <param name="Ranges">Every occurrence, in document order.</param>
/// <param name="Ordinal">
/// Which term this was, counting from zero, in first-appearance order. The
/// tie-break for equal occurrence counts — never alphabetical, because
/// alphabetical order in a frequency list reads as meaning that is not there.
/// </param>
/// <remarks>
/// <b>Casing is verbatim and identity is case-SENSITIVE here.</b>
/// <c>Trovioo</c> and <c>TROVIOO</c> are two terms with two counts and both can
/// reach the cloud. Collapsing them earlier would have to pick a winner without
/// knowing which spelling the user actually writes; the collapse happens once,
/// late, at merge time, where the DECLARED spelling in the existing vocabulary
/// is the right winner because that spelling is what goes back into every
/// transcript.
/// </remarks>
public sealed record VocabularyTerm(
    string Text,
    VocabularyTermKind Kind,
    int Occurrences,
    ImmutableArray<VocabularyTermRange> Ranges,
    int Ordinal);

/// <summary>
/// Turns one source's corpus into ranked candidate terms.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hand-rolled scanner, not a regex and not a word-break iterator.</b>
/// The rules it needs are not the ones a general tokeniser implements:
/// <c>.</c> and <c>-</c> must NOT split a token (so <c>example.ai</c> and
/// <c>Local-Flow</c> survive whole), underscores and digits must not split one
/// (so <c>MAX_SIZE</c> is one token), and the scanner has to carry a
/// sentence-start flag that a tokeniser has no concept of.
/// </para>
/// <para>
/// <b>Sentence-start tracking is the reason a dictionary is needed at all.</b>
/// At the start of a sentence capitalisation carries no information, so a
/// capitalised token there is accepted as a proper noun ONLY if it is not an
/// ordinary English word. Without the newline-as-boundary half of that rule,
/// the first capitalised word of every paragraph, greeting and signature became
/// a proper noun — in an email that is "Following", "Best", "Thanks" and the
/// recipient's own name.
/// </para>
/// </remarks>
public static class VocabularyTermExtractor
{
    /// <summary>
    /// Shortest token considered. Two-character tokens are overwhelmingly
    /// initials, units and operators.
    /// </summary>
    public const int MinimumLength = 3;

    /// <summary>
    /// Longest token considered, in characters.
    /// </summary>
    /// <remarks>
    /// <b>Windows has a maximum term length and macOS does not.</b> The macOS
    /// extractor has no ceiling at all; the store it writes to caps a phrase at
    /// 1,000 entries but not at any length. Here the destination is
    /// <see cref="DictationVocabulary"/>, whose
    /// <see cref="DictationVocabulary.MaxPhraseLength"/> silently DROPS an
    /// over-length line on the next parse. Enforcing it at extraction is the
    /// difference between never offering a 200-character token and offering
    /// one, letting the user keep it, saying "Added 12 words" and writing 11.
    /// </remarks>
    public const int MaximumLength = DictationVocabulary.MaxPhraseLength;

    /// <summary>
    /// Longest run of PAYLOAD characters — the base64/hex/identifier alphabet
    /// and nothing else — that is still treated as something a person wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The token-shape gate cannot reach every fragment of a pasted blob,
    /// because not every fragment is camel-shaped.</b> One Notes <c>.md</c>
    /// holding a single <c>data:image/png;base64,…</c> URI of about 4 KB beside
    /// two sentences of prose offered 59 distinctive terms; the shape gate on
    /// <see cref="LooksLikeWord"/> took that to 13, of which eight were still
    /// base64 — and three of those eight (<c>Kqg</c>, <c>Uczm</c>,
    /// <c>D8LWIJ</c>) classified Proper and Acronym, where an internal capital
    /// is not the evidence and no camel rule applies. By SHAPE they are
    /// indistinguishable from initials and a short name, and no amount of
    /// tightening separates them without also refusing real ones.
    /// </para>
    /// <para>
    /// <b>The reliable signal is not the token, it is the run it lives in.</b>
    /// Base64 payloads, JWTs, hex digests and pasted images are hundreds to
    /// thousands of characters of one uninterrupted alphabet; prose is not. So
    /// a run this long is skipped whole, and every fragment inside it goes with
    /// it, camel or not.
    /// </para>
    /// <para>
    /// <b>The run is measured over the PAYLOAD alphabet, not over layout, and
    /// that correction is the whole of this rule's second version.</b> The
    /// first measured a run as "no whitespace and no control character", which
    /// is the shape of a data URI — and also the shape of a comma-separated
    /// CSV row, a minified JSON object and a markdown link. Measured on the
    /// engine: a single <c>.csv</c> row of comma-joined surnames returned 17
    /// terms at 155 characters and ZERO at 165; a <c>.json</c> note holding
    /// twelve minified contact objects (595 characters) returned zero;
    /// <c>[Trovioo](https://example.com/r/…)</c> kept
    /// <c>Trovioo</c> at 142 characters and lost it at 162. <c>.csv</c> and
    /// <c>.json</c> are ordinary scan inputs (<see cref="VocabularyFileExtensions"/>),
    /// not exotic ones — an exported contact list is the most name-dense file a
    /// person owns, and it scanned as "1 file · 0 names", which is a false
    /// statement about a file of proper nouns.
    /// </para>
    /// <para>
    /// <see cref="IsPayload"/> is exactly a base64 alphabet plus the three
    /// characters an opaque identifier is spelled with — so a blob, a JWT and a
    /// hex digest are still one run, while the <c>,</c> of a CSV row, the
    /// <c>"{:</c> of JSON and the <c>[](?&amp;</c> of a link all break one.
    /// Only the payload run is skipped, not the whole layout-free region it
    /// sits in, which is what keeps the <c>Trovioo</c> in a markdown link whose
    /// URL is over the cap.
    /// </para>
    /// <para>
    /// <b>160 rather than something tighter</b> because the cost of being
    /// wrong is a silently dropped name. A URL path, a Windows path and a long
    /// hyphenated identifier all fit comfortably under it; a data URI is at
    /// least an order of magnitude over.
    /// </para>
    /// <para>
    /// A skipped run is a sentence boundary. The word after a data URI is at
    /// the head of what it is written in, not mid-sentence, and judging it as
    /// sentence-initial is what stops the first capitalised word after every
    /// pasted image being offered as somebody's proper noun.
    /// </para>
    /// </remarks>
    public const int MaxOpaqueRun = 160;

    /// <summary>
    /// Characters that end a token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// macOS's set exactly (<c>DemoTermExtractor.swift:101</c>), plus general
    /// Unicode whitespace, which is checked separately below. Note what is NOT
    /// here: <c>.</c>, <c>-</c>, <c>_</c> and every digit. Each omission is
    /// load-bearing — <c>example.ai</c>, <c>Local-Flow</c>, <c>MAX_SIZE</c> and
    /// <c>ONNX2</c> are all single tokens because of it.
    /// </para>
    /// <para>
    /// <b>Every other control character is a separator too, and that is a
    /// Windows-side addition rather than a port.</b> The three the string names
    /// (tab, newline, carriage return) are the only ones macOS's prose corpus
    /// ever produced. The sources this feature reads on Windows include Notes
    /// and Documents folders holding saved terminal logs and markdown with
    /// pasted coloured CLI output, where an SGR escape — ESC (U+001B) then
    /// <c>[0m</c> — sits hard against the word before it. <c>[</c> is a
    /// separator and ESC was not, so <c>ElmTree</c> followed by an SGR reset
    /// tokenised as <c>ElmTree</c>+ESC: it classified as camel-case, it was
    /// offered, the user kept it, and the merge into the vocabulary file then
    /// rejected the one spelling the file format cannot carry — "Added 39 words" after a button that said
    /// "Add 40 to my vocabulary". Treating the whole of
    /// <see cref="char.IsControl(char)"/> as a boundary rather than merely
    /// refusing such a token also recovers the real words on both sides of one.
    /// </para>
    /// </remarks>
    private const string Separators = " \t\n\r,;:!?()[]{}\"'“”‘’<>/\\|@#$%^&*+=~`";

    private static readonly SearchValues<char> SeparatorChars =
        SearchValues.Create(Separators);

    /// <summary>
    /// Extract candidate terms from one corpus, ordered by occurrence count
    /// descending, ties broken by first appearance.
    /// </summary>
    /// <param name="text">
    /// The whole source's text: one string, not one per file, so a caller
    /// scanning a folder concatenates its files first.
    /// </param>
    /// <param name="lexicon">
    /// The common-word oracle, consulted only for the sentence-initial
    /// decision here. Distinctiveness is a separate, later question.
    /// </param>
    /// <param name="minLength">Shortest token considered.</param>
    public static ImmutableArray<VocabularyTerm> Extract(
        string text,
        IVocabularyLexicon lexicon,
        int minLength = MinimumLength)
    {
        ArgumentNullException.ThrowIfNull(lexicon);

        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        text = NormaliseCorpus(text);

        var found = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var order = new List<string>();
        var i = 0;
        var sentenceStart = true;

        // How far the payload-run scan has already looked. Keeps the scan
        // below O(n) over the whole corpus rather than O(n) per token: a run
        // measured once is never re-measured from a position inside it, and
        // the answer for an interior position is the same answer.
        var runScanned = 0;

        while (i < text.Length)
        {
            // ── Skip the gap between tokens ──────────────────────────────
            //
            // '.' is skipped here even though it is not a separator. That
            // asymmetry is what lets a full stop both stay inside "example.ai"
            // and open a new sentence after "…the end. Next".
            while (i < text.Length)
            {
                var c = text[i];
                if (!IsSeparator(c) && c != '.')
                {
                    break;
                }

                // '。' '！' '？' are the same three marks in the fullwidth
                // forms a CJK keyboard produces; a sentence that ends in one
                // ends just as much as a sentence that ends in a full stop.
                if (c is '.' or '!' or '?' or '\n' or '\r'
                    or '\u3002' or '\uFF01' or '\uFF1F')
                {
                    sentenceStart = true;
                }

                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            // ── Skip an opaque run whole ────────────────────────────────
            if (i >= runScanned)
            {
                var end = i;
                while (end < text.Length && IsPayload(text[end]))
                {
                    end++;
                }

                runScanned = end;

                if (end - i >= MaxOpaqueRun)
                {
                    i = end;
                    sentenceStart = true;
                    continue;
                }
            }

            // ── Take one token ──────────────────────────────────────────
            var start = i;
            while (i < text.Length && !IsSeparator(text[i]))
            {
                i++;
            }

            var raw = text.AsSpan(start, i - start);
            var trimmed = TrimTrailingStops(raw);

            // A token that ended in a full stop closes the sentence, whether or
            // not the token itself was usable. Computed BEFORE the guards
            // below, because "x. Following" has to treat "Following" as
            // sentence-initial even when "x" was too short to keep.
            var closesSentence = raw.Length > 0 && raw[^1] == '.';

            if (trimmed.Length >= minLength && trimmed.Length <= MaximumLength)
            {
                var token = trimmed.ToString();
                var kind = Classify(token, sentenceStart, lexicon);

                if (kind is not null)
                {
                    if (!found.TryGetValue(token, out var accumulator))
                    {
                        accumulator = new Accumulator(kind.Value, order.Count);
                        found[token] = accumulator;
                        order.Add(token);
                    }

                    accumulator.Ranges.Add(new VocabularyTermRange(start, trimmed.Length));
                }
            }

            sentenceStart = closesSentence;
        }

        var terms = ImmutableArray.CreateBuilder<VocabularyTerm>(order.Count);
        foreach (var token in order)
        {
            var accumulator = found[token];
            terms.Add(new VocabularyTerm(
                token,
                accumulator.Kind,
                accumulator.Ranges.Count,
                [.. accumulator.Ranges],
                accumulator.Ordinal));
        }

        // Occurrences descending, first appearance ascending. Sorted here
        // rather than left to the caller because "the order terms come out in"
        // is a product decision, and a caller that re-sorted would be a second
        // copy of it.
        return
        [
            .. terms.OrderByDescending(t => t.Occurrences).ThenBy(t => t.Ordinal)
        ];
    }

    /// <summary>
    /// Compose the corpus to <see cref="NormalizationForm.FormC"/> before
    /// anything looks at a character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, a decomposed accented name is not demoted — it is
    /// gone.</b> <see cref="LooksLikeProper"/> classifies rune by rune and
    /// accepts only letters and hyphens, so <c>Zürich</c> written as
    /// <c>U+0075 U+0308</c> fails on the combining diaeresis, which is a mark
    /// and not a letter, and the word appears in no finding, no ranking and no
    /// count. macOS never had this hole because its extractor classifies over
    /// grapheme clusters, where the base letter and its mark are one character.
    /// </para>
    /// <para>
    /// <b>Decomposed text is the normal case on the inputs this reads, not an
    /// exotic one.</b> macOS-authored <c>.md</c>, <c>.txt</c> and <c>.eml</c>
    /// files, anything that has been through an APFS filename round-trip, and
    /// Word and Outlook exports all emit NFD, and a mail reader's charset
    /// decode does no normalisation of its own. The words at stake are precisely the class the feature exists to
    /// find: Zürich, Søren, Muñoz, Škoda.
    /// </para>
    /// <para>
    /// <b>Composing beats widening the classifier</b> — accepting
    /// <see cref="System.Globalization.UnicodeCategory.NonSpacingMark"/> in the
    /// two <c>LooksLike…</c> tests would keep the term but leave identity
    /// split: two files spelling one name two ways would produce two terms,
    /// two cloud words and two of sixty-four vocabulary slots, because term
    /// identity here is <see cref="StringComparer.Ordinal"/>.
    /// </para>
    /// <para>
    /// <see cref="string.IsNormalized(NormalizationForm)"/> first, because the
    /// overwhelmingly common corpus is ASCII and that check is a scan with no
    /// allocation. The catch is not defensive padding: both methods throw
    /// <see cref="ArgumentException"/> on an unpaired surrogate, which a
    /// truncated 512 KB read can genuinely produce at its tail, and a corpus
    /// that cannot be composed is still perfectly scannable in the form it
    /// arrived in.
    /// </para>
    /// <para>
    /// The composition itself lives in
    /// <see cref="VocabularyIdentity.Compose(string?)"/> so the STORE can call
    /// exactly the same rule. It has to: term identity here is composed and
    /// term identity there was raw ordinal, and the gap between them offered a
    /// word the user had struck out and then wrote it a second time.
    /// </para>
    /// </remarks>
    public static string NormaliseCorpus(string text) => VocabularyIdentity.Compose(text);

    private sealed class Accumulator(VocabularyTermKind kind, int ordinal)
    {
        public VocabularyTermKind Kind { get; } = kind;

        public int Ordinal { get; } = ordinal;

        public List<VocabularyTermRange> Ranges { get; } = [];
    }

    /// <summary>
    /// Does this character end a token?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ASCII set above, plus whitespace and controls, plus <b>all Unicode
    /// punctuation and symbols</b> except the three characters that are
    /// deliberately kept INSIDE a token: <c>.</c> (<c>example.ai</c>), <c>-</c>
    /// (<c>Local-Flow</c>) and <c>_</c> (<c>MAX_SIZE</c>). The apostrophes are
    /// already in the ASCII set and stay separators.
    /// </para>
    /// <para>
    /// <b>The ASCII-only set welded a name to its neighbour and lost both.</b>
    /// Synthetic boundary cases include <c>"We met Trovioo—Zurich last week."</c>
    /// and <c>"The Basel–Zurich corridor with Trovioo."</c>; two names joined
    /// by a middle dot
    /// (<c>·</c>) welded the same way and returned neither.
    /// An em dash with no spaces around it is what Word and Outlook autocorrect
    /// EMIT, and Outlook and Word exports are the stated input for this
    /// feature. Before the word-shape gate this cost one wrong term; after it,
    /// the welded token fails the gate and it costs two missing names.
    /// </para>
    /// <para>
    /// <b>A CJK ideograph, kana or hangul syllable is a separator too, and
    /// that is a script rule rather than a punctuation one.</b>
    /// <c>我们和Trovioo合作</c> is one token to a tokeniser that splits on
    /// spaces, and its first rune is not uppercase, so <c>Trovioo</c> was lost
    /// with the sentence around it — widening the punctuation set does not
    /// reach this, because the boundary is between two scripts and not at a
    /// mark. Nothing is given up by it: every classifier here keys on CASE
    /// (<see cref="LooksLikeProper"/> demands an uppercase first rune), so a
    /// run of ideographs could never have become a term on its own.
    /// </para>
    /// </remarks>
    private static bool IsSeparator(char c)
    {
        if (c is '.' or '-' or '_')
        {
            return false;
        }

        return SeparatorChars.Contains(c)
            || char.IsWhiteSpace(c)
            || char.IsControl(c)
            || char.IsPunctuation(c)
            || char.IsSymbol(c)
            || IsCjk(c);
    }

    /// <summary>
    /// A character from a script written without spaces between its words —
    /// CJK ideographs, kana, hangul and their compatibility blocks.
    /// </summary>
    /// <remarks>
    /// Explicit ranges rather than "any caseless letter", which would also
    /// catch Arabic, Hebrew, Devanagari and Thai. Those are written WITH
    /// spaces, so a Latin name beside one is already a separate token and
    /// there is nothing to recover; naming only the scripts with the problem
    /// keeps the rule to what was measured.
    /// </remarks>
    private static bool IsCjk(char c) =>
        c is (>= '\u1100' and <= '\u11FF')     // Hangul Jamo
            or (>= '\u3040' and <= '\u30FF')   // Hiragana, Katakana
            or (>= '\u3400' and <= '\u4DBF')   // CJK ext A
            or (>= '\u4E00' and <= '\u9FFF')   // CJK unified ideographs
            or (>= '\uAC00' and <= '\uD7AF')   // Hangul syllables
            or (>= '\uF900' and <= '\uFAFF');  // CJK compatibility

    /// <summary>
    /// A character a machine-generated payload is spelled with — the only kind
    /// that CONTINUES a <see cref="MaxOpaqueRun"/> run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>A-Z a-z 0-9 + / =</c> is the base64 alphabet, both the standard one
    /// and the URL-safe one, so a data URI, a JWT segment and a hex digest are
    /// each one run. <c>_ - .</c> are here because they are the three
    /// characters an opaque identifier is spelled with and because a JWT is
    /// three base64 segments joined by dots — without <c>.</c> a JWT is three
    /// runs of 36, 342 and 86, and the 342 is the only one over the cap.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT here is what a person's punctuation looks
    /// like.</b> <c>,</c> breaks a CSV row into surnames, <c>"</c> <c>{</c>
    /// <c>:</c> break minified JSON into its values, <c>[</c> <c>]</c>
    /// <c>(</c> <c>?</c> <c>&amp;</c> break a markdown link away from its
    /// label, and <c>@</c> breaks an address list. Every one of those files
    /// scanned as zero names while this rule measured layout instead.
    /// </para>
    /// </remarks>
    private static bool IsPayload(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
            or '+' or '/' or '=' or '_' or '-' or '.';

    /// <summary>
    /// Drop trailing full stops one at a time, stopping as soon as what is
    /// left looks like a domain.
    /// </summary>
    /// <remarks>
    /// <c>example.ai.</c> → <c>example.ai</c> (stop: the shortened form is a domain).
    /// <c>end.</c> → <c>end</c>. <c>etc..</c> → <c>etc</c>.
    /// </remarks>
    private static ReadOnlySpan<char> TrimTrailingStops(ReadOnlySpan<char> token)
    {
        while (token.Length > 0 && token[^1] == '.')
        {
            token = token[..^1];

            if (LooksLikeDomain(token))
            {
                break;
            }
        }

        return token;
    }

    // ─── Classification ─────────────────────────────────────────────────

    /// <summary>
    /// Which kind this token is, or <c>null</c> for "not a candidate".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ORDER of these four tests is itself the rule. Domain first, because
    /// <c>example.ai</c> contains no capital at all and would otherwise fall out.
    /// Acronym before camel, so <c>ONNX</c> is judged as a shouted word (which
    /// gets a dictionary check) rather than as an internal capital (which does
    /// not). Camel before the letters-only guard in the proper branch, which is
    /// why <c>MAX_SIZE</c> — an underscore, so not letters-only — still
    /// classifies rather than being discarded.
    /// </para>
    /// </remarks>
    private static VocabularyTermKind? Classify(
        string token,
        bool sentenceStart,
        IVocabularyLexicon lexicon)
    {
        if (LooksLikeDomain(token))
        {
            return VocabularyTermKind.Domain;
        }

        // A dot that did not make a domain made a member-access chain.
        //
        // This is the second measured departure from the macOS rules, and it
        // follows from the first. The tokeniser keeps '.' inside a token for
        // exactly one reason — so "example.ai" survives whole — and macOS then
        // lets a non-domain dotted token fall through to the camel test, which
        // it passes, because camel only asks for a leading capital and a later
        // one. On prose that is harmless. On a .NET checkout it offered
        // "ArgumentNullException.ThrowIfNull", "StringComparison.OrdinalIgnoreCase",
        // "Dictation.Services" and "DictationState.Idle" in the top ten of
        // a representative source scan — none of which is a thing anybody says
        // out loud, and each of which would spend one of the vocabulary's 64
        // slots and part of a measured false-trigger budget.
        //
        // It also closes a case the domain test used to let through: "end.Next"
        // in a sentence that lost its space is not a host name and is now not a
        // term either.
        if (token.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        if (LooksLikeAcronym(token))
        {
            return VocabularyTermKind.Acronym;
        }

        if (LooksLikeCamel(token))
        {
            // The shape gate is a REFUSAL, not a demotion to the proper-noun
            // branch. A blob that failed it is not a proper noun either, and
            // falling through would hand a letters-only fragment ("AbCdEfGhIj")
            // to LooksLikeProper, which accepts letters and hyphens and would
            // offer it.
            return LooksLikeWord(token) ? VocabularyTermKind.Camel : null;
        }

        if (LooksLikeProper(token, sentenceStart, lexicon))
        {
            return VocabularyTermKind.Proper;
        }

        return null;
    }

    /// <summary>
    /// Contains a dot, at least two non-empty dot-parts, a last part of 2–6
    /// LOWERCASE letters, and a first part that starts with a letter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bounds are what separate a host name from a version number
    /// ("1.2.3" — the first part does not start with a letter) and from a
    /// sentence that lost its space ("end.Next" — the last part is
    /// capitalised, and <see cref="Classify"/> then discards a dotted token
    /// that is not a domain outright).
    /// </para>
    /// <para>
    /// <b>"Lowercase" is one character tighter than the macOS rule, and it was
    /// added on measurement rather than taste.</b> macOS asks only that the
    /// last part be 2–6 letters, which is correct for the prose the rules were
    /// written against. Pointed at a .NET checkout it is not: a scan of this
    /// project (22 files, 901 distinctive candidates) offered
    /// <c>ArgumentNullException.ThrowIfNull</c>, <c>string.Empty</c>,
    /// <c>StringComparison.OrdinalIgnoreCase</c>, <c>text.Length</c> and
    /// <c>choice.Source</c> in its top ten — nine of the fifteen loudest terms
    /// were dotted member-access chains. Every one of them is a DOMAIN under
    /// the ported rule, and a domain is unconditionally distinctive, so no
    /// later gate could catch them. Requiring the last part to be lowercase
    /// removes all of them and keeps every real host name, because a top-level
    /// domain is written in lowercase.
    /// </para>
    /// <para>
    /// <b>A last part that is a FILE EXTENSION is not a domain either</b>, and
    /// that is the second half of the same rule: the lowercase requirement was
    /// added to remove dotted member-access chains, and a file extension is
    /// lowercase, so <c>README.md</c>, <c>package.json</c>, <c>setup.exe</c>
    /// and <c>main.py</c> walked straight through it and were offered as
    /// unconditionally distinctive Domain terms with an occurrence count equal
    /// to the number of pages that mention them. See
    /// <see cref="VocabularyFileExtensions"/>.
    /// </para>
    /// <para>
    /// <b>What it costs, stated plainly:</b> <c>Example.AI</c> spelled with
    /// capitals is not a domain, and the dotted-token rule in
    /// <see cref="Classify"/> then discards it rather than letting it through
    /// as camel-case — so that spelling is not offered at all. A top-level
    /// domain is written in lowercase and every real one in a document will
    /// be; a user who writes theirs in capitals can still type the word into
    /// their vocabulary. That is the trade, and it buys back the entire top of
    /// a code scan.
    /// </para>
    /// </remarks>
    private static bool LooksLikeDomain(ReadOnlySpan<char> token)
    {
        if (token.IndexOf('.') < 0)
        {
            return false;
        }

        var parts = 0;
        var lastStart = 0;
        var lastLength = 0;

        for (var i = 0; i <= token.Length; i++)
        {
            if (i != token.Length && token[i] != '.')
            {
                continue;
            }

            var length = i - lastStart;
            if (length == 0)
            {
                return false;
            }

            if (parts == 0 && !char.IsLetter(token[lastStart]))
            {
                return false;
            }

            parts++;
            lastLength = length;
            lastStart = i + 1;
        }

        if (parts < 2 || lastLength is < 2 or > 6)
        {
            return false;
        }

        var last = token[^lastLength..];
        foreach (var c in last)
        {
            if (!char.IsLower(c))
            {
                return false;
            }
        }

        // A real host name never ends in .md, .json, .exe or .py, and a file
        // name is otherwise indistinguishable from one — same shape, same
        // lowercase tail. See VocabularyFileExtensions for the measurement and
        // for what refusing these costs.
        return !VocabularyFileExtensions.Contains(last);
    }

    private static bool LooksLikeAcronym(string token)
    {
        if (CountRunes(token) > 6)
        {
            return false;
        }

        var letters = 0;
        foreach (var rune in token.EnumerateRunes())
        {
            if (Rune.IsUpper(rune))
            {
                letters++;
                continue;
            }

            if (!Rune.IsDigit(rune))
            {
                return false;
            }
        }

        return letters >= 2 && !VocabularyStoplist.Contains(token);
    }

    private static bool LooksLikeCamel(string token)
    {
        var first = true;
        var startsUpper = false;

        foreach (var rune in token.EnumerateRunes())
        {
            if (first)
            {
                startsUpper = Rune.IsUpper(rune);
                if (!startsUpper)
                {
                    return false;
                }

                first = false;
                continue;
            }

            if (Rune.IsUpper(rune))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Does this camel-shaped token have the SHAPE of a word, as opposed to
    /// the shape of a random string?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, one pasted inline image spends most of the user's
    /// vocabulary.</b> <see cref="LooksLikeCamel"/> asks for a leading capital
    /// and any later capital and nothing else, and
    /// <see cref="VocabularyDistinctiveness"/> then trusts an internal capital
    /// without a dictionary lookup — so every fragment of a base64 payload
    /// classifies and every fragment is offered. Measured on one Notes
    /// <c>.md</c> holding a single <c>data:image/png;base64,…</c> URI of about
    /// 4 KB beside two sentences of prose: 39 of the 40 offered terms were
    /// base64 fragments, the synthetic names in the prose were pushed past
    /// the offer's display cap by the ordinal tie-break,
    /// and the default action would have spent 39 of the store's 64 slots on
    /// random strings. <c>.md</c>, <c>.txt</c> and <c>.json</c> are all on the
    /// Notes extension list and all routinely carry base64, JWTs and hex
    /// digests, so this is the ordinary shape of a notes folder.
    /// </para>
    /// <para>
    /// <b>Three rules, each of which a real term passes and a random string
    /// does not.</b> Two are asked per separator-delimited segment, because
    /// <c>-</c>, <c>_</c> and <c>.</c> are inside a token here by design and
    /// each side of one is its own word; the syllable rule is asked once over
    /// the whole token, and WHICH of the two scopes each rule gets is the
    /// substance of this gate's second version:
    /// </para>
    /// <list type="number">
    /// <item><b>Per segment: at most <see cref="MaxCaseRuns"/> case runs.</b>
    /// Digits are neutral and do not break a run, so <c>Wav2Vec2</c> is 4.
    /// </item>
    /// <item><b>Per token: at least one case run of
    /// <see cref="MinRunLength"/> letters.</b> This catches the SHORT fragments
    /// the run cap cannot: <c>AbCd</c> is four runs, inside the cap, and every
    /// run is one letter. <b>Deliberately weak</b>: a stricter version — every
    /// lowercase run of two or more, exempting only a <c>Mc</c>/<c>De</c>
    /// prefix — was written first and rejected <c>RoBERTa</c>, which is exactly
    /// the kind of name this feature exists to find.</item>
    /// <item><b>Per segment: at most one INTERIOR digit run, and at least one
    /// vowel anywhere.</b> Trailing digits are ordinary in a product name
    /// (<c>ONNX2</c>, <c>Wav2Vec2</c>); three interior digit runs is a hex or
    /// base64 payload. The vowel test costs nothing and removes consonant-only
    /// hashes.</item>
    /// </list>
    /// <para>
    /// <b>What it does not claim.</b> A short fragment that happens to spell a
    /// plausible camel word (<c>AbcDef</c>) is indistinguishable from a real
    /// one by shape alone and is still offered. The gate is aimed at the
    /// measured failure — a blob producing dozens of terms — not at proving a
    /// single string is not a name.
    /// </para>
    /// <para>
    /// <b>What it cost in its first version, since the paragraph above did not
    /// say.</b> The two scope choices above were both wrong the other way, and
    /// both losses were silent: a four-run cap refused every THREE-word
    /// CamelCase name (<c>CedarGuard</c>, <c>LumaCamera</c>,
    /// <c>MacNotebook</c>), and a per-segment syllable rule refused every name
    /// whose first segment is one letter (<c>T-Mobile</c>, <c>U-Net</c>,
    /// <c>X-Ray</c>, <c>R-CNN</c>). <see cref="Classify"/> treats a shape
    /// refusal as a refusal rather than a fall-through, so those terms reached
    /// no ranking and no count — the row simply said fewer names.
    /// </para>
    /// </remarks>
    private static bool LooksLikeWord(string token)
    {
        var hasVowel = false;
        var hasAcronymSegment = false;
        var longestRun = 0;
        var segmentStart = 0;

        for (var i = 0; i <= token.Length; i++)
        {
            if (i != token.Length && !IsShapeSeparator(token[i]))
            {
                continue;
            }

            var segment = token.AsSpan(segmentStart, i - segmentStart);

            if (!SegmentLooksLikeWord(segment, ref hasVowel, ref longestRun))
            {
                return false;
            }

            hasAcronymSegment |= IsAcronymSegment(segment);
            segmentStart = i + 1;
        }

        // Both asked once over the WHOLE token rather than once per segment,
        // which is the difference between offering T-Mobile and dropping it.
        // See MinRunLength.
        return (hasVowel || hasAcronymSegment) && longestRun >= MinRunLength;
    }

    /// <summary>
    /// Is this segment an acronym in its own right — 2–6 letters, all
    /// uppercase?
    /// </summary>
    /// <remarks>
    /// The same shape <see cref="LooksLikeAcronym"/> recognises as a whole
    /// token, asked of one segment, and it stands in for the vowel the rest of
    /// the gate demands. <c>R-CNN</c> and <c>K-NN</c> have no vowel anywhere
    /// and are exactly the kind of model name a user reads out of ML notes;
    /// measured through <c>Extract</c>, <c>R-CNN</c> returned an empty array
    /// even after the syllable rule moved to the whole token, because the vowel
    /// rule refused it separately. An acronym is a word that is spelled out,
    /// so it has syllables without having vowels.
    /// </remarks>
    private static bool IsAcronymSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length is < 2 or > 6)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (!char.IsUpper(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Shortest case run that counts as having spelled a syllable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This catches the SHORT fragments the run cap cannot: <c>AbCd</c> is four
    /// runs, inside the cap, and every run is one letter. A real term always
    /// spells at least one syllable — <c>SeeSaw</c> (1,2,1,2), <c>ElmTree</c>
    /// (1,2,1,3), <c>RoBERTa</c> (1,1,4,1), <c>McDonald</c> (1,1,1,5).
    /// </para>
    /// <para>
    /// <b>Over the whole token, not over each separator-delimited segment.</b>
    /// Applied per segment it refused every name whose first segment is one
    /// letter — measured through <c>Extract</c>, <c>T-Mobile</c>, <c>U-Net</c>,
    /// <c>X-Ray</c>, <c>R-CNN</c>, <c>E-Trade</c> and <c>ElmTree-v2</c> all
    /// returned an EMPTY array, and macOS offers every one of them. The whole
    /// <c>X-Name</c> class of product and model names is exactly what a general
    /// recogniser gets wrong, and the loss was silent: the shape gate is a
    /// refusal in <see cref="Classify"/>, so the term never reached Findings, a
    /// ranking or any count. Nothing the gate was built for is given back — a
    /// base64 fragment, a JWT segment and a hex digest are each a SINGLE
    /// segment, so <c>AbCd</c> and <c>ASW3oNv8hWdtbqsHe78NpiJlnmLA</c> are
    /// refused by exactly the same arithmetic as before.
    /// </para>
    /// </remarks>
    private const int MinRunLength = 2;

    /// <summary>The characters a token is allowed to carry INSIDE it.</summary>
    /// <remarks>
    /// The same three the tokeniser deliberately keeps out of
    /// <c>Separators</c> — <c>.</c>, <c>-</c> and <c>_</c> — plus the
    /// apostrophe, which arrives inside a name (<c>O'Neill</c>) rather than
    /// between two.
    /// </remarks>
    private static bool IsShapeSeparator(char c) =>
        c is '.' or '-' or '_' or '\'' or '\u2019';

    /// <summary>
    /// How many times the case may flip inside ONE separator-delimited
    /// segment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ElmTree</c> is E|lm|T|ree = 4, <c>SeeSaw</c> = 4, <c>SwiftUI</c> = 3,
    /// <c>MAX_SIZE</c> and <c>LONGACR</c> = 1 per segment; a random base64
    /// fragment alternates about every other character, so a 28-character one
    /// runs about 13.
    /// </para>
    /// <para>
    /// <b>Eight rather than four, because four was a two-word cap and real
    /// names have three words.</b> Each CamelCase word costs two runs, so four
    /// refused every three-word name outright — measured through
    /// <c>Extract</c>: <c>CedarGuard</c> (6 runs), <c>LumaCamera</c>,
    /// <c>JavaScriptCore</c>, <c>MacNotebook</c>, <c>AirPodsPro</c>,
    /// <c>VocabularyOffer</c> and <c>ExampleProductName</c> all returned an
    /// EMPTY array. A user who points the Code source at this very repository
    /// was never offered <c>CedarGuard</c>, silently.
    /// </para>
    /// <para>
    /// <b>Why an absolute eight and not a ratio of the length.</b> A ratio was
    /// written first — <c>caseRuns &lt;= segment.Length / 2</c>, the rule the
    /// "alternates every other character" observation actually implies — and
    /// it does not hold at the short end: <c>MacNotebook</c> is 10 letters and 6
    /// runs, so any average-run-length rule of 2 refuses it. Eight is four
    /// words, which is past every product name measured here and still less
    /// than half of a 28-character fragment's 13.
    /// </para>
    /// </remarks>
    private const int MaxCaseRuns = 8;

    private static bool SegmentLooksLikeWord(
        ReadOnlySpan<char> segment,
        ref bool hasVowel,
        ref int longestRun)
    {
        const int MaxInteriorDigitRuns = 1;

        if (segment.Length == 0)
        {
            return true;
        }

        var caseRuns = 0;
        var lastCase = 0;          // 0 none yet, 1 upper, 2 lower
        var runLength = 0;
        var interiorDigitRuns = 0;
        var inDigitRun = false;

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];

            if (char.IsDigit(c))
            {
                // Digits are neutral: they neither break a case run nor start
                // one, so "Wav2Vec2" reads as two words and not four.
                inDigitRun = true;
                continue;
            }

            if (!char.IsLetter(c))
            {
                // Anything else inside a token is not word shape at all.
                return false;
            }

            if (inDigitRun)
            {
                inDigitRun = false;

                if (++interiorDigitRuns > MaxInteriorDigitRuns)
                {
                    return false;
                }
            }

            if (IsVowelish(c))
            {
                hasVowel = true;
            }

            var kind = char.IsUpper(c) ? 1 : char.IsLower(c) ? 2 : 0;

            if (kind == 0)
            {
                // A cased-letter script this has no case in (CJK, Hebrew). It
                // continues whatever run is open rather than starting one.
                runLength++;
                continue;
            }

            if (kind != lastCase)
            {
                longestRun = Math.Max(longestRun, runLength);

                if (++caseRuns > MaxCaseRuns)
                {
                    return false;
                }

                lastCase = kind;
                runLength = 1;
                continue;
            }

            runLength++;
        }

        longestRun = Math.Max(longestRun, runLength);

        return true;
    }

    /// <summary>
    /// A vowel, or any letter from a script whose vowels this has no business
    /// enumerating.
    /// </summary>
    private static bool IsVowelish(char c) =>
        c > 127
            ? char.IsLetter(c)
            : c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y'
                or 'A' or 'E' or 'I' or 'O' or 'U' or 'Y';

    private static bool LooksLikeProper(
        string token,
        bool sentenceStart,
        IVocabularyLexicon lexicon)
    {
        var first = true;

        foreach (var rune in token.EnumerateRunes())
        {
            if (first)
            {
                if (!Rune.IsUpper(rune))
                {
                    return false;
                }

                first = false;
            }

            // Letters and hyphens only. Rune rather than char so a non-BMP
            // letter is one classifiable thing instead of two surrogate halves
            // that are neither letters nor hyphens.
            if (!Rune.IsLetter(rune) && rune.Value != '-')
            {
                return false;
            }
        }

        if (first)
        {
            return false;
        }

        if (VocabularyStoplist.Contains(token))
        {
            return false;
        }

        // The whole point of tracking sentence starts. Mid-sentence, a capital
        // IS the signal and no dictionary is consulted — which is what lets
        // "Slack" survive in "we shipped Slack yesterday" while "Following" dies
        // at the head of a paragraph.
        return !sentenceStart || !lexicon.IsCommonWord(token);
    }

    private static int CountRunes(string token)
    {
        var count = 0;
        foreach (var _ in token.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}

/// <summary>
/// The short, deliberately English stoplist checked inside the acronym and
/// proper branches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sixty entries, and the shortness is the design.</b> A long stoplist
/// starts deleting the jargon this feature exists to find — the words a
/// general recogniser gets wrong are exactly the ones no general list contains.
/// So this covers only what an email's SHAPE forces to the top: the function
/// words that open sentences, the greetings and sign-offs that open and close
/// every message, and the weekday and month names that fill the middle.
/// </para>
/// <para>
/// Matched verbatim and case-sensitively, as macOS does. An all-caps
/// <c>THE</c> therefore misses this list and is classified as an acronym —
/// and is then dropped anyway by the distinctiveness gate, because "the" is an
/// ordinary English word. Two independent rules catching the same thing is the
/// safe direction here.
/// </para>
/// <para>
/// The exact membership is reconstructed from the recorded description
/// ("function words, greetings, sign-offs, weekdays and months") rather than
/// reconstructed from the shared policy description, because the macOS array was not part of the port
/// package. Every entry is defensible under that description; none of them is
/// a term any user would want biased.
/// </para>
/// </remarks>
public static class VocabularyStoplist
{
    private static readonly FrozenSet<string> Words = new[]
    {
        // Function words that open sentences.
        "The", "This", "That", "These", "Those", "There", "Here", "What",
        "When", "Where", "Which", "While", "With", "Will", "Would", "Could",
        "Should", "And", "But", "For", "Not", "You", "Your", "Our",

        // Greetings and sign-offs, plus the header words a forwarded message
        // drags into the body.
        "Hi", "Hello", "Hey", "Dear", "Thanks", "Thank", "Regards", "Best",
        "Cheers", "Sincerely", "Please", "Sent", "From", "Subject", "Fwd",
        "Attached", "Following",

        // Weekdays.
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
        "Sunday",

        // Months.
        "January", "February", "March", "April", "May", "June", "July",
        "August", "September", "October", "November", "December",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>How many entries the list holds. Traceable, not asserted.</summary>
    public static int Count => Words.Count;

    /// <summary>Is this token on the list? Case-sensitive, verbatim.</summary>
    public static bool Contains(string token) => Words.Contains(token);
}
