"""loop-v2 P6 -- the wire side of `fired[]`, `score`, `rescore`.

Backward compatibility is the headline property, asserted the way
`test_ipc_confidence.py` asserts it: a `transcribe` request without the new
fields produces the exact engine call it produced before AND a reply with
exactly yesterday's keys; the new keys appear only when asked for. The
fake engines here report what `WhisperQnn` reports (stats with `fired`,
encoder-cache methods, `forced_score`, `rescore`) without decoding
anything; `test_learning_ops.py` pins the numbers those methods produce.

Run: python -m pytest transcription_worker/test_ipc_learning.py
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
from transcription_worker.test_ipc_confidence import (  # noqa: E402
    NewEngine,
    OldEngine,
    _pcm_b64,
)

# The `transcribe` reply keys a client saw BEFORE P6, frozen. A new key here
# is a wire change and must be opt-in.
YESTERDAYS_KEYS = {"ok", "text", "words", "stats", "channel", "utc_start",
                   "baseline_text", "baseline_ran", "dropped_reason",
                   "avg_logprob", "nbest"}


class LearningEngine(NewEngine):
    """Reports `fired`, accepts `return_fired`/`keep_encoder`, and exposes
    the encoder cache + `forced_score` + `rescore` like WhisperQnn."""

    def __init__(self, stats=None, score_body=None):
        super().__init__(stats)
        self.cache: dict = {}
        self.score_calls: list = []
        self.rescore_calls: list = []
        self.encodes = 0
        self._score_body = score_body

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None,
                   phrase_deltas=None, return_fired=False, keep_encoder=None, **kw):
        self.calls.append({"bias_phrases": bias_phrases, "beam_size": beam_size,
                           "phrase_deltas": phrase_deltas, "return_fired": return_fired,
                           "keep_encoder": keep_encoder, **kw})
        stats = dict(self._stats)
        if return_fired and bias_phrases:
            hit = {"phrase_id": bias_phrases.index(bias_phrases[-1]),
                   "phrase": bias_phrases[-1], "pos": 2, "n": 1}
            stats["nbest"] = [{"text": "top", "score": -0.1, "avg_logprob": -0.3, "fired": [hit]},
                              {"text": "tap", "score": -0.4, "avg_logprob": -0.9, "fired": []}]
            stats["fired"] = [hit]
        elif return_fired:
            stats["fired"] = None
        if keep_encoder:
            self.cache[keep_encoder] = {"kv": np.zeros(2)}
            stats["encoder_id"] = keep_encoder
        return {"text": "top", "stats": stats}

    # -- encoder cache, `WhisperQnn`'s surface ------------------------------
    def encoder_cache_get(self, encoder_id):
        return self.cache.get(encoder_id)

    def encoder_cache_put(self, encoder_id, cross_kv):
        self.cache[encoder_id] = cross_kv

    def encoder_cache_release(self, encoder_id):
        return self.cache.pop(encoder_id, None) is not None

    def encode_clip(self, audio):
        self.encodes += 1
        if len(audio) > 16000 * 30:
            raise ValueError("clip exceeds one 30 s window")
        return {"kv": np.ones(2)}, 7.5

    def lang_token_id(self, language):
        return {"en": 50259, "zh": 50260}.get(language)

    def forced_score(self, cross_kv, language, orig, corr, distractors=None):
        self.score_calls.append((language, orig, corr, list(distractors or [])))
        if self._score_body is not None:
            return dict(self._score_body)
        return {"language": language, "delta": 1.5, "acoustic": 1.5, "ilm": None,
                "window": {"orig": [1, 3], "corr": [1, 3], "len": 2},
                "sums": {"orig": -4.0, "corr": -2.5, "orig_x0": None, "corr_x0": None},
                "full": {"orig": -9.0, "corr": -8.0}, "tokens": {"orig": 3, "corr": 3},
                "distractors": [{"text": d, "delta": 0.2, "acoustic": 0.2, "ilm": None,
                                 "window_len": 2} for d in (distractors or [])],
                "margin": 1.3 if distractors else None,
                "timing_ms": {"decoder": 12.0, "strings": 2 + len(distractors or []),
                              "passes": 2 + len(distractors or [])}}

    def rescore(self, cross_kv, language, bias_phrases, phrase_deltas=None, beam_size=4, **kw):
        self.rescore_calls.append({"language": language, "bias_phrases": list(bias_phrases),
                                   "phrase_deltas": phrase_deltas, "beam_size": beam_size, **kw})
        hit = {"phrase_id": 0, "phrase": bias_phrases[0], "pos": 0, "n": 1}
        return {"text": "second", "dropped_reason": None,
                "stats": {"chunks": 1, "enc_ms": 0.0, "dec_ms": 5.0, "tokens": 3,
                          "encoder_reused": True, "avg_logprob": -0.2, "fired": [hit],
                          "nbest": [{"text": "second", "score": -0.1, "avg_logprob": -0.2,
                                     "fired": [hit]}]}}


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


def _clip(**over):
    base = {"pcm_b64": _pcm_b64(), "sample_rate": 16000}
    base.update(over)
    return base


# ---------------------------------------------------------------------------
# transcribe: yesterday's request, yesterday's reply
# ---------------------------------------------------------------------------


def test_omitting_the_new_fields_reproduces_todays_engine_call_and_reply():
    eng = LearningEngine()
    asr_server._engine = eng
    resp = asr_server.op_transcribe(_req(bias_phrases=["A"], phrase_deltas={"0": 1.0}))
    call = eng.calls[-1]
    assert call["return_fired"] is False and call["keep_encoder"] is None
    assert call["bias_phrases"] == ["A"] and call["phrase_deltas"] == {0: 1.0}
    assert set(resp) == YESTERDAYS_KEYS
    assert "fired" not in json.dumps(resp) and "encoder" not in json.dumps(resp)
    assert resp["nbest"] is None


def test_old_engine_with_a_new_client_answers_null_not_a_type_error():
    eng = OldEngine()
    asr_server._engine = eng
    resp = asr_server.op_transcribe(_req(bias_phrases=["A"], return_fired=True, keep_encoder=True))
    assert "return_fired" not in eng.calls[-1] and "keep_encoder" not in eng.calls[-1]
    assert resp["ok"] is True and resp["fired"] is None and resp["encoder_id"] is None


def test_fired_crosses_the_wire_with_host_indices():
    eng = LearningEngine()
    asr_server._engine = eng
    resp = asr_server.op_transcribe(_req(bias_phrases=["Cluely", "Trovioo"], return_fired=True))
    assert eng.calls[-1]["return_fired"] is True
    # The request list is the trie's list; the engine fired its last entry.
    assert resp["fired"] == [{"i": 1, "phrase": "Trovioo", "pos": 2, "n": 1}]
    assert resp["nbest"][0]["fired"] == resp["fired"]
    assert resp["nbest"][1]["fired"] == []
    assert resp["nbest"][0]["text"] == resp["text"]
    assert "encoder_id" not in resp
    json.dumps(resp)


def test_fired_for_a_phrase_outside_the_request_has_a_null_host_index():
    """`i` is the index into THIS request's list; an engine reporting a phrase
    the host did not send (there are no init defaults any more, but the wire
    shape keeps the null) gets `i: null`, never an exception."""
    asr_server._engine = LearningEngine()
    wire = asr_server._wire_fired([{"phrase_id": 7, "phrase": "Knemo", "pos": 2, "n": 1}],
                                  ["Cluely"], ["Cluely"])
    assert wire == [{"i": None, "phrase": "Knemo", "pos": 2, "n": 1}]


def test_fired_is_null_on_a_decode_with_no_trie():
    asr_server._engine = LearningEngine()
    resp = asr_server.op_transcribe(_req(return_fired=True))
    assert resp["fired"] is None and resp["nbest"] is None


def test_keep_encoder_returns_a_content_addressed_id():
    eng = LearningEngine()
    asr_server._engine = eng
    a = asr_server.op_transcribe(_req(keep_encoder=True, language="en"))
    b = asr_server.op_transcribe(_req(keep_encoder=True, language="en"))
    c = asr_server.op_transcribe(_req(keep_encoder=True, language="zh"))
    assert a["encoder_id"] == b["encoder_id"] != c["encoder_id"]
    # Namespaced by client (wire contract v2): absent client = dictation.
    assert a["encoder_id"].startswith("dictation:enc:") and a["encoder_id"].endswith(":en")
    assert eng.calls[-1]["keep_encoder"] == c["encoder_id"]
    assert "fired" not in a
    # The stub engine above stores it; a multi-chunk skip reports null.
    eng._stats = {"chunks": 2, "tokens": 5, "encoder_cache": "skipped:multichunk"}
    eng.transcribe = lambda audio, **kw: {"text": "t", "stats": dict(eng._stats)}
    assert asr_server.op_transcribe(_req(keep_encoder=True))["encoder_id"] is None


def test_empty_audio_reply_is_unchanged():
    asr_server._engine = LearningEngine()
    resp = asr_server.op_transcribe(_req(pcm_b64=_pcm_b64(()), return_fired=True))
    assert set(resp) == YESTERDAYS_KEYS - {"words", "dropped_reason"}


# ---------------------------------------------------------------------------
# score
# ---------------------------------------------------------------------------


def _score(**over):
    base = {"op": "score", "clip": _clip(), "language": "en", "orig": "a b", "corr": "a c"}
    base.update(over)
    return asr_server.op_score(base)


def test_score_happy_path_encodes_once_then_hits_the_cache():
    eng = LearningEngine()
    asr_server._engine = eng
    first = _score(distractors=["a d"], tag="claim:1")
    assert first["ok"] is True
    assert first["delta"] == 1.5 and first["ilm"] is None and first["margin"] == 1.3
    assert first["window"] == {"orig": [1, 3], "corr": [1, 3], "len": 2}
    assert first["timing_ms"]["encoder"] == 7.5 and first["timing_ms"]["encoder_cached"] is False
    assert first["timing_ms"]["strings"] == 3 and first["tag"] == "claim:1"
    assert eng.score_calls[-1] == ("en", "a b", "a c", ["a d"])
    assert eng.encodes == 1

    second = _score(clip={"encoder_id": first["encoder_id"]})
    assert second["encoder_id"] == first["encoder_id"]
    assert second["timing_ms"]["encoder"] == 0.0 and second["timing_ms"]["encoder_cached"] is True
    assert eng.encodes == 1, "a cached id never re-runs the encoder"


def test_score_error_strings_are_exact():
    asr_server._engine = None
    assert _score()["error"] == "not initialized"
    eng = LearningEngine()
    asr_server._engine = eng
    assert _score(clip=_clip(sample_rate=44100))["error"] == "score: expected 16kHz PCM"
    assert _score(clip=_clip(pcm_b64=_pcm_b64(())))["error"] == "score: empty audio"
    assert _score(clip={"pcm_b64": _pcm_b64(), "encoder_id": "enc:x:en"})["error"] == \
        "score: clip needs exactly one of pcm_b64 or encoder_id"
    assert _score(clip={})["error"] == "score: clip needs exactly one of pcm_b64 or encoder_id"
    assert _score(clip={"encoder_id": "enc:gone:en"})["error"] == "score: encoder_id not cached"
    assert _score(language=None)["error"] == "score: language required"
    assert _score(language="xx")["error"] == "score: unknown language"
    for bad in (-0.1, 1.5, "nan", float("nan"), "x"):
        assert _score(**{"lambda": bad})["error"] == "score: lambda out of range", bad
    assert _score(**{"lambda": 0.5})["error"].startswith("score: lambda > 0 not available")
    assert _score(distractors=["d"] * 9)["error"] == "score: too many distractors"
    long_clip = _clip(pcm_b64=base64.b64encode(np.zeros(16000 * 31, dtype=np.int16).tobytes()).decode())
    assert _score(clip=long_clip)["error"] == "score: clip exceeds one 30 s window"
    assert eng.score_calls == [], "no error path reached the decoder"


def test_score_without_forced_scoring_support_degrades_to_an_error_not_a_crash():
    asr_server._engine = NewEngine()
    assert _score()["error"] == "score: engine does not support forced scoring"


def test_score_keep_encoder_false_does_not_cache():
    eng = LearningEngine()
    asr_server._engine = eng
    resp = _score(keep_encoder=False)
    assert resp["ok"] and eng.cache == {}
    assert _score(clip={"encoder_id": resp["encoder_id"]})["error"] == "score: encoder_id not cached"


def test_score_release_frees_an_entry_and_tolerates_absence():
    eng = LearningEngine()
    asr_server._engine = eng
    enc = _score()["encoder_id"]
    assert asr_server.op_score_release({"op": "score_release", "encoder_id": enc}) == \
        {"ok": True, "released": True}
    assert asr_server.op_score_release({"op": "score_release", "encoder_id": enc}) == \
        {"ok": True, "released": False}


def test_score_reply_is_finite_or_null_everywhere():
    body = {"language": "en", "delta": float("nan"), "acoustic": float("inf"), "ilm": None,
            "window": {"orig": [0, 1], "corr": [0, 1], "len": 1},
            "sums": {"orig": -1.0, "corr": float("-inf"), "orig_x0": None, "corr_x0": None},
            "full": {"orig": -1.0, "corr": -1.0}, "tokens": {"orig": 1, "corr": 1},
            "distractors": [{"text": "d", "delta": float("nan"), "acoustic": 0.0, "ilm": None,
                             "window_len": 1}],
            "margin": None, "timing_ms": {"decoder": 1.0, "strings": 3, "passes": 3}}
    asr_server._engine = LearningEngine(score_body=body)
    resp = _score(distractors=["d"])
    assert resp["delta"] is None and resp["acoustic"] is None and resp["sums"]["corr"] is None
    assert resp["distractors"][0]["delta"] is None
    assert "NaN" not in json.dumps(resp) and "Infinity" not in json.dumps(resp)


def test_frame_codec_round_trips_a_score_reply_with_eight_distractors():
    asr_server._engine = LearningEngine()
    resp = _score(distractors=[f"d{i} " * 50 for i in range(8)])
    assert len(resp["distractors"]) == 8
    buf = io.BytesIO()
    asr_server._write_frame(buf, resp)
    raw = buf.getvalue()
    assert int.from_bytes(raw[:4], "big") == len(raw) - 4
    assert asr_server._read_frame(io.BytesIO(raw)) == json.loads(json.dumps(resp))


# ---------------------------------------------------------------------------
# rescore
# ---------------------------------------------------------------------------


def _rescore(**over):
    base = {"op": "rescore", "language": "en", "bias_phrases": ["Kai Lin", "Kailin"],
            "phrase_deltas": {"0": 2.0, "1": 9.0}, "beam_size": 4}
    base.update(over)
    return asr_server.op_rescore(base)


def test_rescore_on_a_cached_clip_reuses_the_encoder_and_ignores_init_defaults():
    eng = LearningEngine()
    asr_server._engine = eng
    enc = asr_server.op_transcribe(_req(keep_encoder=True, language="en"))["encoder_id"]
    resp = _rescore(encoder_id=enc, tag="hold:7")
    assert resp["ok"] is True and resp["encoder_reused"] is True and resp["encoder_id"] == enc
    assert eng.encodes == 0
    call = eng.rescore_calls[-1]
    assert call["bias_phrases"] == ["Kai Lin", "Kailin"], "no init defaults merged"
    assert call["phrase_deltas"] == {0: 2.0, 1: 6.0}, "clamped to PHRASE_DELTA_MAX"
    assert call["beam_size"] == 4 and call["language"] == "en"
    assert resp["stats"]["phrase_deltas_rejected"] == 1
    assert resp["text"] == "second" and resp["nbest"][0]["text"] == resp["text"]
    assert resp["fired"] == [{"i": 0, "phrase": "Kai Lin", "pos": 0, "n": 1}]
    assert resp["nbest"][0]["fired"] == resp["fired"]
    assert resp["timing_ms"] == {"encoder": 0.0, "encoder_cached": True, "decoder": 5.0, "tokens": 3}
    assert resp["tag"] == "hold:7" and resp["baseline_ran"] is False
    assert YESTERDAYS_KEYS <= set(resp), "the transcribe reply shape, plus the additions"
    json.dumps(resp)


def test_rescore_accepts_audio_when_nothing_is_cached():
    eng = LearningEngine()
    asr_server._engine = eng
    resp = _rescore(pcm_b64=_pcm_b64())
    assert resp["ok"] is True and resp["encoder_reused"] is False and eng.encodes == 1
    assert resp["timing_ms"]["encoder"] == 7.5
    assert eng.cache == {}, "rescore does not retain unless asked"
    kept = _rescore(pcm_b64=_pcm_b64(), keep_encoder=True)
    assert kept["encoder_id"] in eng.cache


def test_rescore_error_strings_are_exact():
    asr_server._engine = None
    assert _rescore(encoder_id="x")["error"] == "not initialized"
    asr_server._engine = LearningEngine()
    assert _rescore(encoder_id="enc:gone:en")["error"] == "rescore: encoder_id not cached"
    assert _rescore(encoder_id="x", bias_phrases=[])["error"] == "rescore: bias_phrases required (1..8)"
    assert _rescore(encoder_id="x", bias_phrases=["p"] * 9)["error"] == "rescore: bias_phrases required (1..8)"
    assert _rescore(encoder_id="x", beam_size=1)["error"] == "rescore: beam_size must be > 1"
    assert _rescore(encoder_id="x", language=None)["error"] == "rescore: language required"
    assert _rescore()["error"] == "rescore: clip needs exactly one of pcm_b64 or encoder_id"
    asr_server._engine = NewEngine()
    assert _rescore(encoder_id="x")["error"] == "rescore: engine does not support rescore"


# ---------------------------------------------------------------------------
# dispatch: the ops are registered; a worker without them says so
# ---------------------------------------------------------------------------


def _serve(frames: list[dict], ops=None) -> list[dict]:
    """Drive `main()` over in-memory stdio with the given op table."""
    inp = io.BytesIO()
    for f in frames:
        asr_server._write_frame(inp, f)
    inp.seek(0)
    out = io.BytesIO()
    stdin, stdout, table = sys.stdin, sys.stdout, asr_server._OPS
    sys.stdin = type("S", (), {"buffer": inp})()
    sys.stdout = type("S", (), {"buffer": out})()
    if ops is not None:
        asr_server._OPS = ops
    try:
        asr_server.main()
    finally:
        sys.stdin, sys.stdout, asr_server._OPS = stdin, stdout, table
    out.seek(0)
    replies = []
    while (r := asr_server._read_frame(out)) is not None:
        replies.append(r)
    return replies


def test_learning_ops_are_registered_and_a_worker_without_them_answers_unknown_op():
    assert {"score", "score_release", "rescore"} <= set(asr_server._OPS)
    asr_server._engine = None
    with_ops = _serve([{"op": "score"}, {"op": "shutdown"}])
    assert with_ops[0] == {"ok": False, "error": "not initialized"}
    without = {k: v for k, v in asr_server._OPS.items() if k not in ("score", "rescore", "score_release")}
    replies = _serve([{"op": "ping"}, {"op": "score"}, {"op": "shutdown"}], ops=without)
    # ping now carries the wire PROTOCOL_VERSION (handshake field, 2026-09-09).
    assert replies == [{"ok": True, "op": "ping", "protocol": asr_server.PROTOCOL_VERSION},
                       {"ok": False, "error": "unknown op: score"},
                       {"ok": True, "op": "shutdown"}]
