"""The history guards' mutation list, checked without a Swift toolchain.

`macos/tools/history-mutations.py` breaks each guard of the dictation history
in a copy of the Swift package and needs the history tests to go red for
every one. It runs only where Swift is installed, so a row whose anchor a
later edit stopped matching could sit there unnoticed. These tests require
each anchor to occur exactly once in its Swift file, on every platform.
Whether each mutant is killed still needs the tool itself, on a Mac.
"""
from __future__ import annotations

import importlib.util

import harness

TOOL = harness.ROOT / "macos/tools/history-mutations.py"


def load_tool():
    spec = importlib.util.spec_from_file_location("history_mutations", TOOL)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_every_anchor_occurs_exactly_once():
    tool = load_tool()
    assert tool.MUTATIONS, "an empty list would pass every check below"
    assert tool.anchor_problems(harness.ROOT / "macos") == []


def test_the_check_fires_on_a_moved_anchor(tmp_path):
    tool = load_tool()
    (tmp_path / "Sources/DictationHistory").mkdir(parents=True)
    for name, edits in tool.MUTATIONS:
        for file, _, _ in edits:
            target = tmp_path / file
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text((harness.ROOT / "macos" / file).read_text(encoding="utf-8"), encoding="utf-8")
    assert tool.anchor_problems(tmp_path) == []
    seal = tmp_path / tool.SEAL
    text = seal.read_text(encoding="utf-8")
    seal.write_text(text.replace("throw OpenFailure.mismatched }", "throw OpenFailure.mismatched  }"), encoding="utf-8")
    assert tool.anchor_problems(tmp_path) == ["the kind is not checked: the anchor occurs 0 times in " + tool.SEAL]
    seal.write_text(text + "\n" + "        guard header[9] == kind.rawValue else { throw OpenFailure.mismatched }\n",
                    encoding="utf-8")
    assert tool.anchor_problems(tmp_path) == ["the kind is not checked: the anchor occurs 2 times in " + tool.SEAL]
