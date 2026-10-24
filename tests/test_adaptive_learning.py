"""Independent insertions, correction reversals, migration and bounded state."""
import copy
import json
import sys
from pathlib import Path
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import adaptive_reference as adaptive
import learning_reference as ref

LEXICON = ref.Lexicon(["we", "use", "the", "server", "today", "my", "a", "is", "password", "sunshine"])
AT = 1_790_000_000_000


def book():
    return ref.Learning(LEXICON)


def correct(b, identity, heard="Mies", meant="Miles", at=AT):
    return b.observe(identity, f"we use {heard} today", f"we use {meant} today", at)


def test_one_draft_can_change_its_mind_and_retract_without_inflating_counts():
    b = book()
    correct(b, "one")
    before = copy.deepcopy(b.state)
    correct(b, "one", at=AT + 1)
    assert b.state == before
    correct(b, "one", meant="Mils", at=AT + 2)
    assert [w["text"] for w in b.state["words"]] == ["Mils"]
    assert b.state["words"][0]["count"] == 1
    correct(b, "one", meant="Mies", at=AT + 3)
    assert b.projection()["words"] == []
    assert b.state["evidence"]["observations"] == []


def test_restart_preserves_deduplication_and_later_drafts_can_build_confidence():
    b = book()
    correct(b, "one")
    b = ref.Learning(LEXICON, json.loads(json.dumps(b.state)))
    correct(b, "one")
    correct(b, "two", at=AT + 1)
    correct(b, "three", at=AT + 2)
    assert b.state["words"][0]["count"] == 3
    assert len(b.state["evidence"]["observations"]) == 3


def test_explicit_rejection_demotes_then_suppresses_but_can_be_retracted():
    b = book()
    correct(b, "one")
    correct(b, "two")
    correct(b, "three", heard="Miles", meant="Mies")
    miles = next(w for w in b.state["words"] if w["text"] == "Miles")
    assert (miles["count"], miles["opposition"]) == (2, 1)
    correct(b, "four", heard="Miles", meant="Mies")
    assert "Miles" not in b.projection()["words"]
    correct(b, "four", heard="Miles", meant="Miles")
    assert "Miles" in b.projection()["words"]
    correct(b, "three", heard="Miles", meant="Miles")
    assert b.state["words"][0]["opposition"] == 0


def test_contradicting_one_promoted_alias_suspends_its_rewrite_until_clear_margin():
    b = book()
    for identity in ("one", "two"):
        correct(b, identity, "trovio", "Trovioo")
    assert b.projection()["aliases"] == {"trovio": "Trovioo"}
    correct(b, "three", "trovio", "TrovioAI")
    assert b.projection()["aliases"] == {}
    correct(b, "four", "trovio", "Trovioo")
    assert b.projection()["aliases"] == {"trovio": "Trovioo"}


def test_taught_words_and_aliases_survive_negative_evidence_and_revisions():
    b = book()
    b.teach("Miles", "Mies", AT)
    for i in range(4):
        correct(b, str(i), "Miles", "Mils")
    assert next(w for w in b.state["words"] if w["text"] == "Miles")["origin"] == "taught"
    assert b.projection()["aliases"] == {"mies": "Miles"}
    correct(b, "0", "Miles", "Miles")
    assert b.projection()["aliases"] == {"mies": "Miles"}


def test_removal_is_not_undone_by_replacing_an_old_observation():
    b = book()
    correct(b, "one")
    b.remove_word("Miles", AT + 1)
    correct(b, "one", at=AT + 2)
    correct(b, "two", at=AT + 3)
    assert b.projection()["words"] == []
    b.teach("Miles", None, AT + 4)
    correct(b, "one", meant="Mils", at=AT + 5)
    assert "Miles" in b.projection()["words"]


def test_legacy_vocabulary_is_migrated_lazily_with_bounded_support_and_unknowns():
    b = book()
    b.state["unknown"] = {"keep": True}
    b.state["words"] = [{"text": "Miles", "origin": "correction", "count": 500,
                          "first_seen": AT - 1, "last_seen": AT, "future": [1, 2]}]
    correct(b, "one")
    row = b.state["words"][0]
    assert row["count"] == adaptive.MAX_LEGACY_WEIGHT + 1
    assert row["future"] == [1, 2] and b.state["unknown"] == {"keep": True}
    assert row["first_seen"] == AT - 1
    correct(b, "one")
    assert b.state["words"][0]["count"] == 5


def test_evidence_is_bounded_and_does_not_retain_sentences_or_unedited_words():
    b = book()
    for i in range(adaptive.MAX_OBSERVATIONS + 3):
        correct(b, str(i), at=AT + i)
    records = b.state["evidence"]["observations"]
    assert len(records) == adaptive.MAX_OBSERVATIONS
    encoded = json.dumps(records)
    assert "we use" not in encoded and "today" not in encoded
    assert records[0]["id"] == "3"
    b.clear()
    assert "evidence" not in b.state


def test_unknown_journal_version_is_preserved_without_automatic_mutation():
    b = book()
    b.state["evidence"] = {"version": 2, "future": "keep"}
    before = copy.deepcopy(b.state)
    correct(b, "one")
    assert b.state == before


@pytest.mark.parametrize("journal", [None, [], {"version": True, "observations": []},
    {"version": 1, "observations": [None]},
    {"version": 1, "observations": [{"id": "bad", "at": AT, "weight": True, "corrections": []}]}])
def test_malformed_optional_journal_never_discards_existing_vocabulary(journal):
    b = book()
    b.teach("Miles", None, AT)
    b.state["evidence"] = journal
    before = copy.deepcopy(b.state)
    correct(b, "one")
    assert b.state == before


def test_unknown_evidence_keys_and_identical_observation_timestamp_survive():
    b = book()
    correct(b, "one")
    journal = b.state["evidence"]
    journal["future"] = {"keep": True}
    record = journal["observations"][0]
    record["future"] = [1, 2]
    record["corrections"][0]["future"] = "keep"
    before = copy.deepcopy(b.state)
    correct(b, "one", at=AT + 1)
    assert b.state == before


def test_migration_does_not_count_an_old_word_and_its_pending_vote_twice():
    b = book()
    b.state["words"] = [{"text":"Trovioo", "origin":"correction", "count":1,
                         "first_seen":AT-20, "last_seen":AT-10}]
    b.state["pending"] = [{"heard":"trovio", "meant":"Trovioo", "count":1,
                           "dictations":["old"], "first_seen":AT-20, "last_seen":AT-10}]
    correct(b, "new", "trovio", "Trovioo")
    assert b.state["words"][0]["count"] == 2
    assert b.state["words"][0]["first_seen"] == AT-20
    assert b.projection()["aliases"] == {"trovio":"Trovioo"}


def test_migration_keeps_an_already_admitted_secret_shaped_legacy_word():
    b = book()
    b.state["words"] = [{"text":"Qword000", "origin":"correction", "count":1,
                         "first_seen":AT-20, "last_seen":AT-10}]
    correct(b, "new", "trovio", "Trovioo")
    assert next(w for w in b.state["words"] if w["text"] == "Qword000")["count"] == 1


def test_scene_is_optional_reversible_and_fixed_for_an_independent_draft():
    b = book()
    b.observe("scene", "we use Mies", "we use Miles", AT, scene="terminal")
    b = ref.Learning(LEXICON, json.loads(json.dumps(b.state)))
    b.observe("scene", "we use Mies", "we use Mils", AT + 1, scene="wechat")
    rows = b.state["evidence"]["observations"]
    assert len(rows) == 1 and rows[0]["scene"] == "terminal"
    b.observe("scene", "we use Mies", "we use Mies", AT + 2, scene="wechat")
    assert b.state["evidence"]["observations"] == []
    b.observe("other", "we use Mies", "we use Miles", AT + 3, scene="a private application")
    assert "scene" not in b.state["evidence"]["observations"][0]
