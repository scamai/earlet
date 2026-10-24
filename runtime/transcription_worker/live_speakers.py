"""Online speaker attribution for the live caption stream.

The offline pass clusters ALL embeddings at once (sherpa FastClustering);
live we cannot wait, so this is the standard online variant: per-turn
embedding -> cosine match against running speaker centroids -> best match
above threshold joins (centroid updates as a running mean), otherwise a new
speaker is born. Labels are stable for the whole take (Speaker A stays A),
which is exactly what a streaming consumer needs.

Turn detection is energy-based: a run of TURN_GAP_S below the silence floor
closes the current turn. Turns shorter than MIN_TURN_S never reach the
embedder (too little voice to embed reliably — they inherit the previous
speaker instead, matching how humans read captions).

Pure logic lives in OnlineSpeakerRegistry + TurnTracker (unit-testable with
a fake embedder); the sherpa extractor is injected.
"""
from __future__ import annotations

import os
import numpy as np

from . import audio_energy

#: Cosine similarity ABOVE which a turn joins an existing speaker.
#: Originally calibrated on-device 2026-08-18 (sim_calib.py, FLEURS half-clip
#: pairs) against the now-REMOVED CAM++ embedding.onnx; since 2026-08-31 the
#: embed fn is the DiariZen ResNet ClipEmbedder (single stack), whose cosine
#: distribution is coarser — threshold subject to re-tune in the on-device
#: wrapper validation; the bank corrector retro-corrects registry mistakes.
SIM_THRESHOLD = float(os.environ.get("DICTATION_NOTES_SPK_SIM", "0.45"))
#: ^ env-tunable (2026-08-21): on same-room/same-tile remote speakers whose
#: voiceprints sit ~0.53-0.57 apart, 0.45 MERGES them (all one speaker); a
#: bilingual code-switcher's own halves sit ~0.83 (stay merged) but cross-
#: language turns can dip below any single cut. No value separates both cleanly
#: acoustically -> tune per deployment; Zoom active-speaker metadata is the real fix.

#: Evidence-accumulation gate on the retro-merge (design of 2026-08-24):
#: a SINGLE short-turn cosine must NOT collapse two speakers into one. Two
#: centroids are only merge-eligible once BOTH are STABLE (>= MERGE_MIN_OBS
#: turns of accumulated evidence, not a noisy 1-2-turn candidate), and only
#: after they agree across MERGE_CONSEC consecutive checks (a transient 0.7
#: from one noisy short turn resets the streak). Short-turn NeMo embeddings of
#: DISTINCT people sit 0.4-0.7 apart on any single turn but rarely stay high
#: across several turns, so this separates them where a single threshold cannot
#: — while a genuinely over-split speaker (same voice, two ids) stays high and
#: still merges (a few turns later).
MERGE_MIN_OBS = int(os.environ.get("DICTATION_NOTES_MERGE_MIN_OBS", "3"))
MERGE_CONSEC = int(os.environ.get("DICTATION_NOTES_MERGE_CONSEC", "2"))

#: Silence run that closes a turn, and the energy floor that counts as
#: silence (matches ops_stream_whisper's digital-silence PEAK_GATE scale).
TURN_GAP_S = 0.7
SILENCE_PEAK = 0.008

#: SPEECH evidence for turn tracking (2026-08-27 commit-latency-tail fix).
#: The raw SILENCE_PEAK floor cannot see a speech pause under a raised
#: loopback noise floor: Zoom comfort noise / room tone peaks above 0.008
#: continuously (device quiet-room hold: peak 0.0351 with NO speech energy),
#: so no TURN_GAP_S run ever fires, every turn waits for the MAX_OPEN_TURN_S
#: force-close, and empty/fragment carries chain force-close windows into
#: 12.3 s commits — the measured on-device word-aligned latency tail
#: (P50 2.33 s but P90 7.5 s, max ~12 s; duration_s quantized to 6.15/12.3).
#: Fix: a chunk only counts as VOICE when, besides clearing the raw peak
#: floor, it carries speech energy by the sanctioned frame-RMS test
#: (audio_energy.has_speech_energy, SILENCE_RMS 0.004 — the same floor the
#: decode path already uses to REFUSE the audio, so anything the tracker now
#: calls silent could never have decoded in isolation anyway). Turn ends
#: therefore land on the last real speech chunk (the speech envelope), not
#: on the last loud room-tone chunk.
#:
#: DEFAULT OFF (measured, 2026-08-27 server A/B): on FAR-FIELD meeting audio
#: (AMI ES2004a) the same frame-RMS floor classifies quiet real speech as
#: silence, manufacturing extra awkward cuts whose windows choke — carries
#: went 4 -> 12 per 300 s and word-aligned P90 regressed 2.7 -> 5.0 s with
#: WER +0.9 pt; while on the REAL raised-floor loopback take
#: (take_225847_sys) it converted only 5 of 86 force-closes into pause
#: closes (the audio is genuinely continuous), so its upside was marginal
#: even where it was aimed. The commit-latency tail is owned by the carry
#: retry (ops_stream_whisper) instead. DICTATION_NOTES_TURN_SPEECH_RMS=1 opts a
#: raised-noise-floor deployment in.
def _turn_speech_rms_enabled() -> bool:
    return os.environ.get("DICTATION_NOTES_TURN_SPEECH_RMS", "0") == "1"

#: Turns shorter than this inherit the previous speaker instead of being
#: embedded (embeddings on <0.8s of speech are noise).
MIN_TURN_S = 0.8

#: CONTINUOUS audio (a video playing, back-to-back talkers) never produces
#: the silence gap the tracker waits for — turns froze open forever and
#: speaker labels never re-evaluated (field 2026-08-19: a nonstop far-end
#: video captioned an entire session under one label). An open turn is
#: force-closed at this length and a new one opens immediately, so every
#: window gets embedded and speaker changes are caught mid-stream.
MAX_OPEN_TURN_S = 6.0

SAMPLE_RATE = 16000


class OnlineSpeakerRegistry:
    """Running speaker centroids with cosine matching."""

    def __init__(self, embed_fn, sim_threshold: float = SIM_THRESHOLD,
                 max_speakers: int = 8, num_speakers=None):
        self._embed = embed_fn
        self._threshold = sim_threshold
        self._max = max_speakers
        self._centroids: list[np.ndarray] = []
        self._counts: list[int] = []
        self.last_embedding: np.ndarray | None = None
        #: (i,j) -> consecutive checks the pair has been above threshold; a
        #: merge needs MERGE_CONSEC in a row (evidence accumulation).
        self._merge_streak: dict[tuple[int, int], int] = {}
        # Known participant count (roster/meeting window, INCL. the local mic
        # user). The registry tracks only the REMOTE speakers, so the floor is
        # count-1. When known, check_merge never collapses below it — the
        # finalize merge already has this floor; the online registry LACKED it,
        # so live labels collapsed (device 2026-08-24: 3 remote speakers -> 1
        # because short-turn NeMo embeddings drift above 0.45 and nothing
        # stopped the merge). None => no floor (behaves as before).
        self.set_num_speakers(num_speakers)

    def set_num_speakers(self, num_speakers) -> None:
        """Update the known participant count mid-take (the app may learn it
        from the meeting window after the stream starts)."""
        try:
            self._min_remote = (max(1, int(num_speakers) - 1)
                                if num_speakers else None)
        except (TypeError, ValueError):
            self._min_remote = None

    @property
    def num_speakers(self) -> int:
        return len(self._centroids)

    def _active(self) -> int:
        return sum(1 for n in self._counts if n > 0)

    def check_merge(self) -> tuple[int, int] | None:
        """Retro-correction: as centroids accumulate evidence, two of them
        can drift together — the SAME person split into two ids by a noisy
        early turn (the dominant online-clustering error; the offline pass
        fixes it by re-clustering globally, live we merge centroids). Returns
        (kept, absorbed) when a pair crosses the join threshold; the caller
        relabels history. Absorbed slots stay as tombstones so ids of other
        speakers never shift.

        Count floor: with a known participant count, never merge below
        (num_speakers-1) active remote speakers — otherwise short-turn
        embeddings of DISTINCT people (which sit only ~0.4-0.7 apart, not the
        clean 0.1-0.2 of long clips) get fused and the live labels collapse."""
        if self._min_remote is not None and self._active() <= self._min_remote:
            return None
        for i in range(len(self._centroids)):
            # only STABLE speakers (enough accumulated evidence) may merge — a
            # 1-2-turn candidate is too noisy to decide identity.
            if self._counts[i] < MERGE_MIN_OBS:
                continue
            for j in range(i + 1, len(self._centroids)):
                if self._counts[j] < MERGE_MIN_OBS:
                    continue
                if float(np.dot(self._centroids[i], self._centroids[j])) >= self._threshold:
                    # sustained evidence: the pair must stay above threshold
                    # across MERGE_CONSEC consecutive checks, so one transient
                    # noisy-short-turn 0.7 cannot collapse two speakers.
                    k = self._merge_streak.get((i, j), 0) + 1
                    self._merge_streak[(i, j)] = k
                    if k < MERGE_CONSEC:
                        continue
                    n_i, n_j = self._counts[i], self._counts[j]
                    c = self._centroids[i] * n_i + self._centroids[j] * n_j
                    self._centroids[i] = c / (np.linalg.norm(c) + 1e-9)
                    self._counts[i] = n_i + n_j
                    self._counts[j] = 0  # tombstone
                    self._merge_streak.pop((i, j), None)
                    return (i, j)
                else:
                    # disagreement resets the streak — no lingering credit.
                    self._merge_streak.pop((i, j), None)
        return None

    def replace_state(self, centroids: list, counts: list) -> None:
        """Wholesale state replacement from a periodic offline re-clustering
        (the live-converges-to-offline loop): future turns are assigned
        against the re-clustered speakers, so new speech stays consistent
        with the relabeled history."""
        self._centroids = [np.asarray(c, dtype=np.float32) /
                           (np.linalg.norm(c) + 1e-9) for c in centroids]
        self._counts = [int(n) for n in counts]

    def assign(self, audio: np.ndarray) -> int:
        """Speaker index for one turn's audio; updates the centroid."""
        v = np.asarray(self._embed(audio), dtype=np.float32)
        v = v / (np.linalg.norm(v) + 1e-9)
        self.last_embedding = v  # exposed for identity (voice-profile) checks
        best, best_sim = -1, -1.0
        for i, c in enumerate(self._centroids):
            if self._counts[i] == 0:
                continue  # tombstone of a merged speaker
            sim = float(np.dot(v, c))
            if sim > best_sim:
                best, best_sim = i, sim
        if best >= 0 and best_sim >= self._threshold:
            n = self._counts[best]
            c = self._centroids[best] * n + v
            self._centroids[best] = c / (np.linalg.norm(c) + 1e-9)
            self._counts[best] += 1
            return best
        if len(self._centroids) >= self._max:
            return best if best >= 0 else 0  # saturated: nearest wins
        self._centroids.append(v)
        self._counts.append(1)
        return len(self._centroids) - 1


class TurnTracker:
    """Feeds on the live PCM stream; yields (start_s, end_s) turn spans on
    energy gaps. Chunk-based, no lookahead beyond the gap itself."""

    def __init__(self, turn_gap_s: float = TURN_GAP_S,
                 silence_peak: float = SILENCE_PEAK,
                 sample_rate: int = SAMPLE_RATE,
                 max_open_turn_s: float = MAX_OPEN_TURN_S,
                 speech_rms: bool | None = None):
        self._gap = float(os.environ.get("DICTATION_NOTES_TURN_GAP_S", turn_gap_s))
        self._floor = float(os.environ.get("DICTATION_NOTES_TURN_PEAK", silence_peak))
        self._sr = sample_rate
        self._max_open = max_open_turn_s
        self._speech_rms = (_turn_speech_rms_enabled()
                            if speech_rms is None else speech_rms)
        self._pos = 0.0          # stream clock (seconds fed so far)
        self._turn_start: float | None = None
        self._last_loud_end = 0.0
        self._silence_run = 0.0

    def feed(self, chunk: np.ndarray) -> list[tuple[float, float]]:
        """Returns turns CLOSED by this chunk (usually 0 or 1). A turn's
        end is where SPEECH stopped, not where the gap rule fired — the
        trailing silence belongs to nobody. "Speech" needs BOTH floors: the
        raw peak floor AND frame-RMS speech energy (see the
        DICTATION_NOTES_TURN_SPEECH_RMS doc above) — room tone above the peak
        floor must not hold a turn open past the speaker's real pause."""
        closed = []
        dur = len(chunk) / self._sr
        loud = len(chunk) > 0 and float(np.abs(chunk).max()) >= self._floor
        if loud and self._speech_rms:
            loud = audio_energy.has_speech_energy(chunk,
                                                  sample_rate=self._sr)
        if loud:
            if self._turn_start is None:
                self._turn_start = self._pos
            self._last_loud_end = self._pos + dur
            self._silence_run = 0.0
            if self._pos + dur - self._turn_start >= self._max_open:
                # Continuous speech: force-close this window and reopen so
                # labeling keeps re-evaluating without a silence gap. (The
                # 2026-08-27 duration_s/latency-tail findings around these
                # force-closes are fixed downstream: the frame-RMS speech
                # evidence above keeps room tone from feeding this chain,
                # ops_stream_whisper reports the turn's speech span, and a
                # choked boundary window carry-retries ~2.5 s later instead
                # of waiting out the next force-close.)
                closed.append((self._turn_start, self._pos + dur))
                self._turn_start = self._pos + dur
        else:
            self._silence_run += dur
            if self._turn_start is not None and self._silence_run >= self._gap:
                closed.append((self._turn_start, self._last_loud_end))
                self._turn_start = None
        self._pos += dur
        return closed

    @property
    def open_turn_start(self) -> float | None:
        """Stream-time start of the currently open (voiced) turn, or None
        while the tracker sits in silence. Read-only progress signal for
        the carry-retry scheduler (ops_stream_whisper): a retry is only
        worth decoding while speech is actually accruing."""
        return self._turn_start

    def flush(self) -> list[tuple[float, float]]:
        """Closes any open turn (stream end)."""
        if self._turn_start is None:
            return []
        span = (self._turn_start, self._last_loud_end)
        self._turn_start = None
        return [span]


class EnergyEnvelope:
    """Compact per-frame loudness timeline for one channel — the echo gate's
    reference. append() folds arbitrary chunks into FRAME_S peaks; loud_between
    answers "was this channel speaking anywhere in [t0, t1]?" in O(frames)."""

    FRAME_S = 0.05

    def __init__(self, speech_peak: float = 0.01,
                 sample_rate: int = SAMPLE_RATE):
        self._thr = speech_peak
        self._sr = sample_rate
        self._frame_n = int(self.FRAME_S * sample_rate)
        self._peaks: list[float] = []
        self._speech_frames = 0
        self._carry = np.zeros(0, dtype=np.float32)

    @property
    def seconds(self) -> float:
        return len(self._peaks) * self.FRAME_S

    @property
    def speech_seconds(self) -> float:
        """Cumulative seconds of frames above the speech floor — the live
        health signal for channel separation (a loopback endpoint that
        never carries speech means the mic cannot be trusted as You)."""
        return self._speech_frames * self.FRAME_S

    def append(self, chunk: np.ndarray) -> None:
        buf = np.concatenate([self._carry, chunk])
        n_frames = len(buf) // self._frame_n
        for i in range(n_frames):
            frame = buf[i * self._frame_n:(i + 1) * self._frame_n]
            peak = float(np.abs(frame).max())
            self._peaks.append(peak)
            if peak >= self._thr:
                self._speech_frames += 1
        self._carry = buf[n_frames * self._frame_n:]

    def loud_between(self, t0: float, t1: float) -> bool:
        a = max(0, int(t0 / self.FRAME_S))
        b = min(len(self._peaks), int(np.ceil(t1 / self.FRAME_S)))
        return any(p >= self._thr for p in self._peaks[a:b])


#: Reverb tail: speaker audio keeps bouncing around the room briefly after
#: the far end stops, so the mic gate extends this far past sys speech.
ECHO_TAIL_S = 0.3


def suppress_echo(mic: np.ndarray, sys_ch: np.ndarray,
                  speech_peak: float = 0.01,
                  tail_s: float = ECHO_TAIL_S,
                  sample_rate: int = SAMPLE_RATE) -> np.ndarray:
    """Half-duplex echo gate for the OFFLINE dual-channel pass: zero the mic
    wherever the system channel was speaking (plus a reverb tail). Loses the
    user's words during overlap — acceptable for speakers-on demos, robust
    without a real AEC. Both arrays share t=0 (recording start)."""
    if not len(mic) or not len(sys_ch):
        return mic
    frame_n = int(EnergyEnvelope.FRAME_S * sample_rate)
    tail_frames = int(np.ceil(tail_s / EnergyEnvelope.FRAME_S))
    n_frames = int(np.ceil(len(sys_ch) / frame_n))
    out = mic.copy()
    hot = -1  # last frame index where sys was loud
    for i in range(n_frames):
        frame = sys_ch[i * frame_n:(i + 1) * frame_n]
        if len(frame) and float(np.abs(frame).max()) >= speech_peak:
            hot = i
        if hot >= 0 and i - hot <= tail_frames:
            a = i * frame_n
            if a < len(out):
                out[a:a + frame_n] = 0.0
    return out


def trim_trailing_silence(audio: np.ndarray, floor: float = SILENCE_PEAK,
                          keep_s: float = 0.2,
                          sample_rate: int = SAMPLE_RATE) -> np.ndarray:
    """Drop the silent tail of a clip (keeping keep_s of padding). Whisper
    hallucinates pleasantries ("Thank you.") onto long trailing silence —
    the classic FG-6 shape; the accurate pass never sees this because its
    turns come from the segmenter, but the live finish() clip is raw."""
    if not len(audio):
        return audio
    frame = int(EnergyEnvelope.FRAME_S * sample_rate)
    last_loud = -1
    for i in range(0, len(audio), frame):
        if float(np.abs(audio[i:i + frame]).max()) >= floor:
            last_loud = i + frame
    if last_loud < 0:
        return audio[:0]
    return audio[:min(len(audio), last_loud + int(keep_s * sample_rate))]


def speech_spans(audio: np.ndarray, sample_rate: int = SAMPLE_RATE,
                 chunk_s: float = 0.2) -> list[tuple[float, float]]:
    """Energy-based speech spans over a whole clip — TurnTracker run
    offline. The dual-channel accurate pass uses this for the mic channel
    (one known person; a full diarization pass there bought nothing but
    finalize latency)."""
    tracker = TurnTracker(sample_rate=sample_rate)
    spans: list[tuple[float, float]] = []
    step = int(chunk_s * sample_rate)
    for off in range(0, len(audio), step):
        spans.extend(tracker.feed(audio[off:off + step]))
    spans.extend(tracker.flush())
    return spans
