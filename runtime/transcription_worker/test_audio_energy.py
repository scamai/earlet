"""The silence floor, held to the readings it was chosen from.

Pure numpy — no model, no NPU, no ONNX Runtime — so this runs on every machine
in the project, which is the point: the rule it pins is the one thing about
non-speech detection that does NOT depend on which Whisper is loaded.

Every constant asserted here is a measurement, not a preference. The two that
matter are from the eight-clip dictation parity corpus, measured on the
Snapdragon X Elite on 2026-08-20 (`docs/WINDOWS_DICTATION_MEASURED.md` §10):

    loudest 30 ms frame, the silent 0.5 s hold        0.00227
    loudest 30 ms frame, the quietest real hold       0.16944

The floor is 0.0040. It is 1.76x above the first and 42x below the second.
"""

import numpy as np
import pytest

from transcription_worker import audio_energy
from transcription_worker.audio_energy import (
    FRAME_SECONDS,
    SAMPLE_RATE,
    SILENCE_RMS,
    energy_reading,
    frame_rms,
    has_speech_energy,
)

# The two device readings this floor sits between. Named, so a change to
# either is a deliberate edit next to the number it came from.
SILENT_HOLD_FRAME_RMS_MAX = 0.00227      # hold_0500ms.wav, the Korean hold
QUIETEST_REAL_HOLD_FRAME_RMS_MAX = 0.16944   # hold_1700ms.wav


def noise(seconds: float, rms: float, seed: int = 20260820) -> np.ndarray:
    """White noise at an exact whole-buffer RMS. Deterministic: a gate test
    that passes on some seeds is not a gate test."""
    rng = np.random.default_rng(seed)
    x = rng.standard_normal(int(SAMPLE_RATE * seconds)).astype(np.float32)
    return (x * (rms / float(np.sqrt(np.mean(x * x))))).astype(np.float32)


# ─── The floor, against the two readings it was chosen from ───────────────


def test_the_silent_hold_that_typed_korean_is_rejected():
    # hold_0500ms.wav: 0.5 s, whole-buffer RMS 0.00167, loudest frame 0.00227.
    # Through both shipped engines at beam 4 on the device this clip came back
    # as `날씨였습니다.` (base) and `Thank you.` (turbo). It has no speech in it.
    clip = noise(0.5, 0.00167)
    assert float(frame_rms(clip).max()) < SILENCE_RMS
    assert not has_speech_energy(clip)


def test_the_quietest_real_dictation_hold_survives():
    # hold_1700ms.wav: 1.7 s, whole-buffer RMS 0.05008, loudest frame 0.16944,
    # 73 % of its frames voiced. "Testing, testing." — real speech, and the
    # quietest of the seven real holds in the corpus.
    clip = noise(1.7, 0.05008)
    assert float(frame_rms(clip).max()) > SILENCE_RMS
    assert has_speech_energy(clip)


def test_the_floor_sits_between_them_with_the_measured_margins():
    """The separation, stated as a ratio, because a threshold without its
    margins is a preference. §9.7's best model-side margin was 2.76x on base
    and 1.21x on turbo; this one is 1.76x on the near side and 42x on the far
    side, and it is identical on both checkpoints because it never asks one."""
    assert SILENCE_RMS / SILENT_HOLD_FRAME_RMS_MAX == pytest.approx(1.76, abs=0.02)
    assert (QUIETEST_REAL_HOLD_FRAME_RMS_MAX / SILENCE_RMS
            == pytest.approx(42.4, abs=0.5))


# ─── The shape of the rule ────────────────────────────────────────────────


def test_one_short_word_in_a_long_quiet_hold_survives():
    """The reason the rule is per-FRAME and not whole-buffer RMS.

    A 30 s hold containing a single 150 ms word has a whole-buffer RMS of
    about 0.006 — one and a half times the floor, close enough that a slightly
    quieter room deletes the word. Its loudest frame is unchanged at 0.09.
    """
    clip = np.zeros(SAMPLE_RATE * 30, dtype=np.float32)
    clip[: int(SAMPLE_RATE * 0.15)] = noise(0.15, 0.09)
    assert float(np.sqrt(np.mean(clip * clip))) < 10 * SILENCE_RMS
    assert has_speech_energy(clip)


def test_the_measured_quiet_room_transient_is_not_speech():
    """The reason the rule is per-frame RMS and not peak.

    Measured on the device, 2026-08-20, and recorded in `HoldTraceTests`: a
    6.7 s hold at peak 0.0351 over whole-buffer RMS 0.0016 — a working
    microphone in a room nobody was speaking into. The peak alone clears the
    0.0040 floor by 8.8x and says nothing; spread over a 30 ms frame the same
    transient reads 0.0016 and the frame rule rejects it.

    The honest limit, asserted below so nobody has to rediscover it: this is a
    rule about ENERGY DENSITY, not about impulses. One sample at 0.9 carries
    enough energy that its frame reads 0.041 and passes. A loud click is not
    filtered here; it is filtered by the model, which returns nothing for it.
    """
    quiet = np.full(SAMPLE_RATE, 0.0016, dtype=np.float32)
    quiet[100] = 0.0351
    assert float(np.abs(quiet).max()) > SILENCE_RMS      # the peak clears it
    assert not has_speech_energy(quiet)                  # the frame does not

    loud_click = np.zeros(SAMPLE_RATE, dtype=np.float32)
    loud_click[100] = 0.9
    assert has_speech_energy(loud_click), (
        "one full-scale sample spreads to 0.041 over a 30 ms frame; this rule "
        "does not claim to reject it")


def test_a_sub_frame_clip_is_judged_on_its_whole_buffer_not_called_silent():
    """"Too short to frame" is not the same fact as "silent"."""
    short = int(SAMPLE_RATE * FRAME_SECONDS) - 1
    assert has_speech_energy(np.full(short, 0.05, dtype=np.float32))
    assert not has_speech_energy(np.full(short, 0.001, dtype=np.float32))


def test_empty_audio_is_not_speech():
    assert not has_speech_energy(np.zeros(0, dtype=np.float32))
    assert frame_rms(np.zeros(0, dtype=np.float32)).size == 0


def test_digital_silence_is_not_speech():
    assert not has_speech_energy(np.zeros(SAMPLE_RATE * 3, dtype=np.float32))


# ─── The readings a rejection has to be able to show ──────────────────────


def test_a_rejection_carries_the_numbers_it_was_made_from():
    """A verdict without its margin is a category. A rejection at 0.0039 and
    one at 0.0002 are a bug and a correct call, and only the reading tells them
    apart — the same lesson `whisper_qnn` learned about `no_speech_prob`."""
    clip = noise(0.5, 0.00167)
    r = energy_reading(clip)
    assert r["floor"] == SILENCE_RMS
    assert r["frame_rms_max"] < SILENCE_RMS
    assert r["voiced_seconds"] == 0.0
    assert r["voiced_fraction"] == 0.0
    assert r["rms"] == pytest.approx(0.00167, abs=1e-5)


def test_a_real_hold_reports_voiced_seconds_that_match_its_length():
    clip = noise(1.7, 0.05008)
    r = energy_reading(clip)
    assert r["voiced_fraction"] == 1.0            # white noise is voiced throughout
    assert r["voiced_seconds"] == pytest.approx(1.68, abs=FRAME_SECONDS)
    assert r["frame_rms_max"] > SILENCE_RMS


def test_empty_audio_reports_zeros_rather_than_raising():
    r = energy_reading(np.zeros(0, dtype=np.float32))
    assert r["peak"] == 0.0 and r["frame_rms_max"] == 0.0
    assert r["floor"] == SILENCE_RMS


def test_a_sub_frame_clip_reports_its_whole_buffer_rms_not_zero():
    """`frame_rms` is empty below one frame; reporting `frame_rms_max: 0.0`
    would misdescribe the audio the sub-frame branch actually judged."""
    short = np.full(int(SAMPLE_RATE * FRAME_SECONDS) - 1, 0.05, dtype=np.float32)
    assert energy_reading(short)["frame_rms_max"] == pytest.approx(0.05, abs=1e-4)


# ─── The four copies of this rule that must not drift ─────────────────────


def test_the_mac_engine_uses_this_module_rather_than_its_own_copy():
    """`whisper_mac` carried an identical implementation of this function.
    Two copies of "what is audible" is how the pipelines drifted apart the
    first time; this asserts there is now one."""
    whisper_mac = pytest.importorskip(
        "transcription_worker.whisper_mac",
        reason="the mac engine's imports are not satisfiable on every host")
    assert whisper_mac.has_speech_energy is audio_energy.has_speech_energy
    assert whisper_mac.SILENCE_RMS == SILENCE_RMS
    assert whisper_mac.FRAME_SECONDS == FRAME_SECONDS
