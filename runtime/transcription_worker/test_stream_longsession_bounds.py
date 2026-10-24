"""Long-session bounds for the live caption path (soak fix C, 2026-08-25).

A 103-minute on-device meeting decayed from 26 committed lines / 15 min to 1,
with ~20-minute commit gaps and lines twice as coarse. Root causes were
O(session-length) terms on the per-push path:

  1. `_ChannelStream.history` re-concatenated the WHOLE take every push
     (the per-push worth-check forces a contiguous read) — O(n) memcpy per
     200ms push, O(n^2) per meeting, held under the channel lock that every
     background decode also needs.
  2. The mutable open segment [seg_base, now) had no bound across digital
     silence (muted mic): no turn closes, no decode fires, so the per-push
     tail check and the eventual decode snapshot paid O(mute).
  3. `stable_snapshot()` re-attributed every rolling span against every
     committed sys line on every push — O(spans x lines) pure Python,
     quadratic over the meeting.

These tests pin the bounds with the same fake-engine seams the rest of the
suite uses (no NPU, no models).
"""

from __future__ import annotations

import time

import numpy as np

from transcription_worker import ops_stream_whisper as osw
from transcription_worker.test_ops_stream_whisper import (
    FakeEngine, drain, loud, quiet)

SR = osw.SAMPLE_RATE


def _mic_channel(engine=None):
    return osw._ChannelStream(engine or FakeEngine(), osw._CommitLog(), None,
                              fixed_label="You", channel_tag="mic")


def sub_gate(seconds: float, level: float = 0.0015) -> np.ndarray:
    """Audio below PEAK_GATE (never decodable) but with distinct content, so
    buffer-correctness asserts are meaningful. level < PEAK_GATE=0.002 also
    keeps it below the turn tracker's SILENCE_PEAK=0.008: no turns, no
    decodes — pushes exercise ONLY the buffering path."""
    n = int(seconds * SR)
    ramp = (np.arange(n, dtype=np.float32) % 997) / 997.0
    return (level * (ramp - 0.5)).astype(np.float32)


# ---------------------------------------------------------------------------
# 1. history buffer: amortized in-place append, no per-push whole-take copy
# ---------------------------------------------------------------------------

def test_history_fold_is_in_place_within_capacity():
    """Once the preallocated buffer exists, further pushes must append IN
    PLACE: the buffer object survives (no whole-history realloc per push)
    and pending chunks are fully drained. This is the O(n)-per-push term
    that decayed hour-long meetings."""
    ch = _mic_channel()
    pushed = [sub_gate(2.0)]
    ch.push(pushed[0])
    with ch.lock:
        first = ch.history            # forces the first fold
        buf_id = id(ch._hist_buf)
        cap = len(ch._hist_buf)
    assert cap >= int(osw._ChannelStream.HIST_INIT_CAP_S * SR)
    assert len(first) == len(pushed[0])
    for i in range(25):               # ~5s more — far inside capacity
        chunk = sub_gate(0.2, level=0.001 + 0.00002 * i)
        pushed.append(chunk)
        ch.push(chunk)
    with ch.lock:
        h = ch.history
        assert ch._chunks == [], "pending chunks must drain into the buffer"
        assert id(ch._hist_buf) == buf_id, \
            "in-capacity appends must not reallocate the history buffer"
        assert len(ch._hist_buf) == cap
        assert np.array_equal(h, np.concatenate(pushed)), \
            "folded history must equal the pushed audio, sample for sample"


def test_history_buffer_growth_preserves_content():
    """Crossing the initial capacity doubles the buffer and keeps every
    sample; total copy work stays O(n) amortized."""
    ch = _mic_channel()
    pushed = []
    for i in range(14):               # 70s > HIST_INIT_CAP_S=60s
        chunk = sub_gate(5.0, level=0.0012 + 0.00002 * i)
        pushed.append(chunk)
        ch.push(chunk)
    with ch.lock:
        h = ch.history
        assert len(ch._hist_buf) >= 70 * SR
        assert np.array_equal(h, np.concatenate(pushed))


def test_returned_history_view_is_stable_across_later_pushes():
    """A view taken under the lock must keep its contents after later pushes
    (appends only ever write PAST a returned view's end, or into a fresh
    buffer on growth) — the re-diar path now relies on this to copy the
    session audio OFF the sys lock."""
    ch = _mic_channel()
    a = sub_gate(1.0, level=0.0011)
    ch.push(a)
    with ch.lock:
        view = ch.history
    for i in range(120):              # includes a capacity growth (>60s)
        ch.push(sub_gate(1.0, level=0.0013 + 0.00001 * i))
    assert np.array_equal(view, a), \
        "an escaped view's prefix must never be rewritten by later pushes"


# ---------------------------------------------------------------------------
# 2. mutable open segment: bounded through silence and under starvation
# ---------------------------------------------------------------------------

def test_open_segment_bounded_through_long_digital_silence():
    """A muted mic (true digital silence) must not grow the open segment
    without bound: seg_base advances over sub-PEAK_GATE audio, keeping the
    per-push tail check and any future decode snapshot O(bound), while the
    silence itself still never reaches the engine or commits a line."""
    eng = FakeEngine()
    ch = _mic_channel(eng)
    for _ in range(600):              # 120 s of mute in 200 ms pushes
        ch.push(quiet(0.2))
    with ch.lock:
        dur = ch._hist_len / SR - ch.seg_base
        seg_base = ch.seg_base
        hist_s = ch._hist_len / SR
    assert hist_s >= 119.9
    assert dur <= osw.OPEN_TRIM_MIN_S + 1.0, \
        f"open segment grew to {dur:.1f}s across digital silence"
    assert seg_base > 80.0, "seg_base never advanced over the mute"
    assert eng.calls == [], "silence must still never reach the engine"
    assert ch.commits.snapshot() == []


def test_speech_after_long_mute_still_commits_with_bounded_clips():
    """Unmuting after a long silence must behave exactly as before: the next
    utterance decodes and commits, and every engine call stays inside the
    MAX_LIVE_DECODE_S window bound (the decode clip no longer drags the
    whole mute behind it)."""
    eng = FakeEngine()
    ch = _mic_channel(eng)
    for _ in range(300):              # 60 s of mute
        ch.push(quiet(0.2))
    ch.push(loud(2.0, 0.1))
    ch.push(quiet(1.0))               # closes the turn -> boundary commit
    drain(ch)
    time.sleep(0.2)
    drain(ch)
    lines = ch.commits.snapshot()
    assert len(lines) == 1 and lines[0].startswith("[You] "), lines
    assert eng.calls, "the utterance never reached the engine"
    max_clip = max(eng.calls)
    assert max_clip <= int(osw.MAX_LIVE_DECODE_S * SR) + SR, \
        f"engine saw a {max_clip / SR:.1f}s clip — decode window unbounded"


def test_open_segment_hard_cap_under_decode_starvation():
    """When decodes stall entirely (stuck NPU: the job slot never frees),
    real speech accumulates in the open segment. The hard freshness cap must
    bound it at MAX_OPEN_SEGMENT_S — the live window drops the OLDEST audio,
    because the stop-time full-take pass owns the final transcript."""
    eng = FakeEngine()
    ch = _mic_channel(eng)
    with ch.lock:
        ch.decoding = True            # a permanently-stuck decode job
    speech = loud(250.0, 0.1)
    for i in range(250):
        ch.push(speech[i * SR:(i + 1) * SR])
    with ch.lock:
        dur = ch._hist_len / SR - ch.seg_base
    assert dur <= osw.MAX_OPEN_SEGMENT_S + 1.1, \
        f"open segment grew to {dur:.1f}s under starvation"
    assert eng.calls == []            # the stall itself: nothing decoded


# ---------------------------------------------------------------------------
# 3. stable_snapshot: O(1) on unchanged pushes, frozen attribution on change
# ---------------------------------------------------------------------------

def _stable_session():
    s = osw._Session(FakeEngine(), None, rolling_stable=True,
                     stable_window_seconds=4.0, stable_hop_seconds=2.0,
                     stable_lookahead_seconds=1.0)
    s._watchdog_stop.set()
    return s


def _inject_span(s, start, end, text):
    with s.stable_sys.lock:
        s.stable_sys._spans.append({
            "start": start, "end": end, "text": text, "language": "en",
            "channel": "sys", "speaker": "Them"})


def test_stable_snapshot_cached_on_unchanged_state():
    """Two pushes with no span/line/relabel change must return the SAME reply
    object — per-push cost is a key comparison, not an O(spans x lines)
    rescan. Any real change invalidates and re-attributes."""
    s = _stable_session()
    _inject_span(s, 0.0, 10.0, "a long stable sentence")
    snap1 = s.stable_snapshot()
    assert snap1["spans"][0]["speaker"] == "Them"
    assert s.stable_snapshot() is snap1, "unchanged state must hit the cache"
    # a new committed sys line is a state change: recompute + re-attribute
    s.commits.add("Speaker A", "most of it", channel="sys", span=(0.0, 6.0))
    snap2 = s.stable_snapshot()
    assert snap2 is not snap1
    assert snap2["spans"][0]["speaker"] == "Speaker A"
    assert s.stable_snapshot() is snap2


def test_stable_frozen_attribution_reused_across_recomputes():
    """A span far enough behind the newest committed line is attributed ONCE
    and the result reused verbatim on later recomputes (same object) — the
    quadratic rescan term is gone; only the recent tail is re-scanned."""
    s = _stable_session()
    _inject_span(s, 0.0, 10.0, "an old settled sentence")
    s.commits.add("Speaker A", "owner of the old span", channel="sys",
                  span=(0.0, 6.0))
    # a much newer line puts the old span beyond the freeze horizon
    s.commits.add("Speaker A", "much later speech", channel="sys",
                  span=(195.0, 200.0))
    snap1 = s.stable_snapshot()
    old1 = next(x for x in snap1["spans"] if x["start"] == 0.0)
    assert old1["speaker"] == "Speaker A"
    assert s._attr_frozen, "the settled span was not frozen"
    # force a recompute with an unrelated new span
    _inject_span(s, 200.0, 204.0, "brand new tail span")
    snap2 = s.stable_snapshot()
    assert snap2 is not snap1
    old2 = next(x for x in snap2["spans"] if x["start"] == 0.0)
    assert old2 is old1, \
        "frozen attribution must be reused, not recomputed per push"


def test_relabel_still_rewrites_frozen_history():
    """Retroactive speaker corrections must keep working exactly as before:
    set_speakers / relabel bump meta_version, which clears the frozen map
    and re-attributes even long-settled spans under the new labels."""
    s = _stable_session()
    _inject_span(s, 0.0, 10.0, "an old settled sentence")
    s.commits.add("Speaker A", "owner of the old span", channel="sys",
                  span=(0.0, 6.0))
    s.commits.add("Speaker A", "much later speech", channel="sys",
                  span=(195.0, 200.0))
    snap1 = s.stable_snapshot()
    assert next(x for x in snap1["spans"]
                if x["start"] == 0.0)["speaker"] == "Speaker A"
    assert s._attr_frozen
    changed = s.commits.set_speakers({0: "Speaker B"})
    assert changed == 1
    snap2 = s.stable_snapshot()
    assert snap2 is not snap1
    assert next(x for x in snap2["spans"]
                if x["start"] == 0.0)["speaker"] == "Speaker B", \
        "relabel must reach spans whose attribution had been frozen"


def test_meta_version_bumps_on_both_relabel_paths():
    log = osw._CommitLog()
    log.add("Speaker A", "remote line", channel="sys", span=(0.0, 2.0))
    v0 = log.meta_version
    log.relabel("Speaker A", "Speaker B")
    assert log.meta_version > v0
    v1 = log.meta_version
    log.set_speakers({0: "Speaker C"})
    assert log.meta_version > v1


# ---------------------------------------------------------------------------
# 4. decode/boundary completion must reconcile a seg_base that the bounding
#    pass (silence trim / freshness cap) advanced WHILE the job was in flight
# ---------------------------------------------------------------------------

def _speech_channel(seconds: float, engine=None):
    """A mic channel with `seconds` of speech folded into history and the
    background job slot pinned busy, so completion paths can be driven by
    hand with a deterministic mid-flight seg_base move."""
    eng = engine or FakeEngine()
    ch = _mic_channel(eng)
    with ch.lock:
        ch.decoding = True            # block job spawns; we drive by hand
    audio = loud(seconds, 0.1)
    step = SR
    for i in range(int(seconds)):
        ch.push(audio[i * step:(i + 1) * step])
    with ch.lock:
        ch.pending_boundaries.clear()  # force-closed turns are not the subject
        ch.seg_base = 0.0
        ch.decoded_upto = 0.0
    return ch, eng


def test_decode_completion_reconciles_mid_flight_trim():
    """seg_base advanced 5s (silence trim) while a decode of [0, 23s) ran:
    the completion must credit only the 18s still ahead of the new base —
    NOT commit a span shifted into never-decoded audio (the pre-fix code set
    decoded_upto=23 >= COMMIT_AFTER_S and pushed seg_base 5s past the end of
    the decoded audio, skipping real speech from the live window)."""
    ch, eng = _speech_channel(24.0)
    with ch.lock:
        snap = ch.history[:int(23.0 * SR)].copy()
        ch.seg_base = 5.0             # the mid-flight trim/cap advance
    ch._decode(snap, "You", 0.0)
    with ch.lock:
        assert ch.seg_base == 5.0, "no commit: only 18s < COMMIT_AFTER_S remain"
        assert abs(ch.decoded_upto - 18.0) < 1e-6, \
            f"decoded_upto must be snap_end-seg_base, got {ch.decoded_upto}"
        assert ch.seg_base + ch.decoded_upto <= ch._hist_len / SR + 1e-6
    assert ch.commits.snapshot() == []


def test_decode_commit_span_ends_at_decoded_audio_after_small_trim():
    """A small mid-flight advance (0.5s) still leaves >= COMMIT_AFTER_S of
    coverage, so the long-monologue commit fires — but its span and the new
    seg_base must end at the audio the engine actually saw (23.0s), not
    0.5s beyond it."""
    ch, eng = _speech_channel(24.0)
    with ch.lock:
        snap = ch.history[:int(23.0 * SR)].copy()
        ch.seg_base = 0.5
    ch._decode(snap, "You", 0.0)
    with ch.lock:
        assert abs(ch.seg_base - 23.0) < 1e-6, \
            f"seg_base must land at the snapshot end, got {ch.seg_base}"
        assert ch.decoded_upto == 0.0
        assert ch.seg_base <= ch._hist_len / SR + 1e-6
    events = ch.commits.drain_feed()
    assert len(events) == 1
    assert events[0]["start"] == 0.5 and abs(events[0]["end"] - 23.0) < 1e-6, \
        f"commit span must not extend past the decoded audio: {events[0]}"


def test_decode_completion_when_cap_passed_the_whole_snapshot():
    """Under starvation the freshness cap can move seg_base beyond the whole
    in-flight snapshot. The completion must then decode-credit nothing and
    leave seg_base where the cap put it — the pre-fix code committed a
    22s span of audio nobody decoded and pushed seg_base far into the
    future, stale-cut-dropping every subsequent turn."""
    ch, eng = _speech_channel(40.0)
    with ch.lock:
        snap = ch.history[:int(23.0 * SR)].copy()
        ch.seg_base = 30.0            # cap advanced past the snapshot
    ch._decode(snap, "You", 0.0)
    with ch.lock:
        assert ch.seg_base == 30.0
        assert ch.decoded_upto == 0.0
        assert ch.seg_base <= ch._hist_len / SR + 1e-6, \
            "seg_base must never end up in the future"
    assert ch.commits.snapshot() == []


def test_boundary_commit_does_not_rewind_capped_seg_base():
    """A boundary commit that finishes AFTER the freshness cap advanced
    seg_base past its cut must not rewind the base (which would re-open the
    capped window and re-drop the same audio on the next push)."""
    eng = FakeEngine()
    ch = _mic_channel(eng)
    with ch.lock:
        ch.decoding = True
    audio = loud(10.0, 0.1)
    for i in range(10):
        ch.push(audio[i * SR:(i + 1) * SR])
    with ch.lock:
        ch.pending_boundaries.clear()
        ch.seg_base = 0.0

    real_transcribe2 = ch._transcribe2

    def advancing_transcribe2(clip, language=None):
        # the cap fires on the push thread while this decode is in flight
        with ch.lock:
            ch.seg_base = 8.0
        return "a full sentence of real decoded words", None

    ch._transcribe2 = advancing_transcribe2
    try:
        ch._commit_boundary("You", 6.0)
    finally:
        ch._transcribe2 = real_transcribe2
    with ch.lock:
        assert ch.seg_base == 8.0, \
            f"boundary completion rewound a capped seg_base to {ch.seg_base}"
    lines = ch.commits.snapshot()
    assert len(lines) == 1 and lines[0].startswith("[You] ")


# ---------------------------------------------------------------------------
# 5. teardown still frees the take's audio (it lives in the buffer now)
# ---------------------------------------------------------------------------

def test_teardown_frees_history_buffer():
    s = osw._Session(FakeEngine(), None)
    s._watchdog_stop.set()
    s.mic.push(loud(1.0))
    s.sys.push(loud(1.0))
    with s.mic.lock:
        assert len(s.mic.history) > 0
    s.teardown()
    for ch in (s.mic, s.sys):
        assert ch._chunks == []
        assert ch._hist_filled == 0
        assert len(ch._hist_buf) == 0, \
            "teardown must drop the folded buffer or the OOM guard is void"
