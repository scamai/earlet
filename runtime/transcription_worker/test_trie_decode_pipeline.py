"""Regression coverage for the product wiring around the measured bias trie.

These tests deliberately do not judge trie quality.  Research owns the score
and vocabulary experiments; this file proves the product does not bypass
OpenAI Whisper's long-form orchestration when it installs that decoder.
"""

from __future__ import annotations

from types import SimpleNamespace

import numpy as np
import pytest

# The monorepo's lightweight, platform-neutral Python lane intentionally does
# not install torch/OpenAI Whisper.  This is a macOS runtime integration test:
# skip at collection there, and run against the bundled Mac runtime below.
torch = pytest.importorskip("torch")
whisper = pytest.importorskip("whisper")

from transcription_worker import trie_decode
from transcription_worker import whisper_mac


class _BaseModel:
    device = torch.device("cpu")
    marker = "delegated"

    def __init__(self):
        self.decode_calls = []

    def decode(self, mel, options):
        self.decode_calls.append((mel, options))
        return "plain"


def test_long_audio_enters_upstream_transcribe_untrimmed(monkeypatch):
    """A >30 s clip must reach Whisper's window loop, never pad_or_trim."""
    audio = np.arange(65 * 16_000, dtype=np.float32)
    model = _BaseModel()
    seen = {}

    def fake_transcribe(wrapped_model, received_audio, **kwargs):
        seen["model"] = wrapped_model
        seen["audio"] = received_audio
        seen["kwargs"] = kwargs
        return {"text": "first window second window", "language": "en"}

    monkeypatch.setattr(whisper, "transcribe", fake_transcribe)

    text, language, phrase_count, segments = trie_decode.transcribe_with_trie(
        model, audio, bias_phrases=["Trovioo"], beam_size=4)

    assert seen["audio"] is audio
    assert seen["model"] is not model
    assert seen["model"].marker == "delegated"
    assert seen["kwargs"]["beam_size"] == 4
    assert seen["kwargs"]["sample_len"] == 128
    assert seen["kwargs"]["without_timestamps"] is True
    assert (text, language, phrase_count) == (
        "first window second window", "en", 1)
    assert segments == []


def test_zero_temperature_beam_uses_the_bias_decoder(monkeypatch):
    created_tasks = []

    class FakeTokenizer:
        eot = 50_000

        def encode(self, text):
            return [ord(char) for char in text]

    class FakeTask:
        def __init__(self, model, options):
            self.model = model
            self.options = options
            self.tokenizer = FakeTokenizer()
            self.inference = object()
            self.decoder = object()
            self.sequence_ranker = None
            created_tasks.append(self)

        def run(self, mel):
            self.mel = mel
            return ["biased"]

    class FakeBiasDecoder:
        def __init__(self, *args, bias=None, **kwargs):
            self.bias = bias

    monkeypatch.setattr(trie_decode, "DecodingTask", FakeTask)
    monkeypatch.setattr(trie_decode, "BiasBeamSearchDecoder", FakeBiasDecoder)
    monkeypatch.setattr(
        trie_decode, "MaximumLikelihoodRanker", lambda length_penalty: ("ranker", length_penalty))

    model = _BaseModel()
    wrapped = trie_decode.TrieTranscribeModel(model, ["Trovioo"])
    options = SimpleNamespace(
        beam_size=4, temperature=0.0, patience=None, length_penalty=None)

    result = wrapped.decode(torch.zeros((80, 3000)), options)

    assert result == "biased"
    assert model.decode_calls == []
    assert len(created_tasks) == 1
    assert created_tasks[0].decoder.bias is not None


def _bias_from_decode(monkeypatch, **model_kwargs):
    """Run one zero-temperature decode and hand back the trie it installed."""
    created_tasks = []

    class FakeTokenizer:
        eot = 50_000

        def encode(self, text):
            return [ord(char) for char in text]

    class FakeTask:
        def __init__(self, model, options):
            self.tokenizer = FakeTokenizer()
            self.inference = object()
            self.decoder = object()
            self.sequence_ranker = None
            created_tasks.append(self)

        def run(self, mel):
            return ["biased"]

    class FakeBiasDecoder:
        def __init__(self, *args, bias=None, **kwargs):
            self.bias = bias

    monkeypatch.setattr(trie_decode, "DecodingTask", FakeTask)
    monkeypatch.setattr(trie_decode, "BiasBeamSearchDecoder", FakeBiasDecoder)
    monkeypatch.setattr(
        trie_decode, "MaximumLikelihoodRanker", lambda length_penalty: ("ranker", length_penalty))

    wrapped = trie_decode.TrieTranscribeModel(
        _BaseModel(), ["Cluely", "iTerm2"], **model_kwargs)
    wrapped.decode(
        torch.zeros((80, 3000)),
        SimpleNamespace(beam_size=4, temperature=0.0, patience=None, length_penalty=None))
    return created_tasks[0].decoder.bias


def test_absent_per_term_deltas_leave_the_uniform_path_untouched(monkeypatch):
    """The acceptance gate for per-term δ, and it is an identity not a score.

    Internal research finding 24's first implementation boosted a token by a
    per-term δ while `BiasState.advance` still accrued the trie's scalar into
    `pending`, so a partial match that died refunded less than it had been
    charged and kept score it never earned. It cost 3.2 points and surfaced
    only because the uniform control stopped reproducing the shipped decoder.
    So the product-side test is "does the uniform configuration still produce
    the uniform decoder", never "did recovery improve".
    """
    trie = _bias_from_decode(monkeypatch)

    assert trie.phrase_deltas == {}, "no per-term map must reach the trie"
    assert trie._node_delta == {}, "the δ cache must stay unbuilt"
    # `delta_for_token` short-circuits on an empty cache, so every token is
    # charged the scalar exactly as it was before per-term δ existed.
    assert trie.delta_for_token((), ord("C")) == trie.delta


def test_per_term_deltas_reach_the_trie_through_the_product_path(monkeypatch):
    """The wiring this module exists to prove: research's knob is reachable.

    `BiasTrie.set_phrase_deltas` has been implemented and measured for some
    time; what did not exist was any way for the product to populate it —
    `bias_phrases` crossed the wire as a flat string array with nowhere to put
    a weight. Threading it is the whole of the change, so a test that the
    value actually arrives is the whole of the coverage.
    """
    trie = _bias_from_decode(monkeypatch, phrase_deltas={0: 2.5, 1: 0.0})

    assert trie.phrase_deltas == {0: 2.5, 1: 0.0}
    assert trie._node_delta, "the δ cache must be built once a map is present"


def test_per_term_deltas_are_coerced_to_the_types_the_trie_indexes_by(monkeypatch):
    """JSON round-trips object keys as strings; the trie indexes by int.

    The map arrives from the app over the same JSON transport as
    `bias_phrases`, so an entry keyed `"0"` would silently never match phrase
    0 and the term would fall back to the scalar — a per-term δ that reads as
    configured and does nothing.
    """
    trie = _bias_from_decode(monkeypatch, phrase_deltas={"0": "2.5", "1": 0})

    assert trie.phrase_deltas == {0: 2.5, 1: 0.0}


def _decode_twice(monkeypatch, *, same_tokenizer: bool):
    """Two zero-temperature decodes on one model; returns the two tries.

    `same_tokenizer` models the two worlds this wiring has to be right in.
    Upstream's `get_tokenizer` is `lru_cache`d, so a real decode asks for the
    same object every window — that is the hit. A harness that hands out a
    fresh one every call is the miss, and the miss must still be correct.
    """
    tries = []
    shared = _CachedTokenizer()

    class FakeTask:
        def __init__(self, model, options):
            self.tokenizer = shared if same_tokenizer else _CachedTokenizer()
            self.inference = object()
            self.decoder = object()
            self.sequence_ranker = None

        def run(self, mel):
            return ["biased"]

    class FakeBiasDecoder:
        def __init__(self, *args, bias=None, **kwargs):
            tries.append(bias)

    monkeypatch.setattr(trie_decode, "DecodingTask", FakeTask)
    monkeypatch.setattr(trie_decode, "BiasBeamSearchDecoder", FakeBiasDecoder)
    monkeypatch.setattr(
        trie_decode, "MaximumLikelihoodRanker", lambda length_penalty: ("ranker", length_penalty))

    wrapped = trie_decode.TrieTranscribeModel(_BaseModel(), ["Cluely", "iTerm2"])
    options = SimpleNamespace(
        beam_size=4, temperature=0.0, patience=None, length_penalty=None)
    wrapped.decode(torch.zeros((80, 3000)), options)
    wrapped.decode(torch.zeros((80, 3000)), options)
    return tries


class _CachedTokenizer:
    eot = 50_000

    def __init__(self):
        self.calls = 0

    def encode(self, text):
        self.calls += 1
        return [ord(char) for char in text]


def test_long_audio_builds_the_trie_once_not_once_per_window(monkeypatch):
    """The defect: `decode` is upstream's per-30s-window seam.

    A two-minute dictation entered this method four times and rebuilt an
    identical tree each time, re-tokenising all four variants of every phrase.
    The tree is a pure function of its inputs, so those were four identical
    objects. Identity is the assertion because an equal-but-rebuilt tree means
    the cost is still being paid.
    """
    first, second = _decode_twice(monkeypatch, same_tokenizer=True)

    assert first is second, "the second window rebuilt the trie"
    assert first is not None


def test_the_second_window_does_not_re_tokenize(monkeypatch):
    """Where the cost actually sat — one `encode` per phrase variant."""
    tries = []
    tokenizer = _CachedTokenizer()

    class FakeTask:
        def __init__(self, model, options):
            self.tokenizer = tokenizer
            self.inference = object()
            self.decoder = object()
            self.sequence_ranker = None

        def run(self, mel):
            return ["biased"]

    monkeypatch.setattr(trie_decode, "DecodingTask", FakeTask)
    monkeypatch.setattr(
        trie_decode, "BiasBeamSearchDecoder",
        lambda *a, bias=None, **k: tries.append(bias) or SimpleNamespace(bias=bias))
    monkeypatch.setattr(
        trie_decode, "MaximumLikelihoodRanker", lambda length_penalty: None)

    wrapped = trie_decode.TrieTranscribeModel(_BaseModel(), ["Cluely", "iTerm2"])
    options = SimpleNamespace(
        beam_size=4, temperature=0.0, patience=None, length_penalty=None)
    wrapped.decode(torch.zeros((80, 3000)), options)
    after_first = tokenizer.calls
    wrapped.decode(torch.zeros((80, 3000)), options)

    assert after_first > 0, "the first window must actually build something"
    assert tokenizer.calls == after_first, "the second window re-tokenized"


def test_a_different_tokenizer_still_rebuilds(monkeypatch):
    """The miss must be a rebuild, never a stale tree from another model."""
    first, second = _decode_twice(monkeypatch, same_tokenizer=False)

    assert first is not second


def test_temperature_fallback_keeps_upstream_sampling_decoder():
    """Trie research is beam-only; sampling fallback must remain upstream."""
    model = _BaseModel()
    wrapped = trie_decode.TrieTranscribeModel(model, ["Trovioo"])
    options = SimpleNamespace(
        beam_size=None, temperature=0.2, patience=None, length_penalty=None)
    mel = torch.zeros((80, 3000))

    assert wrapped.decode(mel, options) == "plain"
    assert model.decode_calls == [(mel, options)]


def test_product_stats_and_log_report_long_audio_windows(monkeypatch, capsys):
    """The success line must prove long-form coverage, not merely trie entry."""
    engine = object.__new__(whisper_mac.WhisperMac)
    engine._engine_name = "openai"
    engine._torch_model = object()
    audio = np.full(65 * whisper_mac.SAMPLE_RATE, 0.1, dtype=np.float32)

    monkeypatch.setattr(
        trie_decode,
        "transcribe_with_trie",
        lambda *args, **kwargs: (
            "complete long transcript", "en", 32,
            [{"start": 0.0, "end": 30.0, "tokens": list(range(80))}],
        ),
    )

    result = engine.transcribe(
        audio, bias_phrases=[f"phrase-{index}" for index in range(32)])

    assert result["stats"]["chunks"] == 3
    assert result["stats"]["bias"] == "trie:32:windows=3:chunks=3"
    assert "trie decode ok — 32 phrases across 3 windows" in capsys.readouterr().err


def test_trie_undercoverage_detects_early_eot_on_voiced_window():
    """A successful return that skips voiced audio is still a failed decode."""
    audio = np.full(30 * whisper_mac.SAMPLE_RATE, 0.1, dtype=np.float32)
    segments = [{
        "start": 0.0,
        "end": 30.0,
        "tokens": list(range(14)),
    }]

    gap = whisper_mac.trie_coverage_gap(audio, segments)

    assert gap == {"window": 1, "tokens": 14, "voiced_seconds": 30.0}


def test_trie_undercoverage_does_not_penalize_a_short_utterance():
    audio = np.concatenate([
        np.full(5 * whisper_mac.SAMPLE_RATE, 0.1, dtype=np.float32),
        np.zeros(25 * whisper_mac.SAMPLE_RATE, dtype=np.float32),
    ])
    segments = [{
        "start": 0.0,
        "end": 30.0,
        "tokens": [1, 2],
    }]

    assert whisper_mac.trie_coverage_gap(audio, segments) is None


def test_auto_language_spans_split_at_each_qualifying_natural_pause():
    """Mixed-language dictation gets a fresh language decision per speech run."""
    sample_rate = whisper_mac.SAMPLE_RATE
    audio = np.full(48 * sample_rate, 0.1, dtype=np.float32)
    # Every qualifying pause starts a new language-decision chunk.  This
    # replaces the old "latest pause wins" behavior, which could leave a
    # bilingual run before that latest pause.
    for start, end in [(11.5, 12.3), (22.0, 23.0), (40.3, 41.2)]:
        audio[int(start * sample_rate):int(end * sample_rate)] = 0.0

    spans = whisper_mac.auto_language_decode_spans(audio)

    cuts = [end / sample_rate for _, end in spans[:-1]]
    assert cuts[0] == pytest.approx(11.9, abs=whisper_mac.FRAME_SECONDS)
    assert cuts[1] == pytest.approx(22.5, abs=whisper_mac.FRAME_SECONDS)
    assert cuts[2] == pytest.approx(40.75, abs=whisper_mac.FRAME_SECONDS)
    assert spans[0][0] == 0
    assert spans[-1][1] == len(audio)


def test_auto_language_spans_split_field_shape_at_both_pauses():
    """A 28.686 s English-to-Chinese recording must not retain a mixed run."""
    sample_rate = whisper_mac.SAMPLE_RATE
    audio = np.full(int(28.686 * sample_rate), 0.1, dtype=np.float32)
    for start, end in [(8.71, 9.68), (16.05, 17.01)]:
        audio[int(start * sample_rate):int(end * sample_rate)] = 0.0

    spans = whisper_mac.auto_language_decode_spans(audio)

    cuts = [end / sample_rate for _, end in spans[:-1]]
    assert cuts == pytest.approx([9.195, 16.53], abs=whisper_mac.FRAME_SECONDS)
    assert all(left[1] == right[0] for left, right in zip(spans, spans[1:]))
    assert spans[0][0] == 0
    assert spans[-1][1] == len(audio)


def test_auto_language_spans_leave_short_audio_whole():
    audio = np.full(20 * whisper_mac.SAMPLE_RATE, 0.1, dtype=np.float32)

    assert whisper_mac.auto_language_decode_spans(audio) == [(0, len(audio))]


def test_auto_language_spans_cover_continuous_speech_without_tiny_tail():
    sample_rate = whisper_mac.SAMPLE_RATE
    audio = np.full(50 * sample_rate, 0.1, dtype=np.float32)

    spans = whisper_mac.auto_language_decode_spans(audio)

    assert spans == [
        (0, 24 * sample_rate),
        (24 * sample_rate, 44 * sample_rate),
        (44 * sample_rate, 50 * sample_rate),
    ]
    assert all(left[1] == right[0] for left, right in zip(spans, spans[1:]))


def test_product_restarts_auto_language_at_each_speech_chunk(monkeypatch):
    engine = object.__new__(whisper_mac.WhisperMac)
    engine._engine_name = "openai"
    engine._torch_model = object()
    sample_rate = whisper_mac.SAMPLE_RATE
    audio = np.full(48 * sample_rate, 0.1, dtype=np.float32)
    for start, end in [(22.0, 23.0), (40.3, 41.2)]:
        audio[int(start * sample_rate):int(end * sample_rate)] = 0.0
    calls = []

    def fake_trie(model, samples, **kwargs):
        calls.append((len(samples), kwargs.get("language")))
        index = len(calls)
        language = "en" if index == 1 else "zh"
        return (
            f"part-{index}", language, 32,
            [{"start": 0.0, "end": len(samples) / sample_rate,
              "tokens": list(range(100))}],
        )

    monkeypatch.setattr(trie_decode, "transcribe_with_trie", fake_trie)

    result = engine.transcribe(
        audio, bias_phrases=[f"phrase-{index}" for index in range(32)])

    assert [language for _, language in calls] == [None, None, None]
    assert result["text"] == "part-1 part-2 part-3"
    assert result["stats"]["language"] == "mul"
    assert result["stats"]["bias"] == "trie:32:windows=2:chunks=3"


def test_product_falls_back_when_trie_silently_under_covers(monkeypatch, capsys):
    """The measured field failure: trie returns, but most speech disappeared.

    The audio is 20 s rather than 30 s so that it decodes as exactly ONE
    chunk, which keeps this test about the coverage fallback and nothing else.
    It used to be 30 s and rely on `language="en"` forcing a single
    whole-utterance decode — a carve-out removed on 2026-08-17 because it
    silently destroyed speech (a collapsed 30 s window forfeited 23.5 s of the
    an audio evaluation). Continuous speech now splits at the 24 s bound
    regardless of language, so a 30 s fixture would produce two chunks and two
    copies of the fallback text, testing chunk concatenation instead of
    coverage.
    """
    engine = object.__new__(whisper_mac.WhisperMac)
    engine._engine_name = "openai"
    engine._torch_model = SimpleNamespace(
        transcribe=lambda audio, language=None: {
            "text": "complete unbiased transcript",
            "language": "en",
        })
    audio = np.full(20 * whisper_mac.SAMPLE_RATE, 0.1, dtype=np.float32)

    monkeypatch.setattr(
        trie_decode,
        "transcribe_with_trie",
        lambda *args, **kwargs: (
            "short biased text", "en", 32,
            [{"start": 0.0, "end": 20.0, "tokens": list(range(14))}],
        ),
    )

    result = engine.transcribe(
        audio, bias_phrases=[f"phrase-{index}" for index in range(32)],
        language="en")

    assert result["text"] == "complete unbiased transcript"
    assert result["stats"]["bias"] == "trie-coverage-fallback:window=1"
    assert "trie decode under-covered, falling back" in capsys.readouterr().err


def test_a_pinned_language_still_gets_chunked(monkeypatch):
    """The carve-out that destroyed speech must not come back.

    Pinning a language used to hand the whole utterance to the decoder in ONE
    call, and on the trie path that degenerates. Measured on the owner's own
    47.9 s clip: `language="en"` returned 224 characters against 416 for auto,
    with the entire opening paragraph gone; after chunking, 567 with nothing
    dropped.

    The MECHANISM is deliberately not asserted here — see the comment at the
    `decode_spans` line. Two candidate explanations (whisper's 30 s read-head
    advance, and `condition_on_previous_text`) were both tested and neither
    holds. What is established is the outcome, and this test guards the
    outcome's precondition: the decoder must never receive a long utterance
    as a single span.

    This asserts the mechanism rather than the transcript: a long pinned-
    language decode must reach the decoder as MORE THAN ONE span.
    """
    engine = object.__new__(whisper_mac.WhisperMac)
    engine._engine_name = "openai"
    engine._torch_model = SimpleNamespace(
        transcribe=lambda audio, language=None: {"text": "x", "language": "en"})

    seen_spans = []

    def record(model, chunk, **kwargs):
        seen_spans.append(len(chunk))
        return ("chunk text", "en", 1, [{"start": 0.0,
                                         "end": len(chunk) / whisper_mac.SAMPLE_RATE,
                                         "tokens": list(range(400))}])

    monkeypatch.setattr(trie_decode, "transcribe_with_trie_repaired", record)
    monkeypatch.setattr(trie_decode, "transcribe_with_trie", record)

    # 50 s of continuous speech, language pinned — the exact shape that lost
    # 23.5 seconds in the field.
    audio = np.full(50 * whisper_mac.SAMPLE_RATE, 0.1, dtype=np.float32)
    engine.transcribe(audio, bias_phrases=["Trovioo"], language="en")

    assert len(seen_spans) > 1, (
        "a pinned language went to the decoder as one whole-utterance span; "
        "one collapsed 30 s window would forfeit everything after it")
    assert max(seen_spans) <= whisper_mac.AUTO_LANGUAGE_MAX_CHUNK_SECONDS * \
        whisper_mac.SAMPLE_RATE + 1, "a span exceeded the 24 s bound"
