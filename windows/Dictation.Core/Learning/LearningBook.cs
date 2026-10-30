namespace Dictation.Core.Learning;

/// <summary>Why a teach was refused, or that it was not (LEARNING.md §4.3).</summary>
public enum TeachResult
{
    Ok,

    /// <summary>Blank, or a misheard form shorter than 2 characters.</summary>
    Empty,

    /// <summary>More than 60 characters, 6 non-CJK tokens or 12 CJK characters.</summary>
    TooLong,

    /// <summary>The written form contains the misheard form.</summary>
    SelfContaining,

    /// <summary>500 taught words, or 200 aliases none of which is learned.</summary>
    Full,
}

/// <summary>What one observed correction did to the book.</summary>
/// <param name="Extract">The candidates and rejections of the correction.</param>
/// <param name="NewWords">Words learned for the first time.</param>
/// <param name="NewAliases">Heard keys that became aliases.</param>
public sealed record LearningBookObservation(
    CorrectionExtract Extract,
    IReadOnlyList<string> NewWords,
    IReadOnlyList<string> NewAliases);

/// <summary>A pending pair as the worker-facing projection shows it.</summary>
public sealed record PendingProjection(string Heard, string Meant, long Count);

/// <summary>
/// The state reduced to what the golden cases compare: the reference's
/// <c>projection()</c>.
/// </summary>
/// <param name="Aliases">Heard key → meant, in heard order, one per key.</param>
public sealed record LearningProjection(
    IReadOnlyList<string> Words,
    IReadOnlyList<KeyValuePair<string, string>> Aliases,
    IReadOnlyList<PendingProjection> Pending,
    IReadOnlyList<string> RemovedWords,
    IReadOnlyList<(string Heard, string Meant)> RemovedAliases);

/// <summary>
/// The learned vocabulary and the operations on it (LEARNING.md §4): a port of
/// the reference's <c>Learning</c> class, statement for statement, so the two
/// agree on every golden case and on the order of every list they write.
/// </summary>
/// <remarks>
/// Not thread-safe and does no IO; <see cref="DictationLearning"/> adds both.
/// Takes ownership of the state it is given and edits it in place.
/// </remarks>
public sealed class LearningBook
{
    private static readonly IComparer<string> Order = LearningText.CodePointOrder;

    private readonly ILearningLexicon _lexicon;

    public LearningBook(ILearningLexicon lexicon, LearningState? state = null)
    {
        _lexicon = lexicon ?? throw new ArgumentNullException(nameof(lexicon));
        State = state ?? new LearningState();
    }

    /// <summary>The state, as <see cref="LearningStore"/> writes it.</summary>
    public LearningState State { get; private set; }

    // ─── Operations ───

    /// <summary>
    /// Learn from one dictation (<paramref name="dictated"/>) and what the user
    /// made of it (<paramref name="edited"/>). <paramref name="dictation"/> is
    /// the random id of that insertion; the same id counts once.
    /// </summary>
    public LearningBookObservation Observe(string dictation, string dictated, string edited, long at, string? scene = null)
    {
        ArgumentNullException.ThrowIfNull(dictation);
        ArgumentNullException.ThrowIfNull(dictated);
        ArgumentNullException.ThrowIfNull(edited);
        Prune(at);
        var extract = LearningPolicy.Extract(dictated, edited, _lexicon);
        var adaptive = LearningEvidenceEngine.Revise(State, dictation, extract.Candidates, at, _lexicon, scene);
        Prune(at);
        return new LearningBookObservation(extract, adaptive.Words, adaptive.Aliases);
    }

    /// <summary>
    /// Teach a written form and, optionally, what Earlet writes for it now (the
    /// misheard form). The word is taught at once and a misheard form sets a
    /// taught alias at once.
    /// </summary>
    public TeachResult Teach(string written, string? soundsLike, long at)
    {
        ArgumentNullException.ThrowIfNull(written);
        written = LearningText.Strip(written);
        var sounds = LearningText.Strip(soundsLike ?? "");
        if (written.Length == 0)
        {
            return TeachResult.Empty;
        }

        var tokens = LearningTokens.Tokenize(written);
        if (tokens.Count == 0)
        {
            // "!!!" has no word in it: nothing to teach, not something too long.
            return TeachResult.Empty;
        }

        if (LearningText.Length(written) > LearningLimits.MaxTaughtChars
            || LearningTokens.LatinCount(tokens) > LearningLimits.MaxTaughtLatinTokens
            || LearningTokens.CjkCount(tokens) > LearningLimits.MaxTaughtCjkChars)
        {
            return TeachResult.TooLong;
        }

        Prune(at);
        var heard = sounds.Length > 0 ? LearningTokens.HeardKey(sounds) : null;
        if (heard is not null)
        {
            var soundsTooLong = LearningText.Length(sounds) > LearningLimits.MaxTaughtChars;
            if (LearningText.Length(heard) < 2 || soundsTooLong)
            {
                return soundsTooLong ? TeachResult.TooLong : TeachResult.Empty;
            }

            if (LearningTokens.ContainsTokens(written, sounds))
            {
                return TeachResult.SelfContaining;
            }
        }

        var key = LearningText.Lower(written);
        var word = FindWord(key);
        var taught = State.Words.Count(w => w.Origin == LearningOrigins.Taught);
        if (!(word is not null && word.Origin == LearningOrigins.Taught) && taught >= LearningLimits.MaxTaughtWords)
        {
            return TeachResult.Full;
        }

        if (heard is not null && FindAlias(heard) is null && State.Aliases.Count >= LearningLimits.MaxAliases
            && !State.Aliases.Any(a => a.Origin == LearningOrigins.Correction))
        {
            return TeachResult.Full;
        }

        UnremoveWord(key);
        if (word is not null)
        {
            word.Text = written;
            word.Origin = LearningOrigins.Taught;
            word.LastSeen = at;
        }
        else
        {
            State.Words.Add(new LearnedWord
            {
                Text = written, Origin = LearningOrigins.Taught, Count = 0, FirstSeen = at, LastSeen = at,
            });
        }

        if (heard is not null)
        {
            UnremoveAlias(heard, written);
            var alias = FindAlias(heard);
            if (alias is not null)
            {
                State.Aliases.Remove(alias);
            }

            State.Aliases.Add(new LearnedAlias
            {
                Heard = heard, Meant = written, Origin = LearningOrigins.Taught, Count = 0, FirstSeen = at, LastSeen = at,
            });
            State.Pending.RemoveAll(p => p.Heard == heard);
        }

        Prune(at);
        return TeachResult.Ok;
    }

    /// <summary>
    /// Remove a word, every alias that writes it and every pending pair that
    /// would, and tombstone the word and those aliases.
    /// </summary>
    public void RemoveWord(string text, long at)
    {
        ArgumentNullException.ThrowIfNull(text);
        var key = LearningText.Lower(LearningText.Strip(text));
        State.Words.RemoveAll(w => LearningText.Lower(w.Text) == key);
        TombstoneWord(key, at);
        foreach (var alias in State.Aliases.Where(a => LearningText.Lower(a.Meant) == key).ToList())
        {
            State.Aliases.Remove(alias);
            TombstoneAlias(alias.Heard, alias.Meant, at);
        }

        State.Pending.RemoveAll(p => LearningText.Lower(p.Meant) == key);
        Prune(at);
    }

    /// <summary>Remove one alias and tombstone it; its word stays.</summary>
    public void RemoveAlias(string heard, string meant, long at)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(meant);
        heard = LearningTokens.HeardKey(heard);
        var meantKey = LearningText.Lower(meant);
        State.Aliases.RemoveAll(a => a.Heard == heard && LearningText.Lower(a.Meant) == meantKey);
        TombstoneAlias(heard, meant, at);
        State.Pending.RemoveAll(p => p.Heard == heard && LearningText.Lower(p.Meant) == meantKey);
        Prune(at);
    }

    /// <summary>Forget everything, tombstones included.</summary>
    public void Clear() => State = new LearningState();

    /// <summary>Put back a whole earlier state: an undo of the last learned
    /// change (<see cref="DictationLearning.UndoLearning"/>), which keeps it.</summary>
    internal void Restore(LearningState state) => State = state ?? throw new ArgumentNullException(nameof(state));

    /// <summary>What the worker reads, in canonical order (the reference's
    /// <c>projection()</c>, which also canonicalises the state).</summary>
    public LearningProjection Projection()
    {
        Canonicalise();
        var aliases = new List<KeyValuePair<string, string>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var alias in State.Aliases)
        {
            if (index.TryGetValue(alias.Heard, out var at))
            {
                aliases[at] = new(alias.Heard, alias.Meant);
            }
            else
            {
                index[alias.Heard] = aliases.Count;
                aliases.Add(new(alias.Heard, alias.Meant));
            }
        }

        return new LearningProjection(
            State.Words.Select(w => w.Text).ToList(),
            aliases,
            State.Pending.Select(p => new PendingProjection(p.Heard, p.Meant, p.Count)).ToList(),
            State.Removed.Words.Select(r => r.Key).ToList(),
            State.Removed.Aliases.Select(r => (r.Heard, r.Meant)).ToList());
    }

    // ─── Lookups ───

    private LearnedWord? FindWord(string key) =>
        State.Words.FirstOrDefault(w => LearningText.Lower(w.Text) == key);

    private LearnedAlias? FindAlias(string heard) => State.Aliases.FirstOrDefault(a => a.Heard == heard);

    private PendingAlias? FindPending(string heard, string meant) =>
        State.Pending.FirstOrDefault(p => p.Heard == heard && p.Meant == meant);

    private bool WordRemoved(string key) => State.Removed.Words.Any(r => r.Key == key);

    private bool AliasRemoved(string heard, string meant)
    {
        var meantKey = LearningText.Lower(meant);
        return State.Removed.Aliases.Any(r => r.Heard == heard && r.Meant == meantKey);
    }

    private void UnremoveWord(string key) => State.Removed.Words.RemoveAll(r => r.Key == key);

    private void UnremoveAlias(string heard, string meant)
    {
        var meantKey = LearningText.Lower(meant);
        State.Removed.Aliases.RemoveAll(r => r.Heard == heard && r.Meant == meantKey);
    }

    private void TombstoneWord(string key, long at)
    {
        if (!WordRemoved(key))
        {
            State.Removed.Words.Add(new RemovedWord { Key = key, At = at });
        }
    }

    private void TombstoneAlias(string heard, string meant, long at)
    {
        if (!AliasRemoved(heard, meant))
        {
            State.Removed.Aliases.Add(new RemovedAlias { Heard = heard, Meant = LearningText.Lower(meant), At = at });
        }
    }

    // ─── Housekeeping (§4.5) ───

    private void Prune(long at)
    {
        var s = State;
        s.Pending = s.Pending
            .Where(p => p.LastSeen >= at - LearningLimits.PendingTtlMs)
            .OrderByDescending(p => p.LastSeen)
            .ThenBy(p => p.Heard, Order)
            .ThenBy(p => p.Meant, Order)
            .Take(LearningLimits.MaxPending)
            .ToList();

        var learned = s.Words
            .Where(w => w.Origin == LearningOrigins.Correction)
            .OrderByDescending(w => w.LastSeen)
            .ThenBy(w => LearningText.Lower(w.Text), Order)
            .ToList();
        foreach (var word in learned.Skip(LearningLimits.MaxLearnedWords))
        {
            s.Words.Remove(word);
        }

        while (s.Aliases.Count > LearningLimits.MaxAliases)
        {
            LearnedAlias? oldest = null;
            foreach (var alias in s.Aliases)
            {
                if (alias.Origin == LearningOrigins.Correction
                    && (oldest is null || alias.LastSeen < oldest.LastSeen
                        || (alias.LastSeen == oldest.LastSeen && Order.Compare(alias.Heard, oldest.Heard) < 0)))
                {
                    oldest = alias;
                }
            }

            if (oldest is null)
            {
                break;
            }

            s.Aliases.Remove(oldest);
        }

        s.Removed.Words = s.Removed.Words.OrderByDescending(r => r.At).Take(LearningLimits.MaxTombstones).ToList();
        s.Removed.Aliases = s.Removed.Aliases.OrderByDescending(r => r.At).Take(LearningLimits.MaxTombstones).ToList();
        Canonicalise();
    }

    private void Canonicalise()
    {
        var s = State;
        s.Words = s.Words.OrderByDescending(w => w.LastSeen).ThenBy(w => LearningText.Lower(w.Text), Order).ToList();
        s.Aliases = s.Aliases.OrderBy(a => a.Heard, Order).ToList();
        s.Pending = s.Pending.OrderBy(p => p.Heard, Order).ThenBy(p => p.Meant, Order).ToList();
        s.Removed.Words = s.Removed.Words.OrderBy(r => r.Key, Order).ToList();
        s.Removed.Aliases = s.Removed.Aliases.OrderBy(r => r.Heard, Order).ThenBy(r => r.Meant, Order).ToList();
    }
}
