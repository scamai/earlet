"""Integration tests for L2 density-ratio rescoring through beam_search()
itself (not just NgramLM in isolation) -- a FakeDecoderSession stands in for
the real ort.InferenceSession.

Run: .venv/Scripts/python.exe -m pytest transcription_worker/test_density_ratio.py
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.beam_decode import FORCED_PREFIX, DensityRatio, beam_search  # noqa: E402
from transcription_worker.ngram_lm import NgramLM  # noqa: E402

VOCAB = 10  # well under real SOT(50258)/EOT(50257), so specials never trigger


class _IO:
    def __init__(self, name, shape):
        self.name = name
        self.shape = shape


class FakeDecoderSession:
    """Constant acoustic preference for `favorite`, regardless of context --
    isolates whatever effect density_ratio has on beam_search's choice."""

    def __init__(self, vocab: int = VOCAB, n_layers: int = 1, favorite: int = 3):
        self.vocab = vocab
        self.n_layers = n_layers
        self.favorite = favorite

    def get_inputs(self):
        ins = [_IO("input_ids", [1, 1]), _IO("attention_mask", [1, 1, 1, 200]), _IO("position_ids", [1])]
        for i in range(self.n_layers):
            ins.append(_IO(f"k_cache_self_{i}_in", (1, 1, 1, 1)))
            ins.append(_IO(f"v_cache_self_{i}_in", (1, 1, 1, 1)))
        return ins

    def get_outputs(self):
        outs = [_IO("logits", [1, self.vocab, 1, 1])]
        for i in range(self.n_layers):
            outs.append(_IO(f"k_cache_self_{i}_out", (1, 1, 1, 1)))
            outs.append(_IO(f"v_cache_self_{i}_out", (1, 1, 1, 1)))
        return outs

    def run(self, _fetches, _inputs):
        logits = np.full((1, self.vocab, 1, 1), -3.0, dtype=np.float32)
        logits[0, self.favorite, 0, 0] = 3.0
        results = [logits]
        for _ in range(self.n_layers):
            results.append(np.zeros((1, 1, 1, 1), dtype=np.float16))
            results.append(np.zeros((1, 1, 1, 1), dtype=np.float16))
        return results


def _content_tokens(tokens: list[int]) -> list[int]:
    return tokens[len(FORCED_PREFIX) :]


# --- baseline / regression safety -------------------------------------------


def test_beam_search_unaffected_when_density_ratio_omitted():
    sess = FakeDecoderSession(favorite=3)
    tokens = beam_search(sess, cross_kv={}, beam_size=1, max_len=6)
    assert _content_tokens(tokens)[0] == 3


def test_beam_search_unaffected_when_density_ratio_explicitly_none():
    sess = FakeDecoderSession(favorite=3)
    tokens = beam_search(sess, cross_kv={}, beam_size=1, density_ratio=None, max_len=6)
    assert _content_tokens(tokens)[0] == 3


# --- density ratio actually influences the choice ---------------------------


def test_strong_density_ratio_overrides_acoustic_preference():
    sess = FakeDecoderSession(favorite=3)

    p_you = NgramLM(order=2, vocab_size=VOCAB)
    p_you.fit_hard([[7]] * 50)  # heavily favors token 7
    p_ilm = NgramLM(order=2, vocab_size=VOCAB)  # unfit -> ~uniform floor

    ratio = DensityRatio(p_you, p_ilm, lam=1.0, mu=20.0)  # large mu: L2 should dominate
    tokens = beam_search(sess, cross_kv={}, beam_size=1, density_ratio=ratio, max_len=6)
    assert _content_tokens(tokens)[0] == 7


def test_weak_density_ratio_does_not_override_strong_acoustic_signal():
    sess = FakeDecoderSession(favorite=3)

    p_you = NgramLM(order=2, vocab_size=VOCAB)
    p_you.fit_hard([[7]] * 2)  # weak signal
    p_ilm = NgramLM(order=2, vocab_size=VOCAB)

    ratio = DensityRatio(p_you, p_ilm, lam=1.0, mu=0.01)  # negligible mu
    tokens = beam_search(sess, cross_kv={}, beam_size=1, density_ratio=ratio, max_len=6)
    assert _content_tokens(tokens)[0] == 3


# --- combined L1 + L2 ---------------------------------------------------


def test_l1_and_l2_together_do_not_crash():
    from transcription_worker.beam_decode import BiasTrie

    sess = FakeDecoderSession(favorite=3)
    bias = BiasTrie.from_sequences([[5, 6]], delta=2.0, completion_bonus=1.0)
    p_you = NgramLM(order=2, vocab_size=VOCAB)
    p_you.fit_hard([[7]] * 10)
    p_ilm = NgramLM(order=2, vocab_size=VOCAB)
    ratio = DensityRatio(p_you, p_ilm, lam=1.0, mu=1.0)

    tokens = beam_search(sess, cross_kv={}, beam_size=2, bias=bias, density_ratio=ratio, max_len=8)
    assert len(_content_tokens(tokens)) > 0


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-v"]))


# --- vocabulary agreement ----------------------------------------------------


def test_density_ratio_refuses_two_lms_of_different_vocabularies():
    """P_you and P_ilm index the SAME logits vector, so a width disagreement
    silently adds token i of one model to token i of another.

    NgramLM.save/load persist vocab_size inside the artifact, which means the
    shipped p_ilm.json.gz is stamped at whatever width it was distilled at. A
    P_you built fresh against a different checkpoint would not line up. Today
    both come from ngram_lm's default and are always equal — this is the
    tripwire for the day they are not.
    """
    import pytest

    you = NgramLM(order=2, vocab_size=VOCAB)
    ilm = NgramLM(order=2, vocab_size=VOCAB + 1)

    with pytest.raises(ValueError, match="disagree about the vocabulary"):
        DensityRatio(you, ilm)

    # Equal widths stay silent, which is every call this ships with today.
    DensityRatio(you, NgramLM(order=2, vocab_size=VOCAB))
