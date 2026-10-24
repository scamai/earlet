"""FR-001 / P0-A: continuous monologue must produce LIVE commits.

Field (real-device X-Elite QA, FR-001/P0-A): LIVE meeting-notes transcription
produced ZERO live commits on sustained/continuous monologue speech, while
paced/conversational speech worked (46 live turns). Mechanism confirmed in
transcription_worker/ops_stream_whisper.py:

  * Continuous speech never closes a turn on a pause, so seg_base only advances
    on commits. When the single shared NPU is contended, a decode job stays in
    flight (`decoding=True`), no commit fires, seg_base stalls, and the open
    segment [seg_base, now) grows to MAX_OPEN_SEGMENT_S (device: 281 s).
  * The LIVE decode of that window was UNBOUNDED: the boundary decode
    (`_commit_boundary`: clip = history[seg_base:cut_s]) and the carrier decode
    (`Channel.push`: snap = _open_segment_locked()) both snapshot the WHOLE
    open segment. `_transcribe2` windows it into <=MAX_LIVE_DECODE_S *sub-calls*
    but the ONE job still decodes ALL of it (180 s -> ~15 chunks -> ~63 s of NPU
    at RTF 0.35).
  * A single 63 s decode job blows the C# StreamTimeout (5 s) — the host logs
    "Speech worker did not answer within 5s" and kills+restarts the worker; the
    fresh worker faces the same growing window -> repeats -> 0 live commits.

The fix caps every LIVE decode window to MAX_LIVE_DECODE_S (audio older than
the freshest window is left for the accurate finalize pass, which re-decodes
the whole take — the existing design). These tests pin:

  (A) the pre-fix mechanism: a large stuck open segment fed ONE unbounded live
      decode job (asserted against a temporarily-uncapped path);
  (B) the fix: every live decode clip is <= MAX_LIVE_DECODE_S, AND continuous
      speech still commits and advances seg_base;
  (C) non-regression: the paced/short-window path is byte-unchanged.
"""

from __future__ import annotations

import time

import numpy as np

from transcription_worker import live_speakers, ops_stream_whisper as osw
from transcription_worker.test_ops_stream_whisper import FakeEngine, drain, loud

SR = osw.SAMPLE_RATE
CAP_N = int(osw.MAX_LIVE_DECODE_S * SR)


class RecordingEngine(FakeEngine):
    """FakeEngine that also records transcribe_timed clip lengths, so a test
    can assert the LARGEST SINGLE clip any live decode call saw."""

    def __init__(self):
        super().__init__()
        self.timed_calls: list[int] = []

    def transcribe_timed(self, audio, **kw):
        self.timed_calls.append(len(audio))
        return super().transcribe_timed(audio, **kw)

    def max_clip(self) -> int:
        return max([0, *self.calls, *self.timed_calls])


def _mic_channel(engine=None):
    return osw._ChannelStream(engine or RecordingEngine(), osw._CommitLog(),
                              None, fixed_label="You", channel_tag="mic")


def _accumulate_stuck_open_segment(ch, seconds: float) -> tuple[float, float]:
    """Continuous speech with the decode slot pinned busy (the death-spiral
    state): seg_base stalls and the open segment grows to the freshness cap.
    Returns (seg_base, open_segment_seconds) after the accumulation."""
    with ch.lock:
        ch.decoding = True                       # shared NPU busy elsewhere
    audio = loud(seconds, 0.12)
    step = SR
    for i in range(int(seconds)):
        ch.push(audio[i * step:(i + 1) * step])
    with ch.lock:
        ch.pending_boundaries.clear()            # force-closed turns aside
        seg_base = ch.seg_base
        open_s = ch._hist_len / SR - seg_base
    return seg_base, open_s


# ---------------------------------------------------------------------------
# (A) Pre-fix mechanism: an unbounded live decode window IS the failure.
#     Guard the regression by showing the uncapped snapshot would feed one
#     job an open-segment-sized clip (>> MAX_LIVE_DECODE_S).
# ---------------------------------------------------------------------------

def test_prefix_boundary_decode_window_is_unbounded_without_cap():
    """Documents the bug: the RAW open-segment clip a stuck boundary would
    decode is far larger than MAX_LIVE_DECODE_S. This is the window that,
    fed to one decode job, exceeds the 5 s push deadline at RTF 0.35."""
    ch = _mic_channel()
    seg_base, open_s = _accumulate_stuck_open_segment(ch, 200.0)
    with ch.lock:
        a = int(seg_base * SR)
        b = ch._hist_len
        raw_clip = ch.history[a:b]
    # The open segment reached the hard freshness cap — far above the decode
    # window bound, and far above the ~14 s (5 s / RTF 0.35) the deadline allows.
    assert open_s >= osw.OPEN_TRIM_MIN_S
    assert len(raw_clip) > CAP_N * 3, (
        f"pre-fix boundary/carrier clip is {len(raw_clip)/SR:.0f}s — a single "
        f"decode job of this window blows the 5 s deadline")


# ---------------------------------------------------------------------------
# (B) The fix: continuous monologue commits, and every live decode is bounded.
# ---------------------------------------------------------------------------

def test_boundary_decode_clip_bounded_to_max_live_decode():
    """A force-close boundary on a stuck (large) open segment must feed the
    engine at most MAX_LIVE_DECODE_S of audio in the SINGLE decode job — the
    freshest window; the older audio is left for the finalize pass."""
    eng = RecordingEngine()
    ch = _mic_channel(eng)
    seg_base, open_s = _accumulate_stuck_open_segment(ch, 200.0)
    assert open_s > osw.MAX_LIVE_DECODE_S * 3      # genuinely starved
    with ch.lock:
        ch.decoding = False
        cut_s = ch._hist_len / SR
    eng.calls.clear()
    eng.timed_calls.clear()
    ch._commit_boundary("You", cut_s)
    # No SINGLE engine call sees more than the cap. This is the deadline metric
    # (one native decode call is what blocks the worker's main loop). Every
    # window fed is <=MAX_LIVE_DECODE_S: the whole 180 s open segment is never
    # walked (pre-fix: 15 windows / 180 s decoded in one job). A commit may
    # re-decode the SAME capped clip once for a language pin — still bounded.
    assert eng.calls, "the boundary decode never ran"
    assert eng.max_clip() <= CAP_N + SR, (
        f"a boundary decode call saw {eng.max_clip()/SR:.0f}s — a live decode "
        f"must be bounded to MAX_LIVE_DECODE_S ({osw.MAX_LIVE_DECODE_S}s)")
    assert all(c <= CAP_N + SR for c in eng.calls), (
        "every window fed to the engine must be bounded, not a growing "
        "sequence over the whole open segment")
    # The turn still commits (live is not silent on monologue) and seg_base
    # advances to the cut so the window resets.
    assert ch.commits.snapshot(), "the monologue turn produced no live commit"
    with ch.lock:
        assert abs(ch.seg_base - cut_s) < 1e-6, "seg_base did not advance"


def test_carrier_decode_clip_bounded_to_max_live_decode():
    """The periodic carrier decode on a stuck (large) open segment must also
    feed the engine at most MAX_LIVE_DECODE_S in the single job. Driven through
    the REAL scheduler (Channel.push with the slot free), which is the only way
    the carrier is ever invoked in production: the cap advances seg_base to the
    freshest window and snapshots that, so the completion decodes <=12 s."""
    eng = RecordingEngine()
    ch = _mic_channel(eng)
    seg_base, open_s = _accumulate_stuck_open_segment(ch, 200.0)
    assert open_s > osw.MAX_LIVE_DECODE_S * 3
    eng.calls.clear()
    eng.timed_calls.clear()
    # Free the slot and push one more chunk with NO boundary queued -> the
    # carrier branch fires and schedules the (now capped) decode.
    with ch.lock:
        ch.decoding = False
        assert not ch.pending_boundaries
    ch.push(loud(0.3, 0.12))
    drain(ch, timeout=30.0)
    assert eng.calls or eng.timed_calls, "the carrier decode never ran"
    assert eng.max_clip() <= CAP_N + SR, (
        f"a carrier decode call saw {eng.max_clip()/SR:.0f}s — a live decode "
        f"must be bounded to MAX_LIVE_DECODE_S ({osw.MAX_LIVE_DECODE_S}s)")
    # the cap advanced seg_base so the open segment reset toward the window
    with ch.lock:
        assert ch._hist_len / SR - ch.seg_base <= osw.MAX_LIVE_DECODE_S + 1.0, (
            "carrier cap did not advance seg_base / shrink the open segment")


def test_continuous_monologue_commits_and_keeps_window_bounded_end_to_end():
    """End-to-end with the real scheduler: sustained speech (no turn-closing
    pauses) with a SLOW engine that makes decodes fall behind must still
    produce live commits, advance seg_base, and never feed one decode job more
    than MAX_LIVE_DECODE_S. Regression for FR-001/P0-A (0 live commits on
    monologue)."""
    class SlowRecordingEngine(RecordingEngine):
        # ~RTF of the clip; enough to make a >12 s window's single job blow a
        # 5 s budget, but a bounded <=12 s window stays under it.
        def transcribe(self, audio, **kw):
            self.calls.append(len(audio))
            time.sleep(min(len(audio) / SR, 30.0) * 0.02)   # scaled for CI speed
            return {"text": f"seg{len(self.calls)}:{len(audio)}"}

    eng = SlowRecordingEngine()
    ch = _mic_channel(eng)
    audio = loud(90.0, 0.12)
    chunk = int(0.2 * SR)
    for i in range(int(90.0 / 0.2)):
        ch.push(audio[i * chunk:(i + 1) * chunk])
        time.sleep(0.002)
    drain(ch, timeout=60.0)
    time.sleep(0.3)
    drain(ch, timeout=60.0)
    with ch.lock:
        seg_base = ch.seg_base
        open_s = ch._hist_len / SR - seg_base
    assert ch.commits.snapshot(), (
        "continuous monologue produced ZERO live commits (FR-001/P0-A)")
    assert seg_base > 30.0, f"seg_base stalled at {seg_base:.1f}s under monologue"
    assert eng.max_clip() <= CAP_N + SR, (
        f"a live decode saw a {eng.max_clip()/SR:.0f}s clip — window unbounded")
    # the open segment stays small once decodes are bounded (no death-spiral)
    assert open_s <= osw.MAX_LIVE_DECODE_S + live_speakers.MAX_OPEN_TURN_S + 2.0, (
        f"open segment grew to {open_s:.0f}s — the window is not bounded")


def test_env_flag_restores_prior_unbounded_window():
    """The cap is behavioural, so a disable knob must restore the pre-fix path
    byte-for-byte: with the flag off, a stuck boundary decodes the WHOLE open
    segment again (the audit/rollback lever)."""
    import os
    eng = RecordingEngine()
    ch = _mic_channel(eng)
    _seg_base, open_s = _accumulate_stuck_open_segment(ch, 200.0)
    with ch.lock:
        ch.decoding = False
        cut_s = ch._hist_len / SR
    eng.calls.clear()
    eng.timed_calls.clear()
    prev = os.environ.get("DICTATION_NOTES_LIVE_DECODE_CAP")
    os.environ["DICTATION_NOTES_LIVE_DECODE_CAP"] = "0"
    try:
        ch._commit_boundary("You", cut_s)
    finally:
        if prev is None:
            os.environ.pop("DICTATION_NOTES_LIVE_DECODE_CAP", None)
        else:
            os.environ["DICTATION_NOTES_LIVE_DECODE_CAP"] = prev
    total = sum(eng.calls) + sum(eng.timed_calls)
    assert total > CAP_N * 3, (
        "flag off must restore the pre-fix unbounded window (rollback lever)")


# ---------------------------------------------------------------------------
# (C) Non-regression: the paced / short-window path is unchanged.
# ---------------------------------------------------------------------------

def test_paced_speech_short_window_unchanged():
    """A short utterance followed by a pause (the WORKING case) decodes its
    whole window exactly as before — the cap only ever bites a window LONGER
    than MAX_LIVE_DECODE_S, which paced speech never reaches."""
    eng = RecordingEngine()
    ch = _mic_channel(eng)
    ch.push(loud(2.0, 0.12))
    ch.push(osw.np.zeros(int(1.0 * SR), dtype=osw.np.float32))  # closes turn
    drain(ch)
    time.sleep(0.2)
    drain(ch)
    lines = ch.commits.snapshot()
    assert len(lines) == 1 and lines[0].startswith("[You] "), lines
    # the engine saw the whole ~2 s window (never chopped by the cap)
    assert eng.calls, "the utterance never reached the engine"
    assert max(eng.calls) <= CAP_N, "a short window must not be capped"
    assert max(eng.calls) >= int(1.5 * SR), (
        "a paced <=MAX_LIVE_DECODE_S window must be decoded whole, not chopped")
