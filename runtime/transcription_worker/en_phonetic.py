"""Latin-script phonetic vocabulary recovery — the EN counterpart of
zh_phonetic, built on phonetic_match (the dictation line's L3
lexicon layer, already in this worker).

whisper renders unfamiliar Latin terms as similar-sounding common words
("Trilio" for Trovioo, "fable flow" for Fable Flow). phonetic_match
slides a word window over the hypothesis, compares ARPAbet phoneme
sequences (g2p_en), and proposes the lexicon entry when the phonetic
edit distance is tiny; the rewrite itself is a deterministic whole-word
substitution in apply() (no LLM anywhere).

Scope guards mirror zh_phonetic's: candidates come ONLY from the user's
vocabulary; CJK-containing entries are zh_phonetic's job; text with no
Latin letters is skipped outright. Fail-soft: g2p_en/nltk missing ->
no-op (first use also downloads two small NLTK corpora, so the model
loads lazily off the caption path's hot loop).
"""
from __future__ import annotations

import re

_READY: bool | None = None


def _ready() -> bool:
    global _READY
    if _READY is None:
        try:
            from . import phonetic_match  # noqa: F401 — probes g2p_en
            phonetic_match.g2p("ok")
            _READY = True
        except Exception:  # noqa: BLE001 — enhancement layer
            _READY = False
    return _READY


_LATIN = re.compile(r"[A-Za-z]")


class VocabPhoneticEn:
    def __init__(self, phrases):
        self.lexicon = [p for p in (phrases or [])
                        if p and _LATIN.search(p)
                        and not any("一" <= c <= "鿿" for c in p)]

    def apply(self, text: str):
        """Rewrite near-phonetic spans to vocabulary terms.
        Returns (text, [(from, to), ...])."""
        if not self.lexicon or not text or not _LATIN.search(text):
            return text, []
        if not _ready():
            return text, []
        from .phonetic_match import scan_for_corrections
        try:
            matches = scan_for_corrections(text, self.lexicon)
        except Exception:  # noqa: BLE001
            return text, []
        if not matches:
            return text, []
        changed = []
        words = re.findall(r"\S+", text)
        # right-to-left so earlier word indices stay valid
        for m in sorted(matches, key=lambda m: m["start"], reverse=True):
            src = " ".join(words[m["start"]:m["end"]])
            if src == m["correction"]:
                continue
            words[m["start"]:m["end"]] = [m["correction"]]
            changed.append((src, m["correction"]))
        if not changed:
            return text, []
        return " ".join(words), list(reversed(changed))
