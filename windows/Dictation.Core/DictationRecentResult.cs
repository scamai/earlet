using System.Text;
using Dictation.Services;

namespace Dictation.Core;

/// <summary>
/// One recoverable result, in memory only: the macOS app's
/// <c>DictationRecentResult</c>, on the controller's thread. No audio, history,
/// settings or log writes. The next usable result replaces it; Dismiss and
/// quitting discard it. Pausing does not, and it does not expire.
/// </summary>
/// <remarks>
/// <para>
/// The Windows app keeps no transcript history and no audio store: the
/// Mac's encrypted history (DESIGN.md, Review status → Decided, 2026-10-16)
/// has no Windows counterpart yet. Nothing here touches the disk, and
/// nothing here logs; the controller logs outcomes without the words.
/// </para>
/// <para>
/// <b>A copy is two steps</b> because the controller is single-threaded and
/// the clipboard write runs off its thread: <see cref="BeginCopy"/> on the
/// way out, <see cref="FinishCopy"/> when the answer is posted back, or when
/// the controller stops waiting for it. Between the two, <see cref="Copying"/>
/// refuses a second copy, so one click is one write; each copy is its own
/// <see cref="PendingCopy"/>, so the late answer of a copy already given up on
/// changes nothing; and the entry is re-checked at the end, so a newer entry
/// is never labelled copied.
/// </para>
/// <para>
/// <b>Printed without the words.</b> <see cref="Entry"/> and
/// <see cref="DictationRecentView"/> are records, whose generated
/// <c>ToString</c> would print every member; each prints the length of its
/// text instead, so a log line or an exception that formats the view state
/// cannot carry what was said.
/// </para>
/// </remarks>
public sealed class DictationRecentResult
{
    /// <summary>The held text.</summary>
    /// <param name="Id">New for every result: the window closes "Show text"
    /// again for each one, and a late copy is matched against it.</param>
    /// <param name="Text">The words: what was typed, or what a refused paste
    /// could not type. Never logged, never on the indicator.</param>
    /// <param name="At">When the result arrived, which tells it apart from a
    /// newer dictation that was cancelled or failed.</param>
    /// <param name="NeedsRecovery">The paste failed: the words did not land
    /// where the user was typing.</param>
    /// <param name="Attempt">The receipt of the dictation that left the
    /// words, under which the controller logs each copy of them; null for
    /// text held without one.</param>
    public sealed record Entry(
        Guid Id, string Text, DateTimeOffset At, bool NeedsRecovery, DictationAttemptLog? Attempt = null)
    {
        private bool PrintMembers(StringBuilder builder)
        {
            builder.Append(
                $"Id = {Id}, Text = {Text.Length} characters, At = {At:O}, NeedsRecovery = {NeedsRecovery}, "
                + $"Attempt = {Attempt?.Id}");
            return true;
        }
    }

    /// <summary>One copy on its way: the entry it writes. Told apart by
    /// identity, so a second copy of the same entry is a different one.</summary>
    public sealed class PendingCopy(Entry entry)
    {
        public Entry Entry { get; } = entry;
    }

    /// <summary>The held entry, or null.</summary>
    public Entry? Current { get; private set; }

    /// <summary>How the last copy of <see cref="Current"/> went, for the
    /// window; null before a copy, and again once the entry changes.</summary>
    public string? CopyMessage { get; private set; }

    /// <summary>The copy on its way to the clipboard, or null.</summary>
    public PendingCopy? Pending { get; private set; }

    /// <summary>A copy is on its way to the clipboard.</summary>
    public bool Copying => Pending is not null;

    /// <summary>
    /// Hold <paramref name="text"/>, replacing whatever was held. Blank text
    /// is no usable result, and clears instead.
    /// </summary>
    public void Remember(string? text, bool needsRecovery, DateTimeOffset at, DictationAttemptLog? attempt = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Clear();
            return;
        }

        Current = new Entry(Guid.NewGuid(), text, at, needsRecovery, attempt);
        CopyMessage = null;
    }

    /// <summary>
    /// What a finished dictation leaves behind: the words that were typed;
    /// the words a refused paste could not type, kept for recovery; or, after
    /// a password field found at the release or at the paste, nothing at all,
    /// even when an older entry was held. Every other ending (cancelled,
    /// nothing heard, a failure) keeps the older entry, and its time tells the
    /// two apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows checks for a password field at the press, again at the
    /// release, before the recogniser, and once more just before the paste,
    /// where the Mac clears the entry on its secure refusal. A field that
    /// becomes a password field while the words are transcribed is not typed
    /// into, and its words are not held here (the Mac's
    /// <c>rememberRefused</c>).
    /// </para>
    /// <para>
    /// A password field refused at the press never reaches this: no dictation
    /// ran, and the Mac leaves the held text alone then too.
    /// </para>
    /// </remarks>
    public void Remember(DictationSessionResult result, DateTimeOffset at, DictationAttemptLog? attempt = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case DictationOutcome.Inserted or DictationOutcome.ClipboardRestoreFailed:
                Remember(result.Text, needsRecovery: false, at, attempt);
                break;
            case DictationOutcome.InsertionRefused or DictationOutcome.NoFocusedWindow:
                Remember(result.UndeliveredText, needsRecovery: true, at, attempt);
                break;
            case DictationOutcome.SecureField:
                // A secure target, at the release or the paste, is excluded
                // even if words were held before: no later copy can recover
                // anything near a password field.
                Clear();
                break;
        }
    }

    /// <summary>Discard the entry and its copy message.</summary>
    public void Clear()
    {
        Current = null;
        CopyMessage = null;
    }

    /// <summary>
    /// Start copying the held entry. Null when there is nothing to copy or a
    /// copy is already on its way: never submit twice.
    /// </summary>
    public PendingCopy? BeginCopy()
    {
        if (Copying || Current is not { } entry)
        {
            return null;
        }

        Pending = new PendingCopy(entry);
        return Pending;
    }

    /// <summary>
    /// <paramref name="copy"/> is over: the clipboard answered, or the
    /// controller stopped waiting (<paramref name="succeeded"/> false).
    /// Returns whether the clipboard took the words; null when the held entry
    /// changed or went while the write ran, so a newer entry is never
    /// labelled copied, and null, changing nothing, when this copy was
    /// already over: a late answer after the controller gave up.
    /// </summary>
    public bool? FinishCopy(PendingCopy copy, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (!ReferenceEquals(Pending, copy))
        {
            return null;
        }

        Pending = null;
        if (Current?.Id != copy.Entry.Id)
        {
            return null;
        }

        CopyMessage = succeeded ? DictationCopy.Copied : DictationCopy.CopyFailed;
        return succeeded;
    }
}

/// <summary>The window's Recent result section, while one is held.</summary>
/// <param name="Id">The entry; "Show text" closes again when it changes.</param>
/// <param name="Title">"Your words are still here" after a failed paste,
/// otherwise "Last available text".</param>
/// <param name="Detail">The line under the title.</param>
/// <param name="Time">When the result arrived, in the culture's short time.</param>
/// <param name="Text">The words, shown only once the user opens "Show text".</param>
/// <param name="CopyLabel">"Copy last text", or "Copying…" during a copy.</param>
/// <param name="CanCopy">A copy may start now: nothing is recording,
/// transcribing or typing, and no copy is on its way.</param>
/// <param name="Copying">A copy is on its way.</param>
/// <param name="CanDismiss">Dismiss is live (not during a copy).</param>
/// <param name="CopyMessage">How the last copy went, or null.</param>
public sealed record DictationRecentView(
    Guid Id,
    string Title,
    string Detail,
    string Time,
    string Text,
    string CopyLabel,
    bool CanCopy,
    bool Copying,
    bool CanDismiss,
    string? CopyMessage)
{
    /// <summary>Every member but the words, which print as a length.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"Id = {Id}, Title = {Title}, Detail = {Detail}, Time = {Time}, Text = {Text.Length} characters, "
            + $"CopyLabel = {CopyLabel}, CanCopy = {CanCopy}, Copying = {Copying}, CanDismiss = {CanDismiss}, "
            + $"CopyMessage = {CopyMessage}");
        return true;
    }
}

/// <summary>The notification-area menu's recent-result items. No words: the
/// menu offers the copy without showing what it copies.</summary>
/// <param name="CopyLabel">"Copy last text · &lt;time&gt;".</param>
/// <param name="CanCopy">Whether the item is live.</param>
/// <param name="NeedsRecovery">Show the "Your words are still here" line
/// above it: the paste failed.</param>
public sealed record DictationRecentMenu(string CopyLabel, bool CanCopy, bool NeedsRecovery);
