"""Traditional->Simplified normalization for transcript text.

Whisper's zh decoding sometimes emits traditional characters mid-meeting
(與/總統 in an otherwise simplified transcript — cosmetic but jarring for a
simplified-Chinese team). zhconv (pure-Python, bundled wheel) maps them to
simplified at the same "final text" layers that run the alias rewrites:
live commits and the accurate pass's turns.

Fail-soft by design: no zhconv on the box -> text passes through untouched.
"""
from __future__ import annotations

_convert = None


def _load():
    global _convert
    if _convert is None:
        try:
            from zhconv import convert as _c
            _convert = _c
        except Exception:  # noqa: BLE001 — normalization is an enhancement
            _convert = False
    return _convert


def to_simplified(text: str) -> str:
    """Simplified-Chinese form of `text`; unchanged when no CJK present or
    zhconv is unavailable."""
    if not text or not any("一" <= ch <= "鿿" for ch in text):
        return text
    conv = _load()
    if not conv:
        return text
    try:
        return conv(text, "zh-hans")
    except Exception:  # noqa: BLE001
        return text
