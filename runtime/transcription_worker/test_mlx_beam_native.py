"""Run with the packaged Apple Silicon Python as well as the portable gate.

These exercise real MLX arrays and upstream decoder contracts without loading
weights. Real-model latency and output checks are separate device evidence.
"""
from types import SimpleNamespace

import pytest

mx = pytest.importorskip("mlx.core", reason="requires the native MLX runtime")
pytest.importorskip("mlx_whisper", reason="requires the MLX Whisper provider")

from mlx_whisper.decoding import ApplyTimestampRules, DecodingOptions, GreedyDecoder
from transcription_worker.beam_decode import BiasTrie
from transcription_worker.mlx_beam import BeamTimestampRules, MetalBeamDecoder, VocabularyModel, _task_type


class CacheRecorder:
    def __init__(self):
        self.parents = []

    def rearrange_kv_cache(self, parents):
        self.parents.append(parents)


def test_width_one_without_bias_matches_upstream_greedy_tokens_and_scores():
    greedy = GreedyDecoder(0, 5)
    beam = MetalBeamDecoder(1, 5, CacheRecorder())
    gt = bt = mx.array([[0]])
    gs = bs = mx.zeros(1)
    for logits in ([[0., 1., 3., -1., -2., -4.]], [[0., 1., -2., -1., -2., 4.]]):
        gt, gd, gs = greedy.update(gt, mx.array(logits), gs)
        bt, bd, bs = beam.update(bt, mx.array(logits), bs)
        if not gd.item():
            assert bt.tolist() == gt.tolist()
            assert bs.item() == pytest.approx(gs.item(), abs=1e-5)
        assert bd.item() == gd.item()
    finished = beam.search.finalize()
    assert list(finished) == [tuple(gt[0].tolist())]
    assert next(iter(finished.values()))[1] == pytest.approx(gs.item(), abs=1e-5)


def test_dead_partial_credit_does_not_survive_gpu_selection():
    trie = BiasTrie.from_sequences([[1, 2], [3, 6]], delta=2., completion_bonus=0.)
    beam = MetalBeamDecoder(1, 7, CacheRecorder(), trie)
    tokens, _, score = beam.update(mx.array([[0]]), mx.array([[0., 5., 0., 0., 0., 0., 0., -9.]]), mx.zeros(1))
    tokens, _, score = beam.update(tokens, mx.array([[0., -9., -9., 0., 3., 0., -9., -9.]]), score)
    assert tokens.tolist() == [[0, 1, 4]]
    assert beam.search.states[0].pending == 0
    assert beam.search.scores == pytest.approx(beam.search.acoustic, abs=1e-5)


def test_repeat_refunds_happen_before_top_k_so_repeats_cannot_crowd_out_speech():
    trie = BiasTrie.from_sequences([[1], [2]], delta=2., completion_bonus=0.)
    beam = MetalBeamDecoder(1, 5, CacheRecorder(), trie)
    beam.search.start([[0, 1, 2]])
    state, _ = trie.initial_state().advance(1)
    state, _ = state.advance(2)
    beam.search.states = [state]
    # k=2. Adding +2 to completed terms before taking top-k would discard
    # the acoustic winner 3. Refund repeats first, then search.
    tokens, _, _ = beam.update(mx.array([[0, 1, 2]]),
                               mx.array([[0., 2.5, 2.5, 3., 0., -9.]]), mx.zeros(1))
    assert tokens.tolist() == [[0, 1, 2, 3]]


def test_self_attention_reorders_and_cross_attention_stays_shared():
    model = SimpleNamespace(is_multilingual=False, num_languages=99,
                            dims=SimpleNamespace(n_text_ctx=448, n_vocab=51864))
    task = _task_type()(model, DecodingOptions(beam_size=2, language="en", without_timestamps=True), lambda _: None)
    self_key = mx.array([[1., 2.], [3., 4.]])
    self_value = self_key + 10
    cross = (mx.array([[8., 9.]]), mx.array([[18., 19.]]))
    task.inference.kv_cache = [((self_key, self_value), cross)]
    task.inference.rearrange_kv_cache([1, 1])
    cache, new_cross = task.inference.kv_cache[0]
    assert cache[0].tolist() == [[3., 4.], [3., 4.]]
    assert cache[1].tolist() == [[13., 14.], [13., 14.]]
    assert new_cross is cross


def test_sampling_fallback_uses_upstream_without_beam_options():
    calls = []
    marker = object()

    def decode(mel, options):
        calls.append(options)
        return marker

    metrics = {"beam_windows": 0, "sampling_fallbacks": 0}
    model = VocabularyModel(SimpleNamespace(decode=decode), ["repo"], 2, .5, metrics)
    assert model.decode(mx.zeros((1, 1)), DecodingOptions(temperature=.2, best_of=5)) is marker
    assert calls[0].beam_size is None and calls[0].best_of == 5
    assert metrics == {"beam_windows": 0, "sampling_fallbacks": 1}


def test_timestamp_rules_do_not_force_masked_timestamps():
    tokenizer = SimpleNamespace(no_timestamps=7, eot=6, timestamp_begin=8)
    original = ApplyTimestampRules(tokenizer, sample_begin=1, max_initial_timestamp_index=None)
    rules = BeamTimestampRules(original)
    # After the opening timestamp only text is possible. A high raw score on
    # a now-forbidden timestamp must not suppress all remaining text too.
    logits = mx.array([[0., 1., 0., 0., 0., 0., 0., 0., 10., 0., 0., 0.]])
    actual = rules.apply(logits, mx.array([[0, 8]]))
    assert mx.isfinite(actual[:, :6]).all().item()
    assert mx.isneginf(actual[:, 8:]).all().item()


def test_timestamp_rules_use_token_values_instead_of_sequence_positions():
    tokenizer = SimpleNamespace(no_timestamps=7, eot=6, timestamp_begin=8)
    rules = BeamTimestampRules(ApplyTimestampRules(tokenizer, sample_begin=1, max_initial_timestamp_index=None))
    # The previous completed segment ends at timestamp token 10, not index 2.
    # A new segment cannot start at 8, 9 or 10.
    actual = rules.apply(mx.zeros((1, 12)), mx.array([[0, 8, 2, 10, 11]]))
    assert mx.isneginf(actual[:, 8:]).all().item()
    actual = rules.apply(mx.zeros((1, 12)), mx.array([[0, 8, 2, 10, 11, 3]]))
    assert mx.isneginf(actual[:, 8:12]).all().item()


def test_upstream_task_accepts_one_unique_finished_candidate_for_a_two_row_beam():
    # Upstream reshapes active rows BEFORE finalize, then ranks however many
    # unique finished candidates remain. Exercise that exact contract.
    model = SimpleNamespace(is_multilingual=False, num_languages=99,
                            dims=SimpleNamespace(n_text_ctx=448, n_vocab=51864,
                                                 n_audio_ctx=2, n_audio_state=2))
    task = _task_type()(model, DecodingOptions(beam_size=2, language="en", fp16=False,
                                              without_timestamps=True), lambda _: None)
    def finish(audio_features, tokens):
        task.decoder.search.start(tokens.tolist())
        task.decoder.search.advance([[(task.tokenizer.eot, -1., -1.)], []])
        return tokens, mx.zeros(2), mx.zeros(2)
    task._main_loop = finish
    result = task.run(mx.zeros((1, 2, 2)))[0]
    assert result.tokens == [] and result.avg_logprob == pytest.approx(-1.)
