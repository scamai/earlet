"""Pure scheduler/buffering contracts for the rolling meeting transcript."""

from __future__ import annotations

import threading
import time

import numpy as np

from .rolling_stable import RollingStableChannel, SAMPLE_RATE


class _EmptyEngine:
    def __init__(self):
        self.calls = 0

    def transcribe_timed(self, audio, **kwargs):
        self.calls += 1
        assert kwargs["beam_size"] == 4
        assert kwargs["condition_on_prev"] is False
        return {"spans": [], "stats": {}}


def _channel(engine, **kwargs):
    return RollingStableChannel(
        engine, channel="sys", speaker="Them", beam_size=4, **kwargs)


def test_crossing_span_does_not_advance_watermark_past_uncommitted_speech():
    class CrossingEngine:
        def transcribe_timed(self, audio, **kwargs):
            return {"spans": [(0.0, 1.0, "sentence crossing the cut", "en")],
                    "stats": {}}

    channel = _channel(CrossingEngine(), window_s=1.0,
                       hop_s=0.5, lookahead_s=0.25)
    channel._decode_window(np.ones(SAMPLE_RATE, np.float32), 0.0, 0.75)
    snap = channel.snapshot()

    assert snap["spans"] == []
    assert snap["through"] == 0.0, (
        "claiming the 0.75s boundary stable would make the next overlap reject "
        "the sentence before it has ever been committed")


def test_near_silent_span_dropped_by_per_span_energy_gate():
    """Per-span silence-hallucination gate (TURNS-EXECUTION-PLAN.md
    2026-08-24, task 2c — ported from the finalize's 'silence-halluc drop'):
    a window holding one real sentence plus a decoded span over near-silence
    keeps the sentence and drops the phantom. The old whole-window
    has_speech_energy gate let both through."""
    class TwoSpanEngine:
        def transcribe_timed(self, audio, **kwargs):
            return {"spans": [(0.0, 1.0, "real sentence", "en"),
                              (1.2, 1.9, "Thank you.", "en")],
                    "stats": {}}

    channel = _channel(TwoSpanEngine(), window_s=2.0,
                       hop_s=1.0, lookahead_s=0.0)
    clip = np.zeros(2 * SAMPLE_RATE, np.float32)
    clip[:SAMPLE_RATE] = 0.1          # loud first second; silent second half
    channel._decode_window(clip, 0.0, 2.0)
    snap = channel.snapshot()
    assert [s["text"] for s in snap["spans"]] == ["real sentence"]


def test_single_declared_language_pins_rolling_decode():
    class LanguageEngine:
        def __init__(self):
            self.languages = []

        def transcribe_timed(self, audio, **kwargs):
            self.languages.append(kwargs.get("language"))
            return {"spans": [(0.0, 0.5, "English sentence", "en")], "stats": {}}

    engine = LanguageEngine()
    channel = _channel(engine, languages=("en",), window_s=1.0,
                       hop_s=0.5, lookahead_s=0.0)
    channel._decode_window(np.ones(SAMPLE_RATE, np.float32) * 0.1, 0.0, 1.0)

    assert engine.languages == ["en"]


def test_foreign_rolling_window_is_redecoded_in_declared_language():
    class FlapEngine:
        def __init__(self):
            self.languages = []

        def transcribe_timed(self, audio, **kwargs):
            language = kwargs.get("language")
            self.languages.append(language)
            if language is None:
                return {"spans": [(0.0, 0.5, "Hvað er það?", "is")], "stats": {}}
            return {"spans": [(0.0, 0.5, "What is that?", language)], "stats": {}}

    engine = FlapEngine()
    channel = _channel(engine, languages=("en", "zh"), window_s=1.0,
                       hop_s=0.5, lookahead_s=0.0)
    channel._decode_window(np.ones(SAMPLE_RATE, np.float32) * 0.1, 0.0, 1.0)

    assert engine.languages == [None, "en"]
    assert [s["text"] for s in channel.snapshot()["spans"]] == ["What is that?"]


def test_audio_buffer_stays_bounded_to_a_future_window():
    engine = _EmptyEngine()
    channel = _channel(engine, window_s=1.0,
                       hop_s=0.5, lookahead_s=0.25)
    channel.push(np.ones(3 * SAMPLE_RATE, np.float32) * 0.01)
    assert channel._idle.wait(3.0)

    with channel.lock:
        retained = len(channel._audio_locked())
        base = channel._base_sample
    assert retained <= SAMPLE_RATE
    assert base >= 2 * SAMPLE_RATE
    assert engine.calls >= 1


def test_close_coalesces_a_backlog_into_one_tail_decode():
    entered = threading.Event()
    release = threading.Event()

    class BlockingEngine(_EmptyEngine):
        def transcribe_timed(self, audio, **kwargs):
            self.calls += 1
            if self.calls == 1:
                entered.set()
                assert release.wait(3.0)
            return {"spans": [], "stats": {}}

    engine = BlockingEngine()
    channel = _channel(engine, window_s=1.0,
                       hop_s=0.5, lookahead_s=0.25)
    # Enough audio for several overdue hops. Only the already-running window
    # and one close-time tail are allowed to reach the decoder.
    channel.push(np.ones(4 * SAMPLE_RATE, np.float32) * 0.01)
    assert entered.wait(2.0)

    finished = threading.Event()

    def close():
        channel.finish(timeout_s=2.0)
        finished.set()

    closer = threading.Thread(target=close)
    closer.start()
    time.sleep(0.05)
    release.set()
    assert finished.wait(3.0)
    closer.join(1.0)

    assert engine.calls == 2, "close must discard overdue intermediate hops"



def test_hallucination_span_dropped_from_rolling_transcript():
    """Unified commit-text gate (2026-08-28): the rolling transcript's spans
    ride the wire as stable_text / stable_spans and can be substituted into
    committed lines — they must pass the same silence-hallucination text
    guards as every commit. A stock pleasantry span riding a LOUD window
    (so the per-span ENERGY gate cannot catch it) must be dropped; the real
    sentence next to it survives."""
    class JunkSpanEngine:
        def transcribe_timed(self, audio, **kwargs):
            return {"spans": [(0.0, 0.9, "real sentence here", "en"),
                              (1.0, 1.9, "Thank you for watching.", "en")],
                    "stats": {}}

    channel = _channel(JunkSpanEngine(), window_s=2.0,
                       hop_s=1.0, lookahead_s=0.0)
    clip = np.ones(2 * SAMPLE_RATE, np.float32) * 0.1   # loud EVERYWHERE
    channel._decode_window(clip, 0.0, 2.0)
    snap = channel.snapshot()
    assert [s["text"] for s in snap["spans"]] == ["real sentence here"]


def test_repetition_loop_span_dropped_from_rolling_transcript():
    """A whisper repetition-loop span (the loop-junk class) must not become
    durable stable text either — same gate, engine-verdict guard."""
    class LoopSpanEngine:
        def transcribe_timed(self, audio, **kwargs):
            loop = "the same words " * 12
            return {"spans": [(0.0, 1.8, loop.strip(), "en")], "stats": {}}

    channel = _channel(LoopSpanEngine(), window_s=2.0,
                       hop_s=1.0, lookahead_s=0.0)
    clip = np.ones(2 * SAMPLE_RATE, np.float32) * 0.1
    channel._decode_window(clip, 0.0, 2.0)
    assert channel.snapshot()["spans"] == []


def test_engine_without_transcribe_timed_is_refused_at_construction():
    """TH-018: an engine missing transcribe_timed must fail at stream_start
    with a message, never at the first window as a swallowed AttributeError
    that leaves an armed-looking take committing nothing."""
    import pytest

    class NoTimedEngine:
        def transcribe(self, audio):
            return {"text": "x"}

    with pytest.raises(TypeError, match="transcribe_timed"):
        RollingStableChannel(NoTimedEngine(), channel="sys", speaker="Them", beam_size=4)
