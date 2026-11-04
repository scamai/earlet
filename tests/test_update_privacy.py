"""What a Mac update check may carry, read from the Swift sources on any platform.

A check is one request for the public, signed feed (docs/design-macos.md#updates).
DictationUpdater, in the app's executable target that `swift test` cannot
reach, empties the cookie store when it starts and before and after every
check; DictationUpdateCookies' own test, which needs a Mac, shows what that
prevents. These tests read the updater's source, so a removed call turns the
Python gate red on every platform, and they check that no Mac source uses
Sparkle's ways of adding to the request. They are not evidence of what a
packaged copy sends: that takes a capture on a Mac.
"""
from __future__ import annotations

import pytest

import harness

UPDATER = harness.ROOT / "macos/Sources/DictationApp/DictationUpdater.swift"
UPDATER_PATH = str(UPDATER.relative_to(harness.ROOT))
SOURCES = harness.ROOT / "macos/Sources"
FORGET = "DictationUpdateCookies.forget()"
START = "updater.start()"
# The delegate methods Sparkle 2.9.6 calls before and after every check, by
# their Swift names, and the updater's initializer, which runs before the first.
HOOKS = ("mayPerform updateCheck: SPUUpdateCheck", "didFinishUpdateCycleFor updateCheck: SPUUpdateCheck")
INIT = "init(feed: URL?"
# Sparkle's ways to add to a check's request: query parameters and the system
# profile (SPUUpdaterDelegate, SUEnableSystemProfiling, SUSendProfileInfo),
# extra headers and another user agent (SPUUpdater).
ADDITIONS = ("feedParameters", "sendsSystemProfile", "SUEnableSystemProfiling", "SUSendProfileInfo",
             "httpHeaders", "userAgentString")
# Fewer Swift files than this means the scan is reading the wrong tree (127 on
# 2026-11-03 by `find macos/Sources -name '*.swift'`); an empty scan reports
# nothing.
MIN_SOURCES = 50


def span(source: str, declaration: str) -> tuple[int, int] | None:
    """Where the brace-balanced body of the first function whose declaration
    contains ``declaration`` starts and ends."""
    at = source.find(declaration)
    start = source.find("{", at) if at >= 0 else -1
    if start < 0:
        return None
    depth = 0
    for index in range(start, len(source)):
        depth += {"{": 1, "}": -1}.get(source[index], 0)
        if depth == 0:
            return start + 1, index
    return None


def body(source: str, declaration: str) -> str | None:
    found = span(source, declaration)
    return source[found[0]:found[1]] if found else None


def with_body(source: str, declaration: str, change) -> str:
    """``source`` with that function's body passed through ``change``."""
    start, end = span(source, declaration)
    return source[:start] + change(source[start:end]) + source[end:]


def wiring_problems(source: str) -> list[str]:
    problems = [f"{hook}: does not empty the cookie store" for hook in HOOKS
                if FORGET not in (body(source, hook) or "")]
    init = body(source, INIT) or ""
    if FORGET not in init or START not in init or init.index(FORGET) > init.index(START):
        problems.append("init: does not empty the cookie store before the updater starts")
    return problems


def test_every_check_starts_and_ends_with_an_empty_cookie_store():
    assert wiring_problems(UPDATER.read_text(encoding="utf-8")) == []


@pytest.mark.parametrize("declaration", [*HOOKS, INIT])
def test_a_missing_forget_is_reported(declaration):
    source = UPDATER.read_text(encoding="utf-8")
    assert FORGET in (body(source, declaration) or "")
    mutant = with_body(source, declaration, lambda text: text.replace(FORGET, ""))
    assert mutant.count(FORGET) == source.count(FORGET) - 1
    assert wiring_problems(mutant) != []


def test_a_forget_after_the_updater_starts_is_reported():
    source = UPDATER.read_text(encoding="utf-8")
    mutant = with_body(source, INIT, lambda text: text.replace(f"{FORGET}\n", "").replace(START, f"{START}; {FORGET}"))
    assert mutant != source and mutant.count(FORGET) == source.count(FORGET)
    assert wiring_problems(mutant) != []


def swift_sources() -> dict[str, str]:
    """Every Swift source of the Mac app, by its path in this directory."""
    return {str(path.relative_to(harness.ROOT)): path.read_text(encoding="utf-8")
            for path in sorted(SOURCES.rglob("*.swift"))}


def request_additions(sources: dict[str, str]) -> list[str]:
    return [f"{path}: {name}" for path, text in sources.items() for name in ADDITIONS if name in text]


def test_nothing_in_the_mac_app_adds_to_a_checks_request():
    sources = swift_sources()
    assert UPDATER_PATH in sources and len(sources) >= MIN_SOURCES
    assert request_additions(sources) == []


@pytest.mark.parametrize("name, line", [
    ("feedParameters",
     "func feedParameters(for updater: SPUUpdater, sendingSystemProfile: Bool) -> [[String: String]] { [] }"),
    ("sendsSystemProfile", "updater.sendsSystemProfile = true"),
    ("httpHeaders", 'updater.httpHeaders = ["X-Install": "1"]'),
    ("userAgentString", 'updater.userAgentString = "Earlet"'),
])
def test_an_addition_to_the_request_is_reported(name, line):
    sources = swift_sources()
    mutant = sources | {UPDATER_PATH: f"{sources[UPDATER_PATH]}\n{line}\n"}
    assert request_additions(mutant) == [f"{UPDATER_PATH}: {name}"]
