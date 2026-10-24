using Dictation.Core.Learning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core;

/// <summary>What a row of the vocabulary list is.</summary>
public enum VocabularyRowKind
{
    /// <summary>A word in the vocabulary, learned or taught.</summary>
    Word,

    /// <summary>A rewrite: what the recogniser hears, and what is typed instead.</summary>
    Alias,
}

/// <summary>One row of the vocabulary list.</summary>
/// <param name="Kind">A word or an alias.</param>
/// <param name="Text">What the row shows: the word, or "heard → meant".</param>
/// <param name="Detail">A word's secondary line — learned or added — or null.</param>
/// <param name="Written">The word, or the alias's written form.</param>
/// <param name="Heard">The alias's heard form; null for a word.</param>
public sealed record VocabularyRow(
    VocabularyRowKind Kind,
    string Text,
    string? Detail,
    string Written,
    string? Heard);

/// <summary>What the Vocabulary section shows. One immutable snapshot per change.</summary>
/// <param name="Rows">Words (most recently seen first), then aliases.</param>
/// <param name="EmptyText">The empty-list sentence, or null when there are rows.</param>
/// <param name="TeachMessage">Under the teach row: why the last word was not
/// added, or that it was saved, or null.</param>
/// <param name="CanForgetAll">The Forget all button is live: there is something to forget.</param>
/// <param name="ConfirmingForgetAll">The confirmation is showing.</param>
/// <param name="NotSavedText">The one warning beside Save again while the
/// list is not what is on disk — a refused save, or a file that could not be
/// read — or null, and Save again shows exactly while it is set. The latest
/// Remove, Forget all or Save again sentence that says so, else the unsaved
/// notice. A removal the disk refused must not look done.</param>
/// <param name="CorrectionNotice">How the last correction watch went, above
/// the teach row, or null; always null while "Learn from my corrections" is
/// off. Never names a word.</param>
/// <param name="LearningReceipt">The "Learning from you" line: a correction
/// remembered, or what its Undo did; null hides the section. Never names a
/// word.</param>
/// <param name="CanUndoLearning">Undo learning is offered.</param>
/// <param name="ManagementNotice">The latest Remove, Forget all or Save
/// again receipt while everything is on disk, or null. Never names a word.</param>
/// <param name="SavingAgain">Save again is running: it reads "Saving…" and is
/// disabled.</param>
public sealed record VocabularyViewState(
    IReadOnlyList<VocabularyRow> Rows,
    string? EmptyText,
    string? TeachMessage,
    bool CanForgetAll,
    bool ConfirmingForgetAll,
    string? NotSavedText,
    string? CorrectionNotice = null,
    string? LearningReceipt = null,
    bool CanUndoLearning = false,
    string? ManagementNotice = null,
    bool SavingAgain = false);

/// <summary>The Vocabulary section, as <see cref="VocabularyModel"/> drives it.</summary>
public interface IVocabularyView
{
    void Render(VocabularyViewState state);
}

/// <summary>
/// The Vocabulary section's behaviour: the list of learned and taught words,
/// adding a word, removing one, and forgetting everything after a
/// confirmation. The WPF window only renders <see cref="VocabularyViewState"/>
/// and forwards clicks, so all of this runs on Linux under test.
/// </summary>
/// <remarks>
/// <para>
/// <b>One thread.</b> Every public member is called on the UI thread.
/// <see cref="DictationLearning.Changed"/> is raised on whichever thread made
/// the change — a correction watch on the thread pool, usually — and is
/// marshalled back through <c>post</c> before the list is read again.
/// </para>
/// <para>
/// <b>File work off it.</b> Add, Remove, Forget all, Save again and Undo
/// learning hand their learning call to the work runner the window passes in
/// (a <see cref="SerialWorkQueue"/>), and their response comes back through
/// <c>post</c>; how responses are fenced is in
/// <c>VocabularyModel.Saving.cs</c>.
/// </para>
/// <para>
/// The "Learn from my corrections" switch is the controller's, because it
/// lives in the settings file the controller owns; the window passes on what
/// the controller shows (<see cref="ApplyLearningSwitch"/>).
/// </para>
/// <para>
/// The correction notice — what the last watch found — is in
/// <c>VocabularyModel.Notices.cs</c>, and the learning receipt with its Undo
/// in <c>VocabularyModel.Receipt.cs</c>.
/// </para>
/// </remarks>
public sealed partial class VocabularyModel : IDisposable
{
    private readonly DictationLearning _learning;
    private readonly IVocabularyView _view;
    private readonly Action<Action> _post;
    private string? _teachMessage;
    private bool _confirmingForgetAll;
    private bool _disposed;

    /// <param name="corrections">The correction watch whose events become
    /// the notice (<see cref="DictationController.Corrections"/>); none when null.</param>
    /// <param name="work">Runs a vocabulary change's learning call off the UI
    /// thread, in order (<see cref="SerialWorkQueue.Run"/>). Null runs it on
    /// the calling thread, before the call returns.</param>
    /// <param name="log">Save again's outcome and failed changes: outcomes only, never words.</param>
    public VocabularyModel(
        DictationLearning learning,
        IVocabularyView view,
        Action<Action> post,
        ICorrectionWatchEvents? corrections = null,
        Func<Action, Task>? work = null,
        ILogger<VocabularyModel>? log = null)
    {
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _work = work ?? RunHere;
        _log = log ?? (ILogger)NullLogger.Instance;
        _learning.Changed += OnChanged;
        Listen(corrections);
    }

    /// <summary>The last state rendered.</summary>
    public VocabularyViewState? Shown { get; private set; }

    /// <summary>Read the list and paint it: the first render, and every one
    /// after a change.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        Paint(_learning.Snapshot());
    }

    /// <summary>Paint <paramref name="snapshot"/>, read on this thread just now.</summary>
    private void Paint(LearningSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        var rows = new List<VocabularyRow>(snapshot.Words.Count + snapshot.Aliases.Count);
        rows.AddRange(snapshot.Words.Select(w => new VocabularyRow(
            VocabularyRowKind.Word,
            w.Text,
            w.Origin switch
            {
                LearningOrigin.Correction => w.Confidence,
                LearningOrigin.Taught => DictationCopy.AddedByYou,
                _ => null,
            },
            w.Text,
            null)));
        rows.AddRange(snapshot.Aliases.Select(a => new VocabularyRow(
            VocabularyRowKind.Alias, DictationCopy.AliasRow(a.Heard, a.Meant), null, a.Meant, a.Heard)));

        var anything = rows.Count > 0 || snapshot.Pending > 0;
        if (!anything)
        {
            _confirmingForgetAll = false;
        }

        Shown = new VocabularyViewState(
            rows,
            rows.Count == 0 ? DictationCopy.VocabularyEmpty : null,
            _teachMessage,
            CanForgetAll: anything && !_confirmingForgetAll,
            ConfirmingForgetAll: _confirmingForgetAll,
            NotSavedText: SaveWarning(snapshot.Unsaved),
            CorrectionNotice: _correctionNotice,
            LearningReceipt: _receipt,
            CanUndoLearning: _undoToken is not null,
            ManagementNotice: SaveReceipt(snapshot.Unsaved),
            SavingAgain: _savingAgain);
        _view.Render(Shown);
    }

    /// <summary>Add <paramref name="written"/>, and optionally how it sounds.
    /// The line under the teach row says why it was refused, or that it is
    /// saved; an Add that is not on disk says nothing of its own (the
    /// warning beside Save again does).</summary>
    /// <returns>Completes once the section is painted, with whether the word
    /// was added. The window's Add goes through
    /// <see cref="Teach(IVocabularyTeachRow)"/> instead.</returns>
    public Task<bool> Teach(string? written, string? soundsLike)
    {
        var taught = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Teach(written, soundsLike, added => taught.TrySetResult(added));
        return taught.Task;
    }

    /// <param name="taught">Called on the UI thread after the section is
    /// painted, with whether the word was added.</param>
    private void Teach(string? written, string? soundsLike, Action<bool> taught)
    {
        ClearNoticesAndEndWatch();
        _teachMessage = null;
        var generation = _responseGeneration;
        Refresh();
        Run(
            () => _learning.Teach(written ?? string.Empty, soundsLike),
            result =>
            {
                Respond(generation, unsaved =>
                    _teachMessage = DictationCopy.ForTeach(result) ?? (unsaved ? null : DictationCopy.TeachSaved));
                taught(result == TeachResult.Ok);
            },
            () => taught(false));
    }

    /// <summary>Remove one row. A word takes its aliases with it; an alias
    /// leaves its word. Learning does not bring either back.</summary>
    public void Remove(VocabularyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ClearNoticesAndEndWatch();
        _teachMessage = null;
        var generation = _responseGeneration;
        Refresh();
        Run(
            () => row.Kind == VocabularyRowKind.Alias
                ? _learning.RemoveAlias(row.Heard ?? string.Empty, row.Written)
                : _learning.RemoveWord(row.Written),
            _ => Respond(generation, unsaved => ShowManagementReceipt(unsaved, DictationCopy.VocabularyRemoved)));
    }

    /// <summary>The Forget all button: ask first.</summary>
    public void AskForgetAll()
    {
        _confirmingForgetAll = true;
        Refresh();
    }

    /// <summary>The confirmation's Cancel.</summary>
    public void CancelForgetAll()
    {
        _confirmingForgetAll = false;
        Refresh();
    }

    /// <summary>The confirmation's Forget all: everything learned, added and
    /// removed goes. Only after <see cref="AskForgetAll"/>.</summary>
    public void ConfirmForgetAll()
    {
        if (!_confirmingForgetAll)
        {
            return;
        }

        _confirmingForgetAll = false;
        _teachMessage = null;
        ClearNoticesAndEndWatch();
        var generation = _responseGeneration;
        Refresh();
        Run(
            _learning.Clear,
            _ => Respond(generation, unsaved => ShowManagementReceipt(unsaved, DictationCopy.VocabularyForgotten)));
    }

    public void Dispose()
    {
        _disposed = true;
        _learning.Changed -= OnChanged;
        StopListening();
        LetPendingWorkFinish();
    }

    private void OnChanged(object? sender, EventArgs e) => _post(RefreshAfterChange);
}
