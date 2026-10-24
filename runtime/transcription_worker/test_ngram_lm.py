"""Pure-python unit tests for the Stupid Backoff n-gram LM (no ONNX / NPU).

Run: .venv/Scripts/python.exe -m pytest transcription_worker/test_ngram_lm.py
"""

from __future__ import annotations

import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.ngram_lm import NgramLM  # noqa: E402

VOCAB = 20  # small vocab for fast/legible tests


def make_lm(order=3, backoff=0.4, vocab_size=VOCAB) -> NgramLM:
    return NgramLM(order=order, backoff=backoff, vocab_size=vocab_size)


# --- basic shape / floor ----------------------------------------------------


def test_empty_lm_returns_finite_scores_for_any_context():
    lm = make_lm()
    vec = lm.score_vector((1, 2, 3))
    assert vec.shape == (VOCAB,)
    assert np.all(np.isfinite(vec))


def test_score_vector_length_matches_vocab_size():
    lm = make_lm(vocab_size=37)
    assert lm.score_vector(()).shape == (37,)


# --- hard fit / backoff behavior ---------------------------------------------


def test_seen_continuation_scores_higher_than_unseen():
    lm = make_lm(order=3)
    # "1 2 -> 5" seen many times; "1 2 -> 6" never seen.
    lm.fit_hard([[1, 2, 5]] * 10)
    vec = lm.score_vector((1, 2))
    assert vec[5] > vec[6]


def test_unseen_context_backs_off_toward_unigram_shape():
    lm = make_lm(order=3, backoff=0.4)
    lm.fit_hard([[1, 2, 5]] * 10 + [[9, 9, 9, 9, 9, 3]] * 5)
    # context (7, 8) was never observed at any order -> should fall all the
    # way back to unigram + two backoff penalties (log(backoff) applied twice:
    # order-3 -> order-2 -> order-1).
    backed_off = lm.score_vector((7, 8))
    direct_unigram = lm._unigram_log
    np.testing.assert_allclose(backed_off, direct_unigram + 2 * np.log(0.4))


def test_weight_scales_hard_fit_strength():
    lm_light = make_lm(order=2)
    lm_heavy = make_lm(order=2)
    lm_light.fit_hard([[1, 5]], weights=[1.0])
    lm_heavy.fit_hard([[1, 5]], weights=[1.0])
    # add a competing continuation with different weights
    lm_light.fit_hard([[1, 6]], weights=[1.0])
    lm_heavy.fit_hard([[1, 6]], weights=[5.0])
    # heavier-weighted competitor should close the gap on token 5
    gap_light = lm_light.score_vector((1,))[5] - lm_light.score_vector((1,))[6]
    gap_heavy = lm_heavy.score_vector((1,))[5] - lm_heavy.score_vector((1,))[6]
    assert gap_heavy < gap_light


def test_multiple_sequences_accumulate_counts():
    lm = make_lm(order=2)
    lm.fit_hard([[1, 5], [1, 5], [1, 6]])
    vec = lm.score_vector((1,))
    assert vec[5] > vec[6]


# --- soft fit (ILM distillation path) ----------------------------------------


def test_soft_fit_biases_toward_high_probability_tokens():
    lm = make_lm(order=2)
    probs = np.full(VOCAB, 1e-6, dtype=np.float64)
    probs[3] = 0.9
    probs[4] = 0.05
    probs /= probs.sum()
    lm.fit_soft(context=(1,), prob_vector=probs, top_k=5, weight=1.0)
    vec = lm.score_vector((1,))
    assert vec[3] > vec[4]


def test_soft_fit_top_k_excludes_long_tail():
    lm = make_lm(order=2)
    probs = np.full(VOCAB, 1.0 / VOCAB, dtype=np.float64)
    probs[0] = 0.5
    probs /= probs.sum()
    lm.fit_soft(context=(1,), prob_vector=probs, top_k=1, weight=1.0)
    # only token 0 (the single top_k=1 pick) should have a direct count at
    # this context -- everything else still backs off to unigram unchanged.
    counts = lm._counts[2][(1,)]
    assert set(counts.keys()) == {0}


def test_soft_fit_weight_scales_contribution():
    lm = make_lm(order=2)
    probs = np.zeros(VOCAB, dtype=np.float64)
    probs[2] = 1.0
    lm.fit_soft(context=(1,), prob_vector=probs, top_k=1, weight=3.0)
    assert lm._counts[2][(1,)][2] == 3.0


# --- persistence --------------------------------------------------------


def test_save_load_roundtrip_preserves_scores():
    lm = make_lm(order=3)
    lm.fit_hard([[1, 2, 5], [1, 2, 5], [9, 9, 3]])
    with tempfile.TemporaryDirectory() as d:
        path = Path(d) / "lm.json"
        lm.save(path)
        loaded = NgramLM.load(path)
    for ctx in [(), (1,), (1, 2), (7, 8)]:
        np.testing.assert_allclose(lm.score_vector(ctx), loaded.score_vector(ctx))
    assert loaded.order == lm.order
    assert loaded.backoff == lm.backoff
    assert loaded.vocab_size == lm.vocab_size


def test_save_load_roundtrip_gzip():
    lm = make_lm(order=3)
    lm.fit_hard([[1, 2, 5], [1, 2, 5], [9, 9, 3]])
    with tempfile.TemporaryDirectory() as d:
        plain = Path(d) / "lm.json"
        gz = Path(d) / "lm.json.gz"
        lm.save(plain)
        lm.save(gz)
        assert gz.stat().st_size < plain.stat().st_size
        loaded = NgramLM.load(gz)
    for ctx in [(), (1,), (1, 2)]:
        np.testing.assert_allclose(lm.score_vector(ctx), loaded.score_vector(ctx))


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-v"]))
