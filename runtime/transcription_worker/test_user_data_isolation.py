"""conftest.py keeps every worker test off the real Dictation user data.

Kept in a module with no isolation fixture of its own: test_asr_server.py
patches the same path itself, so a guard placed there could not tell whether
conftest did anything.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from transcription_worker import spoken_aliases  # noqa: E402


def test_worker_tests_never_read_the_real_user_data(tmp_path):
    """A Mac where Dictation has run must not leak its learned words into the suite."""
    if sys.platform == "win32":
        assert str(tmp_path) in os.environ["LOCALAPPDATA"]
    else:
        assert tmp_path in spoken_aliases.default_path().parents
        assert tmp_path in spoken_aliases.learning_path().parents
