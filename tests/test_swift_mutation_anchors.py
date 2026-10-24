"""The Swift learning guards' mutation list, checked without a Swift toolchain.

`macos/tools/test-learning-on-linux.sh --mutations` breaks each guard of
macos/Sources/DictationLearning in a copy of the sources and needs the tests
to go red for every one. It runs only where Swift is installed, and nothing
gates it, so a row whose anchor a later edit stopped matching could sit there
unnoticed, turning that run red the next time anyone tried it. These tests read
the script's MUTATIONS array the way bash reads it and require each anchor to
occur exactly once in its Swift file, so the drift turns the Python gate red on
every platform. Whether each mutant is killed still needs `--mutations` on a
machine with Swift.
"""
from __future__ import annotations

import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess

import pytest

import harness

SCRIPT = harness.ROOT / "macos/tools/test-learning-on-linux.sh"
SOURCES = harness.ROOT / "macos/Sources/DictationLearning"
ARRAY = re.compile(r"^MUTATIONS=\(\n(.*?)^\)$", re.M | re.S)


def read(path: Path) -> str:
    # A Windows checkout may carry CRLF; the script and the anchors mean LF.
    return path.read_text(encoding="utf-8").replace("\r\n", "\n")


def array_words(script: str) -> list[str]:
    """The words of the script's MUTATIONS=( ... ) array, unquoted as bash
    unquotes them (the rows are single-quoted; see the bash comparison below)."""
    found = ARRAY.search(script)
    assert found is not None, "no MUTATIONS=( ... ) array in the script"
    return shlex.split(found.group(1), comments=True)


def mutation_rows(script: str) -> list[tuple[str, str, str, str]]:
    """(name, file, anchor, replacement) per row, as the script's loop reads them."""
    words = array_words(script)
    assert words and len(words) % 4 == 0, f"{len(words)} words is not whole rows of name, file, anchor, replacement"
    return [tuple(words[index:index + 4]) for index in range(0, len(words), 4)]


def row_problems(rows, sources: Path) -> list[str]:
    """Each row that `--mutations` would report BROKEN, or that could prove nothing."""
    names = [row[0] for row in rows]
    problems = [f"{name}: named by more than one row" for name in sorted({name for name in names
                                                                          if names.count(name) > 1})]
    for name, file, anchor, replacement in rows:
        source = sources / file
        if not source.is_file():
            problems.append(f"{name}: no {file} in {sources.name}")
            continue
        if "\n" in anchor:
            problems.append(f"{name}: the anchor spans lines, and grep -F reads each line as its own pattern")
        count = read(source).count(anchor)
        if count != 1:
            problems.append(f"{name}: the anchor occurs {count} times in {file}")
        if replacement == anchor:
            problems.append(f"{name}: the replacement is the anchor, so nothing is mutated")
    return problems


# ─── The real list ───────────────────────────────────────────────────────────

def test_every_swift_mutation_anchor_occurs_exactly_once_in_its_source():
    rows = mutation_rows(read(SCRIPT))
    assert row_problems(rows, SOURCES) == []


@pytest.mark.skipif(os.name == "nt" or shutil.which("bash") is None, reason="runs the native shell helper")
def test_mutations_write_literal_swift_without_shell_quotes_or_expansion(tmp_path):
    script = read(SCRIPT)
    begin = script.index("apply_mutation() {")
    end = script.index("\n}\n", begin) + 3
    helper = script[begin:end] + '\napply_mutation "$@"\n'
    source, destination = tmp_path / "source.swift", tmp_path / "mutant.swift"
    source.write_text('public static let value = 2\nif a && b { print("$literal") }\n', encoding="utf-8")
    anchor, replacement = 'static let value = 2', 'static let value = 3 // "quotes" & $literal'
    subprocess.run(["bash", "-c", helper, "mutator", str(source), str(destination), anchor, replacement], check=True)
    assert destination.read_text() == 'public static let value = 3 // "quotes" & $literal\nif a && b { print("$literal") }\n'


@pytest.mark.skipif(os.name == "nt" or shutil.which("bash") is None, reason="compares with bash's own reading")
def test_the_array_is_read_as_bash_reads_it():
    """shlex and bash agree on the quoting the rows use; a row written in a
    form they read differently fails here rather than checking the wrong text."""
    script = read(SCRIPT)
    block = ARRAY.search(script).group(0)
    printed = subprocess.run(["bash", "-c", block + "\nprintf '%s\\0' \"${MUTATIONS[@]}\"\n"], capture_output=True,
                             check=True, timeout=60).stdout.decode("utf-8")
    assert printed.split("\0")[:-1] == array_words(script)


def test_a_drifted_row_in_the_real_script_is_named():
    script = read(SCRIPT)
    name, _, anchor, _ = mutation_rows(script)[0]
    assert script.count(f"'{anchor}'") == 1, "the first row's anchor moved: update this test"
    drifted = script.replace(f"'{anchor}'", f"'{anchor} // drifted'")
    assert f"{name}: the anchor occurs 0 times" in "\n".join(row_problems(mutation_rows(drifted), SOURCES))


# ─── The check itself can fire ───────────────────────────────────────────────
# Each case feeds one kind of drift to row_problems over a synthetic source;
# deleting the check that catches it turns its case red.

SOURCE = "let limit = 2\nif ready { go() }\nif ready { go() }\n"
CLEAN = ("limit", "A.swift", "let limit = 2", "let limit = 3")
DRIFT = {
    "clean": ((CLEAN,), None),
    "anchor matches nothing": ((("limit", "A.swift", "let limit = 4", "let limit = 5"),),
                               "limit: the anchor occurs 0 times in A.swift"),
    "anchor matches twice": ((("ready", "A.swift", "if ready { go() }", "if true { go() }"),),
                             "ready: the anchor occurs 2 times in A.swift"),
    "file gone": ((("limit", "B.swift", "let limit = 2", "let limit = 3"),), "limit: no B.swift in sources"),
    "nothing mutated": ((("limit", "A.swift", "let limit = 2", "let limit = 2"),),
                        "limit: the replacement is the anchor, so nothing is mutated"),
    "name reused": ((CLEAN, ("limit", "A.swift", "limit = 2", "limit = 3")), "limit: named by more than one row"),
    "anchor spans lines": ((("limit", "A.swift", "let limit = 2\nif ready", "let limit = 3\nif ready"),),
                           "limit: the anchor spans lines, and grep -F reads each line as its own pattern"),
}


@pytest.mark.parametrize("case", DRIFT)
def test_the_anchor_check_names_each_kind_of_drift(case, tmp_path):
    sources = tmp_path / "sources"
    sources.mkdir()
    (sources / "A.swift").write_text(SOURCE, encoding="utf-8")
    rows, expected = DRIFT[case]
    assert row_problems(rows, sources) == ([] if expected is None else [expected])


@pytest.mark.parametrize("body, message", [
    ("    'name' 'A.swift' 'anchor'\n", "3 words is not whole rows"),
    ("", "0 words is not whole rows"),
])
def test_a_partial_or_empty_array_is_refused(body, message):
    with pytest.raises(AssertionError, match=message):
        mutation_rows(f"#!/usr/bin/env bash\nMUTATIONS=(\n{body})\n")


def test_a_script_without_the_array_is_refused():
    with pytest.raises(AssertionError, match="no MUTATIONS"):
        mutation_rows("#!/usr/bin/env bash\necho nothing to mutate\n")
