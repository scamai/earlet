using System.Collections.Immutable;
using System.Text;

namespace Dictation.Services;

/// <summary>
/// One line of the user's vocabulary: a phrase, and whether the user has left
/// it out.
/// </summary>
/// <param name="Text">
/// The phrase as the user wrote it, trimmed. Casing is theirs.
/// </param>
/// <param name="IsExcluded">
/// <c>true</c> when the user removed this word from the vocabulary but the
/// word is still on file so they can put it back.
/// </param>
/// <remarks>
/// <para>
/// <b>Exclusion is not deletion, and that is a ported decision rather than an
/// invention.</b> macOS keeps an excluded term exactly where it was — same
/// position, same size, struck through, with the corner control flipped to
/// "Put it back" (<c>VocabularyCloud.swift</c>). Deleting the word instead
/// reflows the cloud and destroys the undo, so the only way back is to
/// remember how it was spelled and type it again.
/// </para>
/// <para>
/// <b>Position survives exclusion because position is rank and rank is
/// size.</b> An excluded entry keeps its slot in this list, so re-including it
/// puts the word back at the size the user last saw it at rather than
/// silently demoting it to the end.
/// </para>
/// </remarks>
public sealed record DictationVocabularyEntry(string Text, bool IsExcluded)
{
    /// <summary>An ordinary, active phrase.</summary>
    public static DictationVocabularyEntry Included(string text) => new(text, false);

    /// <summary>A phrase the user left out but can put back.</summary>
    public static DictationVocabularyEntry Excluded(string text) => new(text, true);
}

/// <summary>
/// The outcome of an edit to a vocabulary list.
/// </summary>
/// <param name="Entries">
/// The list after the edit. On a refusal this is the list unchanged, so a
/// caller can assign it back unconditionally without checking anything.
/// </param>
/// <param name="Changed"><c>false</c> when nothing moved.</param>
/// <param name="Refusal">
/// <c>null</c> unless the edit was refused, in which case it says why in one
/// sentence.
/// </param>
/// <remarks>
/// macOS refuses a duplicate-creating rename as a SILENT no-op
/// (<c>VocabularyImportModel.rename</c>) — nothing is surfaced, the word just
/// snaps back. That is fine when a person is watching one text field, and it
/// is untestable and undiagnosable everywhere else. So the refusal reason is
/// carried here as data: a surface is free to keep macOS's silence and ignore
/// it, and a test can assert WHICH rule fired instead of asserting that
/// nothing happened.
/// </remarks>
public sealed record DictationVocabularyEditResult(
    ImmutableArray<DictationVocabularyEntry> Entries,
    bool Changed,
    string? Refusal);

/// <summary>
/// The result of loading a user's vocabulary from disk.
/// </summary>
/// <param name="Entries">
/// Every line of the vocabulary in file order, including the ones the user
/// left out. This is what the vocabulary SURFACE draws — the cloud needs the
/// excluded words to keep their positions.
/// </param>
/// <param name="Reason">
/// <c>null</c> when the file was absent (not an error — a user who has never
/// added a term) or was read cleanly. Non-null when the file exists but could
/// not be read, so the caller can log or surface it instead of the failure
/// vanishing into a silently-empty list. Deliberately not an exception: this
/// value sits on the path that feeds live dictation, and a vocabulary problem
/// must never be the reason a hold-to-talk key stops working.
/// </param>
public sealed record DictationVocabularyLoadResult(
    ImmutableArray<DictationVocabularyEntry> Entries,
    string? Reason)
{
    /// <summary>
    /// The phrase list to hand to <c>AsrWorkerTranscriber</c>'s
    /// <c>bias_phrases</c> argument. Empty on a missing file, an unreadable
    /// file, or a file with nothing usable in it — three different situations
    /// that all resolve to "bias nothing", which is the only safe default for
    /// the decoder.
    /// </summary>
    /// <remarks>
    /// Excluded entries are absent from this list by construction. That is the
    /// whole safety property of the feature: a word the user struck through
    /// must never reach the decoder, and the only way to guarantee that at
    /// every call site is for the decoder-facing projection to be the one that
    /// does the filtering.
    /// </remarks>
    public ImmutableArray<string> Phrases { get; } =
        Entries.IsDefaultOrEmpty
            ? ImmutableArray<string>.Empty
            : Entries.Where(e => !e.IsExcluded).Select(e => e.Text).ToImmutableArray();

    /// <summary>No file, and nothing wrong. The common first-run state.</summary>
    public static readonly DictationVocabularyLoadResult Empty =
        new(ImmutableArray<DictationVocabularyEntry>.Empty, null);
}

/// <summary>
/// A user's personal dictation vocabulary — names and terms Whisper's stock
/// model does not know, sent to the shared transcription worker as
/// <c>bias_phrases</c> so beam search leans toward them during decoding.
/// </summary>
/// <remarks>
/// <para>
/// <b>This stores short user-supplied terms.</b> The decoder receives the
/// entries as bounded bias phrases, while the file remains inspectable and
/// editable. Tests use synthetic names such as "Trovioo", "Kensworth" and
/// "Mies" → "Miles"; those fixtures describe policy boundaries, not field
/// measurements. This type owns the phrase file; the host supplies its
/// entries on recognition requests.
/// </para>
/// <para>
/// <b>A plain text file.</b> One phrase per line, UTF-8, named
/// <c>bias_phrases.txt</c> by default. A person can read, edit and copy it
/// with any editor, and a list moves between machines with a copy, not a
/// migration.
/// </para>
/// <para>
/// <b>Excluded words ride in the same file, as comments.</b> See
/// <see cref="ExcludedLinePrefix"/> for why that is the right shape and not
/// merely the convenient one.
/// </para>
/// <para>
/// <b>The directory is a parameter, not a constant.</b> The caller chooses
/// it; hardcoding an
/// env-specific path here would make this untestable off the machine it was
/// written for, and this type is exercised on macOS and Linux the same way
/// the rest of <c>Dictation.Services</c> is.
/// </para>
/// <para>
/// <b>Scale WAS a real hazard here, and the hazard is fixed.</b> This
/// paragraph used to argue the cap from a live defect, and that defect is
/// no longer live — leaving the old wording in place made this file the
/// wrong place to answer "what is actually proven", which is the one job it
/// has. What happened: a 98-phrase lexicon on beam-4 search once returned an
/// empty transcript on <b>26.1% of clips</b> (94/360) that decoded correctly
/// at 16 phrases and below, with a content-dependent onset as early as 32
/// (<c>docs/the platform measurements</c> §4). The bounded-pending fix
/// closed it: the same device now measures <b>0 blanks in 4,320 decodes</b>,
/// and nothing breaks anywhere out to <b>640 phrases</b> — ten times this
/// cap — at flat latency (§8.3, §8). So <see cref="MaxPhraseCount"/> is a
/// prudence bound, not a safety threshold; see its own remarks for why it is
/// nonetheless not raised here.
/// </para>
/// </remarks>
public sealed class DictationVocabulary
{
    /// <summary>
    /// The file name the vocabulary is kept under unless the caller names
    /// another.
    /// </summary>
    public const string DefaultFileName = "bias_phrases.txt";

    /// <summary>
    /// Marks a line as a phrase the user left out: <c>"#- Trovioo"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An excluded word must round-trip, and it must be inert to every
    /// reader that has never heard of exclusion.</b> Those two requirements
    /// together are what pick the format. A <c>#</c> line already means
    /// "ignore me" to <see cref="Load"/>, to any other reader of the format, and to a
    /// person scanning the file — so encoding an exclusion as a comment makes
    /// "this word never reaches the decoder" a property of the FILE FORMAT
    /// rather than a property of one code path remembering to filter. A reader
    /// that does not know about <c>#-</c> degrades to exactly the behaviour we
    /// want: the word is not biased.
    /// </para>
    /// <para>
    /// <b>The alternative, a sidecar exclusion file, was rejected.</b> The
    /// whole point of a plain one-phrase-per-line file is that a person can
    /// edit it by hand. Two files drift the moment they do: deleting a line
    /// from the vocabulary leaves an orphan exclusion, and copying the
    /// vocabulary to another machine silently resurrects every word they
    /// struck through. One file, one truth, and a hand-deleted <c>#-</c> line
    /// is a perfectly sensible way to say "forget that I ever excluded this".
    /// </para>
    /// <para>
    /// <b>The marker requires whitespace after it</b> so a human's ASCII rule
    /// line (<c>"#--------"</c>) stays an ordinary comment instead of becoming
    /// an excluded phrase named <c>"-------"</c>. And if a stray line does get
    /// read as an exclusion, the failure is one struck-through word with a
    /// one-click undo — the recoverable direction.
    /// </para>
    /// </remarks>
    public const string ExcludedLinePrefix = "#-";

    /// <summary>
    /// Longest a single phrase may be, in characters, after trimming.
    /// </summary>
    /// <remarks>
    /// Bias phrases are names and short terms — "Kensworth Rey", "Trovioo",
    /// "Zupong" — not sentences. 64 characters comfortably covers a full name
    /// with a title ("Dr. Kensworth Rey-Whatever the Third") while
    /// rejecting a pasted paragraph, which is not a phrase the user meant to
    /// bias toward. Long phrases consume the decoder's token budget and can
    /// crowd out other vocabulary.
    /// </remarks>
    public const int MaxPhraseLength = 64;

    /// <summary>
    /// Most ACTIVE phrases <see cref="Load"/> will return, regardless of how
    /// many lines the file has. Excluded lines are bounded separately by
    /// <see cref="MaxExcludedCount"/> and do not compete for these slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The number is defensible. The reason originally given for it was
    /// false, and was corrected on 2026-08-19 by measurement.</b>
    /// </para>
    /// <para>
    /// It used to say 64 was "comfortably below" the 98 phrases that produced
    /// the 26% blank-transcript rate. That is not what 64 does under the
    /// mechanism those numbers came from: re-measured on the device against
    /// that same pre-fix mechanism, <b>64 phrases blanked 36.7% (11/30)</b>,
    /// with onset at 32. 64 was never a safe distance from the failure — it
    /// was inside it.
    /// </para>
    /// <para>
    /// What actually removed the failure is the bounded-pending fix, not the
    /// cap. Under the shipped configuration the device saw <b>0 blanks, 0
    /// truncations and 100% exact transcripts across 1,500 draws out to 640
    /// phrases</b> — ten times this cap — at flat latency, including an
    /// adversarial arm of acoustically colliding terms.
    /// </para>
    /// <para>
    /// <b>Raised 64 → 500 on 2026-08-20.</b> The cap is a prudence bound, not a
    /// safety threshold, and 64 was neither: it was a number chosen from a
    /// reason that turned out to be false, and it cost anyone with more than 64
    /// names or product terms the choice of which ones Dictation is allowed to get
    /// right. 500 is macOS's <c>DictationDictionaryRules.maxEntries</c>, so the
    /// two platforms now hold the same list; it sits below the largest size
    /// this device has actually been measured at (640 phrases, 0 failures, flat
    /// p50); and on macOS it is a bound nobody reaches — the measured promotion
    /// rate there is roughly one word per 14,000 dictations.
    /// </para>
    /// <para>
    /// <b>Why a cap at all, still.</b> The phrases are compiled into a trie at
    /// worker init and the whole list rides in every transcribe frame once the
    /// live-vocabulary path is in play. Neither cost is visible at 500 on this
    /// device. What the cap buys is a bounded blast radius for a file nobody has
    /// looked at — a corrupted or programmatically-appended bias_phrases.txt
    /// with two hundred thousand lines should degrade to "the first 500 bias"
    /// rather than to a worker that spends its whole init budget building a
    /// trie.
    /// </para>
    /// <para>
    /// It is still NOT raised on evidence of BENEFIT, and this paragraph is
    /// what stops the next reader assuming otherwise. The 640-phrase run
    /// measured no accuracy change at any size — the lexicon changed zero
    /// tokens on that corpus, because the corpus does not contain the terms. So
    /// this move buys headroom, not accuracy, and headroom is all it claims.
    /// </para>
    /// <para>
    /// Outgrowing it degrades to "the first 500 terms in the file bias, the
    /// rest don't", never to a blank transcript.
    /// </para>
    /// </remarks>
    public const int MaxPhraseCount = 500;

    /// <summary>
    /// Most EXCLUDED phrases the file will carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Excluded words cost the decoder nothing — they never leave this file —
    /// so the measured lexicon-size hazard behind <see cref="MaxPhraseCount"/>
    /// does not apply to them, and letting them share that budget would be the
    /// wrong bound: striking a word out would silently make room for nothing
    /// while pushing an active term off the end.
    /// </para>
    /// <para>
    /// They are still bounded, so the file cannot grow without limit. Matching
    /// <see cref="MaxPhraseCount"/> keeps the worst case (everything struck
    /// through) the same size as the best case.
    /// </para>
    /// <para>
    /// Raised alongside <see cref="MaxPhraseCount"/> on 2026-08-20, to keep
    /// that "same size as the best case" property true. Left at 64 it would
    /// have become the tighter of the two bounds, so a user could hold 500
    /// words and strike out only 64 of them — a limit on UNDOING that is easier
    /// to hit than the limit on doing.
    /// </para>
    /// </remarks>
    public const int MaxExcludedCount = 500;

    private readonly string _directoryPath;
    private readonly string _fileName;

    /// <param name="directoryPath">
    /// The directory the vocabulary file lives in. The caller decides this —
    /// tests pass a temp directory. The directory does not need to exist yet;
    /// <see cref="Save"/>
    /// creates it.
    /// </param>
    /// <param name="fileName">
    /// Defaults to <see cref="DefaultFileName"/>, so the common case needs no
    /// argument and a test can still
    /// isolate multiple vocabularies in one directory if it needs to.
    /// </param>
    public DictationVocabulary(string directoryPath, string fileName = DefaultFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _directoryPath = directoryPath;
        _fileName = fileName;
    }

    /// <summary>The file this vocabulary reads and writes.</summary>
    public string FilePath => Path.Combine(_directoryPath, _fileName);

    // ─── Reading ────────────────────────────────────────────────────────

    /// <summary>
    /// Read the vocabulary file, normalise it, and return the entry list.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// A missing file is not an error — it is what every new install looks
    /// like — and returns <see cref="DictationVocabularyLoadResult.Empty"/>.
    /// A file that exists but cannot be read (permissions, a directory sitting
    /// where the file should be, a device that went away) also degrades to an
    /// empty phrase list rather than throwing, because this sits on the path
    /// that feeds live dictation: a corrupt vocabulary file must never be the
    /// reason speech stops turning into text. The difference between the two
    /// is <see cref="DictationVocabularyLoadResult.Reason"/>, which is
    /// <c>null</c> for the former and populated for the latter so the failure
    /// is still visible to whoever wired this up, just not fatal to them.
    /// </remarks>
    public DictationVocabularyLoadResult Load()
    {
        var path = FilePath;

        if (!File.Exists(path))
        {
            // A DIRECTORY at the vocabulary's path is not "no vocabulary yet".
            // File.Exists answers false for one, so without this the early
            // return swallows it as a fresh install and the user gets silence
            // where their words used to be — the catch below was written to
            // report exactly this case and could never be reached.
            if (Directory.Exists(path))
            {
                return new DictationVocabularyLoadResult(
                    ImmutableArray<DictationVocabularyEntry>.Empty,
                    $"Vocabulary file at '{path}' is a directory, not a file.");
            }

            // Genuinely absent, which is the ordinary state of a fresh install
            // and not worth a reason.
            return DictationVocabularyLoadResult.Empty;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Deliberately catches everything, not just IOException /
            // UnauthorizedAccessException. Every failure mode here — a
            // directory where the file should be, a locked file, a removed
            // network drive — has the same correct response: fall back to no
            // bias rather than let a bad vocabulary file take dictation down
            // with it.
            return new DictationVocabularyLoadResult(
                ImmutableArray<DictationVocabularyEntry>.Empty,
                $"Vocabulary file at '{path}' could not be read: {ex.Message}");
        }

        return new DictationVocabularyLoadResult(ParseLines(lines), null);
    }

    // ─── Writing ────────────────────────────────────────────────────────

    /// <summary>
    /// Write the vocabulary atomically: the whole file is staged next to the
    /// real one and only swapped in once it is complete, so a crash or power
    /// loss mid-write leaves the previous good file in place rather than a
    /// half-written one that <see cref="Load"/> would then hand to the
    /// decoder.
    /// </summary>
    /// <param name="lines">
    /// Raw lines, in the same shape <see cref="Load"/> reads: bare phrases,
    /// blanks, prose comments, and <c>#-</c> exclusion markers all allowed.
    /// Normalised exactly the way <see cref="Load"/> normalises what it reads
    /// back, so <c>Save(x); Load()</c> is a true round trip and callers do not
    /// need to pre-clean their input — the vocabulary text box can be saved
    /// verbatim. Prose comment lines a user hand-typed into the file are NOT
    /// preserved by a programmatic save; exclusion markers ARE, because they
    /// are vocabulary state rather than annotation.
    /// </param>
    public void Save(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        WriteAtomically(ParseLines(lines));
    }

    /// <summary>
    /// Write an already-structured entry list, exclusions and all.
    /// </summary>
    /// <remarks>
    /// The overload a surface uses: it holds entries, not text, and going
    /// through a rendered string only to re-parse it is a chance to lose an
    /// exclusion for no gain.
    /// </remarks>
    public void Save(IEnumerable<DictationVocabularyEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WriteAtomically(ParseLines(ToLines(entries)));
    }

    private void WriteAtomically(ImmutableArray<DictationVocabularyEntry> entries)
    {
        Directory.CreateDirectory(_directoryPath);

        var finalPath = FilePath;
        var tempPath = finalPath + ".tmp";

        // No BOM: plain UTF-8 text, because a BOM on the first line would
        // glue itself onto the first phrase when a naive reader splits on '\n'.
        File.WriteAllLines(
            tempPath,
            ToLines(entries),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // The swap. File.Replace is the atomic move-over-existing-file
        // primitive; File.Move is used only for the first-ever save, when
        // there is nothing yet to replace.
        if (File.Exists(finalPath))
        {
            File.Replace(tempPath, finalPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
    }

    // ─── Line format ────────────────────────────────────────────────────

    /// <summary>
    /// Render entries back to file lines: a bare phrase for an active entry,
    /// <c>"#- phrase"</c> for an excluded one.
    /// </summary>
    /// <remarks>
    /// Public because the vocabulary text box shows the file, and rendering it
    /// anywhere else would be a second copy of the format that can disagree
    /// with this one.
    /// </remarks>
    public static ImmutableArray<string> ToLines(IEnumerable<DictationVocabularyEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            builder.Add(entry.IsExcluded
                ? ExcludedLinePrefix + " " + entry.Text
                : entry.Text);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Trim, drop blanks and prose comments, read <c>#-</c> markers as
    /// excluded entries, dedupe case-insensitively while keeping the
    /// first-seen casing and position, and cap length and count.
    /// </summary>
    /// <remarks>
    /// <para><b>Trim:</b> trailing whitespace from a copy-paste is not part
    /// of the phrase and would make an exact-looking duplicate fail the
    /// dedupe check below.</para>
    /// <para><b>Drop blank lines:</b> an empty phrase biases nothing and
    /// would otherwise occupy a slot under <see cref="MaxPhraseCount"/> for
    /// free.</para>
    /// <para><b>Drop <c>#</c> comment lines:</b> lets a user annotate their
    /// own list ("# teammates" above a block of names) without those
    /// annotations being sent to the
    /// decoder as bias terms. A <c>#-</c> line is the one exception — see
    /// <see cref="ExcludedLinePrefix"/>.</para>
    /// <para><b>Dedupe case-insensitively, keep first-seen casing and
    /// order:</b> "Zupong" and "zupong" are the same bias term to the
    /// decoder, so keeping both wastes a slot under <see cref="MaxPhraseCount"/>
    /// for zero benefit. The user's own casing is kept — not upper-cased or
    /// title-cased — because a stylised term like "iPhone" or "eBay" has a
    /// casing that is itself part of what the user typed. Order is the order
    /// the user wrote the list in, so re-saving an unmodified file is a
    /// no-op rather than a silent reshuffle.</para>
    /// <para><b>An active line beats an exclusion marker for the same
    /// word.</b> Our own writer never emits both, so the only way to get both
    /// is a hand edit — and a person who types a word bare, on its own line,
    /// has said they want it. Resolving the other way would silently swallow a
    /// word they just typed, which is the unrecoverable direction: the
    /// exclusion is one click away either way, but a term that vanishes on
    /// save looks like the file is broken. The word keeps the POSITION of
    /// whichever line came first, because position is rank.</para>
    /// <para><b>Cap length and count:</b> see the remarks on
    /// <see cref="MaxPhraseLength"/>, <see cref="MaxPhraseCount"/> and
    /// <see cref="MaxExcludedCount"/> for the measured reasons a bound exists
    /// at all. An over-length line is dropped outright rather than truncated —
    /// a silently truncated phrase would bias toward text the user never
    /// wrote.</para>
    /// </remarks>
    public static ImmutableArray<DictationVocabularyEntry> ParseLines(IEnumerable<string> rawLines)
    {
        ArgumentNullException.ThrowIfNull(rawLines);

        var positionOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<DictationVocabularyEntry>();
        var activeCount = 0;
        var excludedCount = 0;

        foreach (var rawLine in rawLines)
        {
            if (rawLine is null)
            {
                continue;
            }

            // Composed to NFC before ANY identity question is asked of it.
            // The extractor composes its corpus (VocabularyTermExtractor
            // .NormaliseCorpus) and this store did not, so a bias_phrases.txt
            // holding "#- Zürich" with a DECOMPOSED ü — the shape a name
            // pasted out of Word or Outlook arrives in, which Save is
            // documented to keep verbatim — did not ordinal-match the composed
            // spelling a scan produces. The import then offered the word as a
            // fresh find, selected by default, and appended it as a SECOND
            // entry: two visually identical lines, one struck and one live,
            // with the word the user struck out biasing the decoder again.
            //
            // Here rather than at the three import-side comparisons because
            // this method is the single door Load, Save, Rename and the merge
            // all pass through, so composing once makes the store agree with
            // the extractor by construction. See VocabularyIdentity.
            var trimmed = VocabularyIdentity.Compose(rawLine.Trim());

            if (trimmed.Length == 0)
            {
                continue;
            }

            var isExcluded = false;
            var text = trimmed;

            if (trimmed[0] == '#')
            {
                if (!TryReadExcludedPhrase(trimmed, out text))
                {
                    // An ordinary prose comment. Not a phrase, not an
                    // exclusion, and not counted against either cap.
                    continue;
                }

                isExcluded = true;
            }

            if (text.Length == 0 || text.Length > MaxPhraseLength)
            {
                continue;
            }

            if (positionOf.TryGetValue(text, out var at))
            {
                var existing = builder[at];

                // The only duplicate worth acting on: a bare line for a word
                // an earlier marker had struck through. Promote it in place —
                // moving it to the end would change its rank, and its rank is
                // the size it draws at.
                if (!isExcluded && existing.IsExcluded && activeCount < MaxPhraseCount)
                {
                    builder[at] = existing with { IsExcluded = false };
                    excludedCount--;
                    activeCount++;
                }

                continue;
            }

            if (isExcluded)
            {
                if (excludedCount >= MaxExcludedCount)
                {
                    continue;
                }

                excludedCount++;
            }
            else
            {
                if (activeCount >= MaxPhraseCount)
                {
                    // Later lines in the file are simply not added — this is
                    // the "the first MaxPhraseCount terms in the file still
                    // bias, the rest do not" degradation described on
                    // MaxPhraseCount, not a hard failure.
                    continue;
                }

                activeCount++;
            }

            positionOf[text] = builder.Count;
            builder.Add(new DictationVocabularyEntry(text, isExcluded));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Read <c>"#- Trovioo"</c> as the excluded phrase <c>Trovioo</c>.
    /// </summary>
    /// <remarks>
    /// The whitespace requirement after the marker is what keeps a hand-typed
    /// rule line (<c>"#-----"</c>) an ordinary comment. Without it that line
    /// parses as an excluded phrase named <c>"-----"</c>, which then draws in
    /// the user's cloud struck through.
    /// </remarks>
    private static bool TryReadExcludedPhrase(string trimmedLine, out string phrase)
    {
        phrase = string.Empty;

        if (!trimmedLine.StartsWith(ExcludedLinePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = trimmedLine.AsSpan(ExcludedLinePrefix.Length);
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
        {
            return false;
        }

        phrase = rest.Trim().ToString();
        return phrase.Length > 0;
    }

    // ─── Editing ────────────────────────────────────────────────────────

    /// <summary>
    /// The sentence refusing a phrase the file format cannot carry, or
    /// <c>null</c> when it can: a leading <c>#</c> (read back as a prose
    /// comment or as an <see cref="ExcludedLinePrefix"/> marker for a
    /// DIFFERENT word) or any control character (a line break is two lines).
    /// </summary>
    /// <remarks>
    /// ONE predicate, shared by every path that writes a phrase the user did
    /// not type into the text box -- <see cref="Rename"/> and the learning
    /// consumer (CPL 6b). The consumer originally called <c>Save(entries)</c>
    /// directly and wrote <c>#- Trovioo</c>, which the parser read as Trovioo
    /// STRUCK THROUGH while the ledger said "boosted" (verification finding
    /// F1). A second copy of these two checks would drift from this one the
    /// next time the format grows a marker.
    /// </remarks>
    public static string? FormatRefusal(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text[0] == '#')
        {
            return "A word cannot start with # -- that marks a note in your list.";
        }
        if (text.Any(char.IsControl))
        {
            return "A word cannot contain a line break.";
        }
        return null;
    }

    /// <summary>
    /// Replace one term's spelling, keeping its position.
    /// </summary>
    /// <param name="entries">The list to edit. Not mutated.</param>
    /// <param name="term">The existing spelling, matched case-insensitively.</param>
    /// <param name="replacement">What the user typed instead.</param>
    /// <remarks>
    /// <para>
    /// <b>Position is preserved because position is rank and rank is size.</b>
    /// Removing and re-appending would send a corrected word to the bottom of
    /// the cloud and shrink it to the smallest tier — which reads as a
    /// punishment for fixing a typo. macOS makes the same point by replacing
    /// the entry in place so "the word keeps its size and position in the
    /// cloud" (<c>VocabularyImportModel.rename</c>).
    /// </para>
    /// <para>
    /// <b>Why rename exists at all:</b> the declared spelling is what the
    /// decoder is biased toward, so it has to be right here — a term the user
    /// cannot fix is a term they delete and retype, and on a list where
    /// position is rank that is not the same operation.
    /// </para>
    /// <para>
    /// <b>Refusals.</b> A blank replacement is nothing to rename to. An
    /// over-length one would be dropped silently by
    /// <see cref="ParseLines"/> on the next save, which looks exactly like
    /// data loss, so it is refused where the user can still see it. A
    /// replacement that collides with a DIFFERENT existing term is refused
    /// because it would spend two of the vocabulary's slots on one word;
    /// re-casing a term to itself ("zupong" → "Zupong") is not a collision and
    /// is allowed, which is the point of being able to rename at all.
    /// </para>
    /// <para>
    /// The excluded flag follows the new spelling. A struck-through word that
    /// is corrected stays struck through — renaming is not a decision about
    /// whether the word is in the vocabulary.
    /// </para>
    /// </remarks>
    public static DictationVocabularyEditResult Rename(
        IEnumerable<DictationVocabularyEntry> entries,
        string term,
        string replacement)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.Where(e => e is not null).ToImmutableArray();
        var from = VocabularyIdentity.Compose((term ?? string.Empty).Trim());

        // Composed BEFORE the four format guards below, not after them, and
        // that order is the whole of this line.
        //
        // ParseLines composes every line it reads, so the string this method
        // must not exceed MaxPhraseLength with is the COMPOSED one — and
        // composition can LENGTHEN a string. Measured against this file:
        // Rename(..., new string('a', 63) + "\u0958") produced to.Length = 64,
        // Refusal = null, Changed = true, and the very next Save -- which is
        // ParseLines(ToLines(entries)) -- returned an array of length ZERO,
        // because U+0958 has a composition-excluded singleton decomposition
        // that NFC expands 1 -> 2. The user renames their only word and one
        // save later the vocabulary is empty, with no refusal and nothing
        // logged: exactly the silent loss the length refusal exists to
        // prevent. Any Devanagari, Caseygali, Gurmukhi, Oriya or Tibetan nukta
        // letter, and every Hebrew presentation form, does this. The
        // asymmetry ran the other way too -- a 65-character decomposed name
        // that would have composed to 64 was refused as too long.
        //
        // It also closes the duplicate guard below, which compares against
        // entry texts ParseLines has already composed: renaming onto a
        // DECOMPOSED spelling of a word the user struck out passed the
        // ordinal-insensitive compare, and ParseLines' "a bare line beats an
        // exclusion marker" rule then promoted the struck entry back to
        // ACTIVE on the next save -- the exclusion reversed with no click, no
        // refusal and no sentence anywhere.
        //
        // Every other edit path composes already; this was the one left
        // un-composed. See VocabularyIdentity.
        var to = VocabularyIdentity.Compose((replacement ?? string.Empty).Trim());

        if (to.Length == 0)
        {
            return new DictationVocabularyEditResult(list, false, "A word cannot be blank.");
        }

        if (to.Length > MaxPhraseLength)
        {
            return new DictationVocabularyEditResult(
                list, false, $"A word can be at most {MaxPhraseLength} characters.");
        }

        // The two spellings the file format cannot carry. Both would be
        // accepted here, written by ToLines, and then read back by ParseLines
        // as something other than this word -- a leading '#' as a prose
        // comment (the word is gone) or as an ExcludedLinePrefix marker for a
        // DIFFERENT word, and an embedded line break as two separate phrases.
        // Refusing with a sentence is the whole difference between "that name
        // is not allowed" and a word that silently disappears one save after
        // the user corrected its spelling.
        if (FormatRefusal(to) is { } badFormat)
        {
            return new DictationVocabularyEditResult(list, false, badFormat);
        }

        var index = IndexOf(list, from);
        if (index < 0)
        {
            return new DictationVocabularyEditResult(list, false, $"'{from}' is not in the vocabulary.");
        }

        for (var i = 0; i < list.Length; i++)
        {
            if (i != index && string.Equals(list[i].Text, to, StringComparison.OrdinalIgnoreCase))
            {
                return new DictationVocabularyEditResult(
                    list, false, $"'{to}' is already in the vocabulary.");
            }
        }

        if (string.Equals(list[index].Text, to, StringComparison.Ordinal))
        {
            return new DictationVocabularyEditResult(list, false, null);
        }

        return new DictationVocabularyEditResult(
            list.SetItem(index, list[index] with { Text = to }), true, null);
    }

    /// <summary>
    /// Leave a term out, or put it back, without moving it.
    /// </summary>
    /// <remarks>
    /// The refusal on the excluded bound is the honest answer rather than
    /// striking the word through and letting the next
    /// <see cref="ParseLines"/> quietly drop it — a word that disappears one
    /// save after you struck it through is indistinguishable from a bug.
    /// </remarks>
    public static DictationVocabularyEditResult SetExcluded(
        IEnumerable<DictationVocabularyEntry> entries,
        string term,
        bool excluded)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.Where(e => e is not null).ToImmutableArray();
        var index = IndexOf(list, (term ?? string.Empty).Trim());
        if (index < 0)
        {
            return new DictationVocabularyEditResult(
                list, false, $"'{(term ?? string.Empty).Trim()}' is not in the vocabulary.");
        }

        if (list[index].IsExcluded == excluded)
        {
            return new DictationVocabularyEditResult(list, false, null);
        }

        if (excluded && list.Count(e => e.IsExcluded) >= MaxExcludedCount)
        {
            return new DictationVocabularyEditResult(
                list, false, $"Only {MaxExcludedCount} words can be left out at a time.");
        }

        if (!excluded && list.Count(e => !e.IsExcluded) >= MaxPhraseCount)
        {
            return new DictationVocabularyEditResult(
                list, false, $"Your vocabulary already has its {MaxPhraseCount} words.");
        }

        return new DictationVocabularyEditResult(
            list.SetItem(index, list[index] with { IsExcluded = excluded }), true, null);
    }

    /// <summary>
    /// Flip one term between left-out and included.
    /// </summary>
    /// <remarks>
    /// The cloud's corner control is one button that both excludes and
    /// restores (macOS: <c>xmark.circle.fill</c> becomes
    /// <c>arrow.uturn.backward.circle.fill</c>, "Leave this one out" becomes
    /// "Put it back"), so the model it drives is one call, not two.
    /// </remarks>
    public static DictationVocabularyEditResult ToggleExcluded(
        IEnumerable<DictationVocabularyEntry> entries,
        string term)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.Where(e => e is not null).ToImmutableArray();
        var index = IndexOf(list, (term ?? string.Empty).Trim());
        return index < 0
            ? new DictationVocabularyEditResult(
                list, false, $"'{(term ?? string.Empty).Trim()}' is not in the vocabulary.")
            : SetExcluded(list, list[index].Text, !list[index].IsExcluded);
    }

    /// <summary>
    /// Which entry the caller meant, by
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> over COMPOSED text.
    /// </summary>
    /// <remarks>
    /// Composed here because every entry in the array is: <see cref="Load"/>
    /// and <see cref="Save"/> both go through <see cref="ParseLines"/>, which
    /// composes, while the string a caller passes in came from a text field or
    /// a paste and did not. A decomposed "Zürich" typed at a composed "Zürich"
    /// is the same word and has to find it. See <see cref="VocabularyIdentity"/>.
    /// </remarks>
    private static int IndexOf(ImmutableArray<DictationVocabularyEntry> entries, string text)
    {
        text = VocabularyIdentity.Compose(text);

        if (text.Length == 0)
        {
            return -1;
        }

        for (var i = 0; i < entries.Length; i++)
        {
            if (string.Equals(entries[i].Text, text, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
