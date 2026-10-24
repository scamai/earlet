"""Pure-function tests for attribute.py -- no models, no audio, no I/O.

Run: python -m pytest transcription_worker/test_attribute.py
(plain python3, no venv needed -- attribute.py has zero third-party deps)
"""

from __future__ import annotations

from transcription_worker.attribute import assign_speaker, attribute


def _word(w, s, e):
    return {"word": w, "start": s, "end": e}


def _turn(s, e, speaker):
    return {"start": s, "end": e, "speaker": speaker}


# --- assign_speaker: overlap ------------------------------------------------

def test_assign_speaker_clear_overlap():
    turns = [_turn(0.0, 5.0, "S0"), _turn(5.0, 10.0, "S1")]
    assert assign_speaker(_word("hi", 1.0, 1.5), turns) == "S0"
    assert assign_speaker(_word("bye", 6.0, 6.5), turns) == "S1"


def test_assign_speaker_no_turns_returns_none():
    assert assign_speaker(_word("hi", 0.0, 1.0), []) is None


def test_assign_speaker_boundary_straddle_majority_wins():
    # Word spans [4.5, 5.5): 0.5s in S0's turn, 0.5s in S1's turn -- a tie.
    # Give S1 more (word ends further into S1's turn) and confirm majority.
    turns = [_turn(0.0, 5.0, "S0"), _turn(5.0, 10.0, "S1")]
    assert assign_speaker(_word("word", 4.8, 5.6), turns) == "S1"  # 0.2s S0, 0.6s S1
    assert assign_speaker(_word("word", 4.2, 5.2), turns) == "S0"  # 0.8s S0, 0.2s S1


def test_assign_speaker_gap_falls_back_to_nearest():
    # Word falls entirely in a gap between two turns (no overlap with
    # either) -- fall back to whichever turn is closer.
    turns = [_turn(0.0, 2.0, "S0"), _turn(3.0, 5.0, "S1")]
    assert assign_speaker(_word("word", 2.1, 2.3), turns) == "S0"  # closer to S0's end
    assert assign_speaker(_word("word", 2.7, 2.9), turns) == "S1"  # closer to S1's start


# --- attribute: end-to-end merge -------------------------------------------

def test_attribute_empty_words_returns_empty():
    assert attribute([], [_turn(0.0, 5.0, "S0")]) == []


def test_attribute_empty_turns_falls_back_to_single_unknown_segment():
    words = [_word("hello", 0.0, 0.5), _word("world", 0.5, 1.0)]
    segs = attribute(words, [])
    assert len(segs) == 1
    assert segs[0]["speaker"] == "unknown"
    assert segs[0]["text"] == "hello world"
    assert segs[0]["start"] == 0.0
    assert segs[0]["end"] == 1.0


def test_attribute_two_speaker_alternation():
    words = [
        _word("hi", 0.0, 0.3), _word("there", 0.3, 0.6),
        _word("hello", 5.2, 5.5), _word("back", 5.5, 5.9),
        _word("ok", 9.0, 9.3), _word("bye", 9.3, 9.6),
    ]
    turns = [_turn(0.0, 4.5, "S0"), _turn(4.8, 8.5, "S1"), _turn(8.8, 10.0, "S0")]

    segs = attribute(words, turns)
    assert [s["speaker"] for s in segs] == ["S0", "S1", "S0"]
    assert segs[0]["text"] == "hi there"
    assert segs[1]["text"] == "hello back"
    assert segs[2]["text"] == "ok bye"
    # segment span = first_word.start .. last_word.end within that speaker run
    assert segs[0]["start"] == 0.0 and segs[0]["end"] == 0.6
    assert segs[1]["start"] == 5.2 and segs[1]["end"] == 5.9
    assert segs[2]["start"] == 9.0 and segs[2]["end"] == 9.6


def test_attribute_merges_adjacent_same_speaker_across_multiple_turns():
    # Two consecutive turns both labelled S0 (e.g. diarizer emitted two
    # short turns for one continuous utterance) -- words spanning both
    # should merge into a single S0 segment, not split at the turn seam.
    words = [_word("a", 0.0, 0.5), _word("b", 1.0, 1.5), _word("c", 2.0, 2.5)]
    turns = [_turn(0.0, 1.2, "S0"), _turn(1.2, 3.0, "S0")]
    segs = attribute(words, turns)
    assert len(segs) == 1
    assert segs[0]["speaker"] == "S0"
    assert segs[0]["text"] == "a b c"


def test_attribute_blank_words_are_skipped():
    words = [_word("", 0.0, 0.1), _word("real", 0.1, 0.5)]
    turns = [_turn(0.0, 1.0, "S0")]
    segs = attribute(words, turns)
    assert len(segs) == 1
    assert segs[0]["text"] == "real"


def test_attribute_single_speaker_whole_meeting():
    words = [_word(f"w{i}", i * 1.0, i * 1.0 + 0.5) for i in range(5)]
    turns = [_turn(0.0, 10.0, "S0")]
    segs = attribute(words, turns)
    assert len(segs) == 1
    assert segs[0]["speaker"] == "S0"
    assert segs[0]["text"] == "w0 w1 w2 w3 w4"
