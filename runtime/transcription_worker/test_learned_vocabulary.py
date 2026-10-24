"""The worker's read side of the host's learning.json (LEARNING.md §1, §5, §6).

The host is the only writer. These tests hold the reader to what the worker
promises it: tolerant of every damaged shape, one privacy-safe note per
process, the caps, the stored order, a cache that follows the file, and no
writes of its own.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker import learned_vocabulary as lv  # noqa: E402
from transcription_worker import spoken_aliases as sa  # noqa: E402

#: A word no test or log line contains by accident, so its absence from
#: stderr means the reader did not echo the file.
SECRET = "Zqxwvutsrq"


def _document(words=(), aliases=(), schema=lv.SCHEMA) -> dict:
    return {
        "schema": schema,
        "words": [{"text": w, "origin": "correction", "count": 1,
                   "first_seen": 1790000000000, "last_seen": 1790000000000} for w in words],
        "aliases": [{"heard": h, "meant": m, "origin": "correction", "count": 2,
                     "first_seen": 1790000000000, "last_seen": 1790000000000}
                    for h, m in aliases],
        "pending": [],
        "removed": {"words": [], "aliases": []},
    }


def _write(path: Path, document) -> Path:
    text = document if isinstance(document, str) else json.dumps(document)
    path.write_text(text, encoding="utf-8")
    return path


@pytest.fixture(autouse=True)
def _fresh_reader(monkeypatch):
    """Each test starts with no cache and no note printed yet."""
    monkeypatch.setattr(lv, "_cache", None)
    monkeypatch.setattr(lv, "_noted", False)


# ─── Reading ─────────────────────────────────────────────────────────────────


def test_a_missing_file_is_nothing_learned_and_says_nothing(tmp_path, capsys):
    learned = lv.load(tmp_path / lv.FILE_NAME)

    assert learned == ([], {}, [], {})
    assert capsys.readouterr().err == ""


def test_words_keep_the_stored_order_and_aliases_map_heard_to_meant(tmp_path):
    path = _write(tmp_path / lv.FILE_NAME, _document(
        words=["Trovioo", "SkyDock", "GPT4"], aliases=[("trovio", "Trovioo"), ("sky dock", "SkyDock")]))

    learned = lv.load(path)

    # Stored order is most recently seen first; the prompt reads it as relevance.
    assert learned.words == ["Trovioo", "SkyDock", "GPT4"]
    assert learned.aliases == {"trovio": "Trovioo", "sky dock": "SkyDock"}


def test_adaptive_words_project_to_confidence_and_stable_priority(tmp_path):
    document = _document(words=["tentative", "confirmed", "established", "taught", "legacy"])
    rows = document["words"]
    for row, count, opposition in zip(rows[:3], [1, 3, 5], [0, 0, 1]):
        row.update(adaptive=True, count=count, opposition=opposition)
    rows[3]["origin"] = "taught"
    rows[4]["origin"] = "imported-later"

    learned = lv.load(_write(tmp_path / lv.FILE_NAME, document))

    assert learned.words == ["taught", "established", "legacy", "confirmed", "tentative"]
    assert learned.deltas == {"taught": .5, "established": .5, "confirmed": .35,
                              "tentative": .2, "legacy": .5}


def test_adaptive_negative_evidence_suppresses_words_and_weak_aliases(tmp_path):
    document = _document(words=["gone", "survivor"], aliases=[("old", "Old"), ("kept", "Kept")])
    document["words"][0].update(adaptive=True, count=2, opposition=2)
    document["words"][1].update(adaptive=True, count=3, opposition=1)
    document["aliases"][0].update(adaptive=True, count=2, opposition=1)
    document["aliases"][1].update(adaptive=True, count=2, opposition=0)

    learned = lv.load(_write(tmp_path / lv.FILE_NAME, document))

    assert learned.words == ["survivor"]
    assert learned.aliases == {"kept": "Kept"}
    assert learned.deltas == {"survivor": .35}


def test_alias_trigger_never_adds_or_overwrites_written_word_strength(tmp_path):
    document = _document(words=["Repo"], aliases=[("repo", "Other")])
    document["words"][0].update(origin="taught", adaptive=True, count=1, opposition=0)
    document["aliases"][0].update(adaptive=True, count=2, opposition=0)

    learned = lv.load(_write(tmp_path / lv.FILE_NAME, document))

    assert learned.aliases == {"repo": "Other"}
    assert learned.deltas == {"Repo": .5}


@pytest.mark.parametrize("field,value", [
    ("adaptive", False), ("adaptive", 1), ("count", True),
    ("count", float("nan")), ("count", 1.5), ("count", -1),
    ("opposition", True), ("opposition", float("inf")),
    ("opposition", -1), ("opposition", lv.MAX_EVIDENCE + 1),
])
def test_malformed_adaptive_automatic_entries_fail_closed(tmp_path, field, value):
    document = _document(words=["secretly"])
    document["words"][0].update(adaptive=True, count=1, opposition=0)
    document["words"][0][field] = value

    learned = lv.load(_write(tmp_path / lv.FILE_NAME, document))

    assert learned.words == []
    assert learned.deltas == {}


def test_legacy_rows_keep_old_bias_and_three_argument_constructor():
    legacy = lv.Learned(["Legacy"], {"heard": "Meant"}, ["correction"])
    assert legacy.deltas is None
    copied = lv._copy(legacy)
    assert copied.deltas == {}
    copied.deltas["Legacy"] = .5
    assert legacy.deltas is None


def test_each_word_carries_its_stored_origin(tmp_path):
    """Parallel to `words`, so the worker can tell a taught word (declared by
    the user: case, spacing and locale spelling) from one learned from a
    correction (case only). An origin this version does not know is kept as
    it is (§5), and a missing or non-string one reads as "", never "taught"."""
    document = _document(words=["Trovioo", "SkyDock", "Knemo", "GPT4", "QuestPDF"])
    document["words"][1]["origin"] = "taught"
    document["words"][2]["origin"] = "imported-later"
    del document["words"][3]["origin"]
    document["words"][4]["origin"] = 7

    learned = lv.load(_write(tmp_path / lv.FILE_NAME, document))

    assert learned.words == ["SkyDock", "Trovioo", "Knemo", "GPT4", "QuestPDF"]
    assert learned.origins == ["taught", "correction", "imported-later", "", ""]
    assert lv.TAUGHT == "taught"


def test_only_trimmed_non_empty_strings_survive(tmp_path):
    document = _document(words=["  Trovioo  ", "", "   "], aliases=[(" trovio ", " Trovioo "), ("x", "")])
    document["words"] += [{"text": 7}, {"text": None}, "SkyDock", {"origin": "taught"}, None]
    document["aliases"] += [{"heard": 3, "meant": "Three"}, {"heard": "nemo"}, "nemo", None]
    learned = lv.load(_write(tmp_path / lv.FILE_NAME, document))

    assert learned.words == ["Trovioo"]
    assert learned.origins == ["correction"]
    assert learned.aliases == {"trovio": "Trovioo"}


def test_a_heard_form_listed_twice_keeps_its_first_row(tmp_path):
    path = _write(tmp_path / lv.FILE_NAME, _document(aliases=[("trovio", "Trovioo"), ("TROVIO", "Other")]))

    assert lv.load(path).aliases == {"trovio": "Trovioo"}


def test_the_caps_hold_whatever_the_file_says(tmp_path):
    """LEARNING.md §4.5: 100 learned + 500 taught words, 200 aliases. The host
    holds the file to them; the worker holds them again so a hand-edited file
    cannot put an unbounded list on the decode path."""
    words = [f"Word{i:04d}" for i in range(lv.MAX_WORDS + 50)]
    aliases = [(f"heard{i:04d}", f"Meant{i:04d}") for i in range(lv.MAX_ALIASES + 50)]
    learned = lv.load(_write(tmp_path / lv.FILE_NAME, _document(words=words, aliases=aliases)))

    assert lv.MAX_WORDS == 600
    assert learned.words == words[:lv.MAX_WORDS]
    assert len(learned.aliases) == lv.MAX_ALIASES
    assert list(learned.aliases) == [h for h, _ in aliases[:lv.MAX_ALIASES]]


# ─── Damaged files: empty, one note, no contents ─────────────────────────────


@pytest.mark.parametrize("content", [
    "{ not json " + SECRET,
    json.dumps(_document(words=[SECRET], schema="dictation.learning/2")),
    json.dumps({"words": [{"text": SECRET}]}),
    json.dumps([{"text": SECRET}]),
    json.dumps("just a string " + SECRET),
], ids=["unparsable", "other schema", "no schema", "a list", "a string"])
def test_a_damaged_file_is_empty_with_one_note_that_quotes_nothing(tmp_path, capsys, content):
    path = _write(tmp_path / lv.FILE_NAME, content)

    assert lv.load(path) == ([], {}, [], {})

    err = capsys.readouterr().err
    assert err.count("learned_vocabulary:") == 1, err
    assert SECRET not in err
    assert str(tmp_path) not in err, "the note must not name the user's directory"


def test_invalid_utf8_is_a_damaged_file_not_a_crash(tmp_path, capsys):
    path = tmp_path / lv.FILE_NAME
    path.write_bytes(b'{"schema": "dictation.learning/1", "words": [{"text": "\xff\xfe"}]}')

    assert lv.load(path) == ([], {}, [], {})
    assert capsys.readouterr().err.count("learned_vocabulary:") == 1


def test_the_note_is_printed_once_per_process(tmp_path, capsys):
    bad = _write(tmp_path / lv.FILE_NAME, "{ nope")
    other = _write(tmp_path / "other.json", json.dumps(_document(schema="nope")))

    for _ in range(3):
        lv.load(bad)
        lv.load(other)

    assert capsys.readouterr().err.count("learned_vocabulary:") == 1


def test_an_unreadable_path_is_empty_and_not_cached(tmp_path, capsys):
    """A read that fails (here: a directory where the file should be; on
    Windows, a sharing violation mid-replace) is empty for THIS transcribe
    only, never pinned until the file changes."""
    path = tmp_path / lv.FILE_NAME
    path.mkdir()

    assert lv.load(path) == ([], {}, [], {})
    assert lv._cache is None
    assert "learned_vocabulary:" in capsys.readouterr().err

    path.rmdir()
    _write(path, _document(words=["Trovioo"]))
    assert lv.load(path).words == ["Trovioo"]


# ─── Cache: follows the file, never the caller's copy ────────────────────────


def test_a_changed_file_is_read_again(tmp_path):
    path = _write(tmp_path / lv.FILE_NAME, _document(words=["Trovioo"]))
    assert lv.load(path).words == ["Trovioo"]

    _write(path, _document(words=["SkyDock", "Trovioo"]))

    assert lv.load(path).words == ["SkyDock", "Trovioo"]


def test_an_unchanged_file_is_served_from_the_cache(tmp_path):
    """Keyed by (mtime_ns, size), per the contract. Same size and a restored
    mtime is indistinguishable from "unchanged", and that is how the test
    proves the cache is consulted at all."""
    path = _write(tmp_path / lv.FILE_NAME, _document(words=["Trovioo"]))
    before = os.stat(path)
    assert lv.load(path).words == ["Trovioo"]

    _write(path, _document(words=["Troviox"]))              # same length
    os.utime(path, ns=(before.st_atime_ns, before.st_mtime_ns))

    assert lv.load(path).words == ["Trovioo"]


def test_a_caller_cannot_change_what_the_next_read_returns(tmp_path):
    """Both paths hand out copies: the read that fills the cache and every
    read the cache then serves."""
    path = _write(tmp_path / lv.FILE_NAME, _document(words=["Trovioo"], aliases=[("trovio", "Trovioo")]))
    for _ in range(2):                       # a miss, then a hit
        served = lv.load(path)
        served.words.append("Injected")
        served.aliases["injected"] = "Injected"
        served.origins[0] = "taught"

    again = lv.load(path)

    assert again.words == ["Trovioo"]
    assert again.aliases == {"trovio": "Trovioo"}
    assert again.origins == ["correction"]


def test_reading_never_writes(tmp_path):
    path = _write(tmp_path / lv.FILE_NAME, "{ damaged")
    before = (path.read_bytes(), os.stat(path).st_mtime_ns, sorted(tmp_path.iterdir()))

    lv.load(path)
    _write(path, _document(words=["Trovioo"]))
    stamp = (path.read_bytes(), os.stat(path).st_mtime_ns)
    lv.load(path)

    assert (path.read_bytes(), os.stat(path).st_mtime_ns) == stamp
    assert sorted(tmp_path.iterdir()) == before[2], "the reader created or renamed a file"


# ─── Where the file is ───────────────────────────────────────────────────────


@pytest.mark.parametrize("system", ["win32", "darwin"])
def test_the_file_sits_beside_the_users_alias_file(monkeypatch, tmp_path, system):
    """LEARNING.md §1: `<data>/dictation/learning.json` beside
    `bias_aliases.json`, resolved by the one platform switch in
    `spoken_aliases`."""
    monkeypatch.setattr(sys, "platform", system)
    monkeypatch.setenv("HOME", str(tmp_path))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "Local"))

    path = sa.learning_path()

    assert path.parent == sa.default_path().parent
    assert path.name == "learning.json"
    if system == "win32":
        assert path.parts[-4:] == ("Local", "Dictation", "dictation", "learning.json")
    else:
        assert path.parts[-5:] == ("Library", "Application Support", "Dictation", "dictation",
                                   "learning.json")


def test_an_explicit_alias_file_brings_its_own_directory(tmp_path):
    """A caller that names the alias file (every test) reads the learned
    layer from THAT directory, never from the real profile."""
    assert sa.learning_path(tmp_path / "bias_aliases.json") == tmp_path / "learning.json"


def test_context_reorders_cached_words_without_cross_scene_cache_or_strength_leak(tmp_path):
    document = _document(words=[f"Noise{i}" for i in range(40)] + ["Miles"])
    for row in document["words"]:
        row.update(adaptive=True, count=1, opposition=0)
    document["evidence"] = {"version": 1, "observations": [{
        "id": "one", "at": 1790000000000, "weight": 1, "scene": "terminal",
        "corrections": [{"heard": "mies", "meant": "Miles", "word": True, "alias": False, "secret": False}]}]}
    path = _write(tmp_path / lv.FILE_NAME, document)
    original = lv.load(path)
    assert original.words[-1] == "Miles"
    selected = lv.load(path, scene="terminal")
    assert selected.words[0] == "Miles"
    assert selected.deltas["Miles"] == .2 and selected.aliases == {}
    assert lv.load(path, scene="wechat") == original
    assert lv.load(path, scene={}) == original
    selected.words.clear()
    assert lv.load(path, scene="terminal").words[0] == "Miles"
    document["evidence"]["observations"] = []
    _write(path, document)
    assert lv.load(path, scene="terminal") == original
