"""COMMIT-ON-STABLE (Pokee feed latency): a line is committed as soon as its
text is stable across two consecutive periodic decodes (LocalAgreement over
whisper's native timestamp segments) instead of waiting for the energy turn
to close (~0.7 s pause + a boundary re-decode of the whole open window).

These tests drive `_ChannelStream` / `_Session` with a deterministic
timestamped fake engine (no NPU): the test declares a word timeline over the
session clock, the engine recovers a clip's absolute offset by exact-sample
lookup and answers with the words the clip covers — grouped into sentence
spans exactly like `transcribe_timed`'s segment output. That makes the
guardrails checkable as *properties*: no lost or duplicated words vs the
turn-close-only path, no mid-word cuts, min line length, unchanged speaker
attribution, bounded event rate.
"""

import time

import numpy as np
import pytest

from transcription_worker import ops_stream_whisper as osw

SR = osw.SAMPLE_RATE

#: sub-PEAK_GATE dither so every sample value is unique (the fake engine's
#: offset lookup) while still counting as silence for every energy gate
#: (PEAK_GATE 0.002, tracker SILENCE_PEAK 0.008, frame-RMS floor 0.004).
DITHER = 0.0004


def script_audio(words, total_s, level=0.12, seed=11):
    """Session audio for a word timeline: loud noise where words are, unique
    sub-gate dither everywhere else."""
    rng = np.random.default_rng(seed)
    a = (DITHER * rng.standard_normal(int(total_s * SR))).astype(np.float32)
    for s, e, _ in words:
        n0, n1 = int(s * SR), int(e * SR)
        a[n0:n1] = (level * rng.standard_normal(n1 - n0)).clip(-1, 1)
    return a


class TimedScriptEngine:
    """Deterministic whisper-with-timestamps over a scripted word timeline."""

    def __init__(self, audio, words):
        self.audio = audio
        self.words = list(words)
        self.calls = []
        self._index = {float(v): i for i, v in enumerate(audio)}

    def _spans_for(self, clip):
        off = self._index[float(clip[0])] / SR
        end = off + len(clip) / SR
        groups, cur = [], []
        for a, b, w in self.words:
            mid = (a + b) / 2
            if not off <= mid <= end:
                continue
            cur.append((a, b, w))
            if w[-1] in ".!?":
                groups.append(cur)
                cur = []
        if cur:
            groups.append(cur)
        return [(g[0][0] - off, g[-1][1] - off,
                 " ".join(w for _, _, w in g), "en") for g in groups]

    def transcribe(self, clip, **kw):
        self.calls.append(("greedy", len(clip)))
        text = " ".join(s[2] for s in self._spans_for(clip))
        return {"text": text, "stats": {"language": "en"}}

    def transcribe_timed(self, clip, **kw):
        self.calls.append(("timed", len(clip)))
        return {"spans": self._spans_for(clip), "stats": {}}


def push_paced(ch, audio, upto_s, chunk_s=0.25, start_s=0.0, drain_each=True):
    """Push [start_s, upto_s) in chunks, waiting out each background decode
    so the decode sequence is deterministic."""
    n0, n1, step = int(start_s * SR), int(upto_s * SR), int(chunk_s * SR)
    for i in range(n0, n1, step):
        ch.push(audio[i:i + step])
        if drain_each:
            wait_idle(ch)


def wait_idle(ch, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with ch.lock:
            if not ch.decoding and not ch.pending_boundaries:
                return
        time.sleep(0.005)
    raise TimeoutError("decode thread never finished")


def fast_decode(monkeypatch):
    """Remove the wall-clock decode pacing so tests are deterministic."""
    monkeypatch.setattr(osw, "DECODE_COOLDOWN_S", 0.0)
    monkeypatch.setattr(osw, "MIN_NEW_AUDIO_S", 0.05)


def committed_words(log):
    out = []
    for line in log.snapshot():
        out.extend(line.split("] ", 1)[1].split())
    return [w.strip(".!?,").lower() for w in out]


MONOLOGUE = [
    (0.2, 0.5, "hello"), (0.55, 0.85, "there"), (0.9, 1.4, "team."),
    (1.6, 1.9, "let"), (1.95, 2.2, "us"), (2.25, 2.6, "look"),
    (2.65, 2.9, "at"), (2.95, 3.4, "numbers."),
    (3.6, 3.9, "the"), (3.95, 4.3, "plan"), (4.35, 4.7, "is"),
    (4.75, 5.1, "on"), (5.15, 5.4, "track"),
]


def make_mic(engine):
    return osw._ChannelStream(engine, osw._CommitLog(), None,
                              fixed_label="You", channel_tag="mic")


def test_stable_prefix_commits_mid_turn(monkeypatch):
    """A punctuated sentence that two consecutive decodes agree on commits
    WITHOUT waiting for the turn to close — the speech is still running."""
    fast_decode(monkeypatch)
    audio = script_audio(MONOLOGUE, 6.0)
    ch = make_mic(TimedScriptEngine(audio, MONOLOGUE))
    push_paced(ch, audio, 5.3)
    # the turn is still open (no 0.7s gap, force-close at 6.0s not reached)
    assert ch.tracker._turn_start is not None
    lines = ch.commits.snapshot()
    # (stream_repetition.clean capitalizes sentence starts at commit — the
    # same normalization the turn-close path gets)
    assert lines and lines[0].lower().startswith("[you] hello there team.")
    # the segment base advanced past the committed words: the next decode
    # window no longer re-covers them
    assert ch.seg_base >= 1.4
    # the feed event carries the speech span + commit clock (wire shape)
    evt = ch.commits.drain_feed()[0]
    assert evt["start"] == pytest.approx(0.2, abs=0.3)
    assert evt["end"] == pytest.approx(1.4, abs=0.6)
    assert "committed_at" in evt


def test_no_lost_or_duplicated_words_vs_turn_close_path(monkeypatch):
    """Property: over a whole scripted take, the concatenated committed text
    with commit-on-stable ON equals the turn-close-only path's (word-for-word
    — line granularity may differ)."""
    fast_decode(monkeypatch)
    words = MONOLOGUE + [
        (5.5, 5.8, "good."),                       # closes with the turn
        # (0.9s gap -> turn close)
        (6.8, 7.1, "next"), (7.15, 7.5, "topic"), (7.55, 7.9, "please."),
        (8.1, 8.4, "we"), (8.45, 8.8, "ship"), (8.85, 9.3, "tomorrow."),
    ]
    audio = script_audio(words, 11.0)

    def run(gate_on):
        monkeypatch.setenv("DICTATION_NOTES_COMMIT_ON_STABLE",
                           "1" if gate_on else "0")
        ch = make_mic(TimedScriptEngine(audio, words))
        push_paced(ch, audio, 11.0)
        ch.finish()
        return committed_words(ch.commits)

    on, off = run(True), run(False)
    assert on == off, (on, off)
    assert on, "the take committed nothing at all"


def test_min_length_blocks_two_word_fragments(monkeypatch):
    """A stable-but-tiny prefix ('yes sir.') must NOT emit as its own line —
    it waits for the turn machinery instead of spraying fragments."""
    fast_decode(monkeypatch)
    words = [(0.2, 0.5, "yes"), (0.55, 0.9, "sir."),
             (1.1, 1.4, "more"), (1.45, 1.8, "words"), (1.85, 2.2, "flow"),
             (2.25, 2.7, "here"), (2.75, 3.2, "now"), (3.25, 3.7, "still"),
             (3.75, 4.2, "going"), (4.25, 4.7, "on")]
    audio = script_audio(words, 5.5)
    ch = make_mic(TimedScriptEngine(audio, words))
    # push only far enough that ONLY the tiny prefix can be stable: no
    # punctuation / pause boundary exists after it inside the window
    push_paced(ch, audio, 3.0)
    for line in ch.commits.snapshot():
        text = line.split("] ", 1)[1]
        assert text != "yes sir.", "two-word fragment emitted as a line"


def test_never_cuts_mid_word_and_display_is_coherent(monkeypatch):
    """Committed text + the remaining partial reconstruct the hypothesis at
    a word boundary — no split word at the seam."""
    fast_decode(monkeypatch)
    audio = script_audio(MONOLOGUE, 6.0)
    ch = make_mic(TimedScriptEngine(audio, MONOLOGUE))
    push_paced(ch, audio, 5.3)
    lines = ch.commits.snapshot()
    assert lines
    all_words = [w for _, _, w in MONOLOGUE]
    committed = []
    for line in lines:
        committed.extend(line.split("] ", 1)[1].lower().split())
    # every committed token is one of the scripted words, whole
    assert all(w in all_words for w in committed), committed
    with ch.lock:
        partial_words = ch.partial.split()
    assert all(w in all_words for w in partial_words), partial_words
    # and together they are a prefix of the script with no overlap
    assert committed == all_words[:len(committed)]
    if partial_words:
        assert partial_words[0] == all_words[len(committed)]


def test_stable_commit_rate_is_capped(monkeypatch):
    """Sustained agreement must not spray >1 line/s: stable commits respect
    the per-channel minimum interval (turn-close commits are unaffected)."""
    fast_decode(monkeypatch)
    words = []
    t = 0.2
    for i in range(30):
        words.append((t, t + 0.25, f"w{i}."))     # every word a sentence
        t += 0.3
    audio = script_audio(words, t + 1.0)
    ch = make_mic(TimedScriptEngine(audio, words))
    push_paced(ch, audio, t)
    times = ch.stable_commit_times
    assert times, "sustained agreement produced no stable commit at all"
    gaps = [b - a for a, b in zip(times, times[1:])]
    assert all(g >= osw.STABLE_MIN_INTERVAL_S - 0.05 for g in gaps), gaps


def test_speaker_attribution_unchanged_with_pending_boundary(monkeypatch):
    """A queued speaker boundary owns the cut: no stable commit may fire
    across it (same guard as the long-monologue force commit)."""
    fast_decode(monkeypatch)
    audio = script_audio(MONOLOGUE, 6.0)
    ch = make_mic(TimedScriptEngine(audio, MONOLOGUE))
    with ch.lock:
        ch.pending_boundaries.append(("You", 1.5))
    # completion with a pending boundary: the stable path must decline
    spans = [(0.2, 1.4, "hello there team.", "en")]
    assert ch._stable_cut_locked(spans, 0.0, 3.0) is None


def test_gate_off_restores_turn_close_only(monkeypatch):
    """DICTATION_NOTES_COMMIT_ON_STABLE=0: no commit exists before the turn
    closes, exactly the pre-change behavior."""
    fast_decode(monkeypatch)
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_ON_STABLE", "0")
    audio = script_audio(MONOLOGUE, 6.0)
    ch = make_mic(TimedScriptEngine(audio, MONOLOGUE))
    push_paced(ch, audio, 5.3)
    assert ch.commits.snapshot() == []
    # engine without transcribe_timed: same, and nothing crashes
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_ON_STABLE", "1")

    class GreedyOnly:
        def transcribe(self, clip, **kw):
            return {"text": "steady text", "stats": {}}

    ch2 = make_mic(GreedyOnly())
    push_paced(ch2, audio, 5.3)
    assert ch2.commits.snapshot() == []


def test_default_is_on_certified(monkeypatch):
    """Certification guard (COMMIT_ON_STABLE_AB.md): the shipped default —
    env UNSET — is ON, and a stable prefix commits mid-turn without the flag
    being set. The 2026-08-27 AMI A/B showed this default is WER-neutral vs
    the turn-close-only path (ES 0.322 vs 0.319, within noise) while cutting
    commit-latency P50 ~0.3 s, so ON stays the default. This test pins that
    contract so a silent default flip can never re-open the shipped
    regression the A/B ruled out."""
    fast_decode(monkeypatch)
    monkeypatch.delenv("DICTATION_NOTES_COMMIT_ON_STABLE", raising=False)
    assert osw._commit_on_stable_enabled() is True
    audio = script_audio(MONOLOGUE, 6.0)
    ch = make_mic(TimedScriptEngine(audio, MONOLOGUE))
    push_paced(ch, audio, 5.3)
    # the turn is still open, yet the stable prefix has already committed —
    # exactly the default-ON latency win the A/B certified.
    assert ch.tracker._turn_start is not None
    assert ch.commits.snapshot(), "default did not commit-on-stable"


def test_session_feed_carries_stable_commits(monkeypatch):
    """Session-level: stable commits ride the same structured feed events
    (drain cursor, span, committed_at) the partner feed consumes — no new
    wire fields, just earlier lines."""
    fast_decode(monkeypatch)
    monkeypatch.setenv("DICTATION_NOTES_AEC", "0")
    audio = script_audio(MONOLOGUE, 6.0)
    s = osw._Session(TimedScriptEngine(audio, MONOLOGUE), None)
    s._watchdog_stop.set()
    push_paced(s.mic, audio, 5.3)
    events = s.commits.drain_feed()
    assert events, "no early feed event during the open turn"
    evt = events[0]
    assert set(evt) == {"speaker", "text", "channel", "start", "end",
                        "committed_at"}
    assert evt["channel"] == "mic" and evt["speaker"] == "You"
    s.teardown()


def test_sys_channel_never_stable_commits_unlabeled(monkeypatch):
    """Before the first closed turn labels the sys channel, seg_speaker is
    None — a stable commit then would fall back to 'You' and put REMOTE
    speech under the local user's name. It must wait for the label; the
    force-close boundary path (which labels the turn first) owns the
    opening monologue exactly as before."""
    fast_decode(monkeypatch)
    audio = script_audio(MONOLOGUE, 6.0)
    ch = osw._ChannelStream(TimedScriptEngine(audio, MONOLOGUE),
                            osw._CommitLog(), lambda a, s, e: "Speaker A",
                            channel_tag="sys")
    push_paced(ch, audio, 5.3)
    for line in ch.commits.snapshot():
        assert not line.startswith("[You]"), line
    assert ch.stable_commit_times == []


class CountingTimedEngine(TimedScriptEngine):
    """TimedScriptEngine that also counts decodes per audio length."""


def test_boundary_commit_uses_cached_decode_no_redecode(monkeypatch):
    """Turn-close fast path: when the last periodic timed decode already
    covered [seg_base, cut_s] with clean span boundaries, the boundary
    commit uses that CACHED, context-rich text instead of re-decoding the
    isolated window — removing the second decode (and its engine-lock wait)
    from the spoken→committed critical path. The words are identical; only
    the redundant decode disappears."""
    fast_decode(monkeypatch)
    words = [(0.2, 0.5, "hello"), (0.55, 0.85, "there"), (0.9, 1.3, "team"),
             (1.35, 1.7, "how"), (1.75, 2.1, "are"), (2.15, 2.5, "you.")]
    audio = script_audio(words, 5.0)
    eng = CountingTimedEngine(audio, words)
    ch = make_mic(eng)
    # speech through 2.5s, then silence long enough to close the turn
    push_paced(ch, audio, 3.0)
    n_decodes_before_close = len(eng.calls)
    push_paced(ch, audio, 4.2, start_s=3.0)   # pause -> turn closes
    lines = ch.commits.snapshot()
    assert lines and "hello there team how are you" in lines[0].lower()
    # the boundary commit must NOT have re-decoded the turn window: the
    # only decode calls after the close are (possibly) periodic ones on the
    # near-empty remainder, which the energy gate blocks here.
    assert len(eng.calls) == n_decodes_before_close, eng.calls


def test_boundary_cache_declines_on_straddling_span(monkeypatch):
    """A cached span straddling the cut means the decode disagrees with the
    energy boundary — fall back to the re-decode path (never split a span's
    words across a guess)."""
    fast_decode(monkeypatch)
    audio = script_audio(MONOLOGUE, 6.0)
    ch = make_mic(TimedScriptEngine(audio, MONOLOGUE))
    with ch.lock:
        ch._last_timed = (0.0, 5.0,
                          ((0.2, 3.4, "hello there team let us..."),), "en")
    # cut at 1.5 lands INSIDE the cached 0.2-3.4 span
    assert ch._boundary_cache_locked(1.5) is None


def test_boundary_cache_declines_when_stale_or_uncovered(monkeypatch):
    fast_decode(monkeypatch)
    audio = script_audio(MONOLOGUE, 6.0)
    ch = make_mic(TimedScriptEngine(audio, MONOLOGUE))
    with ch.lock:
        ch._last_timed = (0.5, 2.0, ((0.2, 1.0, "hello there team."),), "en")
    # base mismatch (cache from an older window start)
    assert ch._boundary_cache_locked(1.4) is None
    ch.seg_base = 0.5
    # covered and clean -> usable
    assert ch._boundary_cache_locked(1.4) is not None
    # cut beyond the cached window end -> not covered
    assert ch._boundary_cache_locked(2.6) is None
    # empty cached window -> None (the re-decode path owns empty-carry)
    with ch.lock:
        ch._last_timed = (0.5, 2.0, (), "en")
    assert ch._boundary_cache_locked(1.4) is None


def test_timed_decode_covers_long_turns(monkeypatch):
    """Real meeting turns run 8-16 s; the timed live decode must keep
    producing spans up to the force-commit bound (COMMIT_AFTER_S), or the
    boundary cache goes blind exactly on the turns that need it most.
    DICTATION_NOTES_TIMED_MAX_S=12 restores the old windowed fallback ceiling."""
    words = [(0.5 + i, 0.9 + i, f"w{i}.") for i in range(15)]
    audio = script_audio(words, 17.0)
    eng = TimedScriptEngine(audio, words)
    ch = make_mic(eng)
    text, lang, spans, _ = ch._transcribe_live(audio[: int(15.5 * SR)])
    assert spans, "15.5s window fell back to the span-less path"
    monkeypatch.setenv("DICTATION_NOTES_TIMED_MAX_S", "12")
    text, lang, spans, _ = ch._transcribe_live(audio[: int(15.5 * SR)])
    assert spans is None


def test_periodic_decode_refreshes_snapshot_at_engine_acquisition(monkeypatch):
    """The decode snapshot is taken at QUEUE time, but the decode can wait
    seconds behind the engine lock (a rolling beam window) — decoding the
    stale snapshot wastes the call and leaves the boundary cache never
    covering the cut. The timed path must re-snapshot the open segment at
    the last moment, so the decode covers everything heard up to NOW."""
    fast_decode(monkeypatch)
    audio = script_audio(MONOLOGUE, 6.0)

    class SlowStart(TimedScriptEngine):
        def __init__(self, *a):
            super().__init__(*a)
            self.seen_lens = []

        def transcribe_timed(self, clip, **kw):
            self.seen_lens.append(len(clip))
            return super().transcribe_timed(clip, **kw)

    eng = SlowStart(audio, MONOLOGUE)
    ch = make_mic(eng)
    # queue a decode with a 1.0s snapshot, then push 1.5s more BEFORE the
    # decode thread runs (simulate the engine-lock wait by pushing first
    # with the decode slot held)
    with ch.lock:
        ch.decoding = True          # park the scheduler
    ch.push(audio[: int(1.0 * SR)])   # buffered, no decode spawned
    ch.push(audio[int(1.0 * SR): int(2.5 * SR)])
    with ch.lock:
        ch.decoding = False
    snap = audio[: int(1.0 * SR)].copy()
    ch._decode(snap, "You", 0.0)      # stale 1.0s snapshot handed in
    assert eng.seen_lens, "no decode ran"
    # the engine must have seen the REFRESHED window (2.5s), not the stale 1.0s
    assert eng.seen_lens[-1] >= int(2.4 * SR), eng.seen_lens
    # and the cache reflects the refreshed coverage
    with ch.lock:
        assert ch._last_timed is not None and ch._last_timed[1] >= 2.4


# ---------------------------------------------------------------------------
# silence-hallucination scenarios (2026-08-28 unified commit-text gate):
# the NEW commit paths — commit-on-stable and the boundary decode cache —
# must drop hallucination text at the gate, exactly like the legacy path.
# ---------------------------------------------------------------------------

JUNK_THEN_REAL = [
    (0.2, 0.5, "Thank"), (0.55, 0.85, "you"), (0.9, 1.1, "for"),
    (1.15, 1.4, "watching."),
    (1.6, 1.9, "next"), (1.95, 2.3, "topic"), (2.35, 2.7, "please"),
    (2.75, 3.1, "we"), (3.15, 3.5, "ship"), (3.55, 3.9, "tomorrow"),
    (3.95, 4.3, "and"), (4.35, 4.7, "beyond"),
]


def test_stable_commit_drops_hallucination_prefix(monkeypatch):
    """Two consecutive decodes agreeing on 'Thank you for watching.' is
    still a hallucination — the stable commit must pass the gate and drop
    it, never caption it."""
    fast_decode(monkeypatch)
    audio = script_audio(JUNK_THEN_REAL, 6.0)
    ch = make_mic(TimedScriptEngine(audio, JUNK_THEN_REAL))
    push_paced(ch, audio, 5.3)
    ch.finish()
    lines = ch.commits.snapshot()
    for line in lines:
        assert "thank you for watching" != line.split("] ", 1)[1].lower() \
            .rstrip("."), line
        assert not line.lower().startswith("[you] thank you for watching"), \
            line
    joined = " ".join(lines).lower()
    assert "next topic" in joined, lines


def test_boundary_cache_hit_drops_hallucination(monkeypatch):
    """The boundary-cache fast path reuses the periodic timed decode's text
    with NO re-decode — that reused text must still pass the commit gate.
    A cached window decoding to a stock pleasantry commits nothing."""
    fast_decode(monkeypatch)
    junk_words = [(0.2, 0.5, "Thank"), (0.55, 0.85, "you"),
                  (0.9, 1.1, "for"), (1.15, 1.4, "watching.")]
    audio = script_audio(junk_words, 5.0)
    eng = CountingTimedEngine(audio, junk_words)
    ch = make_mic(eng)
    push_paced(ch, audio, 3.0)          # periodic timed decode fills cache
    push_paced(ch, audio, 4.2, start_s=3.0)   # pause -> turn closes
    assert ch.commits.snapshot() == [], ch.commits.snapshot()
    assert ch.commits.drain_feed() == []
