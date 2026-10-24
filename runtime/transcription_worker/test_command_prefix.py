"""L1b closed-set command prefix through beam_search() itself (Option A).

Synthetic ten-to-twenty-token vocabularies and a scripted FakeDecoderSession
stand in for ort.InferenceSession, the same approach as test_density_ratio.py
and test_beam_decode.py -- no ONNX, no tokenizer, no NPU.

What is being defended, in the order it matters:

  * a request WITHOUT `command_prefix` decodes byte-identically to before the
    layer existed (the dual-use requirement: meeting notes never sets it);
  * the payload after the trigger decodes completely unconstrained -- the
    single most important correctness property, because the boundary after a
    short trigger is where the short-trigger bugs live;
  * the constraint is a live trie PATH, not a per-position token filter. The
    device probe (clip S9, whisper-large-v3-turbo-qnn) measured that the two
    tokens beating the spoken ` note` at position 0 are the openings of OTHER
    catalogue workflows -- ` no` (-2.228, "no notifications") and ` not`
    (-6.201, "notepad") against ` note` (-7.322) -- so a first-token argmax
    over the restricted set routes to the wrong workflow. The right answer
    only wins on the CONTINUATION, and `test_the_wrong_workflow_has_the_best_
    first_token` is that case.

Run: python -m pytest transcription_worker/test_command_prefix.py
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.beam_decode import (  # noqa: E402
    FORCED_PREFIX,
    MASK_NEG,
    BiasTrie,
    CommandPrefix,
    beam_search,
)

VOCAB = 20  # far below the real SOT(50258)/EOT(50257): no special is reachable

PREFIX_LEN = len(FORCED_PREFIX)


class _IO:
    def __init__(self, name, shape):
        self.name = name
        self.shape = shape


class ScriptedSession:
    """Logits as a function of the CONTENT position, i.e. "what the audio says".

    `steps` is a list of {token: logit} dicts: entry k is the distribution the
    decoder emits when it is about to generate content position k. Positions
    past the end repeat the last entry. Everything unnamed sits at `floor`.

    Position-dependent because that is the whole subject: a per-position token
    filter and a live trie path are indistinguishable on a decoder whose
    opinion never changes.
    """

    def __init__(self, steps: list[dict], floor: float = -12.0, n_layers: int = 1):
        self.steps = steps
        self.floor = floor
        self.n_layers = n_layers
        self.calls = 0

    def get_inputs(self):
        ins = [_IO("input_ids", [1, 1]), _IO("attention_mask", [1, 1, 1, 200]),
               _IO("position_ids", [1])]
        for i in range(self.n_layers):
            ins.append(_IO(f"k_cache_self_{i}_in", (1, 1, 1, 1)))
            ins.append(_IO(f"v_cache_self_{i}_in", (1, 1, 1, 1)))
        return ins

    def get_outputs(self):
        outs = [_IO("logits", [1, VOCAB, 1, 1])]
        for i in range(self.n_layers):
            outs.append(_IO(f"k_cache_self_{i}_out", (1, 1, 1, 1)))
            outs.append(_IO(f"v_cache_self_{i}_out", (1, 1, 1, 1)))
        return outs

    def run(self, _fetches, inputs):
        self.calls += 1
        # position_ids counts the forced prefix too; content position k is
        # emitted by the call at position_ids == PREFIX_LEN - 1 + k.
        n = int(np.asarray(inputs["position_ids"]).reshape(-1)[0])
        idx = max(n - (PREFIX_LEN - 1), 0)
        spec = self.steps[min(idx, len(self.steps) - 1)]
        logits = np.full((1, VOCAB, 1, 1), self.floor, dtype=np.float32)
        for token, value in spec.items():
            logits[0, int(token), 0, 0] = float(value)
        out = [logits]
        for _ in range(self.n_layers):
            out.append(np.zeros((1, 1, 1, 1), dtype=np.float16))
            out.append(np.zeros((1, 1, 1, 1), dtype=np.float16))
        return out


def content(tokens: list[int]) -> list[int]:
    return tokens[PREFIX_LEN:]


def decode(session, *, beam_size=4, max_len=10, **kw):
    stats: dict = {}
    tokens = beam_search(session, cross_kv={}, beam_size=beam_size,
                         max_len=max_len, stats=stats, **kw)
    return content(tokens), stats


def cp(sequences, **kw) -> CommandPrefix:
    return CommandPrefix.from_sequences(sequences, **kw)


# The synthetic catalogue, laid out to mirror the measured S9 collision.
NO, NOT, NOTE = 1, 2, 3          # three catalogue-initial tokens
NOTIF, PAD = 4, 5                # their continuations
PAY_A, PAY_B = 6, 7              # the payload the speaker actually said
OFF = 8                          # a token on no catalogue path at all

#: "no notifications" / "notepad" / "note" -- lengths 2, 2 and 1, so the
#: winner is decided across DIFFERENT path lengths.
CATALOGUE = [[NO, NOTIF], [NOT, PAD], [NOTE]]


# ---------------------------------------------------------------------------
# 1. Absent field = byte-identical. Non-negotiable: notes shares this decoder.
# ---------------------------------------------------------------------------


def _plain_session():
    return ScriptedSession([
        {NO: 0.0, NOT: -4.0, NOTE: -5.1},
        {PAY_A: 0.0},
        {PAY_B: 0.0},
    ])


def test_absent_command_prefix_decodes_exactly_as_before():
    """The dual-use guarantee, asserted on the decode itself rather than on a
    flag: the notes client never sets this field, and its absence must change
    nothing about the tokens OR the numbers."""
    base_tokens, base_stats = decode(_plain_session())
    none_tokens, none_stats = decode(_plain_session(), command_prefix=None)

    assert base_tokens == none_tokens
    assert base_stats["avg_logprob"] == none_stats["avg_logprob"]
    assert [h["score"] for h in base_stats["nbest"]] == \
           [h["score"] for h in none_stats["nbest"]]
    # ...and the winner really is the unconstrained argmax, or the test below
    # that flips it would be proving nothing.
    assert base_tokens[0] == NO


def test_absent_command_prefix_adds_no_stats_key():
    """A key that appears only sometimes is a key hosts learn to ignore. It
    must be absent, not null, when the field was not sent."""
    _, stats = decode(_plain_session())
    assert "command_prefix" not in stats
    _, stats = decode(_plain_session(), command_prefix=None)
    assert "command_prefix" not in stats


def test_armed_decode_reports_what_it_was_armed_with():
    _, stats = decode(_plain_session(),
                      command_prefix=cp(CATALOGUE, mode="hard", max_tokens=2))
    assert stats["command_prefix"] == {
        "phrases": 3, "max_tokens": 2, "mode": "hard", "delta": 6.0,
        "release": 1,
    }


# ---------------------------------------------------------------------------
# 2. Hard mode forces a catalogue-initial token
# ---------------------------------------------------------------------------


def test_hard_mode_forces_a_catalogue_initial_token():
    """An off-catalogue token with the best raw logprob must lose position 0."""
    session = ScriptedSession([
        {OFF: 0.0, NOTE: -5.1},   # OFF wins outright on acoustics
        {PAY_A: 0.0},
    ])
    free, _ = decode(session)
    assert free[0] == OFF, "fixture must reproduce the open-vocabulary choice"

    session = ScriptedSession([
        {OFF: 0.0, NOTE: -5.1},
        {PAY_A: 0.0},
    ])
    constrained, _ = decode(session, command_prefix=cp(CATALOGUE, mode="hard"))
    assert constrained[0] == NOTE


def test_hard_mode_is_mask_neg_not_negative_infinity():
    """MASK_NEG is finite ON PURPOSE. `beam_search` skips non-finite
    candidates, so an -inf mask over a catalogue that fits no audio at all
    could empty the beam; -100 leaves the decode able to return text."""
    prefix = cp(CATALOGUE, mode="hard")
    assert prefix.penalty == MASK_NEG
    assert np.isfinite(prefix.penalty)

    lp = np.zeros(VOCAB, dtype=np.float32)
    prefix.initial_state().apply(lp, 0, 50258, 50257)
    assert np.isfinite(lp).all()
    assert lp[OFF] == pytest.approx(MASK_NEG)
    assert lp[NOTE] == pytest.approx(0.0)


# ---------------------------------------------------------------------------
# 3. The constraint releases; the PAYLOAD is unconstrained
# ---------------------------------------------------------------------------


def test_payload_after_a_completed_phrase_is_unconstrained():
    """The property that matters most. After ` note` completes at position 0,
    every later position must decode exactly as if the layer were absent --
    including tokens on no catalogue path whatsoever."""
    steps = [
        {NOTE: 0.0},                    # the trigger
        {OFF: 0.0, NOTIF: -1.0},        # payload: off-catalogue token wins
        {OFF: 0.0, PAD: -1.0},
        {OFF: 0.0, PAD: -1.0},
    ]
    tokens, stats = decode(ScriptedSession(steps),
                           command_prefix=cp(CATALOGUE, mode="hard", max_tokens=3))
    assert tokens[0] == NOTE
    assert set(tokens[1:]) == {OFF}, tokens
    # Released after exactly one content position.
    assert stats["command_prefix"]["release"] == 1


def test_release_is_reported_at_the_position_the_phrase_completed():
    """A two-token phrase covers two positions, then lets go."""
    steps = [{NO: 0.0}, {NOTIF: 0.0}, {OFF: 0.0, PAD: -1.0}, {OFF: 0.0}]
    tokens, stats = decode(ScriptedSession(steps),
                           command_prefix=cp(CATALOGUE, mode="hard", max_tokens=3))
    assert tokens[:2] == [NO, NOTIF]
    assert tokens[2] == OFF, "position 2 is past the completed phrase: free"
    assert stats["command_prefix"]["release"] == 2


def test_a_hold_with_no_catalogue_phrase_still_decodes_in_soft_mode():
    """Risk (ii) from the brief: a hold addressed to the wake word that asks for
    nothing in the catalogue must degrade to something sane rather than to a
    workflow nobody asked for. Soft mode is the default for this reason."""
    steps = [{OFF: 0.0}, {PAY_A: 0.0}, {PAY_B: 0.0}]
    tokens, stats = decode(ScriptedSession(steps),
                           command_prefix=cp(CATALOGUE, mode="soft", delta=6.0))
    assert tokens[:3] == [OFF, PAY_A, PAY_B]
    # No phrase completed -- which is exactly how the host can tell.
    assert stats["command_prefix"]["release"] is None


# ---------------------------------------------------------------------------
# 4. Soft mode: strong acoustics still win
# ---------------------------------------------------------------------------


def test_soft_mode_yields_to_a_strong_enough_acoustic_score():
    """The off-catalogue token leads by more than delta, so it survives."""
    steps = [{OFF: 0.0, NOTE: -8.0}, {PAY_A: 0.0}]
    tokens, _ = decode(ScriptedSession(steps),
                       command_prefix=cp(CATALOGUE, mode="soft", delta=6.0))
    assert tokens[0] == OFF


def test_soft_mode_flips_a_margin_smaller_than_delta():
    """...and the other side of the same line: a lead smaller than delta is
    overturned, which is the `note` case at a survivable delta."""
    steps = [{OFF: 0.0, NOTE: -2.0}, {PAY_A: 0.0}]
    tokens, _ = decode(ScriptedSession(steps),
                       command_prefix=cp(CATALOGUE, mode="soft", delta=6.0))
    assert tokens[0] == NOTE


def test_hard_mode_would_not_have_yielded():
    """The two modes are genuinely different, on the same fixture."""
    steps = [{OFF: 0.0, NOTE: -8.0}, {PAY_A: 0.0}]
    tokens, _ = decode(ScriptedSession(steps),
                       command_prefix=cp(CATALOGUE, mode="hard"))
    assert tokens[0] == NOTE


def test_soft_is_the_default_mode():
    """Graceful degradation is the owner's explicit preference; a default that
    drifted to `hard` would be the difference between a bad transcript and a
    wrong workflow."""
    assert CommandPrefix.SOFT == "soft"
    assert cp(CATALOGUE).mode == "soft"
    assert cp(CATALOGUE).max_tokens == 3


# ---------------------------------------------------------------------------
# 5. The measured failure: the WRONG workflow has the best first token
# ---------------------------------------------------------------------------


def test_the_wrong_workflow_has_the_best_first_token():
    """The device measurement, reproduced in miniature.

    Clip S9 is "note ThinkShield ...". At content position 0 the restricted
    distribution reads ` no` (-2.228, opens "no notifications") > ` not`
    (-6.201, "notepad") > ` note` (-7.322, spoken). A first-token argmax over
    the closed set therefore fires the WRONG WORKFLOW, and the 5.1-nat gap is
    too wide for any delta that is safe to ship.

    What decides it is the continuation: "no notifications" and "notepad" must
    keep paying for `notifications` / `pad`, which the speaker never said,
    while `note` completes at position 0 and decodes the payload for free.
    Three candidate paths, two of them length 2 and the winner length 1, so
    they are only comparable through `_normalized` -- which is what
    `beam_search` already ranks the survivors by.
    """
    steps = [
        # Position 0: the measured ordering. The wrong workflow leads by 5.1.
        {NO: -2.228, NOT: -6.201, NOTE: -7.322},
        # Position 1: the audio is the payload. The catalogue continuations
        # `notifications` and `pad` are simply not there.
        {PAY_A: 0.0},
        {PAY_B: 0.0},
        {PAY_B: 0.0},
    ]
    session = ScriptedSession(steps)
    free, _ = decode(session)
    assert free[0] == NO, "fixture must reproduce the wrong-workflow argmax"

    for mode in ("hard", "soft"):
        tokens, stats = decode(
            ScriptedSession(steps),
            command_prefix=cp(CATALOGUE, mode=mode, max_tokens=3, delta=6.0))
        assert tokens[0] == NOTE, (mode, tokens)
        # ...and it won by COMPLETING, not by being boosted into first place.
        assert stats["command_prefix"]["release"] == 1, mode
        # The payload is intact behind it.
        assert tokens[1] == PAY_A, (mode, tokens)


def test_a_beam_that_walks_off_the_catalogue_pays_once_and_is_let_go():
    """The charge is per DECISION, not per position.

    "Is this utterance a catalogue command" is one binary fact. Charging
    -delta at every remaining constrained position would give soft mode
    delta*max_tokens of pull (18 nats at the defaults), which wins the S9 case
    comfortably and is exactly the wrong way to win it: the pull toward the
    catalogue would scale with a window length nobody chose for that reason,
    and a mis-armed constraint over ordinary prose would have 18 nats with
    which to insert a trigger nobody said. `test_a_mis_armed_constraint_does_
    not_invent_a_trigger` is that failure, pinned.
    """
    prefix = cp(CATALOGUE, mode="soft", delta=6.0, max_tokens=3)
    state = prefix.initial_state().advance(NO, 0)   # on the "no notifications" path
    walked = state.advance(PAY_A, 1)                # ...and off it again

    assert walked.done, "leaving the closed set releases the constraint"
    assert not walked.completed, "...but it is not a completion"
    assert not walked.active(2)

    lp = np.zeros(VOCAB, dtype=np.float32)
    walked.apply(lp, 2, 50258, 50257)
    assert lp == pytest.approx(np.zeros(VOCAB)), "released means inert"

    # A COMPLETED phrase releases too, and is distinguishable in the reporting.
    done = prefix.initial_state().advance(NOTE, 0)
    assert done.done and done.completed


def test_a_mis_armed_constraint_does_not_invent_a_trigger():
    """Risk (iii) from the brief -- the wake word is only ~50% reliable on
    Windows, and a mis-armed closed set corrupting ordinary dictation is the
    one failure mode worse than a missed workflow.

    Ordinary prose whose opening is 12 nats better than any catalogue token
    must survive soft mode: the beam pays delta once for leaving and keeps its
    own words. This is the test that fails if the charge is ever made
    per-position -- 3 * 6 = 18 > 12 and the decoder starts typing `note`.
    """
    steps = [{OFF: 0.0}, {PAY_A: 0.0}, {PAY_B: 0.0}]  # catalogue tokens at the -12 floor
    tokens, stats = decode(ScriptedSession(steps),
                           command_prefix=cp(CATALOGUE, mode="soft", delta=6.0,
                                             max_tokens=3))
    assert tokens[:3] == [OFF, PAY_A, PAY_B], tokens
    assert stats["command_prefix"]["release"] is None, (
        "no catalogue phrase completed -- which is how the host tells a "
        "mis-armed hold from a real command")


def test_position_one_extends_the_chosen_prefix_and_does_not_restart():
    """A live PATH, not a per-position filter: at position 1 the legal set is
    the continuation of what position 0 chose, not every catalogue opening
    again. Otherwise "no notifications" would license ` note` at position 1
    and the paths would stop being distinguishable."""
    state = cp(CATALOGUE, mode="hard").initial_state()
    assert state.allowed_tokens() == {NO, NOT, NOTE}
    after_no = state.advance(NO, 0)
    assert after_no.allowed_tokens() == {NOTIF}


def test_the_soft_mode_margin_on_the_measured_case_is_delta_minus_the_gap():
    """Soft mode's whole margin on S9 is `delta - 5.094`, and no more: the
    wrong-workflow beam leaves the closed set once, for one -delta, and is
    then free. At the briefed delta=6.0 that is 0.9 nats.

    Pinned as arithmetic rather than left as a claim, because it is the number
    the device arms have to be sized against -- and because it says plainly
    that any delta at or below the 5.094-nat first-token gap hands the decode
    back to "no notifications" in soft mode. Hard mode does not depend on it.
    """
    steps = [{NO: -2.228, NOT: -6.201, NOTE: -7.322}, {PAY_A: 0.0}, {PAY_B: 0.0}]
    gap = 7.322 - 2.228  # 5.094

    for delta, expected in ((gap + 0.9, NOTE), (gap - 0.5, NO)):
        tokens, _ = decode(ScriptedSession(steps),
                           command_prefix=cp(CATALOGUE, mode="soft",
                                             delta=delta, max_tokens=3))
        assert tokens[0] == expected, (delta, tokens)

    # Hard mode carries it regardless of delta: the wrong path cannot leave,
    # so it pays `notifications`' real acoustic cost instead.
    tokens, _ = decode(ScriptedSession(steps),
                       command_prefix=cp(CATALOGUE, mode="hard", delta=0.1,
                                         max_tokens=3))
    assert tokens[0] == NOTE


def test_the_winner_is_ranked_by_the_length_normalised_score():
    """Catalogue paths of different lengths are only comparable through
    `_normalized`, and `beam_search` already ranks its survivors by it. Pinned
    here because the day someone ranks the N-best by a raw sum, short phrases
    win everything and this package's central case regresses silently."""
    steps = [
        {NO: -2.228, NOT: -6.201, NOTE: -7.322},
        {PAY_A: 0.0},
        {PAY_B: 0.0},
    ]
    _, stats = decode(ScriptedSession(steps),
                      command_prefix=cp(CATALOGUE, mode="hard", max_tokens=3))
    scores = [h["score"] for h in stats["nbest"]]
    assert scores == sorted(scores, reverse=True)
    winner = stats["nbest"][0]
    assert content(winner["tokens"])[0] == NOTE
    # Length-normalised, not a raw sum: the winner's score is its cumulative
    # score over generated**length_penalty, so it is strictly greater than the
    # raw mean would be for a hypothesis this long.
    assert winner["score"] > sum(
        h["score"] for h in stats["nbest"][1:]) / max(len(stats["nbest"]) - 1, 1)


# ---------------------------------------------------------------------------
# 6. max_tokens boundary, exactly
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("max_tokens", [1, 2, 3])
def test_max_tokens_is_the_exact_number_of_constrained_positions(max_tokens):
    """Positions 0..max_tokens-1 are constrained; position max_tokens is free.

    Driven with a catalogue of ONE four-token phrase so the constraint never
    completes and never walks off -- the only thing that can release it is the
    boundary, which is what is being measured. The audio prefers an
    off-catalogue token at every position, so a constrained position shows the
    catalogue token and a free one shows OFF.
    """
    long_phrase = [[NO, NOTIF, PAD, PAY_B]]
    steps = [{OFF: 0.0, NO: -5.0},
             {OFF: 0.0, NOTIF: -5.0},
             {OFF: 0.0, PAD: -5.0},
             {OFF: 0.0, PAY_B: -5.0},
             {OFF: 0.0}]
    tokens, _ = decode(ScriptedSession(steps), max_len=12,
                       command_prefix=cp(long_phrase, mode="hard",
                                         max_tokens=max_tokens))
    on_path = [NO, NOTIF, PAD, PAY_B]
    assert tokens[:max_tokens] == on_path[:max_tokens], tokens
    assert tokens[max_tokens] == OFF, (
        f"position {max_tokens} must be free with max_tokens={max_tokens}")


def test_max_tokens_one_constrains_only_the_first_position():
    state = cp(CATALOGUE, max_tokens=1).initial_state()
    assert state.active(0)
    assert not state.active(1)


def test_max_tokens_must_be_at_least_one():
    with pytest.raises(ValueError):
        cp(CATALOGUE, max_tokens=0)


def test_a_non_positive_delta_is_refused_rather_than_silently_inert():
    """Asking for a constraint that does nothing, and being told it does
    nothing, beats a request that LOOKS armed and is not. The way to ask for
    no constraint is to omit the field."""
    with pytest.raises(ValueError):
        cp(CATALOGUE, delta=0.0)
    with pytest.raises(ValueError):
        cp(CATALOGUE, delta=-1.0)


def test_an_unknown_mode_is_refused():
    with pytest.raises(ValueError):
        cp(CATALOGUE, mode="strict")


# ---------------------------------------------------------------------------
# 7. Composition with L1 (BiasTrie) and L2
# ---------------------------------------------------------------------------


def _bias(sequences, delta=4.0, bonus=3.0) -> BiasTrie:
    return BiasTrie.from_sequences(sequences, delta=delta, completion_bonus=bonus)


def test_composes_with_an_active_bias_trie_without_touching_fired():
    """Both layers live at once. L1's `fired[]` -- the ledger's "did the boost
    do the work" signal -- must report exactly what it reports on its own, and
    the constraint must not add, drop or move an entry."""
    steps = [{NOTE: 0.0}, {PAY_A: 0.0}, {PAY_B: 0.0}, {PAY_B: 0.0}]
    bias = _bias([[PAY_A, PAY_B]])

    solo_tokens, solo_stats = decode(ScriptedSession(steps), bias=bias)
    both_tokens, both_stats = decode(ScriptedSession(steps), bias=bias,
                                     command_prefix=cp(CATALOGUE, mode="hard"))

    assert solo_tokens == both_tokens
    assert solo_stats["nbest"][0]["fired"] == both_stats["nbest"][0]["fired"]
    assert both_stats["nbest"][0]["fired"], "fixture must actually fire L1"
    # L1's own accounting is unchanged: same phrase (0), completing at content
    # position 2, over 2 tokens.
    assert both_stats["nbest"][0]["fired"] == [(0, 2, 2)]


def test_the_constraint_does_not_disturb_l1_rollback_accounting():
    """L1 refunds the boost a dead partial match was charged. That arithmetic
    lives entirely in `BiasState.pending`/`earned`, which this layer never
    writes to -- so a decode that kills a partial match must score the same
    with the constraint armed as without it, once the constraint's own
    (uniform, position-independent) charge is accounted for.

    Asserted as an identity on the raw acoustic mean rather than on the score,
    because `avg_logprob` is bias-free and constraint-free by construction:
    if either layer leaked into it, this is where it would show.
    """
    steps = [{NOTE: 0.0}, {PAY_A: 0.0, OFF: -0.5}, {OFF: 0.0}, {OFF: 0.0}]
    bias = _bias([[PAY_A, PAY_B]])  # PAY_A starts a match that PAY_B never ends

    solo_tokens, solo_stats = decode(ScriptedSession(steps), bias=bias)
    both_tokens, both_stats = decode(ScriptedSession(steps), bias=bias,
                                     command_prefix=cp(CATALOGUE, mode="hard"))

    assert solo_tokens == both_tokens
    assert both_stats["avg_logprob"] == pytest.approx(solo_stats["avg_logprob"])
    assert both_stats["nbest"][0]["fired"] == solo_stats["nbest"][0]["fired"] == []


def test_l1_and_l1b_pull_the_same_way_without_double_counting():
    """The realistic configuration: the catalogue is ALSO in `bias_phrases`
    (the host pushes all 362 on every utterance). The constraint must not need
    L1's help and must not be cancelled by it."""
    steps = [{OFF: 0.0, NOTE: -5.1}, {PAY_A: 0.0}]
    bias = _bias([[NOTE]], delta=1.0, bonus=0.0)

    # L1 alone cannot lift a token the beam would carry but that loses by 5.1.
    l1_only, _ = decode(ScriptedSession(steps), bias=bias)
    assert l1_only[0] == OFF, "delta=1.0 must not be enough on its own"

    both, stats = decode(ScriptedSession(steps), bias=bias,
                         command_prefix=cp(CATALOGUE, mode="hard"))
    assert both[0] == NOTE
    assert stats["command_prefix"]["release"] == 1


def test_the_constraint_is_purely_additive_over_l1_and_l2():
    """All three layers are vector adds at the same `prepare()` hook, so the
    arithmetic must be order-independent. Checked on the vector itself: L1's
    sparse delta on a trie candidate survives the constraint's penalty exactly,
    with no clobbering in either direction."""
    prefix = cp(CATALOGUE, mode="soft", delta=6.0)
    state = prefix.initial_state()

    lp = np.zeros(VOCAB, dtype=np.float32)
    lp[NOTE] += 4.0     # stand-in for L1 boosting a token that IS on the path
    lp[OFF] += 4.0      # ...and one that is not
    state.apply(lp, 0, 50258, 50257)

    assert lp[NOTE] == pytest.approx(4.0), "on-path: L1's boost is untouched"
    assert lp[OFF] == pytest.approx(4.0 - 6.0), "off-path: penalised, not reset"
    assert lp[PAY_A] == pytest.approx(-6.0)


# ---------------------------------------------------------------------------
# 8. Construction reuses the one trie implementation
# ---------------------------------------------------------------------------


def test_the_trie_is_the_bias_trie_with_its_phrase_variants():
    """One trie implementation, not two: `from_phrases` goes through
    `BiasTrie`, so the casing/leading-space variants that make the token
    Whisper actually emits (` note`, with the space) reachable come for free.
    """

    class CharTokenizer:
        def encode(self, text, add_special_tokens=False):
            assert add_special_tokens is False
            return [ord(c) for c in text]

    prefix = CommandPrefix.from_phrases(["Note"], CharTokenizer())
    assert isinstance(prefix.trie, BiasTrie)
    # variants: "Note", " Note", "note", " note" -> 'N', ' ', 'n' at the root
    assert prefix.initial_state().allowed_tokens() == {ord("N"), ord(" "), ord("n")}


def test_the_command_trie_never_boosts():
    """Its delta and completion bonus are constructed at zero and never read.
    If this layer ever started boosting it would need L1's whole rollback
    apparatus, and the point of the penalty form is that it does not."""
    prefix = cp(CATALOGUE)
    assert prefix.trie.delta == 0.0
    assert prefix.trie.completion_bonus == 0.0


def test_empty_phrases_build_nothing_usable():
    """A catalogue that is empty after filtering must not silently mask the
    whole vocabulary; `asr_server` rejects it before it gets this far, and
    this pins what the object itself does."""
    prefix = CommandPrefix.from_sequences([])
    assert prefix.n_phrases == 0
    state = prefix.initial_state()
    assert state.allowed_tokens() == set()
