"""Optional source-only stream utilities. No diarization backend is included.

The packaged dictation preview does not include this module. Its text join
uses whisper_mac directly and its declared language is passed to recognition.
"""
from __future__ import annotations

MIN_SEGMENT_S = 0.4
MERGE_GAP_S = 1.0
MAX_TURN_S = 12.0
DEFAULT_SEED_LANGUAGES = ("zh", "en")

def merge_turns(segs, max_turn_s: float = MAX_TURN_S,
                merge_gap_s: float = MERGE_GAP_S,
                min_segment_s: float = MIN_SEGMENT_S):
    """[(start, end, spk)] → [(start, end, spk)] with adjacent same-speaker
    segments merged (gap < merge_gap_s) and turns capped at max_turn_s.
    Sub-minimum slivers are dropped UNLESS they extend an open turn of the
    same speaker (then they're absorbed — a 0.3s tail of a sentence is
    signal, not noise). Pure, unit-tested."""
    turns = []
    for start, end, spk in sorted(segs, key=lambda s: s[0]):
        if turns:
            t_start, t_end, t_spk = turns[-1]
            same = spk == t_spk
            close = start - t_end < merge_gap_s
            fits = end - t_start <= max_turn_s
            if same and close and fits:
                turns[-1] = (t_start, max(t_end, end), t_spk)
                continue
        if end - start < min_segment_s:
            continue
        turns.append((start, end, spk))
    # SPLIT over-long turns too: capping only stopped further merging, so a
    # single long diarizer segment still produced an unreadable 18s wall
    # (field, 2026-08-19). Chop into near-equal pieces <= max_turn_s.
    split: list[tuple[float, float, int]] = []
    for start, end, spk in turns:
        dur = end - start
        if dur <= max_turn_s:
            split.append((start, end, spk))
            continue
        import math
        n = math.ceil(dur / max_turn_s)
        step = dur / n
        for i in range(n):
            split.append((start + i * step, min(end, start + (i + 1) * step), spk))
    return split

def effective_word_count(text: str) -> int:
    """Count CJK characters and whitespace-delimited non-CJK words."""
    cjk = sum(1 for ch in text if "一" <= ch <= "鿿")
    words = len([w for w in text.split() if any(not ("一" <= c <= "鿿") for c in w)])
    return cjk + words

class LanguageVote:
    """Per-take language profile constrained by the declared language set.

    Seeded languages (zh+en by default, or the request's ``languages``) are
    trusted. A detector result outside that set is always pinned back to a
    declared language. Repeated detector guesses cannot add a language to the
    set; callers explicitly declare genuinely multilingual meetings.
    """

    def __init__(self, seed=DEFAULT_SEED_LANGUAGES):
        self.counts: dict[str, int] = {}
        self.seed = set(seed or ())

    def record(self, lang: str | None):
        if lang:
            self.counts[lang] = self.counts.get(lang, 0) + 1

    def majority(self) -> str | None:
        # Seeded languages win ties; a take that is 90% zh should pin to zh.
        if not self.counts:
            return None
        return max(self.counts.items(),
                   key=lambda kv: (kv[1], kv[0] in self.seed))[0]

    def suspect(self, lang: str | None, duration_s: float, word_count: int) -> bool:
        if not lang or lang in self.seed:
            return False  # seeded languages are never second-guessed
        # Detector outputs are not evidence that a language is genuinely
        # present. Two hallucinated foreign spans used to "earn trust" here
        # and permanently disable the guard. A genuinely multilingual meeting
        # declares its accepted languages on the request instead.
        return True

    def pin_for(self, text: str) -> str | None:
        """Choose a declared language for a suspicious detector result."""
        compact = text.replace(" ", "")
        cjk = sum(1 for ch in compact if "\u4e00" <= ch <= "\u9fff")
        if compact and cjk >= len(compact) * 0.4 and "zh" in self.seed:
            return "zh"
        majority = self.majority()
        if majority in self.seed:
            return majority
        if "en" in self.seed:
            return "en"
        return sorted(self.seed)[0] if self.seed else None

def merge_speaker_labels(turns, src_audio, sample_rate, embed_fn=None,
                         num_speakers=None):
    """Merge over-split labels using an explicitly supplied embedding function.

    Keep the known remote-speaker count as a floor and renumber labels by
    first appearance. With no embedding function, leave labels unchanged."""
    import sys as _sys
    import numpy as np
    sys_speakers = sorted({t["speaker"] for t in turns
                           if t["speaker"].startswith("Speaker")})
    if len(sys_speakers) <= 1:
        return
    if embed_fn is None:
        return
    _embed = embed_fn

    def _speaker_embedding(name):
        clips = []
        total = 0
        for t in sorted((t for t in turns if t["speaker"] == name),
                        key=lambda t: t["end"] - t["start"], reverse=True):
            a0 = int(t["start"] * sample_rate)
            b0 = min(len(src_audio), int(t["end"] * sample_rate))
            if b0 <= a0:
                continue
            clips.append(src_audio[a0:b0])
            total += b0 - a0
            if total >= 10 * sample_rate:
                break
        if not clips:
            return None
        v = np.asarray(_embed(np.concatenate(clips)), dtype=np.float32)
        n = float(np.linalg.norm(v))
        return v / n if n > 0 else None

    embs = {name: _speaker_embedding(name) for name in sys_speakers}
    from .live_speakers import SIM_THRESHOLD
    secs = {name: sum(t["end"] - t["start"] for t in turns
                      if t["speaker"] == name) for name in sys_speakers}
    # Floor on surviving REMOTE speakers from the known participant count
    # (roster/Zoom count includes the local mic user, so remote = count-1).
    # Never merge below it — a known 4-person call must keep 4 distinct voices
    # even if two of them happen to sound similar (the 0.536 false-merge case).
    min_remote = None
    if num_speakers:
        try:
            min_remote = max(1, int(num_speakers) - 1)
        except (TypeError, ValueError):
            min_remote = None
    # A speaker with real airtime is a real participant; merging TWO of them is
    # almost always wrong (distinct people who happen to sound alike). The merge
    # exists to fold OVER-SPLIT fragments (short phantom clusters) back in — so
    # only merge two substantial speakers when their voiceprints agree strongly.
    # Without this, hitting the count floor could fuse two real speakers while a
    # tiny phantom survives (MTG_32063: 4->3 correct count, but the two LARGEST
    # real speakers got merged and a 1.1s phantom kept -> DER 8.2->25.7).
    SUBSTANTIAL_S = 8.0
    HIGH_SIM = 0.70
    # Candidate merges above threshold, HIGHEST similarity first, so if we must
    # stop at the floor we keep only the most-confident merges.
    cands = []
    for i, a_name in enumerate(sys_speakers):
        for b_name in sys_speakers[i + 1:]:
            va, vb = embs.get(a_name), embs.get(b_name)
            if va is None or vb is None:
                continue
            sim = float(np.dot(va, vb))
            print(f"merge_speaker_labels: sim {a_name}~{b_name} = {sim:.3f}",
                  file=_sys.stderr, flush=True)
            lo = min(secs[a_name], secs[b_name])
            if lo < 3.0:
                thr = 0.30            # tiny cluster: relaxed (likely a phantom)
            elif lo >= SUBSTANTIAL_S:
                thr = HIGH_SIM        # both real participants: strong agreement only
            else:
                thr = SIM_THRESHOLD
            if sim >= thr:
                cands.append((sim, a_name, b_name))
    cands.sort(key=lambda c: c[0], reverse=True)
    merged_into = {}
    surviving = len(sys_speakers)
    for sim, a_name, b_name in cands:
        if min_remote is not None and surviving <= min_remote:
            break  # at the known participant floor — keep distinct voices
        if a_name in merged_into or b_name in merged_into:
            continue
        if secs[a_name] >= secs[b_name]:
            merged_into[b_name] = a_name
        else:
            merged_into[a_name] = b_name
        surviving -= 1
    if merged_into:
        for t in turns:
            while t["speaker"] in merged_into:
                t["speaker"] = merged_into[t["speaker"]]
    # renumber by first appearance so labels stay A, B, C...
    order = {}
    for t in sorted(turns, key=lambda t: t["start"]):
        name = t["speaker"]
        if name.startswith("Speaker") and name not in order:
            order[name] = f"Speaker {chr(ord('A') + len(order))}"
    for t in turns:
        if t["speaker"] in order:
            t["speaker"] = order[t["speaker"]]
