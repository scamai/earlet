"""Independent verification of continual-personal-learning setup 2 (IPC).

Written by the setup-2 VERIFIER, not the implementer, against the same fakes
`test_ipc_confidence.py` uses. Each test pins one thing the verifier checked
by hand on the real NPU engine so that the
property stays pinned after the engine is no longer on the desk:

  * claim A -- `beam_search.prepare` charges `delta_for_token`, not the
    scalar: the decoder must call `delta_for_token` for the boosted
    candidates at selection time (origin/demo only called it in `advance`).
  * claim B -- with no map set, `delta_for_token` IS the scalar for every
    token (so the uniform path cannot differ by construction).
  * adversarial `phrase_deltas` on the wire (out-of-range / negative index,
    non-numeric key, string value, wrong container, NaN/inf) never raise
    out of `op_transcribe` and never reach the engine as a bad kwarg.
  * beam_size=1 + a delta map -> null nbest/avg_logprob, no throw.
  * the length-prefixed frame codec round-trips >64 KB and >1 MB.
  * `_wire_nbest` whitelists fields (engine internals never widen the wire).

Run: python -m pytest transcription_worker/test_ipc_confidence_verify.py
"""

from __future__ import annotations

import base64
import io
import json
import math
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker import asr_server  # noqa: E402
from transcription_worker.beam_decode import BiasTrie, beam_search  # noqa: E402
from transcription_worker.test_beam_decode import (  # noqa: E402
    FakeDecoderSession,
    FakeTokenizer,
    WORD_A,
    WORD_B,
    EOT,
    _logits,
    _speech_sot,
    _text,
)

WORD_C = 300
PREFIX_LEN = 4


# ---------------------------------------------------------------------------
# claim A / B at the trie level
# ---------------------------------------------------------------------------


def test_delta_for_token_is_the_scalar_for_every_token_without_a_map():
    trie = BiasTrie.from_sequences([[WORD_A], [WORD_B]], delta=1.5, completion_bonus=0.0)
    for t in range(0, 400):
        assert trie.delta_for_token((), t) == 1.5
    trie.set_phrase_deltas({})
    for t in range(0, 400):
        assert trie.delta_for_token((), t) == 1.5


def test_prepare_charges_per_phrase_delta_not_scalar():
    """Selection must see the PER-PHRASE amount, not the scalar.

    Logits: EOT 9.0 dominant, A 8.0, B 7.0; scalar delta 1.0; per-phrase
    delta 3.0 on B. Charged per token (HEAD), "B eot" = (7+3+9)/2 = 9.5 beats
    a bare "eot" (9.0) and "A eot" (9.0), so B is emitted. Charged with the
    scalar (origin/demo), B is 7+1 and nothing beats the bare EOT -- the map
    cannot change the winner. Verified: HEAD -> [B, EOT]; origin/demo -> [EOT].
    The uniform control (no map) is [EOT] on both.
    """
    tok = FakeTokenizer()
    lg = _logits(**{str(WORD_A): 8.0, str(WORD_B): 7.0, str(EOT): 9.0})

    def run(pd):
        trie = BiasTrie.from_sequences([[WORD_A], [WORD_B]], delta=1.0, completion_bonus=0.0)
        if pd:
            trie.set_phrase_deltas(pd)
        return beam_search(FakeDecoderSession(_speech_sot(), lg), cross_kv={}, tokenizer=tok,
                           beam_size=4, max_len=12, bias=trie)[PREFIX_LEN:]

    assert run(None) == [EOT], "control: uniform delta 1.0 cannot beat the dominant EOT"
    assert run({1: 3.0}) == [WORD_B, EOT], "per-phrase delta on B must be charged at selection"
    assert run({0: 3.0}) == [WORD_A, EOT]


# ---------------------------------------------------------------------------
# asr_server: adversarial phrase_deltas never reach the engine badly
# ---------------------------------------------------------------------------


class _SpyEngine:
    """Records the kwargs op_transcribe forwards. Accepts phrase_deltas."""

    def __init__(self):
        self.calls: list[dict] = []
        self.stats: dict | None = None  # override the reported stats wholesale

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None,
                   phrase_deltas=None, **kw):
        self.calls.append({"bias_phrases": bias_phrases, "beam_size": beam_size,
                           "phrase_deltas": phrase_deltas, **kw})
        if self.stats is not None:
            return {"text": "hi", "stats": dict(self.stats)}
        stats = {"path": "beam" if beam_size > 1 else "greedy"}
        if beam_size > 1:
            stats["nbest"] = [{"text": "hi", "score": -0.1, "avg_logprob": -0.2,
                               "tokens": [1, 2], "generated": 2}]
            stats["avg_logprob"] = -0.2
        return {"text": "hi", "stats": stats}


@pytest.fixture
def spy_engine(monkeypatch):
    eng = _SpyEngine()
    monkeypatch.setattr(asr_server, "_engine", eng)
    monkeypatch.setattr(asr_server, "_p_you_cache", {})
    return eng


def _req(**extra):
    # A loud deterministic ramp so the silence precheck does not swallow it.
    ramp = (np.sin(np.arange(16000) * 0.05) * 8000).astype(np.int16).tobytes()
    return {"op": "transcribe", "pcm_b64": base64.b64encode(ramp).decode(),
            "sample_rate": 16000, **extra}


def _engine_reached(rep, engine):
    if rep.get("dropped_reason") or not engine.calls:
        pytest.skip("silence precheck ate the synthetic audio; engine not reached")


@pytest.mark.parametrize("deltas", [
    {"7": 3.0},             # out of range
    {"-1": 3.0},            # negative index
    {"north": 3.0},         # non-numeric key
    {"0": "big"},           # non-numeric value
    [3.0, 1.0],             # wrong container
    "3.0",                  # wrong type
    {},                     # empty
    None,
])
def test_unusable_phrase_deltas_are_dropped_not_raised(spy_engine, deltas):
    rep = asr_server.op_transcribe(_req(bias_phrases=["North Star", "K"],
                                        beam_size=4, phrase_deltas=deltas))
    assert rep["ok"] is True, rep
    _engine_reached(rep, spy_engine)
    assert spy_engine.calls[-1]["phrase_deltas"] is None


def test_nan_and_inf_deltas_are_dropped_and_counted(spy_engine):
    """Was: "documents CURRENT behaviour -- non-finite deltas reach the engine"
    (on the real NPU, inf gave a one-token transcript with ok:true). Now the
    contract (V2 F1): non-finite deltas never reach the trie; the reply says
    how many were thrown away."""
    rep = asr_server.op_transcribe(_req(bias_phrases=["North Star", "K"], beam_size=4,
                                        phrase_deltas={"0": float("nan"), "1": float("inf")}))
    assert rep["ok"] is True
    _engine_reached(rep, spy_engine)
    assert spy_engine.calls[-1]["phrase_deltas"] is None
    assert rep["stats"]["phrase_deltas_rejected"] == 2


def test_huge_finite_delta_is_clamped_to_the_documented_edge(spy_engine):
    """1e300 serialises as a plain JSON number on the C# side and used to
    overflow final_score into `Infinity` on the wire. It now arrives at the
    trie as PHRASE_DELTA_MAX, and the clamp is counted."""
    rep = asr_server.op_transcribe(_req(bias_phrases=["North Star", "K"], beam_size=4,
                                        phrase_deltas={"0": 1e300, "1": -1e300}))
    assert rep["ok"] is True
    _engine_reached(rep, spy_engine)
    assert spy_engine.calls[-1]["phrase_deltas"] == {
        0: asr_server.PHRASE_DELTA_MAX, 1: asr_server.PHRASE_DELTA_MIN}
    assert rep["stats"]["phrase_deltas_rejected"] == 2
    assert asr_server.PHRASE_DELTA_MAX == 6.0, "highest delta that decodes whole on this engine"
    assert asr_server.PHRASE_DELTA_MIN == -10.0
    # The edge itself is in range; just past it is clamped and counted.
    rep = asr_server.op_transcribe(_req(bias_phrases=["North Star"], beam_size=4,
                                        phrase_deltas={"0": 6.0}))
    assert spy_engine.calls[-1]["phrase_deltas"] == {0: 6.0}
    assert "phrase_deltas_rejected" not in rep["stats"]
    rep = asr_server.op_transcribe(_req(bias_phrases=["North Star"], beam_size=4,
                                        phrase_deltas={"0": 8.0}))
    assert spy_engine.calls[-1]["phrase_deltas"] == {0: 6.0}
    assert rep["stats"]["phrase_deltas_rejected"] == 1


def test_remap_folds_a_duplicated_phrase_onto_one_slot(spy_engine):
    """Wire contract v2: no init defaults prefix the vocabulary; the request
    list is the trie's list, deduped, and a repeated phrase's deltas fold."""
    rep = asr_server.op_transcribe(_req(bias_phrases=["Snapdragon", "North Star", "Snapdragon"],
                                        beam_size=4, phrase_deltas={"1": 2.5, "0": 2.0, "2": 3.0}))
    assert rep["ok"] is True
    _engine_reached(rep, spy_engine)
    call = spy_engine.calls[-1]
    assert call["bias_phrases"] == ["Snapdragon", "North Star"]
    assert call["phrase_deltas"] == {0: 3.0, 1: 2.5}


def test_beam_size_1_with_deltas_yields_null_nbest_and_no_throw(spy_engine):
    rep = asr_server.op_transcribe(_req(bias_phrases=["North Star"], beam_size=1,
                                        phrase_deltas={"0": 3.0}))
    assert rep["ok"] is True
    assert rep["nbest"] is None and rep["avg_logprob"] is None


def test_wire_nbest_whitelists_fields():
    out = asr_server._wire_nbest([{"text": "a", "score": 1.0, "avg_logprob": -0.1,
                                   "tokens": [1, 2], "generated": 2, "secret": 1}])
    assert out == [{"text": "a", "score": 1.0, "avg_logprob": -0.1}]
    assert asr_server._wire_nbest([]) is None and asr_server._wire_nbest(None) is None


# ---------------------------------------------------------------------------
# frame codec
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("size", [70_000, 1_300_000])
def test_frame_codec_roundtrips_large_payloads(size):
    msg = {"ok": True, "nbest": [{"text": "x" * size, "score": -1.0, "avg_logprob": None}]}
    buf = io.BytesIO()
    asr_server._write_frame(buf, msg)
    raw = buf.getvalue()
    assert len(raw) > size
    assert asr_server._read_frame(io.BytesIO(raw)) == msg
    assert json.loads(raw[4:]) == msg


# ---------------------------------------------------------------------------
# FINDING F1 (verifier run 2, real NPU) -- FIXED: a huge-but-finite delta
# (1e300, which System.Text.Json serialises happily) or +inf overflowed
# `final_score`, and the reply carried `"score": Infinity` / `NaN` -- Python's
# json.dumps emits the bare literals, System.Text.Json's JsonDocument.Parse
# rejects them, and NotesWorkerClient.TranscribeDetailedAsync threw for that
# call. Two fences now: `_remap_phrase_deltas` clamps/drops the delta before
# the trie, and `_wire_nbest` / the avg_logprob field map any non-finite
# number to null. The tests below drive the trie DIRECTLY with the bad delta
# (bypassing the clamp) so the second fence is proven on its own.
# ---------------------------------------------------------------------------


def _beam_stats_with_delta(delta: float) -> dict:
    tok = FakeTokenizer()
    lg = _logits(**{str(WORD_A): 8.0, str(WORD_B): 7.0, str(EOT): 9.0})
    trie = BiasTrie.from_sequences([[WORD_A], [WORD_B]], delta=1.0, completion_bonus=0.0)
    trie.set_phrase_deltas({1: delta})
    stats: dict = {}
    beam_search(FakeDecoderSession(_speech_sot(), lg), cross_kv={}, tokenizer=tok,
                beam_size=4, max_len=12, bias=trie, stats=stats)
    return stats


@pytest.mark.parametrize("delta", [1e300, float("inf")])
def test_f1_huge_delta_keeps_every_wire_score_finite_or_null(delta):
    stats = _beam_stats_with_delta(delta)
    assert any(not math.isfinite(h["score"]) for h in stats["nbest"]),         "precondition: the unclamped trie really does overflow the score"
    wire = asr_server._wire_nbest(stats["nbest"])
    assert wire, "beam path must report nbest"
    for h in wire:
        assert h["score"] is None or math.isfinite(h["score"]), h
        assert h["avg_logprob"] is None or math.isfinite(h["avg_logprob"]), h


def test_f1_wire_nbest_never_frames_a_bare_non_finite_literal():
    """Was: documents that json.dumps wrote `Infinity` into the frame. Now:
    the `_wire_nbest` output (ONLY that -- the full reply is covered by the
    F2 test below) frames as strict JSON, parsed with the constants
    forbidden, exactly what JsonDocument.Parse does on the C# side."""
    stats = _beam_stats_with_delta(1e300)
    buf = io.BytesIO()
    asr_server._write_frame(buf, {"nbest": asr_server._wire_nbest(stats["nbest"])})
    raw = buf.getvalue()[4:]
    assert b"Infinity" not in raw and b"NaN" not in raw
    strict = json.loads(raw, parse_constant=lambda c: (_ for _ in ()).throw(ValueError(c)))
    assert any(h["score"] is None for h in strict["nbest"])


def test_f1_avg_logprob_field_is_sanitised_too(spy_engine):
    spy_engine.stats = {"chunks": 1, "tokens": 1, "avg_logprob": float("nan"),
                        "nbest": [{"text": "x", "score": float("-inf"), "avg_logprob": -0.3}]}
    rep = asr_server.op_transcribe(_req(beam_size=4))
    assert rep["avg_logprob"] is None
    assert rep["nbest"] == [{"text": "x", "score": None, "avg_logprob": -0.3}]


def test_beam_size_0_with_deltas_is_greedy_shape(spy_engine):
    rep = asr_server.op_transcribe(_req(bias_phrases=["North Star"], beam_size=0,
                                        phrase_deltas={"0": 3.0}))
    assert rep["ok"] is True
    assert rep["nbest"] is None and rep["avg_logprob"] is None


# ---------------------------------------------------------------------------
# Re-verification @ 98659e051 (verifier run 2b)
# ---------------------------------------------------------------------------


def test_f2_full_reply_frame_is_strict_json_even_when_engine_reports_non_finite(spy_engine):
    """Was F2 (strict xfail): the `stats` copy of nbest/avg_logprob went out
    unsanitised, so one non-finite engine number made the whole frame
    non-JSON for JsonDocument.Parse. Now `_sanitize_floats` walks stats
    recursively; the full reply frames as strict JSON. Defence-in-depth --
    unreachable through the clamp -- but the frame must not depend on it."""
    spy_engine.stats = {"chunks": 1, "tokens": 1, "avg_logprob": float("nan"),
                        "nbest": [{"text": "x", "score": float("-inf"), "avg_logprob": -0.3}]}
    rep = asr_server.op_transcribe(_req(beam_size=4))
    buf = io.BytesIO()
    asr_server._write_frame(buf, rep)
    raw = buf.getvalue()[4:]
    assert b"Infinity" not in raw and b"NaN" not in raw
    json.loads(raw, parse_constant=lambda c: (_ for _ in ()).throw(ValueError(c)))


def test_clamp_edges_are_in_range_and_not_counted(spy_engine):
    for v in (asr_server.PHRASE_DELTA_MIN, asr_server.PHRASE_DELTA_MAX, 0.0, -0.0):
        rep = asr_server.op_transcribe(_req(bias_phrases=["North Star"], beam_size=4,
                                            phrase_deltas={"0": v}))
        _engine_reached(rep, spy_engine)
        assert "phrase_deltas_rejected" not in rep["stats"], v
    for v in (asr_server.PHRASE_DELTA_MIN - 1e-9, asr_server.PHRASE_DELTA_MAX + 1e-9):
        rep = asr_server.op_transcribe(_req(bias_phrases=["North Star"], beam_size=4,
                                            phrase_deltas={"0": v}))
        assert rep["stats"]["phrase_deltas_rejected"] == 1, v
        fwd = spy_engine.calls[-1]["phrase_deltas"][0]
        assert fwd in (asr_server.PHRASE_DELTA_MIN, asr_server.PHRASE_DELTA_MAX)


def test_rejected_count_covers_every_rejection_kind_in_one_map(spy_engine):
    rep = asr_server.op_transcribe(_req(bias_phrases=["a", "b"], beam_size=4, phrase_deltas={
        "0": 1e300,          # clamped -> counted
        "1": float("inf"),   # non-finite -> dropped, counted
        "5": 1.0,            # out of range -> counted
        "x": 1.0,            # bad key -> counted
    }))
    _engine_reached(rep, spy_engine)
    assert rep["stats"]["phrase_deltas_rejected"] == 4
    assert spy_engine.calls[-1]["phrase_deltas"] == {0: asr_server.PHRASE_DELTA_MAX}


def test_phrase_delta_ceiling_is_inside_the_sane_decode_band():
    """Measured on the real NPU (whisper-base-qnn, beam 4), verify-setup2.md
    re-verification: δ=7 and δ=8 TRUNCATE the transcript on both clips tried
    (the decoder emits the boosted phrase first and drops everything before
    it); δ≤6 decodes the full sentence on both. The fence exists to keep the
    dial inside what decodes sanely, so its ceiling must not exceed that.
    Was FINDING F3 (strict xfail at MAX=8); fixed by moving the ceiling to 6."""
    assert asr_server.PHRASE_DELTA_MAX <= 6.0, asr_server.PHRASE_DELTA_MAX


# ---------------------------------------------------------------------------
# Final @ 27cf30b85 (verifier run 2c)
# ---------------------------------------------------------------------------


def test_f2_non_finite_floats_nested_anywhere_in_stats_frame_as_strict_json(spy_engine):
    """Lists, nested dicts, tuples and list-inside-dict-inside-list positions,
    not just the two contract fields. Finite numbers, ints, strings and None
    must pass through untouched."""
    spy_engine.stats = {
        "chunks": 1, "tokens": 1, "avg_logprob": float("nan"),
        "nbest": [{"text": "x", "score": float("-inf"), "avg_logprob": -0.3,
                   "tokens": [1, float("inf"), 3]}],
        "per_chunk": [{"dec_ms": float("nan"), "deep": {"deeper": [float("inf"), {"z": float("-inf")}]}}],
        "tuple_like": (1.0, float("nan")), "ok_float": -0.5, "int": 3, "str": "s", "none": None,
    }
    rep = asr_server.op_transcribe(_req(beam_size=4))
    buf = io.BytesIO()
    asr_server._write_frame(buf, rep)
    raw = buf.getvalue()[4:]
    assert b"Infinity" not in raw and b"NaN" not in raw
    strict = json.loads(raw, parse_constant=lambda c: (_ for _ in ()).throw(ValueError(c)))
    st = strict["stats"]
    assert st["avg_logprob"] is None and st["nbest"][0]["score"] is None
    assert st["nbest"][0]["tokens"] == [1, None, 3]
    assert st["per_chunk"] == [{"dec_ms": None, "deep": {"deeper": [None, {"z": None}]}}]
    assert st["tuple_like"] == [1.0, None]
    assert (st["ok_float"], st["int"], st["str"], st["none"]) == (-0.5, 3, "s", None)
    assert strict["avg_logprob"] is None and strict["nbest"][0]["score"] is None


def test_ceiling_six_is_the_edge_and_above_is_clamped_and_counted(spy_engine):
    """Mirror of the real-NPU sweep in verify-setup2.md (Final @ 27cf30b85):
    6 passes through uncounted; 6.0001 / 7 / 1e300 all arrive at the trie as
    exactly 6 and are counted once."""
    assert asr_server.PHRASE_DELTA_MAX == 6.0
    for v, want_rej in ((6.0, 0), (6.0001, 1), (7.0, 1), (1e300, 1)):
        rep = asr_server.op_transcribe(_req(bias_phrases=["North Star"], beam_size=4,
                                            phrase_deltas={"0": v}))
        _engine_reached(rep, spy_engine)
        assert spy_engine.calls[-1]["phrase_deltas"] == {0: 6.0}, v
        assert rep["stats"].get("phrase_deltas_rejected", 0) == want_rej, v
