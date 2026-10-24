"""Rolling, timestamped beam transcript for an active meeting.

The live caption path remains small and mutable.  This module trails it and
decodes overlapping contextual windows with beam search, committing only the
part that has enough right-hand context.  A meeting stop merely flushes the
remaining tail; it does not start a second whole-meeting transcription.

Audio ingestion never waits for the NPU.  At most one background job is live
per channel, and each job is one <=30 second Whisper encoder window.
"""

from __future__ import annotations

import os
import threading
import time

import numpy as np

from . import hallucination
from .audio_energy import has_speech_energy
from .engine_lock import BACKGROUND, ENGINE_LOCK

SAMPLE_RATE = 16000
DEFAULT_WINDOW_S = 30.0
DEFAULT_HOP_S = 12.0
DEFAULT_LOOKAHEAD_S = 6.0
DEFAULT_BEAM_SIZE = 4

#: Per-span silence-hallucination gate, ported from the finalize
#: (stream_helpers's "silence-halluc drop": a decoded span whose OWN
#: audio peaks under this is a stock phrase whisper put on near-silence, not
#: speech). The rolling path only gated the WHOLE 30 s window with
#: has_speech_energy — one real sentence anywhere in the window let every
#: hallucinated span in the same window through, and those phantom lines
#: then seeded ghost speakers downstream. Same value as the finalize's
#: inline 0.02 on purpose; change them together.
#: (the stream contract 2026-08-24, agent W task 2c.)
SPAN_PEAK_GATE = 0.02


def _valid_timing(window_s: float, hop_s: float, lookahead_s: float) -> None:
    if not 1.0 <= window_s <= 30.0:
        raise ValueError("stable_window_seconds must be between 1 and 30")
    if not 0.5 <= hop_s <= window_s:
        raise ValueError("stable_hop_seconds must be between 0.5 and window")
    if not 0.0 <= lookahead_s < window_s:
        raise ValueError("stable_lookahead_seconds must be smaller than window")
    if hop_s > window_s - lookahead_s:
        raise ValueError("stable hop cannot exceed the committable window")


class RollingStableChannel:
    """One channel's bounded rolling decoder.

    ``snapshot`` is safe on the wire thread while a background decode runs.
    Times in returned spans are absolute channel-audio seconds.
    """

    def __init__(self, engine, *, channel: str, speaker: str,
                 bias_phrases=None, window_s: float = DEFAULT_WINDOW_S,
                 hop_s: float = DEFAULT_HOP_S,
                 lookahead_s: float = DEFAULT_LOOKAHEAD_S,
                 beam_size: int = DEFAULT_BEAM_SIZE, languages=None):
        _valid_timing(window_s, hop_s, lookahead_s)
        if beam_size != 4:
            raise ValueError("rolling meeting transcript requires beam_size=4")
        # Engine contract, checked HERE so a wrong engine fails at
        # stream_start with a message, not at the first window decode with
        # an AttributeError that the decode loop records and nobody reads
        # (TH-018, 2026-08-30: WhisperMac lacked transcribe_timed and the x64
        # rolling pass silently produced an EMPTY transcript for a whole
        # meeting while the take looked armed).
        if not callable(getattr(engine, "transcribe_timed", None)):
            raise TypeError(
                f"{type(engine).__name__} has no transcribe_timed(); the rolling "
                "meeting transcript needs a timed-decode engine (WhisperQnn, "
                "WhisperMac, StubEngine)")
        self.engine = engine
        self.channel = channel
        self.speaker = speaker
        self.bias_phrases = list(bias_phrases or [])
        self.languages = tuple(languages or ())
        self.language = self.languages[0] if len(self.languages) == 1 else None
        if len(self.languages) > 1:
            from .stream_helpers import LanguageVote
            self.language_vote = LanguageVote(seed=self.languages)
        else:
            self.language_vote = None
        self.window_s = float(window_s)
        self.hop_s = float(hop_s)
        self.lookahead_s = float(lookahead_s)
        self.beam_size = 4
        self.lock = threading.Lock()
        self._chunks: list[np.ndarray] = []
        self._base_sample = 0
        self._samples = 0
        self._next_end_s = self.window_s
        self._running = False
        self._closed = False
        self._spans: list[dict] = []
        self._through_s = 0.0
        self._error: str | None = None
        self._idle = threading.Event()
        self._idle.set()
        self._supports_yield: bool | None = None   # engine sig, probed once

    def push(self, samples: np.ndarray) -> None:
        audio = np.asarray(samples, dtype=np.float32)
        if not len(audio):
            return
        with self.lock:
            if self._closed:
                return
            self._chunks.append(audio.copy())
            self._samples += len(audio)
            should_start = (not self._running
                            and self._samples / SAMPLE_RATE >= self._next_end_s)
            if should_start:
                self._running = True
                self._idle.clear()
        if should_start:
            threading.Thread(target=self._run_due, daemon=True).start()

    def _audio_locked(self) -> np.ndarray:
        if len(self._chunks) > 1:
            self._chunks = [np.concatenate(self._chunks)]
        return self._chunks[0] if self._chunks else np.zeros(0, np.float32)

    def _slice_locked(self, start_sample: int, end_sample: int) -> np.ndarray:
        audio = self._audio_locked()
        a = max(0, start_sample - self._base_sample)
        b = max(a, end_sample - self._base_sample)
        return audio[a:b].copy()

    def _trim_locked(self, keep_from_sample: int) -> None:
        """Retain only audio a future <=30 second window can reference."""
        keep_from_sample = min(max(self._base_sample, keep_from_sample), self._samples)
        if keep_from_sample <= self._base_sample:
            return
        audio = self._audio_locked()
        drop = keep_from_sample - self._base_sample
        self._chunks = [audio[drop:].copy()] if drop < len(audio) else []
        self._base_sample = keep_from_sample

    def _run_due(self) -> None:
        try:
            while True:
                with self.lock:
                    if self._closed:
                        self._running = False
                        self._idle.set()
                        return
                    duration = self._samples / SAMPLE_RATE
                    if duration + 1e-6 < self._next_end_s:
                        self._running = False
                        self._idle.set()
                        return
                    end_s = self._next_end_s
                    # OVERLAP SHEDDING (2026-08-27): with the background
                    # pass yielding the engine to live decodes, a dense
                    # meeting can starve this scheduler and hops pile up —
                    # and finish() (by contract) drops what was never
                    # decoded, which LOST ~35% of stable words on the AMI
                    # replay. When behind, jump this window's end forward
                    # as far as COVERAGE allows (its start must stay at or
                    # before `through`, so nothing ever goes undecoded):
                    # the effective hop stretches toward window-lookahead
                    # and the luxurious 18 s window overlap is what gets
                    # shed under pressure — never text. Quality cost is
                    # only the redundant double-decode of overlap regions;
                    # each window itself is the same full-context beam
                    # decode as ever.
                    if end_s < duration:
                        end_s = max(end_s, min(
                            duration,
                            self._through_s + self.window_s - 0.5))
                    self._next_end_s = end_s + self.hop_s
                    start_s = max(0.0, end_s - self.window_s)
                    clip = self._slice_locked(int(start_s * SAMPLE_RATE),
                                              int(end_s * SAMPLE_RATE))
                self._decode_window(clip, start_s,
                                    max(0.0, end_s - self.lookahead_s))
                with self.lock:
                    future_start = max(0.0, self._next_end_s - self.window_s)
                    self._trim_locked(int(future_start * SAMPLE_RATE))
        except Exception as exc:  # keep ingestion and the live path alive
            # Loud on stderr as well as recorded: a rolling pass that dies
            # here stops committing text for the rest of the meeting, and
            # the take still LOOKS armed from the host (TH-018).
            import sys, traceback
            print(f"ROLLING PASS DIED ({self.channel}): {type(exc).__name__}: {exc} "
                  "-- no further text will be committed on this channel",
                  file=sys.stderr, flush=True)
            traceback.print_exc(file=sys.stderr)
            with self.lock:
                self._error = f"{type(exc).__name__}: {exc}"
                self._running = False
                self._idle.set()

    def _yield_kwargs(self) -> dict:
        """{'yield_fn': ...} when the engine supports cooperative yielding
        and DICTATION_NOTES_BG_YIELD=1 opts in, else {} -- legacy engines
        (mac / stub) see the exact old call. The yield hands the engine
        lock to any waiting CAPTION/HOLD decode between decoder steps, so
        this pass's ~whole-window holds stop bounding live-commit latency
        (see EngineLock.yield_if_contended).

        DEFAULT OFF since 2026-08-29 -- measured, not vibes. A 9-probe
        on-device bisect (X2 NPU, persona replay corpus, deterministic
        decode) pinned this yield as the single cause of a 36.9%->55.9%
        WER regression: hypothesis words fell 148->91 with deletions
        104 vs 50 -- whole spans of speech silently vanished from the
        rolling transcript. QNN sessions are stateless across calls, and
        the resume after a yield loses decoded state under exactly the
        conditions the yield exists for. Same tip with the yield off
        scored 28.2% WER (S20/D32/I3, 166 words) -- BETTER than the
        pre-yield baseline, so every other tip improvement survives.
        The owner directive is full-ASR accuracy first, latency
        good-to-have: the yield may return default-ON only after the
        resume path is fixed AND an on-device QNN WER gate (not the server
        one -- it cannot see this class) certifies it.

        MECHANISM REVISED 2026-08-29 (TH-015 A/B, finalize_yield_ab.py):
        the finalize pass runs the SAME mid-decode yield under HOLD
        contention with ZERO degradation (contended == uncontended,
        byte-for-byte error counts), so the resume itself does NOT lose
        decoder state -- the damage here was STARVATION: this pass is
        real-time paced with backlog shedding, and while a yielded window
        decode waits, audio accumulates past the shed horizon and whole
        spans drop (hence DEL-dominant). Unchanged conclusion: default
        OFF. Changed re-enable condition: fix the shed-under-starvation
        interplay (not a decoder resume path), then certify on-device."""
        if os.environ.get("DICTATION_NOTES_BG_YIELD", "0") != "1":
            return {}
        if self._supports_yield is None:
            try:
                import inspect
                self._supports_yield = "yield_fn" in inspect.signature(
                    self.engine.transcribe_timed).parameters
            except (TypeError, ValueError):  # builtins / exotic callables
                self._supports_yield = False
        if not self._supports_yield:
            return {}
        tag = f"notes:rolling:{self.channel}"
        return {"yield_fn":
                lambda: ENGINE_LOCK.yield_if_contended(BACKGROUND, tag)}

    def _decode_window(self, clip: np.ndarray, start_s: float,
                       commit_through_s: float) -> None:
        local_spans = []
        if len(clip) >= SAMPLE_RATE // 2 and has_speech_energy(clip):
            ykw = self._yield_kwargs()
            with ENGINE_LOCK.held(BACKGROUND,
                                  f"notes:rolling:{self.channel}"):
                result = self.engine.transcribe_timed(
                    clip, beam_size=4, bias_phrases=self.bias_phrases,
                    condition_on_prev=False, language=self.language, **ykw)
                # Multilingual meetings remain auto-detected per window, but
                # a language outside the declared set is never allowed to
                # become durable stable text. Re-decode that same window once
                # pinned to the declared language selected by LanguageVote.
                if self.language_vote is not None:
                    spans = result.get("spans") or []
                    foreign = next((s[3] for s in spans
                                    if len(s) > 3 and s[3]
                                    and s[3] not in self.language_vote.seed), None)
                    if foreign:
                        text = " ".join(str(s[2]) for s in spans if len(s) > 2)
                        pin = self.language_vote.pin_for(text)
                        if pin:
                            result = self.engine.transcribe_timed(
                                clip, beam_size=4,
                                bias_phrases=self.bias_phrases,
                                condition_on_prev=False, language=pin,
                                **ykw)
                            spans = result.get("spans") or []
                    for span in spans:
                        if len(span) > 3:
                            self.language_vote.record(span[3])
            local_spans = result.get("spans") or []
            # Per-span energy gate (SPAN_PEAK_GATE): drop spans whose own
            # audio is near-silent — the window-level has_speech_energy above
            # cannot catch a hallucinated span riding a window that also
            # holds real speech. Local span times are window-relative here.
            kept = []
            for raw in local_spans:
                if len(raw) >= 2:
                    seg = clip[int(float(raw[0]) * SAMPLE_RATE):
                               int(float(raw[1]) * SAMPLE_RATE)]
                    if len(seg) and float(np.abs(seg).max()) < SPAN_PEAK_GATE:
                        continue
                kept.append(raw)
            local_spans = kept
        with self.lock:
            old_through = self._through_s
            crossing_start = None
            for raw in local_spans:
                if len(raw) < 3:
                    continue
                a, b, text = float(raw[0]) + start_s, float(raw[1]) + start_s, str(raw[2]).strip()
                language = raw[3] if len(raw) > 3 else None
                # The middle of an overlap is authoritative.  Earlier text is
                # already stable; the right edge remains mutable for lookahead.
                if not text or b <= old_through + 0.05:
                    continue
                # Unified commit-text gate (2026-08-28): these spans ride the
                # wire as stable_text/stable_spans and can be substituted
                # into committed lines (_beam_text_for) — a hallucination
                # pleasantry or a repetition loop must be dropped HERE, the
                # same verdicts every committed line passes. The per-span
                # ENERGY gate above cannot catch junk riding a loud window.
                if (hallucination.is_junk_utterance(text)
                        or hallucination.loop_junk(text)):
                    continue
                if b > commit_through_s + 0.05:
                    # Do not claim this boundary as stable.  The overlapping
                    # next window gets another chance to close/reconcile it.
                    crossing_start = a if crossing_start is None else min(crossing_start, a)
                    continue
                if a < old_through - 0.25:
                    continue
                if self._spans and text == self._spans[-1]["text"] and a < self._spans[-1]["end"] + 0.5:
                    continue
                self._spans.append({
                    "start": round(a, 3), "end": round(b, 3),
                    "text": text, "language": language,
                    "channel": self.channel, "speaker": self.speaker,
                })
            # Silence is stable evidence too, but never advance across a
            # decoded span that extends into the mutable lookahead.
            safe_through = (min(commit_through_s, crossing_start)
                            if crossing_start is not None else commit_through_s)
            self._through_s = max(self._through_s, safe_through)

    def finish(self, timeout_s: float = 45.0) -> None:
        """Drain scheduled work and beam-decode the <=30 second tail once.
        Stop stays fast BY CONTRACT (no second whole-meeting transcription);
        full coverage under load is the overlap-shedding scheduler's job
        (see _run_due), which keeps `through` within one window of the
        take's end so this single tail decode always covers the rest."""
        with self.lock:
            self._closed = True
        self._idle.wait(timeout=max(0.0, timeout_s))
        with self.lock:
            end_s = self._samples / SAMPLE_RATE
            through = self._through_s
            start_s = max(0.0, end_s - self.window_s)
            clip = self._slice_locked(int(start_s * SAMPLE_RATE), self._samples)
        if end_s > through + 0.05 and len(clip):
            self._decode_window(clip, start_s, end_s)

    def cancel(self) -> None:
        """Stop scheduling and release buffered meeting audio."""
        with self.lock:
            self._closed = True
            self._chunks = []
            self._base_sample = self._samples

    def snapshot(self) -> dict:
        with self.lock:
            spans = [dict(s) for s in self._spans]
            return {"spans": spans, "through": round(self._through_s, 3),
                    "text": " ".join(s["text"] for s in spans),
                    "error": self._error}
