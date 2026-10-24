"""Restore a vocabulary term's declared spelling when only its case is wrong.

## The gap this closes, which no metric either side tracks

Internal research finding 39 measured criterion 7 for the first time, through
`transcribe_with_trie` on 63 clips:

    case-folded recovery   92.06%
    exact display form     82.54%

**A 9.5-point gap.** Five terms account for it:

    Soliton       -> soliton        QuestPDF   -> questpdf
    CedarStack -> cedarstack  Jossie Lin -> jossie lin
    SelfBlended   -> selfblended

Every one of those scores as a *hit* for recovery — the decoder found the
word — and every one is a hand-fix for the user, which is the metric that
actually matters. So the programme's own headline number is blind to the
defect by construction.

Finding 38 adds a second, independent source with the same signature:
`ct-punc` punctuation restoration applies sentence-case and destroys
camelCase unconditionally — `ElmTree` -> `Elmtree`, `iTerm2` -> `Iterm2`,
`QNN` -> `Qnn`, 10 of 20 terms, identically in English and Chinese. Every
corrupted term has an internal capital; every survivor does not.

Both are the same defect at the output: *the emitted spelling differs from the
declared one.* So one stage fixes both, and it fixes them regardless of which
upstream stage caused it — which matters, because punctuation restoration is
not shipped yet and this must not have to be revisited when it is.

## Why it is safe

This is `spoken_aliases`' operation with a different key: observed form ->
declared form. Priced the same way, on 1.2M words of ordinary English against
33 declared terms:

    Whisper          6 hits   0.0500 per 10k   -> EXCLUDED
    a product name   1 hit    0.0083 per 10k   -> kept
    other 31         0 hits   0.0000           -> free

`whisper` is an ordinary English word and is excluded outright. The product
name is one hit in 1.2 million words — and it is this user's own product,
said constantly and almost always meaning the product, so excluding it would
cost far more than the 1-in-1.2M it saves. Research recommended a two-term
list; its own table judged that name negligible and only `Whisper`
excludable, and the table is the measurement.

## The second half: spacing, not just case

Case-only was the whole of this stage until the persona packs were measured on
the NPU (H13, 2026-08-23). Nineteen term instances still came out wrong WITH
the pack loaded, and the largest single group was neither a casing error nor a
recognition error — it was **spacing**:

    test bench   -> testbench       always comb  -> always_comb
    iCE40 up 5k  -> iCE40 UP5K      synchronizer -> synchroniser

Every one of those is the declared term's own letters, in the declared order,
written with a space where the declaration has none (or none where the
declaration has one). The decoder heard the term correctly; only the
orthography differs. `the casing contract` in both persona packs already promised that
"T + C" -- the vocabulary line plus declared casing -- covers exactly these
("test bench", "always comb", "meta stability", "net list", "open lane",
"service now", "tape out", "sigma delta", "set property"). It did not: this
pass compared case-folded strings, and a space is not a case difference. The
second half makes the documented mechanism real.

## Why the alias table cannot do it, and why this is not the alias table

A shipped-alias bar refuses a multi-word spoken form whose every token is an
ordinary English word
-- `test bench`, `work day`, `service now` -- because a SHIPPED rewrite spends
its false-positive budget on every install, for someone who never asked (P6a
F3). That rule is correct, and it is why this half lives in this module and not
in the alias table.

This pass is keyed on something else entirely: **a term the user declared in
their own vocabulary, sent with this very utterance**. Nothing is shipped and
nothing is guessed. The cost is the one the declarer chose to spend -- the
distinction that bar draws: a form the user typed costs them what they
chose to spend, and a form the product SHIPS spends it on every install, for
a person who never asked.

It is still strictly more reach than case-folding, and the honest statement of
the cost is: a declared term whose SPACED spelling is ordinary English prose
gets joined in prose too. `stand-up` would re-hyphenate "stand up"; `Workday`
would capitalise "work day". Those are named in `DEFAULT_JOIN_EXCLUSIONS`
rather than left to be discovered, and both exclusion sets are caller-
overridable. Three structural guards keep the rest small, with no dictionary
at runtime:

* the collapsed declared form must be at least `MIN_JOIN_KEY` characters --
  `P&L` (`pl`), `GA`, `HR`, `IT`, `I2C`, `SSO` are below it, and short keys are
  exactly the ones that collide with ordinary two- and three-letter strings;
* a span is at most `MAX_JOIN_TOKENS` words: longer than that is a
  rephrase, not a term;
* the only characters allowed BETWEEN the words of a span are spaces, hyphens
  and underscores, so a join can never cross a comma, a full stop or a newline.

## Locale spellings

`synchroniser` was declared and `synchronizer` was decoded. That is neither a
casing nor a spacing difference, and it is not a recognition error: it is the
same word in the other locale, which no vocabulary line can fix by being
spelled more carefully. The comparison key folds the `-ise/-ize` family (and
only that family) to one canon, so a declared British spelling is restored from
an American decode and the reverse. The replacement is ALWAYS the declared
string, so an over-eager fold can only make two spellings of one word match --
it can never invent a third.
"""

from __future__ import annotations

import re
from functools import lru_cache

#: Declared terms that are also ordinary English words. Re-casing these would
#: rewrite prose the user actually meant. Compared case-insensitively.
DEFAULT_EXCLUSIONS = frozenset({"whisper"})

#: Declared terms whose COLLAPSED form (separators removed, lower-cased) reads
#: as ordinary English once a space is put back, so the respacing half must not
#: fire on them even though the casing half may. Named rather than discovered:
#: `Workday`/`work day`, `Snowflake`/`snow flake`, `stand-up`/`stand up`,
#: `readout`/`read out`, `setup slack`/`set up Slack` (the last one costs an
#: enterprise user their chat app). Every one of these already reaches its
#: declared spelling through the casing half or through the decode itself, so
#: excluding them costs nothing measurable and buys back the prose.
DEFAULT_JOIN_EXCLUSIONS = frozenset(
    {"workday", "snowflake", "standup", "readout", "setupslack"})

#: Collapsed declared forms shorter than this are never respaced. `P&L` -> `pl`,
#: `GA`, `HR`, `IT`, `SSO`, `I2C`, `LUT`: at two and three characters the key
#: stops identifying a term and starts colliding with ordinary text.
MIN_JOIN_KEY = 4

#: Words in one respaced span: a longer span is a rephrase, not a term.
MAX_JOIN_TOKENS = 4

#: A run of word characters. Deliberately Latin-only and deliberately without
#: `_`: an underscore is a SEPARATOR a declared term may spell (`always_ff`),
#: so it belongs in the gap alphabet below, not inside a token.
_TOKEN = re.compile(r"[0-9A-Za-z]+")

#: What may sit between two words of one span. Spaces, hyphens and underscores
#: only -- never a comma, a full stop or a newline, or a join could swallow a
#: clause boundary. Two characters at most (" -" in "AXI -Lite").
_GAP = re.compile(r"[ 	\-_]{1,2}")

#: The Latin-alphanumeric boundary, as a set rather than `\w`, for the reason
#: `_boundary_pattern` documents: CJK characters ARE `\w`.
_LATIN = re.compile(r"[A-Za-z0-9_]")

#: American -> British, longest suffix first. Used ONLY to make two spellings of
#: one word compare equal; the text that gets written is always the declared
#: form, so this can never produce a spelling nobody declared.
_LOCALE_FOLDS = (
    ("izations", "isations"), ("ization", "isation"),
    ("izers", "isers"), ("izing", "ising"),
    ("izer", "iser"), ("ized", "ised"), ("izes", "ises"),
    ("ize", "ise"),
)


def _key(text: str) -> str:
    """Comparison key: separators dropped, case folded, locale folded.

    `always_ff`, `always FF` and `Always-ff` all key to `alwaysff`;
    `synchronizer` and `synchroniser` both key to `synchroniser`.
    """
    key = "".join(c for c in text.lower() if c.isascii() and c.isalnum())
    for american, british in _LOCALE_FOLDS:
        if key.endswith(american):
            return key[: -len(american)] + british
    return key


def _boundary_pattern(terms: list[str]) -> re.Pattern | None:
    """One case-insensitive alternation, longest first.

    Longest-first matters: with both `Visual` and `Visual Studio` declared,
    a shortest-first alternation would rewrite the `Visual` inside
    `visual studio` and leave `studio` lowercase.

    The boundary is Latin-alphanumeric rather than `\\w` for the reason
    `spoken_aliases` learned the hard way: CJK characters ARE `\\w` under
    Python's Unicode `re`, so a `\\w` boundary can never match a Latin term
    sitting against Chinese — which is exactly where 86% of this user's
    vocabulary occurrences live.
    """
    if not terms:
        return None
    ordered = sorted(terms, key=len, reverse=True)
    return re.compile(r"(?<![A-Za-z0-9_])(" + "|".join(re.escape(t) for t in ordered)
                      + r")(?![A-Za-z0-9_])", re.IGNORECASE)


def _join_map(terms: list[str],
              join_exclusions: frozenset[str]) -> dict[str, str]:
    """`{comparison key: declared form}` for the terms the respacing half may
    touch. The longest declared form wins a key collision, for the reason
    `_boundary_pattern` sorts longest-first: `Visual Studio` must not lose to
    `Visual`."""
    out: dict[str, str] = {}
    for term in sorted(terms, key=len):
        key = _key(term)
        if len(key) < MIN_JOIN_KEY or key in join_exclusions:
            continue
        out[key] = term
    return out


@lru_cache(maxsize=32)
def _vocabulary_plan(
    vocabulary: tuple[str, ...],
    exclusions: frozenset[str],
    join_exclusions: frozenset[str],
    case_only: frozenset[str] = frozenset(),
) -> tuple[tuple[str, ...], dict[str, str], dict[str, str]]:
    """Compile the read-only maps shared by successive utterances.

    A vocabulary changes rarely compared with the number of utterances that
    use it. Re-sorting and re-keying every declared term on every call made a
    1,000-term profile spend more than half of its casing budget rebuilding
    identical dictionaries. A tuple key observes in-place list changes on the
    next call, and the small bound prevents old personalized vocabularies from
    accumulating for the lifetime of the worker.
    """
    eligible = tuple(t for t in vocabulary if t.lower() not in exclusions)
    declared = {t.lower(): t for t in eligible}
    join_map = _join_map([t for t in eligible if t.lower() not in case_only], join_exclusions)
    return eligible, declared, join_map


def _respace(text: str, join_map: dict[str, str]) -> tuple[str, list[str]]:
    """Restore the declared spelling of a span whose LETTERS are already right.

    Left to right, longest span first, non-overlapping. A span that already
    reads as the declared form is consumed without being reported, so it can
    never be re-matched at a shorter length ("always_ff" must not fall back to
    "always").
    """
    if not join_map:
        return text, []
    tokens = [(m.start(), m.end()) for m in _TOKEN.finditer(text)]
    pieces: list[str] = []
    restored: list[str] = []
    cut = 0
    i = 0
    while i < len(tokens):
        step = 1
        for j in range(min(i + MAX_JOIN_TOKENS, len(tokens)), i, -1):
            if not all(_GAP.fullmatch(text[tokens[k][1]:tokens[k + 1][0]])
                       for k in range(i, j - 1)):
                continue
            start, end = tokens[i][0], tokens[j - 1][1]
            if start and _LATIN.match(text[start - 1]):
                break            # inside a longer word: no span here can start
            if end < len(text) and _LATIN.match(text[end]):
                continue
            span = text[start:end]
            want = join_map.get(_key(span))
            if want is None:
                continue
            if want != span:
                pieces.append(text[cut:start])
                pieces.append(want)
                restored.append(want)
                cut = end
            step = j - i
            break
        i += step
    pieces.append(text[cut:])
    return "".join(pieces), restored


def apply(text: str, vocabulary: list[str], *,
          exclusions: frozenset[str] = DEFAULT_EXCLUSIONS,
          join_exclusions: frozenset[str] = DEFAULT_JOIN_EXCLUSIONS,
          case_only=(),
          pattern=None) -> tuple[str, list[str]]:
    """Rewrite mismatches of case, spacing or locale spelling to the declared
    form.

    Returns `(text, restored)`. A match that ALREADY has the declared spelling
    is not counted — so `restored` is the list of terms this stage actually
    fixed, never a tally of terms that happened to be present.

    Only the ORTHOGRAPHY of a term is ever changed: its case, the separators
    between its own letters, and the `-ise/-ize` fold. If a match differs from
    the declared term by anything else it is left alone — that is a recognition
    error, not a spelling one, and rewriting it here would be an unverified
    substitution of the kind `spoken_aliases` is deliberately narrow about.
    `Trovio` stays `Trovio` next to a declared `Trovioo`.

    `case_only` names vocabulary terms (compared case-insensitively) that
    restore CASE and nothing else: they never enter the respacing half, so
    neither their spaced form nor their other-locale spelling is rewritten.
    The worker puts every word the host learned from a single correction
    here (LEARNING.md §4.1, §4.2). The respacing half is justified only by a
    term the user declared, and one fix of `restart the service now` to
    `ServiceNow` is not a declaration that every later "service now" in prose
    means the product. Its spaced form becomes a rewrite through the alias
    table once the same fix is seen on two dictations.

    The two halves run in order and the order matters. Case first, on the
    single-token boundary pattern the caller may pre-compile; then spacing,
    which needs no pattern because its unit is a span of up to
    `MAX_JOIN_TOKENS` words rather than one alternation branch. Running spacing
    first would leave the casing half nothing to do on the joined result, which
    is harmless, but it would also make the pre-compiled `pattern` argument a
    lie about what ran.
    """
    if not text or not vocabulary:
        return text, []
    eligible, declared, join_map = _vocabulary_plan(
        tuple(vocabulary), frozenset(exclusions), frozenset(join_exclusions),
        frozenset(t.lower() for t in case_only))
    pattern = pattern or _boundary_pattern(list(eligible))
    if pattern is None:
        return text, []

    restored: list[str] = []

    def sub(m: re.Match) -> str:
        found = m.group(1)
        want = declared.get(found.lower())
        if want is None or want == found:
            return found
        restored.append(want)
        return want

    text = pattern.sub(sub, text)
    text, respaced = _respace(text, join_map)
    return text, restored + respaced
