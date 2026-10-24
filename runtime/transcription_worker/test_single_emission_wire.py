"""Wire tests for single-emission streaming diarization
(DICTATION_NOTES_SINGLE_EMISSION=1, plan 2026-09-01-single-emission-diar Task 3).

Contract under test, at the ops_stream_whisper seam:
  (a) mode on: after scripted dz-bank rediar rounds with the stream clock
      aged past hold_s, drain_feed() contains EXACTLY the matured lines —
      speaker letters from the cluster output space, text joined from the
      internal carrier lines — and drain_relabels() == [] always;
  (b) mode on: internal energy-path sys lines are feed-suppressed carriers
      (they stay in lines/line_meta, never in feed_events); mic flows as is;
  (c) mode off (env unset / no hold built): the legacy dz path is untouched
      — the letter-stability scenario from test_diarizen_cache_recluster
      still holds through the same _cheap_rediar_once entry;
  (d) finish() flushes the hold's pending lines exactly once.

FakeSession pattern follows test_diarizen_cache_recluster._dz_session: real
_Session methods bound onto a scripted fake; the commit log is the REAL
_CommitLog so feed/relabel wire semantics are the production ones. Time is
the STREAM clock (now_s = seconds of sys audio ingested), so tests age the
clock by growing the fake sys history — no wall-clock fakery needed.
"""
import threading

import pytest
from collections import namedtuple

import numpy as np

from transcription_worker import ops_stream_whisper as osw
from transcription_worker.emission_hold import EmissionHold

SR = osw.SAMPLE_RATE


@pytest.fixture(autouse=True)
def _legacy_release_geometry(monkeypatch):
    """These wire tests characterize single-emission's release via the
    _cheap_rediar_once / turn_texts entry — the LEGACY turn-geometry release
    path. The 2026-09-09 default flip to local geometry routes release through
    the TurnBookV2 + confidence-ladder instead (device-verified live: captions
    + names released under single-emission+local), which changes the exact
    drain_feed these assert. Pin them to the path they were written for;
    local-geometry single-emission release is device-covered and is a separate
    unit-coverage item."""
    monkeypatch.setenv("DICTATION_NOTES_TURN_GEOMETRY", "legacy")

Snap = namedtuple("Snap", "starts bins assign num_frames frame_dur n_out")


def _two_speaker_state():
    """One 16 s window (100 frames @0.16 s): track0 -> output speaker 0 over
    [0, 8), track1 -> output speaker 1 over [8, 16). neural_turns cuts this
    into exactly two turns at the speaker change."""
    F = 100
    b = np.zeros((F, 4), dtype=np.int8)
    b[:50, 0] = 1
    b[50:, 1] = 1
    assign = np.full((1, 4), -1, dtype=np.int64)
    assign[0, 0] = 0
    assign[0, 1] = 1
    return Snap(starts=(0,), bins=(b,), assign=assign,
                num_frames=F, frame_dur=0.16, n_out=2)


class _NullLock:
    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


class _WireBank:
    """Scripted DZ bank: cluster_with_centroids publishes the scripted
    LastState-shaped snapshot exactly like the real EmbeddingBank, and
    label_for_span answers in the same output-speaker space (speaker 0
    before 8 s, speaker 1 after — matching _two_speaker_state)."""

    def __init__(self, rounds):
        self.rounds = list(rounds)          # [(segs, cents, state), ...]
        self.last_state = None
        self.label_calls = []

    def update(self, audio):
        return 1

    def cluster_with_centroids(self, audio, total_dur=None, num_speakers=None):
        segs, cents, state = self.rounds.pop(0)
        self.last_state = state
        return segs, cents

    def label_for_span(self, start_s, end_s):
        self.label_calls.append((start_s, end_s))
        mid = (start_s + end_s) / 2.0
        return (0 if mid < 8.0 else 1), (end_s - start_s)


class _MutableSys:
    """sys stand-in whose history length IS the stream clock."""
    lock = _NullLock()

    def __init__(self, seconds):
        self.set_seconds(seconds)

    def set_seconds(self, seconds):
        self.history = np.zeros(int(seconds * SR), dtype=np.float32)


def _mode_session(rounds, hold_s, sys_seconds):
    """A single-emission-mode fake session running the REAL wiring:
    _cheap_rediar_once + the hold/turn/text/emit methods, a REAL _CommitLog,
    and legacy-tail methods that explode if the mode ever touches them."""

    class FakeSession:
        _num_speakers = None
        _bo_bank = None
        _dz_warmup_failed = False
        _registry = None
        _bo_letter_book = {}
        RELABEL_MIN_RATIO = 0.0
        RELABEL_MIN_ABS_S = 0.0
        # the REAL wiring under test, bound to the fake
        _cheap_rediar_once = osw._Session._cheap_rediar_once
        _single_emission_tick = osw._Session._single_emission_tick
        _single_emission_flush = osw._Session._single_emission_flush
        _emit_single_emission_line = osw._Session._emit_single_emission_line
        _text_for_span = osw._Session._text_for_span
        # turn-driven span decode (2026-09-01): real methods; with the fake
        # sys carrying no engine the decode records a failure and the hold
        # falls back to the carrier join — the legacy contract above holds.
        _turn_decode_on = osw._Session._turn_decode_on
        _ensure_turn_decode_state = osw._Session._ensure_turn_decode_state
        _turn_key = staticmethod(osw._Session._turn_key)
        _request_turn_decode = osw._Session._request_turn_decode
        _turn_decode_worker = osw._Session._turn_decode_worker
        _turn_text_for_span = osw._Session._turn_text_for_span
        _turn_bucket = staticmethod(osw._Session._turn_bucket)
        _decode_when_settled = osw._Session._decode_when_settled
        _TURN_DECODE_STABLE_S = osw._Session._TURN_DECODE_STABLE_S
        _TURN_DECODE_PAD_S = osw._Session._TURN_DECODE_PAD_S

        # legacy relabel tail: must NEVER run in single-emission mode
        def _bo_letters_by_overlap(self, *a, **k):
            raise AssertionError(
                "legacy letter assignment ran in single-emission mode")

        def _apply_rediar_segs(self, *a, **k):
            raise AssertionError(
                "_apply_rediar_segs ran in single-emission mode")

    sess = FakeSession()
    sess._watchdog_stop = threading.Event()
    sess.sys = _MutableSys(sys_seconds)
    sess._dz_bank = _WireBank(rounds)
    sess.commits = osw._CommitLog()
    sess.commits.single_emission = True
    sess._hold = EmissionHold(hold_s, text_deadline_s=3.0)
    return sess


def _round(state):
    return ([(0.0, 8.0, 0), (8.0, 16.0, 1)],
            {0: np.ones(4, dtype=np.float32), 1: np.ones(4, dtype=np.float32)},
            state)


# ---------------------------------------------------------------------------
# (a) two rediar rounds -> exactly the matured lines, zero relabels
# ---------------------------------------------------------------------------

def test_mode_on_two_rounds_release_matured_lines():
    state = _two_speaker_state()
    sess = _mode_session([_round(state), _round(state)],
                         hold_s=4.0, sys_seconds=10.0)
    # internal carrier lines (deliberately mislabeled "Speaker Z" to prove
    # released letters come from the NEURAL path, not the carrier labels)
    sess.commits.add("Speaker Z", "hello from the first speaker",
                     channel="sys", span=(0.5, 7.5))
    sess.commits.add("Speaker Z", "and the second speaker replies",
                     channel="sys", span=(8.5, 15.5))
    sess.commits.add("You", "my own mic words", channel="mic", span=(1.0, 2.0))

    # round 1 @10 s ingested: both turn tails younger than hold -> nothing
    assert osw._Session._cheap_rediar_once(sess) is True
    feed = sess.commits.drain_feed()
    assert [e["channel"] for e in feed] == ["mic"]      # only the mic line
    assert sess.commits.drain_relabels() == []

    # round 2 @24 s ingested: both turns aged past hold_s -> both release,
    # in start order, exactly once, with cluster-space letters + joined text
    sess.sys.set_seconds(24.0)
    assert osw._Session._cheap_rediar_once(sess) is True
    feed = sess.commits.drain_feed()
    assert [(e["speaker"], e["text"], e["channel"], e["start"], e["end"])
            for e in feed] == [
        ("Speaker A", "hello from the first speaker", "sys", 0.0, 8.0),
        ("Speaker B", "and the second speaker replies", "sys", 8.0, 16.0)]
    assert all(isinstance(e["committed_at"], float) for e in feed)
    assert sess.commits.drain_relabels() == []

    # a third pass over the same turns must not re-emit (exactly-once)
    sess.sys.set_seconds(30.0)
    sess._dz_bank.rounds.append(_round(state))
    assert osw._Session._cheap_rediar_once(sess) is True
    assert sess.commits.drain_feed() == []
    assert sess.commits.drain_relabels() == []


def test_mode_on_relabel_surface_is_inert():
    """No caller (registry check_merge, consolidate, a stray corrector) can
    ever append a relabel event in mode — the CommitLog guard is structural.
    Carrier metadata stays untouched (it is text-only source material)."""
    sess = _mode_session([], hold_s=4.0, sys_seconds=1.0)
    sess.commits.add("Speaker A", "carrier line one",
                     channel="sys", span=(0.0, 2.0))
    sess.commits.relabel("Speaker A", "Speaker B")
    assert sess.commits.set_speakers({0: "Speaker Q"}) == 0
    assert sess.commits.drain_relabels() == []
    assert sess.commits.line_meta[0]["speaker"] == "Speaker A"
    assert sess.commits.snapshot() == ["[Speaker A] carrier line one"]


# ---------------------------------------------------------------------------
# (b) internal sys lines are carriers: committed internally, never fed
# ---------------------------------------------------------------------------

def test_mode_on_carrier_sys_lines_never_reach_feed():
    sess = _mode_session([], hold_s=4.0, sys_seconds=1.0)
    sess.commits.add("Speaker A", "internal carrier text",
                     channel="sys", span=(0.0, 2.0))
    sess.commits.add("You", "mic flows normally",
                     channel="mic", span=(0.0, 1.0))
    feed = sess.commits.drain_feed()
    assert [(e["channel"], e["text"]) for e in feed] == [
        ("mic", "mic flows normally")]
    # ...but the carrier IS committed internally (the hold's text source)
    assert sess.commits.snapshot() == ["[Speaker A] internal carrier text",
                                       "[You] mic flows normally"]
    assert sess._text_for_span(0.0, 2.0) == "internal carrier text"


def test_text_for_span_majority_overlap_and_time_order():
    """>50%-of-line-duration overlap gates membership; join is time-ordered;
    the embedded '[Speaker X] ' prefix is stripped; mic lines never leak."""
    sess = _mode_session([], hold_s=4.0, sys_seconds=1.0)
    sess.commits.add("Speaker B", "second words", channel="sys",
                     span=(4.0, 6.0))
    sess.commits.add("Speaker A", "first words", channel="sys",
                     span=(0.5, 3.5))
    sess.commits.add("Speaker A", "mostly outside", channel="sys",
                     span=(7.5, 11.5))                 # 0.5/4.0 inside: out
    sess.commits.add("You", "mic text", channel="mic", span=(1.0, 2.0))
    assert sess._text_for_span(0.0, 8.0) == "first words second words"
    assert sess._text_for_span(20.0, 30.0) == ""


# ---------------------------------------------------------------------------
# (c) mode off: the legacy dz path is byte-identical (env unset, no hold)
# ---------------------------------------------------------------------------

def test_mode_off_env_unset_legacy_permute_scenario(monkeypatch):
    """The letter-stability scenario from test_diarizen_cache_recluster runs
    unchanged through the same _cheap_rediar_once entry when no hold exists:
    the retro-relabel tail fires exactly as before the wire landed."""
    monkeypatch.delenv("DICTATION_NOTES_SINGLE_EMISSION", raising=False)
    from transcription_worker.test_diarizen_cache_recluster import (
        _dz_session, _sys_line)

    e0 = np.array([1.0, 0, 0, 0], dtype=np.float32)
    e1 = np.array([0, 1.0, 0, 0], dtype=np.float32)
    rounds = [
        ([(0.0, 2.0, 0), (2.0, 4.0, 1)], {0: e0, 1: e1}),
        ([(0.0, 0.5, 0), (0.5, 2.0, 1), (2.0, 4.0, 0)], {0: e1, 1: e0}),
    ]
    lines = [_sys_line("Speaker A", 0.0, 2.0), _sys_line("Speaker A", 2.0, 4.0)]
    sess = _dz_session(osw, rounds, lines)

    assert osw._Session._cheap_rediar_once(sess) is True   # round 1
    assert [m["speaker"] for m in lines] == ["Speaker A", "Speaker B"]
    assert sess.commits.change_counts == [1]

    assert osw._Session._cheap_rediar_once(sess) is True   # round 2
    assert [m["speaker"] for m in lines] == ["Speaker A", "Speaker B"]
    assert sess.commits.change_counts == [1, 0]


def test_mode_off_commitlog_feed_and_relabel_unchanged(monkeypatch):
    """Flag off, the CommitLog surface is the legacy one: sys commits reach
    the feed immediately and set_speakers emits relabel events."""
    monkeypatch.delenv("DICTATION_NOTES_SINGLE_EMISSION", raising=False)
    log = osw._CommitLog()
    log.add("Speaker A", "legacy sys line", channel="sys", span=(0.0, 2.0))
    feed = log.drain_feed()
    assert [(e["speaker"], e["text"]) for e in feed] == [
        ("Speaker A", "legacy sys line")]
    assert log.set_speakers({0: "Speaker B"}) == 1
    assert log.drain_relabels() == [{"index": 0, "speaker": "Speaker B"}]


# ---------------------------------------------------------------------------
# (d) finish flushes pending lines exactly once
# ---------------------------------------------------------------------------

def test_flush_releases_pending_exactly_once():
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=100.0, sys_seconds=16.0)
    sess.commits.add("Speaker Z", "held tail words",
                     channel="sys", span=(0.5, 7.5))
    sess.commits.add("Speaker Z", "second held words",
                     channel="sys", span=(8.5, 15.5))
    assert osw._Session._cheap_rediar_once(sess) is True   # all held (H=100)
    assert sess.commits.drain_feed() == []

    sess._single_emission_flush()
    feed = sess.commits.drain_feed()
    assert [(e["speaker"], e["text"], e["start"], e["end"]) for e in feed] == [
        ("Speaker A", "held tail words", 0.0, 8.0),
        ("Speaker B", "second held words", 8.0, 16.0)]

    sess._single_emission_flush()                          # exactly once
    assert sess.commits.drain_feed() == []
    assert sess.commits.drain_relabels() == []


def test_module_finish_flushes_hold_into_reply(monkeypatch):
    """Wire-level decision 4: ops_stream_whisper.finish() flushes the hold
    BEFORE draining the feed, so the finish reply carries the tail lines —
    and the reply's relabels stay empty."""
    from transcription_worker.test_ops_stream_whisper import (
        FakeEngine, mean_voice_embed)
    monkeypatch.setenv("DICTATION_NOTES_SINGLE_EMISSION", "1")
    monkeypatch.setenv("DICTATION_NOTES_HOLD_S", "5.0")
    s = osw._Session(FakeEngine(), mean_voice_embed)
    try:
        assert s._hold is not None and s._hold.hold_s == 5.0
        assert s.commits.single_emission is True
        s.commits.add("Speaker Q", "the pending tail words",
                      channel="sys", span=(0.0, 2.0))
        s._hold._pending = [(0.0, 2.0, 0)]     # a turn still under hold
        s.stream_id = "se-finish-1"
        s.client = "notes"
        osw._sessions["se-finish-1"] = s
        reply = osw.finish({"stream_id": "se-finish-1"})
        assert reply["ok"] is True
        sys_events = [e for e in reply["commits"] if e["channel"] == "sys"]
        assert [(e["speaker"], e["text"]) for e in sys_events] == [
            ("Speaker A", "the pending tail words")]
        assert reply["relabels"] == []
    finally:
        osw._sessions.pop("se-finish-1", None)
        s.teardown()


# ---------------------------------------------------------------------------
# __init__ / cadence wiring (decision 3)
# ---------------------------------------------------------------------------

def test_mode_on_builds_hold_and_cadence_default(monkeypatch):
    from transcription_worker.test_ops_stream_whisper import (
        FakeEngine, mean_voice_embed)
    monkeypatch.setenv("DICTATION_NOTES_SINGLE_EMISSION", "1")
    monkeypatch.delenv("DICTATION_NOTES_HOLD_S", raising=False)
    monkeypatch.delenv("DICTATION_NOTES_REDIAR_CADENCE_S", raising=False)
    s = osw._Session(FakeEngine(), mean_voice_embed)
    s._watchdog_stop.set()
    try:
        assert s._hold is not None and s._hold.hold_s == 9.6  # H-MEASUREMENT
        assert s._cheap_cadence_s() == 2.4   # v5.1: mode halves the tick
        monkeypatch.setenv("DICTATION_NOTES_REDIAR_CADENCE_S", "7.5")
        assert s._cheap_cadence_s() == 7.5                    # env still wins
    finally:
        s.teardown()


def test_mode_off_no_hold_and_cadence_default(monkeypatch):
    from transcription_worker.test_ops_stream_whisper import (
        FakeEngine, mean_voice_embed)
    monkeypatch.delenv("DICTATION_NOTES_SINGLE_EMISSION", raising=False)
    monkeypatch.delenv("DICTATION_NOTES_REDIAR_CADENCE_S", raising=False)
    s = osw._Session(FakeEngine(), mean_voice_embed)
    s._watchdog_stop.set()
    try:
        assert s._hold is None
        assert s.commits.single_emission is False
        assert s._cheap_cadence_s() == osw._Session.REDIAR_CHEAP_CADENCE_S
    finally:
        s.teardown()


def test_turn_decode_births_text_one_to_one():
    """Turn-driven span decode (DICTATION_NOTES_TURN_DECODE, 2026-09-01): a
    closed turn's text comes from an ASR decode of EXACTLY that turn's
    audio span — no carriers anywhere — and the line releases 1:1 with the
    turn. The carrier join would have emitted nothing here (the old
    silently-consumed-span miss)."""
    import time as _time
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    calls = []

    class _Eng:
        def transcribe(self, clip, language=None, **kw):
            calls.append(round(len(clip) / SR, 2))
            return {"text": f"decoded {len(calls)}"}

    sess.sys.engine = _Eng()
    # decode both spans ahead of the tick (the wire enqueues on sight; here
    # we pre-seed to keep the test single-tick and race-free)
    sess._request_turn_decode(0.0, 8.0)
    sess._request_turn_decode(8.0, 16.0)
    for _ in range(300):
        with sess._turn_decode_lock:
            if len(sess._turn_texts) >= 2 and not sess._turn_decode_inflight:
                break
        _time.sleep(0.01)
    assert osw._Session._cheap_rediar_once(sess) is True
    feed = sess.commits.drain_feed()
    assert [(e["speaker"], e["text"], e["start"], e["end"]) for e in feed] == [
        ("Speaker A", "decoded 1", 0.0, 8.0),
        ("Speaker B", "decoded 2", 8.0, 16.0)]
    # the engine saw the exact spans (+ the 0.12 s acoustic pads)
    assert sorted(calls) == [8.12, 8.24]
    assert sess.commits.drain_relabels() == []


def test_key_drift_rescue_returns_midpoint_text_after_patience(monkeypatch):
    """KEY-DRIFT RESCUE (2026-09-02): text decoded under an older boundary
    version must reach the turn once patience elapses — the full-8 log walk
    showed 50-100% of clean-meeting miss holes were DEADLINE drops with
    real text sitting under a drifted key."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_text_patience_s = 0.0            # elapse immediately in test
    with sess._turn_decode_lock:
        sess._turn_texts[sess._turn_key(10.0, 15.0)] = "hello world"
    # asking with a drifted end (15.0 -> 15.8): exact key misses, midpoint
    # of the stored span (12.5) is inside [10.0, 15.8) -> rescued whole
    got = sess._turn_text_for_span(10.0, 15.8)
    assert got == "hello world"


def test_key_drift_rescue_never_steals_neighbor_span(monkeypatch):
    """A span whose midpoint lies in ANOTHER turn must not be claimed (the
    misattribution failure of the >50%-of-span rule)."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_text_patience_s = 0.0
    with sess._turn_decode_lock:
        sess._turn_texts[sess._turn_key(10.0, 15.0)] = "neighbor text"
    # asking turn starts after the stored span's midpoint (12.5): no claim,
    # (a decode gets requested; rescue returns None -> hold keeps waiting)
    got = sess._turn_text_for_span(15.2, 20.0)
    assert got is None


def test_key_drift_rescue_joins_spans_in_time_order():
    """Multiple owned spans join whole, in time order — never sliced."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_text_patience_s = 0.0
    with sess._turn_decode_lock:
        sess._turn_texts[sess._turn_key(12.0, 14.5)] = "second"
        sess._turn_texts[sess._turn_key(10.0, 12.0)] = "first"
        sess._turn_texts[sess._turn_key(9.0, 9.8)] = ""   # empty: skipped
    got = sess._turn_text_for_span(9.9, 14.8)
    assert got == "first second"


def test_key_drift_rescue_waits_out_patience(monkeypatch):
    """Before patience elapses the hold keeps waiting (None) — rescue must
    not preempt an in-flight exact decode that would be more complete."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_text_patience_s = 3600.0         # never elapses in test
    with sess._turn_decode_lock:
        sess._turn_texts[sess._turn_key(10.0, 15.0)] = "hello"
    assert sess._turn_text_for_span(10.0, 15.8) is None


def test_key_drift_rescue_fires_while_current_key_inflight():
    """The OBSERVED chase state (stitch-run log: 221 DEADLINE drops, only 4
    lookup logs — every ask found the current key already inflight because
    the tick requests before the hold asks): rescue must fire through the
    inflight branch too, else the chase is unbreakable."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_text_patience_s = 0.0
    with sess._turn_decode_lock:
        sess._turn_texts[sess._turn_key(10.0, 15.0)] = "older version text"
        sess._turn_decode_inflight.add(sess._turn_key(10.0, 15.8))
    got = sess._turn_text_for_span(10.0, 15.8)
    assert got == "older version text"


def test_turnfirst_inversion_priorities(monkeypatch):
    """TURN-FIRST (2026-09-02): in single-emission, sys live decode yields
    (BACKGROUND) and turn-decode becomes the caption producer (CAPTION) —
    the product lines must never queue behind demoted carrier decodes.
    mic keeps CAPTION; legacy mode keeps the old classes."""
    from transcription_worker.engine_lock import BACKGROUND, CAPTION
    prio = osw._ChannelStream._live_decode_prio

    class _Commits: single_emission = True
    class _Sys: channel_tag = "sys"; commits = _Commits()
    class _Mic: channel_tag = "mic"; commits = _Commits()
    monkeypatch.delenv("DICTATION_NOTES_TURNFIRST", raising=False)
    assert prio(_Sys()) == BACKGROUND    # sys live yields in mode
    assert prio(_Mic()) == CAPTION       # user echo snappy
    monkeypatch.setenv("DICTATION_NOTES_TURNFIRST", "0")
    assert prio(_Sys()) == CAPTION       # kill switch -> legacy
    monkeypatch.delenv("DICTATION_NOTES_TURNFIRST", raising=False)
    _Commits.single_emission = False
    assert prio(_Sys()) == CAPTION       # legacy mode untouched


def test_decode_dedup_one_inflight_per_turn_start():
    """DECODE DEDUP (2026-09-02): a drifting turn boundary must not spawn
    parallel decodes of the same turn-start — 38% of decode budget was that
    waste, starving new turns' text (the late-text miss driver). One in-flight
    per start; the drifted version re-enqueues only after the first lands."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    # keep the worker from draining the queue: pretend a decode thread is alive
    sess._turn_decode_thread = type("T", (), {"is_alive": lambda self: True})()
    sess._request_turn_decode(10.0, 12.0)          # first version
    sess._request_turn_decode(10.0, 12.6)          # jitter: same start, +0.6s end
    sess._request_turn_decode(10.1, 15.0)          # growth: start within 0.3s
    q = list(sess._turn_decode_q)
    assert q == [(10.0, 12.0)], f"only the first per-start should enqueue: {q}"
    # a genuinely different turn-start is NOT blocked
    sess._request_turn_decode(20.0, 23.0)
    assert (20.0, 23.0) in list(sess._turn_decode_q)
    # once the first lands (leaves inflight), the grown version can enqueue
    with sess._turn_decode_lock:
        sess._turn_decode_inflight.discard(sess._turn_key(10.0, 12.0))
        sess._turn_texts[sess._turn_key(10.0, 12.0)] = "partial"
    sess._request_turn_decode(10.1, 15.0)
    assert (10.1, 15.0) in list(sess._turn_decode_q), "grown re-enqueues after first lands"



def test_v3_worker_serves_edf_and_prunes_stale():
    """QUEUE ORDER FIX v2 (2026-09-02): the in-order release waits on the
    OLDEST pending turn, so the worker serves the EARLIEST end first (EDF)
    and silently discards entries the emission watermark already passed."""
    import time as _time
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    order = []

    class _Eng:
        def transcribe(self, clip, language=None, **kw):
            order.append(round(len(clip) / SR, 1))
            return {"text": "x"}

    sess.sys.engine = _Eng()
    sess._hold._released_until = 10.0          # watermark: turns <=10 are done
    # deterministic: block the auto-spawn, enqueue all, then drain synchronously
    sess._turn_decode_thread = type("T", (), {"is_alive": lambda self: True})()
    sess._request_turn_decode(2.0, 6.0)        # STALE (end < wm) — must be pruned
    sess._request_turn_decode(16.0, 20.0)      # newest
    sess._request_turn_decode(12.0, 14.0)      # earliest end — must decode FIRST
    sess._turn_decode_worker()                 # drains the queue, then returns
    # earliest end (2s clip w/ pads -> 2.24) before newest (4.24); stale absent
    assert order[0] < order[1], f"EDF violated: {order}"
    assert len(order) == 2, f"stale span was decoded: {order}"


def test_v3_tick_does_not_request_below_watermark():
    """The real tick only queues settled turns beyond its release watermark."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_decode_thread = type("T", (), {"is_alive": lambda self: True})()
    sess._hold._released_until = 12.0
    sess._dz_bank.last_state = state
    osw._Session._single_emission_tick(sess, sess._dz_bank, 24.0)
    q = list(sess._turn_decode_q)
    assert (8.0, 16.0) in q and (0.0, 8.0) not in q, q


def test_settle_decode_once_fires_on_stable_bounds():
    """SETTLE-TRIGGERED DECODE-ONCE: two consecutive consistent sightings ->
    exactly one request; further drift variants never re-request."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=9.6, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_decode_thread = type("T", (), {"is_alive": lambda self: True})()
    sess._decode_when_settled(10.0, 14.0, 14.8)   # frontier (age 0.8): wait
    assert list(sess._turn_decode_q) == []
    sess._decode_when_settled(10.0, 14.1, 19.6)   # interior now -> request
    assert list(sess._turn_decode_q) == [(10.0, 14.1)]
    sess._decode_when_settled(10.0, 14.6, 25.6)   # later drift: decode-once holds
    assert len(sess._turn_decode_q) == 1


def test_settle_forced_at_hold_age_when_still_drifting():
    """A turn that never stops drifting is force-decoded at hold age."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_decode_thread = type("T", (), {"is_alive": lambda self: True})()
    sess._decode_when_settled(10.0, 12.0, 13.0)   # sighting 1 (age 1.0)
    sess._decode_when_settled(10.0, 12.8, 14.0)   # drifted, age 1.2 -> no request
    assert list(sess._turn_decode_q) == []
    sess._decode_when_settled(10.0, 13.4, 17.6)   # still drifting BUT age 4.2>hold
    assert list(sess._turn_decode_q) == [(10.0, 13.4)], "hold-age fallback"


def test_bucket_serves_micro_drifted_key_and_propagates_empty():
    """The one settled decode serves a micro-drifted release key via the
    bucket (coverage-guarded); an authoritative EMPTY propagates as '' so
    the hold consumes instead of squatting out the deadline."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=4.0, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    with sess._turn_decode_lock:
        sess._turn_bucket_texts[sess._turn_bucket(10.0)] = (10.0, 14.0, "settled words")
        sess._turn_bucket_texts[sess._turn_bucket(20.0)] = (20.0, 23.0, "")
    assert sess._turn_text_for_span(10.1, 14.2) == "settled words"   # micro-drift ok
    assert sess._turn_text_for_span(20.0, 23.1) == ""                # empty propagates
    # poor coverage must NOT serve (different turn)
    sess._turn_decode_thread = type("T", (), {"is_alive": lambda self: True})()
    assert sess._turn_text_for_span(10.2, 30.0) != "settled words"


def test_v5_interior_turn_decodes_on_first_sighting():
    """v5: a turn safely behind the frontier (closed by gap/change — right
    boundary cannot grow) enqueues on FIRST sighting; no 2-tick wait."""
    state = _two_speaker_state()
    sess = _mode_session([_round(state)], hold_s=9.6, sys_seconds=24.0)
    sess._ensure_turn_decode_state()
    sess._turn_decode_thread = type("T", (), {"is_alive": lambda self: True})()
    sess._decode_when_settled(10.0, 14.0, 17.0)   # end 3.0s behind now: interior
    assert list(sess._turn_decode_q) == [(10.0, 14.0)], "interior = immediate"
    # frontier turn (end 1.7s behind now) still waits for a 2nd sighting
    sess._decode_when_settled(20.0, 22.3, 23.2)   # age 0.9 < guard 1.2
    assert (20.0, 22.3) not in list(sess._turn_decode_q), "frontier stays conservative"
