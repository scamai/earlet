"""Pure bookkeeping tests for the MLX vocabulary beam adapter.

These tests deliberately avoid importing MLX.  The GPU adapter is responsible
for logits and cache movement; ``BeamCandidates`` owns the CPU-side invariants
that can be verified with a tiny integer-token trie.
"""

import math

import pytest

from transcription_worker.beam_decode import BiasTrie
from transcription_worker.mlx_beam import BeamCandidates


def test_changing_confidence_rebuilds_the_cached_trie_without_mutating_the_previous_one():
    from transcription_worker.mlx_beam import VocabularyModel

    class Tokenizer:
        def encode(self, text):
            return [7]

    tokenizer = Tokenizer()
    model = VocabularyModel(None, ["Miles"], 2, .5, {}, {0: .2})
    tentative = model.build_trie(tokenizer)
    assert tentative.delta_for_token((), 7) == .2
    assert model.build_trie(tokenizer) is tentative
    model.phrase_deltas = {0: .35}
    confirmed = model.build_trie(tokenizer)
    assert confirmed is not tentative
    assert confirmed.delta_for_token((), 7) == .35
    assert tentative.delta_for_token((), 7) == .2


def trie(*sequences, delta=2.0, completion_bonus=0.0):
    return BiasTrie.from_sequences([list(s) for s in sequences],
                                   delta=delta,
                                   completion_bonus=completion_bonus)


def test_advance_deduplicates_prefixes_and_returns_the_winning_parent():
    search = BeamCandidates(width=2, eot=99)
    search.start([[], []])

    active, parents, scores, done = search.advance([
        [(1, -1.0, 0.5)],
        [(1, -0.2, 1.0), (2, -0.3, 0.8)],
    ])

    assert active == [(1,), (2,)]
    assert parents == [1, 1]
    assert scores == [1.0, 0.8]
    assert done is False


def test_non_finite_candidates_are_rejected():
    search = BeamCandidates(width=2, eot=99)
    search.start([[], []])

    with pytest.raises(ValueError, match="no finite candidates"):
        search.advance([
            [(1, math.nan, 0.0), (2, -1.0, math.inf)],
            [(3, -math.inf, 0.0), (4, 0.0, math.nan)],
        ])


def test_all_eot_candidates_complete_and_keep_finished_sequences():
    search = BeamCandidates(width=2, eot=99)
    search.start([[], [7]])

    active, parents, scores, done = search.advance([
        [(99, -0.1, -0.1)],
        [(99, -0.2, -0.2)],
    ])

    assert done is True
    assert len(active) == len(parents) == len(scores) == 2
    assert set(search.finished) == {(99,), (7, 99)}
    assert all(math.isfinite(score) and math.isfinite(raw)
               for score, raw in search.finished.values())


def test_finalize_refunds_unconfirmed_partial_trie_credit():
    search = BeamCandidates(width=1, eot=99,
                            trie=trie((1, 2), delta=2.0))
    search.start([[]])
    search.advance([[(1, -0.25, 1.75)]])

    # The adjusted score includes +2 for the in-progress phrase. Synthetic
    # EOT must refund it, leaving only the raw acoustic score.
    finished = search.finalize()

    assert finished[(1, 99)][0] == pytest.approx(-0.25)
    assert finished[(1, 99)][1] == pytest.approx(-0.25)


def test_forked_bias_states_do_not_share_completion_or_pending_credit():
    state = trie((1, 2), delta=2.0, completion_bonus=3.0).initial_state()
    partial, _ = state.advance(1)

    completed, completion_adjustment = partial.advance(2)
    dead, rollback_adjustment = partial.advance(3)

    assert completed.completed == frozenset({0})
    assert completion_adjustment == pytest.approx(3.0)
    assert completed.pending == 0.0
    assert dead.completed == frozenset()
    assert dead.pending == 0.0
    assert rollback_adjustment == pytest.approx(-2.0)
    assert partial.completed == frozenset()
    assert partial.pending == pytest.approx(2.0)


def test_bias_adjustment_does_not_replace_raw_acoustic_score():
    search = BeamCandidates(width=1, eot=99,
                            trie=trie((1,), delta=2.0))
    search.start([[]])
    search.advance([[(1, -0.75, 1.25)]])

    assert search.scores == [pytest.approx(1.25)]
    assert search.acoustic == [pytest.approx(-0.75)]


def test_exhausted_beam_does_not_invent_an_unscored_finished_path():
    search = BeamCandidates(width=2, eot=99)
    search.start([[1], [2]])
    _, _, _, done = search.advance([[(99, -1.0, -1.0)], [(3, math.nan, math.nan)]])
    assert done
    assert search.finalize() == {(1, 99): (-1.0, -1.0)}


def test_early_short_completions_cannot_stop_a_better_live_transcript():
    search = BeamCandidates(width=2, eot=99, max_tokens=6)
    search.start([[50], [50]])
    search.advance([[(1, -.1, -.1), (2, -.2, -.2)], []])
    _, _, _, done = search.advance([
        [(99, -.4, -.4), (3, -.05, -.05)],
        [(99, -.4, -.4), (4, -.9, -.9)],
    ])
    assert len(search.finished) == 2
    assert not done  # Both short endings lose to the continuing sentence.
    search.advance([[(5, -.05, -.05)], [(6, -.1, -.1)]])
    search.advance([[(99, -.05, -.05)], [(99, -.1, -.1)]])
    assert (50, 1, 3, 5, 99) in search.finalize()
    assert (50, 1, 99) not in search.finished


def test_length_limit_compares_live_candidates_even_when_finished_is_full():
    search = BeamCandidates(width=2, eot=99, max_tokens=3)
    search.start([[50], [50]])
    search.advance([[(1, -.1, -.1), (2, -.2, -.2)], []])
    search.advance([[(99, -.4, -.4), (3, -.05, -.05)],
                    [(99, -.4, -.4), (4, -.9, -.9)]])
    assert (50, 1, 3, 99) in search.finalize()


def test_finished_bound_allows_remaining_vocabulary_credit():
    search = BeamCandidates(width=2, eot=99, max_tokens=3,
                            trie=trie((1, 3, 5), delta=2.0))
    search.start([[], []])
    search.advance([[(1, -3., -1.), (2, -2., -2.)], []])
    search._finish((7, 99), -.1, -.1)
    # Without future vocabulary credit, even -1/3 loses to the ending -.1.
    assert not search._can_stop()
    search.advance([[(3, -.01, 1.99)], [(4, -.1, -.1)]])
    search.advance([[(5, -.01, 1.99)], [(6, -.1, -.1)]])
    search.advance([[(99, -.01, -.01)], [(99, -.1, -.1)]])
    assert (1, 3, 5, 99) in search.finalize()


@pytest.mark.parametrize("penalty", [None, .6, 1.0])
def test_finished_bound_uses_final_length_normalization(penalty):
    search = BeamCandidates(width=2, eot=99, max_tokens=6, length_penalty=penalty)
    search.start([[], []])
    search.advance([[(1, -.6, -.6), (2, -.7, -.7)], []])
    search._finish((7, 99), -.5, -.5)
    # Cumulative scores lose, but a full sentence can win the final rank.
    assert not search._can_stop()


def test_width_one_keeps_greedy_eot_even_when_a_longer_path_normalizes_better():
    search = BeamCandidates(width=1, eot=99)
    search.start([[]])
    search.advance([[(1, -.1, -.1)]])
    _, _, _, done = search.advance([[(99, -.2, -.2), (2, -.21, -.21)]])
    assert done
    assert list(search.finalize()) == [(1, 99)]


def test_transcribe_adapter_keeps_provider_globals_and_resident_model_unchanged(monkeypatch):
    import sys
    from types import ModuleType
    from transcription_worker import mlx_beam

    provider = ModuleType("mlx_whisper")
    exec('''
class ModelHolder:
    model = object()
    @classmethod
    def get_model(cls, path, dtype):
        return cls.model
def transcribe(audio, *, beam_size):
    model = ModelHolder.get_model("local", "fp16")
    return {"text": "", "width": model.width, "phrases": model.phrases}
''', provider.__dict__)
    monkeypatch.setitem(sys.modules, "mlx_whisper", provider)
    holder, resident, original = provider.ModelHolder, provider.ModelHolder.model, provider.transcribe

    first = mlx_beam.transcribe([], bias_phrases=["repo"], beam_size=2)
    second = mlx_beam.transcribe([], bias_phrases=[], beam_size=1)

    assert (first["width"], first["phrases"]) == (2, ("repo",))
    assert (second["width"], second["phrases"]) == (1, ())
    assert provider.ModelHolder is holder and holder.model is resident
    assert provider.transcribe is original


# ─── Trailing silence: a loop must not cost the real sentence ────────────

def outro_loop_beam():
    """Width 2: one candidate ends its sentence on EOT; the other runs on in
    the trailing silence, repeating two tokens at a higher average score,
    until the length limit cuts it off."""
    search = BeamCandidates(width=2, eot=99, max_tokens=8)
    search.start([[50], [50]])
    search.advance([[(1, -.5, -.5), (2, -.6, -.6)], []])
    # Row 0 ends its sentence; row 1 starts the outro.
    search.advance([[(99, -.5, -.5), (7, -.4, -.4)], [(99, -2.0, -2.0), (8, -2.0, -2.0)]])
    for _ in range(6):
        search.advance([[(8, -.01, -.01), (7, -.02, -.02)], [(7, -.01, -.01), (8, -.02, -.02)]])
    return search


def test_a_cut_off_loop_never_evicts_the_sentence_that_reached_eot():
    search = outro_loop_beam()
    finished = search.finalize()
    assert (50, 1, 99) in finished, "the real sentence is kept for the repetition fallback"
    best = max(finished, key=lambda s: search._rank(s, finished[s][0]))
    assert best != (50, 1, 99), "a cut-off loop may still rank first; only eviction changes"


class Tokens:
    """Each token is a word; 7 and 8 are the outro's two words."""
    words = {1: "We use repo.", 2: "We use rebo.", 7: " 请不吝点赞 订阅 转发", 8: " 打赏支持 转发 打赏支持"}

    def decode(self, tokens):
        return "".join(self.words.get(t, "") for t in tokens)


def test_a_repeating_top_candidate_gives_way_to_the_best_one_that_is_not():
    from transcription_worker.mlx_beam import REPETITION_LIMIT, VocabularyModel, compression_ratio
    search = outro_loop_beam()
    search.finalize()

    def generated(sequence):
        tail = sequence[search.sample_begin:]
        return tuple(tail[:tail.index(99)])

    loops = [s for s in search.finished if len(s) > 4]
    assert loops and all(compression_ratio(Tokens().decode(list(generated(s)))) > REPETITION_LIMIT
                         for s in loops)
    tokens, text, ratio = VocabularyModel._unrepeated(search, generated, Tokens())
    assert (tokens, text) == ((1,), "We use repo.")
    assert ratio <= REPETITION_LIMIT


def test_when_every_candidate_repeats_the_window_is_left_to_the_temperature_fallback():
    from transcription_worker.mlx_beam import VocabularyModel
    search = BeamCandidates(width=1, eot=99, max_tokens=12)
    search.start([[50]])
    for _ in range(11):
        search.advance([[(7, -.01, -.01)]])
    search.finalize()

    def generated(sequence):
        tail = sequence[search.sample_begin:]
        return tuple(tail[:tail.index(99)])

    assert VocabularyModel._unrepeated(search, generated, Tokens()) is None


def test_the_repetition_measure_is_upstreams():
    from transcription_worker.mlx_beam import compression_ratio
    upstream = pytest.importorskip("mlx_whisper.decoding")
    for text in ("We use repo.", "转发 打赏支持" * 20, ""):
        if text:
            assert compression_ratio(text) == upstream.compression_ratio(text)
