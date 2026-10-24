namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): each attempted
// hold's private receipt in the log (DictationAttemptLog). The hooks that feed
// it are one line each, at the press (BeginPress), its refusals and its
// admission (ResolvePress, Admit), the hook's cancel (CancelHold), the key
// coming up (Tick and the push-to-talk release), the session's result
// (OnSessionCompleted) and dictation stopping (StopTrigger).
public sealed partial class DictationController
{
    // ─── Attempt receipts ───────────────────────────────────────────────

    private DictationAttemptTracker? _attempts;

    /// <summary>Made on first use, with the controller's own log and clock.
    /// The session's result reaches it on the pipeline's thread, hence the
    /// thread-safe first use.</summary>
    private DictationAttemptTracker Attempts =>
        LazyInitializer.EnsureInitialized(ref _attempts, () => new DictationAttemptTracker(_log, _options.Clock));

    /// <summary>
    /// The push-to-talk release of an admitted hold: the key came up, or the
    /// app ended the hold itself (the recording limit, a silence stop).
    /// Subscribed before the microphone tap is, so it runs first: a hold too
    /// short to keep ends inside the tap's own release, and its receipt must
    /// already carry the release.
    /// </summary>
    /// <remarks>
    /// This is the Mac's release, latch included. The Mac marks it where the
    /// hold is let go to transcription (<c>finishRecording</c>): a latch's
    /// end, or a quick tap's settle, never the pair's own key-ups. The hook
    /// keeps such a hold reading down until that same moment (a quick tap's
    /// hold until press + the pair window, a latch until the press, Space or
    /// Enter that stops it, or a stop the app made), so the push-to-talk
    /// release this follows is that moment (pinned by
    /// <c>HandsFreeIntegrationTests</c>). A double-tap is one attempt: its
    /// second press takes the first press's hold over and is no key-down
    /// edge here.
    /// </remarks>
    private void OnHoldReleased(object? sender, EventArgs e) => Attempts.Released();
}
