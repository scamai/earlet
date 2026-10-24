"""Pure-Python tests for the source-only phonetic-match constraint.

Requires g2p_en and its declared dependencies, plus the NLTK cmudict and
averaged_perceptron_tagger_eng resources in an isolated test environment.

Run: .venv/Scripts/python.exe -m pytest transcription_worker/test_phonetic_match.py
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

# g2p_en is an isolated test dependency. The packaged runtime does not ship
# this source-only utility, so collection skips cleanly when it is unavailable.
pytest.importorskip(
    "g2p_en",
    reason="g2p_en is an optional test dependency",
)

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.phonetic_match import (  # noqa: E402
    find_correction,
    g2p,
    phonetic_distance,
    scan_for_corrections,
)

# Synthetic entries exercise phonetic distance without carrying private names.
LEXICON = ["Asteron", "Fable", "RetinaFace", "Snapdragon", "code-x2.3.20"]


# --- g2p sanity ---------------------------------------------------------------


def test_g2p_returns_arpabet_phonemes_only():
    phonemes = g2p("cat")
    assert phonemes == ["K", "AE1", "T"]
    # no leftover space/punctuation separator tokens
    assert all(p.isalnum() for p in phonemes)


def test_g2p_multiword_phrase_drops_space_separators():
    phonemes = g2p("Amber Lane")
    assert " " not in phonemes
    assert phonemes[0] == "AE1"


# --- phonetic_distance sanity properties --------------------------------------


def test_distance_zero_for_identical_word():
    assert phonetic_distance("Snapdragon", "Snapdragon") == 0


def test_distance_zero_for_identical_phrase():
    assert phonetic_distance("code-x2.3.20", "code-x2.3.20") == 0


def test_distance_is_symmetric():
    a, b = "a steron", "Asteron"
    assert phonetic_distance(a, b) == phonetic_distance(b, a)


def test_distance_is_symmetric_for_unrelated_words():
    a, b = "banana", "RetinaFace"
    assert phonetic_distance(a, b) == phonetic_distance(b, a)


# --- synthetic near-misses ----------------------------------------------------


def test_a_steron_corrects_to_asteron():
    assert find_correction("a steron", LEXICON, max_distance=3) == "Asteron"


def test_fabl_o_corrects_to_fable():
    assert find_correction("fabl o", LEXICON, max_distance=3) == "Fable"


def test_retina_face_corrects_to_retinaface():
    assert find_correction("retina face", LEXICON, max_distance=3) == "RetinaFace"


def test_snap_dragon_corrects_to_snapdragon():
    assert find_correction("snap dragon", LEXICON, max_distance=3) == "Snapdragon"


# --- distant words must NOT get corrected (anti-hallucination guard) ---------


def test_unrelated_word_returns_none():
    assert find_correction("banana", LEXICON, max_distance=3) is None


def test_unrelated_phrase_returns_none():
    assert find_correction("quarterly report", LEXICON, max_distance=3) is None


def test_empty_lexicon_returns_none():
    assert find_correction("Asteron", [], max_distance=3) is None


# --- threshold boundary behavior ----------------------------------------------


def test_distance_at_threshold_is_corrected():
    # "retina face" -> "RetinaFace" measures exactly 3; max_distance=3 (the
    # default) must include it -- boundary is inclusive (<=), documented in
    # find_correction()'s docstring.
    assert phonetic_distance("retina face", "RetinaFace") == 3
    assert find_correction("retina face", LEXICON, max_distance=3) == "RetinaFace"


def test_distance_just_over_threshold_is_rejected():
    # Same pair, but with max_distance one below its actual distance (2): the
    # guard must refuse rather than guess.
    assert find_correction("retina face", LEXICON, max_distance=2) is None


def test_closest_entry_wins_when_multiple_within_threshold():
    # A candidate closest to one entry should not accidentally match another
    # merely because both happen to be within max_distance.
    assert find_correction("Asteron", LEXICON, max_distance=3) == "Asteron"


# --- scan_for_corrections (detection over free text) -------------------------


def test_scan_finds_single_word_mishearing_in_a_sentence():
    text = "Can you ping a steron about the release?"
    matches = scan_for_corrections(text, LEXICON, max_distance=3)
    assert len(matches) == 1
    m = matches[0]
    assert m["correction"] == "Asteron"
    assert m["original"].lower() == "a steron"


def test_scan_does_not_yet_catch_version_string_expansions():
    # Structured or technical notation may not have a canonical spoken form;
    # phonetic-distance scanning alone can therefore miss it even with a wide
    # window. Keep this limitation visible instead of widening the threshold.
    text = "The code x two three twenty cut passed on the device."
    assert phonetic_distance("code x two three twenty", "code-x2.3.20") > 3
    assert scan_for_corrections(text, LEXICON, max_window=6) == []


def test_scan_finds_multiple_non_overlapping_matches():
    text = "a steron said retina face passed the build."
    matches = scan_for_corrections(text, LEXICON, max_distance=3)
    corrections = {m["correction"] for m in matches}
    assert corrections == {"Asteron", "RetinaFace"}
    # non-overlapping: sorted by start, no span reuses a word index
    for a, b in zip(matches, matches[1:]):
        assert a["end"] <= b["start"]


def test_scan_returns_empty_for_text_with_no_entities():
    text = "The quarterly report looked pretty solid this time around."
    assert scan_for_corrections(text, LEXICON) == []


def test_scan_matches_exact_single_word_without_over_extending():
    text = "Fable flagged it."
    matches = scan_for_corrections(text, LEXICON)
    assert len(matches) == 1
    assert matches[0]["correction"] == "Fable"
    assert matches[0]["original"] == "Fable"


def test_scan_prefers_longest_window_over_lowest_distance():
    # A shorter prefix is closer, but the longer span also clears the
    # configured cutoff. Prefer the full span so no adjacent token is left.
    text = "fabl o flagged the account."
    assert phonetic_distance("fabl", "Fable") == 1
    assert phonetic_distance("fabl o", "Fable") == 2
    matches = scan_for_corrections(text, LEXICON, max_distance=3)
    assert len(matches) == 1
    assert matches[0]["original"] == "fabl o"
    assert matches[0]["correction"] == "Fable"


def test_the_default_radius_stays_conservative():
    """Synthetic near-misses stay rejected at the conservative default."""
    lexicon = ["Asteron", "RetinaFace", "Fable"]

    # Two of the three are given up.
    for heard in ("a steron", "retina face"):
        assert scan_for_corrections(heard, lexicon) == [], (
            f"{heard!r} matched at the default radius; if the default was raised, "
            f"re-evaluate the caller's threshold before accepting the change"
        )
        assert scan_for_corrections(heard, lexicon, max_distance=3), (
            f"{heard!r} no longer matches even at distance 3 — the mechanism "
            f"itself regressed, which is a different and worse problem"
        )

    # The conservative default still admits the one-word prefix. Tightening
    # the radius changes which window wins, rather than erasing all recall.
    matches = scan_for_corrections("fabl o flagged the account.", lexicon)
    assert len(matches) == 1
    assert matches[0]["original"] == "fabl"
    assert matches[0]["correction"] == "Fable"
