"""The learning reference (tests/learning_reference.py) agrees with the shared
golden cases that the Swift and C# implementations are also held to."""

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import learning_reference as ref  # noqa: E402

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "learning-golden.json"
GOLDEN = json.loads(FIXTURE.read_text(encoding="utf-8"))


def _run(lexicon_words, steps):
    learning = ref.Learning(ref.Lexicon(lexicon_words))
    results = []
    for step in steps:
        op = step["op"]
        if op == "observe":
            learning.observe(step["dictation"], step["dictated"], step["edited"], step["at"])
        elif op == "teach":
            results.append(learning.teach(step["written"], step["sounds_like"], step["at"]))
        elif op == "remove_word":
            learning.remove_word(step["text"], step["at"])
        elif op == "remove_alias":
            learning.remove_alias(step["heard"], step["meant"], step["at"])
        elif op == "clear":
            learning.clear()
        else:
            raise AssertionError(f"unknown op {op!r}")
    return learning, results


def test_fixture_is_the_expected_schema_and_not_empty():
    assert GOLDEN["schema"] == "dictation.learning-golden/1"
    assert len(GOLDEN["extract"]) >= 15
    assert len(GOLDEN["sequences"]) >= 20
    assert GOLDEN["lexicon"], "the shared lexicon must not be empty"


@pytest.mark.parametrize("case", GOLDEN["extract"], ids=lambda c: c["name"])
def test_extract_matches_the_golden_case(case):
    candidates, rejections = ref.extract(case["dictated"], case["edited"], ref.Lexicon(GOLDEN["lexicon"]))
    assert [[c.heard, c.meant, c.kind] for c in candidates] == case["candidates"]
    assert [[r.heard, r.meant, r.reason] for r in rejections] == case["rejections"]


@pytest.mark.parametrize("case", GOLDEN["sequences"], ids=lambda c: c["name"])
def test_sequence_matches_the_golden_case(case):
    learning, results = _run(case.get("lexicon", GOLDEN["lexicon"]), case["steps"])
    assert results == [s["result"] for s in case["steps"] if s["op"] == "teach"]
    assert learning.projection() == case["expect"]
    if "expect_journal" in case:
        journal = learning.state.get("evidence", {}).get("observations", [])
        assert [{"id": r["id"], "corrections": [[c["heard"], c["meant"]] for c in r["corrections"]]} for r in journal] == case["expect_journal"]
    if "expect_word_evidence" in case:
        assert {w["text"]: [w["count"], w.get("opposition", 0)] for w in learning.state["words"]} == case["expect_word_evidence"]


@pytest.mark.parametrize("case", GOLDEN["sequences"], ids=lambda c: c["name"])
def test_state_survives_a_json_round_trip(case):
    learning, _ = _run(case.get("lexicon", GOLDEN["lexicon"]), case["steps"])
    reloaded = ref.Learning(ref.Lexicon(GOLDEN["lexicon"]), json.loads(json.dumps(learning.state)))
    assert reloaded.projection() == learning.projection()
    assert reloaded.state["schema"] == ref.SCHEMA


def test_a_word_that_waited_keeps_the_count_and_first_sighting_of_its_pending_pair():
    """The golden projection holds word texts only. A secret-shaped word is
    written when its pending pair is promoted (LEARNING.md §4.1), and the entry
    takes the pair's count and first sighting, not the moment of promotion. The
    repeated d1 is the same observation again and changes nothing (§4.6); d2
    sets the last sighting."""
    t0 = 1_790_000_000_000
    learning = ref.Learning(ref.Lexicon(GOLDEN["lexicon"]))
    for dictation, at in (("d1", t0), ("d1", t0 + 1000), ("d2", t0 + 5000)):
        learning.observe(dictation, "the wifi password is sunshine", "the wifi password is Sunshine42x!", at)
    assert learning.state["pending"] == []
    assert learning.state["words"] == [
        {"text": "Sunshine42x", "origin": "correction", "count": 2, "first_seen": t0, "last_seen": t0 + 5000, "adaptive": True, "opposition": 0}
    ]


def test_an_empty_lexicon_fails_closed():
    lexicon = ref.Lexicon([])
    assert not lexicon.available
    assert lexicon.is_ordinary("Trovioo")
    assert not ref.is_distinctive("Trovioo", lexicon)


def test_the_shipped_word_list_is_the_pinned_windows_subset():
    """Windows embeds a curated web2 subset; macOS reads /usr/share/dict/web2."""
    import gzip
    import hashlib

    resource = FIXTURE.parents[2] / "windows" / "Dictation.Services" / "Resources" / "web2-words.txt.gz"
    data = gzip.decompress(resource.read_bytes())
    assert hashlib.md5(data).hexdigest() == "b88a24eac9a2ca33feaf3ba2338f1c46"
    lexicon = ref.Lexicon.from_webster(data.decode("utf-8"))
    assert lexicon.is_ordinary("meetings")
    assert lexicon.is_ordinary("libraries")
    assert not lexicon.is_ordinary("Trovioo")
    assert not lexicon.is_ordinary("kubectl")
    assert not lexicon.is_ordinary("Miles")
    assert not lexicon.is_ordinary("Jarvis")
    assert lexicon.is_ordinary("A") and lexicon.is_ordinary("I")
    learning = ref.Learning(lexicon)
    learning.observe("server-name", "we use Mies", "we use Miles", 1000)
    assert [w["text"] for w in learning.state["words"]] == ["Miles"]
    assert learning.state["aliases"] == []
    assert learning.state["pending"] == []


def test_webster_filter_is_separate_from_caller_declared_ordinary_words():
    text = "we use my mile a A i I apple Apple Miles Jarvis NASA"
    lexicon = ref.Lexicon.from_webster(text)
    assert all(lexicon.is_ordinary(w) for w in ("A", "I", "Apple", "Mies"))
    assert not any(lexicon.is_ordinary(w) for w in ("Miles", "Miles's", "use-Miles", "Jarvis", "NASA"))
    assert ref.Lexicon(["Miles", "SKY"]).is_ordinary("miles")
    assert not ref.Lexicon.from_webster("").available


def test_an_excluded_name_is_not_made_ordinary_by_a_verb_ending():
    """LEARNING.md §2: `Downing` is a Webster name; `down` + `-ing` must not
    make it ordinary. A caller-declared list has no names to exclude."""
    lexicon = ref.Lexicon.from_webster("down played play Downing ewe")
    assert lexicon.is_ordinary("playing") and lexicon.is_ordinary("played")
    assert lexicon.is_ordinary("Ewing")  # not listed, so ew + e = ewe makes it ordinary
    assert not lexicon.is_ordinary("Downing")
    assert not lexicon.is_ordinary("use-Downing")
    assert ref.Lexicon(["down"]).is_ordinary("Downing")
