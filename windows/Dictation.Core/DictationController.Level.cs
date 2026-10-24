using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the level bars on
// the floating indicator while a hold listens.
public sealed partial class DictationController
{
    // ─── The level meter ────────────────────────────────────────────────

    /// <summary>The meter of the hold that is listening, and the microphone
    /// it is attached to; null at every other time, so nothing reads the
    /// microphone's level outside a hold.</summary>
    private (DictationLevelMeter Meter, IAudioCaptureDevice Microphone)? _level;

    /// <summary>
    /// Follow the session into and out of Listening: the macOS app starts
    /// reading levels when a press is admitted and stops when the hold ends,
    /// and so does this, at the state the indicator shows.
    /// </summary>
    /// <remarks>
    /// Every way a hold ends leaves Listening first: a release goes to
    /// Transcribing, and a cancel or a capture that failed goes to Armed
    /// before the session raises its result (<c>DictationSession.Cancel</c>
    /// and <c>Complete</c>). So the result itself has nothing left to stop.
    /// What does not pass through here is stopped where it happens: a cancel,
    /// before its posted state arrives (<see cref="CancelHold"/>), and a
    /// pause or re-arm, whose session is detached first
    /// (<see cref="StopTrigger"/>).
    /// </remarks>
    private void FollowLevel(DictationState state)
    {
        if (state == DictationState.Listening)
        {
            StartLevel();
        }
        else
        {
            StopLevel();
        }
    }

    /// <summary>A hold started listening: a new meter on this arm's
    /// microphone. Nothing at all without an indicator to draw it. The bars
    /// start from the flat line because every hold that stopped listening
    /// left the indicator's level at zero (<see cref="StopLevel"/>).</summary>
    private void StartLevel()
    {
        // "_level is not null" is defensive, not reachable through the
        // session: it drops a repeated state, and a re-arm stops the meter
        // before it builds a new session (StopTrigger). It stays because a
        // second start would leak a subscription on the capture thread.
        if (_hud is null || _microphone is not { } microphone || _level is not null)
        {
            return;
        }

        var meter = new DictationLevelMeter(_options.Clock());
        microphone.BufferCaptured += meter.Offer;
        _level = (meter, microphone);
    }

    /// <summary>The tick: hand the indicator a new level about twelve times
    /// a second while a hold listens.</summary>
    private void SampleLevel()
    {
        if (_level?.Meter.Sample(_options.Clock()) is { } level)
        {
            _hud?.Level(level);
        }
    }

    /// <summary>The hold stopped listening: let go of the microphone's
    /// buffers and leave the indicator's level at zero, so the next listening
    /// pill starts from the flat line. The bars themselves give way to the
    /// next pill's dot or go with the pill, as the Mac's waveform is replaced
    /// when a hold ends.</summary>
    private void StopLevel()
    {
        if (_level is not { } level)
        {
            return;
        }

        _level = null;
        // The microphone the meter was attached to. It is always _microphone
        // today (StopTrigger stops the meter before it replaces the
        // microphone), so this is defensive: it stays right if that order
        // ever changes.
        level.Microphone.BufferCaptured -= level.Meter.Offer;
        _hud?.Level(0);
    }
}
