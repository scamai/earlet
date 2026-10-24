namespace Dictation.Core.Learning;

/// <summary>The numbers in LEARNING.md, in one place.</summary>
public static class LearningLimits
{
    /// <summary>The file's schema tag (§5).</summary>
    public const string Schema = "dictation.learning/1";

    /// <summary>Either side of a dictation with more tokens is not diffed (§3.2).</summary>
    public const int MaxTextTokens = 200;

    /// <summary>A dictation this long that keeps fewer than half its tokens
    /// was rewritten, not corrected (§3.2).</summary>
    public const int RewriteMinTokens = 4;

    /// <summary>Non-CJK tokens a correction may have on either side (§3.3).</summary>
    public const int MaxLatinTokens = 4;

    /// <summary>CJK characters a correction may have on either side (§3.3).</summary>
    public const int MaxCjkChars = 8;

    /// <summary>Above this share of the longer squashed form, a change is a
    /// rewording (§3.3).</summary>
    public const double MaxDistanceRatio = 0.5;

    /// <summary>Distinct dictations a pair needs before it is an alias (§4.2).</summary>
    public const int AliasPromotionCount = 2;

    /// <summary>Dictation ids a pending pair remembers.</summary>
    public const int PendingDictationIds = 4;

    /// <summary>A pending pair expires this long after it was last seen (§4.2).</summary>
    public const long PendingTtlMs = 30L * 24 * 60 * 60 * 1000;

    public const int MaxPending = 500;
    public const int MaxLearnedWords = 100;
    public const int MaxTaughtWords = 500;
    public const int MaxAliases = 200;
    public const int MaxTombstones = 1000;

    /// <summary>Longest written or sounds-like form the user may teach (§4.3).</summary>
    public const int MaxTaughtChars = 60;

    public const int MaxTaughtLatinTokens = 6;
    public const int MaxTaughtCjkChars = 12;

    /// <summary>A token this long, with <see cref="SecretMinDigits"/> digits, is
    /// secret-shaped (§4.1).</summary>
    public const int SecretMinLength = 8;

    public const int SecretMinDigits = 2;
}

/// <summary>What kind of correction a hunk is (§3.3).</summary>
public enum CorrectionKind
{
    Casing,
    Respacing,
    Spelling,
    Transliteration,
}

/// <summary>Why a hunk is not a correction (§3.3).</summary>
public enum RejectionReason
{
    TooLong,
    Punctuation,
    Inflection,
    Rewording,

    /// <summary>The heard side has CJK and the meant side is only lowercase
    /// ASCII letters, apostrophes and spaces: pinyin or romaji an input method
    /// has not committed yet.</summary>
    Composition,

    /// <summary>The hunk reaches an end of both texts and grows there: text
    /// typed straight after (or before) the span, which cannot be told from a
    /// fix (§3.2).</summary>
    Boundary,
}

/// <summary>A correction the rules accept: <paramref name="Heard"/> was
/// dictated, <paramref name="Meant"/> is what the user made of it.</summary>
public sealed record CorrectionCandidate(string Heard, string Meant, CorrectionKind Kind);

/// <summary>A change the rules do not learn from.</summary>
public sealed record CorrectionRejection(string Heard, string Meant, RejectionReason Reason);

/// <summary>One hunk classified: exactly one of <see cref="Candidate"/> and
/// <see cref="Rejection"/> is set.</summary>
public sealed record CorrectionVerdict(CorrectionCandidate? Candidate, CorrectionRejection? Rejection);

/// <summary>Everything one dictation yields, in text order, without repeats.</summary>
public sealed record CorrectionExtract(
    IReadOnlyList<CorrectionCandidate> Candidates,
    IReadOnlyList<CorrectionRejection> Rejections)
{
    public static CorrectionExtract None { get; } = new([], []);
}

/// <summary>The wire names the file, the golden cases and the reference use.</summary>
public static class LearningWireNames
{
    public static string Of(CorrectionKind kind) => kind switch
    {
        CorrectionKind.Casing => "casing",
        CorrectionKind.Respacing => "respacing",
        CorrectionKind.Spelling => "spelling",
        CorrectionKind.Transliteration => "transliteration",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(RejectionReason reason) => reason switch
    {
        RejectionReason.TooLong => "too_long",
        RejectionReason.Punctuation => "punctuation",
        RejectionReason.Inflection => "inflection",
        RejectionReason.Rewording => "rewording",
        RejectionReason.Composition => "composition",
        RejectionReason.Boundary => "boundary",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

/// <summary>
/// From a correction to candidates (LEARNING.md §3), and what a candidate may
/// teach (§4.1, §4.2). Pure functions; a port of
/// <c>tests/learning_reference.py</c>, held to the same golden cases.
/// </summary>
public static class LearningPolicy
{
    // ─── Diff (§3.2) ───

    /// <summary>Candidates and rejections for one dictation, in text order.</summary>
    public static CorrectionExtract Extract(string dictated, string edited, ILearningLexicon lexicon)
    {
        ArgumentNullException.ThrowIfNull(dictated);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(lexicon);

        var a = LearningTokens.Tokenize(dictated);
        var b = LearningTokens.Tokenize(edited);
        if (a.Count == 0 || a.Count > LearningLimits.MaxTextTokens || b.Count > LearningLimits.MaxTextTokens)
        {
            return CorrectionExtract.None;
        }

        var (matches, hunks) = Hunks(a.Select(t => t.Text).ToArray(), b.Select(t => t.Text).ToArray());
        if (a.Count >= LearningLimits.RewriteMinTokens && matches * 2 < a.Count)
        {
            return CorrectionExtract.None;
        }

        var candidates = new List<CorrectionCandidate>();
        var rejections = new List<CorrectionRejection>();
        foreach (var (dels, ins) in hunks)
        {
            if (dels.Count == 0 || ins.Count == 0)
            {
                continue;
            }

            var heard = LearningTokens.Slice(dictated, a[dels[0]], a[dels[^1]]);
            var meant = LearningTokens.Slice(edited, b[ins[0]], b[ins[^1]]);
            // Text typed straight after (or before) the span joins the last
            // (first) hunk: "Call trovio" -> "Call Trovioo now". A hunk that
            // reaches an end of both texts and grows there cannot be told from
            // a continuation, so it is refused rather than learned as
            // "Trovioo now".
            var grows = ins.Count > dels.Count;
            var atEnd = dels[^1] == a.Count - 1 && ins[^1] == b.Count - 1;
            var atStart = dels[0] == 0 && ins[0] == 0;
            var verdict = grows && (atEnd || atStart)
                ? new CorrectionVerdict(null, new CorrectionRejection(heard, meant, RejectionReason.Boundary))
                : Classify(heard, meant, lexicon);
            if (verdict.Candidate is { } candidate && !candidates.Contains(candidate))
            {
                candidates.Add(candidate);
            }
            else if (verdict.Rejection is { } rejection && !rejections.Contains(rejection))
            {
                rejections.Add(rejection);
            }
        }

        return new CorrectionExtract(candidates, rejections);
    }

    /// <summary>
    /// Longest common subsequence of the token texts, compared exactly, and the
    /// maximal runs of non-matching tokens between matches. On a mismatch the
    /// dictated token is dropped when that keeps the LCS at least as long as
    /// dropping the edited one.
    /// </summary>
    private static (int Matches, List<(List<int> Dels, List<int> Ins)> Hunks) Hunks(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var hunks = new List<(List<int>, List<int>)>();
        var dels = new List<int>();
        var ins = new List<int>();

        void Flush()
        {
            if (dels.Count > 0 || ins.Count > 0)
            {
                hunks.Add((dels, ins));
                dels = [];
                ins = [];
            }
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                Flush();
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                dels.Add(x++);
            }
            else
            {
                ins.Add(y++);
            }
        }

        for (; x < n; x++)
        {
            dels.Add(x);
        }

        for (; y < m; y++)
        {
            ins.Add(y);
        }

        Flush();
        return (table[0, 0], hunks);
    }

    // ─── Classifying a correction (§3.3, first match wins) ───

    public static CorrectionVerdict Classify(string heard, string meant, ILearningLexicon lexicon)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(meant);
        ArgumentNullException.ThrowIfNull(lexicon);

        var ht = LearningTokens.Tokenize(heard);
        var mt = LearningTokens.Tokenize(meant);
        if (LearningTokens.LatinCount(ht) > LearningLimits.MaxLatinTokens
            || LearningTokens.LatinCount(mt) > LearningLimits.MaxLatinTokens
            || LearningTokens.CjkCount(ht) > LearningLimits.MaxCjkChars
            || LearningTokens.CjkCount(mt) > LearningLimits.MaxCjkChars)
        {
            return Reject(RejectionReason.TooLong);
        }

        if (LearningText.Lower(heard) == LearningText.Lower(meant))
        {
            return Accept(CorrectionKind.Casing);
        }

        if (LearningTokens.NormSpace(heard) == LearningTokens.NormSpace(meant))
        {
            return Reject(RejectionReason.Punctuation);
        }

        var sh = LearningTokens.Squash(heard);
        var sm = LearningTokens.Squash(meant);
        if (sh == sm)
        {
            return Accept(CorrectionKind.Respacing);
        }

        var shLength = LearningText.Length(sh);
        var smLength = LearningText.Length(sm);
        var shorter = shLength <= smLength ? ht : mt;
        var inflected = sm == sh + "s" || sm == sh + "es" || sh == sm + "s" || sh == sm + "es";
        if (inflected && shorter.Where(t => !t.IsCjk).All(t => lexicon.IsOrdinary(t.Text)))
        {
            return Reject(RejectionReason.Inflection);
        }

        var heardCjk = ht.Any(t => t.IsCjk);
        if (heardCjk != mt.Any(t => t.IsCjk))
        {
            return heardCjk && LooksLikeComposition(meant)
                ? Reject(RejectionReason.Composition)
                : Accept(CorrectionKind.Transliteration);
        }

        var longer = Math.Max(shLength, smLength);
        if (longer > 0 && (double)LearningTokens.Levenshtein(sh, sm) / longer > LearningLimits.MaxDistanceRatio)
        {
            // Ordinary-word substitutions can hide an unfamiliar technical
            // word (wrapper -> repo). Learn that word; LearnsAlias still
            // rejects the ordinary heard token, so no blanket rewrite follows.
            if (ht.Count == 1 && mt.Count == 1 && !ht[0].IsCjk && !mt[0].IsCjk
                && lexicon.IsOrdinary(ht[0].Text) && IsDistinctive(meant, lexicon))
            {
                return Accept(CorrectionKind.Spelling);
            }
            return Reject(RejectionReason.Rewording);
        }

        return Accept(CorrectionKind.Spelling);

        CorrectionVerdict Accept(CorrectionKind kind) => new(new CorrectionCandidate(heard, meant, kind), null);
        CorrectionVerdict Reject(RejectionReason reason) => new(null, new CorrectionRejection(heard, meant, reason));
    }

    // ─── Admission (§2, §4.1, §4.2) ───

    /// <summary>
    /// Does at least one non-CJK token have an internal capital next to a
    /// lowercase letter, mix letters and digits, or fall outside the lexicon?
    /// </summary>
    public static bool IsDistinctive(string text, ILearningLexicon lexicon)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lexicon);
        return LearningTokens.Tokenize(text)
            .Where(t => !t.IsCjk)
            .Any(t => HasInternalUpper(t.Text) || MixesDigitsAndLetters(t.Text) || !lexicon.IsOrdinary(t.Text));
    }

    /// <summary>
    /// Could <paramref name="text"/> be a password or key typed over a dictated
    /// word? A token of at least 8 code points with at least 2 digits
    /// (<c>Sunshine42</c>, or the account number in <c>Miles 44171234</c>),
    /// or two non-CJK tokens glued together, with no space between them, by
    /// anything but <c>' ’ - . _</c> (<c>p@ss</c>, <c>AT&amp;T</c>). Such a word
    /// waits for a second dictation (§4.1); <c>GPT4</c>, <c>M4</c> and
    /// <c>Hunter2</c> are short enough to be learned at once.
    /// </summary>
    public static bool IsSecretShaped(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = LearningTokens.Tokenize(text);
        if (tokens.Any(t => IsLongWithDigits(t.Text)))
        {
            return true;
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            var (left, right) = (tokens[i - 1], tokens[i]);
            if (left.IsCjk || right.IsCjk)
            {
                continue;
            }

            var gap = LearningText.CodePoints(text[left.Utf16End..right.Utf16Start]);
            if (!gap.Any(LearningText.IsSpace) && gap.Any(c => !LearningText.IsJoiner(c)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Does this candidate teach its meant text as a word (§4.1)?</summary>
    public static bool LearnsWord(CorrectionCandidate candidate, ILearningLexicon lexicon)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IsDistinctive(candidate.Meant, lexicon);
    }

    /// <summary>
    /// Do the two texts differ, ignoring case, only by <c>s</c>, <c>es</c>, <c>ed</c> or <c>ing</c>
    /// added to the end of one (<c>ies</c>/<c>ied</c> for a final <c>y</c>)? (§4.2)
    /// </summary>
    internal static bool IsInflectionPair(string a, string b)
    {
        var first = LearningText.Lower(a);
        var second = LearningText.Lower(b);
        var (shortText, longText) = LearningText.Length(first) <= LearningText.Length(second) ? (first, second) : (second, first);
        if (shortText.Length == 0)
        {
            return false;
        }

        if (new[] { "s", "es", "ed", "ing" }.Any(e => longText == shortText + e))
        {
            return true;
        }

        return shortText.EndsWith('y') && new[] { "ies", "ied" }.Any(e => longText == shortText[..^1] + e);
    }

    /// <summary>May this candidate become a <c>heard → meant</c> alias (§4.2)?</summary>
    public static bool LearnsAlias(CorrectionCandidate candidate, ILearningLexicon lexicon)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(lexicon);
        if (IsInflectionPair(candidate.Heard, candidate.Meant))
        {
            return false; // a plural or verb form is never a rewrite (§4.2)
        }

        if (candidate.Kind == CorrectionKind.Casing
            || LearningText.Length(LearningTokens.HeardKey(candidate.Heard)) < 2
            || LearningTokens.ContainsTokens(candidate.Meant, candidate.Heard))
        {
            return false;
        }

        if (!LearningText.HasLetter(candidate.Meant))
        {
            return false; // a number is never a rewrite target (§4.2)
        }

        if (candidate.Kind == CorrectionKind.Respacing)
        {
            return IsDistinctive(candidate.Meant, lexicon);
        }

        var tokens = LearningTokens.Tokenize(candidate.Heard);
        if (tokens.Any(t => t.IsCjk))
        {
            return true;
        }

        if (tokens.Count == 1)
        {
            return !lexicon.IsOrdinary(tokens[0].Text);
        }

        return tokens.Any(t => IsSingleLetter(t.Text) || !lexicon.IsOrdinary(t.Text));
    }

    /// <summary>
    /// Lowercase ASCII letters, apostrophes and spaces only: what an input
    /// method shows (pinyin, romaji) before the user commits the characters.
    /// </summary>
    private static bool LooksLikeComposition(string text) =>
        LearningText.HasLetter(text) && text.All(c => c is >= 'a' and <= 'z' or '\'' or ' ');

    private static bool HasInternalUpper(string text)
    {
        var points = LearningText.CodePoints(text);
        return points.Any(LearningText.IsLowercase) && points.Skip(1).Any(LearningText.IsUppercase);
    }

    private static bool MixesDigitsAndLetters(string text)
    {
        var points = LearningText.CodePoints(text);
        return points.Any(LearningText.IsDigit) && points.Any(LearningText.IsLetter);
    }

    private static bool IsLongWithDigits(string text)
    {
        var points = LearningText.CodePoints(text);
        return points.Length >= LearningLimits.SecretMinLength
            && points.Count(LearningText.IsDigit) >= LearningLimits.SecretMinDigits;
    }

    private static bool IsSingleLetter(string text)
    {
        var points = LearningText.CodePoints(text);
        return points.Length == 1 && LearningText.IsLetter(points[0]);
    }
}
