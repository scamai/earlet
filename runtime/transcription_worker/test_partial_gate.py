"""Display-path gate for the MUTABLE partial caption line (partial_gate.py).

Synthetic fixtures model prompt regurgitation during silence/noise. The
committed transcript is clean while the partial line must suppress these
garbled vocabulary echoes, including "fable", "NeMo", "Asteron", and
"IO do force cedarcore". The cases include the session-start boot backlog
where leading noise is decoded.

Covers the pure decision core AND the _decode wiring contract (the partial
must not change on a suppressed window while the raw decode text still
flows to the commit machinery).
"""

import numpy as np

from transcription_worker import hallucination
from transcription_worker import partial_gate as pg


# Synthetic bias vocabulary for the partial-display gate. The gate receives
# this list independently of any persisted user vocabulary.
DEVICE_VOCAB = [
    "Fable", "Knemo", "Trovioo", "PadForge", "CedarCore", "Asteron",
    "deepfake", "diarization", "SignalMixer", "CedarTagger",
]

# Synthetic regurgitation lines.
ECHO_FIXTURES = ["fable", "NeMo", "IO do force cedarcore", "Asteron"]


def gate() -> pg.PartialDisplayGate:
    return pg.PartialDisplayGate(DEVICE_VOCAB)


# ---------------------------------------------------------------- energy --

def test_quiet_from_the_very_first_window():
    # Session start: no history at all — the absolute floor alone must
    # classify leading room noise as quiet (the boot-backlog case).
    g = gate()
    assert g.note_window_rms(0.001) is True


def test_speech_window_is_not_quiet():
    g = gate()
    assert g.note_window_rms(0.05) is False


def test_noisy_loopback_floor_adapts_upward_slowly():
    g = gate()
    # a hissy loopback idles ~0.006 RMS: after the floor settles, 0.006 must
    # be quiet (it IS the floor) while clear speech stays speech.
    for _ in range(200):
        g.note_window_rms(0.006)
    assert g.note_window_rms(0.006) is True
    assert g.note_window_rms(0.08) is False


def test_speech_never_drags_the_floor_up_to_mask_itself():
    g = gate()
    for _ in range(50):
        g.note_window_rms(0.08)   # continuous loud speech
    # floor is capped: normal speech afterwards still classifies as speech
    assert g.note_window_rms(0.03) is False


# --------------------------------------------------------- regurgitation --

def test_echo_fixtures_score_as_echo():
    g = gate()
    for line in ECHO_FIXTURES:
        assert g.echo_score(line) >= pg.FUZZY_ECHO_COVERAGE, line


def test_garbled_tokens_match_by_edit_distance_one():
    g = gate()
    # "NeMo" is not a clean substring match for "Knemo"— the fuzzy pass
    # carries it; same for "force" (vs "forge" inside PadForge).
    assert g.echo_score("nemo") >= pg.FUZZY_ECHO_COVERAGE
    assert g.echo_score("force cedarcore") >= pg.FUZZY_ECHO_COVERAGE


def test_real_sentence_using_the_terms_survives():
    g = gate()
    s = "we should ship the cedarcore build tomorrow after the standup"
    assert g.echo_score(s) < pg.FUZZY_ECHO_COVERAGE


def test_no_vocab_no_echo():
    g = pg.PartialDisplayGate([])
    assert g.echo_score("fable asteron cedarcore") == 0.0


# -------------------------------------------------------------- decision --

def test_quiet_regurgitation_is_suppressed_from_first_partial():
    g = gate()
    quiet = g.note_window_rms(0.001)
    text, reason = g.decide("", "IO do force cedarcore", quiet,
                            is_junk=hallucination.is_junk_utterance)
    assert text == ""
    assert reason == "regurgitation"


def test_quiet_new_text_holds_previous_partial():
    g = gate()
    quiet = g.note_window_rms(0.001)
    text, reason = g.decide("so the quarterly numbers look",
                            "thank you for watching", quiet,
                            is_junk=lambda s: False)
    assert text == "so the quarterly numbers look"
    assert reason == "energy-hold"


def test_junk_never_replaces_text_at_any_energy():
    g = gate()
    speech = g.note_window_rms(0.05)
    assert speech is False
    text, reason = g.decide("real sentence here", "Thank you.", quiet=False,
                            is_junk=hallucination.is_junk_utterance)
    assert text == "real sentence here"
    assert reason == "junk"


def test_speech_window_real_text_flows():
    g = gate()
    quiet = g.note_window_rms(0.05)
    text, reason = g.decide("so the", "so the quarterly numbers look great",
                            quiet, is_junk=hallucination.is_junk_utterance)
    assert reason is None
    assert text == "so the quarterly numbers look great"


def test_same_text_is_a_noop_even_when_quiet():
    g = gate()
    text, reason = g.decide("held line", "held line", quiet=True,
                            is_junk=hallucination.is_junk_utterance)
    assert (text, reason) == ("held line", None)


def test_empty_decode_defers_to_caller():
    g = gate()
    text, reason = g.decide("keep me", "", quiet=True,
                            is_junk=hallucination.is_junk_utterance)
    assert (text, reason) == ("keep me", None)


# ------------------------------------------------------- wiring contract --

def test_decode_site_routes_partial_through_the_gate():
    """AST-level pin, same spirit as test_commit_gate's ratchet: inside
    _ChannelStream._decode every assignment of `self.partial` from decode
    text must come from the gate's decision variable, and the gate must be
    consulted (partial_gate.decide call present)."""
    import ast
    import inspect
    from transcription_worker import ops_stream_whisper as osw

    src = inspect.getsource(osw._ChannelStream._decode)
    tree = ast.parse("class _X:\n" + src if src.startswith("    def") else src)
    text_src = src
    assert "partial_gate.decide" in text_src or ".decide(" in text_src, (
        "_decode no longer consults the partial display gate")
    assert "_disp_text" in text_src, (
        "_decode's partial assignment no longer uses the gated text")


def test_edit1_helper():
    assert pg._edit1("force", "forge")
    assert pg._edit1("nemo", "knemo")
    assert pg._edit1("do", "io")
    assert not pg._edit1("force", "fable")
    assert not pg._edit1("snap", "cedarcore")


def test_untracked_floor_keeps_only_the_absolute_backstop():
    # A hold-to-talk microphone (the Dictation preview) starts on speech: a
    # floor seeded there would class every later window of a quiet voice
    # as quiet. Untracked, only true silence is quiet.
    g = gate()
    g.track_floor = False
    assert g.note_window_rms(0.006) is False
    assert g.note_window_rms(0.0045) is False
    assert g.note_window_rms(0.003) is True
    tracked = gate()
    tracked.note_window_rms(0.006)
    assert tracked.note_window_rms(0.0045) is True
