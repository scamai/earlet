"""Model-free tests for the optional source-only stream utilities."""
from transcription_worker.stream_helpers import LanguageVote, merge_turns

def test_merge_adjacent_same_speaker():
    segs = [(0.0, 5.0, 0), (5.4, 9.0, 0), (9.5, 12.0, 1), (12.3, 14.0, 1)]
    assert merge_turns(segs) == [(0.0, 9.0, 0), (9.5, 14.0, 1)]

def test_merge_respects_speaker_change_and_gap():
    segs = [(0.0, 4.0, 0), (4.2, 8.0, 1), (12.0, 15.0, 1)]  # 4s gap splits
    assert merge_turns(segs) == [(0.0, 4.0, 0), (4.2, 8.0, 1), (12.0, 15.0, 1)]

def test_merge_caps_turn_length():
    segs = [(0.0, 20.0, 0), (20.2, 40.0, 0)]
    out = merge_turns(segs, max_turn_s=25.0)
    assert out == [(0.0, 20.0, 0), (20.2, 40.0, 0)]  # merge would exceed cap

def test_merge_absorbs_sliver_into_open_turn_but_drops_isolated():
    segs = [(0.0, 5.0, 0), (5.1, 5.3, 0),   # 0.2s tail of speaker 0 — absorbed
            (8.0, 8.2, 1)]                  # isolated 0.2s sliver — dropped
    assert merge_turns(segs) == [(0.0, 5.3, 0)]

def test_language_vote_seeded_languages_always_trusted():
    v = LanguageVote()
    assert not v.suspect("zh", 1.0, 2)      # seeded: never suspected, any length
    assert not v.suspect("en", 1.0, 3)
    v.record("en"); v.record("en")
    assert v.majority() == "en"

def test_language_vote_nonseed_never_earns_trust_from_detector_outputs():
    v = LanguageVote()
    v.record("zh"); v.record("zh")          # profiled take
    assert v.suspect("ja", 10.0, 30)        # zh->ja flip on a LONG turn: caught
    assert v.suspect("ko", 1.0, 2)
    # Repeated detector guesses are not independent confirmation.
    v.record("ja"); v.record("ja")
    assert v.suspect("ja", 1.0, 2)

def test_language_vote_unprofiled_take_pins_nonseed_from_first_span():
    v = LanguageVote()
    assert v.suspect("tr", 10.0, 30)
    assert v.suspect("tr", 1.0, 3)
    assert v.pin_for("hallo heimur") == "en"
    assert v.pin_for("这是一段中文") == "zh"
    v.record("en"); v.record("en")
    assert v.pin_for("这是另一段中文内容") == "zh"  # script beats en majority
    assert v.pin_for("bonjour") == "en"

def test_language_vote_custom_declared_languages_are_authoritative():
    v = LanguageVote(seed=("zh", "fr"))
    assert not v.suspect("fr", 1.0, 2)
    assert v.suspect("en", 10.0, 30)
    assert v.pin_for("hello") == "fr"

def test_effective_word_count_handles_cjk():
    from transcription_worker.stream_helpers import effective_word_count
    assert effective_word_count("hello there world") == 3
    assert effective_word_count("特朗普与土耳其总统通话后发表了声明") == 17
    assert effective_word_count("他说 hello 然后离开") == 7  # 6 CJK chars + 1 word
    assert effective_word_count("嗯") == 1

def test_merge_turns_splits_overlong_segments():
    out = merge_turns([(0.0, 30.0, 0)], max_turn_s=12.0)
    assert len(out) == 3
    assert all(e - s <= 12.01 for s, e, _ in out)
    assert abs(out[0][0] - 0.0) < 1e-6 and abs(out[-1][1] - 30.0) < 1e-6
