using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the live preview of
// the words on the floating indicator while a hold listens, behind
// DICTATION_PREVIEW=1 (off by default on Windows; the Mac has it on unless
// DICTATION_PREVIEW=0). The stream itself is Dictation.Services'
// DictationPreviewSession over the worker slot (DictationWorkerSlot, an IPreviewTranscriber); what is here is
// when it runs. The shared partials only call in: Arm and StopTrigger
// (Session.cs) compose and tear it down, Admit (Hold.cs) starts it, CancelHold
// and ShowSessionState stop it, and Completed logs the comparison.
public sealed partial class DictationController
{
    // ─── The live preview ───────────────────────────────────────────────

    /// <summary>The switch, read once: the override cannot change while the
    /// app runs.</summary>
    private bool? _previewOn;

    /// <summary>This arm's preview, or null when the switch is off or
    /// dictation is not armed.</summary>
    private DictationPreviewSession? _preview;

    /// <summary>The microphone the preview is fed from while a hold listens,
    /// and the handler; null at every other time.</summary>
    private (IAudioCaptureDevice Microphone, EventHandler<AudioChunk> Feed)? _previewFeed;

    /// <summary>The session's number for the run the listening hold feeds
    /// (<see cref="DictationPreviewUpdate.Run"/>), or 0 while none does.
    /// Read and written on the UI thread only: a caption is drawn only when
    /// the run it was produced for is still this one, however late it was
    /// raised or posted, so one hold's words never stand on another's pill.</summary>
    private long _previewRun;

    private bool PreviewOn =>
        _previewOn ??= DictationEnvironment.PreviewIsOn(_environment(DictationEnvironment.Preview));

    /// <summary>
    /// The transcriber this arm's session gets: the worker slot itself with
    /// the switch off, so a build without the preview runs exactly as before;
    /// with it on, the slot behind a preview that is stopped, for a bounded
    /// time, before every final decode (<see cref="PreviewFirst"/>). Nothing
    /// without an indicator to draw it: the caption is all a preview is for,
    /// and its decodes cost the CPU the final pass runs on.
    /// </summary>
    private ITranscriber ComposePreview(DictationWorkerSlot worker)
    {
        if (!PreviewOn || _hud is null)
        {
            return worker;
        }

        var preview = new DictationPreviewSession(worker, _logs.CreateLogger<DictationPreviewSession>());
        preview.PreviewUpdated += OnPreviewUpdated;
        _preview = preview;
        _log.LogInformation(
            "Live preview on ({Switch}=1): the indicator shows the words while the key is held",
            DictationEnvironment.Preview);
        return new PreviewFirst(worker, preview, _options.PreviewStopLimit, _log);
    }

    /// <summary>
    /// A press was admitted and the session listens: open this hold's stream
    /// and feed it the microphone. Asynchronous: nothing on the press path
    /// waits for the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only once capture is up, as on the Mac (the preview starts in the
    /// press's <c>.success</c>): a microphone that would not open leaves
    /// the hold listening to nothing until its release reports it, and a
    /// stream for it would cost a decode slot and a cancel for no words.
    /// </para>
    /// <para>
    /// A press into a password field never gets here: the press-time check
    /// refuses it before the microphone opens (<see cref="ResolvePress"/>),
    /// so no audio of one ever reaches a stream. The session's own probe
    /// is therefore not given one: it would ask the same question again, on
    /// this thread, which the press path keeps off it.
    /// </para>
    /// </remarks>
    private void StartPreview()
    {
        if (_preview is not { } preview || _session?.State != DictationState.Listening
            || _microphone is not { } microphone || _tap?.IsCapturing != true)
        {
            return;
        }

        DetachPreviewFeed();
        EventHandler<AudioChunk> feed = (_, chunk) => preview.Append(chunk);
        microphone.BufferCaptured += feed;
        _previewFeed = (microphone, feed);
        _previewRun = preview.Start(Attempts.AdmittedId?.ToString());
    }

    /// <summary>
    /// Follow the session out of Listening: every other state ends the hold's
    /// preview (a release has already stopped it before its decode,
    /// <see cref="PreviewFirst"/>). Not when the session is listening again
    /// by the time this repaint runs: that is a later hold's preview, and a
    /// state posted for an earlier one must not end it.
    /// </summary>
    private void FollowPreview(DictationState state)
    {
        if (state != DictationState.Listening && _session?.State != DictationState.Listening)
        {
            StopPreview();
        }
    }

    /// <summary>End this hold's preview without waiting for the worker: a
    /// cancel (Escape, or the window's or the menu's Cancel), a hold that
    /// ended without a decode, and the teardown (a pause at rest, quitting, a
    /// failure). A refused <see cref="TryPause"/> does not get here: the hold,
    /// and its preview, go on.</summary>
    private void StopPreview()
    {
        DetachPreviewFeed();
        _previewRun = 0;
        _preview?.Stop();
    }

    /// <summary>Dictation is being disarmed: end the preview for good.</summary>
    private void DisposePreview()
    {
        StopPreview();
        if (_preview is { } preview)
        {
            _preview = null;
            preview.PreviewUpdated -= OnPreviewUpdated;
            preview.Dispose();
        }
    }

    private void DetachPreviewFeed()
    {
        if (_previewFeed is { } attached)
        {
            _previewFeed = null;
            attached.Microphone.BufferCaptured -= attached.Feed;
        }
    }

    /// <summary>A push answered, on the pool's thread: drawn on the UI
    /// thread, if the run it names is still the one listening. Nothing is
    /// read here: the run comes with the text, from the session's lock.</summary>
    private void OnPreviewUpdated(object? sender, DictationPreviewUpdate update) =>
        _post(() => ShowCaption(update.Run, update.Text));

    /// <summary>
    /// The caption on the listening pill: the last seven words or sixteen
    /// CJK characters of what the preview heard (the Mac's
    /// <c>DictationPreviewCaption.window</c>), in place of "Listening".
    /// Display only: never inserted, never announced, never in any name a
    /// screen reader is given, never logged. An empty answer keeps the
    /// caption already shown, as on the Mac; the next state clears it.
    /// Dropped when it was produced for a run that has since stopped or been
    /// replaced (a re-arm stops it too), or when the hold has stopped
    /// listening since: the release is already on its way, and its words
    /// are no longer what the pill is about.
    /// </summary>
    private void ShowCaption(long run, string text)
    {
        if (_previewRun == 0 || run != _previewRun || _session?.State != DictationState.Listening)
        {
            return;
        }

        _hud?.Caption(DictationPreviewCaption.Window(text));
    }

    /// <summary>
    /// After a result, the preview of its hold against the words the final
    /// pass produced, as numbers only: how soon and how often the caption
    /// changed, the two lengths and their agreement. Never either text.
    /// Tagged with the hold's attempt id. The hold's run is stopped by then:
    /// a release stops it before its decode, and every other ending in the
    /// state the session raises just before its result.
    /// </summary>
    private void LogPreviewComparison(DictationSessionResult result)
    {
        if (_preview is not { } preview || preview.LastMetrics is not { } metrics)
        {
            return;
        }

        var final = result.Text ?? result.UndeliveredText;
        var comparison = preview.CompareLastToFinal(final);
        _log.LogInformation(
            "Dictation preview/final: attempt={Attempt} first_partial_ms={FirstPartialMs} changed={Changed} "
            + "changed_gap_p50_ms={GapP50} changed_gap_p95_ms={GapP95} preview_length={PreviewLength} "
            + "final_length={FinalLength} agreement={Agreement}",
            metrics.OperationId,
            metrics.FirstPartialMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
            metrics.ChangedPartialCount,
            metrics.ChangedGapP50Ms?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
            metrics.ChangedGapP95Ms?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
            comparison?.PreviewLength ?? metrics.LastPreviewLength,
            comparison?.FinalLength ?? final?.Length ?? 0,
            comparison is { } c ? c.Agreement.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "n/a");
    }

    /// <summary>
    /// The session's transcriber with the preview on: the worker slot, after
    /// the hold's preview has stopped. The worker answers one request at a
    /// time, so the stream is cancelled before the final decode goes down
    /// the same pipe, and the decode never waits behind a caption the user
    /// will not see. The wait is bounded, as the Mac's is
    /// (<see cref="DictationControllerOptions.PreviewStopLimit"/>): the stop
    /// detaches the run at once, so even a stop still waiting on the worker
    /// shows nothing more and leaves the next hold free to start its own.
    /// </summary>
    /// <remarks>
    /// Called on whatever thread the session's pipeline runs on, right after
    /// it moved to Transcribing; <see cref="DictationPreviewSession"/> is
    /// thread-safe. It never ends a decode: a preview that cannot stop costs
    /// the wait, not the words.
    /// </remarks>
    private sealed class PreviewFirst(
        DictationWorkerSlot worker, DictationPreviewSession preview, TimeSpan limit, ILogger log) : ITranscriber
    {
        public bool? RunsOnNpu => worker.RunsOnNpu;

        public async Task<TranscriptionResult> TranscribeAsync(MicrophoneCapture capture, CancellationToken ct)
        {
            try
            {
                await preview.StopAsync().WaitAsync(limit, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                log.LogInformation(
                    "The live preview had not stopped after {Limit:0.#} s; transcribing anyway",
                    limit.TotalSeconds);
            }

            return await worker.TranscribeAsync(capture, ct).ConfigureAwait(false);
        }

        /// <summary>Nothing to give back: the slot outlives the session, and
        /// the controller disposes it.</summary>
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
