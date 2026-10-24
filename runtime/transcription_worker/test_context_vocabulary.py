from __future__ import annotations

import copy

from .context_vocabulary import rank_indices


def _journal(*records):
    return {"version": 1, "observations": list(records)}


def _record(identity, at, scene, *corrections, weight=1):
    return {"id": identity, "at": at, "weight": weight, "scene": scene, "corrections": list(corrections)}


def _word(meant, heard=None, *, word=True):
    return {"heard": heard or meant, "meant": meant, "word": word, "alias": False, "secret": False}


def test_cold_unknown_and_legacy_scene_fallback_exactly():
    entries = [{"text": "alpha"}, {"text": "beta"}]
    assert rank_indices(entries, None, "terminal") == [0, 1]
    assert rank_indices(entries, _journal(), "unknown") == [0, 1]
    assert rank_indices(entries, _journal(_record("x", 1, None, _word("alpha"))), "terminal") == [0, 1]
    assert rank_indices(entries, _journal(_record("x", 1, "terminal", _word("alpha"))), {}) == [0, 1]
    assert rank_indices(entries, _journal(_record("x", 1, {}, _word("alpha"))), "terminal") == [0, 1]
    assert rank_indices(entries, _journal(_record("x", 1, "terminal", _word("alpha"))), []) == [0, 1]


def test_context_rescues_term_from_a_41_word_beam():
    entries = [{"text": f"term-{i}"} for i in range(41)]
    evidence = _journal(_record("seen", 1_000, "terminal", _word("term-40"), weight=4))
    assert rank_indices(entries, evidence, "terminal", now_ms=1_000)[0] == 40


def test_global_prior_keeps_cross_app_word_when_local_context_is_sparse():
    entries = [{"text": "alpha"}, {"text": "beta"}]
    evidence = _journal(
        _record("other", 1_000, "wechat", _word("beta"), weight=4),
        _record("local", 2_000, "terminal", _word("alpha")),
    )
    assert set(rank_indices(entries, evidence, "terminal", now_ms=2_000)) == {0, 1}


def test_negative_other_scene_does_not_reorder_other_scene():
    entries = [{"text": "alpha"}, {"text": "beta"}]
    evidence = _journal(
        _record("local", 1_000, "other", _word("alpha")),
        _record("terminal-negative", 2_000, "terminal", _word("beta", "alpha")),
    )
    assert rank_indices(entries, evidence, "other", now_ms=2_000) == [0, 1]


def test_negative_in_another_scene_does_not_change_terminal_order():
    entries = [{"text": "alpha", "count": 3, "opposition": 0},
               {"text": "beta", "count": 1, "opposition": 0}]
    baseline = _journal(_record("local", 1_000, "terminal", _word("alpha")))
    with_negative = _journal(
        _record("local", 1_000, "terminal", _word("alpha")),
        _record("negative", 2_000, "other", _word("beta", "alpha")),
    )
    assert rank_indices(entries, baseline, "terminal", now_ms=2_000) == rank_indices(
        entries, with_negative, "terminal", now_ms=2_000
    )


def test_repeated_correction_inside_one_record_counts_once():
    entries = [{"text": "alpha"}, {"text": "beta"}]
    competing = _record("a", 1_000, "terminal", _word("alpha"))
    single = _record("b", 1_000, "terminal", _word("beta"))
    repeated = _record("b", 1_000, "terminal", _word("beta"), _word("beta"))
    assert rank_indices(entries, _journal(competing, single), "terminal", now_ms=1_000) == [0, 1]
    assert rank_indices(entries, _journal(competing, repeated), "terminal", now_ms=1_000) == [0, 1]


def test_unicode_keys_preserve_the_native_learning_and_merge_identity():
    entries = [{"text": "FussApp"}, {"text": "FußApp"}]
    evidence = _journal(_record("fix", 1_000, "terminal", _word("FußApp", "FussApp")))
    assert rank_indices(entries, evidence, "terminal", now_ms=1_000) == [1, 0]


def test_duplicate_ids_and_malformed_journal_fall_back():
    entries = [{"text": "alpha"}, {"text": "beta"}]
    row = _record("x", 1_000, "terminal", _word("beta"))
    assert rank_indices(entries, _journal(row, copy.deepcopy(row)), "terminal") == [0, 1]
    bad = _record("bad", 1_000, "terminal", _word("beta"))
    bad["weight"] = True
    assert rank_indices(entries, _journal(bad), "terminal") == [0, 1]


def test_no_input_mutation_and_taught_is_first_tier():
    entries = [{"text": "ordinary"}, {"text": "manual", "origin": "taught"}]
    before = copy.deepcopy(entries)
    evidence = _journal(_record("x", 1_000, "terminal", _word("ordinary"), weight=4))
    assert rank_indices(entries, evidence, "terminal", now_ms=1_000) == [1, 0]
    assert entries == before


def test_recency_bonus_is_bounded_and_future_time_does_not_explode():
    entries = [{"text": "old"}, {"text": "new"}]
    evidence = _journal(
        _record("old", 1_000, "terminal", _word("old")),
        _record("new", 86_400_000, "terminal", _word("new")),
    )
    assert rank_indices(entries, evidence, "terminal", now_ms=86_400_000) == [1, 0]
    assert rank_indices(entries, evidence, "terminal", now_ms=0) == [0, 1]
