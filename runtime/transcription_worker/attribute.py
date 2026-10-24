"""Pure function: merge word timestamps (whisper_mac.transcribe(...,
return_words=True)) + diarization turns (diarize_mac.diarize(...)) into
speaker-attributed segments.

No models, no I/O, no state -- unit-testable on hand-built inputs. This is
the "cpWER" half of the eval/meeting_notes_eval north star: WER is scored on
the plain hyp text; cpWER is scored per-speaker on `attribute()`'s output.

Algorithm
---------
For each word, assign the diarization turn with the greatest **temporal
overlap** with the word's [start, end) span (majority-overlap, not
nearest-center or nearest-boundary -- more robust when a word straddles a
turn boundary, which happens often given ASR word timestamps are decoder
estimates, not ground truth). A word with zero overlap against every turn
(e.g. it falls in a VAD-trimmed gap the diarizer dropped) falls back to the
nearest turn by boundary distance, so no word is ever silently ignored.

Words are then grouped into speaker-attributed segments: consecutive words
assigned to the same speaker are merged into one segment; the segment
boundary is the [first_word.start, last_word.end) span, and its text is the
words joined with a single space (callers needing original spacing/casing
should keep whisper_mac's own text — this module's `text` field is meant
for display, not exact-replay of the ASR text).
"""

from __future__ import annotations

from typing import TypedDict


class Word(TypedDict, total=False):
    word: str
    start: float
    end: float


class Turn(TypedDict, total=False):
    start: float
    end: float
    speaker: str


class Segment(TypedDict):
    start: float
    end: float
    speaker: str
    text: str


def _overlap(a_start: float, a_end: float, b_start: float, b_end: float) -> float:
    return max(0.0, min(a_end, b_end) - max(a_start, b_start))


def _boundary_distance(a_start: float, a_end: float, b_start: float, b_end: float) -> float:
    """0 if the spans overlap; otherwise the gap between the nearest edges."""
    if a_end <= b_start:
        return b_start - a_end
    if b_end <= a_start:
        return a_start - b_end
    return 0.0


def assign_speaker(word: Word, turns: list[Turn]) -> str | None:
    """The speaker for one word: max-overlap turn, falling back to the
    closest turn by boundary distance if no turn overlaps it at all.
    Returns None only when `turns` is empty."""
    if not turns:
        return None

    w_start = float(word.get("start", 0.0))
    w_end = float(word.get("end", w_start))

    best_turn = None
    best_overlap = 0.0
    for t in turns:
        ov = _overlap(w_start, w_end, float(t["start"]), float(t["end"]))
        if ov > best_overlap:
            best_overlap = ov
            best_turn = t

    if best_turn is not None:
        return best_turn["speaker"]

    # No overlap with any turn (word landed in a gap the diarizer dropped,
    # e.g. VAD trimmed it as non-speech): fall back to the nearest turn.
    closest = min(
        turns,
        key=lambda t: _boundary_distance(w_start, w_end, float(t["start"]), float(t["end"])),
    )
    return closest["speaker"]


def attribute(words: list[Word], turns: list[Turn]) -> list[Segment]:
    """Merge word timestamps + diarization turns into speaker-attributed
    segments. Pure function: same inputs -> same outputs, no side effects.

    words: [{"word","start","end"}, ...] in time order (whisper_mac's
        return_words=True output, possibly concatenated across utterances).
    turns: [{"start","end","speaker"}, ...] (diarize_mac.diarize()["turns"]).

    Returns [{"start","end","speaker","text"}, ...], time-ordered, adjacent
    same-speaker words merged into one segment.
    """
    if not words:
        return []
    if not turns:
        # No diarization available (e.g. mono short clip, or diarize()
        # returned zero speakers) -- one segment, unknown speaker, rather
        # than dropping the transcript on the floor.
        starts = [float(w.get("start", 0.0)) for w in words]
        ends = [float(w.get("end", s)) for w, s in zip(words, starts)]
        return [{
            "start": round(min(starts), 3),
            "end": round(max(ends), 3),
            "speaker": "unknown",
            "text": " ".join(w.get("word", "").strip() for w in words if w.get("word", "").strip()),
        }]

    segments: list[Segment] = []
    current_speaker: str | None = None
    current_words: list[str] = []
    current_start: float | None = None
    current_end: float | None = None

    def _flush():
        if current_words:
            segments.append({
                "start": round(current_start, 3),
                "end": round(current_end, 3),
                "speaker": current_speaker,
                "text": " ".join(current_words),
            })

    for w in words:
        text = (w.get("word") or "").strip()
        if not text:
            continue
        w_start = float(w.get("start", 0.0))
        w_end = float(w.get("end", w_start))
        speaker = assign_speaker(w, turns)

        if speaker != current_speaker:
            _flush()
            current_speaker = speaker
            current_words = [text]
            current_start = w_start
            current_end = w_end
        else:
            current_words.append(text)
            current_end = max(current_end, w_end)

    _flush()
    return segments
