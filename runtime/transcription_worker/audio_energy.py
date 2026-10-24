"""Was there any speech-level energy in this audio? — asked once, in one place.

WHY THIS IS ITS OWN MODULE, AND WHY IT IS NOT A MODEL QUESTION.

Whisper's own answer to "is this speech" is the probability it puts on
``<|nospeech|>`` at the SOT step, and on ``whisper-base`` that answer works:
measured on the X Elite, non-speech reads 0.86–0.94 and speech reads
0.004–0.12, a 7x gap with the 0.75 bar inside it. On
``large-v3-turbo`` the same reading is **1e-13 to 1e-10 on every input,
speech included** — nine to eleven orders of magnitude below any usable bar,
so both of ``beam_decode.SpeechGate``'s rules are dead and the model ships
with no non-speech gate at all. That is measured, twice, in
``docs/the platform measurements`` §9.3 and §9.7: turbo rejected **0 of
224** real microphone segments where base rejected 56, and typed words into
130 of the 131 segments base left empty — 85 of them the phrase "Thank you."

The mistake in every attempt to repair that was to look for a better
threshold on a number the model does not produce. §9.7 swept rule 2 and
found no bar that is both safe and effective: any bar high enough to clear
turbo's noise readings starts deleting real sentences.

**So the silence question is answered from the audio instead**, where the
separation is not marginal. On the eight-clip parity corpus, measured
2026-08-20 on the device (see ``energy_reading`` for the table):

    quietest real dictation hold, loudest 30 ms frame   0.16944
    the silent hold that typed Korean                   0.00227
    the floor this module uses                          0.00400

The floor sits **1.76x above** the silent hold and **42x below** the
quietest real hold. Compare §9.7's best model-side margin: 2.76x on base and
1.21x on turbo. This is a different order of separability, and it is the
same on both models because it never asks the model anything.

The gate is also free in a way the model's is not: rejecting before the
encoder runs saves turbo's ~600 ms encoder pass on audio that had nothing
in it.

WHERE ELSE THIS EXACT RULE LIVES. Four copies, deliberately identical:
``whisper_mac.has_speech_energy`` (which is now this function, re-exported),
the Swift segmenter (``SegmenterConstants.frameSeconds`` 0.03 /
``silenceRMS`` 0.004), ``eval/meeting_notes_eval/run_eval.py``, and
``WorkflowAudio.HasSpeechEnergy`` on the Windows capture side. If you change
the numbers here, change them all in the same commit — three copies of "what
is audible" is how the two pipelines drifted apart the first time.
"""

from __future__ import annotations

import numpy as np

SAMPLE_RATE = 16000

# Frame length and RMS floor for the energy check. See the module docstring
# for the four places these numbers are duplicated on purpose.
FRAME_SECONDS = 0.03
SILENCE_RMS = 0.004


def frame_rms(audio, sample_rate: int = SAMPLE_RATE,
              frame_seconds: float = FRAME_SECONDS) -> np.ndarray:
    """Per-frame RMS over non-overlapping ``frame_seconds`` windows.

    Empty for audio shorter than one frame — callers that must answer for a
    sub-frame clip fall back to whole-buffer RMS (see ``has_speech_energy``),
    because "too short to frame" is not the same fact as "silent".
    """
    arr = np.asarray(audio, dtype=np.float32)
    frame = int(sample_rate * frame_seconds)
    if arr.size == 0 or frame <= 0 or arr.size < frame:
        return np.zeros(0, dtype=np.float32)
    count = arr.size // frame
    frames = arr[: count * frame].reshape(count, frame)
    return np.sqrt(np.mean(frames * frames, axis=1)).astype(np.float32)


def has_speech_energy(audio, threshold: float = SILENCE_RMS,
                      sample_rate: int = SAMPLE_RATE) -> bool:
    """True when ANY analysis frame of ``audio`` clears ``threshold`` RMS.

    Deliberately "nothing crossed the line at all", NOT a
    minimum-voiced-duration: a per-pipeline voiced floor is a POLICY, and
    policy lives at the capture layer (the Swift gate's
    ``AsrGatePolicy``, and ``MicrophoneTap`` on Windows)
    where dictation and meetings can legitimately differ. This is the floor
    underneath both, and it must never eat a real 120 ms word from a client
    that gated correctly.

    It is STRONGER than a whole-clip peak test and WEAKER than a whole-clip
    RMS test, and both directions are intentional: the frame is the unit a
    decoder actually sees. Stronger, at the level that matters — the device's
    measured quiet-room hold (peak 0.0351 over RMS 0.0016) clears the floor
    on peak by 8.8x and fails it on frame RMS. Weaker where it must be — a
    long hold carrying one short sentence has a whole-clip RMS near the
    floor and a loud frame.

    What it does NOT claim: this is a rule about energy DENSITY, not about
    impulses. A single full-scale sample spreads to 0.041 over a 30 ms frame
    and passes. A loud click is not filtered here; it is filtered by the
    model, which returns nothing for it.
    """
    arr = np.asarray(audio, dtype=np.float32)
    frame = int(sample_rate * FRAME_SECONDS)
    if arr.size == 0 or frame <= 0:
        return False
    if arr.size < frame:
        # Sub-frame clip: fall back to whole-buffer RMS rather than reporting
        # "no speech" for audio too short to frame.
        return bool(np.sqrt(np.mean(arr * arr)) > threshold)
    return bool(np.any(frame_rms(arr, sample_rate) > threshold))


def energy_reading(audio, sample_rate: int = SAMPLE_RATE,
                   threshold: float = SILENCE_RMS) -> dict:
    """The numbers a rejection has to be able to show, as plain floats.

    A verdict without its readings is a category, not a measurement: a gate
    that ate a real sentence and a gate working correctly reach the log as
    the same sentence unless the margin travels with the answer. This is the
    same lesson ``whisper_qnn`` learned about ``no_speech_prob`` — see the
    long comment in its chunk loop.

    Keys: ``peak``, ``rms`` (whole buffer), ``frame_rms_max`` (the loudest
    30 ms frame — the reading the verdict is actually made from),
    ``voiced_seconds`` and ``voiced_fraction`` (frames over the floor), and
    ``floor``.
    """
    arr = np.asarray(audio, dtype=np.float32)
    if arr.size == 0:
        return {"peak": 0.0, "rms": 0.0, "frame_rms_max": 0.0,
                "voiced_seconds": 0.0, "voiced_fraction": 0.0,
                "floor": float(threshold)}
    rms = frame_rms(arr, sample_rate)
    voiced = rms > threshold
    whole = float(np.sqrt(np.mean(arr * arr)))
    return {
        "peak": round(float(np.abs(arr).max()), 5),
        "rms": round(whole, 5),
        # Sub-frame clips have no frames; the whole-buffer RMS is the only
        # reading there is, and reporting 0.0 would misdescribe the audio the
        # sub-frame branch of has_speech_energy actually judged.
        "frame_rms_max": round(float(rms.max()) if rms.size else whole, 5),
        "voiced_seconds": round(float(np.count_nonzero(voiced) * FRAME_SECONDS), 3),
        "voiced_fraction": round(float(np.mean(voiced)) if rms.size else 0.0, 4),
        "floor": float(threshold),
    }
