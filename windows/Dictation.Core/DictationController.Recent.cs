using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the recent result
// and Copy last text — the macOS contract in docs/design-macos.md's "Recent result and
// Copy last text", with Windows' keys. The shared partials only call in here:
// Completed (RememberResult, LogRecovery, WordsKeptInApp), BeginPress
// (RefusePressWhileCopying), Tick (GiveUpOnASlowCopy), Render (RecentView,
// RecentMenu) and Dispose. Its two log lines about the words, a refused
// paste's recovery and each copy's answer, are lines of the attempt receipt
// (DictationAttemptLog) of the dictation that left the words.
public sealed partial class DictationController
{
    // ─── Recent result ──────────────────────────────────────────────────

    /// <summary>The last usable result, in memory only. Never logged.</summary>
    private readonly DictationRecentResult _recent = new();

    /// <summary>When the copy on its way started, on <see cref="DictationControllerOptions.Clock"/>.</summary>
    private TimeSpan _copyStartedAt;

    /// <summary>The refusal a press during a copy put on the status line, and
    /// the line it replaced: the copy's end puts that line back if nothing
    /// else has replaced the refusal meanwhile.</summary>
    private DictationStatus? _copyRefusal;
    private DictationStatus? _statusBeforeCopyRefusal;

    /// <summary>Whether a result is held: true from a usable result until a
    /// newer one, Dismiss or quitting. Says nothing of the words, which only
    /// the window's section shows (<see cref="DictationViewState.Recent"/>).</summary>
    public bool HoldsRecentText => _recent.Current is not null;

    /// <summary>
    /// Copy last text, from the window's button or the notification-area
    /// menu: put the held words on the clipboard again without another
    /// transcription. Allowed while paused; refused while anything records,
    /// transcribes or types, and while a copy is already on its way.
    /// </summary>
    public void CopyLastText()
    {
        if (!CanCopyLastText || _recent.BeginCopy() is not { } copy)
        {
            return;
        }

        // "Copying…", and a press from here until the answer is refused.
        _copyStartedAt = _options.Clock();
        Render();
        var clipboard = _platform.Clipboard;
        var text = copy.Entry.Text;
        _options.RunClipboardCopy(() => WriteClipboard(clipboard, text)).ContinueWith(
            t => _post(() => CopyFinished(copy, t.IsCompletedSuccessfully && t.Result)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Tick's hook: a copy the clipboard has not answered within
    /// <see cref="DictationControllerOptions.ClipboardCopyDeadline"/> has
    /// failed. <c>EmptyClipboard</c> waits on the clipboard's previous owner,
    /// so a hung owner could hold the write forever, and with it every press
    /// of the hold key, which a copy refuses. Runs while paused too. A late
    /// answer from the write is then ignored.
    /// </summary>
    private void GiveUpOnASlowCopy()
    {
        if (_disposed
            || _recent.Pending is not { } copy
            || _options.Clock() - _copyStartedAt < _options.ClipboardCopyDeadline)
        {
            return;
        }

        _log.LogWarning(
            "Copy last text: the clipboard did not answer within {Seconds} s; treated as failed",
            _options.ClipboardCopyDeadline.TotalSeconds);
        CopyFinished(copy, copied: false);
    }

    /// <summary>Dismiss: discard the held text. The clipboard is left alone,
    /// and so is the text if a copy of it is on its way.</summary>
    public void DismissLastText()
    {
        if (_disposed || _recent.Copying || _recent.Current is null)
        {
            return;
        }

        // A recovery's words: its recording goes with them (DictationController.Takes.cs).
        RecoveredWordsDismissed(_recent.Current.Id);
        _recent.Clear();
        _log.LogInformation("The last text was dismissed");
        Render();
    }

    /// <summary>
    /// A copy may start: something is held, no copy is on its way, and
    /// nothing is recording, waiting on the press-time field check,
    /// transcribing or typing (the Mac's "not busy, not recording"). Paused
    /// is at rest; so is the window's own close while dictation is on.
    /// </summary>
    private bool CanCopyLastText =>
        _recent.Current is not null && !_recent.Copying && AtRest && _fieldCheck is null;

    /// <summary>On the thread pool: one write, its failure folded into
    /// false. Logs the exception's type only, never its message.</summary>
    private bool WriteClipboard(IClipboard clipboard, string text)
    {
        try
        {
            return clipboard.TrySet(text);
        }
        catch (Exception error)
        {
            _log.LogWarning("Copy last text: the clipboard write threw ({Error})", error.GetType().Name);
            return false;
        }
    }

    private void CopyFinished(DictationRecentResult.PendingCopy copy, bool copied)
    {
        if (!ReferenceEquals(_recent.Pending, copy))
        {
            // Given up on at the deadline; this is the write's late answer.
            _log.LogDebug("Copy last text: a late answer from a copy already treated as failed was ignored");
            return;
        }

        var outcome = _recent.FinishCopy(copy, copied);
        if (_disposed)
        {
            return;
        }

        if (outcome is { } done)
        {
            // The outcome, never the words, under the attempt that left them.
            copy.Entry.Attempt?.Copied(done);
            _hud?.Copied(done);
            if (done)
            {
                // A recovery's words are safe now: its recording goes (DictationController.Takes.cs).
                RecoveredWordsCopied(copy.Entry.Id);
            }
        }

        // A press refused during the copy left "Still copying…" on the status
        // line, and no dictation follows to replace it: put back the line it
        // replaced ("Your words are still here…", say), unless something else
        // (Pause, Enable) has already said something newer.
        var refusal = _copyRefusal;
        _copyRefusal = null;
        Render(refusal is not null && ReferenceEquals(_status, refusal) ? _statusBeforeCopyRefusal : null);
    }

    /// <summary>
    /// Completed's hook, before the outcome is put into words: what this
    /// dictation leaves for Copy last text (see
    /// <see cref="DictationRecentResult.Remember(DictationSessionResult, DateTimeOffset, DictationAttemptLog?)"/>),
    /// with its receipt, under which each copy of the words is logged.
    /// </summary>
    private void RememberResult(DictationSessionResult result, DictationAttemptLog? attempt)
    {
        var held = _recent.Current is not null;
        _recent.Remember(result, _options.Now(), attempt);
        if (result.Outcome == DictationOutcome.SecureField && held)
        {
            _log.LogInformation("The last text was discarded: the dictation ended in a password field");
        }
    }

    /// <summary>
    /// KeepOnClipboard's hook, once the clipboard has answered: the refused
    /// paste's recovery line on its attempt's receipt, the Mac's
    /// <c>event=recovery clipboard=… available_in_app=…</c>. RememberResult
    /// has already run, so <see cref="DictationRecentResult.Current"/> says
    /// whether Dictation kept the words.
    /// </summary>
    private void LogRecovery(DictationAttemptLog? attempt, bool clipboard) =>
        attempt?.Recovery(clipboard, availableInApp: _recent.Current is { NeedsRecovery: true });

    /// <summary>
    /// KeepOnClipboard's sentence when the clipboard refused the words too:
    /// they are still held, so the status line says where to get them. Null
    /// when nothing is held, and the plain refusal follows.
    /// </summary>
    private DictationStatus? WordsKeptInApp(DictationSessionResult result) =>
        _recent.Current is { NeedsRecovery: true }
            ? new DictationStatus(result.Outcome == DictationOutcome.NoFocusedWindow
                ? DictationCopy.WordsStillHereNoWindow
                : DictationCopy.WordsStillHere)
            : null;

    /// <summary>
    /// BeginPress's hook: a press while Copy last text is still writing is
    /// refused out loud, like a press during a dictation, so the copy and the
    /// next paste never share the clipboard. The press never reaches Admit,
    /// so nothing records until the key comes up and goes down again. Its
    /// attempt, already started, ends <c>still_copying</c>.
    /// </summary>
    private bool RefusePressWhileCopying()
    {
        if (!_recent.Copying)
        {
            return false;
        }

        _log.LogInformation("Press refused: Copy last text is still writing; the microphone was not opened");
        if (!ReferenceEquals(_status, _copyRefusal))
        {
            _statusBeforeCopyRefusal = _status;
        }

        _copyRefusal = new DictationStatus(DictationCopy.StillCopyingLastText(_holdKey));
        Render(_copyRefusal);
        _hud?.PressRefused(DictationPressRefusal.StillCopying);
        Attempts.PressEnded(DictationAttemptEnding.StillCopying);
        return true;
    }

    /// <summary>The window's Recent result section, or null.</summary>
    private DictationRecentView? RecentView()
    {
        if (_recent.Current is not { } entry)
        {
            return null;
        }

        var copying = _recent.Copying;
        return new DictationRecentView(
            entry.Id,
            entry.NeedsRecovery ? DictationCopy.RecentTitleRecovery : DictationCopy.RecentTitle,
            entry.NeedsRecovery ? DictationCopy.RecentDetailRecovery : DictationCopy.RecentDetail,
            DictationCopy.ShortTime(entry.At),
            entry.Text,
            copying ? DictationCopy.CopyingLastText : DictationCopy.CopyLastText,
            CanCopyLastText,
            copying,
            CanDismiss: !copying,
            _recent.CopyMessage);
    }

    /// <summary>The notification-area menu's items, or null: the time, never
    /// the words.</summary>
    private DictationRecentMenu? RecentMenu() =>
        _recent.Current is { } entry
            ? new DictationRecentMenu(
                DictationCopy.CopyLastTextAt(DictationCopy.ShortTime(entry.At)), CanCopyLastText, entry.NeedsRecovery)
            : null;
}
