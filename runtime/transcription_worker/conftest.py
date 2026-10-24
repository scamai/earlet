"""Make `transcription_worker` importable as a root, whatever pytest's rootdir is.

`learning/tests/*` import their subject as a TOP-LEVEL package -- `from learning
import event_log`, `from learning import schemas` -- which only resolves when
`transcription_worker/` itself is on `sys.path`. That happens automatically when
pytest is invoked from inside this directory, and not at all when it is invoked
from the runtime directory above it, which is what `pytest.ini`'s
`testpaths = tests transcription_worker` and the release's
`python -m pytest -q transcription_worker` both do.

The symptom was eight collection errors, all `ModuleNotFoundError: No module
named 'learning'`, and an exit code of 2 rather than 1 -- a COLLECTION failure,
so the other 657 tests in this package never ran either. Every local run had
been working around it with `--ignore=transcription_worker/learning`, which is
why it stayed invisible: the suite looked green because the broken part was
being skipped by hand, and the release, which passes no `--ignore`, is the first
thing to run the command as written.

pytest imports the nearest `conftest.py` above a test file before collecting it,
so putting this here fixes the import for every entry point at once -- the
release, the local runner, and a bare `pytest` from either directory -- without
touching the test modules or weakening what the release runs.

Deliberately NOT done: adding `--ignore=learning` to the release, which would
make the gate pass by not looking; and rewriting the imports to
`from transcription_worker.learning import ...`, which is a wider change to
files this work never touched, and which would break running them from inside
their own directory.
"""
from __future__ import annotations

import os
import sys

import pytest

_HERE = os.path.dirname(os.path.abspath(__file__))

if _HERE not in sys.path:
    # Appended rather than inserted at 0: this only needs to make `learning`
    # resolvable, and prepending would let a module in here shadow a real
    # dependency of the same name for the whole session.
    sys.path.append(_HERE)


@pytest.fixture(autouse=True)
def _isolate_windows_user_configuration(monkeypatch, tmp_path):
    """Keep Windows worker tests independent of an installed app's user data.

    The production worker deliberately reads aliases and other learned state
    from the current user's profile.  A release test must never do that: on a
    machine whose user dictionary contains ``Hello -> Hullo``, otherwise-pure
    commit and diarization tests rewrote their fixture text and failed.  Point
    Windows' application-data root at a fresh per-test directory.  Limiting
    this to Windows also avoids hiding user-site Python packages by changing
    ``HOME`` on the macOS lane.  Tests that exercise a particular Windows path
    remain free to override ``LOCALAPPDATA`` with their own ``monkeypatch``.
    """
    if sys.platform == "win32":
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "LocalAppData"))


# Modules that assert the real platform paths themselves, and so must see the
# unpatched `default_path`.
_READS_REAL_USER_PATHS = frozenset({"test_spoken_aliases", "test_learned_vocabulary", "test_voice_profile"})


@pytest.fixture(autouse=True)
def _isolate_dictation_user_data(request, monkeypatch, tmp_path):
    """Keep macOS and Linux worker tests off the real Dictation user data.

    The worker reads `bias_aliases.json` and the app's `learning.json` from
    ~/Library/Application Support/Dictation/dictation (LEARNING.md §1). On a
    Mac where Dictation has run, that real file would put the user's learned
    words in front of every transcribe these tests make, and tests that pin
    exact `bias_phrases` would fail for reasons that are not in the code.
    HOME is left alone for the reason the Windows fixture above gives; only
    the one path function is pointed at a per-test folder. Windows is already
    covered by that fixture's LOCALAPPDATA.
    """
    if sys.platform == "win32" or request.module.__name__.rsplit(".", 1)[-1] in _READS_REAL_USER_PATHS:
        return
    fake = tmp_path / "dictation-user-data" / "bias_aliases.json"
    for name in ("transcription_worker.spoken_aliases", "spoken_aliases"):
        module = sys.modules.get(name)
        if module is None and name.startswith("transcription_worker."):
            try:
                import importlib
                module = importlib.import_module(name)
            except ImportError:
                continue
        if module is not None:
            monkeypatch.setattr(module, "default_path", lambda: fake)
