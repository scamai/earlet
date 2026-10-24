"""Pure-python unit tests for the L1 bias trie (no ONNX / NPU required).

Run: .venv/Scripts/python.exe -m pytest transcription_worker/test_bias_trie.py
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

# The old module-level `pytest.importorskip("torch")` was guarding the wrong
# module. Its reason said "beam_decode scores candidates with torch tensors" --
# beam_decode imports numpy and nothing else; the torch dependency lives in
# `trie_decode`, which only the repair tests below reach, and they import it
# lazily inside the test body. So the blanket skip silently disabled 18 tests
# that need no torch at all, on every machine without it. Verified by bypassing
# the skip: 18 passed, and only the 5 trie_decode tests failed to import.
# Scope it to those 5 instead.
requires_torch = pytest.mark.skipif(
    importlib.util.find_spec("torch") is None,
    reason="trie_decode imports torch; it is a device-lane dependency",
)

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.beam_decode import BiasTrie, phrase_variants  # noqa: E402

DELTA = 5.0
BONUS = 3.0


def make_trie(seqs):
    return BiasTrie.from_sequences(seqs, delta=DELTA, completion_bonus=BONUS)


class FakeTokenizer:
    """Maps each character to its ordinal, so tests can also exercise the
    tokenizer-driven constructor path."""

    def encode(self, text, add_special_tokens=False):
        assert add_special_tokens is False
        return [ord(c) for c in text]


# --- construction -----------------------------------------------------------


def test_phrase_variants_cover_case_and_leading_space():
    assert phrase_variants("Trovioo") == [
        "Trovioo",
        " Trovioo",
        "trovioo",
        " trovioo",
    ]
    # already-lowercase phrase: no duplicate variants
    assert phrase_variants("abc") == ["abc", " abc"]


def test_constructor_builds_trie_from_tokenizer():
    trie = BiasTrie(["ab"], FakeTokenizer(), delta=DELTA, completion_bonus=BONUS)
    state = trie.initial_state()
    # variants: "ab", " ab" -> root children are 'a' and ' '
    assert state.candidate_tokens() == {ord("a"), ord(" ")}


def test_empty_sequences_ignored():
    trie = make_trie([[], [1, 2]])
    assert trie.initial_state().candidate_tokens() == {1}


# --- candidate tokens (start / extend) --------------------------------------


def test_root_candidates_are_phrase_first_tokens():
    trie = make_trie([[10, 11, 12], [20, 21]])
    assert trie.initial_state().candidate_tokens() == {10, 20}


def test_extend_candidates_include_active_match_and_root():
    trie = make_trie([[10, 11, 12], [20, 21]])
    state, _ = trie.initial_state().advance(10)
    # can extend 10 -> 11, or start fresh with 10 or 20
    assert state.candidate_tokens() == {10, 11, 20}


# --- completion --------------------------------------------------------------


def test_first_full_match_gets_bonus_and_keeps_boost():
    trie = make_trie([[10, 11, 12]])
    s = trie.initial_state()
    s, adj = s.advance(10)
    assert adj == 0.0 and s.pending == DELTA
    s, adj = s.advance(11)
    assert adj == 0.0 and s.pending == 2 * DELTA
    s, adj = s.advance(12)
    assert adj == BONUS  # bonus paid, accumulated boost confirmed (kept)
    assert s.pending == 0.0
    assert s.nodes == ()  # nothing left in progress


def test_repeat_completion_retires_boost_and_pays_no_bonus():
    # a phrase is confirmed at most once per beam: the second completion gets
    # its guidance boost retired and no bonus (anti phrase-spam).
    trie = make_trie([[10, 11]])
    s = trie.initial_state()
    s, adj = s.advance(10)
    s, adj = s.advance(11)
    assert adj == BONUS  # first completion: confirmed
    s, adj = s.advance(10)
    assert s.pending == DELTA
    s, adj = s.advance(11)
    assert adj == -2 * DELTA  # repeat: retire both boosts, no bonus
    assert s.pending == 0.0


def test_variant_of_confirmed_phrase_counts_as_repeat():
    # all variants of a phrase share one phrase id: after " RetinaFace" is
    # confirmed, completing the no-leading-space variant is a repeat.
    trie = BiasTrie(["ab"], FakeTokenizer(), delta=DELTA, completion_bonus=BONUS)
    s = trie.initial_state()
    for t in " ab":
        s, adj = s.advance(ord(t))
    assert adj == BONUS  # first completion via the leading-space variant
    s, adj = s.advance(ord("a"))
    s, adj = s.advance(ord("b"))
    assert adj == -2 * DELTA  # variant repeat: retired, no bonus


def test_single_token_phrase_completes_immediately():
    trie = make_trie([[42]])
    s, adj = trie.initial_state().advance(42)
    assert adj == BONUS
    assert s.pending == 0.0


# --- rollback -----------------------------------------------------------------


def test_dead_partial_match_rolls_back_accumulated_boost():
    trie = make_trie([[10, 11, 12]])
    s = trie.initial_state()
    s, _ = s.advance(10)
    s, _ = s.advance(11)
    assert s.pending == 2 * DELTA
    s, adj = s.advance(99)  # not in trie: match dies
    assert adj == -2 * DELTA  # rollback of everything unconfirmed
    assert s.pending == 0.0 and s.nodes == ()


def test_no_rollback_when_nothing_pending():
    trie = make_trie([[10, 11]])
    s, adj = trie.initial_state().advance(99)
    assert adj == 0.0 and s.pending == 0.0


def test_dying_match_that_restarts_at_root_keeps_only_the_live_boost():
    # in [10, 11] then token 10 again: the depth-1 match has no child 10 so it
    # DIES, while 10 starts a fresh match at root (also depth 1).
    #
    # This asserted adj == 0.0 and pending == 2 * DELTA -- keeping the dead
    # match's boost alongside the live one, because rollback only fired when
    # EVERY match died and here one survives. That over-credit is the bug: with
    # a real lexicon the root offers a phrase-initial token for almost any word,
    # so a match nearly always "survives", pending grows every step without
    # bound, EOT (never boosted) stops being competitive, and decode runs to the
    # length cap and emits a repetition loop the junk filter blanks. Measured on
    # the Windows/QNN copy of this engine with a 98-phrase lexicon: 26.1% of
    # clips came back EMPTY, all of which greedy transcribed correctly.
    #
    # Only one token of a phrase is matched here, so only one delta is earned.
    trie = make_trie([[10, 11]])
    s, _ = trie.initial_state().advance(10)
    s, adj = s.advance(10)
    assert adj == -DELTA  # the dead match's boost is handed back
    assert s.pending == DELTA  # the live depth-1 match keeps its own
    s, adj = s.advance(11)  # completes the fresh match (first completion)
    assert adj == BONUS and s.pending == 0.0


def test_pending_is_bounded_by_longest_phrase_not_by_lexicon_size():
    """Unconfirmed boost must not grow with vocabulary size.

    A lexicon where nearly every token restarts some match is the shape a real
    user vocabulary approximates; that is what drove pending to infinity.
    """
    trie = make_trie([[t] for t in range(100, 140)])
    s = trie.initial_state()
    for t in range(100, 140):
        s, _ = s.advance(t)
        assert s.pending <= DELTA + 1e-9, f"pending {s.pending} grew at token {t}"

    trie = make_trie([[10, 11, 12]] + [[t] for t in range(200, 260)])
    s = trie.initial_state()
    for t in (10, 11):
        s, _ = s.advance(t)
    assert s.pending == 2 * DELTA
    for t in range(200, 260):
        s, _ = s.advance(t)
        assert s.pending <= 3 * DELTA + 1e-9

def test_shared_prefix_tracks_single_state():
    trie = make_trie([[10, 11], [10, 12]])
    s, _ = trie.initial_state().advance(10)
    assert len(s.nodes) == 1  # shared trie node, not two states
    assert s.candidate_tokens() == {10, 11, 12}


def test_phrase_that_is_prefix_of_another_completes_then_continues():
    trie = make_trie([[10, 11], [10, 11, 12]])
    s = trie.initial_state()
    s, _ = s.advance(10)
    s, adj = s.advance(11)
    assert adj == BONUS  # short phrase completed and confirmed
    assert s.pending == 0.0
    assert 12 in s.candidate_tokens()  # longer phrase still extendable
    s, adj = s.advance(12)
    assert adj == BONUS  # longer phrase is a distinct terminal: also confirmed


# --- immutability (beam forking) ----------------------------------------------


def test_advance_is_pure_so_forked_beams_stay_independent():
    trie = make_trie([[10, 11, 12]])
    s0 = trie.initial_state()
    s1, _ = s0.advance(10)
    # fork: two children advance differently from the same parent state
    a, adj_a = s1.advance(11)
    b, adj_b = s1.advance(99)
    assert s1.pending == DELTA  # parent untouched
    assert a.pending == 2 * DELTA and adj_a == 0.0
    assert b.pending == 0.0 and adj_b == -DELTA


# --- per-phrase delta ------------------------------------------------------


def test_uniform_phrase_deltas_reproduce_the_scalar_path():
    """The acceptance test for per-phrase δ, and the only one that matters.

    Setting every phrase to the trie's own δ must leave every boost and every
    refund exactly where they were. A per-term implementation that cannot do
    this has broken the accrual/refund invariant somewhere -- and it breaks in a
    flattering direction, because an under-refunded dead match keeps score.
    Prototyping this in the research module produced exactly that bug and it
    cost 3.2 points, visible only because the uniform control stopped matching.
    """
    trie = make_trie([[1, 2, 3], [1, 4]])
    state = trie.initial_state()
    before = [(t, trie.delta_for_token(state.nodes, t)) for t in state.candidate_tokens()]

    trie.set_phrase_deltas({0: DELTA, 1: DELTA})
    after = [(t, trie.delta_for_token(state.nodes, t)) for t in state.candidate_tokens()]
    assert before == after

    # and one step in, where a match is live rather than at the root
    advanced, _ = state.advance(1)
    assert all(trie.delta_for_token(advanced.nodes, t) == DELTA
               for t in advanced.candidate_tokens())


def test_per_phrase_delta_boosts_a_shared_prefix_by_the_neediest_phrase():
    """Token 1 leads to both phrases, so it must carry the larger δ.

    Resolving δ only at the terminal node would boost the shared prefix at the
    smaller value and starve the phrase that needed the help before it ever got
    far enough to be identified.
    """
    trie = make_trie([[1, 2, 3], [1, 4]])
    trie.set_phrase_deltas({0: 1.0, 1: 3.0})
    state = trie.initial_state()

    assert trie.delta_for_token(state.nodes, 1) == 3.0

    # once the paths separate, each continuation gets its own phrase's delta
    advanced, _ = state.advance(1)
    assert trie.delta_for_token(advanced.nodes, 2) == 1.0
    assert trie.delta_for_token(advanced.nodes, 4) == 3.0


def test_per_phrase_delta_refund_matches_the_boost_that_was_charged():
    """A dying partial match must give back exactly what it was given.

    `advance` accrues `pending` through `delta_for_token`, the same call the
    decoder uses to charge the logprob, so the two cannot drift apart.
    """
    trie = make_trie([[1, 2, 3], [1, 4]])
    trie.set_phrase_deltas({0: 1.0, 1: 3.0})
    state = trie.initial_state()

    charged = trie.delta_for_token(state.nodes, 1)
    live, adjustment = state.advance(1)
    assert adjustment == 0.0
    assert live.pending == charged

    # token 9 is on no path: every partial match dies and the boost is refunded
    dead, refund = live.advance(9)
    assert refund == -charged
    assert dead.pending == 0.0


def test_phrase_deltas_default_to_the_scalar_for_unlisted_phrases():
    trie = make_trie([[1, 2, 3], [1, 4]])
    trie.set_phrase_deltas({1: 3.0})  # phrase 0 unlisted
    state = trie.initial_state()
    advanced, _ = state.advance(1)
    assert trie.delta_for_token(advanced.nodes, 2) == DELTA


# --- seam repair -----------------------------------------------------------


@requires_torch
def test_repair_substitutes_only_segments_that_recovered_a_missing_term():
    from transcription_worker.trie_decode import _repair_from_offset_pass

    main = [{"start": 0.0, "end": 30.0, "text": "the build went out"},
            {"start": 30.0, "end": 60.0, "text": "and Nemo shipped"}]
    repair = [{"start": 10.0, "end": 40.0, "text": "and Knemo shipped"}]

    out, repaired = _repair_from_offset_pass(main, repair, {"Knemo"}, 15.0)
    assert repaired == {"Knemo"}
    assert [s["text"] for s in out] == ["the build went out", "and Knemo shipped"]


@requires_torch
def test_repair_is_a_no_op_when_nothing_was_missing():
    from transcription_worker.trie_decode import _repair_from_offset_pass

    main = [{"start": 0.0, "end": 30.0, "text": "the build went out"}]
    repair = [{"start": 0.0, "end": 30.0, "text": "something else entirely"}]

    out, repaired = _repair_from_offset_pass(main, repair, set(), 15.0)
    assert repaired == set()
    assert out is main or [s["text"] for s in out] == ["the build went out"]


@requires_torch
def test_repair_ignores_a_pass_that_did_not_find_the_missing_term():
    """The offset pass may simply fail too. It must then change nothing."""
    from transcription_worker.trie_decode import _repair_from_offset_pass

    main = [{"start": 0.0, "end": 30.0, "text": "and Nemo shipped"}]
    repair = [{"start": 0.0, "end": 30.0, "text": "and Nemo shipped again"}]

    out, repaired = _repair_from_offset_pass(main, repair, {"Knemo"}, 15.0)
    assert repaired == set()
    assert [s["text"] for s in out] == ["and Nemo shipped"]


@requires_torch
def test_repair_only_touches_segments_it_overlaps_in_time():
    """A recovered term may not rewrite a region the repair window never saw."""
    from transcription_worker.trie_decode import _repair_from_offset_pass

    main = [{"start": 0.0, "end": 10.0, "text": "opening remarks"},
            {"start": 50.0, "end": 80.0, "text": "and Nemo shipped"}]
    # repair segment covers 15-45 s in absolute time: it overlaps neither.
    repair = [{"start": 0.0, "end": 30.0, "text": "and Knemo shipped"}]

    out, repaired = _repair_from_offset_pass(main, repair, {"Knemo"}, 15.0)
    assert repaired == set()
    assert [s["text"] for s in out] == ["opening remarks", "and Nemo shipped"]


@requires_torch
def test_term_presence_matches_on_word_boundaries():
    from transcription_worker.trie_decode import _terms_present

    assert _terms_present("we deployed ArcFace today", ["ArcFace"]) == {"ArcFace"}
    assert _terms_present("we deployed arcface today", ["ArcFace"]) == {"ArcFace"}
    # a substring inside a longer word is not the term
    assert _terms_present("ArcFacelift", ["ArcFace"]) == set()
