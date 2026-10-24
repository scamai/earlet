"""The vocabulary the host learned from the user's corrections, read-only.

The host app is the one writer of ``learning.json`` (LEARNING.md §1): it
replaces the file atomically after a correction, a teach or a removal. This
module only reads it, on every transcribe, and hands the worker two things:

- ``words``: the learned and taught written forms, strongest evidence first
  and then by scene (LEARNING.md §4.6, §4.7), which ``asr_server`` puts in front of the request's ``bias_phrases`` so they
  reach the recognition prompt and the declared-casing pass (§6);
- ``origins``: each word's ``origin`` as stored (``"taught"``,
  ``"correction"``, one this version does not know, or ``""`` when the entry
  names none), parallel to ``words``. Only a taught word was declared by the
  user, so only a taught word may have its spacing or locale spelling
  rewritten; a word learned from one correction restores case only, and its
  spaced form waits for the two-dictation alias (§4.2);
- ``aliases``: ``{heard: meant}`` rewrites, which ``spoken_aliases.load``
  layers between the shipped table and the user's own ``bias_aliases.json``.

The file sits beside ``bias_aliases.json``; ``spoken_aliases.learning_path``
names it, so there is one platform switch for both files. This module takes
the path as an argument and imports nothing from the worker, which keeps the
import graph a DAG (``spoken_aliases`` imports this, never the reverse).

Tolerant by contract, like the alias loader: a missing file is no learned
vocabulary, and so is a file that does not parse or carries another schema.
The host keeps a bad file aside and starts a new one, so the worker never
needs to repair anything. It says so on stderr ONCE per process, without
quoting the file: a learned word is personal vocabulary, and the hosts copy
worker stderr into their logs (LEARNING.md §6, last bullet).
"""
from __future__ import annotations

import json
import math
import os
import sys
from pathlib import Path
from typing import NamedTuple

from . import context_vocabulary

SCHEMA = "dictation.learning/1"
FILE_NAME = "learning.json"

# ─── Caps (LEARNING.md §4.5) ───
# The host already holds the file to these; the worker holds them again so a
# hand-edited or damaged file cannot put an unbounded list on the decode path.
MAX_LEARNED_WORDS = 100
MAX_TAUGHT_WORDS = 500
MAX_WORDS = MAX_LEARNED_WORDS + MAX_TAUGHT_WORDS
MAX_ALIASES = 200
MAX_EVIDENCE = 1_000_000


class Learned(NamedTuple):
    words: list[str]
    aliases: dict[str, str]
    #: ``origins[i]`` is the stored origin of ``words[i]``.
    origins: list[str]
    #: Per-entry recognition bias, keyed only by a written target word.
    #: Legacy three-argument callers get an empty map.
    deltas: dict[str, float] | None = None


EMPTY = Learned([], {}, [], {})


def merge_words(learned: list[str], requested) -> list[str]:
    """Put learned words before requested phrases, preserving first spelling.

    This is deliberately pure: stream routing and the final transcribe path
    use the same ordering and case-insensitive deduplication without sharing
    request dictionaries or importing one router from the other.
    """
    merged: list[str] = []
    seen: set[str] = set()
    for phrase in list(learned) + list(requested or []):
        if not isinstance(phrase, str):
            continue
        key = phrase.lower()
        if key in seen:
            continue
        seen.add(key)
        merged.append(phrase)
    return merged


#: The one origin the user declared (LEARNING.md §4.3).
TAUGHT = "taught"

#: One entry: (path, (st_mtime_ns, st_size), parsed document). The host rewrites the
#: file by atomic replace, which changes one or both; a re-read costs a JSON
#: parse of at most a few hundred short entries.
_cache: tuple[str, tuple[int, int], dict | None] | None = None

#: Whether the one "ignored a bad file" note has been printed.
_noted = False


# ─── Reading ───


def load(path: Path, *, scene=None) -> Learned:
    """The learned vocabulary in ``path``. Never raises, never writes.

    Returns fresh containers on every call, so a caller that edits its copy
    cannot change what the next transcribe reads.
    """
    global _cache
    try:
        stat = os.stat(path)
    except OSError:
        return _copy(EMPTY)  # absent: nothing learned yet, which is normal
    key = (stat.st_mtime_ns, stat.st_size)
    if _cache is not None and _cache[0] == str(path) and _cache[1] == key:
        return _from_document(_cache[2], scene=scene)

    try:
        # The key comes from the open handle, not the stat above: a replace
        # that lands between the two must not file new bytes under an old key.
        with open(path, "rb") as handle:
            stat = os.fstat(handle.fileno())
            raw = handle.read()
    except OSError:
        # Not cached: a transient failure (a replace in progress, a sharing
        # violation on Windows) must not pin "nothing learned" until the file
        # changes again.
        _note("could not be read")
        return _copy(EMPTY)

    document = _document(raw)
    _cache = (str(path), (stat.st_mtime_ns, stat.st_size), document)
    return _from_document(document, scene=scene)


def _parse(raw: bytes) -> Learned:
    return _from_document(_document(raw))


def _document(raw: bytes) -> dict | None:
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        _note("does not parse")
        return None
    if not isinstance(document, dict) or document.get("schema") != SCHEMA:
        _note(f"is not a {SCHEMA} file")
        return None
    return document


def _from_document(document: dict | None, *, scene=None) -> Learned:
    if document is None:
        return _copy(EMPTY)
    words, origins, deltas = _words(document.get("words"), evidence=document.get("evidence"), scene=scene)
    aliases = _aliases(document.get("aliases"))
    return Learned(words, aliases, origins, deltas)


def _words(entries, *, evidence=None, scene=None) -> tuple[list[str], list[str], dict[str, float]]:
    """Written forms ranked by evidence strength, then stored order, then
    reordered by scene (LEARNING.md §4.6, §4.7), which the prompt reads as
    relevance, and each one's origin. Strings only, trimmed,
    non-empty; an origin that is not a string reads as ``""``, which is not
    ``taught``."""
    words: list[str] = []
    origins: list[str] = []
    deltas: dict[str, float] = {}
    ranked: list[tuple[int, int, str, str, float, dict]] = []
    for entry in entries if isinstance(entries, list) else []:
        if not isinstance(entry, dict):
            continue
        text = entry.get("text")
        if not isinstance(text, str) or not text.strip():
            continue
        text = text.strip()
        origin = entry.get("origin") if isinstance(entry.get("origin"), str) else ""
        projection = _project(entry, origin, alias=False)
        if projection is None:
            continue
        rank, delta = projection
        ranked.append((rank, len(ranked), text, origin, delta, dict(entry, text=text)))
    ranked.sort(key=lambda row: (-row[0], row[1]))
    ranked = ranked[:MAX_WORDS]
    order = context_vocabulary.rank_indices([row[5] for row in ranked], evidence, scene)
    for i in order:
        _, _, text, origin, delta, _ = ranked[i]
        words.append(text)
        origins.append(origin)
        deltas.setdefault(text, delta)
    return words, origins, deltas


def _aliases(entries) -> dict[str, str]:
    """``{heard: meant}``. A heard form listed twice keeps its first row; the
    host never writes two, so a second one is damage, not intent."""
    aliases: dict[str, str] = {}
    seen: set[str] = set()
    for entry in entries if isinstance(entries, list) else []:
        if not isinstance(entry, dict):
            continue
        heard, meant = entry.get("heard"), entry.get("meant")
        if not (isinstance(heard, str) and isinstance(meant, str)):
            continue
        heard, meant = heard.strip(), meant.strip()
        if not heard or not meant or heard.lower() in seen:
            continue
        origin = entry.get("origin") if isinstance(entry.get("origin"), str) else ""
        projection = _project(entry, origin, alias=True)
        if projection is None:
            continue
        seen.add(heard.lower())
        aliases[heard] = meant
        if len(aliases) == MAX_ALIASES:
            break
    return aliases


def _project(entry: dict, origin: str, *, alias: bool) -> tuple[int, float] | None:
    """Return (stable-sort rank, bias delta), or reject malformed evidence.

    ``adaptive`` is deliberately opt-in. Existing rows without it retain the
    old worker behavior, while a damaged row that claims to be adaptive is
    ignored when it is an automatic correction. User-taught and unknown-origin
    rows remain usable for compatibility and privacy-safe fail-open behavior.
    """
    if origin == TAUGHT:
        return 4, 0.5
    if origin not in ("correction", ""):
        return 3, 0.5
    if "adaptive" not in entry:
        return 3, 0.5
    if entry.get("adaptive") is not True:
        return None
    count = _evidence_number(entry.get("count"), positive=True)
    opposition = _evidence_number(entry.get("opposition", 0), positive=False)
    if count is None or opposition is None:
        return None
    net = max(0, count - opposition)
    if alias:
        if net < 2:
            return None
        return (3 if net <= 3 else 4), (0.35 if net <= 3 else 0.5)
    if net == 0:
        return None
    if net == 1:
        return 1, 0.2
    if net <= 3:
        return 2, 0.35
    return 3, 0.5


def _evidence_number(value, *, positive: bool) -> int | None:
    """Validate bounded finite integer evidence without accepting booleans."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if int(value) != value:
        return None
    number = int(value)
    if (number < 1 if positive else number < 0) or number > MAX_EVIDENCE:
        return None
    return number


def _copy(learned: Learned) -> Learned:
    return Learned(list(learned.words), dict(learned.aliases),
                   list(learned.origins), dict(learned.deltas or {}))


def _note(problem: str) -> None:
    """Say once per process that a learning file was ignored. Never quotes
    the file or its path: both can carry the user's vocabulary or name."""
    global _noted
    if _noted:
        return
    _noted = True
    print(f"learned_vocabulary: {FILE_NAME} {problem}; "
          "dictating without learned vocabulary", file=sys.stderr, flush=True)
