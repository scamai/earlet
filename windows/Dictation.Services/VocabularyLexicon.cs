using System.Collections.Frozen;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Dictation.Services;

/// <summary>
/// The two "we already know this word" questions the import asks.
/// </summary>
/// <remarks>
/// An interface so a test can pin the answers without a 236,000-word list, and
/// so the fail-open branch — the one nobody exercises by accident — can be
/// exercised on purpose.
/// </remarks>
public interface IVocabularyLexicon
{
    /// <summary>
    /// Is this an ordinary English word?
    /// </summary>
    /// <remarks>
    /// Case-insensitive, and inflection-aware to the extent described on
    /// <see cref="VocabularyLexicon"/>.
    /// </remarks>
    bool IsCommonWord(string token);

    /// <summary>
    /// Is this a programming-language or industry name a general recogniser
    /// already transcribes correctly?
    /// </summary>
    bool IsLanguageIdentifier(string token);
}

/// <summary>
/// The distinctiveness dictionary, shipped rather than borrowed.
/// </summary>
/// <remarks>
/// <para>
/// <b>macOS reads <c>/usr/share/dict/words</c>; Windows ships no word list at
/// any path, so this one carries its own.</b> The embedded resource is the
/// BSD <c>web2</c> list — Webster's Second International, 1934, whose copyright
/// has lapsed — gzipped to about 736 KB, with 334 product-name entries removed
/// from the 235,976-line source. macOS reads its own system copy. Shipping it
/// rather than calling the Win32 spell checker is
/// deliberate: <c>ISpellChecker</c> costs zero install bytes and handles
/// inflections natively, but its answer varies with installed language packs
/// and with the USER's own added-words dictionary, and
/// <c>IsSupported("en-US")</c> can return false on a stripped image. That makes
/// results machine-dependent and tests non-hermetic. A bundled list makes the
/// gate identical on every machine and identical to macOS, which is what makes
/// it testable at all and what stops two Windows boxes offering different terms
/// for the same folder.
/// </para>
/// <para>
/// <b>Fail-open is the direction, and it is deliberate.</b> If the resource
/// cannot be loaded, <see cref="IsCommonWord"/> answers false for everything —
/// every sentence-initial capital is KEPT and every acronym and proper term
/// counts as distinctive. An extra word in the list is visible and one click
/// from removal; a missing proper noun is the feature not working. This is the
/// branch nobody exercises by accident, so
/// <c>VocabularyLexiconTests</c> exercises it on purpose.
/// </para>
/// <para>
/// <b>The three-rule stemmer stays because <c>web2</c> stays.</b> web2 is
/// base-form sparse — it has "application" but not "Applications", "log" but
/// not "Logs" — and a bare lookup let <c>Applications</c>, <c>Logs</c>,
/// <c>Users</c>, <c>Resources</c> and <c>Defaults</c> through as if they were
/// user jargon, visible at the top of a scan. Those cases are regression
/// fixtures and have tests. A general stemmer was rejected because it can
/// fold proper names onto ordinary bases; these three rules are deliberately limited. If this ever ships a hunspell/SCOWL list with inflections
/// included, DELETE the stemmer with it — at that point it stops paying for
/// itself and starts folding proper names onto dictionary words.
/// </para>
/// </remarks>
public sealed class VocabularyLexicon : IVocabularyLexicon
{
    /// <summary>
    /// The embedded resource's logical name, fixed in the csproj so it does
    /// not follow the root namespace or the folder layout. Public so a
    /// diagnostic can say which resource it looked for rather than reporting
    /// an empty dictionary with no explanation.
    /// </summary>
    public const string WordListResourceName = "web2-words.txt.gz";

    private static readonly Lazy<VocabularyLexicon> SharedLexicon =
        new(() => new VocabularyLexicon(LoadEmbeddedWords()),
            LazyThreadSafetyMode.ExecutionAndPublication);

    // Learning has a different question from source import: a web2 entry that
    // exists only with an initial capital is usually a proper name. Keep the
    // full shared lexicon for import and expose a separate lazy view for the
    // learning wrapper below.
    private static readonly Lazy<VocabularyLexicon> SharedLearningLexicon =
        new(() => {
            var loaded = LoadEmbeddedLearningWords();
            return new VocabularyLexicon(loaded.Words, loaded.Excluded);
        },
            LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly FrozenSet<string> _words;
    private readonly FrozenSet<string> _learningExcluded;

    private VocabularyLexicon(FrozenSet<string> words,
                              FrozenSet<string>? learningExcluded = null)
    {
        _words = words;
        _learningExcluded = learningExcluded ?? FrozenSet<string>.Empty;
    }

    /// <summary>
    /// The shipped lexicon. The word list is read once, on first use, and then
    /// held for the process — an import is the only thing that asks for it, so
    /// an install that never imports never pays for it.
    /// </summary>
    public static VocabularyLexicon Shared => SharedLexicon.Value;

    /// <summary>
    /// The learning-only view of the same embedded web2 bytes. Entries whose
    /// raw spelling is capitalized are omitted; a lowercase counterpart is
    /// therefore retained naturally. The import lexicon above remains full.
    /// </summary>
    public static VocabularyLexicon SharedForLearning => SharedLearningLexicon.Value;

    /// <summary>
    /// A lexicon over an explicit word list. For tests, and for the day this
    /// ships a different list.
    /// </summary>
    /// <param name="words">
    /// Base forms. Lowercased on the way in, so the caller's casing does not
    /// matter and cannot silently disable the gate.
    /// </param>
    public static VocabularyLexicon Of(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        return new VocabularyLexicon(words
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim().ToLowerInvariant())
            .ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// A lexicon with no dictionary at all — the fail-open state, reachable in
    /// production only when the embedded resource cannot be read.
    /// </summary>
    public static VocabularyLexicon Empty { get; } =
        new(FrozenSet<string>.Empty);

    /// <summary>
    /// How many words the dictionary holds. Zero means the gate is open and
    /// everything capitalised is being kept.
    /// </summary>
    public int WordCount => _words.Count;

    /// <summary>
    /// Is this lowercase word a capitalized-only web2 entry the learning view
    /// leaves out? Always false for the import view and for explicit lists.
    /// </summary>
    public bool IsLearningExcludedName(string word) => _learningExcluded.Contains(word);

    /// <inheritdoc />
    public bool IsCommonWord(string token)
    {
        if (string.IsNullOrEmpty(token) || _words.Count == 0)
        {
            return false;
        }

        var word = token.ToLowerInvariant();

        // A capitalized-only web2 entry is deliberately absent from the
        // learning word set. Check this before inflection folding, otherwise
        // A capitalized-only proper name could otherwise match an ordinary
        // base via -ies -> -y.
        if (_learningExcluded.Contains(word))
        {
            return false;
        }

        if (_words.Contains(word))
        {
            return true;
        }

        // "libraries" -> "library". Checked before the two suffix drops
        // because "libraries" minus "es" is "librari", which is in no
        // dictionary, and the term would survive as somebody's jargon.
        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 3 &&
            _words.Contains(string.Concat(word.AsSpan(0, word.Length - 3), "y")))
        {
            return true;
        }

        // "boxes" -> "box".
        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length > 2 &&
            _words.Contains(word[..^2]))
        {
            return true;
        }

        // "logs" -> "log". Before the shouted rules, so "boxes" is not tested
        // as "boxe" first.
        if (word.EndsWith('s') && word.Length > 1 && _words.Contains(word[..^1]))
        {
            return true;
        }

        return IsShoutedInflection(token, word);
    }

    /// <summary>
    /// The wider stemmer, applied ONLY to a token with no lowercase letter in
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five words of licence boilerplate outranked every real name in a
    /// checkout, and the three-rule stemmer could not reach any of them.</b>
    /// <c>LooksLikeAcronym</c> stops at six characters, so every all-caps word
    /// of seven or more classifies Camel, and
    /// <see cref="VocabularyDistinctiveness"/> sends a camel token with no
    /// lowercase letter here. Measured end-to-end on a 400-file <c>.cs</c>
    /// checkout carrying a standard license header with three synthetic names in five
    /// of the files: the row read "400 files · 8 names" and the offer was
    /// <c>ARISING(400)</c>, <c>IMPLIED(400)</c>, <c>INCLUDING(400)</c>,
    /// <c>MERCHANTABILITY(400)</c>, <c>NONINFRINGEMENT(400)</c>, then
    /// <c>ElmTree(5)</c>, <c>Trovioo(5)</c>, <c>Zupong(5)</c>. web2 holds
    /// <c>arise</c>, <c>imply</c>, <c>include</c>, <c>merchantable</c> and
    /// <c>infringement</c>, and none of the five shouted forms — the base-form
    /// sparseness the existing three rules already exist to paper over, one
    /// suffix family further out.
    /// </para>
    /// <para>
    /// <b>Gated on "no lowercase letter", which is what makes widening the
    /// stemmer safe.</b> The standing warning on this class is that a real
    /// stemmer folds a proper name onto an ordinary base — i.e. that aggressive
    /// stemming destroys names. A name is written with its own casing, not all
    /// caps; this path never runs on a token carrying a lowercase
    /// letter, so it cannot reach one. What it can reach is a shouted word,
    /// and a shouted word is either boilerplate or an acronym — and an acronym
    /// does not end in <c>-ing</c>.
    /// </para>
    /// <para>
    /// The <c>NON</c> prefix is here for one measured word:
    /// <c>NONINFRINGEMENT</c> is absent from web2 in every form, while
    /// <c>infringement</c> is present. <c>non-</c> is a productive English
    /// prefix and stripping it re-asks the whole question, suffix rules
    /// included.
    /// </para>
    /// </remarks>
    private bool IsShoutedInflection(string token, string word)
    {
        foreach (var c in token)
        {
            if (char.IsLower(c))
            {
                return false;
            }
        }

        // "NONINFRINGEMENT" -> "infringement". Recursive rather than inline so
        // the suffix rules below apply to the stripped form too.
        if (word.Length > 6 && word.StartsWith("non", StringComparison.Ordinal) &&
            IsCommonWord(word[3..].ToUpperInvariant()))
        {
            return true;
        }

        // "INCLUDING" -> "include", "ARISING" -> "arise".
        if (word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 4 &&
            (_words.Contains(word[..^3]) || _words.Contains(word[..^3] + "e")))
        {
            return true;
        }

        // "IMPLIED" -> "imply", "GRANTED" -> "grant", "USED" -> "use".
        if (word.EndsWith("ied", StringComparison.Ordinal) && word.Length > 4 &&
            _words.Contains(word[..^3] + "y"))
        {
            return true;
        }

        if (word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 3 &&
            (_words.Contains(word[..^2]) || _words.Contains(word[..^1])))
        {
            return true;
        }

        // "MERCHANTABILITY" -> "merchantable".
        if (word.EndsWith("ability", StringComparison.Ordinal) && word.Length > 8 &&
            _words.Contains(word[..^7] + "able"))
        {
            return true;
        }

        if (word.EndsWith("ity", StringComparison.Ordinal) && word.Length > 4 &&
            (_words.Contains(word[..^3]) || _words.Contains(word[..^3] + "e")))
        {
            return true;
        }

        // "AGREEMENT" -> "agree".
        return word.EndsWith("ment", StringComparison.Ordinal) && word.Length > 5 &&
            (_words.Contains(word[..^4]) || _words.Contains(word[..^4] + "e"));
    }

    /// <inheritdoc />
    public bool IsLanguageIdentifier(string token) =>
        VocabularyLanguageIdentifiers.Contains(token);

    private static FrozenSet<string> LoadEmbeddedWords()
    {
        try
        {
            using var stream = typeof(VocabularyLexicon).Assembly
                .GetManifestResourceStream(WordListResourceName);

            if (stream is null)
            {
                return FrozenSet<string>.Empty;
            }

            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);

            var words = new HashSet<string>(240_000, StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                var word = line.Trim();
                if (word.Length > 0)
                {
                    words.Add(word.ToLowerInvariant());
                }
            }

            return words.ToFrozenSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Deliberately everything. A missing, truncated or corrupt resource
            // all mean the same thing to this feature — no dictionary — and the
            // fail-open answer is the safe one. Throwing here would take an
            // import down over a word list.
            return FrozenSet<string>.Empty;
        }
    }

    private static (FrozenSet<string> Words, FrozenSet<string> Excluded) LoadEmbeddedLearningWords()
    {
        try
        {
            using var stream = typeof(VocabularyLexicon).Assembly
                .GetManifestResourceStream(WordListResourceName);

            if (stream is null)
            {
                return (FrozenSet<string>.Empty, FrozenSet<string>.Empty);
            }

            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var raw = new List<string>(240_000);
            var lowercaseEntries = new HashSet<string>(240_000, StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                var word = line.Trim();
                if (word.Length == 0)
                {
                    continue;
                }

                raw.Add(word);
                if (word == word.ToLowerInvariant())
                {
                    lowercaseEntries.Add(word);
                }
            }

            var words = raw.Where(word => word == word.ToLowerInvariant())
                .Select(word => word.ToLowerInvariant())
                .ToFrozenSet(StringComparer.Ordinal);
            var excluded = raw.Where(word => word != word.ToLowerInvariant()
                                             && !lowercaseEntries.Contains(word.ToLowerInvariant()))
                .Select(word => word.ToLowerInvariant())
                .ToFrozenSet(StringComparer.Ordinal);
            return (words, excluded);
        }
        catch (Exception)
        {
            return (FrozenSet<string>.Empty, FrozenSet<string>.Empty);
        }
    }
}

/// <summary>
/// Names a general recogniser already gets right, so a scan that offers them
/// demonstrates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This list exists because scanning source code was added to a rules set
/// written for prose.</b> A repository is full of capitalised tokens that are
/// no user's private vocabulary: type names, keywords, framework names, and the
/// handful of company names every model on earth was trained on.
/// </para>
/// <para>
/// <b>The licence for an entry is narrow and falsifiable:</b> "a general
/// recogniser already transcribes this correctly, so it cannot demonstrate
/// anything". It is explicitly NOT "words we find unhelpful" — that reading
/// turns this into the long stoplist
/// <see cref="VocabularyStoplist"/> deliberately is not, and it starts deleting
/// the jargon the feature exists to find.
/// </para>
/// <para>
/// The .NET group is an addition to the macOS list, under the same licence and
/// for the same reason the Swift group is there: the repository source reads
/// <c>.cs</c> files, so this port's scans surface .NET type names exactly the
/// way macOS's surfaced Foundation's.
/// </para>
/// </remarks>
public static class VocabularyLanguageIdentifiers
{
    private static readonly FrozenSet<string> Identifiers = new[]
    {
        // Swift, Foundation, SwiftUI.
        "swift", "swiftui", "foundation", "combine", "uikit", "appkit",
        "xcode", "xctest", "codable", "decodable", "encodable", "sendable",
        "mainactor", "observableobject", "published", "binding", "optional",
        "struct", "enum", "protocol", "guard", "defer", "func", "var", "let",
        "vstack", "hstack", "zstack", "toggle", "picker", "slider", "navigation",

        // .NET, C#, WPF. Same licence, added because this port reads .cs.
        "csharp", "dotnet", "xaml", "wpf", "linq", "nuget", "msbuild",
        "roslyn", "xunit", "viewmodel", "namespace", "readonly", "nullable",
        "immutable", "task", "async", "await", "delegate", "interface",

        // Python, TypeScript, JavaScript and their ecosystems.
        "python", "javascript", "typescript", "react", "node", "npm", "yarn",
        "django", "flask", "numpy", "pandas", "pytorch", "tensorflow", "onnx",
        "lambda", "tuple", "dict", "none", "undefined", "const", "promise",
        "export", "import", "module", "require", "def", "elif", "iter",

        // Formats, protocols, wire words.
        "json", "yaml", "toml", "xml", "html", "css", "sql", "http", "https",
        "api", "rest", "graphql", "regex", "utf", "ascii", "uuid", "guid",
        "url", "uri", "sdk", "cli", "gui", "ide", "repo", "git",

        // Hardware and platform words every model knows.
        "cpu", "gpu", "npu", "ram", "usb", "wifi", "pdf", "png", "jpeg",

        // Companies and products every model knows.
        "google", "apple", "microsoft", "amazon", "meta", "github", "gitlab",
        "slack", "zoom", "teams", "outlook", "windows", "macos", "linux",
        "android", "chrome", "safari", "firefox", "docker", "kubernetes",
        "aws", "azure", "openai", "nvidia", "intel", "qualcomm", "snapdragon",
        "whisper",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many names the list holds.</summary>
    public static int Count => Identifiers.Count;

    /// <summary>
    /// Case-insensitive, which is what macOS's <c>token.lowercased()</c> check
    /// amounts to with an all-lowercase list.
    /// </summary>
    public static bool Contains(string token) =>
        !string.IsNullOrEmpty(token) && Identifiers.Contains(token);
}

/// <summary>
/// The product's whole claim, expressed as a predicate.
/// </summary>
/// <remarks>
/// <para>
/// A term is OFFERED only if it is distinctive. Everything else is still
/// ranked and still counted — nothing is discarded, only demoted — so ordinary
/// but real vocabulary like "Slack" and "Whisper" keeps its place in the
/// ranking, just below the words that make the point.
/// </para>
/// <para>
/// <b>Each kind gets a different test, and the differences are the argument.</b>
/// A domain skips the DICTIONARY: nothing else looks like one and no
/// recogniser spells one right, so an ordinary English word inside a host name
/// is still worth offering. It does not skip the identifier list — see
/// <see cref="VocabularyDistinctiveness"/> — because <c>github.com</c> is the
/// same non-answer <c>github</c> is. A camel-case token is checked only against the
/// identifier list, with no dictionary lookup, because an internal capital is
/// itself the evidence — "ElmTree" is not the English word "elmtree". An
/// acronym and a proper noun get both checks, because both can collide head-on
/// with ordinary English: "NOT" and "See" are exactly the false positives the
/// dictionary was added to kill.
/// </para>
/// </remarks>
public static class VocabularyDistinctiveness
{
    /// <summary>Would this term be worth offering?</summary>
    public static bool IsDistinctive(
        VocabularyTermKind kind,
        string text,
        IVocabularyLexicon lexicon)
    {
        ArgumentNullException.ThrowIfNull(lexicon);

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return kind switch
        {
            VocabularyTermKind.Domain => !lexicon.IsLanguageIdentifier(RegistrableLabel(text)),
            VocabularyTermKind.Camel when HasLowercase(text) => !lexicon.IsLanguageIdentifier(text),
            _ => !lexicon.IsCommonWord(text) && !lexicon.IsLanguageIdentifier(text),
        };
    }

    /// <summary>
    /// The label a host name is actually KNOWN by — the dot-part before the
    /// last one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A domain was unconditionally distinctive, and the identifier denylist
    /// could not reach it.</b> <c>github</c> and <c>google</c> are explicit
    /// <see cref="VocabularyLanguageIdentifiers"/> entries — a general
    /// recogniser spells both correctly and neither is worth a vocabulary slot
    /// — yet <c>github.com</c>, <c>google.com</c> and <c>docs.google.com</c>
    /// were all offered as distinctive. Measured on an ordinary plain-text
    /// email body, <c>docs.google.com</c>, <c>github.com</c> and
    /// <c>mailer.example.com</c> came back distinctive, and because
    /// <c>FoundInMail</c> is the second ranking key they outranked everything a
    /// code or documents scan had found.
    /// </para>
    /// <para>
    /// The last part is the top-level domain and carries no identity;
    /// <c>docs</c> in <c>docs.google.com</c> is a subdomain and carries none
    /// either. What a person means by "the domain" is the label in between, so
    /// that is the one the denylist is asked about.
    /// </para>
    /// <para>
    /// <b>The DICTIONARY is still not consulted</b>, which is the part of "a
    /// domain is always distinctive" that was right: nothing else looks like a
    /// host name and no recogniser spells one correctly, so an ordinary English
    /// word inside one — <c>orange.com</c>, <c>apple.co.uk</c> — is still worth
    /// offering. Only the "we already know this name" list applies.
    /// </para>
    /// </remarks>
    private static string RegistrableLabel(string text)
    {
        var lastDot = text.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return text;
        }

        var head = text[..lastDot];
        var previousDot = head.LastIndexOf('.');

        return previousDot < 0 ? head : head[(previousDot + 1)..];
    }

    /// <summary>
    /// Does this term contain a lowercase letter — i.e. is the internal capital
    /// actually evidence of anything?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A shouted English word is not camel-case, and treating it as such
    /// handed a licence header the top of every code scan.</b>
    /// <c>LooksLikeAcronym</c> stops at six characters, so every all-caps word
    /// of seven or more falls into the camel branch — and the camel branch is
    /// the one that skips the dictionary. Measured directly:
    /// <c>SOFTWARE</c>, <c>WITHOUT</c>, <c>PROVIDED</c>, <c>WARRANTY</c>,
    /// <c>WARRANTIES</c>, <c>EXPRESS</c>, <c>IMPLIED</c>, <c>INCLUDING</c>,
    /// <c>LIMITED</c>, <c>MERCHANTABILITY</c>, <c>FITNESS</c>,
    /// <c>PARTICULAR</c>, <c>PURPOSE</c>, <c>COPYRIGHT</c>, <c>LICENSE</c> all
    /// classified Camel and all returned distinctive, while
    /// <see cref="IVocabularyLexicon.IsCommonWord"/> answers true for every one
    /// of them.
    /// </para>
    /// <para>
    /// An MIT or Apache header sits at the top of every file in most
    /// checkouts, so on a 400-file scan these arrive with an occurrence count
    /// equal to the file count and dominate the ranking. macOS survived it
    /// against a 1,000-phrase budget; here 40 offered terms against
    /// <see cref="DictationVocabulary.MaxPhraseCount"/> means one import
    /// of a repository would hand most of the user's vocabulary to licence
    /// boilerplate.
    /// </para>
    /// <para>
    /// <b>The rule is the presence of a lowercase letter, not the length.</b>
    /// An internal capital is evidence only when there is a lowercase letter
    /// for it to be internal TO — <c>ElmTree</c> keeps its exemption,
    /// <c>SeeSaw</c> keeps its exemption, and <c>MAX_SIZE</c> survives the
    /// dictionary check it now gets because <c>max_size</c> is not a word.
    /// </para>
    /// </remarks>
    private static bool HasLowercase(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLower(c))
            {
                return true;
            }
        }

        return false;
    }
}
