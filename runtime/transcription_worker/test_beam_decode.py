"""The non-speech gate on the beam path (beam_decode.SpeechGate).

Regression cover for the 2026-08-19 field report: a user held the dictation
key, said nothing, and `날씨였습니다.` was typed into their document. Greedy
returned empty on the same audio; dictation ships beam 4
(`TranscriberOptions.Default`), so the ungated path was the only one anyone
ran.

These tests assert what the USER sees -- the decoder returns nothing to
detokenize -- not that some probability crossed some line. A FakeDecoderSession
stands in for ort.InferenceSession, same approach as test_density_ratio.py, so
the whole gate is exercised without a Snapdragon.

Run: .venv/Scripts/python.exe -m pytest transcription_worker/test_beam_decode.py
"""

from __future__ import annotations

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

# Real whisper-base special ids: the gate reads P(<|nocaptions|>) out of the
# SOT-step distribution, so the fake vocabulary has to be big enough to
# contain it. EOT/SOT keep their real values for the same reason.
EOT = 50257
SOT = 50258
EN = 50259
KO = 50264
ZH = 50260
TRANSLATE = 50358
TRANSCRIBE = 50359
NO_SPEECH = 50362  # <|nocaptions|> on whisper-base
NOTIMESTAMPS = 50363
VOCAB = 50364

WORD_A, WORD_B = 100, 200


class _IO:
    def __init__(self, name, shape):
        self.name = name
        self.shape = shape


class FakeTokenizer:
    """Only what beam_search and no_speech_token_id ask of it. Mirrors the
    whisper-base trap measured on an X Elite laptop: `<|nospeech|>` is NOT a known token
    there and resolves to unk (== EOT); the real name is `<|nocaptions|>`."""

    unk_token_id = EOT

    _IDS = {
        "<|startoftranscript|>": SOT,
        "<|translate|>": TRANSLATE,
        "<|transcribe|>": TRANSCRIBE,
        "<|notimestamps|>": NOTIMESTAMPS,
        "<|nocaptions|>": NO_SPEECH,
        "<|endoftext|>": EOT,
    }

    def convert_tokens_to_ids(self, token):
        return self._IDS.get(token, self.unk_token_id)


class FakeDecoderSession:
    """Scripted logits. `sot_logits` is what the decoder emits after consuming
    SOT (the distribution the gate reads); `text_logits` is every later step.
    """

    def __init__(self, sot_logits: np.ndarray, text_logits: np.ndarray,
                 n_layers: int = 1):
        self.sot_logits = sot_logits
        self.text_logits = text_logits
        self.n_layers = n_layers
        self.steps = 0

    def get_inputs(self):
        ins = [
            _IO("input_ids", [1, 1]),
            _IO("attention_mask", [1, 1, 1, 200]),
            _IO("position_ids", [1]),
        ]
        for i in range(self.n_layers):
            ins.append(_IO(f"k_cache_self_{i}_in", [1, 1, 4, 8]))
            ins.append(_IO(f"v_cache_self_{i}_in", [1, 1, 4, 8]))
        return ins

    def get_outputs(self):
        outs = [_IO("logits", [1, VOCAB, 1, 1])]
        for i in range(self.n_layers):
            outs.append(_IO(f"k_cache_self_{i}_out", [1, 1, 4, 8]))
            outs.append(_IO(f"v_cache_self_{i}_out", [1, 1, 4, 8]))
        return outs

    def run(self, _out_names, inputs):
        vec = self.sot_logits if self.steps == 0 else self.text_logits
        self.steps += 1
        logits = vec.reshape(1, VOCAB, 1, 1).astype(np.float32)
        kv = [np.zeros((1, 1, 4, 8), dtype=np.float16) for _ in range(2 * self.n_layers)]
        return [logits] + kv


def _logits(**mass) -> np.ndarray:
    """Logits vector: a flat floor plus the named token boosts."""
    v = np.full(VOCAB, -10.0, dtype=np.float32)
    for token, value in mass.items():
        v[int(token)] = value
    return v


def _noise_sot() -> np.ndarray:
    """The measured shape of hiss at the SOT step: <|nocaptions|> takes almost
    all the mass, every language is a lottery ticket. On the device this read
    P(nospeech)=0.947 with <|ko|> at 0.023."""
    return _logits(**{str(NO_SPEECH): 6.0, str(KO): 2.0, str(EN): 1.6, str(ZH): 1.0})


def _speech_sot(lang=EN) -> np.ndarray:
    """The measured shape of real speech: no-speech mass negligible, one
    language committed to. jfk.wav read P(nospeech)=0.007, P(en)=0.988."""
    return _logits(**{str(lang): 12.0, str(NO_SPEECH): -2.0})


def _text() -> np.ndarray:
    """A confident two-word continuation that then stops."""
    return _logits(**{str(WORD_A): 8.0, str(WORD_B): 7.0, str(EOT): 6.0})


def _gate(**over) -> SpeechGate:
    kw = dict(
        no_speech_id=NO_SPEECH,
        no_speech_threshold=0.75,
        language_prob_threshold=0.10,
        avg_logprob_threshold=-1.0,
        prompt_conditioned=False,
    )
    kw.update(over)
    return SpeechGate(**kw)


def _decode(sot, text=None, gate=None, **kw):
    sess = FakeDecoderSession(sot, text if text is not None else _text())
    stats: dict = {}
    tokens = beam_search(
        sess, cross_kv={}, tokenizer=FakeTokenizer(), beam_size=4,
        max_len=12, speech_gate=gate, stats=stats, **kw
    )
    return tokens, stats


def _is_empty(tokens) -> bool:
    """What the user sees: nothing but the SOT special, so
    `tokenizer.decode(..., skip_special_tokens=True)` yields ""."""
    return [t for t in tokens if t < SOT] == []


# ---------------------------------------------------------------------------
# The defect
# ---------------------------------------------------------------------------


def test_noise_decodes_to_words_without_the_gate():
    """The bug, pinned: ungated, noise does not merely score badly -- it
    produces text. This is the state every shipped Windows dictation hold was
    in, and it must keep failing loudly if someone removes the gate."""
    tokens, _ = _decode(_noise_sot(), gate=None)
    assert not _is_empty(tokens), "fixture no longer reproduces the defect"


def test_noise_in_empty_out():
    tokens, stats = _decode(_noise_sot(), gate=_gate())
    assert _is_empty(tokens)
    assert stats["no_speech"] is True


def test_noise_costs_no_beam_steps():
    """Rejection happens at the SOT step, so the decode is not merely
    discarded -- it never runs. What that saves on the device is whatever
    WINDOWS_DICTATION_MEASURED.md 8.1 clocks as beam-4 decoder p50 (~437 ms);
    what this test asserts is only that the steps do not happen."""
    sess = FakeDecoderSession(_noise_sot(), _text())
    beam_search(sess, cross_kv={}, tokenizer=FakeTokenizer(), beam_size=4,
                max_len=12, speech_gate=_gate())
    assert sess.steps == 4  # the forced prefix only: sot, lang, task, notimestamps


# ---------------------------------------------------------------------------
# It must not fire on speech
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("lang", [EN, ZH, KO])
def test_speech_survives_the_gate(lang):
    """Including Korean: the gate rejects unconfident LANGUAGE CHOICES, not
    the Korean language. A user who dictates in Korean is not the bug."""
    tokens, stats = _decode(_speech_sot(lang), gate=_gate())
    assert not _is_empty(tokens)
    assert not stats.get("no_speech")


def test_quiet_speech_survives():
    """The dangerous case: someone speaking softly. Modelled on the device
    reading for jfk.wav scaled to peak 0.005 (P(nospeech)=0.148, P(en)=0.823)
    -- degraded, still nowhere near either threshold."""
    sot = _logits(**{str(EN): 3.0, str(NO_SPEECH): 1.2})
    tokens, stats = _decode(sot, gate=_gate())
    assert not _is_empty(tokens)
    assert stats["no_speech_prob"] < 0.75
    assert stats["language_prob"] > 0.10


# ---------------------------------------------------------------------------
# The two rules are independent
# ---------------------------------------------------------------------------


def test_language_rule_catches_what_the_no_speech_rule_misses():
    """Measured case: a real mic window read P(nospeech)=0.650 -- under the
    0.75 bar -- with P(language)=0.067, and beam 4 emitted a Korean
    repetition loop that looks_like_junk did not blank."""
    sot = _logits(**{str(NO_SPEECH): 2.2, str(KO): 0.0, str(EN): -0.2})
    ns_only = _gate(language_prob_threshold=None)
    tokens, stats = _decode(sot, gate=ns_only)
    assert not _is_empty(tokens), "fixture must sit UNDER the no-speech bar"
    assert stats["no_speech_prob"] < 0.75

    tokens, stats = _decode(sot, gate=_gate())
    assert _is_empty(tokens)
    assert stats["no_speech_reason"] == "language"


def test_language_rule_spares_a_chunk_the_model_insists_is_speech():
    """The conjunction. P(lang) is read off the SAME softmax as P(nospeech)
    and is NOT renormalised inside the language range, so a chunk can read
    under 0.10 on every individual language token merely because the mass is
    SPLIT -- accented or code-switched speech landing between two languages is
    the ordinary case, not a rare one. Standing alone, rule 2 discarded that
    chunk whole while <|nospeech|> read near zero, i.e. while the model was
    actively asserting this IS speech. Deleting confident speech is worse than
    the mislabelling rule 2 exists to prevent.
    """
    split = _logits(**{str(EN): -1.4, str(ZH): -1.5, str(NO_SPEECH): -5.0})
    tokens, stats = _decode(split, gate=_gate())

    # The fixture must actually sit in the contested band, or this test proves
    # nothing about the rule.
    assert stats["language_prob"] < 0.10, stats["language_prob"]
    assert stats["no_speech_prob"] < 0.05, stats["no_speech_prob"]

    assert not _is_empty(tokens)
    assert not stats.get("no_speech")


def test_the_conjunction_does_not_cost_the_measured_rejection():
    """The other side of the same coin: the real mic window that motivated
    rule 2 read P(nospeech)=0.650, which is ABOVE the corroboration bar, so
    narrowing rule 2 must not hand that case back."""
    sot = _logits(**{str(NO_SPEECH): 2.2, str(KO): 0.0, str(EN): -0.2})
    _, stats = _decode(sot, gate=_gate())

    assert stats["no_speech_prob"] >= 0.50, stats["no_speech_prob"]
    assert stats["no_speech_reason"] == "language"


def test_language_rule_stands_alone_when_there_is_no_no_speech_token():
    """A vocabulary with no <|nospeech|> token offers no second opinion. The
    rule then behaves exactly as it did before the conjunction -- failing
    toward rejection here is the pre-existing contract, not a new decision."""
    gate = _gate(no_speech_id=None)
    split = _logits(**{str(EN): -1.4, str(ZH): -1.5, str(NO_SPEECH): -5.0})
    tokens, stats = _decode(split, gate=gate)

    assert stats.get("no_speech_prob") is None
    assert _is_empty(tokens)
    assert stats["no_speech_reason"] == "language"


def test_each_rule_disables_alone():
    noise = _noise_sot()
    assert _is_empty(_decode(noise, gate=_gate(language_prob_threshold=None))[0])
    assert _is_empty(_decode(noise, gate=_gate(no_speech_threshold=None))[0])
    both_off = _gate(no_speech_threshold=None, language_prob_threshold=None)
    assert not _is_empty(_decode(noise, gate=both_off)[0])


# ---------------------------------------------------------------------------
# The prompt interaction (FLEURS)
# ---------------------------------------------------------------------------


def test_prompt_conditioned_decode_needs_avg_logprob_too():
    """A <|startofprev|> prompt in a different language from the audio
    INFLATES the no-speech logit. Rejecting on that probability alone once
    zeroed every Chinese decode on FLEURS. Confident text under an inflated
    probability must survive."""
    inflated = _logits(**{str(NO_SPEECH): 6.0, str(ZH): 2.0})
    gate = _gate(prompt_conditioned=True)
    tokens, stats = _decode(inflated, gate=gate)
    assert not _is_empty(tokens)
    assert stats["no_speech_prob"] > 0.75  # the naive rule WOULD have fired
    assert stats["avg_logprob"] > -1.0
    assert not stats.get("no_speech")


def test_prompt_conditioned_decode_still_rejects_low_confidence():
    """The other half: noise reciting the prompt's vocabulary is both
    non-speech-shaped AND low-confidence, and still dies."""
    flat = np.full(VOCAB, 0.0, dtype=np.float32)  # ~uniform: avg_logprob ~ -10.8
    tokens, stats = _decode(_noise_sot(), text=flat, gate=_gate(prompt_conditioned=True))
    assert _is_empty(tokens)
    assert stats["avg_logprob"] < -1.0


def test_avg_logprob_ignores_the_bias_trie():
    """avg_logprob is a claim about the ACOUSTICS. If L1's delta leaked into
    it, a large enough lexicon could talk the gate out of firing on the very
    noise that regurgitates the lexicon.

    The trie is free to change WHICH hypothesis wins -- that is its job -- so
    the invariant is not "same number with and without bias", it is "the
    number is the mean raw log-softmax of the tokens that actually won", with
    no delta and no completion bonus anywhere in it.
    """
    from transcription_worker.beam_decode import BiasTrie

    text = _text()
    raw = text - np.log(np.exp(text - text.max()).sum()) - text.max()
    trie = BiasTrie.from_sequences([[WORD_A, WORD_B]], delta=30.0, completion_bonus=5.0)
    for bias in (None, trie):
        tokens, stats = _decode(_speech_sot(), gate=_gate(), bias=bias)
        generated = tokens[4:]  # everything after the forced 4-token prefix
        assert generated, "nothing decoded, the assertion below would be vacuous"
        expected = float(np.mean([raw[t] for t in generated]))
        assert stats["avg_logprob"] == pytest.approx(expected, abs=1e-5)


# ---------------------------------------------------------------------------
# Token resolution
# ---------------------------------------------------------------------------


def test_no_speech_token_resolves_by_name_not_by_id():
    """Measured on the shipped whisper-base tokenizer: asking for
    `<|nospeech|>` returns 50257, which is unk AND <|endoftext|>. A gate that
    accepted that answer would read P(EOT) at the SOT step and fire on
    ordinary speech."""
    assert no_speech_token_id(FakeTokenizer()) == NO_SPEECH


def test_gate_is_inert_when_the_token_is_unknown():
    """A tokenizer with neither spelling disables the no-speech rule rather
    than gating on a wrong id. The language rule is unaffected."""
    gate = _gate(no_speech_id=None)
    assert gate.no_speech_prob(np.zeros(VOCAB, dtype=np.float32)) is None
    assert gate.suspects(None, 0.9) is None
    assert gate.suspects(None, 0.01) == "language"


def test_gate_survives_a_vocab_smaller_than_the_token_id():
    """test_density_ratio.py drives beam_search with a 10-token vocabulary."""
    gate = _gate()
    assert gate.no_speech_prob(np.zeros(10, dtype=np.float32)) is None


# ---------------------------------------------------------------------------
# Nothing changes when no gate is asked for
# ---------------------------------------------------------------------------


def test_ungated_decode_is_byte_identical():
    """The macOS side shares this module. A decode with no gate must produce
    exactly what it produced before the gate existed."""
    plain, _ = _decode(_speech_sot(), gate=None)
    gated, _ = _decode(_speech_sot(), gate=_gate())
    assert plain == gated


def test_forced_language_overrides_the_models_lottery():
    """The host does not send a language today, which is why noise landed on
    Korean rather than on English gibberish. When it does, the beam path now
    honours it -- it used to be greedy-only."""
    tokens, _ = _decode(_noise_sot(), gate=None, forced_language_id=ZH)
    assert tokens[1] == ZH


def test_forced_language_does_not_arm_the_language_rule():
    """Caught on the device, not in review: with <|en|> pinned over real
    Chinese speech the model reads P(en)=0.0023, and the language rule
    rejected the whole utterance as noise. Pinning removes the choice, so the
    reading stops being evidence -- otherwise the host's eventual fix for this
    bug (start sending a language) silently swallows every other language."""
    speaks_chinese = _logits(**{str(ZH): 12.0, str(EN): -1.0, str(NO_SPEECH): -2.0})
    tokens, stats = _decode(speaks_chinese, gate=_gate(), forced_language_id=EN)
    assert not _is_empty(tokens)
    assert stats["language_prob"] < 0.10  # the rule WOULD have fired
    assert stats["language_forced"] is True
    # ...and the no-speech rule is still armed on a pinned decode.
    tokens, stats = _decode(_noise_sot(), gate=_gate(), forced_language_id=EN)
    assert _is_empty(tokens)
    assert stats["no_speech_reason"] == "no_speech"


# The suite this was copied from also pinned prose in an end-to-end gate of
# its own. That gate is not part of Dictation, so the check stays there; the two forced-language
# tests above still own the behaviour here.


# --- want_timestamps: the special-token wall must spare timestamp tokens ----
#
# Regression for the 2026-08-24 finding: prepare()'s `lp[sot_id:] = -inf`
# made every timestamp token unsamplable, so a want_timestamps beam decode
# emitted none, transcribe_timed collapsed each window into one edge-clamped
# span, and the rolling-stable pass committed nothing until the tail flush —
# the saved meeting transcript was silently its final <=30 s. Verified on the
# staged QNN engine before the fix (0 timestamp tokens, beam AND greedy) and
# after it (segment boundaries landing in the real silence gaps).

TS0 = VOCAB          # <|0.00|> — first timestamp id, directly above the fake vocab
TS_VOCAB = VOCAB + 8


class TimestampTokenizer(FakeTokenizer):
    _IDS = dict(FakeTokenizer._IDS)
    _IDS["<|0.00|>"] = TS0


class ScriptedSession(FakeDecoderSession):
    """Per-step scripted logits over the timestamp-extended vocab."""

    def __init__(self, vectors):
        super().__init__(vectors[0], vectors[-1])
        self.vectors = vectors

    def get_outputs(self):
        outs = [_IO("logits", [1, TS_VOCAB, 1, 1])]
        for i in range(self.n_layers):
            outs.append(_IO(f"k_cache_self_{i}_out", [1, 1, 4, 8]))
            outs.append(_IO(f"v_cache_self_{i}_out", [1, 1, 4, 8]))
        return outs

    def run(self, _out_names, inputs):
        vec = self.vectors[min(self.steps, len(self.vectors) - 1)]
        self.steps += 1
        logits = vec.reshape(1, TS_VOCAB, 1, 1).astype(np.float32)
        kv = [np.zeros((1, 1, 4, 8), dtype=np.float16)
              for _ in range(2 * self.n_layers)]
        return [logits] + kv


def _ts_logits(**mass):
    v = np.full(TS_VOCAB, -10.0, dtype=np.float32)
    for token, value in mass.items():
        v[int(token)] = value
    return v


def _ts_profile():
    from transcription_worker.whisper_profile import WhisperProfile
    return WhisperProfile.from_tokenizer(TimestampTokenizer(), model="fake-ts")


def _ts_session():
    # after the 3-token prefix: a step where a timestamp token dominates, a
    # word, another dominant timestamp, then EOT.
    speech = _ts_logits(**{str(EN): 6.0})
    ts_step = _ts_logits(**{str(TS0): 5.0, str(WORD_A): 4.0, str(EOT): 1.0})
    word = _ts_logits(**{str(WORD_A): 5.0, str(EOT): 1.0})
    end = _ts_logits(**{str(EOT): 5.0})
    return ScriptedSession([speech, speech, speech, ts_step, word,
                            _ts_logits(**{str(TS0 + 4): 5.0, str(WORD_B): 4.0,
                                          str(EOT): 1.0}), end])


def test_want_timestamps_beam_emits_timestamp_tokens():
    out = beam_search(_ts_session(), {}, beam_size=2, profile=_ts_profile(),
                      want_timestamps=True)
    ts = [t for t in out if t >= TS0]
    assert ts, f"no timestamp token in {out}"
    assert WORD_A in out


def test_without_want_timestamps_the_wall_still_holds():
    out = beam_search(_ts_session(), {}, beam_size=2, profile=_ts_profile(),
                      want_timestamps=False)
    # <|notimestamps|> rides in the forced prefix here — the wall's job is
    # only that no timestamp token is SAMPLED.
    assert not [t for t in out if t >= TS0], out
    assert WORD_A in out
