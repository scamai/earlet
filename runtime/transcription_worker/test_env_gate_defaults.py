"""Default-config contract for the notes live-caption master gates (P0).

The field incident (2026-09-09): live captions produced ZERO output for weeks
because the path that mints them was env-gated OFF by default and nobody had a
test on the DEFAULT resolution. These pin the gate defaults so any flip is a
deliberate, reviewed one-line change here — not a silent regression.
"""

from transcription_worker.ops_stream_whisper import (
    _single_emission_enabled,
    _turn_geometry_local,
)


def test_single_emission_default_off():
    assert _single_emission_enabled({}) is False


def test_single_emission_explicit():
    assert _single_emission_enabled({"DICTATION_NOTES_SINGLE_EMISSION": "1"}) is True
    assert _single_emission_enabled({"DICTATION_NOTES_SINGLE_EMISSION": "0"}) is False


def test_turn_geometry_default_is_local():
    # The 2026-09-09 flip: default must be local (legacy starves on real
    # meetings). If this line has to change, the live path is changing.
    assert _turn_geometry_local({}) is True


def test_turn_geometry_legacy_optout_and_case():
    assert _turn_geometry_local({"DICTATION_NOTES_TURN_GEOMETRY": "legacy"}) is False
    assert _turn_geometry_local({"DICTATION_NOTES_TURN_GEOMETRY": "LOCAL"}) is True
    # Only UNSET falls back to local; an explicitly-empty value is treated as
    # non-local (historical inline behaviour, preserved). The contract that
    # matters — a default launch gets local — is test_turn_geometry_default.
    assert _turn_geometry_local({"DICTATION_NOTES_TURN_GEOMETRY": ""}) is False
