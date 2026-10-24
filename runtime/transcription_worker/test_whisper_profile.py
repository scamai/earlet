"""The model-identity derivation — the guard the old code had nowhere.

Every test here runs against FAKE tokenizers and FAKE ONNX sessions, on any
machine, with no model on disk. That is the point: `whisper_qnn` imports
`onnxruntime_qnn`, which exists only on Windows ARM64, so the engine module
cannot be imported on a developer's Mac and NOTHING in it was ever covered by a
suite anybody could run. The ids, the mel count and the vocabulary width are
now derived in `whisper_profile`, which is numpy-only and therefore testable
everywhere — the refactor moved the fragile part to where it can be proven.

The id tables below are the two real ones. base's come from the shipped
tokenizer on an X Elite laptop (2026-08-19); turbo's from the cached
`openai/whisper-large-v3-turbo` `added_tokens.json`. Neither was invented for
this file, and the whole defect is visible in the two columns: base's
`<|nocaptions|>` (50362) is turbo's `<|startofprev|>`, and base's
`<|notimestamps|>` (50363) is turbo's no-speech token.
"""

from __future__ import annotations

import io
import re
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.beam_decode import (  # noqa: E402
    SpeechGate,
    beam_search,
    no_speech_token_id,
)
from transcription_worker.whisper_profile import (  # noqa: E402
    BIAS_DELTA_BY_FINGERPRINT,
    CALIBRATED_FINGERPRINT,
    TURBO_FINGERPRINT,
    bias_delta_for,
    WhisperGraph,
    WhisperModelMismatch,
    WhisperProfile,
    resolve_token,
    warn_if_uncalibrated,
)

# ---------------------------------------------------------------------------
# The two real layouts
# ---------------------------------------------------------------------------

EOT = 50257  # identical on both, and it is also base's unk_token

BASE_IDS = {
    "<|endoftext|>": EOT,
    "<|startoftranscript|>": 50258,
    "<|en|>": 50259,
    "<|zh|>": 50260,
    "<|translate|>": 50358,
    "<|transcribe|>": 50359,
    "<|startofprev|>": 50361,
    "<|nocaptions|>": 50362,  # base spells it this way, and ONLY this way
    "<|notimestamps|>": 50363,
    "<|0.00|>": 50364,
}
BASE_VOCAB = 51865
BASE_MELS = 80

TURBO_IDS = {
    "<|endoftext|>": EOT,
    "<|startoftranscript|>": 50258,
    "<|en|>": 50259,
    "<|zh|>": 50260,
    "<|yue|>": 50358,  # the hundredth language, which shifted everything below
    "<|translate|>": 50359,
    "<|transcribe|>": 50360,
    "<|startofprev|>": 50362,  # <- base's no-speech id
    "<|nospeech|>": 50363,  # <- base's <|notimestamps|> id
    "<|notimestamps|>": 50364,
    "<|0.00|>": 50365,
}
TURBO_VOCAB = 51866
TURBO_MELS = 128


class FakeTokenizer:
    """`convert_tokens_to_ids` returning unk for anything it does not know —
    the behaviour that makes a by-name lookup dangerous without the unk check.
    """

    def __init__(self, ids: dict[str, int], vocab: int, unk: int = EOT):
        self._ids = dict(ids)
        self._vocab = vocab
        self.unk_token_id = unk
        self.eos_token_id = EOT

    def convert_tokens_to_ids(self, token):
        return self._ids.get(token, self.unk_token_id)

    def __len__(self):
        return self._vocab


class FakeExtractor:
    def __init__(self, feature_size: int):
        self.feature_size = feature_size


def base_tokenizer(**kw) -> FakeTokenizer:
    return FakeTokenizer(BASE_IDS, BASE_VOCAB, **kw)


def turbo_tokenizer(**kw) -> FakeTokenizer:
    return FakeTokenizer(TURBO_IDS, TURBO_VOCAB, **kw)


class _IO:
    def __init__(self, name, shape, type_=None):
        self.name = name
        self.shape = shape
        self.type = type_


class FakeEncoder:
    def __init__(self, mels=BASE_MELS, name="input_features",
                 type_="tensor(float16)"):
        self._in = [_IO(name, [1, mels, 3000], type_)]

    def get_inputs(self):
        return self._in

    def get_outputs(self):
        return [_IO("k_cache_cross_0", [1, 1, 1, 1], "tensor(float16)")]


class FakeDecoder:
    def __init__(self, vocab=BASE_VOCAB, context=200, n_layers=1,
                 kv_type="tensor(float16)"):
        self.vocab = vocab
        self._in = [
            _IO("input_ids", [1, 1], "tensor(int32)"),
            _IO("attention_mask", [1, 1, 1, context], "tensor(float16)"),
            _IO("position_ids", [1], "tensor(int32)"),
        ]
        for i in range(n_layers):
            self._in.append(_IO(f"k_cache_self_{i}_in", [1, 1, 4, 8], kv_type))
            self._in.append(_IO(f"v_cache_self_{i}_in", [1, 1, 4, 8], kv_type))
        self._out = [_IO("logits", [1, vocab, 1, 1], "tensor(float32)")]
        for i in range(n_layers):
            self._out.append(_IO(f"k_cache_self_{i}_out", [1, 1, 4, 8], kv_type))
            self._out.append(_IO(f"v_cache_self_{i}_out", [1, 1, 4, 8], kv_type))

    def get_inputs(self):
        return self._in

    def get_outputs(self):
        return self._out


# ---------------------------------------------------------------------------
# The no-speech id — the one the whole gate hangs on
# ---------------------------------------------------------------------------


def test_base_resolves_its_no_speech_token_under_the_nocaptions_spelling():
    """The value the old hardcoded constant carried, arrived at by derivation.

    This is the behaviour-preservation assertion for the shipped model: the
    refactor must produce 50362 on base, or dictation changed underneath us.
    """
    profile = WhisperProfile.from_tokenizer(
        base_tokenizer(), model="openai/whisper-base",
        feature_extractor=FakeExtractor(BASE_MELS),
    )
    assert profile.no_speech == 50362


def test_a_128_mel_tokenizer_yields_turbos_no_speech_id_not_bases():
    """The defect, inverted into an assertion.

    50362 is a perfectly valid token on turbo — it is `<|startofprev|>` — so
    the old constant produced no error, no log line and no failing test. It
    just made `_no_speech_prob` report P(startofprev) ~ 0 at the SOT step
    forever, which retires the gate that stops a silent hold from typing
    Korean. Derived by name, the same tokenizer answers 50363.
    """
    profile = WhisperProfile.from_tokenizer(
        turbo_tokenizer(), model="openai/whisper-large-v3-turbo",
        feature_extractor=FakeExtractor(TURBO_MELS),
    )
    assert profile.n_mels == 128
    assert profile.no_speech == 50363
    assert profile.no_speech != BASE_IDS["<|nocaptions|>"]
    # And the id the old constant pointed at means something else entirely.
    assert resolve_token(turbo_tokenizer(), "<|startofprev|>") == 50362


def test_the_wrong_spelling_is_rejected_rather_than_answered_with_unk():
    """On base, asking for `<|nospeech|>` returns 50257 = unk = <|endoftext|>.
    A gate built on that answer reads P(EOT) at the SOT step and fires on
    ordinary speech."""
    tok = base_tokenizer()
    assert tok.convert_tokens_to_ids("<|nospeech|>") == EOT  # the trap itself
    assert resolve_token(tok, "<|nospeech|>") is None
    assert no_speech_token_id(tok) == 50362


def test_a_model_without_any_no_speech_token_fails_loudly_at_load():
    """Not None, not a disabled rule, not a warning — a raise naming the model.

    A gate that can never fire is indistinguishable from no gate at all until
    a user's silence types Korean, so this is the one absence that must stop
    the load rather than degrade it.
    """
    ids = {k: v for k, v in BASE_IDS.items() if k != "<|nocaptions|>"}
    with pytest.raises(WhisperModelMismatch, match=r"nospeech.*nocaptions"):
        WhisperProfile.from_tokenizer(
            FakeTokenizer(ids, BASE_VOCAB), model="some/checkpoint")
    # ... and the model is named, so the log line is actionable.
    try:
        WhisperProfile.from_tokenizer(
            FakeTokenizer(ids, BASE_VOCAB), model="some/checkpoint")
    except WhisperModelMismatch as exc:
        assert "some/checkpoint" in str(exc)


@pytest.mark.parametrize("missing", [
    "<|startoftranscript|>", "<|transcribe|>", "<|translate|>",
    "<|notimestamps|>",
])
def test_every_prefix_token_is_required_not_defaulted(missing):
    """The old beam path fell back to base's four ids when ANY of these failed
    to resolve — which on turbo decodes English+translate with a no-speech
    token in the prompt (measured: en CER 17.12 / zh CER 73.76)."""
    ids = {k: v for k, v in TURBO_IDS.items() if k != missing}
    with pytest.raises(WhisperModelMismatch, match="turbo"):
        WhisperProfile.from_tokenizer(
            FakeTokenizer(ids, TURBO_VOCAB), model="openai/whisper-large-v3-turbo")


# ---------------------------------------------------------------------------
# The rest of the identity
# ---------------------------------------------------------------------------


def test_the_forced_prefix_and_language_range_follow_the_model():
    base = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")
    turbo = WhisperProfile.from_tokenizer(turbo_tokenizer(), model="turbo")

    assert base.forced_prefix == [50258, -1, 50359, 50363]
    assert turbo.forced_prefix == [50258, -1, 50360, 50364]
    # The language block grew by one on large-v3 (<|yue|>), which is what
    # pushed every task token up and made base's constants wrong.
    assert base.language_range == (50259, 50357)
    assert turbo.language_range == (50259, 50358)
    assert base.vocab_size == BASE_VOCAB
    assert turbo.vocab_size == TURBO_VOCAB
    # -1 is the language slot: chosen by the model at decode time, never baked.
    assert base.forced_prefix[1] == -1


def test_timestamp_base_and_startofprev_are_derived_too():
    base = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")
    turbo = WhisperProfile.from_tokenizer(turbo_tokenizer(), model="turbo")

    assert (base.timestamp_begin, base.startofprev) == (50364, 50361)
    assert (turbo.timestamp_begin, turbo.startofprev) == (50365, 50362)


def test_a_checkpoint_that_orders_its_specials_differently_is_refused():
    """`language_range` is (sot + 1, translate - 1) and `prepare()` suppresses
    everything at or above SOT. A layout that breaks those assumptions would
    decode garbage under both rules without raising anything."""
    ids = dict(BASE_IDS)
    ids["<|translate|>"] = ids["<|startoftranscript|>"] - 1
    with pytest.raises(WhisperModelMismatch, match="language-token range"):
        WhisperProfile.from_tokenizer(
            FakeTokenizer(ids, BASE_VOCAB), model="scrambled/checkpoint")


def test_vocab_falls_back_to_the_highest_special_when_the_tokenizer_cannot_say():
    """The tiny fakes the decoder suites build implement no `__len__`."""

    class Minimal:
        unk_token_id = EOT
        eos_token_id = EOT

        def convert_tokens_to_ids(self, token):
            return BASE_IDS.get(token, EOT)

    profile = WhisperProfile.from_tokenizer(Minimal(), model="minimal")
    assert profile.vocab_size == max(BASE_IDS.values()) + 1


# ---------------------------------------------------------------------------
# The graph half — the mel mismatch that looks like a broken microphone
# ---------------------------------------------------------------------------


def test_the_shipped_pairing_still_derives_todays_constants():
    """Behaviour preservation, stated as numbers: base tokenizer + the shipped
    AI Hub base assets must derive exactly what the constants said."""
    profile = WhisperProfile.from_tokenizer(
        base_tokenizer(), model="openai/whisper-base",
        feature_extractor=FakeExtractor(BASE_MELS))
    graph = WhisperGraph.from_sessions(
        FakeEncoder(mels=BASE_MELS), FakeDecoder(vocab=BASE_VOCAB, context=200),
        profile)

    assert graph.encoder_input_name == "input_features"
    assert graph.feature_dtype is np.float16
    assert graph.decode_context == 200
    assert graph.n_layers == 1
    assert graph.n_mels == 80


def test_an_80_mel_tokenizer_over_a_128_mel_encoder_raises_naming_both():
    """The live failure of 2026-08-18, moved from the first utterance to load.

    Before this, `build_asr_payload.py` would stage base's 80-mel feature
    extractor next to a 128-mel encoder and report "speech payload staged";
    its own verify() only asserted `on_npu`, which construction satisfies
    because the extractor is not invoked until decode time.
    """
    profile = WhisperProfile.from_tokenizer(
        base_tokenizer(), model="openai/whisper-base",
        feature_extractor=FakeExtractor(80))

    with pytest.raises(WhisperModelMismatch) as excinfo:
        WhisperGraph.from_sessions(
            FakeEncoder(mels=128), FakeDecoder(vocab=BASE_VOCAB), profile)

    message = str(excinfo.value)
    assert "80" in message and "128" in message
    assert "openai/whisper-base" in message


def test_a_128_mel_tokenizer_over_an_80_mel_encoder_raises_too():
    profile = WhisperProfile.from_tokenizer(
        turbo_tokenizer(), model="openai/whisper-large-v3-turbo",
        feature_extractor=FakeExtractor(128))

    with pytest.raises(WhisperModelMismatch, match="128-mel"):
        WhisperGraph.from_sessions(
            FakeEncoder(mels=80), FakeDecoder(vocab=TURBO_VOCAB), profile)


def test_the_matching_pairing_passes():
    profile = WhisperProfile.from_tokenizer(
        turbo_tokenizer(), model="openai/whisper-large-v3-turbo",
        feature_extractor=FakeExtractor(128))
    graph = WhisperGraph.from_sessions(
        FakeEncoder(mels=128), FakeDecoder(vocab=TURBO_VOCAB), profile)

    assert graph.n_mels == 128 == profile.n_mels


def test_a_decoder_too_narrow_for_the_tokenizers_specials_is_refused():
    """A base-width decoder under a turbo tokenizer: `<|notimestamps|>` =
    50364 would index past a 50364-wide softmax."""
    profile = WhisperProfile.from_tokenizer(
        turbo_tokenizer(), model="turbo", feature_extractor=FakeExtractor(128))

    with pytest.raises(WhisperModelMismatch, match="different models"):
        WhisperGraph.from_sessions(
            FakeEncoder(mels=128), FakeDecoder(vocab=50364), profile)


def test_decode_context_comes_from_the_graph_not_from_a_constant():
    """MEAN_DECODE_LEN = 200 is a property of how the decoder was COMPILED,
    and it was duplicated across two modules with no shared definition."""
    profile = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")

    assert WhisperGraph.from_sessions(
        FakeEncoder(), FakeDecoder(context=448), profile).decode_context == 448


def test_a_symbolic_context_falls_back_to_the_documented_default():
    profile = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")

    assert WhisperGraph.from_sessions(
        FakeEncoder(), FakeDecoder(context="n_ctx"), profile
    ).decode_context == 200


def test_the_feature_dtype_follows_the_encoders_declared_precision():
    """AI Hub publishes fp32 encoders too; the old code hardcast to float16."""
    profile = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")

    fp32 = WhisperGraph.from_sessions(
        FakeEncoder(type_="tensor(float)"), FakeDecoder(), profile)
    assert fp32.feature_dtype is np.float32

    with pytest.raises(WhisperModelMismatch, match="tensor\\(int8\\)"):
        WhisperGraph.from_sessions(
            FakeEncoder(type_="tensor(int8)"), FakeDecoder(), profile)


def test_the_encoder_input_name_is_discovered_not_assumed():
    """Every other graph name in the engine was discovered dynamically; this
    one string literal was not."""
    profile = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")

    graph = WhisperGraph.from_sessions(
        FakeEncoder(name="audio"), FakeDecoder(), profile)
    assert graph.encoder_input_name == "audio"


def test_a_decoder_this_worker_cannot_drive_says_so_by_name():
    profile = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")

    decoder = FakeDecoder()
    decoder._out = [_IO("output_0", [1, BASE_VOCAB, 1, 1], "tensor(float32)")]
    with pytest.raises(WhisperModelMismatch, match="no 'logits' output"):
        WhisperGraph.from_sessions(FakeEncoder(), decoder, profile)

    no_kv = FakeDecoder(n_layers=0)
    with pytest.raises(WhisperModelMismatch, match="k_cache_self"):
        WhisperGraph.from_sessions(FakeEncoder(), no_kv, profile)


def test_kv_and_mask_dtypes_are_read_off_the_decoder():
    profile = WhisperProfile.from_tokenizer(base_tokenizer(), model="base")

    graph = WhisperGraph.from_sessions(
        FakeEncoder(), FakeDecoder(kv_type="tensor(float)"), profile)
    assert graph.kv_dtype is np.float32
    assert graph.mask_dtype is np.float16  # the mask input still declares fp16


# ---------------------------------------------------------------------------
# The calibration marker
# ---------------------------------------------------------------------------


def test_the_shipped_model_does_not_warn():
    profile = WhisperProfile.from_tokenizer(
        base_tokenizer(), model="openai/whisper-base",
        feature_extractor=FakeExtractor(BASE_MELS))

    assert profile.fingerprint == CALIBRATED_FINGERPRINT
    out = io.StringIO()
    assert warn_if_uncalibrated(profile, stream=out) is False
    assert out.getvalue() == ""


def test_another_model_warns_that_every_threshold_is_unmeasured_on_it():
    """SpeechGate's 0.75/0.10/0.90 and BiasTrie's delta=1.0 come from ONE
    whisper-base-on-X-Elite table. They will not fail on another checkpoint,
    they will be mis-set in an unknown direction — on the gate that exists to
    stop foreign-language hallucination. A warning, not a raise: refusing to
    load would turn "re-measure this" into "cannot ship"."""
    profile = WhisperProfile.from_tokenizer(
        turbo_tokenizer(), model="openai/whisper-large-v3-turbo",
        feature_extractor=FakeExtractor(TURBO_MELS))

    out = io.StringIO()
    assert warn_if_uncalibrated(profile, stream=out) is True
    warning = out.getvalue()
    assert "openai/whisper-large-v3-turbo" in warning
    assert "openai/whisper-base" in warning


# ---------------------------------------------------------------------------
# Through beam_search — the decoder the shipped dictation path actually runs
# ---------------------------------------------------------------------------


class ScriptedDecoder(FakeDecoder):
    """Emits a fixed distribution every step, so the only thing under test is
    which prefix tokens beam_search forces."""

    def __init__(self, vocab, favourite, context=200):
        super().__init__(vocab=vocab, context=context, n_layers=1)
        self.favourite = favourite
        self.fed: list[int] = []

    def run(self, _fetches, inputs):
        self.fed.append(int(inputs["input_ids"][0][0]))
        logits = np.full((1, self.vocab, 1, 1), -8.0, dtype=np.float32)
        # 12.0 against -8.0 over a ~52k vocabulary puts P(favourite) at 0.9998,
        # which is above SpeechGate's certain_threshold (0.90) -- the bar the
        # gate requires before rejecting a chunk WITHOUT decoding it.
        logits[0, self.favourite, 0, 0] = 12.0
        logits[0, EOT, 0, 0] = 2.0
        kv = np.zeros((1, 1, 4, 8), dtype=np.float16)
        return [logits, kv, kv]


def _gate(profile):
    return SpeechGate(
        no_speech_id=profile.no_speech,
        no_speech_threshold=0.75,
        language_prob_threshold=None,
        avg_logprob_threshold=-1.0,
        prompt_conditioned=False,
    )


@pytest.mark.parametrize("tokenizer_factory,vocab,expect_transcribe", [
    (base_tokenizer, BASE_VOCAB, 50359),
    (turbo_tokenizer, TURBO_VOCAB, 50360),
])
def test_beam_search_forces_the_prefix_of_the_model_it_was_given(
    tokenizer_factory, vocab, expect_transcribe
):
    """The FORCED_PREFIX defect, end to end.

    base's [50258, 50259, 50359, 50363] reads on turbo as
    [sot, en, TRANSLATE, NOSPEECH] — English-translate mode with a no-speech
    token in the prompt. That shipped and measured zh CER 73.76.
    """
    decoder = ScriptedDecoder(vocab, favourite=100)
    tokens = beam_search(
        decoder, cross_kv={}, tokenizer=tokenizer_factory(),
        beam_size=1, max_len=8)

    assert tokens[0] == 50258
    assert tokens[2] == expect_transcribe
    # The language slot was chosen by the model, inside the language range.
    profile = WhisperProfile.from_tokenizer(tokenizer_factory(), model="x")
    lo, hi = profile.language_range
    assert lo <= tokens[1] <= hi
    assert tokens[3] == profile.notimestamps


def test_beam_search_refuses_a_tokenizer_it_cannot_resolve_instead_of_guessing():
    """This is the branch that used to `prefix = list(FORCED_PREFIX)`. A
    tokenizer that fails one lookup got base's four ids, silently, on whatever
    checkpoint it actually was."""
    ids = {k: v for k, v in TURBO_IDS.items() if k != "<|transcribe|>"}

    with pytest.raises(WhisperModelMismatch):
        beam_search(
            ScriptedDecoder(TURBO_VOCAB, favourite=100), cross_kv={},
            tokenizer=FakeTokenizer(ids, TURBO_VOCAB), beam_size=1, max_len=8)


def test_the_gate_reads_turbos_no_speech_slot_through_beam_search():
    """A turbo-shaped decode whose SOT step puts its mass on 50363 must be
    rejected as non-speech. Under the old constant the gate read 50362 —
    `<|startofprev|>`, in range, ~0 — and returned a transcript."""
    profile = WhisperProfile.from_tokenizer(turbo_tokenizer(), model="turbo")
    decoder = ScriptedDecoder(TURBO_VOCAB, favourite=profile.no_speech)

    stats: dict = {}
    tokens = beam_search(
        decoder, cross_kv={}, tokenizer=turbo_tokenizer(), beam_size=1,
        max_len=8, speech_gate=_gate(profile), stats=stats)

    assert stats["no_speech"] is True
    assert stats["no_speech_reason"] == "no_speech"
    assert tokens == [profile.sot]

    # Same decoder, but with base's constant as the gate's id: the mass sits
    # on turbo's no-speech token and 50362 sees nothing, so the chunk sails
    # through. This is the shipped defect, asserted so it stays fixed.
    wrong = SpeechGate(
        no_speech_id=BASE_IDS["<|nocaptions|>"], no_speech_threshold=0.75,
        language_prob_threshold=None, avg_logprob_threshold=-1.0,
        prompt_conditioned=False)
    passed_through: dict = {}
    beam_search(
        ScriptedDecoder(TURBO_VOCAB, favourite=profile.no_speech), cross_kv={},
        tokenizer=turbo_tokenizer(), beam_size=1, max_len=8,
        speech_gate=wrong, stats=passed_through)
    assert not passed_through.get("no_speech")


# ---------------------------------------------------------------------------
# The phrase-bias operating point follows the checkpoint
# ---------------------------------------------------------------------------


def _profile(tokenizer, mels, model):
    return WhisperProfile.from_tokenizer(
        tokenizer, model=model, feature_extractor=FakeExtractor(mels))


def test_the_two_checkpoints_get_different_deltas():
    """The defect this closes: Turbo ran at base's 1.0.

    Not a tautology over the table — it asserts the two are DIFFERENT, which
    is the whole content of "delta is a property of the checkpoint". A future
    edit that collapses them back to one value fails here.
    """
    base = _profile(base_tokenizer(), BASE_MELS, "base")
    turbo = _profile(turbo_tokenizer(), TURBO_MELS, "turbo")

    assert bias_delta_for(base) == 1.0
    assert bias_delta_for(turbo) == 2.5
    assert bias_delta_for(base) != bias_delta_for(turbo)


def test_turbos_fingerprint_is_the_one_the_id_table_says_it_is():
    """Keyed on what the tokenizer actually yields, not on a copied literal.

    TURBO_FINGERPRINT is a hand-written constant; this derives the same triple
    from the real turbo id table and asserts they agree, so a typo in the
    constant cannot silently send Turbo down the unknown-checkpoint branch and
    quietly cost 25 points of recall.
    """
    turbo = _profile(turbo_tokenizer(), TURBO_MELS, "turbo")
    assert turbo.fingerprint == TURBO_FINGERPRINT
    assert turbo.fingerprint in BIAS_DELTA_BY_FINGERPRINT


def test_an_unknown_checkpoint_gets_the_gentlest_measured_delta_and_says_so():
    """Conservative, not middling, and never silent.

    Too low costs recall; too high costs the document (26.1% of clips came
    back EMPTY at a delta a beam could not confirm). An unmeasured checkpoint
    has an unknown breaking point, so it gets the floor — and one line naming
    it, because the failure mode this package keeps repeating is a caller
    inheriting an operating point nobody chose.
    """
    stranger = FakeTokenizer(dict(TURBO_IDS), TURBO_VOCAB)
    profile = _profile(stranger, 999, "some/unmeasured-whisper")

    out = io.StringIO()
    assert bias_delta_for(profile, stream=out) == min(
        BIAS_DELTA_BY_FINGERPRINT.values())

    warned = out.getvalue()
    assert "some/unmeasured-whisper" in warned
    assert "conservative" in warned


def test_the_delta_is_never_silently_inherited():
    """`BiasTrie` still refuses to supply one of its own.

    The resolver would be pointless if the constructor could hand a caller a
    default anyway — that is exactly how delta=4.0 shipped twice.
    """
    from transcription_worker.beam_decode import BiasTrie

    with pytest.raises(TypeError):
        BiasTrie([], None)
