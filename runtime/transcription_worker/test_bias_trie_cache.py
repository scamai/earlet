"""The trie is built once per vocabulary, and caching it changes nothing else.

`BiasTrie` is a pure function of (ordered phrases, tokenizer, δ, bonus,
per-phrase δs) and is read-only after construction, which is what makes one
built tree safe to serve to every decode that asks for the same thing. Both
halves are asserted here rather than assumed, because the cache is only correct
while they hold: a tree that could be mutated mid-decode, or that differed run
to run, would make a cache hit a silent behaviour change.

These run anywhere — no torch, no ONNX, no model on disk.
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.beam_decode import (  # noqa: E402
    BiasState,
    BiasTrie,
    BiasTrieCache,
)

PHRASES = ["Trovioo", "ArcFace", "Knemo", "Fable Flow"]


class Tok:
    """Deterministic and cheap. Identity is what the cache compares, so each
    instance is deliberately a distinct object."""

    def __init__(self, name: str = "a"):
        self.name = name

    def encode(self, text, add_special_tokens=False):
        return [ord(c) for c in text]


def fingerprint(trie: BiasTrie) -> str:
    """A structural hash: every path, and the phrase ids at its node."""
    out = []

    def walk(node, path):
        out.append((tuple(path), tuple(sorted(node.phrase_ids))))
        for token in sorted(node.children):
            walk(node.children[token], path + [token])

    walk(trie.root, [])
    return hashlib.sha256(json.dumps(out).encode()).hexdigest()


def build(phrases=PHRASES, tokenizer=None, *, delta=2.5, bonus=0.0, deltas=None):
    trie = BiasTrie(phrases, tokenizer or Tok(), delta=delta, completion_bonus=bonus)
    if deltas:
        trie.set_phrase_deltas(deltas)
    return trie


# ---------------------------------------------------------------------------
# The two properties the cache rests on
# ---------------------------------------------------------------------------


def test_the_same_inputs_build_the_same_tree():
    """Purity. Without this the cache would be serving a coin flip."""
    assert fingerprint(build()) == fingerprint(build())


def test_reordering_the_list_builds_a_different_tree():
    """`phrase_id` is the phrase's INDEX, so order is part of the identity.

    This is why `BiasTrieCache.key` uses an ordered tuple and not a set. With a
    set key, a caller that reordered its list would be served a tree whose
    `set_phrase_deltas` map binds each δ to the wrong phrase — silently.
    """
    assert fingerprint(build()) != fingerprint(build(list(reversed(PHRASES))))


def test_a_duplicate_entry_builds_a_different_tree():
    """A repeated phrase earns a second `phrase_id` on the same leaf."""
    assert fingerprint(build()) != fingerprint(build(PHRASES + [PHRASES[0]]))


def test_decoding_does_not_mutate_the_trie():
    """Read-only after construction — the other half of the cache's licence.

    `BiasState` is immutable and returns new states; `delta_for_token` only
    reads `_node_delta`. If a decode could write to the tree, a second decode
    served the cached one would start from a different object than the first.
    """
    trie = build(deltas={0: 3.0, 1: 1.0})
    before = fingerprint(trie)

    state = trie.initial_state()
    for token in Tok().encode(" Trovioo") + Tok().encode(" ArcFace"):
        trie.delta_for_token(state.nodes, token)
        state, _ = state.advance(token)

    assert fingerprint(trie) == before
    assert isinstance(state, BiasState)


# ---------------------------------------------------------------------------
# The cache itself
# ---------------------------------------------------------------------------


def test_a_repeat_request_returns_the_very_same_object():
    """The point of the whole change: no second build.

    Identity, not equality — an equal-but-rebuilt tree would mean the cost is
    still being paid.
    """
    cache, tok, builds = BiasTrieCache(), Tok(), []

    def make():
        builds.append(1)
        return build(tokenizer=tok)

    key = BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=0.0)
    first = cache.get(key, tok, make)
    second = cache.get(key, tok, make)

    assert first is second
    assert len(builds) == 1, "the second request rebuilt the tree"


def test_a_cached_tree_is_identical_to_a_freshly_built_one():
    """The cache must be invisible in the output, not merely fast."""
    cache, tok = BiasTrieCache(), Tok()
    key = BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=0.0)

    cached = cache.get(key, tok, lambda: build(tokenizer=tok))
    assert fingerprint(cached) == fingerprint(build(tokenizer=tok))
    assert cached.delta == build(tokenizer=tok).delta


def test_every_input_that_changes_the_tree_changes_the_key():
    """A key that collided would serve a tree built for something else."""
    base = BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=0.0)

    assert BiasTrieCache.key(list(reversed(PHRASES)), delta=2.5, completion_bonus=0.0) != base
    assert BiasTrieCache.key(PHRASES + ["Fable"], delta=2.5, completion_bonus=0.0) != base
    assert BiasTrieCache.key(PHRASES, delta=1.0, completion_bonus=0.0) != base
    assert BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=3.0) != base
    assert BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=0.0,
                             phrase_deltas={0: 3.0}) != base


def test_the_key_survives_the_json_round_trip_the_deltas_arrive_through():
    """Per-phrase δs cross the wire as JSON, so `{"0": "3.0"}` must key the
    same as `{0: 3.0}` — otherwise one utterance builds and the next rebuilds,
    for ever, and the cache silently never hits."""
    assert (BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=0.0,
                              phrase_deltas={"0": "3.0"})
            == BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=0.0,
                                 phrase_deltas={0: 3.0}))


def test_a_different_tokenizer_object_rebuilds():
    """Compared by identity: two tokenizers that look alike can encode
    differently, and serving the wrong tree is worse than rebuilding."""
    cache, key = BiasTrieCache(), BiasTrieCache.key(
        PHRASES, delta=2.5, completion_bonus=0.0)
    one, two = Tok("one"), Tok("two")

    first = cache.get(key, one, lambda: build(tokenizer=one))
    second = cache.get(key, two, lambda: build(tokenizer=two))

    assert first is not second


def test_the_entry_holds_its_tokenizer_so_an_address_cannot_be_reused():
    """`id()` alone would be unsound — a collected tokenizer's address can be
    handed to a new object, and the entry would then serve a tree built for a
    different vocabulary. Holding a reference makes that unrepresentable."""
    cache, tok = BiasTrieCache(), Tok()
    key = BiasTrieCache.key(PHRASES, delta=2.5, completion_bonus=0.0)
    cache.get(key, tok, lambda: build(tokenizer=tok))

    held = [entry[0] for entry in cache._entries.values()]
    assert any(item is tok for item in held)


def test_the_cache_is_bounded():
    """Bounded, so a caller that varies its list cannot grow a leak."""
    cache = BiasTrieCache()
    tok = Tok()
    for i in range(BiasTrieCache.CAPACITY + 3):
        key = BiasTrieCache.key(PHRASES + [f"Extra{i}"], delta=2.5, completion_bonus=0.0)
        cache.get(key, tok, lambda: build(tokenizer=tok))

    assert len(cache._entries) <= BiasTrieCache.CAPACITY


def test_the_oldest_entry_is_the_one_dropped():
    """Eviction order stated, so a future change to it fails here."""
    cache, tok = BiasTrieCache(), Tok()
    keys = [BiasTrieCache.key(PHRASES + [f"E{i}"], delta=2.5, completion_bonus=0.0)
            for i in range(BiasTrieCache.CAPACITY + 1)]
    for key in keys:
        cache.get(key, tok, lambda: build(tokenizer=tok))

    assert keys[0] not in cache._entries
    assert keys[-1] in cache._entries
