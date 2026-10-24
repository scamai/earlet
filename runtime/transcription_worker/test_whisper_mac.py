"""Tests for the Apple-silicon backend that run WITHOUT any model or
Snapdragon dependency — the Mac counterpart of test_junk_filter.py, whose
`from transcription_worker.whisper_qnn import …` cannot even be collected
off-QNN (module-top `import onnxruntime_qnn`).

Covers: the duplicated junk filter (must stay in sync with whisper_qnn's —
same cases as test_junk_filter.py), StubEngine's engine-surface contract
(everything asr_server.op_init/op_transcribe touches), and asr_server's
backend dispatch using the stub.

Run: python -m pytest transcription_worker/test_whisper_mac.py
"""

from __future__ import annotations

import numpy as np
import pytest

from transcription_worker.whisper_mac import StubEngine, looks_like_junk


# --- junk filter: same cases as test_junk_filter.py (keep in sync) ---------

def test_pure_annotation_is_junk():
    assert looks_like_junk("(whisper)")
    assert looks_like_junk("[BLANK_AUDIO]")
    assert looks_like_junk("♪♪♪")


def test_annotation_mixed_with_speech_is_kept():
    assert not looks_like_junk("(coughs) so the quarterly numbers look fine")


def test_single_word_repetition_is_junk():
    assert looks_like_junk("the the the the the the the the")


def test_hypercompressible_loop_is_junk():
    assert looks_like_junk("okay so okay so okay so okay so okay so okay so "
                           "okay so okay so okay so okay so")


def test_normal_speech_is_kept():
    assert not looks_like_junk("let's move the launch to Thursday and tell Avrit")


def test_empty_is_junk():
    assert looks_like_junk("")
    assert looks_like_junk("...")


# --- StubEngine: the exact surface asr_server binds against ----------------

def test_stub_engine_surface():
    e = StubEngine()
    assert e.on_npu is False
    assert e.encoder.get_providers() == ["StubEngine"]
    assert e.p_ilm is None


def test_stub_transcribe_contract():
    e = StubEngine()
    audio = (0.1 * np.sin(np.linspace(0, 400 * np.pi, 16000))).astype(np.float32)
    r = e.transcribe(audio, bias_phrases=["Fable"], beam_size=4, p_you=object())
    assert set(r) == {"text", "stats"}
    assert r["text"] == "stub transcript"
    assert r["stats"]["engine"] == "stub"


def test_stub_silence_yields_empty_text():
    e = StubEngine()
    r = e.transcribe(np.zeros(16000, dtype=np.float32))
    assert r["text"] == ""


# --- asr_server dispatch with the stub backend -----------------------------

def test_asr_server_init_and_transcribe_via_stub():
    import base64

    from transcription_worker import asr_server

    init = asr_server.op_init({"op": "init", "backend": "stub"})
    assert init["ok"] and init["backend"] == "stub"
    assert init["encoder_providers"] == ["StubEngine"]

    pcm = (np.full(16000, 3000, dtype=np.int16)).tobytes()
    r = asr_server.op_transcribe({
        "op": "transcribe", "pcm_b64": base64.b64encode(pcm).decode(),
        "sample_rate": 16000, "channel": "system", "utc_start": 123,
    })
    assert r["ok"] and r["text"] == "stub transcript"
    assert r["channel"] == "system" and r["utc_start"] == 123
    assert r["baseline_text"] is None


def test_asr_server_rejects_unknown_backend():
    from transcription_worker import asr_server

    r = asr_server.op_init({"op": "init", "backend": "hexagon2"})
    assert r["ok"] is False and "hexagon2" in r["error"]




# --- detected-language metadata (no model: a fake mlx_whisper module) ------
#
# The dictation path runs language=None (auto-detect) and the Swift host
# surfaces "what did the last dictation come out as?" from
# stats["language"]. These tests pin that plumbing with a stand-in
# mlx_whisper module and a local (empty-weights) repo dir, so they run with
# zero downloads and no mlx install — same spirit as the StubEngine tests.

def _fake_mlx_module(result):
    import types

    mod = types.ModuleType("mlx_whisper")
    mod.calls = []

    def transcribe(audio, **kwargs):
        mod.calls.append(kwargs)
        return dict(result)

    mod.transcribe = transcribe
    return mod


def _local_mlx_repo(tmp_path):
    d = tmp_path / "fake-mlx-repo"
    d.mkdir()
    (d / "config.json").write_text("{}")
    (d / "weights.npz").write_text("")
    return str(d)


def test_faster_whisper_receives_live_caption_initial_prompt(monkeypatch):
    """Pin the exact x64 regression boundary without loading model weights."""
    import sys
    import types

    calls = []
    fake = types.ModuleType("faster_whisper")

    class FakeWhisperModel:
        def __init__(self, *_args, **_kwargs):
            pass

        def transcribe(self, _audio, **kwargs):
            calls.append(kwargs)
            segment = types.SimpleNamespace(text="prompted transcript")
            info = types.SimpleNamespace(language="en", language_probability=0.99)
            return iter([segment]), info

    fake.WhisperModel = FakeWhisperModel
    monkeypatch.setitem(sys.modules, "faster_whisper", fake)
    monkeypatch.setenv("DICTATION_ASR_ENGINE", "faster")

    from transcription_worker.whisper_mac import WhisperMac

    engine = WhisperMac()
    result = engine.transcribe(
        np.full(1600, 0.1, dtype=np.float32),
        initial_prompt="Fable, Snapdragon",
    )

    assert result["text"] == "prompted transcript"
    assert calls[0]["initial_prompt"] == "Fable, Snapdragon"


def test_mlx_detected_language_lands_in_stats(tmp_path, monkeypatch):
    import sys

    fake = _fake_mlx_module({
        "text": "你好",
        "segments": [{"avg_logprob": -0.2}],
        "language": "zh",
    })
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)
    from transcription_worker.whisper_mac import WhisperMac

    engine = WhisperMac(model_dir=_local_mlx_repo(tmp_path))
    r = engine.transcribe(np.full(1600, 0.1, dtype=np.float32))
    assert r["stats"]["language"] == "zh"
    # mlx_whisper.transcribe keeps only the argmax; no probability today.
    assert r["stats"]["language_probability"] is None
    # The dictation default: nothing pinned, auto-detect.
    assert fake.calls[0]["language"] is None


def test_mlx_pinned_language_is_forwarded_and_reported(tmp_path, monkeypatch):
    import sys

    fake = _fake_mlx_module({"text": "hello there", "segments": [],
                             "language": "en"})
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)
    from transcription_worker.whisper_mac import WhisperMac

    engine = WhisperMac(model_dir=_local_mlx_repo(tmp_path))
    r = engine.transcribe(
        np.full(1600, 0.1, dtype=np.float32), language="en")
    assert fake.calls[0]["language"] == "en"
    assert r["stats"]["language"] == "en"


def test_stub_stats_carry_no_language_key():
    # Absence is a valid state of the stats contract ("unknown", never a
    # detection) — consumers must tolerate it, so the stub deliberately
    # does not fabricate one.
    e = StubEngine()
    r = e.transcribe(np.full(16000, 0.1, dtype=np.float32))
    assert "language" not in r["stats"]


class TestDropNoSpeechSegments:
    """Guard 2 of the 2026-08-09 anti-hallucination pair: BOTH conditions
    must hold before a segment dies — quiet-but-real speech and hard accents
    each trip one condition alone and must survive."""

    def _seg(self, text, no_speech=None, logprob=None):
        seg = {"text": text}
        if no_speech is not None:
            seg["no_speech_prob"] = no_speech
        if logprob is not None:
            seg["avg_logprob"] = logprob
        return seg

    def test_hallucination_shape_dropped(self):
        from transcription_worker.whisper_mac import drop_no_speech_segments
        segs = [self._seg("Thank you.", no_speech=0.9, logprob=-1.5)]
        assert drop_no_speech_segments(segs) == []

    def test_quiet_but_real_speech_kept(self):
        from transcription_worker.whisper_mac import drop_no_speech_segments
        segs = [self._seg("move the launch", no_speech=0.9, logprob=-0.3)]
        assert drop_no_speech_segments(segs) == segs

    def test_hard_accent_kept(self):
        from transcription_worker.whisper_mac import drop_no_speech_segments
        segs = [self._seg("move the launch", no_speech=0.1, logprob=-1.8)]
        assert drop_no_speech_segments(segs) == segs

    def test_missing_fields_kept(self):
        from transcription_worker.whisper_mac import drop_no_speech_segments
        segs = [self._seg("plain"), self._seg("half", no_speech=0.9)]
        assert drop_no_speech_segments(segs) == segs

    def test_mixed_keeps_order(self):
        from transcription_worker.whisper_mac import drop_no_speech_segments
        good = self._seg("real words", no_speech=0.2, logprob=-0.4)
        junk = self._seg("Hay subscribe", no_speech=0.95, logprob=-2.0)
        good2 = self._seg("more real words", no_speech=0.3, logprob=-0.5)
        assert drop_no_speech_segments([good, junk, good2]) == [good, good2]

    def test_thresholds_are_strict_boundaries(self):
        from transcription_worker.whisper_mac import drop_no_speech_segments
        boundary = self._seg("edge", no_speech=0.6, logprob=-1.0)
        assert drop_no_speech_segments([boundary]) == [boundary]


# --- the vocabulary prompt: what survives the 223-token window -------------
#
# `_vocabulary_prompt` documents its contract as "callers pass the vocabulary
# in RELEVANCE ORDER, least relevant first, because the tail is what survives
# truncation". These tests pin that behaviour so the two ends of the wire can
# be checked against each other: the Swift side currently writes and sends the
# vocabulary with the most valuable terms FIRST, which is the end this drops.


def _terms(n, prefix="Term"):
    """`n` distinct terms of a realistic shape (7-9 chars, Latin, no spaces)."""
    return [f"{prefix}{i:04d}" for i in range(n)]


def test_a_vocabulary_that_fits_keeps_every_term():
    from transcription_worker.whisper_mac import MLX_SCRIPT_SEED, _vocabulary_prompt

    terms = ["Knemo", "PadForge", "Trovioo", "ArcFace", "Miles", "Wavelet"]
    prompt = _vocabulary_prompt(terms)

    assert prompt.startswith(MLX_SCRIPT_SEED), "the script seed must lead the prompt"
    for term in terms:
        assert term in prompt, f"{term} was dropped from a vocabulary that fits"

    # Emission order is most-relevant-LAST even when nothing is truncated: the
    # 17-term arm, which fits comfortably, was scored in exactly that order
    # (0.8497, the best of the ladder). Position carries weight inside the
    # window, not only at its edge.
    positions = [prompt.index(t) for t in terms]
    assert positions == sorted(positions, reverse=True), (
        f"expected most-relevant last, got {prompt}"
    )


def test_truncation_keeps_THE_MOST_RELEVANT_and_drops_the_rest():
    """Callers pass most-relevant-first; those are the terms that must survive."""
    from transcription_worker.whisper_mac import _vocabulary_prompt

    terms = _terms(300)
    prompt = _vocabulary_prompt(terms)
    kept = [t for t in terms if t in prompt]

    assert kept, "a 300-term vocabulary produced no terms at all"
    assert len(kept) < len(terms), "300 terms must not fit in a 223-token window"
    # What survives is a PREFIX of the input: the caller's ranking is honoured.
    assert kept == terms[:len(kept)], (
        "truncation must keep the most relevant terms, which arrive first; kept "
        f"{kept[:3]}…{kept[-3:]} out of {len(terms)}"
    )
    assert terms[0] in prompt, "the MOST relevant term must never be dropped"
    assert terms[-1] not in prompt, "the LEAST relevant term must be dropped first"


def test_the_kept_terms_are_emitted_most_relevant_LAST():
    """Whisper keeps the final 223 tokens, so the best term must sit at the end.

    This is the half that makes the ranking worth anything: keeping the right
    terms and then emitting them head-first would put them back in the region
    that gets discarded.
    """
    from transcription_worker.whisper_mac import _vocabulary_prompt

    terms = ["Trovioo", "Knemo", "ArcFace", "PadForge"]       # most relevant first
    prompt = _vocabulary_prompt(terms)

    positions = [prompt.index(t) for t in terms]
    assert positions == sorted(positions, reverse=True), (
        f"expected most-relevant last in the emitted prompt, got {prompt}"
    )


def test_the_same_terms_survive_or_vanish_purely_by_position():
    """Position alone decides, which is why the end-to-end order matters."""
    from transcription_worker.whisper_mac import _vocabulary_prompt

    wanted = ["Trovioo", "Knemo", "ArcFace"]
    filler = _terms(300, prefix="Filler")

    first = _vocabulary_prompt(wanted + filler)
    last = _vocabulary_prompt(filler + wanted)

    for term in wanted:
        assert term in first, f"{term} ranked FIRST must survive"
        assert term not in last, f"{term} ranked LAST must be truncated away"


def test_truncation_is_reported_rather_than_silent(capsys):
    """Silent truncation is what let a 1000-term ceiling hide a ~67-term one."""
    from transcription_worker.whisper_mac import _vocabulary_prompt

    _vocabulary_prompt(_terms(300))
    err = capsys.readouterr().err
    assert "vocabulary truncated" in err, err
    assert "300" in err and "dropped" in err, err

    _vocabulary_prompt(["Knemo", "Trovioo"])
    assert "truncated" not in capsys.readouterr().err, "a fitting vocabulary must be quiet"


def test_the_effective_ceiling_is_far_below_maxPhrases():
    """`DictationBiasVocabulary.maxPhrases` is 1000. Measure what actually fits.

    Not asserted as an exact number — the estimator may be retuned — but the
    ORDER of magnitude is the point: a four-figure ceiling on a path whose real
    capacity is two figures means most of the vocabulary is silently dropped.
    """
    from transcription_worker.whisper_mac import _vocabulary_prompt

    terms = _terms(1000)
    kept = [t for t in terms if t in _vocabulary_prompt(terms)]

    assert len(kept) < 150, (
        f"expected the 223-token window to hold well under 150 terms, kept {len(kept)}"
    )
    assert len(kept) > 10, f"sanity: expected a usable prompt, kept only {len(kept)}"


def test_an_empty_or_blank_vocabulary_produces_no_prompt():
    from transcription_worker.whisper_mac import _vocabulary_prompt

    assert _vocabulary_prompt(None) is None
    assert _vocabulary_prompt([]) is None
    assert _vocabulary_prompt(["", "   "]) is None


def test_the_script_seed_is_overridable_for_measurement(monkeypatch):
    """The seed tradeoff depends on the model and audio corpus.

    Keep it configurable so external evaluation can compare both settings
    on the same audio. This fixture establishes configuration wiring only.
    """
    import importlib
    from transcription_worker import whisper_mac as wm

    monkeypatch.setenv("DICTATION_MLX_SCRIPT_SEED", "")
    reloaded = importlib.reload(wm)
    try:
        prompt = reloaded._vocabulary_prompt(["Knemo", "Trovioo"])
        assert "以下是" not in prompt, f"seed survived an empty override: {prompt}"
        assert "Knemo" in prompt and "Trovioo" in prompt
        english = reloaded._vocabulary_prompt(["repo", "repository"], language="en")
        assert "English dictation" not in english
        assert "repo" in english and "repository" in english
    finally:
        monkeypatch.delenv("DICTATION_MLX_SCRIPT_SEED", raising=False)
        importlib.reload(wm)


def test_the_seed_is_present_by_default():
    import importlib
    from transcription_worker import whisper_mac as wm
    reloaded = importlib.reload(wm)
    assert reloaded.MLX_SCRIPT_SEED.startswith("以下是"), reloaded.MLX_SCRIPT_SEED
    assert reloaded._vocabulary_prompt(["Knemo"]).startswith(reloaded.MLX_SCRIPT_SEED)


# --- the vocabulary prompt on the faster (Windows) engine -------------------
#
# LEARNING.md §6: learned words reach Windows' CPU engine through the same
# prompt the mlx branch builds. Unmeasured for accuracy on that engine; these
# pin only that the bytes reach the library, and that nothing changes when
# there is no vocabulary.


def _faster(monkeypatch, text):
    """A real WhisperMac on the faster branch. ``text`` is what the fake
    library decodes, or a function of the options it received."""
    import sys
    import types

    calls = []
    fake = types.ModuleType("faster_whisper")

    class FakeWhisperModel:
        def __init__(self, *_args, **_kwargs):
            pass

        def transcribe(self, _audio, **kwargs):
            calls.append(kwargs)
            segment = types.SimpleNamespace(text=text(kwargs) if callable(text) else text)
            info = types.SimpleNamespace(language="en", language_probability=0.99)
            return iter([segment]), info

    fake.WhisperModel = FakeWhisperModel
    monkeypatch.setitem(sys.modules, "faster_whisper", fake)
    monkeypatch.setenv("DICTATION_ASR_ENGINE", "faster")
    from transcription_worker.whisper_mac import WhisperMac

    return WhisperMac(), calls


AUDIBLE = np.full(1600, 0.1, dtype=np.float32)


@pytest.fixture
def no_user_aliases(monkeypatch, tmp_path):
    """The engine's own alias pass reads the user's data folder; keep it empty."""
    from transcription_worker import learned_vocabulary, spoken_aliases

    monkeypatch.setattr(spoken_aliases, "default_path", lambda: tmp_path / "bias_aliases.json")
    monkeypatch.setattr(learned_vocabulary, "_cache", None)
    return tmp_path


def test_faster_gets_the_prompt_mlx_would_build(monkeypatch, no_user_aliases):
    from transcription_worker.whisper_mac import _vocabulary_prompt

    engine, calls = _faster(monkeypatch, " hello")
    vocabulary = ["Trovioo", "SkyDock", "Knemo"]

    result = engine.transcribe(AUDIBLE, bias_phrases=vocabulary, beam_size=4)

    assert calls[0]["initial_prompt"] == _vocabulary_prompt(vocabulary)
    assert result["stats"]["personalization"] == "L1-prompt: initial_prompt seeded (faster engine)"


def test_faster_keeps_the_same_budget_as_mlx(monkeypatch, no_user_aliases):
    from transcription_worker.whisper_mac import _vocabulary_prompt

    engine, calls = _faster(monkeypatch, " hello")
    vocabulary = [f"Term{i:04d}" for i in range(300)]

    engine.transcribe(AUDIBLE, bias_phrases=vocabulary)

    assert calls[0]["initial_prompt"] == _vocabulary_prompt(vocabulary)
    assert "Term0000" in calls[0]["initial_prompt"] and "Term0299" not in calls[0]["initial_prompt"]


def test_a_callers_prompt_still_wins_on_faster(monkeypatch, no_user_aliases):
    """The live caption path passes its own prompt; the vocabulary does not
    replace it, and the stats do not claim a vocabulary prompt was sent."""
    engine, calls = _faster(monkeypatch, " hello")

    result = engine.transcribe(AUDIBLE, bias_phrases=["Trovioo"], beam_size=4,
                               initial_prompt="previous words")

    assert calls[0]["initial_prompt"] == "previous words"
    assert result["stats"]["personalization"].startswith("L0-only")


def test_no_vocabulary_means_no_prompt_on_faster(monkeypatch, no_user_aliases):
    engine, calls = _faster(monkeypatch, " hello")

    engine.transcribe(AUDIBLE, bias_phrases=None, beam_size=4)
    engine.transcribe(AUDIBLE, bias_phrases=["", "  "], beam_size=4)

    assert [call["initial_prompt"] for call in calls] == [None, None]


def _echoing(speech):
    """A decoder that says ``speech`` and then continues the prompt it was
    given: the vocabulary list exactly as `_vocabulary_prompt` wrote it. The
    echo is read from the prompt the library RECEIVED, never typed by the
    test, so it cannot drift from the prompt's order (an earlier version of
    the test below typed the list in the order it was sent, which is not the
    prompt's order, and so pinned the guard's inverted check as correct)."""
    from transcription_worker.whisper_mac import MLX_SCRIPT_SEED

    def decode(options):
        prompt = options["initial_prompt"]
        assert prompt.startswith(MLX_SCRIPT_SEED + " "), prompt
        return f" {speech} {prompt[len(MLX_SCRIPT_SEED) + 1:]}"

    return decode


VOCABULARY = ["Trovioo", "Knemo", "ArcFace", "PadForge"]


def test_faster_strips_an_echoed_vocabulary_list(monkeypatch, no_user_aliases, capsys):
    """The regurgitation guard is engine-independent, and a prompted faster
    decode can echo its prompt just as mlx does."""
    engine, calls = _faster(monkeypatch, _echoing("你能帮我看一下?"))

    result = engine.transcribe(AUDIBLE, bias_phrases=VOCABULARY)

    # The prompt writes the most relevant (first sent) LAST, so the echo is
    # the reverse of the list that was sent.
    assert calls[0]["initial_prompt"].endswith(" PadForge, ArcFace, Knemo, Trovioo")
    assert result["text"] == "你能帮我看一下?"
    err = capsys.readouterr().err
    assert "dropped 4 regurgitated" in err
    for term in VOCABULARY:
        assert term not in err


def test_a_list_spoken_in_the_order_sent_is_kept(monkeypatch, no_user_aliases, capsys):
    """The mirror of the echo: the same four words in the order the host SENT
    them are not the prompt coming back, and are the user's own words."""
    engine, _ = _faster(monkeypatch, " 我们用了 Trovioo, Knemo, ArcFace, PadForge")

    result = engine.transcribe(AUDIBLE, bias_phrases=VOCABULARY)

    assert result["text"] == "我们用了 Trovioo, Knemo, ArcFace, PadForge"
    assert "regurgitated" not in capsys.readouterr().err


def test_the_guard_reads_the_prompt_the_budget_left():
    """A term the 223-token window dropped was never in the prompt, so a run
    of dropped terms is speech, while the tail of what was written is an
    echo."""
    from transcription_worker.whisper_mac import _prompt_terms, _strip_prompt_regurgitation

    vocabulary = _terms(300)
    written = _prompt_terms(vocabulary)
    assert written[-1] == "Term0000" and "Term0299" not in written

    echo = ", ".join(written[-5:])
    assert _strip_prompt_regurgitation(f"ok {echo}", vocabulary) == ("ok", 5)
    never_written = "Term0296, Term0297, Term0298, Term0299"
    assert _strip_prompt_regurgitation(f"ok {never_written}", vocabulary) == (f"ok {never_written}", 0)


def test_the_engine_says_it_applied_the_alias_table(monkeypatch, no_user_aliases):
    """So the server's text pass does not apply it a second time
    (`spoken_aliases.APPLIED_STAT`). The unassisted baseline ran no table and
    says nothing."""
    from transcription_worker import spoken_aliases

    engine, _ = _faster(monkeypatch, " hello")

    assert engine.transcribe(AUDIBLE)["stats"][spoken_aliases.APPLIED_STAT] is True
    assert spoken_aliases.APPLIED_STAT not in engine.transcribe(AUDIBLE, personalize=False)["stats"]


def test_a_failed_alias_pass_is_not_reported_as_applied(monkeypatch, no_user_aliases):
    """The server then runs the table itself, which fails soft too."""
    from transcription_worker import spoken_aliases

    def boom(*_a, **_k):
        raise RuntimeError("synthetic")

    monkeypatch.setattr(spoken_aliases, "load", boom)
    engine, _ = _faster(monkeypatch, " hello")

    assert spoken_aliases.APPLIED_STAT not in engine.transcribe(AUDIBLE)["stats"]


def test_the_engine_logs_counts_never_words(monkeypatch, no_user_aliases, capsys):
    (no_user_aliases / "bias_aliases.json").write_text('{"aliases": {"trovio": "Trovioo"}}',
                                                       encoding="utf-8")
    engine, _ = _faster(monkeypatch, " questpdf and trovio")

    result = engine.transcribe(AUDIBLE, bias_phrases=["QuestPDF"])

    assert result["text"] == "QuestPDF and Trovioo"
    err = capsys.readouterr().err
    assert "whisper_mac: aliases applied — 1 rewrite(s)" in err
    assert "whisper_mac: casing restored — 1 word(s)" in err
    for word in ("QuestPDF", "questpdf", "Trovioo", "trovio"):
        assert word not in err, f"{word!r} reached stderr: {err}"


def test_mlx_reports_its_prompt_under_its_own_name(tmp_path, monkeypatch, no_user_aliases):
    import sys

    fake = _fake_mlx_module({"text": "hello", "segments": [], "language": "en"})
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)
    from transcription_worker.whisper_mac import WhisperMac, _vocabulary_prompt

    engine = WhisperMac(model_dir=_local_mlx_repo(tmp_path))
    r = engine.transcribe(AUDIBLE, bias_phrases=["Trovioo"], language="en")

    assert fake.calls[0]["initial_prompt"] == _vocabulary_prompt(["Trovioo"], language="en")
    assert r["stats"]["personalization"] == "L1-prompt: initial_prompt seeded (mlx engine)"


def test_mlx_live_preview_keeps_explicit_prompt_through_decode(tmp_path, monkeypatch, no_user_aliases):
    import sys
    from transcription_worker import ops_stream_whisper as stream
    from transcription_worker.whisper_mac import WhisperMac

    fake = _fake_mlx_module({"segments": [{"text": "open the repo"}], "language": "en"})
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)
    engine = WhisperMac(model_dir=_local_mlx_repo(tmp_path))
    channel = stream._ChannelStream(
        engine, stream._CommitLog(), None, fixed_label="You", channel_tag="mic",
        bias_phrases=["repo"], initial_prompt="repo, repository", beam_size=1)

    text, language = channel._transcribe2(AUDIBLE, language="en")

    assert fake.calls[0]["initial_prompt"] == "repo, repository"
    assert text == "open the repo" and language == "en"


@pytest.mark.parametrize("fail", [False, True])
def test_mlx_hybrid_prompt_search_and_visible_fallback(tmp_path, monkeypatch, no_user_aliases, capsys, fail):
    import sys
    from transcription_worker import mlx_beam
    from transcription_worker.whisper_mac import WhisperMac, _vocabulary_prompt

    fake = _fake_mlx_module({"segments": [{"text": "open the repo"}], "language": "en"})
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)
    calls = []

    def beam(audio, **options):
        calls.append(options)
        if fail:
            raise RuntimeError("private vocabulary must never appear in stderr")
        return {"segments": [{"text": "open the repo", "avg_logprob": -.4}], "language": "en",
                "vocabulary_search": {"beam_windows": 1, "matched_terms": 1, "sampling_fallbacks": 0}}

    monkeypatch.setattr(mlx_beam, "transcribe", beam)
    engine = WhisperMac(model_dir=_local_mlx_repo(tmp_path))
    result = engine.transcribe(AUDIBLE, bias_phrases=["repo"], beam_size=2,
                               language="en", vocabulary_search=True)

    assert calls[0]["initial_prompt"] == _vocabulary_prompt(["repo"], language="en")
    assert calls[0]["bias_phrases"] == ["repo"]
    assert calls[0]["beam_size"] == 2
    assert result["text"] == "open the repo"
    assert result["stats"]["vocabulary_search"]["beam_windows"] == (0 if fail else 1)
    assert result["stats"]["vocabulary_search"]["fallback"] == ("RuntimeError" if fail else None)
    assert len(fake.calls) == (1 if fail else 0)
    assert "private vocabulary" not in capsys.readouterr().err


def test_mlx_search_keeps_confidence_attached_after_the_prompt_term_cap(tmp_path, monkeypatch, no_user_aliases):
    import sys
    from transcription_worker import mlx_beam
    from transcription_worker.whisper_mac import WhisperMac

    fake = _fake_mlx_module({"segments": [{"text": "hello"}], "language": "en"})
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)
    calls = []

    def beam(audio, **options):
        calls.append(options)
        return {"segments": [{"text": "hello", "avg_logprob": -.4}], "language": "en",
                "vocabulary_search": {"beam_windows": 1, "matched_terms": 0, "sampling_fallbacks": 0}}

    monkeypatch.setattr(mlx_beam, "transcribe", beam)
    monkeypatch.setattr(mlx_beam, "MAX_TERMS", 2)
    engine = WhisperMac(model_dir=_local_mlx_repo(tmp_path))
    engine.transcribe(AUDIBLE, bias_phrases=["Taught", "Confirmed", "Tentative"], beam_size=2,
                      language="en", vocabulary_search=True, phrase_deltas={1: .35, 2: .2})
    assert calls[0]["bias_phrases"] == ["Taught", "Confirmed"]
    assert calls[0]["phrase_deltas"] == {1: .35}


def test_mlx_generated_prompt_matches_each_detected_chunk_language(tmp_path, monkeypatch, no_user_aliases):
    import sys
    from types import SimpleNamespace
    from transcription_worker import whisper_mac as wm

    fake = _fake_mlx_module({"segments": [{"text": "open the repo", "avg_logprob": -.2}], "language": "en"})
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)
    readings = iter([("en", .9, .01), ("zh", .9, .01)])
    monkeypatch.setattr(wm, "_mlx_sot_probe", lambda _: SimpleNamespace(read=lambda _: next(readings)))
    monkeypatch.setattr(wm, "auto_language_decode_spans", lambda audio: [(0, 800), (800, len(audio))])
    engine = wm.WhisperMac(model_dir=_local_mlx_repo(tmp_path))
    engine.transcribe(AUDIBLE, bias_phrases=["repo", "repository"])

    english, chinese = fake.calls
    assert english["language"] == "en" and chinese["language"] == "zh"
    assert english["initial_prompt"].startswith(wm.MLX_ENGLISH_SEED)
    assert not english["initial_prompt"].startswith(wm.MLX_SCRIPT_SEED)
    assert chinese["initial_prompt"].startswith("以下是一段")
    assert not chinese["initial_prompt"].startswith(wm.MLX_ENGLISH_SEED)
    assert all("repo" in call["initial_prompt"] for call in fake.calls)


# --- a faster-whisper model whose features cannot fit it --------------------
#
# A Turbo (128-mel) CTranslate2 folder without preprocessor_config.json loads,
# and faster-whisper then builds 80-bin features for every decode. Init says
# so by name; tests/test_worker_frames.py drives it through the staged worker.


def _faster_model(model_bins, feature_bins):
    import types

    return types.SimpleNamespace(
        model=types.SimpleNamespace(n_mels=model_bins),
        feature_extractor=types.SimpleNamespace(mel_filters=np.zeros((feature_bins, 201), dtype=np.float32)))


def test_a_mel_mismatch_is_refused_by_name():
    from transcription_worker.whisper_mac import MelBinMismatch, check_faster_mel_bins

    with pytest.raises(MelBinMismatch, match="128 mel bins .* 80; preprocessor_config.json"):
        check_faster_mel_bins(_faster_model(128, 80))


def test_matching_or_unknown_mel_bins_claim_nothing():
    import types

    from transcription_worker.whisper_mac import check_faster_mel_bins

    check_faster_mel_bins(_faster_model(128, 128))
    check_faster_mel_bins(_faster_model(80, 80))
    # A provider that does not expose the numbers is not a mismatch.
    check_faster_mel_bins(types.SimpleNamespace())
    check_faster_mel_bins(types.SimpleNamespace(model=types.SimpleNamespace(n_mels=128)))


# --- faster-whisper decodes an automatic-language hold per span --------------
#
# faster-whisper with language=None detects once, from the first window, and
# decodes the whole call under it, so a code-switching hold lost its second
# language. The engine now decodes each span of auto_language_decode_spans on
# its own, as the mlx branch does: a span ends at a natural pause, or at a hard
# cut when speech runs past 24 s without one. Fake library: the calls and the
# joining are pinned here; what it gains or costs on real speech is NOT
# measured (no Windows device run).


def _faster_by_span(monkeypatch, decoded):
    """A real WhisperMac on the faster branch whose fake library answers its
    n-th call with decoded[n] = (text, language, probability)."""
    import sys
    import types

    calls = []
    fake = types.ModuleType("faster_whisper")

    class FakeWhisperModel:
        def __init__(self, *_args, **_kwargs):
            pass

        def transcribe(self, audio, **kwargs):
            calls.append({"samples": len(audio), **kwargs})
            text, language, probability = decoded[len(calls) - 1]
            info = types.SimpleNamespace(language=language, language_probability=probability)
            return iter([types.SimpleNamespace(text=text)]), info

    fake.WhisperModel = FakeWhisperModel
    monkeypatch.setitem(sys.modules, "faster_whisper", fake)
    monkeypatch.setenv("DICTATION_ASR_ENGINE", "faster")
    from transcription_worker.whisper_mac import WhisperMac

    return WhisperMac(), calls


def _speech_pause_speech(first_s, pause_s, second_s):
    rate = 16000
    speech = lambda seconds: np.full(int(seconds * rate), 0.1, dtype=np.float32)  # noqa: E731
    return np.concatenate([speech(first_s), np.zeros(int(pause_s * rate), dtype=np.float32), speech(second_s)])


def test_faster_decodes_each_language_span_of_an_automatic_hold(monkeypatch, no_user_aliases):
    from transcription_worker.whisper_mac import auto_language_decode_spans

    clip = _speech_pause_speech(8.0, 1.0, 8.0)
    spans = auto_language_decode_spans(clip)
    assert len(spans) == 2
    engine, calls = _faster_by_span(monkeypatch, [(" We ship on Friday.", "en", 0.93),
                                                  (" 然后我们再看一下。", "zh", 0.61)])

    result = engine.transcribe(clip)

    assert [call["samples"] for call in calls] == [end - start for start, end in spans]
    assert [call["language"] for call in calls] == [None, None]
    assert result["text"] == "We ship on Friday. 然后我们再看一下。"
    assert result["stats"]["language"] == "mul"
    assert result["stats"]["language_probability"] == 0.61


def test_a_chinese_seam_between_spans_gets_no_space(monkeypatch, no_user_aliases):
    engine, _ = _faster_by_span(monkeypatch, [(" 我们明天发布", "zh", 0.9), (" 然后再看", "zh", 0.8)])

    result = engine.transcribe(_speech_pause_speech(8.0, 1.0, 8.0))

    assert result["text"] == "我们明天发布然后再看"
    assert result["stats"]["language"] == "zh"
    assert result["stats"]["language_probability"] == 0.8


def test_a_short_hold_is_still_one_faster_call(monkeypatch, no_user_aliases):
    """A 10 s hold with a pause cannot keep 6 s on both sides of it: one
    call over the whole clip, the call this engine always made."""
    clip = _speech_pause_speech(4.5, 1.0, 4.5)
    engine, calls = _faster_by_span(monkeypatch, [(" hello there", "en", 0.97)])

    result = engine.transcribe(clip)

    assert [call["samples"] for call in calls] == [len(clip)]
    assert result["text"] == "hello there"
    assert (result["stats"]["language"], result["stats"]["language_probability"]) == ("en", 0.97)


def test_unpaused_speech_past_24_s_is_cut_without_a_pause(monkeypatch, no_user_aliases):
    """The spans are not all natural-pause spans: 30 s of speech with no
    pause is cut at 24 s, where a word may be, and decoded as two calls. A
    single whole-hold call never had that seam."""
    from transcription_worker.whisper_mac import AUTO_LANGUAGE_MAX_CHUNK_SECONDS

    clip = np.full(30 * 16000, 0.1, dtype=np.float32)
    engine, calls = _faster_by_span(monkeypatch, [(" we kept talking", "en", 0.9),
                                                  (" right through", "en", 0.8)])

    result = engine.transcribe(clip)

    cut = int(AUTO_LANGUAGE_MAX_CHUNK_SECONDS * 16000)
    assert [call["samples"] for call in calls] == [cut, len(clip) - cut]
    assert result["text"] == "we kept talking right through"


def test_a_pinned_language_is_one_faster_call(monkeypatch, no_user_aliases):
    clip = _speech_pause_speech(8.0, 1.0, 8.0)
    engine, calls = _faster_by_span(monkeypatch, [(" one decode", "en", 1.0)])

    result = engine.transcribe(clip, language="en")

    assert [(call["samples"], call["language"]) for call in calls] == [(len(clip), "en")]
    assert result["text"] == "one decode"


def test_every_span_call_gets_the_vocabulary_prompt(monkeypatch, no_user_aliases):
    from transcription_worker.whisper_mac import _vocabulary_prompt

    engine, calls = _faster_by_span(monkeypatch, [(" first", "en", 0.9), (" second", "en", 0.9)])

    engine.transcribe(_speech_pause_speech(8.0, 1.0, 8.0), bias_phrases=["Trovioo"])

    assert [call["initial_prompt"] for call in calls] == [_vocabulary_prompt(["Trovioo"])] * 2


# --- the per-chunk prompt language (the zh punctuation fix) -----------------
#
# `initial_prompt` conditions style as well as content: a Chinese chunk
# decoded under the Latin comma-list prompt may lose its "。". A chunk the SOT probe read as
# zh — or a caller-pinned language="zh" — therefore decodes under the Chinese
# carrier prompt (`_zh_vocabulary_prompt`); every other chunk keeps the Latin
# one. These fake-engine tests establish prompt routing, not punctuation
# quality or recognition accuracy.


def _mlx_by_chunk(monkeypatch, tmp_path, readings):
    """A WhisperMac on the mlx branch with a fake mlx_whisper module recording
    each call and a fake SOT probe answering readings[n] for the n-th chunk
    (None = the probe ran but could not read that chunk)."""
    import sys
    import types

    from transcription_worker import whisper_mac

    calls = []
    fake = types.ModuleType("mlx_whisper")

    def transcribe(_audio, **kwargs):
        calls.append(kwargs)
        return {"text": " 文本", "segments": [], "language": kwargs.get("language")}

    fake.transcribe = transcribe
    monkeypatch.setitem(sys.modules, "mlx_whisper", fake)

    reads = []

    def read(_chunk):
        reads.append(len(reads))
        return readings[reads[-1]]

    monkeypatch.setattr(whisper_mac, "_mlx_sot_probe",
                        lambda _repo: types.SimpleNamespace(read=read))

    return whisper_mac.WhisperMac(model_dir=_local_mlx_repo(tmp_path)), calls


def test_a_zh_chunk_decodes_under_the_chinese_carrier_prompt(monkeypatch, tmp_path, no_user_aliases):
    from transcription_worker.whisper_mac import _vocabulary_prompt, _zh_vocabulary_prompt

    engine, calls = _mlx_by_chunk(monkeypatch, tmp_path,
                                  [("zh", 0.9, None), ("en", 0.95, None)])
    engine.transcribe(_speech_pause_speech(8.0, 1.0, 8.0), bias_phrases=["Trovioo"])

    assert [c["language"] for c in calls] == ["zh", "en"]
    assert [c["initial_prompt"] for c in calls] == [
        _zh_vocabulary_prompt(["Trovioo"]), _vocabulary_prompt(["Trovioo"], language="en")]


def test_a_chunk_the_probe_cannot_read_keeps_the_latin_prompt(monkeypatch, tmp_path, no_user_aliases):
    """The unreadable-probe path degrades to today's behavior, never to a
    prompt-less or seed-only decode."""
    from transcription_worker.whisper_mac import _vocabulary_prompt

    engine, calls = _mlx_by_chunk(monkeypatch, tmp_path, [None, None])
    engine.transcribe(_speech_pause_speech(8.0, 1.0, 8.0), bias_phrases=["Trovioo"])

    assert [c["initial_prompt"] for c in calls] == [_vocabulary_prompt(["Trovioo"])] * 2


def test_a_pinned_chinese_language_prompts_every_chunk_in_chinese(monkeypatch, tmp_path, no_user_aliases):
    from transcription_worker.whisper_mac import _zh_vocabulary_prompt

    engine, calls = _mlx_by_chunk(monkeypatch, tmp_path, [])  # a pin builds no probe
    engine.transcribe(_speech_pause_speech(8.0, 1.0, 8.0),
                      bias_phrases=["Trovioo"], language="zh")

    assert [c["language"] for c in calls] == ["zh", "zh"]
    assert [c["initial_prompt"] for c in calls] == [_zh_vocabulary_prompt(["Trovioo"])] * 2


def test_without_vocabulary_a_zh_chunk_gets_no_prompt(monkeypatch, tmp_path, no_user_aliases):
    engine, calls = _mlx_by_chunk(monkeypatch, tmp_path, [("zh", 0.9, None)])
    engine.transcribe(AUDIBLE)

    assert calls[0]["initial_prompt"] is None


def test_the_zh_prompt_keeps_the_budgeted_terms_in_prompt_order():
    from transcription_worker.whisper_mac import (
        MLX_SCRIPT_SEED, MLX_ZH_PROMPT_HEAD, MLX_ZH_PROMPT_TAIL,
        _prompt_terms, _zh_vocabulary_prompt)

    terms = ["Trovioo", "Slack"]
    prompt = _zh_vocabulary_prompt(terms)
    assert prompt == MLX_ZH_PROMPT_HEAD + "、".join(_prompt_terms(terms)) + MLX_ZH_PROMPT_TAIL
    assert "、Trovioo " in prompt  # the same most-relevant-last order as the Latin build
    assert _zh_vocabulary_prompt(None) is None
    assert _zh_vocabulary_prompt([]) is None
    assert _zh_vocabulary_prompt(["   "]) is None


def test_the_regurgitation_guard_eats_a_zh_style_echo():
    """The zh prompt writes the same terms in the same order, 、-separated;
    its echo is the list shape the Latin prompt's guard already strips."""
    from transcription_worker.whisper_mac import REGURGITATION_RUN, _strip_prompt_regurgitation

    phrases = ["Trovioo", "Knemo", "ArcFace", "Fable Flow"]
    echo = "Fable Flow、ArcFace、Knemo、Trovioo"  # `_prompt_terms` order
    assert len(phrases) >= REGURGITATION_RUN
    text, dropped = _strip_prompt_regurgitation(f"好的，{echo}", phrases)

    assert dropped == 4
    assert "Trovioo" not in text


def test_native_backend_never_registers_retired_meeting_operations(monkeypatch):
    from transcription_worker import asr_server, whisper_mac
    monkeypatch.setattr(whisper_mac, "WhisperMac", lambda **_kwargs: StubEngine())
    assert asr_server.op_init({"op": "init"})["backend"] == "mac"
    assert {"summarize", "enhance", "note_templates", "diarized_transcribe", "diarized_start", "voice_enroll"}.isdisjoint(asr_server._OPS)
    assert asr_server.op_init({"backend": "qnn", "model_dir": "unused"})["ok"] is False
