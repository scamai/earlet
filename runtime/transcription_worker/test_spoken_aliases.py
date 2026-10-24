"""Tests for the spoken-form alias table, and for it actually being called."""
import importlib.util
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker import spoken_aliases as sa  # noqa: E402

#: apps/dictation -- the product root this worker ships inside.
PRODUCT_ROOT = Path(__file__).resolve().parents[2]

TABLE = {"nemo": "Knemo", "trovio": "Trovioo", "whisper flow": "Whispr Flow"}


def test_rewrites_synthetic_single_and_multiword_terms():
    """Fictional terms exercise a silent prefix, an extra letter and a phrase.

    These declared spellings test literal alias application, not acoustic
    recognition or a measured phonetic distance.
    """
    heard = ("Ask Nemo what we decided about PadForge. Do you know the company "
             "called Trovio? We are comparing ourselves against Whisper Flow.")
    fixed, applied = sa.apply(heard, TABLE)
    assert "Knemo" in fixed and "Nemo " not in fixed
    assert "Trovioo" in fixed and "Trovio?" not in fixed
    assert "Whispr Flow" in fixed and "Whisper Flow" not in fixed
    assert sorted(set(applied)) == ["Knemo", "Trovioo", "Whispr Flow"]


def test_longest_alias_wins():
    """`whisper` must not consume the first word of `whisper flow`."""
    table = {"whisper": "Whisper", "whisper flow": "Whispr Flow"}
    fixed, _ = sa.apply("we use whisper flow daily", table)
    assert fixed == "we use Whispr Flow daily"


def test_matches_on_word_boundaries_only():
    fixed, applied = sa.apply("the nemophila bloomed", TABLE)
    assert fixed == "the nemophila bloomed"
    assert applied == []


def test_a_term_touching_chinese_still_rewrites():
    """Synthetic mixed-script regression, and why the boundary is not `\\w`.

    CJK characters are `\\w` under Python's Unicode `re`, so the old
    `(?!\\w)` found no boundary between a Latin spoken form and the Chinese
    character after it. The same declared aliases must fire in both scripts.
    """
    for said, want in [
        ("Nemo里也存了", "Knemo里也存了"),
        ("我们之前跟trovio有过合作", "我们之前跟Trovioo有过合作"),
        ("用whisper flow对比一下", "用Whispr Flow对比一下"),
    ]:
        fixed, applied = sa.apply(said, TABLE)
        assert fixed == want, f"{said!r} -> {fixed!r}, wanted {want!r}"
        assert applied, f"{said!r} recorded no rewrite"


def test_relaxing_the_boundary_did_not_relax_the_latin_one():
    """The budget this change is allowed to spend is zero, so prove the set of
    matchable strings did not grow: a Latin character on either side still
    blocks, and `_` counts as Latin for this purpose (identifiers)."""
    for blocked in ["Nemostuff", "xnemo", "nemo2", "3nemo", "nemo_ctx", "ctx_nemo"]:
        fixed, applied = sa.apply(blocked, TABLE)
        assert fixed == blocked, f"{blocked!r} was rewritten to {fixed!r}"
        assert applied == []


def test_is_case_insensitive_but_writes_the_declared_form():
    fixed, _ = sa.apply("NEMO and nemo and Nemo", TABLE)
    assert fixed == "Knemo and Knemo and Knemo"


def test_saying_the_written_form_is_not_recorded_as_a_rewrite():
    table = {"Knemo": "Knemo"}
    fixed, applied = sa.apply("Knemo shipped", table)
    assert fixed == "Knemo shipped"
    assert applied == []


def test_missing_or_malformed_file_yields_an_empty_table(tmp_path):
    """Asserted on the USER layer, which is what this test was always about.

    It read `sa.load(...) == {}` while `load()` returned only that layer. It
    now returns the shipped floor underneath it too, so the same intent is
    `include_defaults=False`: a missing or malformed file contributes nothing,
    and does not raise. `test_a_malformed_user_file_still_leaves_the_shipped_
    table_working` covers the other half.
    """
    assert sa.load(tmp_path / "absent.json", include_defaults=False) == {}
    bad = tmp_path / "bad.json"
    bad.write_text("not json at all", encoding="utf-8")
    assert sa.load(bad, include_defaults=False) == {}
    wrapped = tmp_path / "ok.json"
    wrapped.write_text(json.dumps({"aliases": {"trovio": "Trovioo"}}), encoding="utf-8")
    assert sa.load(wrapped, include_defaults=False) == {"trovio": "Trovioo"}
    bare = tmp_path / "bare.json"
    bare.write_text(json.dumps({"nemo": "Knemo"}), encoding="utf-8")
    assert sa.load(bare, include_defaults=False) == {"nemo": "Knemo"}


# --- the check that exists because this repo has shipped dead code twice ----


def test_every_personalization_mechanism_has_a_caller():
    """A mechanism with no call site is not shipped, however well it is tested.

    `phonetic_match`'s screened lexicon was tuned on 4.75M words, tested, and
    pushed while its only consumer, a transcript corrector (since deleted; see
    the removed list in import-manifest.json), had zero callers — so the one field
    failure it could have fixed went unfixed. Same class as the two earlier
    trie-wiring bugs, where the symptom was the ABSENCE of a symptom.

    This asserts the wiring, not the behaviour, because the behaviour tests all
    passed while the feature was dead.
    """
    worker = Path(__file__).resolve().parent
    sources = [p for p in worker.glob("*.py")
               if not p.name.startswith("test_")]

    def callers_of(module: str, call: str) -> list[str]:
        """Files containing an actual CALL, not merely the name.

        The first version of this matched the bare module name as a substring
        and passed against a file where the only occurrence was
        `spoken_aliases_DISABLED` — a check that cannot fail is worth exactly as
        much as the criterion-5 gate that never fired. `call` must therefore
        include the open paren.
        """
        return [p.name for p in sources
                if p.stem != module and call in p.read_text(encoding="utf-8")]

    assert callers_of("spoken_aliases", "spoken_aliases.apply("), (
        "nothing calls spoken_aliases.apply() — the alias table is dead code")
    assert callers_of("trie_decode", "transcribe_with_trie_repaired("), (
        "nothing calls transcribe_with_trie_repaired() — the seam fix is dead")
    assert callers_of("beam_decode", "BiasTrie("), (
        "nothing constructs a BiasTrie")


# ─── The bytes a Windows writer produces ─────────────────────────────────────

# Dictation's Windows app writes no bias_aliases.json; the user's own file is
# hand-written. This synthetic byte fixture preserves the Windows JSON shape:
# CRLF line endings, two-space indent, a `schema` member this loader ignores,
# and NO byte-order mark.
#
# Pinned as literal bytes because every one of those is a way the two sides can
# silently stop agreeing. A BOM is the sharpest: this module reads with
# encoding="utf-8", not utf-8-sig, so a BOM lands in `load`'s ValueError branch
# and empties the table with no error anywhere — the user's declared
# corrections just stop firing.
WINDOWS_WRITER_BYTES = (
    b'{\r\n'
    b'  "schema": "spoken_aliases/0.1.0",\r\n'
    b'  "aliases": {\r\n'
    b'    "Javis": "Jarvis",\r\n'
    b'    "LumaNodes": "Luma Notes"\r\n'
    b'  }\r\n'
    b'}'
)


def test_loads_the_synthetic_windows_json_shape(tmp_path):
    """The Windows half of this feature: the file a person keeps there.

    `asr_server.py` applies this table on every transcribe, and
    `default_path()` resolves
    `%LOCALAPPDATA%\\Dictation\\dictation\\bias_aliases.json` on Windows. Without
    a file there the table is empty, and a term the recogniser consistently
    misspells has no fix at all.

    The literal byte fixture checks the loader's encoding contract; it does
    not establish that a native Windows writer ran in this test.
    """
    path = tmp_path / "bias_aliases.json"
    path.write_bytes(WINDOWS_WRITER_BYTES)

    # The user layer alone: this test is about the seam between the two
    # languages, and mixing the shipped floor in would let a change to that
    # file break a test about C#'s serialiser.
    assert sa.load(path, include_defaults=False) == {
        "Javis": "Jarvis", "LumaNodes": "Luma Notes"}


def test_the_loaded_windows_json_shape_rewrites_text(tmp_path):
    """Loadable is not the same as effective — the mapping has to fire."""
    path = tmp_path / "bias_aliases.json"
    path.write_bytes(WINDOWS_WRITER_BYTES)

    fixed, applied = sa.apply("I asked Javis to open LumaNodes.", sa.load(path))

    assert fixed == "I asked Jarvis to open Luma Notes."
    assert sorted(applied) == ["Jarvis", "Luma Notes"]


def test_a_byte_order_mark_would_be_a_silent_failure(tmp_path, shipped):
    """States the cost of the rule the writer keeps, so it is not folklore.

    Not an assertion about the writer — it is one about THIS loader, and it is
    why a writer must write UTF-8 without a BOM: the failure is total, silent,
    and indistinguishable from an empty table.

    The shipped floor made this **worse**, and that is recorded rather than
    left to be rediscovered. Before it, a BOM emptied the whole table and the
    user would notice every rewrite stop at once. Now the shipped rows keep
    firing, so the feature still looks alive while every correction that person
    typed has silently gone. Same rule, higher cost for breaking it.
    """
    path = tmp_path / "bias_aliases.json"
    path.write_bytes(b"\xef\xbb\xbf" + WINDOWS_WRITER_BYTES)

    assert sa.load(path, include_defaults=False) == {}
    assert sa.load(path) == shipped


def test_the_windows_default_path_is_the_one_the_app_writes_to(monkeypatch):
    """One location, agreed by both sides.

    A hand-written file goes to `dictation\\bias_aliases.json` under the app's
    own data root, `%LOCALAPPDATA%\\Dictation`. A file beside the vocabulary
    would be one nothing ever reads, and nothing would report it.
    """
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setenv("LOCALAPPDATA", r"C:\Users\someone\AppData\Local")

    path = sa.default_path()

    assert path.parts[-3:] == ("Dictation", "dictation", "bias_aliases.json")


# ─── The shipped table, and the layering it sits under ───────────────────────
#
# The layer ships as data, in ONE file, read from beside this module on both
# platforms. What
# Dictation puts in it is deliberately nothing: a shipped alias rewrites every
# user's text on every install, and company or partner names in it would land
# in everyone's documents. So the tests below split in two. The mechanism -- layering,
# tombstones, fail-quiet reads -- runs against a TEST-LOCAL shipped table and
# keeps being tested whatever the product ships; the shipped file itself is
# held to the rules any future entry has to pass.


@pytest.fixture
def shipped(monkeypatch, tmp_path):
    """Serve `TABLE` as the shipped layer for one test, then restore the real
    file. Returns the table the test should expect underneath the user's."""
    path = tmp_path / "shipped" / sa.DEFAULTS_FILE_NAME
    path.parent.mkdir()
    path.write_text(json.dumps({"schema": sa.SCHEMA_VERSION, "aliases": TABLE}),
                    encoding="utf-8")
    monkeypatch.setattr(sa, "defaults_path", lambda: path)
    monkeypatch.setattr(sa, "_defaults_cache", None)
    return dict(TABLE)


def test_the_shipped_table_is_where_the_packager_will_put_it():
    """`tools/package.py:stage_resources` copies this directory's `.py` and
    `.json` files, minus tests, into both the Windows and the macOS bundle. A
    data file HERE therefore reaches both with no packaging change on either
    side. Anywhere else is a packaging step that has to be remembered twice.
    """
    path = sa.defaults_path()

    assert path.name == "default_aliases.json"
    assert path.parent == Path(__file__).resolve().parent
    assert path.is_file(), f"{path} is missing — the shipped layer has no file"
    # Empty is a decision, not an absence: the file and its schema stay so the
    # mechanism and the packager keep a real file to carry.
    raw = json.loads(path.read_text(encoding="utf-8"))
    assert raw["schema"] == sa.SCHEMA_VERSION
    assert isinstance(raw["aliases"], dict)


def test_the_shipped_table_carries_no_global_vocabulary(tmp_path):
    """The source preview ships no aliases that rewrite everybody's text.

    User-declared aliases are tested separately with synthetic tables. Any
    future global alias requires an explicit change to this release contract.
    """
    assert sa.defaults() == {}
    table = sa.load(tmp_path / "bias_aliases.json")
    for said in ("We watched Finding Nemo last night.",
                 "走在黑路上要小心",
                 "Open Luma nodes and ask Javis.",
                 "I use bad forge and new tools daily."):
        assert sa.apply(said, table) == (said, []), f"{said!r} was rewritten"


def test_a_fresh_install_applies_the_shipped_layer(tmp_path, shipped):
    """`tmp_path` has no user file at all, which is exactly a fresh install:
    whatever ships is what it gets, and it fires."""
    table = sa.load(tmp_path / "bias_aliases.json")

    assert table == shipped
    fixed, applied = sa.apply("Ask Nemo about Trovio.", table)
    assert fixed == "Ask Knemo about Trovioo."
    assert sorted(applied) == ["Knemo", "Trovioo"]


def test_shipped_defaults_are_not_ordinary_english_words():
    """The rule the shipped file is held to, checked against the web2 word list
    the Windows app ships rather than against an opinion.

    A user's own alias costs them what they chose to spend. A SHIPPED alias
    spends it on every install, for somebody who never asked -- so a shipped
    single-token spoken form must not be an ordinary English word. Multi-token
    phrases are checked separately. The ordinary-word check here is only
    an admission floor, not evidence that a phrase is acoustically safe.

    Written vocabulary cannot automatically become a global alias: doing so
    may put an ordinary word such as `hive` or `clear` on the spoken side.
    """
    import gzip

    # The word list ships inside this product (Dictation.Services embeds
    # it), so its absence is a broken tree, not an optional dependency: a skip
    # here once hid this whole check behind a path outside this product.
    words_gz = (PRODUCT_ROOT / "windows" / "Dictation.Services"
                / "Resources" / "web2-words.txt.gz")
    assert words_gz.is_file(), f"{words_gz} is missing; the admission rule has no word list"

    with gzip.open(words_gz, "rt", encoding="utf-8", errors="ignore") as handle:
        web2 = {line.strip().lower() for line in handle if line.strip()}
    assert len(web2) > 200_000, "the word list did not load; this check would pass vacuously"

    def ordinary(table):
        return sorted(spoken for spoken in table
                      if " " not in spoken and spoken.lower() in web2)

    # The positive control, through the same word list and the same filter. A
    # rule that rejects nothing is not a rule, and an empty shipped table would
    # pass the assertion below however broken this check was.
    control = {"hive": "Hive", "dell": "Dell", "moonshine": "Moonshine",
               "clear": "Clear", "trovio": "Trovioo", "whisper flow": "Whispr Flow"}
    assert ordinary(control) == ["clear", "dell", "hive", "moonshine"]

    offenders = ordinary(sa.defaults())
    assert offenders == [], (
        f"shipped aliases that are ordinary English words: {offenders}. "
        "They would rewrite ordinary speech on every install.")


def test_the_entries_that_were_left_out_stay_out():
    """Representative ordinary words and inflections stay out of defaults.

    `mies` folds to the ordinary base `my`, even though it is absent as a
    literal entry in the bundled word list.
    """
    for ordinary in ["hive", "dell", "moonshine", "clear", "mies"]:
        assert ordinary not in sa.defaults(), (
            f"{ordinary!r} is an ordinary English word and must not ship as an alias")


def test_a_user_declaration_outranks_the_shipped_one(tmp_path, shipped):
    """Their file, their answer. The shipped row is dropped rather than
    shadowed, so the compiled alternation carries one branch per spoken form.
    """
    assert shipped["nemo"] == "Knemo"
    path = tmp_path / "bias_aliases.json"
    path.write_text(json.dumps({"aliases": {"nemo": "Nemo the fish"}}), encoding="utf-8")

    table = sa.load(path)

    assert table["nemo"] == "Nemo the fish"
    assert len([k for k in table if k.lower() == "nemo"]) == 1
    assert sa.apply("ask nemo", table)[0] == "ask Nemo the fish"


def test_a_removed_default_stays_removed(tmp_path, shipped):
    """The whole answer to "what happens to my edits when the defaults update".

    Shipped rows are never written into the user's file, so an update cannot
    overwrite a hand-tuned alias. The other direction needs a record: without a
    tombstone, the next build would silently reinstate a mapping somebody
    deliberately deleted, and they would delete it again after every release.
    """
    path = tmp_path / "bias_aliases.json"
    path.write_text(json.dumps({"aliases": {}, "removed": ["NEMO"]}), encoding="utf-8")

    table = sa.load(path)

    assert "nemo" not in table, "a removed default came back"
    assert "trovio" in table, "one tombstone suppressed the whole shipped table"
    assert sa.apply("ask nemo", table)[0] == "ask nemo"


def test_removed_is_only_a_tombstone_list_in_a_wrapped_file(tmp_path):
    """A FLAT `{spoken: written}` file is the whole table by definition, so a
    key that happens to be spelled `removed` there is somebody's alias. Reading
    it as a tombstone list would silently delete a declared rewrite -- and the
    flat shape is the one `load()` has always accepted.
    """
    path = tmp_path / "bias_aliases.json"
    path.write_text(json.dumps({"removed": "Removed Ltd"}), encoding="utf-8")

    assert sa.load(path)["removed"] == "Removed Ltd"


def test_the_user_half_can_be_read_alone(tmp_path):
    """What a surface editing that file needs to show as "yours"."""
    path = tmp_path / "bias_aliases.json"
    path.write_text(json.dumps({"aliases": {"zzz": "ZZZ"}}), encoding="utf-8")

    assert sa.load(path, include_defaults=False) == {"zzz": "ZZZ"}


def test_a_malformed_user_file_still_leaves_the_shipped_table_working(tmp_path, shipped):
    """Stated contract: the failure mode is "dictation behaves exactly as
    before", never "dictation stops working". With a floor underneath, "as
    before" now means the shipped rewrites keep firing rather than everything
    silently switching off because of one stray comma.
    """
    path = tmp_path / "bias_aliases.json"
    path.write_text("{ this is not json", encoding="utf-8")

    assert sa.load(path) == shipped


def _package_tool():
    """`tools/package.py`, loaded by path: it is a script, not a package."""
    spec = importlib.util.spec_from_file_location(
        "dictation_package_tool", PRODUCT_ROOT / "tools" / "package.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize("target", ["windows", "macos"])
def test_the_packager_actually_carries_the_shipped_table(tmp_path, target):
    """Present in the repo is not the same as present at runtime.

    The mechanism has shipped with no data behind it once already -- the C#
    alias writer and its settings pane went out while the worker's alias
    directory stayed empty on every install -- so "does the data reach the
    runtime" is asserted rather than assumed.

    Asserted by RUNNING the packager's staging step rather than by reading its
    filter: `stage_resources` copies worker files by suffix, and the table is a
    `.json` file among `.py` modules, so a suffix list narrowed to modules
    would ship `spoken_aliases.py` reading a table that is not there.
    """
    resources = tmp_path / "resources"

    _package_tool().stage_resources(resources, target, None, None, app_only=True)

    staged = resources / "runtime" / "transcription_worker"
    assert (staged / "spoken_aliases.py").is_file(), "the loader itself was not staged"
    table = staged / sa.DEFAULTS_FILE_NAME
    assert table.is_file(), (
        f"tools/package.py does not stage {sa.DEFAULTS_FILE_NAME} for {target}, "
        "so that bundle's worker loads no shipped table")
    assert table.read_bytes() == sa.defaults_path().read_bytes()


# ─── A match inside its own written form is left alone ───────────────────────
#
# A written form that contains its own spoken form used to be rewritten again:
# `flow -> Whispr Flow` over "I use Whispr Flow" (the user said the written form,
# or a second pass saw the first one's output) came back as
# "I use Whispr Whispr Flow". The table runs once per decode (asr_server skips
# its pass after WhisperMac's), because across two aliases a second pass is
# NOT a no-op; the last test here pins that limit.


def test_a_self_containing_rewrite_is_idempotent():
    table = {"flow": "Whispr Flow"}

    once, applied = sa.apply("I use flow", table)
    twice, applied_again = sa.apply(once, table)

    assert once == twice == "I use Whispr Flow"
    assert applied == ["Whispr Flow"]
    assert applied_again == [], "the second pass recorded a rewrite it did not make"


def test_a_match_inside_its_written_form_is_left_alone_in_any_case():
    """Case-insensitive, like matching: the recognizer's own `whispr flow` is
    the written form in the wrong case, not a spoken `flow` to expand. Casing
    is declared_casing's job."""
    fixed, applied = sa.apply("I use whispr flow and flow", {"flow": "Whispr Flow"})

    assert fixed == "I use whispr flow and Whispr Flow"
    assert applied == ["Whispr Flow"]


def test_the_written_form_uses_the_same_boundaries_as_the_spoken_one():
    """`Whispr Flowchart` is not an occurrence of `Whispr Flow`, so it does not
    shelter anything -- and `flow` inside it is not a whole word either."""
    table = {"flow": "Whispr Flow"}

    assert sa.apply("Whispr Flowchart then flow", table)[0] == "Whispr Flowchart then Whispr Flow"
    assert sa.apply("xWhispr Flow", table)[0] == "xWhispr Whispr Flow"


def test_idempotence_holds_for_a_cjk_written_form():
    table = {"沸德勒": "Blx90沸德勒"}

    once, _ = sa.apply("我们用沸德勒", table)

    assert once == "我们用Blx90沸德勒"
    assert sa.apply(once, table)[0] == once


def test_a_case_only_rewrite_still_fires():
    """An occurrence the SAME length as the match is the match itself; the
    shelter rule is for longer written forms only, or `knemo -> Knemo` would
    never fire."""
    assert sa.apply("ask knemo", {"knemo": "Knemo"}) == ("ask Knemo", ["Knemo"])


def test_a_table_with_no_alias_inside_anothers_written_form_is_idempotent():
    table = dict(TABLE, flow="Whispr Flow", **{"sky dock": "SkyDock"})
    said = "Ask nemo about trovio, whisper flow, flow and sky dock. Nemo里也存了"

    once, _ = sa.apply(said, table)

    assert sa.apply(once, table) == (once, [])


def test_across_two_aliases_a_second_pass_is_not_a_no_op():
    """The limit, pinned so the wording cannot drift back to "idempotent":
    `gpt` sits inside `GPT-4o` at a hyphen boundary, and only an alias's OWN
    written form shelters a match. Both aliases pass teaching's check, which
    compares each alias with itself. One pass is right; that is why the
    worker runs the table once per decode."""
    table = {"gpt four oh": "GPT-4o", "gpt": "ChatGPT"}

    once, _ = sa.apply("ask gpt four oh", table)

    assert once == "ask GPT-4o"
    assert sa.apply(once, table)[0] == "ask ChatGPT-4o"


# ─── The learned layer (LEARNING.md §1) ──────────────────────────────────────


def _learning(folder: Path, aliases: dict[str, str]) -> None:
    (folder / "learning.json").write_text(json.dumps({
        "schema": "dictation.learning/1",
        "words": [{"text": meant} for meant in aliases.values()],
        "aliases": [{"heard": h, "meant": m} for h, m in aliases.items()],
    }), encoding="utf-8")


@pytest.fixture
def fresh_learned(monkeypatch):
    from transcription_worker import learned_vocabulary

    monkeypatch.setattr(learned_vocabulary, "_cache", None)
    monkeypatch.setattr(learned_vocabulary, "_noted", False)


def test_learned_aliases_join_the_table(tmp_path, fresh_learned):
    _learning(tmp_path, {"javis": "Jarvis"})

    table = sa.load(tmp_path / "bias_aliases.json")

    assert table["javis"] == "Jarvis"
    assert sa.apply("ask javis", table)[0] == "ask Jarvis"


def test_learned_aliases_win_over_the_shipped_table(tmp_path, shipped, fresh_learned):
    _learning(tmp_path, {"NEMO": "Nemo Labs"})

    table = sa.load(tmp_path / "bias_aliases.json")

    assert [k for k in table if k.lower() == "nemo"] == ["NEMO"]
    assert sa.apply("ask nemo", table)[0] == "ask Nemo Labs"
    assert table["trovio"] == "Trovioo", "the rest of the shipped table stays"


def test_the_users_own_alias_wins_over_a_learned_one(tmp_path, fresh_learned):
    _learning(tmp_path, {"javis": "Jarvis", "trovio": "Trovioo"})
    (tmp_path / "bias_aliases.json").write_text(
        json.dumps({"aliases": {"Javis": "J.A.R.V.I.S."}}), encoding="utf-8")

    table = sa.load(tmp_path / "bias_aliases.json")

    assert [k for k in table if k.lower() == "javis"] == ["Javis"]
    assert sa.apply("ask javis about trovio", table)[0] == "ask J.A.R.V.I.S. about Trovioo"


def test_a_tombstone_suppresses_a_learned_alias_too(tmp_path, shipped, fresh_learned):
    _learning(tmp_path, {"javis": "Jarvis", "sky dock": "SkyDock"})
    (tmp_path / "bias_aliases.json").write_text(
        json.dumps({"aliases": {}, "removed": ["JAVIS", "nemo"]}), encoding="utf-8")

    table = sa.load(tmp_path / "bias_aliases.json")

    assert "javis" not in table and "nemo" not in table
    assert table["sky dock"] == "SkyDock" and table["trovio"] == "Trovioo"


def test_the_users_half_alone_never_includes_learned_rows(tmp_path, fresh_learned):
    _learning(tmp_path, {"javis": "Jarvis"})
    (tmp_path / "bias_aliases.json").write_text(json.dumps({"aliases": {"zzz": "ZZZ"}}), encoding="utf-8")

    assert sa.load(tmp_path / "bias_aliases.json", include_defaults=False) == {"zzz": "ZZZ"}


def test_a_damaged_learning_file_leaves_the_other_layers_working(tmp_path, shipped, fresh_learned):
    (tmp_path / "learning.json").write_text('{"schema": "dictation.learning/1", "aliases": [',
                                            encoding="utf-8")
    (tmp_path / "bias_aliases.json").write_text(json.dumps({"aliases": {"zzz": "ZZZ"}}), encoding="utf-8")

    assert sa.load(tmp_path / "bias_aliases.json") == dict(shipped, zzz="ZZZ")
