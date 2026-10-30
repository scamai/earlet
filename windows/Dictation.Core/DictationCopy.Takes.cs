using Dictation.Speech;

namespace Dictation.Core;

// Part of DictationCopy (see DictationCopy.cs): long takes are never lost
// (LIMIT 2026-10-10, Windows stage 1) — a take a fault ended, a take that
// could not be transcribed and is kept for Recover last dictation, the saved
// recording's section and menu item, recovering it, and the question Escape
// asks before it throws a long take away. The design's Windows column
// (LIMIT-DESIGN §5, Copy), one line each on the indicator; the window says
// the same in sentences. Never a dictated word.
public static partial class DictationCopy
{
    // ─── A take a fault ended ───────────────────────────────────────────

    /// <summary>What ended the take, as the start of a sentence: "The
    /// microphone stopped at 4:12".</summary>
    private static string Stopped(DictationTakeStop stop, TimeSpan at) => stop == DictationTakeStop.WorkerLost
        ? $"Speech recognition stopped at {HudClock(at.TotalSeconds)}"
        : $"The microphone stopped at {HudClock(at.TotalSeconds)}";

    /// <summary>The window while what was captured is transcribed.</summary>
    public static string TakeStoppedTranscribing(DictationTakeStop stop, TimeSpan at) =>
        $"{Stopped(stop, at)}, so the recording ended there. Transcribing what was recorded…";

    /// <summary>The window when the words up to the stop went in. A removed
    /// microphone names the one the next hold opens, when Windows named one.</summary>
    public static string TakeStoppedInserted(DictationTakeStop stop, TimeSpan at, string? nextMicrophone = null) =>
        $"{Stopped(stop, at)}, so the recording ended there. Your words up to then went in."
        + (stop == DictationTakeStop.MicrophoneRemoved && MicrophoneForSentence(nextMicrophone) is { } name
            ? $" The next hold uses {name}."
            : string.Empty);

    /// <summary>The window when nothing had been heard before the stop.</summary>
    public static string TakeStoppedNothingHeard(DictationTakeStop stop, TimeSpan at, HotkeyBinding key) =>
        stop == DictationTakeStop.WorkerLost
            ? $"{Stopped(stop, at)} before anything was heard. Hold {key.KeyName} to try again."
            : $"{Stopped(stop, at)} before anything was heard. Check the microphone, then hold {key.KeyName} again.";

    /// <summary>The indicator when the words up to the stop went in: the
    /// design's "Microphone stopped at 4:12 — your words up to then went in".</summary>
    public static string HudTakeStoppedInserted(DictationTakeStop stop, TimeSpan at) =>
        (stop == DictationTakeStop.WorkerLost ? "Speech recognition" : "Microphone")
        + $" stopped at {HudClock(at.TotalSeconds)} — your words up to then went in";

    /// <summary>The indicator when nothing had been heard before the stop.</summary>
    public static string HudTakeStoppedNothingHeard(DictationTakeStop stop, TimeSpan at) =>
        (stop == DictationTakeStop.WorkerLost ? "Speech recognition" : "Microphone")
        + $" stopped at {HudClock(at.TotalSeconds)} — nothing was heard before it";

    // ─── A take kept for Recover last dictation ─────────────────────────

    /// <summary>The menu item and the section's button.</summary>
    public const string RecoverLastDictation = "Recover last dictation";

    /// <summary>The button and the menu item while a recovery runs.</summary>
    public const string RecoveringLastDictation = "Recovering…";

    /// <summary>Where the saved recording is, as the end of a sentence.</summary>
    private const string TakeSavedWhere =
        "The recording is saved: choose " + RecoverLastDictation + " below or from the Earlet icon's menu.";

    /// <summary>The decode failed twice (the retry too) and the take is kept.</summary>
    public const string TakeSaved = "Couldn't transcribe that dictation, so nothing was inserted. " + TakeSavedWhere;

    /// <summary>The take is kept and the worker that lost it is restarting.</summary>
    public const string TakeSavedWorkerRestarting =
        "Speech recognition stopped, so nothing was inserted. The recording is saved for "
        + RecoverLastDictation + ". It is restarting…";

    /// <summary>A shortcut ended a long take: it is kept, not thrown away.</summary>
    public static string CancelledByShortcutTakeSaved(HotkeyBinding key) =>
        $"Cancelled because {key.KeyName} was used in a shortcut. " + TakeSavedWhere;

    /// <summary>The worker lost the decode and the slot is trying once more.</summary>
    public const string RetryingTranscription =
        "Speech recognition stopped, so it is starting again to transcribe this dictation…";

    /// <summary>The kept take could not be moved into place after all: it
    /// is gone, and the window says so rather than keep promising it.</summary>
    public const string TakeNotSaved =
        "Couldn't transcribe that dictation, and its recording could not be saved. " + LogDetails;

    /// <summary>The indicator's line when a shortcut ended a long take.</summary>
    public const string HudCancelledByShortcutTakeSaved =
        "Cancelled, that was a shortcut — the recording is saved for " + RecoverLastDictation;

    /// <summary>The indicator's line for a kept take: the design's.</summary>
    public const string HudTakeSaved =
        "Couldn't transcribe — the recording is saved. Use " + RecoverLastDictation + " to try again";

    // ─── The saved recording's section and menu item ────────────────────

    /// <summary>The section's title.</summary>
    public const string SavedTakeTitle = "Saved recording";

    /// <summary>The section's line for a take that could not be transcribed.</summary>
    public static string SavedTakeDetail(TimeSpan length) =>
        $"Couldn't transcribe it. {HudClock(length.TotalSeconds)} long.";

    /// <summary>The section's line for a take left by a Dictation that
    /// closed or crashed before it was transcribed.</summary>
    public static string SavedTakeDetailInterrupted(TimeSpan length) =>
        $"Earlet closed before it was transcribed. {HudClock(length.TotalSeconds)} long.";

    /// <summary>After the line, when keeping it deleted an earlier saved
    /// recording: one is kept at a time.</summary>
    public const string SavedTakeReplacedEarlier = "It replaced an earlier saved recording.";

    /// <summary>After the line, while a recovery's words wait in Last
    /// available text to be copied.</summary>
    public const string SavedTakeAwaitsCopy = "Its words are in Last available text; it stays until they are copied.";

    /// <summary>After the line, while dictation is off: the recovery needs
    /// the speech engine.</summary>
    public const string SavedTakeNeedsDictation = "Turn on dictation to recover it.";

    /// <summary>The section's Discard.</summary>
    public const string DiscardSavedTake = "Discard";

    /// <summary>Discard's accessible name: what it does.</summary>
    public const string DiscardSavedTakeName = "Discard the saved recording";

    /// <summary>Under the section.</summary>
    public const string SavedTakeFootnote =
        "Kept on this PC and never sent anywhere, until you recover or discard it, for 7 days at most.";

    /// <summary>The menu item: the time, never the words.</summary>
    public static string RecoverLastDictationAt(string time) => $"{RecoverLastDictation} · {time}";

    // ─── Recovering it ──────────────────────────────────────────────────

    /// <summary>While the saved recording is transcribed.</summary>
    public const string Recovering = "Transcribing your saved recording…";

    /// <summary>Transcribed and on its way to the clipboard (Copy last
    /// text's own ending follows in the Recent result section).</summary>
    public const string Recovered = "Your saved recording is transcribed. Its words are in Last available text below.";

    /// <summary>Transcribed while a dictation was under way: the words wait
    /// in the Recent result section, and the recording stays until they are
    /// copied (the next dictation's words would replace them there).</summary>
    public const string RecoveredUseCopy =
        "Your saved recording is transcribed: choose " + CopyLastText + " below or from the Earlet icon's menu. "
        + "The recording stays saved until its words are copied.";

    /// <summary>The saved recording held nothing worth typing.</summary>
    public const string RecoveredNothingHeard = "Nothing was heard in your saved recording, so it was discarded.";

    /// <summary>The recovery's decode failed; the recording is still kept.</summary>
    public const string RecoveryFailed =
        "Couldn't transcribe your saved recording. It is still saved; try again. " + LogDetails;

    /// <summary>The saved recording could not be opened just now (another
    /// program holds it, say); it is not damaged and stays saved.</summary>
    public const string RecoveryNotOpened =
        "Your saved recording could not be opened just now. It is still saved; try again. " + LogDetails;

    /// <summary>The saved recording could not be read back.</summary>
    public const string RecoveryUnreadable =
        "Your saved recording could not be read, so it was discarded. " + LogDetails;

    /// <summary>Discard.</summary>
    public const string SavedTakeDiscarded = "The saved recording was discarded.";

    /// <summary>The indicator when a recovery failed.</summary>
    public const string HudRecoveryFailed = "Couldn't transcribe the saved recording — it is still saved, try again";

    // ─── Escape on a long take ──────────────────────────────────────────

    /// <summary>The first Escape on a take with more than thirty seconds of
    /// speech: the design's Windows words (LIMIT-DESIGN §5, Copy, "Discard
    /// 3:42 of speech? Press Esc again"), split at the indicator's " — " as
    /// every Windows notice is, so it draws as a headline and what to do; the
    /// Mac's indicator titles it "Discard 3:42 of speech?" over "Press Esc
    /// again to discard. Keep talking to continue."</summary>
    public static string HudAskToDiscard(TimeSpan length) =>
        $"Discard {HudClock(length.TotalSeconds)} of speech? — press Esc again";
}
