"""Display-path gate for the MUTABLE partial caption line.

The committed transcript is protected by the unified commit-text gate
(_CommitLog.gate); the PARTIAL line deliberately bypasses it (it must render
before commit evidence exists), and the field showed exactly the predicted
leak (live, 2026-08-28): during silence/noise windows the decoder
echoes the <|startofprev|> vocabulary prompt back as caption text — observed
verbatim: a product name, "NeMo" (a garbling of vocab term "Fable"), "the accelerator vendor",
"IO do force snapdragon" (a garbling of "CedarForge Snapdragon"). Worst at
session start, where the boot backlog decodes leading room noise.

Why the existing partial_line() checks missed it: _CommitLog.is_vocab_echo
demands >=90% of word pieces be SUBSTRINGS of the vocabulary — a garbled
echo ("force" vs "forge") falls under the bar, and there was no energy
condition at all.

This module is the pure decision core, wired at the ONE site that assigns
`self.partial` from a fresh decode (_ChannelStream._decode):

  * ENERGY HOLD — every decode window reports the RMS of its newest audio;
    a self-tracking noise floor (asymmetric EMA: falls fast toward quiet,
    rises slowly, absolute cap) classifies the window QUIET or SPEECH. A
    quiet window may not REPLACE the partial with new text — the previous
    partial holds. Works from the very first window (the floor starts at the
    first observation and the absolute SILENCE_RMS backstop is always live),
    which is the session-start case.
  * REGURGITATION — on quiet windows the vocab-echo test runs FUZZY
    (edit-distance-1 token matches count, threshold 0.6): garbled prompt
    echoes score high while real sentences that merely USE the terms keep
    plenty of non-vocab words. Suppression reason is reported separately so
    the log says what happened.
  * JUNK — hallucination.is_junk_utterance on the candidate text, any
    energy: a junk decode never replaces good text (partial_line would have
    blanked the line; holding the previous text is strictly better).

Cheap by construction: string splits + tiny edit-distance on <=20 tokens,
no extra decodes, no allocation beyond the token list.
"""
from __future__ import annotations

import re

# Absolute silence backstop — same constant family as audio_energy.SILENCE_RMS
# (kept literal here so this module stays dependency-free for the C-side
# import order; the wiring asserts they agree).
ABS_SILENCE_RMS = 0.004

#: A window must exceed the tracked noise floor by this factor to count as
#: speech. Loopback/system channels idle at a nonzero hiss floor ("the
#: loopback noise floor hides pauses", 2026-08-27) — a fixed threshold can't
#: serve both a silent mic and a hissy loopback, hence the tracked floor.
NOISE_FLOOR_MULT = 2.5

#: The tracked floor never exceeds this, so the quiet threshold is capped at
#: max(ABS_SILENCE_RMS, MULT*CAP) = 0.02 RMS — genuinely audible speech can
#: never be classified quiet even on a pathologically noisy channel.
NOISE_FLOOR_CAP = 0.008

#: Fuzzy vocab-echo coverage at/above which a QUIET window's text is treated
#: as prompt regurgitation. 0.6, not 0.9: garbled echoes lose exact matches
#: ("force"/"forge") but still consist mostly of near-vocab tokens.
FUZZY_ECHO_COVERAGE = 0.6

_SPLIT = re.compile(r"[\W_]+")


def _edit1(a: str, b: str) -> bool:
    """True when edit distance(a, b) <= 1. Early-exit, no DP table."""
    la, lb = len(a), len(b)
    if abs(la - lb) > 1:
        return False
    if la > lb:
        a, b, la, lb = b, a, lb, la
    # la <= lb, lb - la in {0, 1}
    i = j = 0
    edits = 0
    while i < la and j < lb:
        if a[i] == b[j]:
            i += 1
            j += 1
            continue
        edits += 1
        if edits > 1:
            return False
        if la == lb:
            i += 1
            j += 1          # substitution
        else:
            j += 1          # deletion from the longer string
    edits += (lb - j) + (la - i)
    return edits <= 1


class PartialDisplayGate:
    """Per-channel state: the tracked noise floor + the vocab token set."""

    def __init__(self, bias_phrases=None):
        phrases = [p for p in (bias_phrases or ()) if p and str(p).strip()]
        self.vocab_norm = "".join(
            ch for ch in ", ".join(str(p) for p in phrases).lower()
            if ch.isalnum())
        self.tokens = sorted({
            t for p in phrases for t in _SPLIT.split(str(p).lower()) if len(t) >= 2})
        self._noise_floor: float | None = None

    # -- energy -----------------------------------------------------------
    def note_window_rms(self, rms: float) -> bool:
        """Feed one decode window's newest-audio RMS; returns True when the
        window is QUIET (no speech-RMS above the established floor)."""
        rms = max(float(rms), 0.0)
        if self._noise_floor is None:
            self._noise_floor = min(rms, NOISE_FLOOR_CAP)
        elif rms < self._noise_floor:
            # fall fast: one quiet window pulls the floor down
            self._noise_floor += (rms - self._noise_floor) * 0.5
        else:
            # rise slowly: speech must not drag the floor up after itself
            self._noise_floor += min(rms - self._noise_floor,
                                     NOISE_FLOOR_CAP) * 0.02
        self._noise_floor = min(self._noise_floor, NOISE_FLOOR_CAP)
        threshold = max(ABS_SILENCE_RMS, NOISE_FLOOR_MULT * self._noise_floor)
        return rms < threshold

    # -- regurgitation ----------------------------------------------------
    def echo_score(self, text: str) -> float:
        """Fraction of the text's word pieces that are (near-)vocabulary:
        exact substring of the joined vocab (the is_vocab_echo rule) OR
        edit-distance-1 from any single vocab token (the garble case)."""
        if not self.vocab_norm:
            return 0.0
        pieces = [w for w in _SPLIT.split(text.lower()) if w]
        if not pieces:
            return 0.0
        covered = 0
        for w in pieces:
            if len(w) >= 2 and w in self.vocab_norm:
                covered += 1
            elif any(_edit1(w, t) for t in self.tokens):
                covered += 1
            elif len(w) >= 4 and self._near_substring(w):
                # garble of a token FRAGMENT ("force" for the "forge" inside
                # CedarForge): one edit away from some window of the vocab
                # string. Length >=4 keeps short words from matching noise.
                covered += 1
        return covered / len(pieces)

    def _near_substring(self, w: str) -> bool:
        norm = self.vocab_norm
        for width in (len(w) - 1, len(w), len(w) + 1):
            if width < 3 or width > len(norm):
                continue
            for i in range(len(norm) - width + 1):
                if _edit1(w, norm[i:i + width]):
                    return True
        return False

    # -- the decision -----------------------------------------------------
    def decide(self, prev_partial: str, new_text: str, quiet: bool,
               is_junk=None) -> tuple[str, str | None]:
        """(text_the_partial_should_become, suppression_reason|None).

        Reasons: "junk" (junk-phrase decode, any energy), "regurgitation"
        (quiet window + fuzzy vocab echo), "energy-hold" (quiet window +
        any other NEW text). None = take new_text (or no-op)."""
        if new_text == prev_partial:
            return prev_partial, None
        if not new_text:
            # empty decode never clears the display here; the anti-shrink
            # rule at the call site owns that case.
            return prev_partial, None
        if is_junk is not None and is_junk(new_text):
            return prev_partial, "junk"
        if quiet:
            if self.echo_score(new_text) >= FUZZY_ECHO_COVERAGE:
                return prev_partial, "regurgitation"
            return prev_partial, "energy-hold"
        return new_text, None
