"""loop-v2 P6 -- the decoder side of the three additive worker ops.

  * ``fired[]``: `beam_search` reports, per surviving hypothesis, the bias
    phrases that COMPLETED on its path. The claim is pinned against the one
    thing it must equal -- a replay of that hypothesis' generated tokens
    through `BiasState.advance` -- not against a hand-written expectation.
  * `forced_logprobs`: teacher forcing on the same `run_step` beam search
    uses; equals a direct log-softmax read at every step on a scripted
    session.
  * `forced_window` / `forced_delta`: the verifier arithmetic, checked
    against numbers worked by hand on a deterministic logits table (the
    -ln 2 / -ln 3 / -40 fixture below -- see `_table()`).
These model-free tests use a deterministic logits table. They do not verify
real-model inference, speech accuracy or a hardware backend.

Run: python -m pytest transcription_worker/test_learning_ops.py
"""

from __future__ import annotations

import math
import sys
import types
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker import beam_decode  # noqa: E402
from transcription_worker.beam_decode import (  # noqa: E402
    BiasTrie,
    beam_search,
    forced_delta,
    forced_logprobs,
    forced_window,
)
from transcription_worker.test_beam_decode import (  # noqa: E402
    EN,
    EOT,
    VOCAB,
    WORD_A,
    WORD_B,
    FakeDecoderSession,
    FakeTokenizer,
    _logits,
    _speech_sot,
)
from transcription_worker.whisper_profile import WhisperProfile  # noqa: E402

WORD_C = 300
PREFIX_LEN = 4
LN2, LN3 = math.log(2.0), math.log(3.0)


def _replay_fired(trie: BiasTrie, generated: list[int]) -> list[int]:
    """`bias_hf.decode_worker`'s rule: replay the path, read `completed`."""
    state = trie.initial_state()
    for t in generated:
        state, _ = state.advance(t)
    return sorted(state.completed)


def _search(text_logits, bias, stats):
    sess = FakeDecoderSession(_speech_sot(), text_logits)
    return beam_search(sess, cross_kv={}, tokenizer=FakeTokenizer(), beam_size=4,
                       max_len=12, bias=bias, stats=stats)


# ---------------------------------------------------------------------------
# fired[]
# ---------------------------------------------------------------------------

# A and B near-tied, C behind, EOT in reach: several distinct paths survive.
_TIE = _logits(**{str(WORD_A): 8.0, str(WORD_B): 7.9, str(WORD_C): 5.0, str(EOT): 6.0})


def _trie(*seqs, delta=0.0, bonus=0.0) -> BiasTrie:
    return BiasTrie.from_sequences([list(s) for s in seqs], delta=delta,
                                   completion_bonus=bonus)


def test_fired_equals_a_replay_of_each_hypothesis_through_bias_state():
    trie = _trie([WORD_A, WORD_B], [WORD_B], [WORD_C, WORD_A], delta=1.5)
    stats: dict = {}
    winner = _search(_TIE, trie, stats)
    assert stats["nbest"][0]["tokens"] == winner
    assert len(stats["nbest"]) >= 2
    for hyp in stats["nbest"]:
        gen = [t for t in hyp["tokens"][PREFIX_LEN:] if t != EOT]
        ids = [pid for pid, _pos, _n in hyp["fired"]]
        assert sorted(ids) == _replay_fired(trie, gen), hyp
        assert len(ids) == len(set(ids)), "a phrase fires once per hypothesis"


def test_fired_carries_position_and_match_length():
    # "A B" is a two-token phrase; make that path the clear winner.
    logits = _logits(**{str(WORD_A): 9.0, str(WORD_B): 8.0, str(EOT): 5.0})
    trie = _trie([WORD_A, WORD_B], delta=2.0)
    stats: dict = {}
    tokens = _search(logits, trie, stats)
    gen = tokens[PREFIX_LEN:]
    assert gen[:2] == [WORD_A, WORD_B]
    hits = stats["nbest"][0]["fired"]
    assert hits[0] == (0, 1, 2), "phrase 0 completed at generated index 1 over 2 tokens"
    # Repeat completions (A B A B ...) are not re-reported.
    assert [h[0] for h in hits].count(0) == 1


def test_fired_is_none_without_a_trie_and_empty_when_nothing_completed():
    stats: dict = {}
    _search(_TIE, None, stats)
    assert all(h["fired"] is None for h in stats["nbest"])
    stats = {}
    _search(_TIE, _trie([WORD_C, WORD_C, WORD_C]), stats)
    assert all(h["fired"] == [] for h in stats["nbest"])


def test_fired_bookkeeping_does_not_change_the_decode():
    trie = _trie([WORD_A, WORD_B], [WORD_B], delta=1.5, bonus=0.5)
    assert _search(_TIE, trie, None) == _search(_TIE, trie, {})


def test_bias_state_fired_is_the_history_of_completed():
    trie = _trie([WORD_A, WORD_B], [WORD_B])
    s = trie.initial_state()
    s, _ = s.advance(WORD_A)
    assert s.fired == () and s.completed == frozenset()
    s, _ = s.advance(WORD_B)  # completes both "A B" (len 2) and "B" (len 1)
    assert set(s.completed) == {0, 1}
    assert sorted(s.fired) == [(0, 2), (1, 1)]
    s, _ = s.advance(WORD_B)  # repeat: retires the boost, records nothing
    assert len(s.fired) == 2


# ---------------------------------------------------------------------------
# forced_logprobs on a deterministic table
# ---------------------------------------------------------------------------


class TableSession(FakeDecoderSession):
    """Logits keyed by DECODER POSITION (not call count), so two forced walks
    over one session read the same table. Position 0 is the SOT step; text
    position k is decoder position PREFIX_LEN - 1 + k; the last row repeats."""

    def __init__(self, rows: list[np.ndarray]):
        super().__init__(_speech_sot(), rows[-1])
        self.rows = rows
        self.calls: list[int] = []

    def run(self, _out_names, inputs):
        n = int(inputs["position_ids"][0])
        self.calls.append(n)
        vec = self.sot_logits if n == 0 else self.rows[min(max(n - (PREFIX_LEN - 1), 0),
                                                            len(self.rows) - 1)]
        logits = vec.reshape(1, VOCAB, 1, 1).astype(np.float32)
        kv = [np.zeros((1, 1, 4, 8), dtype=np.float16) for _ in range(2 * self.n_layers)]
        return [logits] + kv


def _table() -> list[np.ndarray]:
    """Three text positions whose log-softmax values are exact by hand:
    a mass of 30 over a floor of -10 on a 50k vocab makes the floor's
    contribution ~2e-13, so
        pos 0: {A, B}        -> log p(A) = log p(B) = -ln 2
        pos 1: {A, C, EOT}   -> log p(A) = log p(C) = -ln 3 ; log p(B) = -40 - ln 3
        pos 2: {EOT}         -> log p(EOT) = 0
    """
    return [
        _logits(**{str(WORD_A): 30.0, str(WORD_B): 30.0}),
        _logits(**{str(WORD_A): 30.0, str(WORD_C): 30.0, str(EOT): 30.0}),
        _logits(**{str(EOT): 30.0}),
    ]


def _profile() -> WhisperProfile:
    return WhisperProfile.from_tokenizer(FakeTokenizer(), model="fake")


def test_forced_logprobs_equals_a_direct_log_softmax_read():
    sess = TableSession(_table())
    ids = [WORD_A, WORD_A]
    lp = forced_logprobs(sess, {}, ids, profile=_profile(), forced_language_id=EN, max_len=32)
    assert lp.shape == (3,), "one entry per token + EOT"
    rows = _table()
    expect = [beam_decode._log_softmax(rows[k])[t] for k, t in enumerate(ids + [EOT])]
    assert lp == pytest.approx(expect, abs=1e-6)
    # The walk is teacher-forced over prefix + ids: positions 0..len-2.
    assert sess.calls == list(range(PREFIX_LEN + len(ids)))


def test_forced_logprobs_pins_the_language_slot():
    sess = TableSession(_table())
    forced_logprobs(sess, {}, [WORD_A], profile=_profile(), forced_language_id=EN, max_len=32)
    # Without a pin the slot goes to the SOT-step argmax, which _speech_sot
    # makes <|en|> as well -- so pin a different language and check it sticks
    # by watching the sequence the session was fed.
    fed: list[int] = []
    orig_run = sess.run

    def spy(out, inputs):
        fed.append(int(inputs["input_ids"][0, 0]))
        return orig_run(out, inputs)

    sess.run = spy
    forced_logprobs(sess, {}, [WORD_A], profile=_profile(), forced_language_id=EN + 5, max_len=32)
    assert fed[1] == EN + 5


def test_forced_logprobs_refuses_a_string_past_the_context():
    with pytest.raises(ValueError, match="exceeds decode context"):
        forced_logprobs(TableSession(_table()), {}, [WORD_A] * 10, profile=_profile(),
                        forced_language_id=EN, max_len=12)


# ---------------------------------------------------------------------------
# window + sums: the verifier arithmetic, by hand
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("orig,corr,want", [
    ([1, 2, 3], [1, 2, 3], ((0, 0), (0, 0))),            # identical: empty windows
    ([1, 2, 3], [1, 5, 3], ((1, 3), (1, 3))),            # substitution + 1 right ctx
    ([1, 2, 3], [1, 2, 3, 4], ((3, 4), (3, 5))),         # append: ctx reaches EOT on both
    ([1, 2, 3, 4], [1, 4], ((1, 4), (1, 2))),            # deletion
    ([7], [8], ((0, 2), (0, 2))),                         # whole string; EOT included
    ([1, 2, 3, 4, 5], [1, 9, 3, 4, 8], ((1, 6), (1, 6))),  # two edits: one window spans both
])
def test_forced_window_is_the_verifier_window(orig, corr, want):
    assert forced_window(orig, corr) == want


def test_forced_delta_hand_computed_on_the_table():
    sess = TableSession(_table())
    prof = _profile()
    orig, corr = [WORD_A, WORD_A], [WORD_A, WORD_B]
    lo = forced_logprobs(sess, {}, orig, profile=prof, forced_language_id=EN, max_len=32)
    lc = forced_logprobs(sess, {}, corr, profile=prof, forced_language_id=EN, max_len=32)
    # fp32 log-softmax: 1e-4 absolute is far below the verifier's own 1e-2.
    assert lo == pytest.approx([-LN2, -LN3, 0.0], abs=1e-4)
    assert lc == pytest.approx([-LN2, -40.0 - LN3, 0.0], abs=1e-4)
    d = forced_delta(lo, lc, orig, corr)
    # Diff at token 1; window [1,2) + 1 right context = [1,3) on both (EOT in).
    assert d["window"] == {"orig": [1, 3], "corr": [1, 3], "len": 2}
    assert d["sum_orig"] == pytest.approx(-LN3, abs=1e-4)
    assert d["sum_corr"] == pytest.approx(-40.0 - LN3, abs=1e-4)
    assert d["acoustic"] == pytest.approx(-40.0, abs=1e-4)
    assert d["full_orig"] == pytest.approx(-LN2 - LN3, abs=1e-4)
    assert d["full_corr"] == pytest.approx(-LN2 - 40.0 - LN3, abs=1e-4)
    assert d["tokens"] == {"orig": 2, "corr": 2}
    # A distractor the table likes as much as orig scores delta 0 exactly.
    ld = forced_logprobs(sess, {}, [WORD_A, WORD_C], profile=prof, forced_language_id=EN, max_len=32)
    assert forced_delta(lo, ld, orig, [WORD_A, WORD_C])["acoustic"] == pytest.approx(0.0, abs=1e-4)


def test_forced_delta_identical_strings_is_zero_over_an_empty_window():
    lp = np.array([-1.0, -2.0, -0.5])
    d = forced_delta(lp, lp, [1, 2], [1, 2])
    assert d["acoustic"] == 0.0 and d["window"]["len"] == 0
