"""The Mac names the Accessibility pane the way System Settings does.

macOS 27 renamed the pane Device Control and Data Access, so every sentence
that names it reads `DictationPermissionStep.accessibilitySettingsName`
instead of spelling it. These tests read every Swift string literal under
macos/Sources (comments are not literals, and interpolated code is read as
code) and fail on one that contains the word Accessibility, outside the few
listed in ALLOWED. The match is the capitalized pane name as a whole word:
`missingAccessibility`, `Privacy_Accessibility` and `accessibility-needed`
do not match it, and no exclusion for them is needed.
"""
from __future__ import annotations

import bisect
from pathlib import Path
import re
import shutil

import harness

SOURCES = Path("macos/Sources")
PANE = re.compile(r"\bAccessibility\b")
# (file under macos/Sources, the literal's text): every literal that may name
# the pane, each exactly once.
ALLOWED = (
    # The one place the name is decided, from the macOS version.
    ("DictationAppCore/DictationPermissionSetup.swift", "Accessibility"),
    # DictationInsertFailure's description: only the log reads it, and the
    # app maps the failure to its own sentence (DictationCopy.insert).
    ("DictationKit/TextInserter.swift", "no Accessibility permission, so the focused field cannot be read"),
)


def swift_literals(source: str) -> list[tuple[int, str]]:
    """(line, text) of each string literal, in order of its opening quote.

    Line and block comments are skipped (block comments nest, as in Swift).
    Single-line, multi-line and raw (`#"..."#`) literals are read; an
    interpolation `\\(...)` stands as `\\(…)` in its literal's text, and the
    literals inside it are listed on their own.
    """
    newlines = [index for index, char in enumerate(source) if char == "\n"]
    found: list[tuple[int, int, str]] = []
    # Frames: ["code", open parens or None at top level] or
    # ["string", hashes, multi-line, start offset, text so far].
    stack: list[list] = [["code", None]]
    i, end = 0, len(source)
    while i < end:
        frame = stack[-1]
        char = source[i]
        if frame[0] == "code":
            if source.startswith("//", i):
                stop = source.find("\n", i)
                i = end if stop < 0 else stop
            elif source.startswith("/*", i):
                depth, i = 1, i + 2
                while i < end and depth:
                    if source.startswith("/*", i):
                        depth, i = depth + 1, i + 2
                    elif source.startswith("*/", i):
                        depth, i = depth - 1, i + 2
                    else:
                        i += 1
            elif char in '#"':
                quote = i
                while quote < end and source[quote] == "#":
                    quote += 1
                if quote < end and source[quote] == '"':
                    multi = source.startswith('"""', quote)
                    stack.append(["string", quote - i, multi, i, []])
                    i = quote + (3 if multi else 1)
                else:
                    i = quote if quote > i else i + 1
            elif char == "(" and frame[1] is not None:
                frame[1] += 1
                i += 1
            elif char == ")" and frame[1] is not None:
                if frame[1] == 0:
                    stack.pop()
                    stack[-1][4].append("\\(…)")
                else:
                    frame[1] -= 1
                i += 1
            else:
                i += 1
            continue
        _, hashes, multi, start, text = frame
        closing = ('"""' if multi else '"') + "#" * hashes
        escape = "\\" + "#" * hashes
        if source.startswith(closing, i):
            found.append((start, bisect.bisect_right(newlines, start - 1) + 1, "".join(text)))
            stack.pop()
            i += len(closing)
        elif source.startswith(escape, i) and i + len(escape) < end:
            after = i + len(escape)
            if source[after] == "(":
                stack.append(["code", 0])
                i = after + 1
            else:
                text.append(source[i:after + 1])
                i = after + 1
        else:
            text.append(char)
            i += 1
    return [(line, text) for _, line, text in sorted(found)]


def pane_literals(root: Path) -> list[tuple[str, int, str]]:
    """(file under macos/Sources, line, text) of each literal naming the pane."""
    sources = root / SOURCES
    found = []
    for path in sorted(sources.rglob("*.swift")):
        source = path.read_text(encoding="utf-8").replace("\r\n", "\n")
        for line, text in swift_literals(source):
            if PANE.search(text):
                found.append((path.relative_to(sources).as_posix(), line, text))
    return found


def pane_errors(root: Path, allowed=ALLOWED) -> list[str]:
    """Each literal that spells the pane's name, and each ALLOWED one gone."""
    found = pane_literals(root)
    unused = list(allowed)
    errors = []
    for file, line, text in found:
        if (file, text) in unused:
            unused.remove((file, text))
        else:
            errors.append(f"{file}:{line} spells the pane's name in {text!r}: "
                          "use DictationPermissionStep.accessibilitySettingsName")
    errors += [f"{file}: the allowed literal {text!r} is gone; remove it from ALLOWED"
               for file, text in unused]
    return errors


def test_no_mac_sentence_spells_the_accessibility_pane_name():
    assert pane_errors(harness.ROOT) == []


def test_the_scan_sees_the_allowed_literals_where_they_are():
    # Not vacuous: with nothing allowed, exactly the listed literals are reported.
    reported = pane_errors(harness.ROOT, allowed=())
    assert len(reported) == len(ALLOWED)
    for (file, text), error in zip(sorted(ALLOWED), sorted(reported)):
        assert error.startswith(f"{file}:") and repr(text) in error


def test_a_restored_old_sentence_is_reported(tmp_path):
    # The mutation: the indicator's detail line as it read before the fix.
    shutil.copytree(harness.ROOT / SOURCES, tmp_path / SOURCES)
    hud = tmp_path / SOURCES / "DictationAppCore/DictationHUD.swift"
    current = 'detail: "Allow Earlet in Privacy & Security → \\(accessibilitySettingsName), then try again.",'
    source = hud.read_text(encoding="utf-8")
    assert source.count(current) == 1, "the anchor moved: update this mutation"
    hud.write_text(source.replace(current, 'detail: "Allow Accessibility in System Settings, then try again.",'),
                   encoding="utf-8")
    errors = pane_errors(tmp_path)
    assert len(errors) == 1
    assert errors[0].startswith("DictationAppCore/DictationHUD.swift:")
    assert "'Allow Accessibility in System Settings, then try again.'" in errors[0]


def test_literals_are_read_the_way_swift_reads_them():
    source = "\n".join([
        'let a = "Allow \\(flag ? "Accessibility" : "x") here" // "Accessibility" in a comment',
        '/* "Accessibility" /* nested */ still a comment */ let b = #"raw "Accessibility" quote"#',
        'let c = "escaped \\"Accessibility\\" quote"',
        'let d = """',
        'multi-line Accessibility',
        '"""',
        'let e = "Privacy_Accessibility missingAccessibility accessibility-needed"',
    ])
    assert swift_literals(source) == [
        (1, "Allow \\(…) here"),
        (1, "Accessibility"),
        (1, "x"),
        (2, 'raw "Accessibility" quote'),
        (3, 'escaped \\"Accessibility\\" quote'),
        (4, "\nmulti-line Accessibility\n"),
        (7, "Privacy_Accessibility missingAccessibility accessibility-needed"),
    ]
    assert [text for _, text in swift_literals(source) if PANE.search(text)] == [
        "Accessibility", 'raw "Accessibility" quote', 'escaped \\"Accessibility\\" quote',
        "\nmulti-line Accessibility\n"]
