"""Long-form (meeting) junk-filter regression for the mac engine.

Separate from test_junk_filter.py because that file imports `whisper_qnn`,
which needs `onnxruntime_qnn` and is unimportable on macOS. Same heuristics,
mac copy, plus the long-form carve-out.

THE BUG THIS GUARDS
-------------------
`op_transcribe` over the real 24m48s reference meeting decoded 4,269 words at
min_logprob -0.27 (a confident decode) and then returned `text: ""` with
`ok: true`. Cause: `looks_like_junk`'s compression-ratio rule (>2.4:1 = a
decoder loop). That threshold is calibrated for ONE <=30s Whisper window; on
concatenated long-form output ordinary English crosses it at ~100 words
(measured 100w -> 2.64, 500w -> 4.09, 4,269w -> 5.46) purely because zlib
gets a bigger dictionary. So every meeting longer than a paragraph produced
an empty transcript, empty diarization merge and empty notes, with no error
anywhere.

Run: macos/runtime/bin/python3.11 -m pytest transcription_worker/test_junk_filter_mac.py
"""

import random

from transcription_worker.whisper_mac import LONG_FORM_AUDIO_S, looks_like_junk


def _long_natural_english(n_words: int) -> str:
    words = ("we talked about the roadmap and the timeline for hiring plus "
             "budget review before the launch window closes next quarter").split()
    rng = random.Random(0)
    return " ".join(rng.choice(words) for _ in range(n_words))


def test_utterance_mode_behaviour_is_unchanged():
    assert looks_like_junk("(whisper)")
    assert looks_like_junk("[BLANK_AUDIO]")
    assert looks_like_junk("snap " * 50)
    assert looks_like_junk("yes yes yes yes yes yes yes yes")
    assert not looks_like_junk("Yes.")
    assert not looks_like_junk("Okay, sounds good.")
    # and the rule we are carving out of, on long text, still fires by default
    assert looks_like_junk(_long_natural_english(500))


def test_long_natural_english_survives_long_form():
    for n in (100, 500, 2000, 4269):
        assert not looks_like_junk(_long_natural_english(n), long_form=True), n


def test_long_form_still_catches_real_junk():
    assert looks_like_junk("snap " * 200, long_form=True)      # repetition rule
    assert looks_like_junk("[BLANK_AUDIO]", long_form=True)    # annotation rule
    assert looks_like_junk("", long_form=True)                 # empty rule


def test_short_utterances_unaffected_by_the_flag():
    for text in ("Yes.", "Okay, sounds good.", "(laughs) good point",
                 "Can you ping Avrit about the release cut before stand up?"):
        assert looks_like_junk(text) == looks_like_junk(text, long_form=True)


def test_long_form_threshold_is_well_above_any_dictation_utterance():
    # Whisper's own window is 30s; the gate must not trip on dictation.
    assert LONG_FORM_AUDIO_S >= 120.0


# ---------------------------------------------------------------------------
# Hallucination-phrase blocklist + energy pre-check (Task D)
#
# THE BUG THESE GUARD
# -------------------
# Meeting notes shipped with NO silence gate (dictation's W-2 gate was never
# ported), so every quiet stretch of a real meeting was handed to Whisper.
# Whisper does not return nothing for silence: the decoder free-runs and emits
# training-set artifacts — "Thank you.", "Thanks for watching", YouTube outros
# — which landed in the transcript ATTRIBUTED TO A REAL PERSON. The existing
# shared guards do not catch it: `drop_no_speech_segments` needs
# no_speech>0.6 AND avg_logprob<-1.0 (a confident "Thank you." decode trips
# neither) and `looks_like_junk` has no rule that fires on two words.
#
# The Swift gate (the Mac's AudioGate) is the primary fix; this is the
# backstop for the residue that still gets through, and for every non-Swift
# client of the wire protocol.
# ---------------------------------------------------------------------------

from transcription_worker.whisper_mac import (  # noqa: E402
    HALLUCINATION_PHRASES,
    MAX_BLOCKABLE_WORDS,
    SAMPLE_RATE,
    blocked_hallucination,
    has_speech_energy,
)

import numpy as np  # noqa: E402


def test_artifact_phrases_are_blocked_when_the_caller_asks_for_the_guard():
    for text in ("Thank you.", "thank you", "THANK YOU!", "  Thank you . ",
                 "Thanks for watching", "Please subscribe.",
                 "Thank you very much."):
        assert blocked_hallucination(text, hallucination_guard=True), text


def test_non_english_outro_artifacts_are_blocked():
    for phrase in ("谢谢", "谢谢大家", "字幕由 Amara.org 社区提供",
                   "ご視聴ありがとうございました"):
        assert blocked_hallucination(phrase, hallucination_guard=True), phrase


def test_the_guard_is_off_by_default():
    # An unknown caller must get the text that was actually decoded. The
    # blocklist is opt-in per pipeline, never ambient.
    for text in ("Thank you.", "谢谢", "Thanks for watching"):
        assert not blocked_hallucination(text), text
        assert not blocked_hallucination(text, hallucination_guard=False), text


def test_dictation_shaped_call_never_blocks_real_words():
    # THE REGRESSION THIS PINS: the first cut keyed applicability on
    # voiced_fraction < 0.15. Dictation gates with NO floors by design, so a
    # real "谢谢" spoken inside a long key-hold measures well under 0.15 and
    # was silently deleted -> nothingHeard. Dictation sends guard=False, so
    # the phrase survives no matter how thin the clip was.
    assert not blocked_hallucination("谢谢", hallucination_guard=False)
    assert not blocked_hallucination("Thank you very much.", hallucination_guard=False)


def test_notes_shaped_call_blocks_at_and_above_the_swift_gate_fraction():
    # ...and the mirror image: notes' own gate only ever forwards clips at or
    # above ITS minVoicedFraction (0.15), so a rule keyed on `< 0.15` could
    # never fire on the one path the blocklist exists for. Applicability is
    # now the caller's explicit declaration, with no numeric coupling to any
    # Swift gate constant, so a 0.16-voiced clip decoding to "Thank you." is
    # blocked like any other.
    assert blocked_hallucination("Thank you.", hallucination_guard=True)


def test_a_long_transcript_containing_the_phrase_survives():
    long_text = ("Thank you for joining, let's start with the roadmap and then "
                 "we can go through the hiring plan before the launch window closes.")
    assert not blocked_hallucination(long_text, hallucination_guard=True)
    assert not blocked_hallucination(long_text)
    # The short-output rule is what protects it: well over the word cap.
    assert len(long_text.split()) > MAX_BLOCKABLE_WORDS


def test_ordinary_short_speech_is_not_blocked():
    for text in ("Yes.", "Okay, sounds good.", "I agree.", "Ship it.",
                 "Can you repeat that?"):
        assert not blocked_hallucination(text, hallucination_guard=True), text
        assert not blocked_hallucination(text), text


def test_blocklist_is_non_empty_and_normalized():
    assert len(HALLUCINATION_PHRASES) >= 9
    # Every entry must already be in normalized form, or the lookup silently
    # never matches it.
    for phrase in HALLUCINATION_PHRASES:
        assert phrase == phrase.strip().casefold()


def test_energy_precheck_rejects_silence_and_keeps_speech():
    silence = np.zeros(SAMPLE_RATE, dtype=np.float32)
    assert not has_speech_energy(silence)
    # Digital noise well under the 0.004 RMS floor is still silence.
    assert not has_speech_energy(np.full(SAMPLE_RATE, 0.001, dtype=np.float32))

    t = np.arange(SAMPLE_RATE, dtype=np.float32) / SAMPLE_RATE
    speech = (0.08 * np.sin(2 * np.pi * 220 * t)).astype(np.float32)
    assert has_speech_energy(speech)
    # A 120ms word inside a quiet second must NOT be pre-rejected here — the
    # per-pipeline voiced floor lives in the Swift gate, this is the
    # nothing-at-all backstop only.
    blip = np.zeros(SAMPLE_RATE, dtype=np.float32)
    blip[:1920] = speech[:1920]
    assert has_speech_energy(blip)


def test_energy_precheck_handles_empty_audio():
    assert not has_speech_energy(np.zeros(0, dtype=np.float32))


# ---------------------------------------------------------------------------
# Stock hallucinations, and the copy-drift that let them through on this side
# ---------------------------------------------------------------------------


def test_stock_hallucinations_are_dropped():
    """The YouTube-ish phrases turbo emits over near-silence.

    They read as ordinary English, so neither the repetition rule (needs one
    word >60% of the text) nor the compression rule (needs >2.4:1) sees them.
    Both field cases from 2026-08-21 are here by name.
    """
    for text in (
        "tune in the middle of the road",
        "tune in the background",
        "Thanks for watching!",
        "Please subscribe to the channel",
        "See you in the next video",
        "For more information visit www.example.com",
    ):
        assert looks_like_junk(text), f"stock hallucination survived: {text!r}"


def test_real_speech_that_merely_starts_like_one_survives():
    """The rule is scoped to SHORT output for a reason.

    Somebody genuinely saying "stay tuned" inside a sentence, or a long turn
    that happens to open with one of these, is real speech. Only a segment
    that is essentially nothing but the stock phrase (<=12 words) is dropped.
    """
    long_turn = ("thanks for watching the demo earlier today i wanted to walk "
                 "through what changed in the release and why we moved the "
                 "date")
    assert len(long_turn.split()) > 12
    assert not looks_like_junk(long_turn)
    assert not looks_like_junk("we should stay tuned to what finance says")




# ---------------------------------------------------------------------------
# The stock-phrase rule follows the caller's hallucination_guard
# ---------------------------------------------------------------------------
#
# The stems were collected from meeting windows over near-silence. Dictation
# is short by nature, and there "See you next week, Evan." is a message: it
# decoded correctly and was then emptied with `ok: true` on both engines,
# while the host blamed the microphone. Dictation sends
# hallucination_guard=false, and the rule now honours it.

SIGN_OFFS = ("See you next week, Evan.", "Stay tuned for the launch update.",
             "See you guys at standup", "Subscribe to the release notes, please.",
             "http://intranet/status is down again")


def test_a_dictated_sign_off_is_not_junk_without_the_guard():
    for text in SIGN_OFFS:
        assert looks_like_junk(text), f"the meeting default no longer drops {text!r}"
        assert not looks_like_junk(text, hallucination_guard=False), text


def test_without_the_guard_the_other_rules_still_apply():
    """Only the stem rule is scoped: annotations, loops and the compression
    rule are junk for every caller."""
    for text in ("(whisper)", "[BLANK_AUDIO]", "snap " * 50, "yes yes yes yes yes yes yes yes",
                 "see you next " * 20):
        assert looks_like_junk(text, hallucination_guard=False), text[:40]






def test_engine_hands_the_callers_guard_to_the_junk_filter():
    """The flag must reach the filter from transcribe, on both engines. Read
    as source for the QNN engine, which cannot be imported here; the macOS
    engine is also driven end to end by tests/test_worker_frames.py."""
    import ast
    import pathlib

    here = pathlib.Path(__file__).parent
    for module, cls in (("whisper_mac.py", "WhisperMac"),):
        tree = ast.parse((here / module).read_text(encoding="utf-8"))
        transcribe = next(item for node in tree.body
                          if isinstance(node, ast.ClassDef) and node.name == cls
                          for item in node.body
                          if isinstance(item, ast.FunctionDef) and item.name == "transcribe")
        calls = [node for node in ast.walk(transcribe) if isinstance(node, ast.Call)
                 and getattr(node.func, "id", None) == "looks_like_junk"]
        assert calls, f"{cls}.transcribe no longer calls looks_like_junk"
        for call in calls:
            guard = {kw.arg: kw.value for kw in call.keywords}.get("hallucination_guard")
            assert isinstance(guard, ast.Name) and guard.id == "hallucination_guard", \
                f"{cls}.transcribe calls looks_like_junk without the caller's hallucination_guard"
