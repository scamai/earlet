"""Exact spoken-form -> written-form rewrites: shipped, learned and the user's.

## Where the table comes from

Three layers, one file each.

`default_aliases.json` sits **beside this module** and is the set the product
ships with -- empty in Dictation, because a shipped alias rewrites every
user's text. It is there
rather than anywhere else because `tools/package.py:stage_resources` copies
this directory's modules and data files into both the Windows and the macOS
bundle, so a data file here reaches both with no packaging change on either
side. One file, read by the worker on both platforms, nothing to drift.

`bias_aliases.json` under the user's own data directory is theirs, and
`learning.json` beside it is what the host learned from their corrections
(`learned_vocabulary`, LEARNING.md §1). The worker writes neither.

The layering rule (LEARNING.md §1, "Precedence of spoken aliases"):

1. start from the shipped table;
2. overlay the learned aliases;
3. drop every spoken form named in the user file's `removed` array, whichever
   layer it came from;
4. overlay the user's own `aliases`, which win outright on a collision.

Shipped defaults are therefore **never written into the user's file**. That is
the whole answer to "what happens to a hand-tuned alias when the defaults
update": nothing can happen to it, because the update does not touch the file
it lives in. And a default somebody deliberately removed stays removed, because
the tombstone is in their file and outlives any number of releases.

Before this existed the Windows table was empty on every install -- a writer
and a settings pane and no data -- while the Mac it was meant to match had
eighteen entries typed in by hand over time.

## Why this exists when `phonetic_match` already corrects things

They solve different halves and only one of them can reach a homophone.

`phonetic_match` rewrites any span within a phonetic radius of a lexicon entry.
That is powerful and dangerous: measured on 4.75M words of ordinary English, a
radius of 3 rewrites 12% of all input, and even at radius 1 an unfiltered
vocabulary breaches, because `ElmTree` sits one phoneme from `area`. It is
therefore restricted to a screened lexicon and can never carry a term that
sounds like an ordinary word (internal research finding 19).

This module carries exactly those terms. An alias fires **only** on the literal
string the user declared, so its cost is the frequency of that one string rather
than of everything within a radius. `Trovio` is not an English word, so
`trovio -> Trovioo` costs nothing; `nemo -> Knemo` costs one rewrite per 1.2M
words of ordinary English, which a user can weigh against how often they
discuss the film.

## Why biasing cannot cover this instead

`Knemo` tokenizes as `[' K', 'nem', 'o']` and the K is silent, so the trie's
entry token has no acoustic support at all: the decoder must emit it *before*
any boost accrues. Synthetic decoder checks across varied voices and boost
values show that the term can fail to recover. This is structural, not a
tuning failure: a silent initial consonant can be emitted as an ordinary word.

The working shape is two stages. The trie biases the *spoken* form so the
acoustics are captured reliably; this table rewrites it to the *written* form.

## Matching

Case-insensitive, on word boundaries, longest alias first. Longest-first
matters: with both `whisper` and `local flow` declared, the shorter one would
otherwise consume the first word and leave `flow` stranded. It also keeps the
unit honest — `whisper` alone occurs 6 times in 1.2M words while `local flow`
occurs 0, so an alias declared at the wrong granularity is 6x more dangerous
than the same intent declared correctly.

A match that already sits inside a longer occurrence of its OWN written form
is left alone, so `flow -> Local Flow` applied to its own output stays
`Local Flow` (it does not come back as `Local Local Flow`). That makes one
alias idempotent, not a table. When one alias's spoken form lies inside
ANOTHER alias's written form at a boundary this matcher accepts (`gpt ->
ChatGPT` beside `gpt four oh -> GPT-4o`; `-` and spaces are boundaries), a
second pass rewrites the first one's output: `ask gpt four oh` -> `ask GPT-4o`
-> `ask ChatGPT-4o`. Teaching cannot rule such a pair out, because it checks
each alias only against itself.

So the table runs ONCE per decode. `whisper_mac.transcribe` applies it and
says so in its stats (`APPLIED_STAT`); `asr_server` applies it only to text
no engine has already rewritten, and takes the flag off the reply. A separate
meeting-caption path may apply it over engine output a second time; Dictation
does not use that path.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

try:
    from . import learned_vocabulary
except ImportError:  # loaded as a top-level module (whisper_mac's fallback)
    import learned_vocabulary

SCHEMA_VERSION = "spoken_aliases/0.1.0"

#: The stats key an engine sets (to True) when it has already applied the
#: table to the text it returns, so the server's text pass does not apply it
#: again. See "Matching" above for why a second pass is not harmless.
APPLIED_STAT = "spoken_aliases_applied"

#: The shipped table's file name. It lives beside this module; see the header
#: for why that particular directory and not a platform-specific one.
DEFAULTS_FILE_NAME = "default_aliases.json"

#: Read once. The shipped file cannot change while the process runs, and
#: `load()` is called on EVERY transcribe. The user's file is deliberately not
#: cached alongside it: the settings pane promises "it applies from your next
#: dictation", and a cache would make that promise false.
_defaults_cache: dict[str, str] | None = None


def defaults_path() -> Path:
    """The shipped table, beside this module."""
    return Path(__file__).resolve().with_name(DEFAULTS_FILE_NAME)


def default_path() -> Path:
    """Beside the bias vocabulary, under Dictation's own user-data directory:
    ~/Library/Application Support/Dictation/dictation on macOS, and
    %LOCALAPPDATA%\\Dictation\\dictation on Windows (the app's data root)."""
    import os
    import sys
    if sys.platform == "win32":
        base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local"))
        return base / "Dictation" / "dictation" / "bias_aliases.json"
    return (Path.home() / "Library/Application Support/Dictation/dictation"
            / "bias_aliases.json")


def learning_path(path: Path | None = None) -> Path:
    """The host's `learning.json`, always beside the user's alias file.

    Derived from `default_path()` (or from the alias file a caller named) so
    the platform switch above stays the only one: the two files cannot drift
    into different directories, and a test that points `load()` at a
    temporary alias file reads the learned layer from that same directory
    rather than from the real user profile.
    """
    return (path or default_path()).with_name(learned_vocabulary.FILE_NAME)


def _read_file(path: Path) -> tuple[dict[str, str], set[str]]:
    """One alias file as `(aliases, removed)`. Never raises.

    A missing or malformed file yields an empty table rather than an error: the
    failure mode of this feature must be "dictation behaves exactly as before",
    never "dictation stops working". Same contract as the vocabulary loader.

    `removed` is read ONLY from a wrapped file -- one carrying an `aliases`
    member. A flat `{spoken: written}` file is the whole table by definition,
    so a key that happened to be spelled `removed` there is somebody's alias,
    not a tombstone list, and reading it as one would silently delete a
    declared rewrite.
    """
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}, set()
    if not isinstance(raw, dict):
        return {}, set()

    wrapped = "aliases" in raw
    table = raw.get("aliases", raw)
    if not isinstance(table, dict):
        table = {}
    aliases = {str(k).strip(): str(v).strip()
               for k, v in table.items() if str(k).strip() and str(v).strip()}

    removed: set[str] = set()
    if wrapped:
        listed = raw.get("removed", [])
        if isinstance(listed, list):
            removed = {str(k).strip().lower() for k in listed if str(k).strip()}
    return aliases, removed


def defaults() -> dict[str, str]:
    """The shipped table. Empty if it is missing or malformed."""
    global _defaults_cache
    if _defaults_cache is None:
        table, _ = _read_file(defaults_path())
        _defaults_cache = table
    return dict(_defaults_cache)


def load(path: Path | None = None, include_defaults: bool = True) -> dict[str, str]:
    """The effective table as {spoken: written}: shipped defaults, then the
    learned aliases, then the user's own declarations on top.

    `include_defaults=False` returns only what the user's file declares, which
    is what a surface editing that file needs to show as "yours".

    A lower row whose spoken form a higher layer also declares is DROPPED
    rather than shadowed, so the compiled alternation carries one branch per
    spoken form and `compile_pattern`'s longest-first ordering stays a
    function of the spoken forms alone. Keys compare case-insensitively
    because matching does.

    The user file's `removed` list suppresses shipped AND learned rows: a
    tombstone means "never rewrite this", whoever proposed the rewrite.
    """
    user_path = path or default_path()
    declared, removed = _read_file(user_path)
    if not include_defaults:
        return declared

    learned = learned_vocabulary.load(learning_path(user_path)).aliases
    # Lowest layer first; a later layer replaces an earlier row for the same
    # spoken form. The flag says whether the user's tombstones apply to it.
    layers = ((defaults(), True), (learned, True), (declared, False))
    rows: dict[str, tuple[str, str]] = {}
    for layer, removable in layers:
        for spoken, written in layer.items():
            if removable and spoken.lower() in removed:
                continue
            rows[spoken.lower()] = (spoken, written)
    return dict(rows.values())


def compile_pattern(aliases: dict[str, str]):
    """One regex over all spoken forms, longest first. None when empty.

    The boundary is **Latin alphanumeric**, not `\\w`, and that is the whole of
    the difference between this working and not working for a bilingual user.

    CJK characters ARE `\\w` under Python's Unicode `re`. So `(?!\\w)` finds no
    boundary between a Latin spoken form and the Chinese character after it,
    and every alias in a Chinese sentence silently failed to fire. Measured in
    the field on 2026-08-17: the same three terms in one code-mixed paragraph,
    same build, same session — `Knemo`, `Trovioo` and `Local Flow` rewritten
    correctly in the English half, while `Nemo里也存了` and `跟Trovio有过合作`
    came back carrying the alias table's own keys, unrewritten. The recognizer
    produced exactly the spoken form this table exists to replace and the
    replacement could not match it.

    This does NOT spend Finding 25's false-positive budget, and that was
    measured rather than assumed (internal research finding 34): over 1,200,007
    words of ordinary English, both boundaries match exactly **1** time. The
    equality is by construction — every alias is Latin, so the set of matchable
    strings is unchanged; only the adjacency condition relaxes, and only into
    contexts where a non-Latin character abuts, which English text does not
    contain. `Nemostuff` and `xnemo` are still rejected.

    What remains unmeasured, stated because a zero should not read as complete:
    how often one of these nine Latin spoken forms sits next to CJK meaning
    something else. No corpus exists for it. Ruled on the asymmetry instead —
    a wrong rewrite is visible and undoable, a miss must be fixed by hand every
    time.
    """
    if not aliases:
        return None
    ordered = sorted(aliases, key=len, reverse=True)
    return re.compile("(" + "|".join(_bounded(a) for a in ordered) + ")",
                      re.IGNORECASE)


def _bounded(form: str) -> str:
    """`form` as a regex with boundary assertions on its Latin/digit edges.

    Boundary assertions belong only on Latin/digit EDGES. A CJK-edged alias
    needs none — and demanding one breaks it whenever the text butts it
    against Latin/digits ("Blx90沸德勒": the '0' fails the lookbehind and the
    alias silently never fires; field 2026-08-19).
    """
    latin = lambda ch: ch.isascii() and (ch.isalnum() or ch == "_")
    pre = r"(?<![A-Za-z0-9_])" if latin(form[0]) else ""
    post = r"(?![A-Za-z0-9_])" if latin(form[-1]) else ""
    return pre + re.escape(form) + post


def apply(text: str, aliases: dict[str, str], pattern=None) -> tuple[str, list[str]]:
    """Rewrite declared spoken forms. Returns `(text, applied_written_forms)`.

    A rewrite that would produce the text already present is not counted, so a
    user who says the written form gets no spurious "alias applied" record.

    A match that already lies inside a LONGER occurrence of its own written
    form (case-insensitive, same boundary rules) is left alone. `flow ->
    Local Flow` turns "I use flow" into "I use Local Flow", and a second pass
    then finds `Flow` inside `Local Flow` and keeps it, where it used to
    produce "Local Local Flow". Only the alias's OWN written form shelters a
    match, so a second pass is safe for a single alias but not for a table
    in which one alias's spoken form lies inside another's written form
    (see "Matching" in the module docstring); run it once per decode. An
    occurrence the same length as the match is the written form itself in
    other letter case, which the ordinary rule below already settles.
    """
    if not text or not aliases:
        return text, []
    pattern = pattern or compile_pattern(aliases)
    if pattern is None:
        return text, []

    lowered = {k.lower(): v for k, v in aliases.items()}
    applied: list[str] = []
    occurrences: dict[str, list[tuple[int, int]]] = {}

    def already_written(match: re.Match, written: str) -> bool:
        if written not in occurrences:
            occurrences[written] = [
                found.span() for found in
                re.finditer(_bounded(written), text, re.IGNORECASE)] if written else []
        start, end = match.span(1)
        return any(s <= start and end <= e and e - s > end - start
                   for s, e in occurrences[written])

    def swap(match: re.Match) -> str:
        written = lowered.get(match.group(1).lower())
        if written is None or written == match.group(1):
            return match.group(1)
        if already_written(match, written):
            return match.group(1)
        applied.append(written)
        return written

    return pattern.sub(swap, text), applied
