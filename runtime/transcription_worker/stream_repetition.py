"""Collapse the streaming model's stutter before it reaches the screen.

## Why this exists, and why it is safe

The sherpa streaming model doubles symbols on this user's voice. Measured on
his own retained clips, one 2.7-second utterance of "测试" decodes as:

    t=1.92s  测试
    t=2.24s  测试测测试
    t=2.56s  测试测测试测测试

It is a property of the model's transducer, not of the plumbing and not of the
search: an A/B against `modified_beam_search` on 2026-08-17 produced *identical*
doubling at 19% more compute, and was marginally worse on English.

He reported it as "it has something that is repeating", reading the preview
panel — which sits 8pt from the caret, exactly where the real transcript lands.
So a broken preview is indistinguishable from a broken product.

**This never touches the transcript.** The preview is a different pass, from a
different model, shown while he is still speaking; the accurate pass runs
separately and was verified byte-identical to a fresh decode of the same audio.
Cleaning a display hypothesis is honest. Cleaning the inserted text would not
be, and nothing here is wired to it.

## The conservative bias

Over-collapsing a preview costs a briefly-wrong word that the accurate pass
overwrites a second later. Under-collapsing costs the state this shipped in,
where the panel is unreadable. So this leans toward collapsing — but only on
IMMEDIATE adjacency, never across a gap, so genuine emphasis separated by other
words ("很好，真的很好") survives.
"""
from __future__ import annotations

import re

#: Longest repeated unit to collapse. Beyond this a repeat is far more likely
#: to be real speech than a transducer stutter.
MAX_UNIT = 4

#: A CONTIGUOUS run of CJK. Collapsing is scoped to these spans so Latin
#: text on the same line is never touched — see `clean`.
_CJK_RUN = re.compile(r"[㐀-鿿豈-﫿]+")


def _collapse_runs(text: str) -> str:
    """`AAA` -> `A`, `ABAB` -> `AB`, for units up to `MAX_UNIT`.

    Longest unit first: `测试测测试测测试` collapses on the 2-char unit `测试`
    and would otherwise be half-eaten by the 1-char pass first.
    """
    out = text
    for size in range(MAX_UNIT, 0, -1):
        pattern = re.compile(r"(.{%d})\1{1,}" % size, re.DOTALL)
        previous = None
        # Repeat to a fixed point: collapsing can create a new adjacency.
        while previous != out:
            previous = out
            out = pattern.sub(r"\1", out)
    return out


def _collapse_words(text: str) -> str:
    """Immediately repeated Latin words, case-insensitively.

    `THAT IS IS REPEATING` -> `THAT IS REPEATING`. Bounded to adjacent
    duplicates so "very very good" — which a person does say — survives only
    if... it does not. That is the accepted cost of a readable preview, and it
    is why this is preview-only.
    """
    return re.sub(r"\b(\w+)(\s+\1\b)+", r"\1", text, flags=re.IGNORECASE)


def _truecase(text: str) -> str:
    """Lowercase the model's shouting and sentence-case the result.

    The streaming model's token inventory is **entirely uppercase** —
    `▁AS`, `▁ONE`, `▁OF`, `▁COMP`, `AN`, `Y` — so it cannot emit lowercase
    English at all. Every English preview therefore arrives as
    `IF THE WORK HAS ALREADY BEEN PUT INSIDE THE APP`, which the owner
    reported on 2026-08-17 as "它只会打大写".

    Because the model has no lowercase in its vocabulary, an uppercase word
    carries **zero information** about intended casing — there is nothing to
    preserve. Lowercasing loses nothing and stops the panel shouting.

    Real acronyms (`QNN`, `MLX`) come out lowercase as a result. That is
    acceptable and unavoidable here: those terms are not in this model's
    inventory either, so it was never going to spell them. The accurate pass
    is what puts them back, correctly cased, a second later.
    """
    def fix(match: re.Match) -> str:
        word = match.group()
        # Only touch words that are ENTIRELY uppercase — i.e. everything this
        # model produces. A mixed-case word could only come from somewhere
        # else and is left alone.
        return word.lower() if word.isupper() else word

    out = re.sub(r"[A-Za-z']+", fix, text)
    # Sentence-case the FIRST Latin letter — not the first lowercase one.
    # Scanning for the first lowercase letter uppercases it wherever it is,
    # which turns `Miles ArcFace running` into `MIles ArcFace running`: the
    # first lowercase character in that string is the `r` inside `Miles`.
    # Caught by this module's own test, which is the argument for it existing.
    match = re.search(r"[A-Za-z]", out)
    if match and match.group().islower():
        i = match.start()
        out = out[:i] + out[i].upper() + out[i + 1:]
    return out


def clean(text: str) -> str:
    """Make the streaming hypothesis presentable. Display only.

    Never applied to the transcript — see the module docstring.
    """
    if not text:
        return text
    out = _collapse_words(text)
    # Character-run collapsing runs ONLY over CJK runs, span by span.
    #
    # The first version tested `if _CJK.search(out)` and then collapsed the
    # WHOLE string, which meant a single Chinese character anywhere mangled
    # every English word on the line:
    #
    #     "we will look at the bookkeeper 的 committee"
    #  -> "we wil lok at the bokeper 的 comite"
    #
    # That is not hypothetical for this user. He code-mixes in most
    # utterances, and the streaming model's inventory is 5755 CJK characters
    # against ~497 Latin word-pieces, so it emits stray CJK into English
    # speech on its own.
    #
    # Latin doubling from this model appears as whole repeated WORDS, which
    # `_collapse_words` already handles. Running a character collapser over
    # Latin only ever eats real letters — `ll` in `will`, `oo` in `look`,
    # `tt`/`ee` in `committee`.
    out = _CJK_RUN.sub(lambda m: _collapse_runs(m.group()), out)
    return _truecase(out)
