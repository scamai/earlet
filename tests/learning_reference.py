"""Reference implementation of the Dictation learning policy (LEARNING.md).

Not shipped. It exists so the rules in LEARNING.md are executable in one place:
``tests/test_learning_golden.py`` checks ``tests/fixtures/learning-golden.json``
against it, and the Swift (``macos/Sources/DictationLearning``) and C#
(``windows/Dictation.Core/Learning``) implementations are checked against the
same fixture. A disagreement between any two of the three is a bug in one of
them, never a tolerance.

Indices are Unicode code points, which is what Swift ``unicodeScalars`` and C#
``Rune`` enumeration give; the three implementations cut the same substrings.
"""

from __future__ import annotations

import copy
import unicodedata
import sys

import adaptive_reference
from dataclasses import dataclass

# ─── Knobs (LEARNING.md §3.2-§4.5; the schema name is §5) ───

SCHEMA = "dictation.learning/1"
MAX_TEXT_TOKENS = 200
REWRITE_MIN_TOKENS = 4
MAX_LATIN_TOKENS = 4
MAX_CJK_CHARS = 8
MAX_DISTANCE_RATIO = 0.5
ALIAS_PROMOTION_COUNT = 2
PENDING_DICTATION_IDS = 4
PENDING_TTL_MS = 30 * 24 * 60 * 60 * 1000
MAX_PENDING = 500
MAX_LEARNED_WORDS = 100
MAX_TAUGHT_WORDS = 500
MAX_ALIASES = 200
MAX_TOMBSTONES = 1000
MAX_TAUGHT_CHARS = 60
MAX_TAUGHT_LATIN_TOKENS = 6
MAX_TAUGHT_CJK_CHARS = 12
SECRET_MIN_LENGTH = 8
SECRET_MIN_DIGITS = 2
MIN_VERB_BASE = 3

JOINERS = frozenset("'’-._")
APOSTROPHES = frozenset("'’")
#: A doubled final consonant before -ed or -ing ("stopped", "running").
DOUBLED_CONSONANTS = frozenset("bcdfghjklmnpqrstvwxyz")
WORD_CATEGORIES = frozenset({"Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd"})
CJK_RANGES = (
    (0x3040, 0x30FF),  # Hiragana, Katakana
    (0x3400, 0x4DBF),  # CJK Extension A
    (0x4E00, 0x9FFF),  # CJK Unified Ideographs
    (0xAC00, 0xD7AF),  # Hangul syllables
    (0xF900, 0xFAFF),  # CJK Compatibility Ideographs
    (0x20000, 0x2FFFF),  # CJK Extensions B-F
)


# ─── Lexicon (LEARNING.md §2) ───


class Lexicon:
    """Ordinary-word test over a base-form list, three plural rules and the
    -ed/-ing endings.

    An empty lexicon FAILS CLOSED: every token counts as ordinary, so nothing
    is distinctive and no alias is admitted. (The earlier import lexicon fails
    open; learning must not.)
    """

    def __init__(self, words):
        self._words = frozenset(w.strip().lower() for w in words if w and w.strip())
        self._excluded = frozenset()

    @classmethod
    def from_webster(cls, text: str):
        """Webster's capitalized-only names are not common-word evidence.

        Explicit constructor lists still declare ordinary words irrespective
        of case. A/a and I/i survive through their lowercase entries.
        """
        entries = text.split()
        lexicon = cls(w for w in entries if w == w.lower())
        lexicon._excluded = frozenset(w.lower() for w in entries) - lexicon._words
        return lexicon

    @property
    def available(self) -> bool:
        return bool(self._words)

    def _base(self, word: str) -> bool:
        if word in self._excluded:
            return False  # Miles must not fold to the ordinary base mile.
        w = self._words
        if word in w:
            return True
        if word.endswith("ies") and len(word) > 3 and word[:-3] + "y" in w:
            return True
        if word.endswith("es") and len(word) > 2 and word[:-2] in w:
            return True
        return word.endswith("s") and len(word) > 1 and word[:-1] in w

    def _verb_base(self, word: str) -> bool:
        """``changed``, ``running``, ``tried``: a verb ending dropped (LEARNING.md §2).

        The base is the stem, the stem plus ``e`` (``chang|ed``), the stem less
        one of a doubled final consonant (``runn|ing``) or, before -ed, the stem
        with its final ``i`` turned back into ``y`` (``tri|ed``). It is looked up
        with the plural rules and must have at least 3 code points, so ``Ying``
        and ``Xing`` are not read as a letter plus -ing. An excluded name is
        checked first, as in ``_base``: ``Downing`` must not fold to ``down``.
        """
        if word in self._excluded:
            return False
        for suffix in ("ed", "ing"):
            if not word.endswith(suffix):
                continue
            stem = word[: -len(suffix)]
            bases = [stem, stem + "e"]
            if len(stem) >= 2 and stem[-1] == stem[-2] and stem[-1] in DOUBLED_CONSONANTS:
                bases.append(stem[:-1])
            if suffix == "ed" and stem.endswith("i"):
                bases.append(stem[:-1] + "y")
            if any(len(base) >= MIN_VERB_BASE and self._base(base) for base in bases):
                return True
        return False

    def is_ordinary(self, token: str) -> bool:
        if not self._words:
            return True
        word = token.lower()
        for i, ch in enumerate(word):
            if ch in APOSTROPHES:
                if i > 0:
                    word = word[:i]
                break
        parts = [p for p in _split_any(word, "-._") if p]
        if not parts:
            return True
        # A part with no letter ("10", "4417", "1-800") is ordinary: a number on
        # its own is never vocabulary (LEARNING.md §2), so a fix with no letter
        # (a PIN, a phone number) is never stored or prompted. A number beside a
        # distinctive word can be ("Trovioo 44171234", after a second dictation).
        return all(not has_letter(p) or self._base(p) or self._verb_base(p) for p in parts)


def has_letter(text: str) -> bool:
    return any(unicodedata.category(ch).startswith("L") for ch in text)


def _split_any(text: str, seps: str):
    part = []
    for ch in text:
        if ch in seps:
            yield "".join(part)
            part = []
        else:
            part.append(ch)
    yield "".join(part)


# ─── Tokens (LEARNING.md §3.1) ───


@dataclass(frozen=True)
class Token:
    text: str
    start: int
    end: int
    cjk: bool


def is_cjk(ch: str) -> bool:
    cp = ord(ch)
    return any(lo <= cp <= hi for lo, hi in CJK_RANGES)


def is_word_char(ch: str) -> bool:
    return not is_cjk(ch) and unicodedata.category(ch) in WORD_CATEGORIES


def tokenize(text: str) -> list[Token]:
    cps = list(text)
    tokens: list[Token] = []
    i, n = 0, len(cps)
    while i < n:
        ch = cps[i]
        if is_cjk(ch):
            tokens.append(Token(ch, i, i + 1, True))
            i += 1
            continue
        if is_word_char(ch):
            j = i + 1
            while j < n:
                if is_word_char(cps[j]):
                    j += 1
                elif cps[j] in JOINERS and j + 1 < n and is_word_char(cps[j + 1]):
                    j += 2
                else:
                    break
            tokens.append(Token("".join(cps[i:j]), i, j, False))
            i = j
            continue
        i += 1
    return tokens


def _strip_joiners(text: str) -> str:
    return "".join(ch for ch in text if ch not in JOINERS)


def squash(text: str) -> str:
    return "".join(_strip_joiners(t.text.lower()) for t in tokenize(text))


def norm_space(text: str) -> str:
    return " ".join(_strip_joiners(t.text.lower()) for t in tokenize(text))


def heard_key(text: str) -> str:
    return " ".join(text.lower().split())


def levenshtein(a: str, b: str) -> int:
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


# ─── Diff and candidates (LEARNING.md §3.2-3.3) ───


@dataclass(frozen=True)
class Candidate:
    heard: str
    meant: str
    kind: str  # casing | respacing | spelling | transliteration


@dataclass(frozen=True)
class Rejection:
    heard: str
    meant: str
    reason: str  # too_long | punctuation | inflection | rewording | composition | boundary


def _hunks(a: list[str], b: list[str]):
    n, m = len(a), len(b)
    table = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n - 1, -1, -1):
        for j in range(m - 1, -1, -1):
            table[i][j] = (
                table[i + 1][j + 1] + 1 if a[i] == b[j] else max(table[i + 1][j], table[i][j + 1])
            )
    hunks, dels, ins = [], [], []
    i = j = 0

    def flush():
        if dels or ins:
            hunks.append((list(dels), list(ins)))
            dels.clear()
            ins.clear()

    while i < n and j < m:
        if a[i] == b[j]:
            flush()
            i += 1
            j += 1
        elif table[i + 1][j] >= table[i][j + 1]:
            dels.append(i)
            i += 1
        else:
            ins.append(j)
            j += 1
    dels.extend(range(i, n))
    ins.extend(range(j, m))
    flush()
    return table[0][0], hunks


def _latin_count(tokens):
    return sum(1 for t in tokens if not t.cjk)


def _cjk_count(tokens):
    return sum(1 for t in tokens if t.cjk)


def classify(heard: str, meant: str, lexicon: Lexicon):
    ht, mt = tokenize(heard), tokenize(meant)
    if (
        _latin_count(ht) > MAX_LATIN_TOKENS
        or _latin_count(mt) > MAX_LATIN_TOKENS
        or _cjk_count(ht) > MAX_CJK_CHARS
        or _cjk_count(mt) > MAX_CJK_CHARS
    ):
        return Rejection(heard, meant, "too_long")
    if heard.lower() == meant.lower():
        return Candidate(heard, meant, "casing")
    if norm_space(heard) == norm_space(meant):
        return Rejection(heard, meant, "punctuation")
    sh, sm = squash(heard), squash(meant)
    if sh == sm:
        return Candidate(heard, meant, "respacing")
    shorter = ht if len(sh) <= len(sm) else mt
    if (sm in (sh + "s", sh + "es") or sh in (sm + "s", sm + "es")) and all(
        lexicon.is_ordinary(t.text) for t in shorter if not t.cjk
    ):
        return Rejection(heard, meant, "inflection")
    heard_cjk = any(t.cjk for t in ht)
    meant_cjk = any(t.cjk for t in mt)
    if heard_cjk != meant_cjk:
        if heard_cjk and _looks_like_composition(meant):
            return Rejection(heard, meant, "composition")
        return Candidate(heard, meant, "transliteration")
    if levenshtein(sh, sm) / max(len(sh), len(sm)) > MAX_DISTANCE_RATIO:
        # A distant ordinary-word substitution can still teach an unfamiliar
        # single word. The ordinary heard token remains ineligible as an alias.
        if (
            len(ht) == len(mt) == 1
            and not ht[0].cjk
            and not mt[0].cjk
            and lexicon.is_ordinary(ht[0].text)
            and is_distinctive(meant, lexicon)
        ):
            return Candidate(heard, meant, "spelling")
        return Rejection(heard, meant, "rewording")
    return Candidate(heard, meant, "spelling")


def _looks_like_composition(text: str) -> bool:
    """Lowercase ASCII letters, apostrophes and spaces only: what an input
    method shows (pinyin, romaji) before the user commits the characters."""
    return has_letter(text) and all(ch in "abcdefghijklmnopqrstuvwxyz' " for ch in text)


def extract(dictated: str, edited: str, lexicon: Lexicon):
    """Candidates and rejections for one dictation, in text order."""
    a, b = tokenize(dictated), tokenize(edited)
    if not a or len(a) > MAX_TEXT_TOKENS or len(b) > MAX_TEXT_TOKENS:
        return [], []
    matches, hunks = _hunks([t.text for t in a], [t.text for t in b])
    if len(a) >= REWRITE_MIN_TOKENS and matches * 2 < len(a):
        return [], []
    dcp, ecp = list(dictated), list(edited)
    candidates, rejections = [], []
    for dels, ins in hunks:
        if not dels or not ins:
            continue
        heard = "".join(dcp[a[dels[0]].start : a[dels[-1]].end])
        meant = "".join(ecp[b[ins[0]].start : b[ins[-1]].end])
        # Text typed straight after (or before) the span joins the last (first)
        # hunk: "Call trovio" -> "Call Trovioo now". A hunk that reaches an end
        # of both texts and grows there cannot be told from a continuation, so
        # it is refused rather than learned as "Trovioo now".
        grows = len(ins) > len(dels)
        at_end = dels[-1] == len(a) - 1 and ins[-1] == len(b) - 1
        at_start = dels[0] == 0 and ins[0] == 0
        result = Rejection(heard, meant, "boundary") if grows and (at_end or at_start) else classify(
            heard, meant, lexicon)
        target = candidates if isinstance(result, Candidate) else rejections
        if result not in target:
            target.append(result)
    return candidates, rejections


# ─── Admission (LEARNING.md §4.1-4.2) ───


def _has_internal_upper(text: str) -> bool:
    return any(ch.islower() for ch in text) and any(ch.isupper() for ch in text[1:])


def _mixes_digits_and_letters(text: str) -> bool:
    return any(ch.isdigit() for ch in text) and any(ch.isalpha() for ch in text)


def is_distinctive(text: str, lexicon: Lexicon) -> bool:
    latin = [t for t in tokenize(text) if not t.cjk]
    return any(
        _has_internal_upper(t.text) or _mixes_digits_and_letters(t.text) or not lexicon.is_ordinary(t.text)
        for t in latin
    )


def is_secret_shaped(text: str) -> bool:
    """Could ``text`` be a password or key typed over a dictated word?

    True when a token has at least 8 code points and at least 2 digits
    (``Sunshine42``, or the account number in ``Trovioo 44171234``), or when
    two non-CJK tokens are glued together, with no space between them, by a
    character other than ``' ’ - . _`` (``p@ss``, ``AT&T``). Such a word is not
    learned on its first sighting (LEARNING.md §4.1). Short tokens (``GPT4``,
    ``M4``, ``Hunter2``) are not secret-shaped.
    """
    tokens = tokenize(text)
    for t in tokens:
        if len(t.text) >= SECRET_MIN_LENGTH and sum(1 for ch in t.text if ch.isdigit()) >= SECRET_MIN_DIGITS:
            return True
    for left, right in zip(tokens, tokens[1:]):
        if left.cjk or right.cjk:
            continue
        gap = text[left.end : right.start]
        if not any(ch.isspace() for ch in gap) and any(ch not in JOINERS for ch in gap):
            return True
    return False


def contains_tokens(haystack: str, needle: str) -> bool:
    h = [t.text.lower() for t in tokenize(haystack)]
    n = [t.text.lower() for t in tokenize(needle)]
    if not n or len(n) > len(h):
        return False
    return any(h[i : i + len(n)] == n for i in range(len(h) - len(n) + 1))


def learns_word(c: Candidate, lexicon: Lexicon) -> bool:
    return is_distinctive(c.meant, lexicon)


def is_inflection_pair(a: str, b: str) -> bool:
    """Do the two texts differ, ignoring case, only by `s`, `es`, `ed` or `ing`
    added to the end of one (`ies`/`ied` for a final `y`)? (LEARNING.md §4.2)"""
    a, b = a.lower(), b.lower()
    short, long_ = (a, b) if len(a) <= len(b) else (b, a)
    if not short:
        return False
    if any(long_ == short + ending for ending in ("s", "es", "ed", "ing")):
        return True
    return short.endswith("y") and any(long_ == short[:-1] + ending for ending in ("ies", "ied"))


def learns_alias(c: Candidate, lexicon: Lexicon) -> bool:
    if c.kind == "casing" or len(heard_key(c.heard)) < 2 or contains_tokens(c.meant, c.heard):
        return False
    if is_inflection_pair(c.heard, c.meant):
        return False  # a plural or verb form is never a rewrite (LEARNING.md §4.2)
    if not has_letter(c.meant):
        return False  # a number is never a rewrite target (LEARNING.md §4.2)
    if c.kind == "respacing":
        return is_distinctive(c.meant, lexicon)
    tokens = tokenize(c.heard)
    if any(t.cjk for t in tokens):
        return True
    if len(tokens) == 1:
        return not lexicon.is_ordinary(tokens[0].text)
    return any(len(t.text) == 1 and t.text.isalpha() or not lexicon.is_ordinary(t.text) for t in tokens)


# ─── State (LEARNING.md §4.3-4.6) ───


def empty_state() -> dict:
    return {
        "schema": SCHEMA,
        "words": [],
        "aliases": [],
        "pending": [],
        "removed": {"words": [], "aliases": []},
    }


class Learning:
    def __init__(self, lexicon: Lexicon, state: dict | None = None):
        self.lexicon = lexicon
        self.state = copy.deepcopy(state) if state else empty_state()

    # ── lookups ──

    def _word(self, key):
        return next((w for w in self.state["words"] if w["text"].lower() == key), None)

    def _alias(self, heard):
        return next((a for a in self.state["aliases"] if a["heard"] == heard), None)

    def _pending(self, heard, meant):
        return next((p for p in self.state["pending"] if p["heard"] == heard and p["meant"] == meant), None)

    def _word_removed(self, key):
        return any(r["key"] == key for r in self.state["removed"]["words"])

    def _alias_removed(self, heard, meant):
        return any(r["heard"] == heard and r["meant"] == meant.lower() for r in self.state["removed"]["aliases"])

    def _unremove_word(self, key):
        self.state["removed"]["words"] = [r for r in self.state["removed"]["words"] if r["key"] != key]

    def _unremove_alias(self, heard, meant):
        self.state["removed"]["aliases"] = [
            r for r in self.state["removed"]["aliases"] if not (r["heard"] == heard and r["meant"] == meant.lower())
        ]

    def _tombstone_word(self, key, at):
        if not self._word_removed(key):
            self.state["removed"]["words"].append({"key": key, "at": at})

    def _tombstone_alias(self, heard, meant, at):
        if not self._alias_removed(heard, meant):
            self.state["removed"]["aliases"].append({"heard": heard, "meant": meant.lower(), "at": at})

    # ── operations ──

    def observe(self, dictation: str, dictated: str, edited: str, at: int, scene=None) -> dict:
        self._prune(at)
        candidates, _ = extract(dictated, edited, self.lexicon)
        result = adaptive_reference.revise(self.state, dictation, candidates, at, sys.modules[__name__], self.lexicon, scene=scene)
        self._prune(at)
        return result

    def teach(self, written: str, sounds_like: str | None, at: int) -> str:
        written = written.strip()
        sounds_like = (sounds_like or "").strip()
        if not written:
            return "empty"
        tokens = tokenize(written)
        if not tokens:
            return "empty"
        if (
            len(written) > MAX_TAUGHT_CHARS
            or _latin_count(tokens) > MAX_TAUGHT_LATIN_TOKENS
            or _cjk_count(tokens) > MAX_TAUGHT_CJK_CHARS
        ):
            return "too_long"
        self._prune(at)
        heard = heard_key(sounds_like) if sounds_like else None
        if heard is not None:
            if len(heard) < 2 or len(sounds_like) > MAX_TAUGHT_CHARS:
                return "too_long" if len(sounds_like) > MAX_TAUGHT_CHARS else "empty"
            if contains_tokens(written, sounds_like):
                return "self_containing"
        key = written.lower()
        word = self._word(key)
        taught = sum(1 for w in self.state["words"] if w["origin"] == "taught")
        if not (word and word["origin"] == "taught") and taught >= MAX_TAUGHT_WORDS:
            return "full"
        if heard is not None and not self._alias(heard) and len(self.state["aliases"]) >= MAX_ALIASES:
            if not any(a["origin"] == "correction" for a in self.state["aliases"]):
                return "full"
        self._unremove_word(key)
        if word:
            word.update(text=written, origin="taught", last_seen=at)
        else:
            self.state["words"].append({"text": written, "origin": "taught", "count": 0, "first_seen": at, "last_seen": at})
        if heard is not None:
            self._unremove_alias(heard, written)
            alias = self._alias(heard)
            if alias:
                self.state["aliases"].remove(alias)
            self.state["aliases"].append(
                {"heard": heard, "meant": written, "origin": "taught", "count": 0, "first_seen": at, "last_seen": at}
            )
            self.state["pending"] = [p for p in self.state["pending"] if p["heard"] != heard]
        self._prune(at)
        return "ok"

    def remove_word(self, text: str, at: int) -> None:
        key = text.strip().lower()
        self.state["words"] = [w for w in self.state["words"] if w["text"].lower() != key]
        self._tombstone_word(key, at)
        for alias in [a for a in self.state["aliases"] if a["meant"].lower() == key]:
            self.state["aliases"].remove(alias)
            self._tombstone_alias(alias["heard"], alias["meant"], at)
        self.state["pending"] = [p for p in self.state["pending"] if p["meant"].lower() != key]
        self._prune(at)

    def remove_alias(self, heard: str, meant: str, at: int) -> None:
        heard = heard_key(heard)
        self.state["aliases"] = [
            a for a in self.state["aliases"] if not (a["heard"] == heard and a["meant"].lower() == meant.lower())
        ]
        self._tombstone_alias(heard, meant, at)
        self.state["pending"] = [
            p for p in self.state["pending"] if not (p["heard"] == heard and p["meant"].lower() == meant.lower())
        ]
        self._prune(at)

    def clear(self) -> None:
        self.state = empty_state()

    # ── housekeeping ──

    def _prune(self, at: int) -> None:
        s = self.state
        s["pending"] = [p for p in s["pending"] if p["last_seen"] >= at - PENDING_TTL_MS]
        s["pending"].sort(key=lambda p: (-p["last_seen"], p["heard"], p["meant"]))
        del s["pending"][MAX_PENDING:]
        learned = sorted(
            (w for w in s["words"] if w["origin"] == "correction"), key=lambda w: (-w["last_seen"], w["text"].lower())
        )
        for word in learned[MAX_LEARNED_WORDS:]:
            s["words"].remove(word)
        while len(s["aliases"]) > MAX_ALIASES:
            evictable = [a for a in s["aliases"] if a["origin"] == "correction"]
            if not evictable:
                break
            s["aliases"].remove(min(evictable, key=lambda a: (a["last_seen"], a["heard"])))
        for kind in ("words", "aliases"):
            tombs = s["removed"][kind]
            tombs.sort(key=lambda r: -r["at"])
            del tombs[MAX_TOMBSTONES:]
        self._canonicalise()

    def _canonicalise(self) -> None:
        s = self.state
        s["words"].sort(key=lambda w: (-w["last_seen"], w["text"].lower()))
        s["aliases"].sort(key=lambda a: a["heard"])
        s["pending"].sort(key=lambda p: (p["heard"], p["meant"]))
        s["removed"]["words"].sort(key=lambda r: r["key"])
        s["removed"]["aliases"].sort(key=lambda r: (r["heard"], r["meant"]))

    # ── what the worker reads (LEARNING.md §6) ──

    def projection(self) -> dict:
        self._canonicalise()
        s = self.state
        return {
            "words": [w["text"] for w in s["words"]],
            "aliases": {a["heard"]: a["meant"] for a in s["aliases"]},
            "pending": [{"heard": p["heard"], "meant": p["meant"], "count": p["count"]} for p in s["pending"]],
            "removed_words": [r["key"] for r in s["removed"]["words"]],
            "removed_aliases": [[r["heard"], r["meant"]] for r in s["removed"]["aliases"]],
        }
