"""Continual-personal-learning setup 2: decode confidence and per-phrase bias
strength across the C#<->Python worker IPC.

Two layers, each pinned at the contract the next one consumes:

  * `beam_decode.beam_search` -- the surviving hypotheses are REPORTED
    (stats["nbest"]) without changing what is RETURNED, and a per-phrase delta
    moves the phrase it was given to and nothing else.
  * `asr_server.op_transcribe` -- the wire: host-indexed `phrase_deltas` are
    remapped onto the merged vocabulary before the engine sees them; `nbest`
    and `avg_logprob` come back as top-level fields; an engine that reports
    neither (the greedy path, the mac engine, any older worker) yields nulls
    rather than a missing key; and the frame codec survives a payload two
    orders of magnitude larger than a normal reply, which an N-best list over
    a long take can be.

Backward compatibility is the headline property and is asserted directly: a
request with no `phrase_deltas` produces the exact engine call it produced
before, and a beam search with no `stats` dict returns the exact tokens it
returned before. The FakeDecoderSession is test_beam_decode's (same scripted
logits, same fake tokenizer), so the decode the N-best claims are made about
is one the gate tests already trust.

Run: python -m pytest transcription_worker/test_ipc_confidence.py
"""

from __future__ import annotations

import base64
import io
import json
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker import asr_server  # noqa: E402
from transcription_worker.beam_decode import BiasTrie, beam_search  # noqa: E402
from transcription_worker.test_beam_decode import (  # noqa: E402
    EOT,
    FakeDecoderSession,
    FakeTokenizer,
    WORD_A,
    WORD_B,
    _logits,
    _speech_sot,
    _text,
)

WORD_C = 300
PREFIX_LEN = 4  # whisper-base's forced prefix on the fake tokenizer


def _search(text_logits=None, bias=None, stats=None):
    sess = FakeDecoderSession(_speech_sot(), text_logits if text_logits is not None else _text())
    return beam_search(sess, cross_kv={}, tokenizer=FakeTokenizer(), beam_size=4,
                       max_len=12, bias=bias, stats=stats)


# ---------------------------------------------------------------------------
# beam_decode: N-best
# ---------------------------------------------------------------------------


def test_nbest_has_at_least_two_distinct_hypotheses_at_beam_4():
    stats: dict = {}
    tokens = _search(stats=stats)
    nbest = stats["nbest"]
    assert len(nbest) >= 2
    assert len({tuple(h["tokens"]) for h in nbest}) == len(nbest), "duplicates"
    assert nbest[0]["tokens"] == tokens, "nbest[0] is the returned winner"
    scores = [h["score"] for h in nbest]
    assert scores == sorted(scores, reverse=True), "best first"
    assert all(h["generated"] == len(h["tokens"]) - PREFIX_LEN for h in nbest)


def test_nbest_avg_logprob_is_raw_acoustic_not_biased():
    """Each hypothesis' avg_logprob is its own mean raw log-softmax, with no
    delta in it -- the same invariant test_avg_logprob_ignores_the_bias_trie
    pins for the winner, here for every survivor."""
    text = _text()
    raw = text - text.max() - np.log(np.exp(text - text.max()).sum())
    trie = BiasTrie.from_sequences([[WORD_A, WORD_B]], delta=30.0, completion_bonus=5.0)
    stats: dict = {}
    _search(bias=trie, stats=stats)
    for h in stats["nbest"]:
        gen = h["tokens"][PREFIX_LEN:]
        if not gen:
            assert h["avg_logprob"] is None
            continue
        assert h["avg_logprob"] == pytest.approx(float(np.mean([raw[t] for t in gen])), abs=1e-5)


def test_reporting_nbest_does_not_change_the_returned_tokens():
    """No `stats` dict = exactly the decode the host got before this setup."""
    assert _search(stats=None) == _search(stats={})


# ---------------------------------------------------------------------------
# beam_decode: per-phrase delta
# ---------------------------------------------------------------------------

# A near-tie the bias can tip: A leads B by 0.1 nat; C is 3 nats back.
_TIE = {str(WORD_A): 8.0, str(WORD_B): 7.9, str(WORD_C): 5.0, str(EOT): 6.0}


def _tie_trie() -> BiasTrie:
    # Three single-token phrases: index 0 = A, 1 = B, 2 = C.
    return BiasTrie.from_sequences([[WORD_A], [WORD_B], [WORD_C]],
                                   delta=0.0, completion_bonus=0.0)


def test_per_phrase_delta_changes_the_output_for_that_phrase_only():
    uniform = _search(_logits(**_TIE), bias=_tie_trie())
    assert uniform[PREFIX_LEN] == WORD_A, "sanity: A wins the tie unaided"

    boosted_b = _tie_trie()
    boosted_b.set_phrase_deltas({1: 3.0})
    flipped = _search(_logits(**_TIE), bias=boosted_b)
    assert flipped[PREFIX_LEN] == WORD_B, "boosting phrase 1 (B) flips the near-tie"

    # Boosting a phrase that is NOT in contention (C, 3 nats back, +2 is not
    # enough) leaves the decode exactly as it was -- the delta reached C and
    # only C.
    boosted_c = _tie_trie()
    boosted_c.set_phrase_deltas({2: 2.0})
    assert _search(_logits(**_TIE), bias=boosted_c) == uniform

    # Boosting the phrase that already wins may reshape ITS trajectory (the
    # repeat-completion rule retires A's boost on the second A, so EOT can
    # win a step earlier) but must never pull B or C into the transcript.
    boosted_a = _tie_trie()
    boosted_a.set_phrase_deltas({0: 3.0})
    with_a = _search(_logits(**_TIE), bias=boosted_a)
    assert with_a[PREFIX_LEN] == WORD_A
    assert WORD_B not in with_a and WORD_C not in with_a


def test_empty_phrase_deltas_is_the_uniform_path():
    plain = _tie_trie()
    empty = _tie_trie()
    empty.set_phrase_deltas({})
    assert _search(_logits(**_TIE), bias=plain) == _search(_logits(**_TIE), bias=empty)


# ---------------------------------------------------------------------------
# asr_server: the wire
# ---------------------------------------------------------------------------


class NewEngine:
    """Accepts phrase_deltas and reports nbest/avg_logprob, like WhisperQnn."""

    def __init__(self, stats=None):
        self.calls: list[dict] = []
        self._stats = stats if stats is not None else {"chunks": 1, "tokens": 1}

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None,
                   phrase_deltas=None, **_kw):
        self.calls.append({"bias_phrases": bias_phrases, "beam_size": beam_size,
                           "phrase_deltas": phrase_deltas})
        return {"text": "top", "stats": dict(self._stats)}


class OldEngine:
    """No phrase_deltas parameter, no nbest in stats -- yesterday's worker."""

    def __init__(self):
        self.calls: list[dict] = []

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None, **_kw):
        self.calls.append({"bias_phrases": bias_phrases, "beam_size": beam_size, **_kw})
        return {"text": "top", "stats": {"chunks": 1, "tokens": 1}}


def _pcm_b64(samples=(100, -100, 50)) -> str:
    return base64.b64encode(np.array(samples, dtype=np.int16).tobytes()).decode("ascii")


@pytest.fixture(autouse=True)
def _reset_globals():
    saved = (asr_server._engine, dict(asr_server._p_you_cache))
    asr_server._p_you_cache.clear()
    yield
    asr_server._engine = saved[0]
    asr_server._p_you_cache.clear()
    asr_server._p_you_cache.update(saved[1])


def _req(**over):
    base = {"op": "transcribe", "pcm_b64": _pcm_b64(), "beam_size": 4}
    base.update(over)
    return base


def test_phrase_deltas_are_remapped_onto_the_deduped_request_vocabulary():
    """Wire contract v2: the request's bias_phrases is the whole list, so the
    host's indices are the trie's -- except that a phrase listed twice folds
    onto one slot (the larger delta wins)."""
    eng = NewEngine()
    asr_server._engine = eng
    asr_server.op_transcribe(_req(bias_phrases=["Cluely", "Trovioo", "Cluely"],
                                  phrase_deltas={"1": 2.5, "0": 1.0, "2": 1.5}))
    call = eng.calls[-1]
    assert call["bias_phrases"] == ["Cluely", "Trovioo"]
    # host index 1 (Trovioo) -> 1; host indices 0 and 2 (Cluely) -> 0, max wins
    assert call["phrase_deltas"] == {1: 2.5, 0: 1.5}


def test_phrase_deltas_accept_int_keys_and_skip_garbage():
    eng = NewEngine()
    asr_server._engine = eng
    asr_server.op_transcribe(_req(bias_phrases=["A", "B"],
                                  phrase_deltas={0: 1.5, "7": 9.0, "x": 1.0, "1": "nope"}))
    assert eng.calls[-1]["phrase_deltas"] == {0: 1.5}


def test_omitting_phrase_deltas_reproduces_todays_engine_call():
    eng = NewEngine()
    asr_server._engine = eng
    asr_server.op_transcribe(_req(bias_phrases=["A"]))
    asr_server.op_transcribe(_req(bias_phrases=["A"], phrase_deltas={}))
    asr_server.op_transcribe(_req(bias_phrases=["A"], phrase_deltas=None))
    assert all(c["phrase_deltas"] is None for c in eng.calls)
    assert all(c["bias_phrases"] == ["A"] for c in eng.calls)


def test_old_engine_never_sees_phrase_deltas():
    eng = OldEngine()
    asr_server._engine = eng
    resp = asr_server.op_transcribe(_req(bias_phrases=["A"], phrase_deltas={"0": 3.0}))
    assert "phrase_deltas" not in eng.calls[-1]
    assert resp["ok"] is True
    # ...and reports the absence of a measurement as null, not as a KeyError.
    assert resp["nbest"] is None
    assert resp["avg_logprob"] is None


def test_nbest_and_avg_logprob_cross_the_wire_whitelisted():
    stats = {"chunks": 1, "tokens": 2, "avg_logprob": -0.31,
             "nbest": [{"text": "top", "score": -0.1, "avg_logprob": -0.31,
                        "tokens": [1, 2], "generated": 2},
                       {"text": "tap", "score": -0.4, "avg_logprob": -0.9,
                        "tokens": [1, 3], "generated": 2}]}
    asr_server._engine = NewEngine(stats)
    resp = asr_server.op_transcribe(_req())
    assert resp["avg_logprob"] == -0.31
    assert resp["nbest"] == [
        {"text": "top", "score": -0.1, "avg_logprob": -0.31},
        {"text": "tap", "score": -0.4, "avg_logprob": -0.9},
    ]
    assert resp["nbest"][0]["text"] == resp["text"]
    json.dumps(resp)  # the whole reply is wire-serializable


def test_empty_audio_reply_carries_the_new_keys_as_null():
    asr_server._engine = NewEngine()
    resp = asr_server.op_transcribe(_req(pcm_b64=_pcm_b64(())))
    assert resp["nbest"] is None and resp["avg_logprob"] is None


def test_frame_codec_round_trips_a_large_reply():
    """An N-best list over a long take is far bigger than today's replies;
    the 4-byte big-endian prefix must carry it unchanged, and a shorter-than-
    declared payload must read as EOF rather than as a truncated JSON."""
    msg = {"ok": True, "text": "x" * 1000,
           "nbest": [{"text": f"hyp {i} " + "word " * 200, "score": -i / 7,
                      "avg_logprob": -0.5} for i in range(2000)]}
    buf = io.BytesIO()
    asr_server._write_frame(buf, msg)
    raw = buf.getvalue()
    assert len(raw) > 2_000_000
    assert int.from_bytes(raw[:4], "big") == len(raw) - 4
    assert asr_server._read_frame(io.BytesIO(raw)) == msg
    assert asr_server._read_frame(io.BytesIO(raw[:-1])) is None
