"""Phonetic-match constraint for a source-only correction utility.

The guard allows a substitution only when a candidate is within a phonetic
edit-distance radius of a known lexicon entry. A candidate outside that radius
returns ``None`` so callers can leave the transcript unchanged. The module is
Python code that loads g2p_en's pronunciation model lazily. Its NLTK data
may require a first-use download when it has not been provisioned.

g2p_en dependency notes: the optional test environment needs its declared
Python dependencies and small NLTK corpora on first use. Keep those resources
isolated; the product package does not bundle them.

max_distance default: see find_correction().
"""

from __future__ import annotations

import re
from functools import lru_cache

# g2p_en emits ARPABET phoneme tokens (e.g. "AE1", "NG", "K") interleaved with
# separator tokens for spaces and punctuation (" ", "'", ...). Only the
# phoneme tokens are actual sound units, so those separators are dropped
# before comparison -- keeping them would penalize two phrases that differ
# only in spacing/punctuation as if they sounded different.
_PHONEME_RE = re.compile(r"^[A-Z]+[0-9]?$")

_g2p_model = None  # lazy singleton: loading G2p() pulls in the CMU dict + tagger


def _model():
    global _g2p_model
    if _g2p_model is None:
        from g2p_en import G2p  # imported lazily so importing this module never

        _g2p_model = G2p()
    return _g2p_model


@lru_cache(maxsize=4096)
def _g2p_cached(text: str) -> tuple[str, ...]:
    # Lexicon entries get re-g2p'd against every candidate; cache keeps
    # find_correction() over an N-entry lexicon from redoing the same neural
    # G2P call N times per candidate.
    return tuple(p for p in _model()(text) if _PHONEME_RE.match(p))


def g2p(text: str) -> list[str]:
    """ARPABET phoneme sequence for a word or short phrase."""
    return list(_g2p_cached(text))


def _levenshtein(a: tuple[str, ...], b: tuple[str, ...]) -> int:
    """Edit distance between two phoneme sequences without a backtrace."""
    n, m = len(a), len(b)
    cost = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(1, n + 1):
        cost[i][0] = i
    for j in range(1, m + 1):
        cost[0][j] = j
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            if a[i - 1] == b[j - 1]:
                cost[i][j] = cost[i - 1][j - 1]
            else:
                cost[i][j] = 1 + min(
                    cost[i - 1][j - 1],  # sub
                    cost[i - 1][j],      # del
                    cost[i][j - 1],      # ins
                )
    return cost[n][m]


def phonetic_distance(a: str, b: str) -> int:
    """Edit distance between the ARPABET phoneme sequences of `a` and `b`."""
    return _levenshtein(_g2p_cached(a), _g2p_cached(b))


#: Default phonetic radius for a correction.
#:
#: The default is intentionally conservative. Callers with an evaluated
#: vocabulary may pass a wider radius explicitly.
DEFAULT_MAX_DISTANCE = 1


def find_correction(candidate: str, lexicon: list[str],
                    max_distance: int = DEFAULT_MAX_DISTANCE) -> str | None:
    """Closest lexicon entry to `candidate` by phonetic distance, or None.

    This is the anti-hallucination guard for L3: it must default to "no
    correction" rather than guessing when nothing in the lexicon is close
    enough, so `max_distance` is a hard cutoff, not a soft preference.

    See DEFAULT_MAX_DISTANCE for why the default is 1 and what a looser
    setting costs.

    Projects should evaluate their own vocabulary and spoken forms before
    choosing a wider radius.
    """
    best: str | None = None
    best_dist: int | None = None
    for entry in lexicon:
        d = phonetic_distance(candidate, entry)
        if best_dist is None or d < best_dist:
            best_dist, best = d, entry
    if best_dist is not None and best_dist <= max_distance:
        return best
    return None


_WORD_RE = re.compile(r"\S+")


def scan_for_corrections(
    text: str,
    lexicon: list[str],
    max_distance: int = DEFAULT_MAX_DISTANCE,
    max_window: int = 6,
) -> list[dict]:
    """Finds candidate corrections in free text by sliding a word-count
    window across it and phonetically matching each span against the
    lexicon -- this scans free text on top of find_correction()'s verification
    of an already-isolated candidate.
    max_window=6: name-type mishearings can expand a single lexicon entry
    into several words, so the window is wider than the lexicon entries'
    own word counts, not matched to them. Known gap, not fixed by window
    size alone: structured/technical-notation entries ("code-x2.3.20" spoken
    as "code x two three twenty") aren't caught even at max_window=6 --
    g2p_en's guessed pronunciation of the compact written form may not match
    how a person says it aloud. That limitation is kept explicit instead of
    silently worked around by loosening max_distance.

    Returns non-overlapping matches only, as
    `[{"start": word_idx, "end": word_idx, "original": str,
       "correction": str, "distance": int}, ...]` (word indices are
    inclusive-exclusive, like a slice).

    Scans left to right, and at each uncovered position tries the *longest*
    window first, down to length 1 -- not globally-lowest-distance, which
    was tried first and had a real bug: a two-word span can score higher than
    its one-word prefix against the same target. Sorting candidates by distance
    globally picked the shorter match and left a trailing word uncorrected.
    Longest-first avoids this: a longer window that still clears max_distance
    is preferred over a shorter, tighter one, since it explains more of the
    input. Over-extension into unrelated
    neighboring words is naturally self-limiting -- each extra word adds
    its own phonemes as pure insertions against a fixed-length lexicon
    target, which costs real edit-distance and usually exceeds
    max_distance on its own.
    """
    words = _WORD_RE.findall(text)
    accepted: list[dict] = []
    start = 0
    while start < len(words):
        matched = False
        for length in range(min(max_window, len(words) - start), 0, -1):
            end = start + length
            span = " ".join(words[start:end])
            correction = find_correction(span, lexicon, max_distance)
            if correction is None:
                continue
            accepted.append(
                {
                    "start": start,
                    "end": end,
                    "original": span,
                    "correction": correction,
                    "distance": phonetic_distance(span, correction),
                }
            )
            start = end
            matched = True
            break
        if not matched:
            start += 1
    return accepted


def corrector_lexicon(
    vocabulary: list[str],
    budget_per_10k: float = 0.2,
    max_distance: int = DEFAULT_MAX_DISTANCE,
) -> tuple[list[str], list[dict]]:
    """Split a bias vocabulary into terms safe to correct and terms that are not.

    A term can be useful for beam biasing yet unsafe for post-processing. The
    caller supplies the vocabulary and budget policy; this function reports
    which entries pass that policy.

    Returns `(admitted, refused)`; each refusal carries the words it would eat,
    so a caller can tell the user why a term does not autocorrect rather than
    dropping it silently.

    Raises `ImportError` if the frequency data is unavailable. That is
    deliberate: correcting against an unscreened lexicon is measurably worse
    than not correcting at all, so there is no safe degraded mode to fall back
    to.
    """
    from wordfreq import zipf_frequency  # noqa: F401  (hard requirement, see docstring)

    try:
        from nltk.corpus import cmudict

        reference = cmudict.dict()
    except LookupError:  # pragma: no cover - first run on a fresh machine
        import nltk

        nltk.download("cmudict", quiet=True)
        from nltk.corpus import cmudict

        reference = cmudict.dict()

    scored: list[dict] = []
    for term in vocabulary:
        phonemes = _g2p_cached(term)
        if not phonemes:
            scored.append({"term": term, "rate_per_10k": float("inf"),
                           "reason": "no pronunciation", "collisions": []})
            continue
        lowered = term.lower()
        collisions: list[dict] = []
        for word, pronunciations in reference.items():
            if word == lowered:
                continue
            for pronunciation in pronunciations:
                # |len(a)-len(b)| lower-bounds edit distance, so this prunes the
                # ~123k-entry scan to candidates of plausible length.
                if abs(len(pronunciation) - len(phonemes)) > max_distance:
                    continue
                if _levenshtein(phonemes, tuple(pronunciation)) <= max_distance:
                    zipf = zipf_frequency(word, "en")
                    collisions.append({
                        "word": word,
                        "rate_per_10k": 10.0 ** (zipf - 5.0) if zipf > 0 else 0.0,
                    })
                    break
        collisions.sort(key=lambda c: -c["rate_per_10k"])
        scored.append({
            "term": term,
            "rate_per_10k": sum(c["rate_per_10k"] for c in collisions),
            "reason": ("is itself an ordinary English word" if lowered in reference
                       else "sounds like ordinary English" if collisions
                       else "no ordinary word sounds like it"),
            "collisions": collisions[:6],
        })

    scored.sort(key=lambda s: s["rate_per_10k"])
    admitted: list[str] = []
    refused: list[dict] = []
    spent = 0.0
    for entry in scored:
        if spent + entry["rate_per_10k"] <= budget_per_10k:
            spent += entry["rate_per_10k"]
            admitted.append(entry["term"])
        else:
            refused.append(entry)
    return admitted, refused
