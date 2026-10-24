using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Learning;

/// <summary>What an operation did to <c>learning.json</c>.</summary>
public enum LearningWrite
{
    /// <summary>Nothing changed that the file does not already hold.</summary>
    Unchanged,

    /// <summary>The change is on disk.</summary>
    Saved,

    /// <summary>The change is in use for this run but not on disk: the disk
    /// refused, or the file could not be read. The next change, or
    /// <see cref="DictationLearning.Flush"/>, tries again.</summary>
    NotSaved,
}

/// <summary>What one observed correction taught: counts only, never words.</summary>
/// <param name="Candidates">Corrections the rules accepted.</param>
/// <param name="Rejections">Changes the rules did not learn from.</param>
/// <param name="NewWords">Words learned for the first time.</param>
/// <param name="NewAliases">Aliases that became active.</param>
/// <param name="PendingCorrections">Pairs this dictation's correction left
/// waiting for a second matching dictation: the pending entries whose
/// dictation ids include this one, after the change. Not a candidate count
/// and not "the file changed", so a notice built on it never claims a word
/// is waiting when none is (docs/design-macos.md, Correction notices).</param>
/// <param name="UndoToken">Set only when this correction saved something new
/// (<see cref="LearningWrite.Saved"/> with a new word or alias): what
/// <see cref="DictationLearning.UndoLearning"/> takes to put the vocabulary
/// back as it was just before. The next change to the vocabulary expires it.
/// Random, never derived from text.</param>
public sealed record LearningObservation(
    int Candidates, int Rejections, int NewWords, int NewAliases, LearningWrite Write, int PendingCorrections,
    Guid? UndoToken = null);

/// <summary>Where an entry came from, for the app window.</summary>
public enum LearningOrigin
{
    /// <summary>Learned from a correction.</summary>
    Correction,

    /// <summary>Taught by the user.</summary>
    Taught,

    /// <summary>An origin this version does not know (a newer app wrote it).</summary>
    Other,
}

/// <summary>A word in the vocabulary, as the app window lists it.</summary>
public sealed record LearnedWordEntry(string Text, LearningOrigin Origin, long Count, DateTimeOffset LastSeen,
    string Confidence = "Learned from your fix");

/// <summary>An alias, as the app window lists it: <paramref name="Heard"/> is
/// rewritten to <paramref name="Meant"/>.</summary>
public sealed record LearnedAliasEntry(string Heard, string Meant, LearningOrigin Origin);

/// <summary>Everything the app window shows about learning.</summary>
/// <param name="Words">Most recently seen first.</param>
/// <param name="Aliases">By heard form.</param>
/// <param name="Pending">Corrections waiting for a second dictation.</param>
/// <param name="File">What the last load found on disk.</param>
/// <param name="Unsaved">The list shown is not what is on disk: a save failed,
/// or the file could not be read. The Vocabulary section says so while it is
/// set (LEARNING.md §5).</param>
public sealed record LearningSnapshot(
    IReadOnlyList<LearnedWordEntry> Words,
    IReadOnlyList<LearnedAliasEntry> Aliases,
    int Pending,
    LearningFileStatus File,
    bool Unsaved);

/// <summary>
/// Automatic vocabulary learning for the Windows app (LEARNING.md): the
/// correction observer hands it what was dictated and what the user made of
/// it, the app window lists, teaches and removes entries, and every change
/// lands in <c>learning.json</c> for the worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-safe.</b> Every member may be called from any thread; operations
/// run one at a time, and each change is written before the next operation
/// starts, so the file never goes back to an older state. <see cref="Changed"/>
/// is raised after the lock is released, on the thread that made the change.
/// </para>
/// <para>
/// <b>Loaded on first use.</b> A bad file is kept aside as
/// <c>learning.json.bad</c> and learning starts empty (see
/// <see cref="LearningStore"/>).
/// </para>
/// <para>
/// <b>An unreadable file is kept, and so is the session.</b> A file that could
/// not be read is never overwritten. Learning goes on in memory: every later
/// change reads it again, and once that succeeds the changes made in the
/// meantime are replayed, in order and at their own times, onto what the file
/// holds, and the result is saved (LEARNING.md §5). A retry that still cannot
/// read the file keeps the session as it is.
/// </para>
/// <para>
/// <b>A last save at quit.</b> The host calls <see cref="Flush"/> when it
/// quits, so a change whose save failed is tried once more.
/// </para>
/// <para>
/// <b>One learned change can be undone.</b> A correction that saved a new
/// word or alias carries an undo token, and the vocabulary just before it is
/// kept (one previous state, never a transcript).
/// <see cref="UndoLearning"/> puts that state back, replaced aliases and
/// counts included. Any later change to the vocabulary — Add, Remove, Forget
/// all, another learned correction — expires the token, so an undo never
/// overwrites a newer edit; an observation that changed nothing does not.
/// The check and the restore happen under the same lock as every change.
/// </para>
/// <para>
/// <b>Counts, never words.</b> The log says what an operation did and how many
/// entries it touched. Dictated text, field contents and learned words stay out
/// of it.
/// </para>
/// </remarks>
public sealed class DictationLearning
{
    private readonly Lock _gate = new();
    private readonly LearningStore _store;
    private readonly ILearningLexicon _lexicon;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>Changes made while the file could not be read, for the load
    /// that finally reads it to replay.</summary>
    private readonly List<Action<LearningBook>> _whileUnreadable = [];

    private LearningBook? _book;
    private LearningFileStatus _file;
    private string _current = "";
    private string? _saved;

    /// <summary>The newest learned change that can be undone: its token and
    /// the state just before it, as the file would hold it. Cleared by every
    /// change after it.</summary>
    private (Guid Token, string Before)? _undo;

    public DictationLearning(
        LearningStore store,
        ILearningLexicon lexicon,
        TimeProvider? clock = null,
        ILogger<DictationLearning>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lexicon = lexicon ?? throw new ArgumentNullException(nameof(lexicon));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<DictationLearning>.Instance;
    }

    /// <summary>The app's learning: this user's file and the shipped word list.</summary>
    public static DictationLearning ForUser(DictationPaths paths, ILoggerFactory? logs = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new DictationLearning(
            LearningStore.ForUser(paths, logs?.CreateLogger<LearningStore>()),
            WebsterLexicon.Shared,
            TimeProvider.System,
            logs?.CreateLogger<DictationLearning>());
    }

    /// <summary>The learned words or aliases changed.</summary>
    public event EventHandler? Changed;

    // ─── Operations ───

    /// <summary>
    /// Learn from one confirmed insertion: <paramref name="dictated"/> is the
    /// text that was pasted, <paramref name="edited"/> the final text of that
    /// span, <paramref name="dictationId"/> a random id for the insertion.
    /// </summary>
    public LearningObservation Observe(string dictationId, string dictated, string edited, DateTimeOffset at, string? scene = null)
    {
        ArgumentNullException.ThrowIfNull(dictationId);
        ArgumentNullException.ThrowIfNull(dictated);
        ArgumentNullException.ThrowIfNull(edited);

        LearningBookObservation? result = null;
        var pending = 0;
        var write = Mutate(
            book =>
            {
                result = book.Observe(dictationId, dictated, edited, at.ToUnixTimeMilliseconds(), LearningScene.Normalize(scene));
                pending = book.State.Pending.Count(p => p.Dictations.Contains(dictationId, StringComparer.Ordinal));
            },
            // Only something new that is on disk: a pending pair is not a word
            // yet, and a receipt for a change the disk refused would be untrue.
            saved => saved == LearningWrite.Saved && result!.NewWords.Count + result.NewAliases.Count > 0,
            out var undo);
        var outcome = new LearningObservation(
            result!.Extract.Candidates.Count,
            result.Extract.Rejections.Count,
            result.NewWords.Count,
            result.NewAliases.Count,
            write,
            pending,
            undo);

        if (outcome.Candidates + outcome.Rejections > 0)
        {
            _log.LogInformation(
                "Correction observed: {Candidates} accepted, {Rejections} rejected, {NewWords} new words, " +
                "{NewAliases} new aliases; file {Write}; can be undone: {Undoable}",
                outcome.Candidates, outcome.Rejections, outcome.NewWords, outcome.NewAliases, outcome.Write,
                undo is not null);
        }
        else
        {
            _log.LogDebug("Observed dictation had no correction; file {Write}", outcome.Write);
        }

        return outcome;
    }

    /// <summary>Teach a word and, optionally, how it sounds (LEARNING.md §4.3).</summary>
    /// <param name="at">When; the clock's now when omitted.</param>
    public TeachResult Teach(string written, string? soundsLike = null, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(written);
        var result = TeachResult.Ok;
        var millis = Millis(at); // read once: a replay keeps the time it was taught
        var write = Mutate(book => result = book.Teach(written, soundsLike, millis));
        _log.LogInformation("Teach: {Result}, with a sounds-like form: {HasSoundsLike}; file {Write}",
            result, !string.IsNullOrWhiteSpace(soundsLike), write);
        return result;
    }

    /// <summary>Remove a word, the aliases that write it and its pending
    /// corrections; learning will not bring it back.</summary>
    public LearningWrite RemoveWord(string text, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var millis = Millis(at);
        var write = Mutate(book => book.RemoveWord(text, millis));
        _log.LogInformation("Removed a learned word; file {Write}", write);
        return write;
    }

    /// <summary>Remove one alias and keep its word; learning will not bring
    /// the alias back.</summary>
    public LearningWrite RemoveAlias(string heard, string meant, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(meant);
        var millis = Millis(at);
        var write = Mutate(book => book.RemoveAlias(heard, meant, millis));
        _log.LogInformation("Removed a learned alias; file {Write}", write);
        return write;
    }

    /// <summary>Forget everything, including what was removed.</summary>
    public LearningWrite Clear()
    {
        var write = Mutate(book => book.Clear());
        _log.LogInformation("Cleared everything learned; file {Write}", write);
        return write;
    }

    /// <summary>
    /// Put the vocabulary back exactly as it was before the learned change that
    /// issued <paramref name="token"/> (<see cref="LearningObservation.UndoToken"/>),
    /// replaced aliases and counts included, and save it.
    /// </summary>
    /// <returns>What the save did — a refused save stays in memory and is
    /// retried like any other — or null when the token has expired: the
    /// vocabulary changed after that learned change, and putting the old state
    /// back would overwrite the newer one. An expired undo reads and writes
    /// nothing.</returns>
    /// <remarks>
    /// A token exists only after a save, which needs a file this run has read;
    /// a file once read is never read again, so the restore is never one of
    /// the changes replayed onto an unreadable file.
    /// </remarks>
    public LearningWrite? UndoLearning(Guid token)
    {
        LearningWrite write;
        bool changed;
        lock (_gate)
        {
            if (_undo is not { } undo || undo.Token != token)
            {
                _log.LogInformation("Undo of the last learned change refused: the vocabulary changed after it");
                return null;
            }

            // The restore changes the vocabulary, which expires the token
            // like any other change: it undoes once.
            var before = LearningStore.Parse(System.Text.Encoding.UTF8.GetBytes(undo.Before))
                ?? throw new InvalidOperationException("the state kept for an undo did not read back");
            (write, changed) = Apply(book => book.Restore(before), undoable: null, out _);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _log.LogInformation("Undid the last learned change; file {Write}", write);
        return write;
    }

    /// <summary>
    /// Try once more to save what is not on disk yet: a save the disk refused,
    /// or changes made while the file could not be read (which reads it again
    /// first). The host calls it when it quits. Nothing is read or written when
    /// the file already holds everything, or learning was never used.
    /// </summary>
    public LearningWrite Flush()
    {
        var write = Mutate(null);
        _log.LogInformation("Flushed learned vocabulary; file {Write}", write);
        return write;
    }

    /// <summary>What the app window shows: what this session holds. Loads the
    /// file on first use and does no IO after that; an unreadable file is read
    /// again by the next change or <see cref="Flush"/>.</summary>
    public LearningSnapshot Snapshot()
    {
        lock (_gate)
        {
            var state = (_book ?? EnsureLoaded()).State;
            var words = state.Words
                .OrderByDescending(w => w.LastSeen)
                .ThenBy(w => LearningText.Lower(w.Text), LearningText.CodePointOrder)
                .Select(w => new LearnedWordEntry(w.Text, OriginOf(w.Origin), w.Count, TimeOf(w.LastSeen), ConfidenceOf(w)))
                .ToList();
            var aliases = state.Aliases
                .OrderBy(a => a.Heard, LearningText.CodePointOrder)
                .Select(a => new LearnedAliasEntry(a.Heard, a.Meant, OriginOf(a.Origin)))
                .ToList();
            return new LearningSnapshot(words, aliases, state.Pending.Count, _file, _current != _saved);
        }
    }

    // ─── Helpers ───

    /// <summary>
    /// Apply one change under the lock (none for <see cref="Flush"/>) and write
    /// it when the file does not already hold the result. A failed write leaves
    /// the change in memory and is retried by the next operation, because the
    /// file still differs. While the file cannot be read the change is also
    /// kept for the load that reads it to replay.
    /// </summary>
    private LearningWrite Mutate(Action<LearningBook>? change) => Mutate(change, undoable: null, out _);

    /// <param name="undoable">Asked, under the lock, with what the write did:
    /// true keeps the state from before this change for
    /// <see cref="UndoLearning"/> and hands out <paramref name="undo"/>.</param>
    private LearningWrite Mutate(Action<LearningBook>? change, Func<LearningWrite, bool>? undoable, out Guid? undo)
    {
        LearningWrite write;
        bool changed;
        lock (_gate)
        {
            if (change is null && _book is null)
            {
                undo = null;
                return LearningWrite.Unchanged; // never used: nothing to save
            }

            (write, changed) = Apply(change, undoable, out undo);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return write;
    }

    /// <summary><see cref="Mutate(Action{LearningBook}?, Func{LearningWrite, bool}?, out Guid?)"/>'s
    /// work, with the lock already held.</summary>
    /// <returns>What the write did, and whether the window has news: the list
    /// or whether it is saved changed.</returns>
    private (LearningWrite Write, bool Changed) Apply(
        Action<LearningBook>? change, Func<LearningWrite, bool>? undoable, out Guid? undo)
    {
        // Against what the window was last told about: the state before a
        // retried load too, whose reload may itself have changed the list.
        var before = _book is null ? null : _current;
        var wasUnsaved = _book is not null && _current != _saved;
        var book = EnsureLoaded();
        before ??= _current;
        var start = _current; // what this change starts from
        if (change is not null)
        {
            change(book);
            if (_file == LearningFileStatus.Unreadable)
            {
                _whileUnreadable.Add(change);
            }
        }

        var json = LearningStore.Serialize(book.State);
        _current = json;
        LearningWrite write;
        if (json == _saved)
        {
            write = LearningWrite.Unchanged;
        }
        else if (_store.Save(book.State))
        {
            _saved = json;
            write = LearningWrite.Saved;
        }
        else
        {
            write = LearningWrite.NotSaved;
        }

        // Any change to the vocabulary expires the undo: it would overwrite it.
        if (json != start)
        {
            _undo = null;
        }

        undo = null;
        if (undoable?.Invoke(write) == true)
        {
            undo = Guid.NewGuid();
            _undo = (undo.Value, start);
        }

        // The window also shows whether the list is saved, so a retry that
        // saves the same list is news too.
        return (write, json != before || wasUnsaved != (_current != _saved));
    }

    /// <summary>
    /// The book, loaded on first use. After a load that could not read the
    /// file, each call reads it again: still unreadable, the session's book is
    /// kept as it is; readable, the book is rebuilt from the file and the
    /// changes made in between are replayed onto it.
    /// </summary>
    private LearningBook EnsureLoaded()
    {
        if (_book is not null && _file != LearningFileStatus.Unreadable)
        {
            return _book;
        }

        var load = _store.Load();
        if (_book is not null && load.Status == LearningFileStatus.Unreadable)
        {
            return _book;
        }

        // What the file holds, before the replay edits the state in place. A
        // missing or kept-aside file reads as empty. An unreadable one is
        // unknown, so nothing matches it.
        var saved = load.Status == LearningFileStatus.Unreadable ? null : LearningStore.Serialize(load.State);
        var book = new LearningBook(_lexicon, load.State);
        var replayed = _whileUnreadable.Count;
        foreach (var change in _whileUnreadable)
        {
            change(book);
        }

        _whileUnreadable.Clear();
        _book = book;
        _file = load.Status;
        _current = LearningStore.Serialize(book.State);
        _saved = saved;
        _log.LogInformation(
            "Learned vocabulary loaded: {Status}, {Words} words, {Aliases} aliases, {Pending} pending; " +
            "{Replayed} changes made while it could not be read replayed",
            load.Status, book.State.Words.Count, book.State.Aliases.Count, book.State.Pending.Count, replayed);
        return _book;
    }

    private long Millis(DateTimeOffset? at) => (at ?? _clock.GetUtcNow()).ToUnixTimeMilliseconds();

    private static LearningOrigin OriginOf(string origin) => origin switch
    {
        LearningOrigins.Correction => LearningOrigin.Correction,
        LearningOrigins.Taught => LearningOrigin.Taught,
        _ => LearningOrigin.Other,
    };

    private static string ConfidenceOf(LearnedWord word)
    {
        if (word.Extra?.TryGetValue("adaptive", out var adaptive) != true
            || adaptive.ValueKind != System.Text.Json.JsonValueKind.True) return "Learned from your fix";
        var opposition = word.Extra.TryGetValue("opposition", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var count) ? count : 0;
        var score = word.Count - Math.Clamp(opposition, 0, Math.Max(0, word.Count));
        return score >= 4 ? "Established" : score >= 2 ? "Confirmed" : "Tentative";
    }

    private static DateTimeOffset TimeOf(long millis) => DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(
        millis,
        DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
        DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
}
