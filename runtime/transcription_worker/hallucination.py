"""Whisper hallucination phrases — the FG-6 family.

On silence, music or an abruptly-cut clip, whisper emits training-data
pleasantries: "Thank you.", "Thanks for watching!", "Продолжение следует...",
"谢谢观看", subtitle credits, subscribe pleas. Two treatments:

- `is_junk_utterance(text)`: the WHOLE utterance is one of these — drop the
  line outright (macOS AudioGate blocklist parity). Safe: nobody's real turn
  consists solely of "please subscribe to my channel".
- `strip_trailing(text)`: the phrase is APPENDED after real content. Only
  called when the audio evidence says the tail is fabricated (the clip had a
  long trailing silence trimmed away, or the pinned re-decode came back
  empty) — a genuine "thank you" at the end of a real sentence spoken with
  real audio behind it is never touched.
"""
from __future__ import annotations

import re

#: Lowercased, punctuation-insensitive phrase cores.
_PHRASES = (
    "thank you",
    "thanks for watching",
    "thank you for watching",
    "please subscribe",
    "don't forget to subscribe",
    "see you next time",
    "продолжение следует",
    "субтитры",
    "谢谢观看",
    "谢谢大家观看",
    "请不吝点赞",
    "订阅我的频道",
    "字幕由",
    "amara.org",
    "ご視聴ありがとうございました",
)

_STRIP = re.compile(r"[\s\.,!。!…、-]+")

#: Single stray words ASR coughs up on breaths/noise — junk ONLY when they
#: are the ENTIRE utterance. Deliberately tiny: "Yes."/"Wow."/"Peace." are
#: real interjections and stay; "You"/"The"/"嗯" alone are not speech.
STRAY_SINGLES = {"you", "the", "a", "uh", "um", "oh", "嗯", "啊", "呃"}


def _core(text: str) -> str:
    return _STRIP.sub(" ", text.lower()).strip()


def is_junk_utterance(text: str) -> bool:
    """True when the entire text is (only) hallucination pleasantries."""
    core = _core(text)
    if not core:
        return False
    if core in STRAY_SINGLES:
        return True
    # LONGEST phrase first: "thank you" replaced before "thank you for
    # watching" broke the longer phrase mid-match, leaving "for watching"
    # behind — so the flagship pleasantry itself was never a junk
    # UTTERANCE (only strip_trailing caught it, and only with audio
    # evidence). Found 2026-08-28 building the unified commit gate.
    for p in sorted(_PHRASES, key=len, reverse=True):
        core = core.replace(p, " ")
    return not core.strip()


def strip_trailing(text: str) -> str:
    """Remove ONE trailing blocklisted phrase (with its punctuation)."""
    stripped = text.rstrip()
    for p in _PHRASES:
        pattern = re.escape(p).replace(r"\ ", r"[\s\.,!。!…、]*")
        m = re.search(pattern + r"[\s\.,!。!…、]*$", stripped, re.IGNORECASE)
        if m:
            return stripped[:m.start()].rstrip(" ,.。!!…、-")
    return text


def loop_junk(text: str, hallucination_guard: bool = True) -> bool:
    """Repetition-loop hallucination verdict — the ENGINE's looks_like_junk
    (compression-ratio + n-gram loop rules), fail-soft to False when no
    engine module is importable (pure-logic tests). Lives here so EVERY
    caption surface — the commit gate, the partial preview, the rolling
    stable transcript — asks the same question of the same rule."""
    try:
        from .whisper_mac import looks_like_junk
    except Exception:  # preserve fail-soft behavior when an optional engine cannot load
        return False
    return looks_like_junk(text, hallucination_guard=hallucination_guard)


_PHRASE_LOOP = re.compile(
    r"(?P<ph>.{2,40}?)(?P<sep>[,,、。.!!??::;;\s]*)(?:(?P=ph)(?P=sep)){2,}",
    re.S)


def collapse_phrase_loops(text: str) -> str:
    """Collapse whisper's loop-tail hallucination: after real speech a
    window ending in silence pads itself by repeating the last phrase
    ("都不贵的, " x12 riding the END of a real sentence — field
    2026-08-19). Whole-utterance loops are caught upstream; this trims
    loops EMBEDDED in otherwise-real text. Conservative: a phrase must
    repeat 3+ times to collapse, so emphatic doubles ("超级, 超级")
    survive."""
    if not text:
        return text
    prev = None
    while prev != text:
        prev = text
        text = _PHRASE_LOOP.sub(lambda m: m.group("ph") + m.group("sep"),
                                text)
    return text


REGURGITATION_RUN = 4


def strip_vocab_run(text: str, phrases) -> tuple[str, int]:
    """Excise a comma-run of >=REGURGITATION_RUN vocabulary terms appearing
    IN PROMPT ORDER — the model continuing the prompt list, not speech.
    Ported from whisper_mac's dictation fix: the
    order check is what keeps attendee-name sentences alive ("今天参加的有
    Jossie Lin、Yurin Zhang…" is comma-separated vocabulary terms AND an
    ordinary sentence; a person naming people does not recite them in
    vocabulary order). Complements the whole-line coverage guard, which
    cannot touch a run EMBEDDED in otherwise-real text.
    Returns (cleaned_text, dropped_term_count)."""
    terms = [p.strip() for p in (phrases or []) if p and p.strip()]
    if not terms or not text:
        return text, 0
    order = {t.lower(): i for i, t in enumerate(terms)}
    alt = "|".join(re.escape(t) for t in
                   sorted(set(terms), key=len, reverse=True))
    pattern = re.compile(
        rf"(?:{alt})(?:\s*[,，、]\s*(?:{alt})){{{REGURGITATION_RUN - 1},}}"
        rf"(?:\s*[,，、]\s*)?", re.IGNORECASE)
    dropped = 0

    def _sub(m):
        nonlocal dropped
        run = [t for t in re.split(r"\s*[,，、]\s*", m.group(0).strip(" ,，、"))
               if t]
        seen = [order.get(t.lower(), -1) for t in run]
        if any(i < 0 for i in seen) or not all(
                a < b for a, b in zip(seen, seen[1:])):
            return m.group(0)  # not in prompt order — a human list, keep
        dropped += len(run)
        return ""

    cleaned = pattern.sub(_sub, text)
    if not dropped:
        return text, 0
    cleaned = re.sub(r"\s{2,}", " ", cleaned)
    cleaned = re.sub(r"[ \t]*([,，、.。?？!！])", r"\1", cleaned)
    return cleaned.strip(" ,，、"), dropped
