"""Unit tests for asr_server.op_transcribe's comparison-mode wiring: request
field parsing (compare_baseline), the L2 p_you pass-through into the
personalized call (per-request `profile`, wire contract v2), the "only
compare when P_you exists" gate, and the request's bias_phrases being the
whole vocabulary. A FakeEngine stands in for WhisperQnn -- op_init's real construction
needs the actual ONNX/QNN assets and is exercised manually via the app, not
here -- so this stays a fast pure-python test of asr_server's own glue logic.

Run: .venv/Scripts/python.exe -m pytest transcription_worker/test_asr_server.py
"""

from __future__ import annotations

import base64
import json
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker import asr_server  # noqa: E402
from transcription_worker import ops_stream, ops_stream_whisper  # noqa: E402


def _frame(message: dict) -> bytes:
    payload = json.dumps(message, separators=(",", ":")).encode("utf-8")
    return struct.pack(">I", len(payload)) + payload


def _read_frames(raw: bytes) -> list[dict]:
    messages = []
    offset = 0
    while offset < len(raw):
        assert len(raw) - offset >= 4
        size = struct.unpack_from(">I", raw, offset)[0]
        offset += 4
        assert len(raw) - offset >= size
        messages.append(json.loads(raw[offset:offset + size].decode("utf-8")))
        offset += size
    return messages


def test_native_stdout_write_cannot_corrupt_protocol_frames():
    """A native library bypassing sys.stdout must land on stderr.

    The worker is isolated in a subprocess because the behavior under test is
    the process fd table itself.  ``os.write(1, ...)`` has the same bypass
    semantics as ONNX Runtime / QNN's cold-context ``Starting...`` output.
    """
    script = r'''
import os
from transcription_worker import asr_server

def native_stdout_probe(_request):
    os.write(1, b"NATIVE_STDOUT_NOISE\n")
    print("PYTHON_STDOUT_NOISE", flush=True)
    return {"ok": True, "op": "native_stdout_probe"}

asr_server._OPS["native_stdout_probe"] = native_stdout_probe
asr_server.main()
'''
    environment = dict(os.environ)
    package_root = str(Path(__file__).resolve().parent.parent)
    existing_path = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = (
        package_root if not existing_path
        else package_root + os.pathsep + existing_path
    )
    wire_input = (
        _frame({"op": "native_stdout_probe"})
        + _frame({"op": "shutdown"})
    )

    completed = subprocess.run(
        [sys.executable, "-c", script],
        input=wire_input,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=environment,
        timeout=20,
        check=False,
    )

    assert completed.returncode == 0, completed.stderr.decode("utf-8", "replace")
    assert _read_frames(completed.stdout) == [
        {"ok": True, "op": "native_stdout_probe"},
        {"ok": True, "op": "shutdown"},
    ]
    assert b"NATIVE_STDOUT_NOISE" in completed.stderr
    assert b"PYTHON_STDOUT_NOISE" in completed.stderr


def test_dedicated_protocol_pipe_isolated_from_process_stdout():
    """Production replies never share the process stdout handle at all."""
    if sys.platform == "win32":
        pytest.skip("Windows named-pipe transport is covered by the installed E2E")
    script = r'''
import os
from transcription_worker import asr_server

def native_stdout_probe(_request):
    os.write(1, b"NATIVE_STDOUT_NOISE\n")
    print("PYTHON_STDOUT_NOISE", flush=True)
    return {"ok": True, "op": "native_stdout_probe"}

asr_server._OPS["native_stdout_probe"] = native_stdout_probe
asr_server.main()
'''
    environment = dict(os.environ)
    package_root = str(Path(__file__).resolve().parent.parent)
    existing_path = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = (
        package_root if not existing_path
        else package_root + os.pathsep + existing_path
    )
    with tempfile.TemporaryDirectory() as temporary:
        protocol_pipe = str(Path(temporary) / "asr-replies.pipe")
        os.mkfifo(protocol_pipe)
        environment["DICTATION_ASR_PROTOCOL_PIPE"] = protocol_pipe

        process = subprocess.Popen(
            [sys.executable, "-c", script],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environment,
        )
        with open(protocol_pipe, "rb", buffering=0) as protocol_reader:
            wire_input = (
                _frame({"op": "native_stdout_probe"})
                + _frame({"op": "shutdown"})
            )
            process_stdout, process_stderr = process.communicate(
                input=wire_input, timeout=20)
            protocol_stdout = protocol_reader.read()

    assert process.returncode == 0, process_stderr.decode("utf-8", "replace")
    assert _read_frames(protocol_stdout) == [
        {"ok": True, "op": "native_stdout_probe"},
        {"ok": True, "op": "shutdown"},
    ]
    assert b"NATIVE_STDOUT_NOISE" in process_stdout
    assert b"PYTHON_STDOUT_NOISE" in process_stderr


def _raw_frame(payload: bytes) -> bytes:
    return struct.pack(">I", len(payload)) + payload


def test_a_malformed_frame_is_answered_and_the_worker_keeps_serving():
    """The header promises the worker "never dies on a bad request". The
    frame read used to sit outside main's try, so undecodable bytes or a JSON
    value that is not an object ended the process; each now gets ok:false
    naming only the error type, and the next frame is served. The nested
    array is a million levels deep because json.loads must raise
    RecursionError for it, which is not a ValueError: measured on Python 3.9,
    3.12 and 3.14, where 3.14's stack-based check lets 100,000 levels through
    to a JSONDecodeError instead."""
    script = "from transcription_worker import asr_server\nasr_server.main()\n"
    environment = dict(os.environ)
    environment["PYTHONPATH"] = str(Path(__file__).resolve().parent.parent)
    environment.pop("DICTATION_ASR_PROTOCOL_PIPE", None)
    wire_input = (_raw_frame(b'{"op": "ping"')            # truncated JSON
                  + _raw_frame(b"\xff\xfe secret words")  # not UTF-8
                  + _raw_frame(b'["op", "ping"]')          # an array
                  + _raw_frame(b'"ping"')                  # a string
                  + _raw_frame(b"[" * 1_000_000)           # nested past the recursion limit
                  + _frame({"op": "ping"})
                  + _frame({"op": "shutdown"}))

    completed = subprocess.run([sys.executable, "-c", script], input=wire_input, capture_output=True,
                               env=environment, timeout=20, check=False)

    assert completed.returncode == 0, completed.stderr.decode("utf-8", "replace")
    replies = _read_frames(completed.stdout)
    assert replies == [
        {"ok": False, "error": "malformed frame: JSONDecodeError"},
        {"ok": False, "error": "malformed frame: UnicodeDecodeError"},
        {"ok": False, "error": "malformed frame: a JSON list, not an object"},
        {"ok": False, "error": "malformed frame: a JSON str, not an object"},
        {"ok": False, "error": "malformed frame: RecursionError"},
        asr_server.op_ping({}),
        {"ok": True, "op": "shutdown"},
    ]
    assert b"secret words" not in completed.stdout + completed.stderr


class FakeEngine:
    """Records every transcribe() call's kwargs so tests can distinguish the
    pure-greedy baseline pass from the personalized pass without needing a
    real WhisperQnn/ONNX session."""

    def __init__(self):
        self.calls: list[dict] = []

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None, **_kw):
        self.calls.append(
            {"bias_phrases": bias_phrases, "beam_size": beam_size, "p_you": p_you}
        )
        return {"text": f"call-{len(self.calls)}", "stats": {"chunks": 1, "tokens": 1}}


def _pcm_b64(samples=(100, -100, 50)) -> str:
    return base64.b64encode(np.array(samples, dtype=np.int16).tobytes()).decode("ascii")


@pytest.fixture(autouse=True)
def user_data(monkeypatch, tmp_path):
    """The user's Dictation data folder for one test: empty unless the test
    writes into it. op_transcribe reads learning.json and bias_aliases.json
    from there on every call, and a developer Mac that has dictated has real
    ones; without this, the assertions below on `bias_phrases` would depend on
    whoever ran them."""
    from transcription_worker import learned_vocabulary, spoken_aliases

    folder = tmp_path / "user-data"
    folder.mkdir()
    monkeypatch.setattr(spoken_aliases, "default_path", lambda: folder / "bias_aliases.json")
    monkeypatch.setattr(learned_vocabulary, "_cache", None)
    monkeypatch.setattr(learned_vocabulary, "_noted", False)
    return folder


@pytest.fixture(autouse=True)
def _reset_globals():
    """asr_server's engine and P_you cache are module globals (single-process
    IPC server) -- save/restore around each test so they don't leak."""
    saved = (asr_server._engine, dict(asr_server._p_you_cache))
    asr_server._p_you_cache.clear()
    yield
    asr_server._engine = saved[0]
    asr_server._p_you_cache.clear()
    asr_server._p_you_cache.update(saved[1])


#: The per-request personal prior (wire contract v2): `profile.you` asks for
#: P_you built from `profile.db_path`; tests seed the cache instead of a db.
PROFILE = {"you": True, "db_path": "fake.db"}


def _seed_p_you(prior):
    asr_server._p_you_cache[PROFILE["db_path"]] = prior
    return prior


# --- not initialized ---------------------------------------------------


def test_transcribe_before_init_returns_error():
    asr_server._engine = None
    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "op": "transcribe"})
    assert resp == {"ok": False, "error": "not initialized"}


# --- empty audio short-circuit ---------------------------------------------


def test_empty_audio_short_circuits_without_calling_engine():
    engine = FakeEngine()
    asr_server._engine = engine
    _seed_p_you(object())
    resp = asr_server.op_transcribe({"pcm_b64": "", "compare_baseline": True,
                                     "profile": PROFILE})
    assert resp["ok"] is True
    assert resp["text"] == ""
    assert resp["baseline_text"] is None
    assert engine.calls == []


# --- L2: p_you always threaded into the personalized call ------------------


def test_p_you_passed_to_personalized_call_even_without_compare_baseline():
    engine = FakeEngine()
    asr_server._engine = engine
    p_you = _seed_p_you(object())
    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "compare_baseline": False,
                                     "profile": PROFILE})
    assert resp["ok"] is True
    assert resp["baseline_text"] is None
    assert len(engine.calls) == 1
    assert engine.calls[0]["p_you"] is p_you


def test_p_you_none_when_not_built():
    engine = FakeEngine()
    asr_server._engine = engine
    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})
    assert resp["ok"] is True
    assert len(engine.calls) == 1
    assert engine.calls[0]["p_you"] is None


# --- comparison mode gating -------------------------------------------------


def test_compare_baseline_skipped_when_p_you_missing():
    engine = FakeEngine()
    asr_server._engine = engine
    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "compare_baseline": True})
    assert resp["baseline_text"] is None
    assert len(engine.calls) == 1  # only the personalized call, no extra greedy pass


def test_compare_baseline_runs_both_passes_when_p_you_available():
    engine = FakeEngine()
    asr_server._engine = engine
    p_you = _seed_p_you(object())
    resp = asr_server.op_transcribe(
        {
            "pcm_b64": _pcm_b64(),
            "compare_baseline": True,
            "beam_size": 4,
            "bias_phrases": ["Avrit Raj"],
            "profile": PROFILE,
        }
    )
    assert resp["baseline_text"] is not None
    assert len(engine.calls) == 2

    # First call: the same decode minus personalisation -- no bias or p_you,
    # but the requested beam is retained so the comparison isolates what the
    # vocabulary changes instead of crediting beam search to it.
    first = engine.calls[0]
    assert first == {"bias_phrases": None, "beam_size": 4, "p_you": None}

    # Second call: the personalized one (merged bias + requested beam + p_you).
    second = engine.calls[1]
    assert second["bias_phrases"] == ["Avrit Raj"]
    assert second["beam_size"] == 4
    assert second["p_you"] is p_you


# --- bias phrases: the request's list IS the vocabulary (deduped, ordered) --


def test_bias_phrases_are_the_requests_list_deduped():
    """Wire contract v2: nothing process-global is prepended. A phrase the
    host lists twice takes one trie slot."""
    engine = FakeEngine()
    asr_server._engine = engine
    resp = asr_server.op_transcribe(
        {
            "pcm_b64": _pcm_b64(),
            "bias_phrases": ["Avrit Raj", "Snapdragon", "Avrit Raj"],
            "beam_size": 4,
        }
    )
    assert resp["ok"] is True
    assert engine.calls[0]["bias_phrases"] == ["Avrit Raj", "Snapdragon"]


# --- detected-language metadata: forward the pin, pass the detection back --


class LanguageAwareEngine:
    """Minimal engine whose transcribe() accepts `language` (so
    op_transcribe's capability check forwards it) and reports a detected
    language in stats, the way whisper_mac does."""

    def __init__(self, detected="zh"):
        self.detected = detected
        self.calls: list[dict] = []

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None,
                   language=None, **_kw):
        self.calls.append({"language": language})
        return {"text": "ok",
                "stats": {"chunks": 1, "tokens": 1,
                          "language": language or self.detected}}


def test_language_pin_forwarded_to_language_aware_engine():
    engine = LanguageAwareEngine()
    asr_server._engine = engine
    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "language": "en"})
    assert engine.calls[-1]["language"] == "en"
    assert resp["stats"]["language"] == "en"


def test_detected_language_passes_through_response_stats():
    engine = LanguageAwareEngine(detected="zh")
    asr_server._engine = engine
    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})
    # Absent request field means auto-detect (None), never a default pin.
    assert engine.calls[-1]["language"] is None
    assert resp["ok"] is True
    assert resp["stats"]["language"] == "zh"


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))


# --- declared-form casing, engine-agnostic ---------------------------------
#
# This pass lived only inside `whisper_mac.transcribe`, so the QNN path had no
# casing restoration at all. It moved here for the same reason the alias pass
# is here: one implementation, both engines, and the mac path is idempotent
# because once the declared form is in the text there is no case-only mismatch
# left to find.


class _CasingEngine:
    """Returns whatever text the test wants, so the pass can be observed."""

    def __init__(self, text: str):
        self.text = text

    def transcribe(self, audio, **_kw):
        return {"text": self.text, "stats": {"chunks": 1, "tokens": 1}}


def test_casing_is_restored_on_the_engine_agnostic_path():
    """The measured gap: a term recovered but spelled flat.

    Finding 39 put exact display form 9.5 points below case-folded recovery
    through the product decoder. Every one of those counts as a hit and is
    still a hand-fix, which is why the metric could not see it.
    """
    asr_server._engine = _CasingEngine("we shipped questpdf and selfblended today")

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(),
                                     "bias_phrases": ["QuestPDF", "SelfBlended"]})

    assert resp["ok"] is True
    assert "QuestPDF" in resp["text"]
    assert "SelfBlended" in resp["text"]


def test_casing_uses_the_calls_own_phrases_too():
    """A term sent with THIS utterance declares its spelling; the pass reads
    the request's list (there is no init-time list any more)."""
    asr_server._engine = _CasingEngine("the arcface run finished")

    resp = asr_server.op_transcribe(
        {"pcm_b64": _pcm_b64(), "bias_phrases": ["ArcFace"]})

    assert "ArcFace" in resp["text"]


def test_casing_only_changes_case_never_spelling():
    """A near-miss is a recognition error, not a casing one. Rewriting it here
    would be the unverified substitution `spoken_aliases` is narrow about.

    The misspelling is deliberately one the shipped alias table does not carry:
    an earlier draft used `arcvase`, which IS a declared alias, so the alias
    pass corrected it before this stage ever saw it and the test proved nothing
    about casing. That the suite caught it is the alias layer working.
    """
    asr_server._engine = _CasingEngine("we evaluated questbdf yesterday")

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["QuestPDF"]})

    assert resp["text"] == "we evaluated questbdf yesterday"


def test_casing_leaves_an_ordinary_english_word_alone():
    """`whisper` is excluded by default: 6 hits per 1.2M words of ordinary
    English, which is over the budget the alias table is priced against."""
    asr_server._engine = _CasingEngine("i heard a whisper in the hallway")

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["Whisper"]})

    assert resp["text"] == "i heard a whisper in the hallway"


def test_a_broken_casing_pass_cannot_cost_the_transcript(monkeypatch):
    """Same contract the alias pass keeps: failure means the text as decoded,
    never a failed dictation."""
    asr_server._engine = _CasingEngine("questpdf shipped")

    from transcription_worker import declared_casing

    def boom(*_a, **_k):
        raise RuntimeError("synthetic")

    monkeypatch.setattr(declared_casing, "apply", boom)

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["QuestPDF"]})

    assert resp["ok"] is True
    assert resp["text"] == "questpdf shipped"


def test_casing_keeps_the_nbest_head_equal_to_the_text():
    """`nbest[0].text == text` is this module's own documented contract.

    Re-casing only the winner would break it on exactly the terms this stage
    exists to fix, and leave the host comparing `QuestPDF` against a runner-up
    spelled `questpdf` — a casing difference read as a decode difference. The
    alias pass above already states that reasoning for its own rewrite.
    """

    class _NbestEngine:
        def transcribe(self, audio, **_kw):
            return {
                "text": "questpdf shipped",
                "stats": {
                    "chunks": 1,
                    "tokens": 1,
                    "nbest": [
                        {"text": "questpdf shipped", "score": -0.1},
                        {"text": "questpdf shipping", "score": -0.9},
                    ],
                },
            }

    asr_server._engine = _NbestEngine()

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["QuestPDF"]})

    assert resp["text"] == "QuestPDF shipped"
    assert resp["nbest"][0]["text"] == resp["text"]
    assert resp["nbest"][1]["text"] == "QuestPDF shipping"


# --- command_prefix (L1b closed-set constraint) ------------------------------
#
# The wire half of Option A: what op_transcribe forwards, what it refuses, and
# the guarantee that a request WITHOUT the field produces the call it produced
# yesterday. The decode semantics live in test_command_prefix.py.


class _CommandPrefixEngine:
    """Names `command_prefix` in its signature, so op_transcribe's capability
    check forwards it (FakeEngine's `**_kw` deliberately does not -- that is
    the old-engine case, covered below)."""

    def __init__(self):
        self.calls: list[dict] = []

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None,
                   command_prefix=None, **_kw):
        self.calls.append({"bias_phrases": bias_phrases, "beam_size": beam_size,
                           "p_you": p_you, "command_prefix": command_prefix})
        return {"text": f"call-{len(self.calls)}", "stats": {"chunks": 1, "tokens": 1}}


CATALOGUE = ["note", "tell", "look up"]


def test_absent_command_prefix_is_not_forwarded_at_all():
    """The dual-use guarantee at the wire: notes never sends this field, and
    its absence must leave the engine call byte-identical. Not `None` in the
    kwargs -- ABSENT, so an engine that never grew the parameter is called
    exactly as before."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4})

    assert resp["ok"] is True
    assert engine.calls[0]["command_prefix"] is None
    assert "command_prefix_rejected" not in (resp.get("stats") or {})


def test_command_prefix_is_forwarded_normalised():
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    asr_server.op_transcribe({
        "pcm_b64": _pcm_b64(), "beam_size": 4,
        "command_prefix": {"phrases": CATALOGUE, "max_tokens": 2,
                           "mode": "hard", "delta": 4.0},
    })

    assert engine.calls[0]["command_prefix"] == {
        "phrases": CATALOGUE, "max_tokens": 2, "mode": "hard", "delta": 4.0}


def test_command_prefix_defaults_are_soft_three_six():
    """Soft by default: the owner's explicit preference is graceful
    degradation, and a default that drifted to `hard` would turn a bad
    transcript into a wrong workflow."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4,
                              "command_prefix": {"phrases": CATALOGUE}})

    assert engine.calls[0]["command_prefix"] == {
        "phrases": CATALOGUE, "max_tokens": 3, "mode": "soft", "delta": 6.0}


def test_command_prefix_dedupes_phrases_order_preserving():
    """A catalogue that lists an alias twice must build the same trie as one
    that lists it once, or `BiasTrieCache`'s key lies about the tree."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    asr_server.op_transcribe({
        "pcm_b64": _pcm_b64(), "beam_size": 4,
        "command_prefix": {"phrases": ["note", "tell", "note", "look up"]},
    })

    assert engine.calls[0]["command_prefix"]["phrases"] == ["note", "tell", "look up"]


def test_command_prefix_is_dropped_from_the_baseline_pass():
    """`compare_baseline` shows what the vocabulary bought. A baseline decoded
    under the closed set would credit the constraint's win to the vocabulary
    and show the two arms agreeing."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    asr_server.op_transcribe({
        "pcm_b64": _pcm_b64(), "beam_size": 4, "compare_baseline": True,
        "bias_phrases": ["Trovioo"],
        "command_prefix": {"phrases": CATALOGUE},
    })

    assert len(engine.calls) == 2
    assert engine.calls[0]["command_prefix"] is None      # baseline
    assert engine.calls[1]["command_prefix"] is not None  # personalised


@pytest.mark.parametrize("spec", [{"phrases": []}, {"phrases": ["", "  "]}])
def test_an_empty_catalogue_is_refused_not_forwarded(spec):
    """An empty closed set would mask the WHOLE vocabulary. Refuse it here
    rather than let the engine reason about it."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4,
                                     "command_prefix": spec})

    assert engine.calls[0]["command_prefix"] is None
    assert "phrases: empty" in resp["stats"]["command_prefix_rejected"]


@pytest.mark.parametrize("spec", [None, {}])
def test_an_empty_or_null_field_reads_as_absent(spec):
    """`{}` and `null` are indistinguishable from not sending the field, and
    are treated as such: nothing forwarded, and nothing reported as rejected,
    because nothing was asked for."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4,
                                     "command_prefix": spec})

    assert engine.calls[0]["command_prefix"] is None
    assert "command_prefix_rejected" not in resp["stats"]


def test_malformed_values_are_clamped_and_counted_never_fatal():
    """Same contract as phrase_deltas_rejected: a host whose catalogue just
    changed must not lose the hold, and must be able to see what was ignored."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({
        "pcm_b64": _pcm_b64(), "beam_size": 4,
        "command_prefix": {"phrases": ["note", 7, None], "max_tokens": 99,
                           "mode": "strict", "delta": float("inf")},
    })

    assert resp["ok"] is True
    forwarded = engine.calls[0]["command_prefix"]
    assert forwarded["phrases"] == ["note"]
    assert forwarded["max_tokens"] == asr_server.COMMAND_PREFIX_MAX_TOKENS
    assert forwarded["mode"] == "soft", "an unknown mode must fail toward soft"
    # A non-finite delta has no meaningful clamp -- it falls back to the
    # default rather than to the ceiling, which is the conservative direction.
    assert forwarded["delta"] == 6.0
    rejected = resp["stats"]["command_prefix_rejected"]
    assert any("max_tokens" in r for r in rejected)
    assert any("mode" in r for r in rejected)
    assert any("delta" in r for r in rejected)
    assert sum("phrase:" in r for r in rejected) == 2


def test_a_non_object_command_prefix_is_refused():
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4,
                                     "command_prefix": ["note", "tell"]})

    assert resp["ok"] is True
    assert engine.calls[0]["command_prefix"] is None
    assert resp["stats"]["command_prefix_rejected"] == ["command_prefix: expected an object"]


def test_an_engine_without_the_parameter_gets_a_transcript_not_a_typeerror():
    """Capability-checked like return_words/language: FakeEngine's signature
    has no `command_prefix`, so the field is dropped and said to be dropped."""
    engine = FakeEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4,
                                     "command_prefix": {"phrases": CATALOGUE}})

    assert resp["ok"] is True
    assert resp["text"] == "call-1"
    assert "engine: no command_prefix support" in resp["stats"]["command_prefix_rejected"]


@pytest.mark.parametrize("sent,expect", [
    (500.0, asr_server.COMMAND_PREFIX_DELTA_MAX),
    (-3.0, 6.0),
    ("loud", 6.0),
])
def test_out_of_range_delta_is_clamped_or_defaulted(sent, expect):
    """A finite over-ceiling value clamps (the host gets the most this worker
    will do); a value with no sane clamp -- negative, non-numeric -- falls back
    to the default. Either way the hold decodes."""
    engine = _CommandPrefixEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({
        "pcm_b64": _pcm_b64(), "beam_size": 4,
        "command_prefix": {"phrases": CATALOGUE, "delta": sent}})

    assert engine.calls[0]["command_prefix"]["delta"] == expect
    assert any("delta" in r for r in resp["stats"]["command_prefix_rejected"])


def test_the_command_prefix_stat_reaches_the_wire():
    """`stats.command_prefix` is how a device run tells "the constraint fired
    and released after 1 token" from "it was never armed". It must survive the
    reply assembly and the non-finite-float sweep."""

    class _StatEngine:
        def transcribe(self, audio, command_prefix=None, **_kw):
            return {"text": "note ThinkShield", "stats": {
                "chunks": 1, "tokens": 4,
                "command_prefix": {"phrases": 3, "max_tokens": 3,
                                   "mode": "soft", "delta": 6.0, "release": 1},
            }}

    asr_server._engine = _StatEngine()

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4,
                                     "command_prefix": {"phrases": CATALOGUE}})

    assert resp["stats"]["command_prefix"] == {
        "phrases": 3, "max_tokens": 3, "mode": "soft", "delta": 6.0, "release": 1}


def test_a_greedy_request_is_told_the_constraint_did_not_apply():
    """beam_size defaults to 1 on the wire. A host that arms the constraint on
    the greedy path must not be left reading a normal transcript and wondering
    why nothing changed."""

    class _GreedyEngine:
        def transcribe(self, audio, beam_size=1, command_prefix=None, **_kw):
            stats = {"chunks": 1, "tokens": 2, "path": "greedy"}
            if command_prefix and beam_size <= 1:
                stats["command_prefix"] = {"applied": False, "reason": "greedy"}
            return {"text": "no thinkshield", "stats": stats}

    asr_server._engine = _GreedyEngine()

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(),
                                     "command_prefix": {"phrases": CATALOGUE}})

    assert resp["stats"]["command_prefix"] == {"applied": False, "reason": "greedy"}


# --- learned vocabulary (LEARNING.md §6) ------------------------------------
#
# The host writes learning.json beside the user's bias_aliases.json; the worker
# reads it on every transcribe. Learned words lead the request's bias_phrases,
# learned aliases join the rewrite table under the user's own, and nothing
# about either reaches stderr but counts.


def _learning(folder, words=(), aliases=(), pending=()):
    """learning.json as the host writes it. A word is its text (learned from
    a correction) or ``(text, origin)``; ``pending`` pairs wait for their
    second dictation (LEARNING.md §4.2)."""
    words = [(w, "correction") if isinstance(w, str) else w for w in words]
    (folder / "learning.json").write_text(json.dumps({
        "schema": "dictation.learning/1",
        "words": [{"text": w, "origin": origin, "count": 1,
                   "first_seen": 1790000000000, "last_seen": 1790000000000} for w, origin in words],
        "aliases": [{"heard": h, "meant": m, "origin": "correction", "count": 2,
                     "first_seen": 1790000000000, "last_seen": 1790000000000} for h, m in aliases],
        "pending": [{"heard": h, "meant": m, "count": 1, "dictations": ["d1"],
                     "first_seen": 1790000000000, "last_seen": 1790000000000} for h, m in pending],
        "removed": {"words": [], "aliases": []},
    }), encoding="utf-8")


def _stream_prompt(monkeypatch, request):
    """Start through the public stream router and inspect its real session."""
    monkeypatch.setattr(ops_stream_whisper, "_get_engine", lambda: FakeEngine())
    monkeypatch.setattr(ops_stream_whisper, "_make_embed_fn", lambda: None)
    monkeypatch.setattr(ops_stream_whisper, "_live_diarization_info", lambda _session: {})
    reply = ops_stream.op_stream_start(request)
    assert reply["ok"] is True
    session = ops_stream_whisper._sessions[reply["stream_id"]]
    try:
        return session.initial_prompt
    finally:
        ops_stream_whisper.cancel({"stream_id": reply["stream_id"]})


class HybridEngine:
    _engine_name = "mlx"

    def __init__(self):
        self.calls = []

    def transcribe(self, audio, *, vocabulary_search=False, personalize=True, **kwargs):
        self.calls.append(dict(kwargs, vocabulary_search=vocabulary_search, personalize=personalize))
        return {"text": "open the repo", "stats": {}}


def test_final_dictation_search_reads_learned_words_and_preserves_fair_baseline(user_data):
    _learning(user_data, words=["repo"])
    engine = HybridEngine()
    asr_server._engine = engine

    response = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4,
                                        "compare_baseline": True})

    assert response["ok"]
    baseline, personalized = engine.calls
    assert baseline["vocabulary_search"] is personalized["vocabulary_search"] is True
    assert baseline["beam_size"] == personalized["beam_size"] == 2
    assert baseline["personalize"] is False
    assert not baseline.get("bias_phrases")
    assert personalized["bias_phrases"] == ["repo"]


def test_adaptive_file_strength_reaches_search_and_explicit_requests_keep_priority(user_data, monkeypatch):
    from transcription_worker.mlx_beam import VocabularyModel

    rows = [("Tentative", 1, 0), ("Confirmed", 3, 1), ("Established", 4, 0), ("Rejected", 2, 2)]
    _learning(user_data, words=[w for w, _, _ in rows] + [("Taught", "taught")])
    path = user_data / "learning.json"
    state = json.loads(path.read_text())
    for row, (_, count, opposition) in zip(state["words"], rows):
        row.update(adaptive=True, count=count, opposition=opposition)
    path.write_text(json.dumps(state))

    class Tokenizer:
        def encode(self, text):
            return [{"taught": 1, "established": 2, "confirmed": 3, "tentative": 4}[text.strip().lower()]]

    class Engine(HybridEngine):
        def transcribe(self, audio, *, vocabulary_search=False, phrase_deltas=None, **kwargs):
            result = super().transcribe(audio, vocabulary_search=vocabulary_search,
                                       phrase_deltas=phrase_deltas, **kwargs)
            if kwargs.get("bias_phrases"):
                self.trie = VocabularyModel(None, kwargs["bias_phrases"], 2, .5, {}, phrase_deltas).build_trie(Tokenizer())
            return result

    engine = Engine()
    asr_server._engine = engine
    response = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "compare_baseline": True})
    assert response["ok"]
    baseline, biased = engine.calls
    assert baseline["phrase_deltas"] is None
    assert biased["bias_phrases"] == ["Taught", "Established", "Confirmed", "Tentative"]
    assert biased["phrase_deltas"] == {0: .5, 1: .5, 2: .35, 3: .2}
    assert [engine.trie.delta_for_token((), i) for i in range(1, 5)] == [.5, .5, .35, .2]
    preview = _stream_prompt(monkeypatch, {"engine": "whisper", "client": "dictation"})
    assert "Rejected" not in preview and "Tentative" in preview

    engine.calls.clear()
    response = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["tentative"],
                                        "phrase_deltas": {"0": .8}})
    assert response["ok"]
    assert engine.calls[0]["phrase_deltas"][3] == .8
    assert engine.trie.delta_for_token((), 4) == .8


@pytest.mark.parametrize("fields,engine_name,words", [
    ({"vocabulary_search": False}, "mlx", ["repo"]),
    ({"client": "notes"}, "mlx", ["repo"]),
    ({"return_words": True}, "mlx", ["repo"]),
    ({}, "faster", ["repo"]),
    ({}, "mlx", []),
])
def test_search_activation_keeps_other_paths_and_empty_vocabulary_fast(user_data, fields, engine_name, words):
    _learning(user_data, words=words)
    engine = HybridEngine()
    engine._engine_name = engine_name
    asr_server._engine = engine
    response = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "beam_size": 4, **fields})
    assert response["ok"]
    assert engine.calls[0]["vocabulary_search"] is False
    assert engine.calls[0]["beam_size"] == 4


def test_dictation_stream_reads_learning_on_each_new_stream(user_data, monkeypatch):
    request = {"op": "stream_start", "engine": "whisper", "client": "dictation"}
    assert _stream_prompt(monkeypatch, request) is None

    _learning(user_data, words=["repo"], pending=[("sunshine", "Sunshine42x!")],
              aliases=[("true leo", "Trovioo")])
    assert _stream_prompt(monkeypatch, request) == "repo"

    (user_data / "learning.json").unlink()
    assert _stream_prompt(monkeypatch, request) is None


def test_dictation_stream_malformed_learning_is_fail_soft(user_data, monkeypatch):
    (user_data / "learning.json").write_text("not json", encoding="utf-8")
    request = {"op": "stream_start", "engine": "whisper", "client": "dictation",
               "bias_phrases": ["QuestPDF"]}
    assert _stream_prompt(monkeypatch, request) == "QuestPDF"


def test_dictation_stream_dedupes_without_mutating_request_and_notes_stay_unchanged(user_data, monkeypatch):
    _learning(user_data, words=["Repo"])
    request = {"op": "stream_start", "engine": "whisper", "client": "dictation",
               "bias_phrases": ["repo", "QuestPDF", "QuestPDF"]}
    original = json.loads(json.dumps(request))
    assert _stream_prompt(monkeypatch, request) == "Repo, QuestPDF"
    assert request == original

    notes = {"op": "stream_start", "engine": "whisper", "client": "notes",
             "bias_phrases": ["QuestPDF"]}
    assert _stream_prompt(monkeypatch, notes) == "QuestPDF"


def test_learned_words_lead_the_requests_phrases(user_data):
    _learning(user_data, words=["SkyDock", "Trovioo"])
    engine = FakeEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(),
                                     "bias_phrases": ["QuestPDF", "trovioo", "SKYDOCK", "QuestPDF"]})

    assert resp["ok"] is True
    # Learned first, then the host's; case-insensitive duplicates fold into
    # the first spelling seen, which is the learned one.
    assert engine.calls[0]["bias_phrases"] == ["SkyDock", "Trovioo", "QuestPDF"]
    assert resp["stats"]["learned"] == {"words": 2, "aliases": 0}


def test_learned_words_reach_a_request_that_sent_none(user_data):
    """Both hosts send no bias_phrases today (Windows passes none, the Mac's
    flag is off), so this is the shipped case: the learned words ARE the
    vocabulary."""
    _learning(user_data, words=["Trovioo"], aliases=[("trovio", "Trovioo")])
    engine = FakeEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})

    assert engine.calls[0]["bias_phrases"] == ["Trovioo"]
    assert resp["stats"]["learned"] == {"words": 1, "aliases": 1}


def test_without_a_learning_file_the_call_is_yesterdays(user_data):
    engine = FakeEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["Avrit Raj"]})
    asr_server.op_transcribe({"pcm_b64": _pcm_b64()})

    assert engine.calls[0]["bias_phrases"] == ["Avrit Raj"]
    assert engine.calls[1]["bias_phrases"] is None
    assert resp["stats"]["learned"] == {"words": 0, "aliases": 0}


def test_a_malformed_learning_file_changes_nothing(user_data, capsys):
    (user_data / "learning.json").write_text('{"schema": "dictation.learning/1", "words": [{"text": "Trovioo"',
                                            encoding="utf-8")
    engine = FakeEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["Avrit Raj"]})

    assert resp["ok"] is True
    assert engine.calls[0]["bias_phrases"] == ["Avrit Raj"]
    assert resp["stats"]["learned"] == {"words": 0, "aliases": 0}
    err = capsys.readouterr().err
    assert err.count("learned_vocabulary:") == 1 and "Trovioo" not in err


def test_a_failing_learned_read_never_costs_the_transcript(monkeypatch, capsys):
    from transcription_worker import learned_vocabulary

    def boom(_path):
        raise RuntimeError("Trovioo")

    monkeypatch.setattr(learned_vocabulary, "load", boom)
    asr_server._engine = FakeEngine()

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["Avrit Raj"]})

    assert resp["ok"] is True and resp["text"] == "call-1"
    assert resp["stats"]["learned"] == {"words": 0, "aliases": 0}
    assert "Trovioo" not in capsys.readouterr().err, "the exception text can carry vocabulary"


def test_phrase_deltas_keep_the_hosts_indices_behind_learned_words(user_data):
    """The host keys a delta by ITS index. Two learned words in front shift
    every merged slot by two, and a host phrase folded into a learned word
    must land on that word's slot."""
    _learning(user_data, words=["SkyDock", "Trovioo"])

    remapped, rejected = asr_server._remap_phrase_deltas(
        {"0": 2.0, "1": 3.0}, ["QuestPDF", "trovioo"],
        asr_server._merge_vocabulary(["SkyDock", "Trovioo"], ["QuestPDF", "trovioo"]))

    assert remapped == {2: 2.0, 1: 3.0}
    assert rejected == 0


def test_fired_reports_the_hosts_index_for_a_folded_phrase():
    merged = asr_server._merge_vocabulary(["SkyDock", "Trovioo"], ["QuestPDF", "trovioo"])
    fired = [{"phrase_id": 1, "pos": 3, "n": 1}, {"phrase_id": 0, "pos": 1, "n": 1},
             {"phrase_id": 2, "pos": 5, "n": 1}]

    wire = asr_server._wire_fired(fired, merged, ["QuestPDF", "trovioo"])

    assert [(hit["i"], hit["phrase"]) for hit in wire] == [(1, "Trovioo"), (None, "SkyDock"),
                                                           (0, "QuestPDF")]


def test_a_non_string_phrase_is_dropped_not_fatal(user_data):
    engine = FakeEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["Avrit Raj", 7, None],
                                     "phrase_deltas": {"1": 2.0}})

    assert resp["ok"] is True
    assert engine.calls[0]["bias_phrases"] == ["Avrit Raj"]
    assert resp["stats"]["phrase_deltas_rejected"] == 1


def test_learned_aliases_rewrite_and_the_users_win(user_data):
    _learning(user_data, aliases=[("trovio", "Trovioo"), ("javis", "Jarvis")])
    (user_data / "bias_aliases.json").write_text(json.dumps({"aliases": {"javis": "J.A.R.V.I.S."}}),
                                                 encoding="utf-8")
    asr_server._engine = _CasingEngine("ask javis about trovio")

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})

    assert resp["text"] == "ask J.A.R.V.I.S. about Trovioo"


def test_the_text_passes_log_counts_never_words(user_data, capsys):
    """The hosts copy worker stderr into their logs (the Mac for the whole
    session). A learned word is the user's vocabulary: the log may say that
    something was rewritten or re-cased, never what."""
    _learning(user_data, words=["QuestPDF"], aliases=[("trovio", "Trovioo")])
    asr_server._engine = _CasingEngine("questpdf and trovio")

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})

    assert resp["text"] == "QuestPDF and Trovioo"
    err = capsys.readouterr().err
    assert "aliases applied — 1 rewrite(s)" in err
    assert "casing restored — 1 word(s)" in err
    for word in ("QuestPDF", "questpdf", "Trovioo", "trovio"):
        assert word not in err, f"{word!r} reached stderr: {err}"


# --- the double alias pass: op_transcribe -> WhisperMac -> op_transcribe ------


def _speech_b64(seconds: float = 1.0) -> str:
    """Audible PCM: WhisperMac's silence pre-check drops anything quieter
    before the engine is called."""
    t = np.arange(int(16000 * seconds)) / 16000
    return base64.b64encode((9000 * np.sin(2 * np.pi * 220 * t)).astype(np.int16).tobytes()).decode("ascii")


def _faster_engine(monkeypatch, text: str):
    """A real WhisperMac on the faster branch, over a fake faster_whisper
    that returns ``text`` and records every call's options."""
    import types

    calls: list[dict] = []
    fake = types.ModuleType("faster_whisper")

    class WhisperModel:
        def __init__(self, *_args, **_kwargs):
            pass

        def transcribe(self, _audio, **options):
            calls.append(options)
            segment = types.SimpleNamespace(text=text)
            return iter([segment]), types.SimpleNamespace(language="en", language_probability=0.99)

    fake.WhisperModel = WhisperModel
    monkeypatch.setitem(sys.modules, "faster_whisper", fake)
    monkeypatch.setenv("DICTATION_ASR_ENGINE", "faster")
    from transcription_worker.whisper_mac import WhisperMac

    return WhisperMac(), calls


#: What the Windows host sends (AsrWorkerTranscriber): no vocabulary.
WINDOWS_FIELDS = {"client": "dictation", "text_passes": True, "beam_size": 4,
                  "hallucination_guard": False, "channel": "mic"}


def _counting_passes(monkeypatch):
    from transcription_worker import spoken_aliases

    passes = []
    real_apply = spoken_aliases.apply
    monkeypatch.setattr(spoken_aliases, "apply",
                        lambda text, *a, **k: passes.append(text) or real_apply(text, *a, **k))
    return passes


def test_the_alias_table_runs_once_per_decode(user_data, monkeypatch):
    """`flow -> Fable Flow` through the path it ships on: WhisperMac applies
    the table after decoding and says so, and op_transcribe's text passes do
    not apply it again. The flag is between the two and stays off the reply."""
    (user_data / "bias_aliases.json").write_text(json.dumps({"aliases": {"flow": "Fable Flow"}}),
                                                 encoding="utf-8")
    engine, _ = _faster_engine(monkeypatch, " I use flow")
    passes = _counting_passes(monkeypatch)
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _speech_b64(), **WINDOWS_FIELDS})

    assert resp["ok"] is True
    assert resp["text"] == "I use Fable Flow"
    assert passes == ["I use flow"]
    assert "spoken_aliases_applied" not in resp["stats"]


#: Both pass teaching's check (it compares an alias only with itself), and
#: `gpt` sits inside `GPT-4o` at a hyphen, which the matcher treats as a
#: boundary. A second pass turned "ask GPT-4o" into "ask ChatGPT-4o".
CROSS_ALIASES = [("gpt four oh", "GPT-4o"), ("gpt", "ChatGPT")]


@pytest.mark.parametrize("path", ["whisper_mac then server", "server only"])
def test_one_aliass_spoken_form_inside_anothers_written_form_is_rewritten_once(user_data, monkeypatch, path):
    """Once per decode on either engine: WhisperMac's pass alone, or the
    server's alone on an engine without one (the QNN engine's shape)."""
    _learning(user_data, aliases=CROSS_ALIASES)
    passes = _counting_passes(monkeypatch)

    resp = _dictate(monkeypatch, path, " ask gpt four oh, or just gpt")

    assert resp["text"] == "ask GPT-4o, or just ChatGPT"
    assert passes == ["ask gpt four oh, or just gpt"]


def test_a_learned_word_reaches_the_faster_prompt(user_data, monkeypatch):
    from transcription_worker.whisper_mac import MLX_SCRIPT_SEED

    _learning(user_data, words=["SkyDock", "Trovioo"])
    engine, calls = _faster_engine(monkeypatch, " we met skydock")
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _speech_b64(), **WINDOWS_FIELDS})

    # Most relevant (stored first) is written LAST, where the window keeps it.
    assert calls[0]["initial_prompt"] == MLX_SCRIPT_SEED + " Trovioo, SkyDock"
    # Declared casing of a learned word, on the CPU engine too.
    assert resp["text"] == "we met SkyDock"
    assert resp["stats"]["personalization"] == "L1-prompt: initial_prompt seeded (faster engine)"
    assert resp["stats"]["learned"] == {"words": 2, "aliases": 0}


def test_without_learned_words_the_faster_call_is_unchanged(user_data, monkeypatch):
    """No learning.json and no vocabulary on the wire: the engine call is the
    one this worker made before learned words existed, option for option."""
    engine, calls = _faster_engine(monkeypatch, " plain words")
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _speech_b64(), **WINDOWS_FIELDS})

    assert calls == [{"language": None, "beam_size": 4, "initial_prompt": None,
                      "condition_on_previous_text": False, "no_speech_threshold": 0.6,
                      "log_prob_threshold": -1.0}]
    assert resp["stats"]["personalization"].startswith("L0-only")



# --- a word learned from one correction restores case only (§4.1, §4.2) ------
#
# One fix of "restart the service now" to "restart the ServiceNow" learns the
# word ServiceNow at once and holds `service now -> ServiceNow` in pending
# until the same fix is seen on a second dictation. Declared casing also joins
# spaced forms and folds -ise/-ize, so without a limit the learned word did
# the pending alias's job from the first fix, in ordinary prose. Both casing
# passes are covered: WhisperMac's own (Windows and macOS both run it) and the
# server's (the only one on an engine without it).


def _server_only(_monkeypatch, text):
    """An engine with no text passes of its own (the QNN engine's shape)."""
    return _CasingEngine(text.strip())


CASING_PATHS = {"whisper_mac then server": _faster_engine, "server only": _server_only}


def _dictate(monkeypatch, path, said, **fields):
    engine = CASING_PATHS[path](monkeypatch, said)
    asr_server._engine = engine[0] if isinstance(engine, tuple) else engine
    return asr_server.op_transcribe({"pcm_b64": _speech_b64(), **WINDOWS_FIELDS, **fields})


@pytest.mark.parametrize("path", CASING_PATHS)
def test_a_word_learned_from_one_correction_restores_case_but_never_joins(user_data, monkeypatch, path):
    _learning(user_data, words=["ServiceNow"], pending=[("service now", "ServiceNow")])

    resp = _dictate(monkeypatch, path, " please call the service now, not the servicenow desk")

    assert resp["text"] == "please call the service now, not the ServiceNow desk"


@pytest.mark.parametrize("path", CASING_PATHS)
def test_a_learned_word_never_folds_the_other_locales_spelling(user_data, monkeypatch, path):
    _learning(user_data, words=["synchroniser"])

    resp = _dictate(monkeypatch, path, " the synchronizer is slow")

    assert resp["text"] == "the synchronizer is slow"


@pytest.mark.parametrize("path", CASING_PATHS)
def test_a_taught_word_keeps_the_join_and_the_fold(user_data, monkeypatch, path):
    """Teaching is the user declaring the term, which is what the join and
    the fold are priced against (declared_casing's safety argument)."""
    _learning(user_data, words=[("ServiceNow", "taught"), ("synchroniser", "taught")])

    resp = _dictate(monkeypatch, path, " call the service now about the synchronizer")

    assert resp["text"] == "call the ServiceNow about the synchroniser"


@pytest.mark.parametrize("path", CASING_PATHS)
def test_a_phrase_the_request_sends_keeps_the_join(user_data, monkeypatch, path):
    """The request declares its own bias_phrases for this utterance, even
    when the same word was also learned from a correction."""
    _learning(user_data, words=["ServiceNow"])

    resp = _dictate(monkeypatch, path, " call the service now", bias_phrases=["servicenow"])

    assert resp["text"] == "call the ServiceNow"


@pytest.mark.parametrize("path", CASING_PATHS)
def test_an_origin_this_version_does_not_know_restores_case_only(user_data, monkeypatch, path):
    """§5 keeps an unknown origin as it is; nothing says the user declared
    it, so it gets the narrower pass: a missed join, never a wrong one."""
    _learning(user_data, words=[("ServiceNow", "imported-later")])

    resp = _dictate(monkeypatch, path, " call the service now, not the servicenow desk")

    assert resp["text"] == "call the service now, not the ServiceNow desk"


def test_the_alternatives_get_the_same_case_only_rule(user_data):
    """`nbest[0].text == text`: a runner-up joined where the winner was not
    would read as a decode difference."""

    class _NbestEngine:
        def transcribe(self, audio, **_kw):
            said = "call the service now, not servicenow"
            return {"text": said, "stats": {"nbest": [{"text": said, "score": -0.1},
                                                      {"text": "call the service now", "score": -0.9}]}}

    _learning(user_data, words=["ServiceNow"])
    asr_server._engine = _NbestEngine()

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})

    assert resp["text"] == "call the service now, not ServiceNow"
    assert [hyp["text"] for hyp in resp["nbest"]] == [resp["text"], "call the service now"]


def test_the_case_only_set_reaches_the_engine_and_leaves_the_baseline(user_data):
    class _Recording:
        def __init__(self):
            self.calls = []

        def transcribe(self, audio, bias_phrases=None, case_only_phrases=None, personalize=True, **_kw):
            self.calls.append({"bias_phrases": bias_phrases, "case_only_phrases": case_only_phrases,
                               "personalize": personalize})
            return {"text": "x", "stats": {}}

    _learning(user_data, words=["ServiceNow", ("SkyDock", "taught"), "Trovioo"])
    engine = _Recording()
    asr_server._engine = engine

    asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "bias_phrases": ["trovioo"], "compare_baseline": True})

    baseline, personalised = engine.calls
    # Taught (SkyDock) and request-declared (Trovioo) words keep the full pass.
    assert personalised["case_only_phrases"] == frozenset({"servicenow"})
    assert baseline == {"bias_phrases": None, "case_only_phrases": None, "personalize": False}


def test_scene_reaches_final_decoder_and_preview_without_strength_or_alias_escalation(user_data, monkeypatch):
    _learning(user_data, words=["Noise", "Miles"])
    path = user_data / "learning.json"
    state = json.loads(path.read_text())
    for row in state["words"]:
        row.update(adaptive=True, count=1, opposition=0)
    state["evidence"] = {"version": 1, "observations": [{
        "id": "one", "at": 1790000000000, "weight": 1, "scene": "terminal",
        "corrections": [{"heard": "mies", "meant": "Miles", "word": True, "alias": False, "secret": False}]}]}
    path.write_text(json.dumps(state))
    class ContextEngine(HybridEngine):
        def transcribe(self, audio, *, phrase_deltas=None, **kwargs):
            return super().transcribe(audio, phrase_deltas=phrase_deltas, **kwargs)
    engine = ContextEngine()
    asr_server._engine = engine
    request = {"pcm_b64": _pcm_b64(), "client": "dictation", "vocabulary_scene": "terminal"}
    assert asr_server.op_transcribe(request)["ok"]
    assert engine.calls[-1]["bias_phrases"] == ["Miles", "Noise"]
    assert engine.calls[-1]["phrase_deltas"] == {0: .2, 1: .2}
    assert _stream_prompt(monkeypatch, {"engine": "whisper", "client": "dictation", "vocabulary_scene": "terminal"}) == "Miles, Noise"
    asr_server.op_transcribe(dict(request, vocabulary_scene="wechat"))
    assert engine.calls[-1]["bias_phrases"] == ["Noise", "Miles"]
    asr_server.op_transcribe(dict(request, client="notes"))
    assert engine.calls[-1]["bias_phrases"] == ["Noise", "Miles"]


# --- an empty decode says why, in one word; a recovery skips the gates ------
#
# The Mac's ending reads `dropped` (DictationAsrDropReason) to say why no
# words came back and whether to keep the take for Recover Last Dictation.
# Mutations: tests/test_guards_can_fire.py drives the same contract through
# the staged worker.


class DroppingEngine:
    """Answers with the text and `dropped_reason` the test sets, and records
    whether a call was a recovery."""

    def __init__(self, text="", dropped_reason=None):
        self.text = text
        self.dropped_reason = dropped_reason
        self.calls: list[dict] = []

    def transcribe(self, audio, bias_phrases=None, beam_size=1, p_you=None, recovery=False, **_kw):
        self.calls.append({"recovery": recovery})
        return {"text": self.text, "stats": {"chunks": 1, "tokens": 0},
                "dropped_reason": self.dropped_reason}


@pytest.mark.parametrize("reason, word", [
    ("silence-precheck", "quiet"),
    ("language-prob-gate", "language"),
    ("language-prob-gate+avg-logprob", "language"),
    ("junk-filter", "junk"),
    ("hallucination-phrase:'Thank you for watching.'", "other"),
    (None, "other"),
])
def test_an_empty_decode_says_why_in_one_word(reason, word):
    asr_server._engine = DroppingEngine(dropped_reason=reason)

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})

    assert resp["ok"] is True and resp["text"] == ""
    assert resp["dropped"] == word
    # The log's detail is unchanged; the word never carries the decode.
    assert resp["dropped_reason"] == reason
    assert "Thank" not in resp["dropped"]


def test_a_decode_with_words_keeps_yesterdays_reply_keys():
    asr_server._engine = DroppingEngine(text="hello there")

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64()})

    assert resp["text"] == "hello there"
    assert "dropped" not in resp


@pytest.mark.parametrize("sent, forwarded", [(True, True), (None, False), (False, False), ("yes", False), (1, False)])
def test_only_a_literal_recovery_request_reaches_the_engine(sent, forwarded):
    engine = DroppingEngine(text="kept words")
    asr_server._engine = engine
    request = {"pcm_b64": _pcm_b64()}
    if sent is not None:
        request["recovery"] = sent

    assert asr_server.op_transcribe(request)["ok"] is True

    assert engine.calls[-1]["recovery"] is forwarded


def test_an_engine_without_recovery_still_decodes_a_recovery():
    engine = FakeEngine()
    asr_server._engine = engine

    resp = asr_server.op_transcribe({"pcm_b64": _pcm_b64(), "recovery": True})

    assert resp["ok"] is True and resp["text"] == "call-1"
