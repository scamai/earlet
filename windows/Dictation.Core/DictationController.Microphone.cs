using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the microphone
// itself. The watch on Windows' microphone switch (DictationMicrophoneWatch),
// which says when access is lost or comes back without waiting for a hold to
// fail, and a microphone that goes away while a hold listens, which ends that
// hold and transcribes what it captured (DictationController.Takes.cs). Hooks, one line each: Tick
// (WatchMicrophoneAccess), Arm and StopTrigger (WatchMicrophoneEndpoint,
// StopWatchingMicrophoneEndpoint), RecheckMicrophone (MicrophoneNoLongerBlocked),
// ChooseHoldKey and AfterRestart (ReadyUnlessBlocked) and Completed (the
// sentence and the indicator's context).
public sealed partial class DictationController
{
    // ─── Microphone access ──────────────────────────────────────────────

    /// <summary>When the watch last read the switch, or began to watch; null
    /// while nothing is watched.</summary>
    private TimeSpan? _microphoneReadAt;

    /// <summary>A reading on its way back from the thread pool. One at a
    /// time: a registry walk that is slow is not asked again on top of itself.</summary>
    private Task<MicrophoneAccess>? _microphoneReading;

    /// <summary>
    /// Once per tick: ask Windows again whether it blocks the microphone,
    /// every 5 seconds while dictation is on and every second while this run
    /// knows it does (<see cref="DictationMicrophoneWatch.Interval"/>). Only
    /// at rest: never while a press is being decided or a dictation listens,
    /// transcribes or types, whose own ending reports a block, and whose
    /// status line a reading must not replace. The read runs off the UI
    /// thread (<see cref="DictationControllerOptions.RunMicrophoneProbe"/>),
    /// and what the run was when it asked goes with it, so that
    /// <see cref="MicrophoneAccessRead"/> can tell an answer the world has
    /// moved past.
    /// </summary>
    private void WatchMicrophoneAccess()
    {
        var enabled = _session is not null;
        if (_disposed || DictationMicrophoneWatch.Interval(enabled, _microphoneBlocked) is not { } every)
        {
            _microphoneReadAt = null;
            return;
        }

        var now = _options.Clock();
        _microphoneReadAt ??= now;
        if (_microphoneReading is not null || !AtRest || _fieldCheck is not null || now - _microphoneReadAt.Value < every)
        {
            return;
        }

        _microphoneReadAt = now;
        var press = _press;
        var blockedKnown = _microphoneBlocked;
        var probe = _platform.MicrophoneAccess;
        Task<MicrophoneAccess> reading;
        try
        {
            reading = _options.RunMicrophoneProbe(() => ReadMicrophoneAccess(probe));
        }
        catch (Exception error)
        {
            _log.LogDebug(error, "The microphone access watch could not start a reading");
            return;
        }

        _microphoneReading = reading;
        reading.ContinueWith(
            _ => _post(() => MicrophoneAccessRead(reading, enabled, press, blockedKnown)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>On the thread pool. The probe promises not to throw; a
    /// stand-in that does reads as nothing learned.</summary>
    private MicrophoneAccess ReadMicrophoneAccess(IMicrophoneAccessProbe probe)
    {
        try
        {
            return probe.Probe();
        }
        catch (Exception error)
        {
            _log.LogDebug(error, "Microphone access could not be read by the watch");
            return MicrophoneAccess.Unknown;
        }
    }

    /// <summary>
    /// A reading is back on the UI thread. Dropped when the world moved under
    /// it, and the next due reading asks again: a press began since it was
    /// asked (whether that press is still being decided, listens, or has
    /// finished and learned of a block itself), dictation was turned on or
    /// off, something else learned about the microphone (Check again, the
    /// recheck when the window is activated, a hold that Windows kept the
    /// microphone from, a delivered dictation), or the app is busy or closing
    /// (<see cref="AtRest"/>). An answer from before any of those is older
    /// than what the run now knows, and applied it could clear a block a hold
    /// has just met, or bring the window forward while a press is undecided.
    /// Otherwise a loss says so in the window and the icon and brings the
    /// window forward once, and a regain takes the warning away
    /// (<see cref="DictationMicrophoneWatch.Judge"/>).
    /// </summary>
    /// <param name="reading">The finished reading.</param>
    /// <param name="enabled">Whether dictation was on when it was asked.</param>
    /// <param name="press">The press count when it was asked.</param>
    /// <param name="blockedKnown">Whether the run knew of a block when it was asked.</param>
    private void MicrophoneAccessRead(Task<MicrophoneAccess> reading, bool enabled, int press, bool blockedKnown)
    {
        if (ReferenceEquals(reading, _microphoneReading))
        {
            _microphoneReading = null;
        }

        // No reading is asked while a press is being decided, so a press
        // still being decided when one lands began after it was asked: the
        // press count answers for it.
        if (press != _press
            || (_session is not null) != enabled
            || blockedKnown != _microphoneBlocked
            || !AtRest)
        {
            _log.LogDebug("A microphone access reading the run had moved past was dropped");
            return;
        }

        var access = reading.IsCompletedSuccessfully ? reading.Result : MicrophoneAccess.Unknown;
        switch (DictationMicrophoneWatch.Judge(_microphoneBlocked, access))
        {
            case MicrophoneWatchVerdict.Lost:
                // Only while on: off, nothing is watched until a block is
                // known (DictationMicrophoneWatch.Interval).
                _log.LogWarning("Windows now blocks the microphone ({Access}); the window comes forward", access);
                // A hold-key sentence the hook watch put on the line stays,
                // after the microphone's (DictationCopy.WithHoldKeySentence).
                Render(DictationCopy.WithHoldKeySentence(
                    DictationCopy.ForMicrophone(access)!, DictationCopy.HoldKeySentenceIn(_status, _holdKey)));
                // Once: the flag Render just set keeps a block that stays from
                // asking again, so a window the user closes stays closed. The
                // Mac's DictationWindowAttention brings its window forward on
                // the same edge.
                _view.Reveal();
                break;

            case MicrophoneWatchVerdict.Regained:
                MicrophoneNoLongerBlocked(access);
                break;
        }
    }

    /// <summary>
    /// Windows no longer blocks the microphone this run knew to be blocked:
    /// the warning goes from the icon, "Try dictation" comes back, and a
    /// blocked sentence still on the status line goes back to what dictation
    /// is doing (off, a blocked sentence is the paused one, and it goes back
    /// to saying paused), or to the hold key's sentence when the hook watch's
    /// shared the line. Any other status line, newer than the block, stays.
    /// The watch's regain and <see cref="RecheckMicrophone"/>'s.
    /// </summary>
    private void MicrophoneNoLongerBlocked(MicrophoneAccess access)
    {
        _log.LogInformation("Microphone access is no longer blocked ({Access})", access);
        _microphoneBlocked = false;
        if (DictationCopy.IsMicrophoneBlocked(_status))
        {
            // Only the microphone's half is untrue now: a hold-key sentence
            // that shared the line stays on it.
            Render(_status.HoldKeySentence is { } holdKey
                ? new DictationStatus(holdKey)
                : new DictationStatus(_session is not null ? ArmedLine : DictationCopy.Paused));
        }
        else
        {
            Render();
        }
    }

    /// <summary>
    /// <paramref name="ready"/>, a line that invites the next hold (dictation
    /// re-armed on a new key, the speech engine back), unless this run knows
    /// Windows blocks the microphone: then the blocked sentence with its
    /// buttons, which a hold would only meet again. The window's header says
    /// "Microphone blocked" then (<see cref="DictationTrayGlyph"/>), and the
    /// Mac's header shows its setup step whenever a grant is missing and
    /// nothing is recording, so an invitation under it would contradict it.
    /// A hold-key sentence that rode on the blocked line belonged to the hook
    /// that line was written under, and is not carried.
    /// </summary>
    private DictationStatus ReadyUnlessBlocked(string ready) =>
        _microphoneBlocked && _microphoneBlockedBy is { } blocked
            ? blocked with { HoldKeySentence = null }
            : new DictationStatus(ready);

    // ─── A microphone change during a hold ──────────────────────────────

    /// <summary>Arm's: hear about the microphone changing under an open
    /// capture, from a device that can say (WASAPI's endpoint notifications).</summary>
    private void WatchMicrophoneEndpoint()
    {
        if (_microphone is IAudioCaptureEndpointWatch endpoints)
        {
            endpoints.EndpointChanged += OnMicrophoneEndpointChanged;
        }
    }

    /// <summary>StopTrigger's, before the microphone is disposed.</summary>
    private void StopWatchingMicrophoneEndpoint()
    {
        if (_microphone is IAudioCaptureEndpointWatch endpoints)
        {
            endpoints.EndpointChanged -= OnMicrophoneEndpointChanged;
        }
    }

    /// <summary>On the thread pool: nothing is read here.</summary>
    private void OnMicrophoneEndpointChanged(object? sender, AudioCaptureEndpointChange change) =>
        _post(() => MicrophoneEndpointChanged(sender, change));

    /// <summary>
    /// The microphone the listening hold records from was unplugged,
    /// disabled or removed. The hold ends here as a release would, and what
    /// it captured up to the change is transcribed and inserted: "The
    /// microphone stopped at 0:42, so the recording ended there. Your words up
    /// to then went in. The next hold uses …" (LIMIT 2026-10-10, rule zero:
    /// no stop loses words). A key still held starts nothing until it comes up
    /// and is pressed again; a hands-free latch ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Mac's device-gone trigger. The Mac's in-hold watchdog (D15) binds
    /// a device at the press and acts only on a dead input (the bound device
    /// gone, no audio for 300 ms, a stale stream that also went silent), then
    /// rebinds, records on to the release and, today, discards the whole
    /// hold; a default that moves while the bound microphone still delivers
    /// (a headset plugged in) does not interrupt it, and neither does it here:
    /// the device reports no default changes. Windows has no rebind, so it
    /// ends the hold when the removal arrives and keeps what it captured. The
    /// no-audio trigger is the take watchdog (<see cref="DictationCaptureWatchdog"/>),
    /// and a stream Windows stops with an error is the fault watch
    /// (<c>IAudioCaptureFaultWatch</c>); both keep the take the same way.
    /// </para>
    /// <para>
    /// Only for the capture that is open now, the one the change was seen
    /// during: a change that reaches the UI thread after its hold was
    /// released, cancelled or replaced is dropped, as is one from a microphone
    /// an earlier arm opened, and one from the last capture that lands while
    /// the next press is still being decided (no capture is open then, so its
    /// number still matches). The tap captures only while a hold listens,
    /// held or hands-free; once the hold is transcribing, its audio is already
    /// captured and a change concerns nothing.
    /// </para>
    /// </remarks>
    private void MicrophoneEndpointChanged(object? sender, AudioCaptureEndpointChange change)
    {
        if (!ReferenceEquals(sender, _microphone)
            || sender is not IAudioCaptureEndpointWatch endpoints
            || change.Capture != endpoints.Capture
            || _tap is not { IsCapturing: true })
        {
            _log.LogDebug("A microphone change ({Change}) that concerned no listening hold was ignored", change.Kind);
            return;
        }

        // The kind only: the device logs which microphone the next hold opens.
        _log.LogInformation(
            "The microphone changed while the hold listened ({Change}); transcribing what was captured", change.Kind);
        StopTake(DictationTakeStop.MicrophoneRemoved, change.NewEndpointName);
    }
}
