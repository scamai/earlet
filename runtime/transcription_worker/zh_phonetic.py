"""Pinyin phrase recovery for CJK vocabulary terms.

The WeChat-智聆 "hot words" layer, local edition: whisper renders an
unfamiliar domain term as a same/near-sounding common phrase (甜区 →
填区, 容错率 → 容作率 — every zh entry in the hand-written alias table
is this failure). Instead of hand-maintaining aliases per term, slide a
pinyin window over the hypothesis and rewrite any span whose
pronunciation matches a vocabulary phrase.

Scope is deliberately narrow — candidates come ONLY from the user's own
vocabulary: an open-vocabulary LM rescorer was prototyped first and
measured harmful (it "fixed" correct casual speech: 吃发力→喜发力,
与→于; conversational trigrams don't know domain words, margin tuning
can't save it — 2026-08-19 spike). Exact-pinyin matches always rewrite;
one-syllable-off matches rewrite only when the syllable is CLOSE
(shared initial or final — accent drift), so 田区 recovers 甜区 but
random text stays put.

Common-word protection: a source span that is itself a frequent word
(jieba's dictionary, e.g. 天气) is never rewritten — without this,
天气(tianqi) fuzzy-matched 甜区(tianqu) and got "corrected". Rare
nonsense spans (填区, 容作率, 桑古拉斯) sail through.

Fail-soft: no pypinyin on the box -> no-op; no jieba -> exact-pinyin
matches only.
"""
from __future__ import annotations

from functools import lru_cache

_lazy_pinyin = None
_COMMON_FREQ_MIN = 500


@lru_cache(maxsize=1)
def _word_freq():
    try:
        import jieba
        jieba.dt.initialize()
        return jieba.dt.FREQ
    except Exception:  # noqa: BLE001
        return None


def _load():
    global _lazy_pinyin
    if _lazy_pinyin is None:
        try:
            from pypinyin import lazy_pinyin as lp
            _lazy_pinyin = lp
        except Exception:  # noqa: BLE001 — enhancement layer
            _lazy_pinyin = False
    return _lazy_pinyin


def _is_cjk(ch: str) -> bool:
    return "一" <= ch <= "鿿"


def _close(a: str, b: str) -> bool:
    """One-syllable tolerance for accent drift: same syllable, or the two
    share an initial or a final (pu~gu shares final 'u'; tian~qian shares
    final 'ian'). Anything looser starts rewriting real words."""
    if a == b:
        return True
    if not a or not b:
        return False
    # crude initial/final split: initial = leading consonant cluster
    def split(s):
        for i, c in enumerate(s):
            if c in "aeiouv":
                return s[:i], s[i:]
        return s, ""
    ia, fa = split(a)
    ib, fb = split(b)
    return (ia == ib and ia != "") or (fa == fb and fa != "")


class VocabPhonetic:
    def __init__(self, phrases):
        lp = _load()
        self.terms = []
        if not lp:
            return
        for ph in phrases or []:
            chars = [c for c in ph if _is_cjk(c)]
            if len(chars) < 2 or len(chars) != len(ph):
                continue  # CJK-pure multi-char terms only
            self.terms.append((ph, lp(ph)))

    def apply(self, text: str):
        """Rewrite near-pinyin spans to vocabulary terms.
        Returns (text, [(from, to), ...])."""
        lp = _load()
        if not lp or not self.terms or not text:
            return text, []
        changed = []
        chars = list(text)
        for term, tpy in self.terms:
            n = len(tpy)
            i = 0
            while i + n <= len(chars):
                window = chars[i:i + n]
                if not all(_is_cjk(c) for c in window):
                    i += 1
                    continue
                if "".join(window) == term:
                    i += n
                    continue
                src = "".join(window)
                freq = _word_freq()
                if freq is not None and freq.get(src, 0) >= _COMMON_FREQ_MIN:
                    i += 1
                    continue  # the span is a real common word — hands off
                wpy = lp(src)
                diff = sum(1 for a, b in zip(wpy, tpy) if a != b)
                ok = diff == 0 or (diff == 1 and freq is not None and all(
                    _close(a, b) for a, b in zip(wpy, tpy)))
                if ok:
                    changed.append((src, term))
                    chars[i:i + n] = list(term)
                    i += n
                else:
                    i += 1
        return "".join(chars), changed
