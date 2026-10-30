"""Whisper-backed live captions: the SAME selected engine as the accurate pass,
re-decoded periodically. It is the only product live-ASR implementation.

The meeting-notes surface keeps live text on screen for the whole meeting, so
it must be readable on its own. Dictation uses the same implementation for its
temporary preview; only the released full-hold pass may be inserted.

Mechanics: pushes only buffer (never block — the wire contract's "one
frame out per frame in" rule still holds); background threads re-decode
each channel's open segment on the NPU (serialized by a module-wide engine
lock) and the freshest hypothesis rides back on the next push.

## Live speakers (dual-channel + online diarization)

`stream_push` carries an optional `"channel"`: `"mic"` (default, the local
user — label **You**) or `"sys"` (system loopback — everyone remote).
The sys channel runs energy-based turn tracking; each closed turn (≥0.8s)
is embedded (~27 ms on device) and matched against running speaker
centroids (`live_speakers.OnlineSpeakerRegistry`). A SPEAKER CHANGE
commits the open segment under the previous speaker and starts a new
decode segment at the boundary — so live lines read

    [You] let's look at the numbers
    [Speaker A] 我觉得没问题
    [Speaker B] agreed, shipping it

Committed lines interleave across channels in commit order; each channel's
current partial renders at the tail. Segment cuts at speaker boundaries
also help decode quality: whisper sees one voice per window.

Silence: segments whose peak stays under PEAK_GATE (true digital silence,
e.g. a muted mic) are never sent to the NPU — whisper hallucinates on
silence and an empty room must not caption itself.
"""

from __future__ import annotations

import base64
import os
import sys
import threading
import time
import traceback

import numpy as np

from . import live_speakers


def _dbg(msg: str) -> None:
    """Append-only debug trail — the C# host logs worker stderr at Debug
    level into a NullLogger by default, so failures in this path are
    otherwise invisible. GATED behind DICTATION_NOTES_DIAG=1: a release install
    must not quietly write transcript-adjacent breadcrumbs to disk."""
    if os.environ.get("DICTATION_NOTES_DIAG") != "1":
        return
    try:
        path = os.path.join(os.path.expanduser("~"), "dictation_live_debug.log")
        with open(path, "a", encoding="utf-8") as f:
            f.write(f"{time.strftime('%H:%M:%S')} {msg}\n")
    except Exception:
        pass


SAMPLE_RATE = 16000

#: Commit the open segment once it exceeds this — comfortably inside
#: whisper's 30 s window with headroom for the cut landing mid-word.
COMMIT_AFTER_S = 22.0

#: Do not start a new decode sooner than this after the last one finished.
DECODE_COOLDOWN_S = 0.8

#: Minimum new audio since the last decode before another is worth it.
MIN_NEW_AUDIO_S = 0.5

#: The paced Dictation preview's smallest decode window. On the Mac the
#: preview's first decode came at 0.6 s of audio: too short to hold a word,
#: it returned noise or nothing, and on quiet speech it ran Whisper's whole
#: temperature ladder (about 3-4 s here) while the final transcription of a
#: short hold waited behind it for the engine.
PREVIEW_MIN_WINDOW_S = 1.5

#: Peak below this = digital silence (muted mic); skip the NPU entirely.
#: Deliberately PEAK, not RMS, and deliberately tiny: FLEURS has real speech
#: at RMS 0.0004 that an RMS gate silently swallowed (2026-08-18 device run —
#: whole clips came back empty). Only true silence is worth gating; a rare
#: hallucinated caption on room tone loses to the accurate pass anyway.
PEAK_GATE = 0.002

#: Streaming AEC (speakers-on): the mic chunk's echo is cancelled against
#: the sys reference BEFORE turn tracking / decode (same FdafAec + env gate
#: DICTATION_NOTES_AEC as the accurate pass). Delay is re-estimated on a clock —
#: never assumed — and delay==0 keeps the whole path a bypass (headset).
AEC_EST_WINDOW_S = 10.0     # aligned recent window fed to estimate_delay
AEC_EST_INTERVAL_S = 60.0   # re-check cadence once a verdict exists
AEC_EST_RETRY_S = 10.0      # sys still silent: poll faster, no 60s burn
AEC_SAFETY_WINDOW_S = 5.0   # running in/out energy watch (the ADD-energy trap)
AEC_SAFETY_RATIO = 1.05     # out > 1.05x in over the window => bail out

#: One decode at a time across EVERY NPU user in the process — the two
#: caption channels, dictation holds, learning probes and the finalize all
#: share the Hexagon and must serialize, not race. The lock is the
#: process-wide `engine_lock.ENGINE_LOCK`; live-caption decodes take it in
#: the CAPTION class, so a queued dictation hold (HOLD class) is admitted
#: first. `.holder` is the hang watchdog's evidence (field 2026-08-19: live
#: decodes went silent 2min into a take while the worker process stayed
#: alive; a stuck NPU call under the lock starves every channel and
#: nothing logs).
from .engine_lock import CAPTION, ENGINE_LOCK as _ENGINE_LOCK  # noqa: E402

_ENGINE_LOCK_HOLDER: dict = _ENGINE_LOCK.holder

#: Upper bound on the audio fed to ONE live-caption engine call, in seconds.
#: The engine lock is held per call, so this is the longest a dictation hold
#: can wait behind captions: one decode of at most this much audio (plan
#: §2 principle 3, target <= 3 s p95 on turbo). A longer clip is decoded as
#: consecutive windows of at most this length, releasing the lock between
#: them, and the texts are joined; `COMMIT_AFTER_S` still decides WHEN a
#: monologue commits (22 s = two windows). Windows cut mid-word cost a
#: little caption accuracy at the seam — acceptable for a preview that the
#: finalize (`diarized_transcribe`) re-decodes whole; not acceptable would
#: be a hold waiting on a 22 s decode. Must stay below whisper's 30 s.
MAX_LIVE_DECODE_S = 12.0

#: LIVE-DECODE WINDOW CAP (FR-001 / P0-A, 2026-09-11). MAX_LIVE_DECODE_S bounds
#: one engine CALL, but a single live decode JOB used to process the WHOLE open
#: segment [seg_base, cut_s): `_transcribe2` split it into <=MAX_LIVE_DECODE_S
#: sub-calls yet the ONE job still decoded ALL of it. Under continuous
#: monologue the shared NPU stays busy, no commit fires, seg_base stalls, and
#: the open segment grows to MAX_OPEN_SEGMENT_S (device: 281 s) — so that one
#: job decoded ~15 chunks (~63 s of NPU at RTF 0.35). The single-threaded worker
#: main loop cannot answer `stream_push` while that job runs, so the C#
#: StreamTimeout (5 s) fires, the host kills+restarts the worker, and the fresh
#: worker faces the same window: ZERO live commits on monologue (paced speech,
#: which closes turns on pauses, was fine — 46 live turns). FIX: a LIVE decode
#: (the periodic carrier + the turn/force-close boundary) feeds the engine at
#: most MAX_LIVE_DECODE_S — the FRESHEST window; audio older than that is left
#: for the accurate finalize pass, which re-decodes the whole take (the
#: existing "live degraded, final recovers" design). Bounding the job also
#: breaks the death-spiral: a ~4 s job frees the decode slot, boundaries commit,
#: seg_base advances, and the window stays short. The finalize/`finish` drain
#: (which legitimately decodes the whole remaining open segment) is NOT capped.
#: DICTATION_NOTES_LIVE_DECODE_CAP=0 restores the pre-fix unbounded window byte-for-
#: byte (audit/rollback lever).
def _live_decode_cap_enabled() -> bool:
    return os.environ.get("DICTATION_NOTES_LIVE_DECODE_CAP", "1") != "0"

#: SWALLOW FIX (env DICTATION_NOTES_KEEP_EMPTY_WINDOW, default ON; =0 restores the
#: pre-fix drop-on-empty behavior exactly). A boundary re-decode of the isolated
#: turn window [seg_base, cut_s) sometimes returns EMPTY. The pre-fix code then
#: (1) committed that empty result — dropping the line — AND (2) advanced
#: seg_base past [a:b], DISCARDING the audio forever, even though the accurate
#: finalize re-decodes the whole take and recovers those words. Proven on
#: take_121356 (whisper-base on this dev box): the 13.4-19.8s window decoded
#: EMPTY alone but recovered ("Sometimes I think I'm a little torsie.") once it
#: rode into the next window. FIX: on an EMPTY boundary decode, do NOT advance
#: seg_base — leave the audio in the open segment so the NEXT boundary re-decodes
#: the superset window (whisper on a longer, context-richer window succeeds where
#: the short isolated cut failed). STRICTLY non-regressive: bounded by
#: EMPTY_CARRY_MAX_S, at which point we advance-and-drop exactly like the pre-fix
#: path, and we NEVER commit anything the pre-fix path wouldn't (only ever a real
#: non-empty decode). NOTE the recovery MAGNITUDE is engine-dependent: on
#: whisper-base some spans are undecodable at ANY window length (a base model-
#: quality floor), so this must be re-measured on the PRODUCTION large-v3-turbo
#: engine — there the empty-window rate is far lower and the recovery larger.
KEEP_EMPTY_WINDOW = os.environ.get("DICTATION_NOTES_KEEP_EMPTY_WINDOW", "1") != "0"
#: Cap on how far seg_base may lag behind cut_s while carrying empty windows
#: forward. Kept below MAX_LIVE_DECODE_S so a carried span is still ONE engine
#: window (no sub-split re-failure) and the lock-hold stays bounded; at the cap
#: we advance-and-drop (pre-fix behavior) so the open segment can never grow.
EMPTY_CARRY_MAX_S = 11.0

#: LONG-SESSION BOUND (soak fix C, 2026-08-25). The mutable open segment
#: [seg_base, now) is bounded in normal operation by turn-boundary commits and
#: COMMIT_AFTER_S — but nothing ever advanced seg_base across DIGITAL SILENCE
#: (peak < PEAK_GATE, e.g. a muted mic): no turn closes there, the
#: worth-decoding gate blocks every decode, and after an hour of mute the open
#: segment IS that hour, so every per-push tail check and every eventual
#: decode snapshot paid O(mute). Advancing seg_base over sub-PEAK_GATE audio
#: is semantics-preserving: _transcribe2 returns "" for it at any window
#: length (its very first gate), and the turn tracker's floor
#: (live_speakers.SILENCE_PEAK = 0.008) sits above PEAK_GATE, so no turn can
#: begin inside a trimmed region. The scan is budgeted per push (amortized
#: O(chunk)); it stops at the first block that clears PEAK_GATE, leaving up to
#: one block of leading silence as onset margin. No audio is DELETED — only
#: the mutable-window start moves; the full take stays buffered for the
#: diarizer and the accurate stop-time pass, exactly as before.
OPEN_TRIM_MIN_S = 30.0        # only trim once the open segment exceeds this
OPEN_TRIM_BLOCK_S = 0.5       # silence-scan granularity
OPEN_TRIM_BUDGET_BLOCKS = 40  # <= 20 s scanned per push (catch-up amortizes)

#: Hard freshness cap on the open segment, reachable only when decodes are
#: NOT keeping up with real (non-silent) audio — an NPU stall / starvation
#: state in which live captions already trail by minutes. The live path only
#: owes BOUNDED, FRESH decodes (the accurate full-take pass at meeting stop
#: owns the final transcript), so at this depth the oldest audio is dropped
#: from the LIVE decode window rather than letting every later decode
#: snapshot and boundary clip grow without bound. Far above every normal
#: bound (COMMIT_AFTER_S = 22, EMPTY_CARRY_MAX_S = 11), so it cannot fire in
#: healthy operation.
MAX_OPEN_SEGMENT_S = 180.0

#: SWALLOW FIX, VARIANT 2 — SHORT-FRAGMENT choke (field, 2026-08-24):
#: the isolated boundary window sometimes decodes not to "" but to a tiny
#: LEADING FRAGMENT (a stray conjunction — the classic "And" left on screen when
#: "And for the sake of the client…" was swallowed). whisper on an
#: awkwardly-cut multi-second window emits one token and stops; the pre-fix path
#: commits that fragment and advances seg_base, dropping the rest of the sentence
#: — same mechanism as the empty case, just non-empty. Detection MUST NOT eat a
#: legitimately short real turn ("Yeah" / "OK" / "对"): key on the WINDOW being
#: much longer than the decoded content — a multi-second window (>=
#: FRAGMENT_MIN_WINDOW_S) that yields <= FRAGMENT_MAX_CHARS of text is a choke,
#: whereas a ~0.6s window yielding "Yeah" is a real short turn and is kept. Gated
#: by the SAME env flag; DICTATION_NOTES_KEEP_EMPTY_WINDOW=0 disables both variants.
FRAGMENT_MIN_WINDOW_S = 2.5   # only windows at least this long can be a "choke"
FRAGMENT_MAX_CHARS = 6        # <= this many non-space chars from a long window

#: CARRY RETRY (2026-08-27 commit-latency tail). An empty/fragment carry
#: keeps seg_base put so the NEXT boundary re-decodes the superset — but
#: under CONTINUOUS speech the next boundary is a MAX_OPEN_TURN_S
#: force-close, a full window (~6.15 s) away, so every choke manufactured
#: one double-window commit (device word-aligned evidence: duration_s
#: 12.3-15.1 s lines at 7.5-9.5 s latency; base-QNN window replay on the
#: same take: 5.6% of blind force-close windows decode to ""/"of"/"the").
#: Instead of waiting a whole window, a carry re-arms a bounded RETRY
#: boundary once CARRY_RETRY_S more audio has streamed past the choked cut
#: — enough fresh right-hand context for whisper to recover (the
#: EMPTY_CARRY evidence), a window shorter by ~4 s. The retry only fires
#: while the tracker holds an OPEN turn (speech is actually accruing): in
#: silence, or with a real boundary already queued, the existing recovery
#: paths own the audio exactly as before. ONE retry per carry chain
#: (MAX_CARRY_RETRIES): the fragment heuristic cannot tell a choked decode
#: from a real filler utterance ("So...", "Okay.", "um"), and on AMI
#: far-field audio an uncapped retry re-armed on genuine filler every
#: 2.5 s — pure engine churn that queued everyone else's decodes (measured
#: 2026-08-27 server A/B). One retry captures the device win (chokes recover
#: WITH context); a still-choked chain falls back to the pre-fix
#: wait-for-the-real-boundary, bounded as ever by EMPTY_CARRY_MAX_S's
#: advance-and-drop. =0 disables.
MAX_CARRY_RETRIES = 1


def _carry_retry_s() -> float:
    try:
        return float(os.environ.get("DICTATION_NOTES_CARRY_RETRY_S", "2.5"))
    except (TypeError, ValueError):
        return 2.5

#: Minimum fraction of a rolling stable span's duration the BEST-overlapping
#: diarizer turn must cover before its label replaces the span's channel
#: default. Why ~0.3: the rolling span boundaries are whisper timestamp-token
#: estimates (decoder guesses, routinely off by up to ~1 s at the edges of a
#: 30 s window) and the online diarizer's turns are energy-turn spans that
#: regularly clip a span's head or tail — so a turn overlapping well under a
#: third of the span is evidence about an EDGE of the span, not its body.
#: Adopting such a sliver's label renamed whole 10 s spans after a 0.5 s
#: clip. 0.3 keeps genuine majority/plurality owners (a 2-speaker span split
#: near the middle still clears it on the larger side) while pushing
#: edge-slivers back to the honest channel default ("Them").
#: (the stream contract 2026-08-24, agent W task 2a.)
MIN_ATTR_OVERLAP_RATIO = 0.3

#: A diarizer turn must overlap a rolling span by at least this many seconds
#: to own words in the word-level split — the same ≥0.8 s floor the live
#: path uses before a closed turn is embedded at all (see the module
#: docstring): anything shorter was never independently voiced evidence.
WORDSPLIT_MIN_TURN_OVERLAP_S = 0.8

#: COMMIT-ON-STABLE (2026-08-27, Pokee feed latency): a line used to commit
#: only when its energy TURN closed (~0.7 s pause + a boundary re-decode of
#: the whole open window), putting spoken→committed at 3-8 s. Now the
#: periodic live decode asks the engine for whisper's native timestamp
#: segments, and once TWO consecutive decodes of the same open window agree
#: on a leading segment run (LocalAgreement), that run is committed
#: immediately — the turn machinery still owns speaker boundaries and the
#: final remainder. Cut points are quality-aware: only at a segment that
#: ends in punctuation or is followed by a ≥ STABLE_PAUSE_S gap, never
#: within STABLE_TAIL_GUARD_S of the window edge (the decoder must have
#: heard real right-hand context past the cut), never below the minimum
#: line length (no two-word fragments), and the audio cut is snapped to the
#: quietest 25 ms frame after the segment so the remainder window cannot
#: open mid-word. seg_base advances to the cut exactly like a boundary
#: commit, so the open window stays SHORT (faster decodes — the second
#: latency term) and the eventual turn-close decode covers only the
#: remainder (no duplicated words; a fully-consumed turn hits the existing
#: stale-cut guard). DICTATION_NOTES_COMMIT_ON_STABLE=0 restores turn-close-only
#: commits byte-for-byte; engines without transcribe_timed keep the old
#: path automatically.
STABLE_MIN_INTERVAL_S = 2.0   # per-channel stable-commit rate cap (feed rate)
STABLE_MIN_WORDS = 3          # no two-word fragment lines
STABLE_MIN_CHARS = 12         # non-space chars (non-CJK)
STABLE_MIN_CHARS_CJK = 6      # 6 CJK chars is a real clause; 12 is a speech
STABLE_TAIL_GUARD_S = 0.35    # cut must precede the window edge by this
STABLE_PAUSE_S = 0.5          # inter-segment gap that counts as a boundary
STABLE_SNAP_MAX_S = 0.4       # how far the audio cut may slide forward
STABLE_SNAP_FRAME_S = 0.025   # energy-trough scan granularity
STABLE_TS_TOL_S = 0.5         # timestamp agreement tolerance across decodes
STABLE_PUNCT = ".!?;。！？；…"


def _registry_floor_forced() -> bool:
    """Keep the greedy registry's known-k merge floor even when a corrector
    (DZ bank or blockonline) is active (measurement override, 2026-08-28)."""
    return os.environ.get("DICTATION_NOTES_REGISTRY_FLOOR", "0") == "1"


def _commit_on_stable_enabled() -> bool:
    """Default ON; =0 restores the pre-change turn-close-only commits."""
    return os.environ.get("DICTATION_NOTES_COMMIT_ON_STABLE", "1") != "0"


def _single_emission_enabled(env=None) -> bool:
    """The master live-caption mode (plan 2026-09-01-single-emission-diar):
    a line leaves ONCE after the D=H hold and is immutable. Default OFF (=1
    to enable). Centralised so the default is a tested contract, not three
    scattered string compares — a silent default flip broke live captions in
    the field (2026-09-09)."""
    e = os.environ if env is None else env
    return e.get("DICTATION_NOTES_SINGLE_EMISSION") == "1"


def _turn_geometry_local(env=None) -> bool:
    """Turn geometry source. Default "local" (2026-09-09 flip): turns from
    LOCAL segmentation activity, immune to global-recluster churn — the legacy
    path STARVED on real meetings (0 live commits over 25 min). "legacy"
    restores the old path for A/B. Centralised so the flip is a tested
    contract."""
    e = os.environ if env is None else env
    return e.get("DICTATION_NOTES_TURN_GEOMETRY", "local").lower() == "local"


#: Character budget for the greedy path's <|startofprev|> vocabulary prompt.
#: ``prompt_ids`` hard-caps at 96 TOKENS by keeping the LAST 96 — so an
#: uncapped join of a big vocabulary file was silently truncated MID-PHRASE
#: and dropped the EARLIEST terms unpredictably (a dangling half-term in the
#: prompt biases toward wrong forms). 320 weighted chars stays safely under
#: 96 tokens (EN averages ~4 chars/token; a CJK char is ~a token, weighted
#: 4x below), so the tail-truncation can no longer fire and the included
#: set is a predictable front-prefix of whole phrases. The beam trie and
#: the regurgitation guard keep the FULL list — neither has a token budget.
PROMPT_CHAR_BUDGET = 320

#: CONTEXT CARRY (DICTATION_NOTES_CTX_CARRY=1): the previous committed line's
#: tail rides in the greedy decode's initial_prompt AFTER the vocabulary
#: prompt — whisper's native conditioning contract (the model was trained
#: to continue a preceding transcript). The streaming worker otherwise
#: decodes every window context-free, which measurably loses continuity at
#: commit-span edges (AMI replay: deletions cluster at boundaries). Capped
#: so vocab(320 ch ~= 96 tok) + tail stays under whisper's 224-token
#: prompt window; the tail keeps the line's END (most recent context).
#: CERTIFICATION: REJECTED — default stays OFF. The N=5 A/B
#: (the context experiment, 2026-08-27) showed ES2004a WER +2.2 pt outside noise
#: under the certified commit-on-stable cadence, plus one observed
#: parroted continuation; the feature-commit single-run gain did not
#: replicate. Do not flip the default without a new A/B.
CTX_CARRY_MAX_CHARS = 160


def _ctx_carry_enabled() -> bool:
    return os.environ.get("DICTATION_NOTES_CTX_CARRY", "0") == "1"


#: INPUT AGC (DICTATION_NOTES_INPUT_AGC=1): level quiet-but-clear input up toward a
#: normal speech RMS before turn tracking and decode. Whisper's decode AND the
#: diarizer's embeddings are both mildly level-sensitive (device 2026-08-30:
#: clean LibriSpeech attenuated to 0.15x = WER 10.5%->12.5% PLUS a spurious 2nd
#: speaker); normalising recovers both and lets the fixed turn gate see a
#: soft-spoken remote at a normal level. SAFETY: the gain is driven ONLY by
#: blocks whose RMS already clears INPUT_AGC_SPEECH_RMS (itself above the
#: has_speech_energy floor SILENCE_RMS=0.004), so AGC only makes speech the gate
#: WOULD ALREADY admit louder — it never lifts the comfort-noise/silence floor
#: over the gate (which would hallucinate). During silence the gain RELEASES
#: back to unity. It never attenuates already-loud speech (gain floored at 1.0),
#: so normal-level audio is a strict no-op. Default OFF; needs a fresh A/B
#: (like CTX_CARRY) before the default may flip.
INPUT_AGC_TARGET_RMS = 0.05
INPUT_AGC_MAX_GAIN = 12.0
INPUT_AGC_SPEECH_RMS = 0.006   # block RMS above this = real speech -> adapt gain
INPUT_AGC_ATTACK = 0.4         # per ~200 ms push, toward the speech target
INPUT_AGC_RELEASE = 0.10       # per push, back to unity during silence


def _input_agc_enabled() -> bool:
    return os.environ.get("DICTATION_NOTES_INPUT_AGC", "0") == "1"


class InputAgc:
    """Streaming input leveller, one per channel (see INPUT_AGC_* notes)."""

    def __init__(self) -> None:
        self.gain = 1.0
        # env-tunable operating point (sweeps without code edits)
        self.target = float(os.environ.get("DICTATION_NOTES_AGC_TARGET", INPUT_AGC_TARGET_RMS))
        self.speech_rms = float(os.environ.get("DICTATION_NOTES_AGC_SPEECH_RMS", INPUT_AGC_SPEECH_RMS))
        self.max_gain = float(os.environ.get("DICTATION_NOTES_AGC_MAX_GAIN", INPUT_AGC_MAX_GAIN))

    def process(self, samples: np.ndarray) -> np.ndarray:
        if samples is None or not len(samples):
            return samples
        rms = float(np.sqrt(np.mean(samples.astype(np.float32) ** 2)))
        if rms > self.speech_rms:
            desired = min(self.target / rms, self.max_gain)
            if desired < 1.0:
                desired = 1.0        # only boost quiet speech, never attenuate
            self.gain += (desired - self.gain) * INPUT_AGC_ATTACK
        else:
            self.gain += (1.0 - self.gain) * INPUT_AGC_RELEASE
        if self.gain <= 1.0 + 1e-6:
            return samples
        return np.clip(samples * self.gain, -1.0, 1.0).astype(np.float32)


def _vocab_prompt(bias_phrases) -> str | None:
    """Dedup (order-preserving) + whole-phrase cap under the token budget."""
    seen, out, cost = set(), [], 0
    for raw in bias_phrases or ():
        phrase = str(raw).strip()
        key = phrase.lower()
        if not phrase or key in seen:
            continue
        seen.add(key)
        # >= U+2E80 covers CJK ideographs AND kana/hangul/compat blocks —
        # all ~1 token per char, so all weighted 4x against the budget.
        w = sum(4 if ord(ch) >= 0x2E80 else 1 for ch in phrase)
        if out and cost + 2 + w > PROMPT_CHAR_BUDGET:
            break
        cost += (2 if out else 0) + w
        out.append(phrase)
    return ", ".join(out) if out else None


def _wordsplit_enabled() -> bool:
    """DICTATION_NOTES_WORDSPLIT=1 splits a rolling sys span that crosses diarizer
    speaker turns into per-speaker sub-spans (attribute.py's word-level
    max-overlap + merge-consecutive, live on macOS, ported here).

    DEFAULT OFF — and deliberately so: `WhisperQnn.transcribe_timed` yields
    only whisper's native timestamp-TOKEN segments (start, end, text, lang);
    there are NO per-word timestamps on this path (verified 2026-08-24
    against whisper_qnn.py:797ff — the token loop closes a span per
    timestamp-token pair, never per word). The split therefore has to
    ESTIMATE word midpoints by character-weight interpolation across the
    span, which is honest enough to opt into but not enough to default on
    (the plan gates the default-ON on real word timestamps existing). With
    the flag off, attribution stays exactly today's majority-overlap span
    labeling."""
    return os.environ.get("DICTATION_NOTES_WORDSPLIT") == "1"


_CJK_CHAR = None  # compiled lazily; zh text has no spaces to tokenize on


def _span_tokens(text: str) -> list[tuple[int, int]]:
    """Whole-word token offsets [(char_start, char_end)] for a span's text:
    whitespace-delimited chunks, with runs of CJK characters split into
    single-character tokens (a zh sentence is one whitespace chunk)."""
    global _CJK_CHAR
    import re
    if _CJK_CHAR is None:
        _CJK_CHAR = re.compile(r"[㐀-鿿豈-﫿]")
    out: list[tuple[int, int]] = []
    for m in re.finditer(r"\S+", text):
        chunk, base = m.group(), m.start()
        run = 0
        for j, ch in enumerate(chunk):
            if _CJK_CHAR.match(ch):
                if run < j:
                    out.append((base + run, base + j))
                out.append((base + j, base + j + 1))
                run = j + 1
        if run < len(chunk):
            out.append((base + run, base + len(chunk)))
    return out


def _split_span_at_turn_boundaries(span: dict, sys_meta: list[dict]) -> list[dict] | None:
    """Split one rolling sys span at diarizer turn boundaries, keeping WHOLE
    words with the side owning each word's midpoint (attribute.py port —
    its `assign_speaker` does the per-word max-overlap with nearest-turn
    fallback; the merge-consecutive grouping here mirrors its `attribute`,
    but slices the ORIGINAL text by character offsets so zh spacing and
    punctuation survive instead of being re-joined with spaces).

    Word times are ESTIMATED: transcribe_timed carries no word timestamps,
    so each word's span is interpolated across the ASR span proportional to
    its character weight — whole words are never cut (a proportional cut of
    the raw text could land mid-word; this cannot). Returns the sub-spans,
    or None when the span does not genuinely cross ≥2 speakers' turns (the
    caller then labels it whole, as today)."""
    from .attribute import assign_speaker
    turns = []
    for m in sys_meta:
        ov = min(span["end"], m["end"]) - max(span["start"], m["start"])
        if ov >= WORDSPLIT_MIN_TURN_OVERLAP_S:
            turns.append({"start": float(m["start"]), "end": float(m["end"]),
                          "speaker": m["speaker"]})
    if len({t["speaker"] for t in turns}) < 2:
        return None
    text = span["text"]
    tokens = _span_tokens(text)
    if len(tokens) < 2:
        return None
    total_w = sum(b - a for a, b in tokens)
    if total_w <= 0:
        return None
    dur = span["end"] - span["start"]
    if dur <= 0:
        return None
    # estimated word times: cumulative character weight across the span
    words, acc = [], 0
    for a, b in tokens:
        w_start = span["start"] + dur * (acc / total_w)
        acc += b - a
        w_end = span["start"] + dur * (acc / total_w)
        words.append({"c0": a, "c1": b, "start": w_start, "end": w_end})
    # per-word owner: attribute.py's max-overlap rule over the estimated
    # word span (with its nearest-turn fallback for words in a gap). Under
    # a uniform estimate, majority-overlap IS "the side owning the word's
    # midpoint" — and a whole word is never cut at the boundary.
    for w in words:
        w["speaker"] = assign_speaker(
            {"word": text[w["c0"]:w["c1"]],
             "start": w["start"], "end": w["end"]}, turns)
    # merge consecutive same-speaker words (attribute.attribute's grouping)
    pieces: list[dict] = []
    for w in words:
        if pieces and pieces[-1]["speaker"] == w["speaker"]:
            pieces[-1]["c1"] = w["c1"]
            pieces[-1]["end"] = w["end"]
        else:
            pieces.append(dict(w))
    if len({p["speaker"] for p in pieces}) < 2:
        return None
    out = []
    for p in pieces:
        sub_text = text[p["c0"]:p["c1"]].strip()
        if not sub_text:
            continue
        sub = dict(span)
        sub.update({"start": round(p["start"], 3), "end": round(p["end"], 3),
                    "text": sub_text, "speaker": p["speaker"]})
        out.append(sub)
    return out if len(out) >= 2 else None


def _get_engine():
    """Find the initialized engine without importing a second server instance."""
    for name in ("__main__", "transcription_worker.asr_server", "asr_server"):
        m = sys.modules.get(name)
        if m is not None and getattr(m, "_engine", None) is not None:
            return m._engine
    return None


def _loop_junk(text: str, hallucination_guard: bool = True) -> bool:
    """Repetition-loop hallucination check (fail-soft when the engine
    module is unavailable in tests). Shared: hallucination.loop_junk, so
    the rolling stable transcript applies the identical verdict."""
    from . import hallucination
    return hallucination.loop_junk(text, hallucination_guard=hallucination_guard)


def _join_text(left: str, right: str) -> str:
    """Use the same CJK seam rule as final recognition on every platform."""
    from . import whisper_mac
    return whisper_mac._join_span_texts(left, right)


def _spk_letter(idx: int) -> str:
    """Cluster/output speaker index -> the product letter namespace
    ("Speaker A".."Speaker Z", then plain "Speaker <n>"). Same mapping as
    the registry's letter() closure in _Session.__init__."""
    return f"Speaker {chr(ord('A') + idx)}" if idx < 26 else f"Speaker {idx}"


class _CommitLog:
    """Committed caption lines, shared by both channels, in commit order.
    Commit is the live stream's "final" moment, so the same engine-agnostic
    correction layer the accurate pass runs (spoken-alias rewrites) applies
    HERE — a Pokee-style consumer of committed turns gets corrected company
    terms without waiting for the post-meeting pass."""

    def __init__(self):
        self.lock = threading.Lock()
        self.lines: list[str] = []
        # per-line metadata, parallel to `lines`: speaker/channel plus the
        # AUDIO-time span the text came from — the periodic re-diarization
        # relabels history by overlapping these spans with fresh clusters.
        self.line_meta: list[dict] = []
        # Monotone counter bumped whenever line_meta SPEAKERS mutate in place
        # (relabel / set_speakers). stable_snapshot's attribution cache keys
        # on it: appends are already visible via len(line_meta), only in-place
        # relabels need an explicit signal. Internal — never a wire field.
        self.meta_version = 0
        self._recent_sys: list[str] = []
        # normalized vocabulary prompt — a decode that is just (a chunk of)
        # the bias vocabulary is prompt regurgitation on noise, not speech
        # (field 2026-08-19: live captions "reciting" the tennis terms).
        self.vocab_norm = ""
        self.vocab_phrases: list = []
        self.vocab_phonetic = None  # zh pinyin term recovery (hot words)
        self.vocab_phonetic_en = None  # EN phoneme term recovery (the L3 layer)
        # Meeting captions filter stock sign-offs; Dictation preview must
        # match final transcription, which passes hallucination_guard=False.
        self.hallucination_guard = True
        # Structured commit events for the partner feed (Pokee): every
        # committed line, in order, with a consume cursor. Append-only —
        # a consumer may already have emitted a line, so relabel() does
        # NOT rewrite these; live speaker labels are provisional by
        # contract (the accurate pass owns the final transcript).
        self.feed_events: list[dict] = []
        self._feed_cursor = 0
        # Structured relabel events for the wire (the stream contract
        # 2026-08-24): every retroactive speaker correction of a committed
        # SYS line, in order, with its own drain cursor — same discipline as
        # feed_events/commits[]. `index` is the commit-log line index being
        # corrected (the commit log is append-only, so the index is stable
        # forever). Mic lines are pinned "You" by contract and never emit a
        # relabel. A consumer that never reads `relabels` sees exactly
        # today's behavior; the two cursors are independent, so a relabel
        # for a commit not yet drained is legal.
        self.relabel_events: list[dict] = []
        self._relabel_cursor = 0
        # SINGLE-EMISSION MODE (DICTATION_NOTES_SINGLE_EMISSION=1; set True by
        # _Session.__init__ when the flag is on, default False = legacy,
        # byte-identical). In mode, sys-channel commits from the internal
        # ASR/turn machinery become text CARRIERS: they land in lines/
        # line_meta (the emission hold's text source) but are SUPPRESSED
        # from feed_events; only hold-released final lines (add with
        # feed_only=True) reach the partner feed, and the retro-relabel
        # surface (relabel/set_speakers) is inert so relabel_events stays
        # PERMANENTLY empty. Mic ("You") lines flow exactly as before.
        self.single_emission = False
        # Presentation mode (env-gated, default None => inert). Installed by
        # presentation_mode.PresentationSession.attach() from start().
        self.presentation = None
        try:
            from . import spoken_aliases
            self._aliases = spoken_aliases.load()
            self._alias_pattern = (spoken_aliases.compile_pattern(self._aliases)
                                   if self._aliases else None)
            self._alias_apply = spoken_aliases.apply
        except Exception:  # noqa: BLE001 — aliases are an enhancement
            self._aliases, self._alias_pattern, self._alias_apply = {}, None, None

    @staticmethod
    def _norm(text: str) -> str:
        import re
        return re.sub(r"[\W_]+", "", text.lower())

    @staticmethod
    def _similar(a: str, b: str) -> bool:
        # NEAR-EXACT only. The audio AEC (FdafAec) already removes the bulk of
        # speaker->mic echo; this text pass is a strict BACKSTOP for residual
        # echo, not a general dedup. It used to fire at ratio 0.75 + any 8-char
        # containment, which deleted whole REAL sentences that merely resembled
        # or were a fragment of a sys line — on fast multi-speaker / podcast
        # audio that "swallowed" the user's own speech (field 2026-08-21).
        # Mac trusts its audio AEC and never text-deletes; we keep only a tight
        # backstop so real speech survives.
        if not a or not b:
            return False
        if a == b:
            return True
        if a in b or b in a:
            lo, hi = min(len(a), len(b)), max(len(a), len(b))
            return lo >= 16 and lo >= 0.6 * hi   # a true echo ~= the whole line
        import difflib
        return difflib.SequenceMatcher(None, a, b).ratio() >= 0.88

    def is_echo_of_sys(self, text: str) -> bool:
        """True when a mic-channel text matches something the SYS channel
        recently produced — the far end's echo transcribes to (nearly) the
        same words, so duplicates are removed here in TEXT space instead of
        muting audio (which ate the user's real speech under a
        continuously-loud far end)."""
        norm = self._norm(text)
        with self.lock:
            recent = list(self._recent_sys)
        return any(self._similar(norm, r) for r in recent)

    _SENTENCE_SPLIT = None

    def excise_echo(self, text: str) -> str:
        """SENTENCE-level echo removal for mixed mic segments. With no audio
        gate, an intermittent talker's segment holds BOTH their words and
        the far end's — whole-line dedup deleted the user's words along
        with the echo (field: '断断续续说话还是有点删'). Sentences matching
        recent sys text are excised; the user's sentences survive."""
        import re
        if self._SENTENCE_SPLIT is None:
            type(self)._SENTENCE_SPLIT = re.compile(r"(?<=[。.!?!?;;\n])\s*")
        parts = [p for p in self._SENTENCE_SPLIT.split(text) if p.strip()]
        if len(parts) <= 1:
            return "" if self.is_echo_of_sys(text) else text
        with self.lock:
            recent = list(self._recent_sys)
        kept = [p for p in parts
                if not any(self._similar(self._norm(p), r) for r in recent)]
        return " ".join(kept).strip()

    def is_vocab_echo(self, text: str) -> bool:
        """True when the text is (a rearrangement/repetition of) the bias
        vocabulary: >=90% of its word pieces are EXACT vocabulary word
        pieces AND the line has list shape — a repeated piece
        ("桑普拉斯, 桑普拉斯") or >=3 distinct pieces (a bare term list).
        Regurgitation repeats vocab items with separators, so containment
        alone misses it. A real sentence USING the terms has plenty of
        non-vocab words and survives.

        Two false-positive classes fixed 2026-08-28 (QA on a tester's real
        speech — the tester SAYS the product terms):
        - membership used to be a SUBSTRING test against the concatenated
          vocab blob, so "on" counted as covered ("snapdragON") and the
          real fragment "<product> on Snapdragon" was eaten;
        - a 2-piece fragment of distinct terms ("the accelerator vendor Snapdragon") is
          an ordinary noun phrase, not a recitation — the live pipeline
          commits fragments constantly."""
        if not self.vocab_norm:
            return False
        import re as _re
        pieces = [w for w in _re.split(r"[\W_]+", text.lower()) if w]
        if len(pieces) < 2:
            return False
        vocab_pieces = set()
        for p in self.vocab_phrases:
            vocab_pieces.update(
                w for w in _re.split(r"[\W_]+", str(p).lower()) if w)
        if not vocab_pieces:
            return False
        covered = sum(1 for w in pieces if w in vocab_pieces)
        if covered / len(pieces) < 0.9:
            return False
        distinct = len(set(pieces))
        return distinct < len(pieces) or distinct >= 3

    def note_sys_text(self, text: str) -> None:
        norm = self._norm(text)
        if not norm:
            return
        with self.lock:
            self._recent_sys.append(norm)
            if len(self._recent_sys) > 12:
                self._recent_sys.pop(0)

    def gate(self, speaker: str, text: str, channel: str = "sys") -> str:
        """THE commit-text gate (unified 2026-08-28). Every caption line —
        legacy turn-close, commit-on-stable, boundary-cache reuse,
        carry-retry, finish drain/tail — must pass here before it can be
        committed or emitted; `add` (the ONLY appender, enforced by
        test_commit_gate's AST ratchet) consults it first, so a future
        decode path cannot emit unguarded text by construction. The chain
        and its order are exactly the previously-shipped `add` guards:
        junk/hallucination drop, vocabulary-echo drop, repetition-loop
        drop, vocab-run excision, loop-tail collapse, trailing-pleasantry
        strip, echo bookkeeping/excision, phonetic + alias + zh_norm cleanup,
        presentation snap/repeat-suppression.
        Returns the cleaned text, or "" to DROP the line."""
        text = (text or "").strip()
        if text and os.environ.get("DICTATION_NOTES_GATE_TRACE") == "1":
            _dbg(f"GATE_RAW\t{channel}\t{text}")   # what whisper PRODUCED, pre-gate
        from . import hallucination
        if text and self.hallucination_guard and hallucination.is_junk_utterance(text):
            _dbg(f"drop-audit: junk_utterance [{speaker}] {text[:60]!r}")
            return ""
        if text and self.is_vocab_echo(text):
            _dbg(f"drop-audit: vocab-echo [{speaker}] {text[:60]!r}")
            return ""
        if text and _loop_junk(text, hallucination_guard=self.hallucination_guard):
            _dbg(f"drop-audit: loop-junk [{speaker}] {text[:60]!r}")
            return ""
        if self.vocab_phrases:
            text, _n = hallucination.strip_vocab_run(text, self.vocab_phrases)
            if _n:
                _dbg(f"drop-audit: vocab-run excised {_n} terms [{speaker}]")
            if not text:
                return ""
        collapsed = hallucination.collapse_phrase_loops(text)
        if collapsed != text:
            _dbg(f"drop-audit: loop-tail collapse [{speaker}] "
                 f"{len(text)}->{len(collapsed)} ch")
        text = collapsed
        if not text:
            return ""
        # Trailing pleasantries ride on the END of real utterances too
        # ("...明天发 Thank you." — field, recurring). Strip at every
        # commit; a caption stream losing a rare genuine trailing
        # "thank you" costs less than fabricated ones appearing nightly.
        if self.hallucination_guard:
            stripped = hallucination.strip_trailing(text)
            if stripped != text:
                _dbg(f"drop-audit: trailing-strip [{speaker}] {text[-40:]!r} -> {stripped[-40:]!r}")
            text = stripped
            if not text:
                return ""
        if channel == "sys":
            self.note_sys_text(text)
        elif os.environ.get("DICTATION_NOTES_ECHO_EXCISE", "0") == "1":
            # DEFAULT OFF (2026-08-21): without earphones the user's OWN
            # voice lands on the sys loopback too, so text-space echo removal
            # deleted whole real sentences from the mic ("You") channel — see
            # drop-audit 'echo-excise [You] 46->0 removed ...'. The demo uses
            # earphones (sys = far-end only, no echo to excise), so gating this
            # off costs nothing there and, without earphones, trades an
            # occasional duplicate line for never deleting the user's speech.
            excised = self.excise_echo(text)
            if excised != text:
                _dbg(f"drop-audit: echo-excise [{speaker}] {len(text)}->{len(excised)} ch: "
                     f"removed {text[:50]!r}..." if not excised else
                     f"drop-audit: echo-excise [{speaker}] {len(text)}->{len(excised)} ch")
            text = excised
            if not text:
                return ""  # nothing but far-end echo in this line
        if self.vocab_phonetic is not None:
            text, fixed = self.vocab_phonetic.apply(text)
            if fixed:
                _dbg(f"vocab-phonetic [{speaker}] {fixed}")
        if self.vocab_phonetic_en is not None:
            text, fixed = self.vocab_phonetic_en.apply(text)
            if fixed:
                _dbg(f"vocab-phonetic-en [{speaker}] {fixed}")
        if self._aliases and self._alias_apply is not None:
            try:
                text, _ = self._alias_apply(text, self._aliases, self._alias_pattern)
            except Exception:  # noqa: BLE001
                pass
        from . import zh_norm
        text = zh_norm.to_simplified(text)
        # NO stream_repetition.clean here (removed 2026-08-28 QA): that
        # module is sherpa-PREVIEW cosmetics ("display only — never touches
        # the transcript", its own contract) and on committed whisper text
        # it collapsed real Chinese reduplication (谢谢大家→谢大家,
        # 好好聊聊→好聊) and lowercased real acronyms/pronouns (NPU→npu,
        # mid-sentence I→i). Whisper loop hallucinations remain covered by
        # collapse_phrase_loops + the loop-junk verdict above.
        if self.presentation is not None:
            # env-gated script snap, AFTER all cleanup passes; returns the
            # text unchanged unless the line aligns to the loaded script
            text = self.presentation.snap_line(text)
            if self.presentation.suppress_repeat(channel, text):
                # consecutive script-line echo (the low-signal decode loop
                # re-emitting the script bypasses the vocab guards by
                # design — see presentation_mode (c)): commit once, drop
                # the repeats. Never fires outside presentation mode.
                _dbg(f"drop-audit: presentation-repeat [{speaker}] {text[:60]!r}")
                return ""
        return text

    def add(self, speaker: str, text: str, channel: str = "sys",
            span: tuple | None = None, feed_only: bool = False):
        """Commit one caption line. The ONLY appender to lines/line_meta/
        feed_events (test_commit_gate's AST ratchet) — and its first act is
        the gate, so no commit path can emit unguarded text.

        feed_only=True (single-emission mode only): the line is a hold-
        RELEASED final line — append it to the partner feed alone, not to
        lines/line_meta (its text is a join of carrier lines already
        committed there; re-adding would double the transcript)."""
        text = self.gate(speaker, text, channel=channel)
        if not text:
            return
        with self.lock:
            if not feed_only:
                self.lines.append(f"[{speaker}] {text}")
                self.line_meta.append({
                    "speaker": speaker, "channel": channel,
                    "start": span[0] if span else None,
                    "end": span[1] if span else None})
                if self.single_emission and channel == "sys":
                    # single-emission: internal sys lines are text CARRIERS
                    # for the emission hold — they must never reach the
                    # partner feed themselves (mic lines keep flowing).
                    return
            self.feed_events.append({
                "speaker": speaker, "text": text, "channel": channel,
                # Audio-time span of the SPEECH itself, seconds from stream
                # start (same span guard as line_meta above). The partner
                # feed derives line.spoken_at / line.duration_s from these,
                # so a commit carries BOTH clocks: when it was said
                # (start/end) and when it finalized (committed_at).
                # null = span unknown.
                "start": span[0] if span else None,
                "end": span[1] if span else None,
                "committed_at": time.time()})

    def drain_feed(self) -> list[dict]:
        """Commit events appended since the last drain (wire-reply cursor)."""
        with self.lock:
            out = self.feed_events[self._feed_cursor:]
            self._feed_cursor = len(self.feed_events)
            return list(out)

    def relabel(self, old_speaker: str, new_speaker: str):
        """Retroactive speaker correction — rewrites history in place; the
        next display() shows the converged labels. Each changed SYS line is
        also appended to `relabel_events` so the wire can carry the
        correction (mic lines are pinned "You" and never emit one)."""
        if self.single_emission:
            # Single-emission: emitted lines are IMMUTABLE and only sys
            # lines ever relabel (mic is pinned "You"), so the whole retro
            # path is inert — relabel_events stays PERMANENTLY empty.
            # Callers (registry check_merge, _consolidate_registry) need no
            # per-site guards; the pin is structural here.
            return
        prefix_old, prefix_new = f"[{old_speaker}] ", f"[{new_speaker}] "
        with self.lock:
            self.meta_version += 1
            self.lines = [prefix_new + l[len(prefix_old):] if l.startswith(prefix_old)
                          else l for l in self.lines]
            for idx, m in enumerate(self.line_meta):
                if m["speaker"] == old_speaker:
                    m["speaker"] = new_speaker
                    if m.get("channel") == "sys":
                        self.relabel_events.append(
                            {"index": idx, "speaker": new_speaker})

    def set_speakers(self, new_by_index: dict) -> int:
        """Line-level retro-relabel from the periodic re-diarization:
        {line_index: new_speaker}. Returns how many lines changed. Only SYS
        lines may be relabelled — the mic channel IS the local user ("You",
        product call 2026-08-19) and no relabel path may touch it; the guard
        here makes that pin structural instead of trusting every caller.
        Every applied change is appended to `relabel_events` for the wire."""
        if self.single_emission:
            # Single-emission: set_speakers only ever touches sys lines,
            # and in mode those are internal carriers whose labels are
            # unused (text-only source). No relabel event may ever exist.
            return 0
        changed = 0
        with self.lock:
            self.meta_version += 1
            for idx, new in new_by_index.items():
                if not (0 <= idx < len(self.lines)):
                    continue
                m = self.line_meta[idx]
                if m.get("channel") != "sys":
                    continue  # mic is pinned "You"; never relabel, never emit
                if m["speaker"] == new:
                    continue
                old_prefix = f"[{m['speaker']}] "
                if self.lines[idx].startswith(old_prefix):
                    self.lines[idx] = f"[{new}] " + self.lines[idx][len(old_prefix):]
                m["speaker"] = new
                self.relabel_events.append({"index": idx, "speaker": new})
                changed += 1
        return changed

    def drain_relabels(self) -> list[dict]:
        """Relabel events appended since the last drain (wire-reply cursor,
        independent from the commits[] cursor)."""
        with self.lock:
            out = self.relabel_events[self._relabel_cursor:]
            self._relabel_cursor = len(self.relabel_events)
            return list(out)

    def snapshot(self) -> list[str]:
        with self.lock:
            return list(self.lines)


class _ChannelStream:
    """One channel's live pipeline: buffer -> (turns -> speaker) -> decode.

    `label_fn(turn_audio) -> str` names the speaker for a closed turn; the
    mic channel uses a constant "You", the sys channel goes through the
    online registry. A change of label commits the open segment at the
    turn boundary."""

    def __init__(self, engine, commit_log: _CommitLog, label_fn,
                 fixed_label: str | None = None, language_vote=None,
                 language: str | None = None,
                 clip_filter=None, channel_tag: str = "mic",
                 initial_prompt: str | None = None,
                 bias_phrases=None, beam_size: int = 1,
                 hallucination_guard: bool = True):
        self.engine = engine
        self.initial_prompt = initial_prompt
        self.bias_phrases = bias_phrases
        self.beam_size = beam_size
        self.commits = commit_log
        self.label_fn = label_fn
        self.language_vote = language_vote
        self.language = language
        self.clip_filter = clip_filter  # (clip, start_s) -> filtered clip
        self.channel_tag = channel_tag
        self.hallucination_guard = hallucination_guard
        self.lock = threading.Lock()
        self._chunks: list[np.ndarray] = []   # appended per push, drained
        # by the `history` fold into the preallocated buffer below.
        self._hist_len = 0                    # total samples ever pushed
        self._hist_buf = np.zeros(0, dtype=np.float32)  # folded storage
        self._hist_filled = 0                 # valid samples in _hist_buf
        self.seg_base = 0.0        # seconds: open segment start in history
        self.seg_speaker: str | None = fixed_label
        self.fixed_label = fixed_label
        self._agc = InputAgc() if _input_agc_enabled() else None
        self.tracker = live_speakers.TurnTracker()
        self.partial = ""
        # Display-path gate for the mutable partial line (energy hold +
        # fuzzy prompt-regurgitation + junk; see partial_gate.py — field
        # 2026-08-28: silence partials echoed the notes vocabulary prompt).
        from . import partial_gate as _pg
        self.partial_gate = _pg.PartialDisplayGate(bias_phrases)
        self._partial_supp_log_at = 0.0   # rate-limits the suppression DBG line
        self.decoded_upto = 0.0    # seconds of the open segment last decoded
        self.last_decode_end = 0.0
        self.decoding = False      # ONE background job at a time per channel
        # Dictation's macOS MLX preview shares the GPU with the final pass.
        # This is enabled by _Session only for that exact product/engine pair;
        # notes and other engines retain the existing scheduling behavior.
        self._preview_pacing = False
        self._preview_next_start = 0.0
        self._preview_job_started = 0.0
        # FIFO of (speaker, cut_s, turn_start): two rapid speaker changes are
        # two boundaries — a single slot lost the first one and its commit
        # then swallowed both turns under the later speaker (device,
        # 2026-08-18). turn_start is the closed turn's own SPEECH start
        # (tracker evidence): the boundary commit reports its span from
        # there, not from seg_base, so leading dead air between the previous
        # commit and this turn never inflates spoken_at/duration_s (the
        # duration_s==window-length device finding, 2026-08-27). None =
        # unknown (legacy callers / finish-drain synthesized cuts).
        self.pending_boundaries: list[tuple[str, float, float | None]] = []
        # Earliest SPEECH start carried across empty/fragment boundary
        # windows (seg_base held): the eventual superset commit still
        # contains those turns' words, so its span starts at the FIRST
        # carried turn's speech, not at this window's turn.
        self._carry_speech_start: float | None = None
        # Stream-time at which a carried (choked) window earns its RETRY
        # boundary (see _carry_retry_s); None = no retry armed. The counter
        # caps retries per carry chain (MAX_CARRY_RETRIES).
        self._carry_retry_at: float | None = None
        self._carry_retries_used = 0
        self.closed = False
        # Set by _Session.teardown (stream_cancel, the end of stream_finish,
        # a superseded notes session): from then on no decode may reach the
        # engine for this channel. Not `closed`, which finish() sets BEFORE
        # its own tail decode and which therefore cannot mean "skip".
        self.torn_down = False
        # COMMIT-ON-STABLE state: the previous periodic decode's segment
        # signature (keyed by its window base — any base move invalidates
        # it), the per-channel rate-cap clock, and the audit trail of
        # stable-commit times (the event-rate guarantee is testable).
        self._stab_prev: tuple[float, tuple] | None = None
        self._stable_last_commit = 0.0
        self.stable_commit_times: list[float] = []   # capped audit trail
        # BOUNDARY DECODE CACHE: the last periodic timed decode as
        # (snap_base, snap_end, spans, lang). When a turn closes and this
        # window already covers [seg_base, cut_s] with clean span
        # boundaries, the boundary commit reuses its context-rich text and
        # SKIPS the re-decode — on real meeting audio the re-decode (plus
        # its engine-lock wait behind a rolling beam window) is the
        # dominant spoken->committed term, and whisper decodes an isolated
        # cut window WORSE than the growing window that already heard past
        # the cut (the EMPTY_CARRY evidence). Invalidated by any seg_base
        # move (base is part of the key).
        self._last_timed: tuple | None = None
        # Commit-time sourcing hooks, set by _Session (None = old behavior):
        # beam_text_fn(start_s, end_s) -> beam-grade text when the rolling
        # stable pass already covers the span; settled_label_fn(start_s,
        # end_s) -> the last re-cluster's label when it covers the span.
        # Both are non-blocking lookups — a commit NEVER waits on them.
        self.beam_text_fn = None
        self.settled_label_fn = None
        # CONTEXT CARRY: last committed text tail for this channel (always
        # tracked — cheap — so the env can flip mid-session; only prompt
        # COMPOSITION is gated, see _prompt_for_decode).
        self._ctx_tail = ""

    def configure_preview_pacing(self, enabled: bool) -> None:
        """Enable the bounded MLX preview scheduler for this channel.

        The same switch stops the caption gate tracking a noise floor: a
        hold-to-talk microphone is already speech in its first window, so a
        floor seeded there classed every later window of a quiet voice as
        quiet and the caption never appeared (PartialDisplayGate.track_floor).
        """
        with self.lock:
            self._preview_pacing = bool(enabled)
            self._preview_next_start = 0.0
            self.partial_gate.track_floor = not self._preview_pacing

    def _preview_decode_kwargs(self) -> dict:
        """One temperature-0 pass for the paced Dictation preview.

        Its captions are display-only, and a decode already on the engine
        cannot be stopped, so the final transcription of the hold waits for
        all of it. The ladder's extra passes are what made that wait long;
        every other channel keeps the engine default."""
        if not self._preview_pacing:
            return {}
        try:
            import inspect
            if "temperature" in inspect.signature(
                    self.engine.transcribe).parameters:
                return {"temperature": 0.0}
        except (TypeError, ValueError):
            pass
        return {}

    def _preview_ready_locked(self) -> bool:
        return (not self._preview_pacing
                or time.monotonic() >= self._preview_next_start)

    def _queue_boundary_locked(self, boundary: tuple) -> None:
        """Queue a boundary, coalescing mic/You preview cuts to the latest."""
        if (self._preview_pacing and self.fixed_label == "You"
                and self.channel_tag == "mic"):
            if self.pending_boundaries:
                earliest = min(
                    (b[2] for b in self.pending_boundaries if len(b) > 2
                     and b[2] is not None),
                    default=None)
                current = boundary[2] if len(boundary) > 2 else None
                starts = [s for s in (earliest, current) if s is not None]
                boundary = (boundary[0], boundary[1], min(starts)
                            if starts else None)
                self.pending_boundaries.clear()
            self.pending_boundaries.append(boundary)
        else:
            self.pending_boundaries.append(boundary)

    # -- wire thread ---------------------------------------------------
    def push(self, samples: np.ndarray) -> None:
        job = None  # ("boundary", speaker, cut_s) | ("decode", snapshot)
        if self._agc is not None:
            samples = self._agc.process(samples)
        with self.lock:
            # Chunked append; the `history` fold drains these into a
            # preallocated capacity-doubling buffer, so a contiguous read is
            # amortized O(chunk) — NOT the old whole-take np.concatenate,
            # which the per-push worth-check triggered every ~200ms (the
            # long-session O(n^2) decay; see `history`).
            self._chunks.append(samples)
            self._hist_len += len(samples)
            for t_start, t_end in self.tracker.feed(samples):
                if t_end - t_start < live_speakers.MIN_TURN_S:
                    continue  # sliver rides along in the open segment
                spk = self._label_turn_locked(t_start, t_end)
                if self.seg_speaker is None:
                    self.seg_base = max(self.seg_base, t_start)
                self.seg_speaker = spk
                # EVERY closed turn commits (utterance-granular). The old
                # commit-on-speaker-change-only design kept a solo talker's
                # text in the mutable partial for up to 22s, and whisper's
                # output over a growing window with internal pauses is
                # UNSTABLE — later decodes sometimes stop at a pause,
                # replacing good text with less ("写了又删", field
                # 2026-08-19, both sides muted, solo speech). Committing at
                # each pause makes the caption stream append-only.
                self._queue_boundary_locked((spk, t_end, t_start))
            self._bound_open_segment_locked()
            if self._carry_retry_at is not None and not self.closed:
                if self.pending_boundaries:
                    # A real boundary reached the queue first — its superset
                    # decode IS the recovery; the retry stands down.
                    self._carry_retry_at = None
                elif self._hist_len / SAMPLE_RATE >= self._carry_retry_at:
                    if self.tracker.open_turn_start is None:
                        # The armed time passed in SILENCE: the pause itself
                        # disproves the continuous-speech premise — firing at
                        # the NEXT voice instead decoded a window bloated
                        # with the whole pause (ES2004a: armed 136.5, fired
                        # 156.6, a 25 s window) and could put the new turn's
                        # opening words under the old speaker. The gap
                        # boundary owns the recovery; expire unused.
                        self._carry_retry_at = None
                    else:
                        # Continuous speech past the choked cut: retry the
                        # carried window NOW with the fresh context instead
                        # of waiting out the force-close (~a window away).
                        cut = self._hist_len / SAMPLE_RATE
                        _dbg(f"carry-retry [{self.channel_tag}]: "
                             f"cut={cut:.2f} (choked window + "
                             f"{_carry_retry_s():.1f}s context)")
                        self._queue_boundary_locked(
                            (self.seg_speaker, cut, None))
                        self._carry_retry_at = None
                        self._carry_retries_used += 1
            if (not self.decoding and not self.closed
                    and self._preview_ready_locked()):
                if self.pending_boundaries:
                    job = ("boundary", *self.pending_boundaries.pop(0))
                    self.decoding = True
                    self._preview_job_started = time.monotonic()
                elif self._worth_decoding_locked():
                    # Carry the CURRENT speaker with the snapshot — by the
                    # time the decode lands, a boundary may have flipped
                    # seg_speaker, and committing old audio under the NEW
                    # speaker merged two people into one line (device,
                    # 2026-08-18, third failure mode of this pipeline).
                    # LIVE-DECODE WINDOW CAP (FR-001 / P0-A): never let the
                    # carrier decode a window longer than MAX_LIVE_DECODE_S. This
                    # branch runs only when NO boundary is queued (see the `if`
                    # above), so the open segment is the carrier's alone to
                    # manage — advancing seg_base to the freshest window here is
                    # the freshness cap at decode granularity (older audio goes
                    # to the finalize, which re-decodes the whole take). Doing it
                    # under the lock BEFORE the snapshot keeps _decode's
                    # reconciliation exact: snap_base == seg_base, one <=12 s
                    # window. Normal (short) open segments are a no-op.
                    if _live_decode_cap_enabled():
                        over = (self._hist_len / SAMPLE_RATE - self.seg_base
                                - MAX_LIVE_DECODE_S)
                        if over > 0:
                            self.seg_base += over
                            self.decoded_upto = max(0.0,
                                                    self.decoded_upto - over)
                    snap = self._open_segment_locked()
                    if self.clip_filter is not None:
                        snap = self.clip_filter(snap, self.seg_base)
                    # Carry the snapshot's seg_base too: _bound_open_segment
                    # runs on the push thread and may advance seg_base WHILE
                    # this decode is in flight (silence trim / freshness cap),
                    # so the completion must reconcile against where the
                    # snapshot actually started, not assume the base held
                    # still (see _decode).
                    #
                    # The general carrier decode: decode the open segment so
                    # live captions, commit-on-stable and the boundary cache
                    # all get mid-turn spans. RESTORED 2026-09-09 — the v5.1
                    # sys throttle below had ABSORBED this assignment, so the
                    # mic channel (and every non-single-emission channel) got
                    # NO mid-turn decode at all: that violated THIS engine's
                    # own turn-first invariant ("Legacy mode single_emission
                    # off is byte-identical / the mic channel keeps CAPTION")
                    # and starved commit-on-stable on the local speaker. The
                    # sys throttle now only DEMOTES this job to None; it no
                    # longer owns the assignment.
                    job = ("decode", snap, self.seg_speaker, self.seg_base)
                    if (self.channel_tag == "sys"
                            and getattr(self.commits, "single_emission", False)
                            and os.environ.get("DICTATION_NOTES_TURNFIRST", "1") != "0"):
                        # v5.1 throttled the sys-live carrier; v5.3 can disable
                        # it outright. In single-emission the carrier's decode
                        # output is a DEMOTED fallback (the bucket/turn path owns
                        # every visible line), yet on a LONG meeting it is the
                        # decode that TIPS the one serialized engine past
                        # real-time: once seg_base lags, the open segment grows
                        # to MAX_OPEN_SEGMENT_S and every carrier decode re-runs
                        # ~15 windows (fixed 30s-encoder each) — a death-spiral.
                        # X2 24.8-min E2E (2026-09-02): "open-segment cap"
                        # x6541 from ~3 min in, stable_through 0.0, only the
                        # final flush emitted (30 lines). The finalize still
                        # decodes the full take, so turning the carrier OFF costs
                        # NO completeness. DICTATION_NOTES_SYS_CARRIER=0 disables it
                        # (the fix); default "1" keeps the v5.1 throttled trickle
                        # (language vote / failed-decode fallback) for a clean A/B
                        # until the device before/after lands, then flip to "0".
                        if os.environ.get("DICTATION_NOTES_SYS_CARRIER", "1") != "1":
                            job = None
                        else:
                            _min_iv = float(os.environ.get(
                                "DICTATION_NOTES_SYS_LIVE_MIN_S", "20.0"))
                            _last = getattr(self, "_last_sys_live_decode", -1e9)
                            if (self._hist_len / SAMPLE_RATE) - _last < _min_iv:
                                job = None
                            else:
                                self._last_sys_live_decode = self._hist_len / SAMPLE_RATE
                                # carrier stays armed for this window (the
                                # general assignment above already set `job`)
                    # Only latch the single decode slot when a job actually
                    # exists. The sys-carrier throttle above (and the legacy
                    # path, which sets no carrier job at all) can leave job
                    # None here; latching `decoding` for a None job left the
                    # slot permanently busy — no `_finish_job` ever ran to
                    # release it — which stranded every queued boundary and
                    # wedged the channel (deadlock repro'd 2026-09-09: sys
                    # `_worth_decoding` true + throttle→None → decoding stuck
                    # True, pending_boundaries never fired, zero lines).
                    if job is not None:
                        self.decoding = True
                        self._preview_job_started = time.monotonic()
        self._spawn(job)

    def _spawn(self, job) -> None:
        if job is None:
            return
        if job[0] == "boundary":
            # job[1:] is (speaker, cut_s[, turn_start]) — legacy 2-tuple
            # boundaries (tests, older queues) still spawn fine.
            threading.Thread(target=self._commit_boundary,
                             args=job[1:], daemon=True).start()
        else:
            threading.Thread(target=self._decode,
                             args=(job[1], job[2], job[3]),
                             daemon=True).start()

    def _finish_job(self) -> None:
        """Common tail of every background job: release the slot and chain
        a parked boundary commit if one arrived mid-job."""
        job = None
        with self.lock:
            self.decoding = False
            finished = time.monotonic()
            self.last_decode_end = finished
            if self._preview_pacing:
                elapsed = max(0.0, finished - self._preview_job_started)
                self._preview_next_start = finished + max(2.0, 3.0 * elapsed)
            if (self.pending_boundaries and not self.closed
                    and not self.torn_down and self._preview_ready_locked()):
                job = ("boundary", *self.pending_boundaries.pop(0))
                self.decoding = True
                self._preview_job_started = time.monotonic()
        self._spawn(job)

    #: Initial capacity of the folded history buffer, in seconds. Capacity
    #: grows by doubling, so the total copy work over a whole session is
    #: O(n) amortized (see `history`).
    HIST_INIT_CAP_S = 60.0

    @property
    def history(self) -> np.ndarray:
        """Contiguous view of the channel audio; folds pending chunks into a
        preallocated capacity-doubling buffer.

        CONTRACT: CALL ONLY WHILE HOLDING self.lock. This getter MUTATES
        internal storage (drains self._chunks into self._hist_buf), so a
        concurrent push() appending to _chunks races it — a torn read or a
        dropped chunk. Every call site does hold the lock (audited 2026-08-23
        for the old fold; re-audited 2026-08-25 for the buffer form); the lock
        is intentionally NOT taken here because threading.Lock is
        non-reentrant and callers already hold it, so acquiring it again
        would DEADLOCK. Keep every new caller under the lock.

        LONG-SESSION BOUND (soak fix C, 2026-08-25): the old fold was
        `np.concatenate(self._chunks)` — a WHOLE-HISTORY copy every time any
        chunk had been appended since the last read. The per-push
        worth-decoding check reads history, so the wire thread re-copied the
        entire session roughly every push: O(session) memcpy per push,
        O(session^2) per meeting (~230 MB per push by minute 60, held under
        self.lock — the measured long-session live-decode decay). Chunks are
        now appended IN PLACE into a preallocated buffer that doubles when
        full: amortized O(chunk) per push, and a read that follows no append
        costs nothing.

        The returned view stays consistent after the lock is released:
        appends write past the view's end (or, on growth, into a NEW buffer),
        never into the prefix an already-returned view covers.
        """
        if self._chunks:
            need = self._hist_filled + sum(len(c) for c in self._chunks)
            if need > len(self._hist_buf):
                cap = max(need, 2 * len(self._hist_buf),
                          int(self.HIST_INIT_CAP_S * SAMPLE_RATE))
                grown = np.empty(cap, dtype=np.float32)
                grown[:self._hist_filled] = self._hist_buf[:self._hist_filled]
                self._hist_buf = grown
            for c in self._chunks:
                n = len(c)
                self._hist_buf[self._hist_filled:self._hist_filled + n] = c
                self._hist_filled += n
            self._chunks = []
        return self._hist_buf[:self._hist_filled]

    def _label_turn_locked(self, t_start: float, t_end: float) -> str:
        if self.fixed_label is not None:
            return self.fixed_label
        a = int(t_start * SAMPLE_RATE)
        b = int(t_end * SAMPLE_RATE)
        return self.label_fn(self.history[a:b], t_start, t_end)

    def _open_segment_locked(self) -> np.ndarray:
        return self.history[int(self.seg_base * SAMPLE_RATE):].copy()

    def _worth_decoding_locked(self) -> bool:
        dur = self._hist_len / SAMPLE_RATE - self.seg_base
        if dur - self.decoded_upto < MIN_NEW_AUDIO_S:
            return False
        if self._preview_pacing and dur < PREVIEW_MIN_WINDOW_S:
            return False
        if time.monotonic() - self.last_decode_end < DECODE_COOLDOWN_S:
            return False
        # Peak-check the undecoded tail on a VIEW. The old form copied the
        # WHOLE open segment (`_open_segment_locked()`) per push just to look
        # at the tail — during a long silent/muted stretch the open segment
        # was the whole stretch, so every push paid an O(stretch) memcpy on
        # the wire thread (soak fix C, 2026-08-25).
        a = (int(self.seg_base * SAMPLE_RATE)
             + int(self.decoded_upto * SAMPLE_RATE))
        tail = self.history[a:]
        if len(tail) and float(np.abs(tail).max()) < PEAK_GATE:
            return False
        return True

    def _bound_open_segment_locked(self) -> None:
        """Keep the mutable open segment bounded (soak fix C — see the
        OPEN_TRIM_* / MAX_OPEN_SEGMENT_S docs). Called once per push, under
        self.lock; amortized O(chunk) per push. Moves seg_base only — never
        deletes buffered audio."""
        dur = self._hist_len / SAMPLE_RATE - self.seg_base
        if dur <= OPEN_TRIM_MIN_S:
            return
        if not self.pending_boundaries:
            # (a) digital-silence trim: advance over sub-PEAK_GATE audio that
            # can never decode to text nor start a turn. Skipped while a
            # boundary is queued (its commit will advance seg_base anyway).
            hist = self.history
            blk = int(OPEN_TRIM_BLOCK_S * SAMPLE_RATE)
            for _ in range(OPEN_TRIM_BUDGET_BLOCKS):
                if (self._hist_len / SAMPLE_RATE - self.seg_base
                        <= OPEN_TRIM_MIN_S):
                    break
                a = int(self.seg_base * SAMPLE_RATE)
                seg = hist[a:a + blk]
                if not len(seg) or float(np.abs(seg).max()) >= PEAK_GATE:
                    break
                advanced = len(seg) / SAMPLE_RATE
                self.seg_base += advanced
                self.decoded_upto = max(0.0, self.decoded_upto - advanced)
        # (b) hard freshness cap: only reachable under decode starvation.
        # v5.3: env-overridable. 180s is FAR above normal bounds — but by the
        # time an overloaded engine reaches it, every decode is already re-running
        # ~15 windows (the death-spiral). A LOWER cap (e.g. 30s) makes the live
        # path shed old audio to the finalize BEFORE the window balloons, so each
        # decode stays cheap and the engine can catch back up. DICTATION_NOTES_MAX_OPEN_S.
        try:
            _max_open = float(os.environ.get("DICTATION_NOTES_MAX_OPEN_S",
                                             MAX_OPEN_SEGMENT_S))
        except (TypeError, ValueError):
            _max_open = MAX_OPEN_SEGMENT_S
        dur = self._hist_len / SAMPLE_RATE - self.seg_base
        if dur > _max_open:
            new_base = self._hist_len / SAMPLE_RATE - _max_open
            dropped = new_base - self.seg_base
            _dbg(f"open-segment cap [{self.channel_tag}]: decodes {dur:.0f}s "
                 f"behind — dropped the oldest {dropped:.1f}s from the LIVE "
                 f"window (the finalize still decodes the full take)")
            self.seg_base = new_base
            self.decoded_upto = max(0.0, self.decoded_upto - dropped)

    def _prompt_for_decode(self) -> str | None:
        """The greedy decode's initial_prompt. With DICTATION_NOTES_CTX_CARRY=1
        the previous committed line's tail is appended after the vocab
        prompt (vocab FIRST: it must stay inside the device prompt-token
        budget's front prefix — see PROMPT_CHAR_BUDGET). Beam decodes never
        call this; the bias trie owns beam biasing."""
        if not _ctx_carry_enabled() or not self._ctx_tail:
            return self.initial_prompt
        if self.initial_prompt:
            return f"{self.initial_prompt}\n{self._ctx_tail}"
        return self._ctx_tail

    def _transcribe_without_personalization(self, clip: np.ndarray, **kwargs):
        """Decode live text while reserving aliases/casing for ``gate``.

        The macOS engine applies its text personalization by default.  Live
        commits have their own single text-pass gate, so tell engines that
        explicitly support the switch to leave that work to the commit path.
        Older/test engines do not have the parameter and must receive the
        exact legacy kwargs instead.
        """
        try:
            import inspect
            if "personalize" in inspect.signature(
                    self.engine.transcribe).parameters:
                kwargs["personalize"] = False
        except (TypeError, ValueError):
            # Some extension/callable implementations have no inspectable
            # signature; preserving the old call is the compatible fallback.
            pass
        return self.engine.transcribe(clip, **kwargs)

    def _note_ctx_tail(self, text: str) -> None:
        """Track the committed line's END as the next decode's context.
        A line the hallucination gate would drop never enters the tail —
        carrying a fabricated "Thank you." forward would SEED the exact
        pleasantry loop the gates exist to stop."""
        text = (text or "").strip()
        if not text:
            return
        from . import hallucination
        if hallucination.is_junk_utterance(text):
            return
        if len(text) > CTX_CARRY_MAX_CHARS:
            cut = text[-CTX_CARRY_MAX_CHARS:]
            # cut at a word edge so the prompt never starts mid-word
            sp = cut.find(" ")
            text = cut[sp + 1:] if 0 <= sp < len(cut) - 1 else cut
        self._ctx_tail = text

    # -- background threads --------------------------------------------
    def _transcribe(self, clip: np.ndarray, language: str | None = None) -> str:
        text, _ = self._transcribe2(clip, language)
        return text

    def _torn_down_while_waiting(self) -> bool:
        """Call right after taking the engine lock. A live decode can queue
        behind a dictation hold for a whole decode, and the host cancels the
        preview stream before it sends that hold's transcribe. Without this
        check the queued decode ran anyway, on a cancelled stream, and kept
        the engine from the NEXT hold for up to one MAX_LIVE_DECODE_S window.
        A decode already inside the engine is not stopped; this only keeps a
        waiting one from starting."""
        with self.lock:
            torn_down = self.torn_down
        if torn_down:
            _dbg(f"decode skipped [{self.channel_tag}]: the stream was torn down "
                 "while this decode waited for the engine")
        return torn_down

    def _transcribe2(self, clip: np.ndarray,
                     language: str | None = None) -> tuple[str, str | None]:
        language = language or self.language
        if not len(clip) or float(np.abs(clip).max()) < PEAK_GATE:
            return "", None
        # SILENCE-HALLUCINATION gate (frame-RMS, not just PEAK). PEAK_GATE only
        # catches TRUE digital silence (muted mic); a live sys/remote channel's
        # room tone / hiss / key clicks peak WELL above it (device quiet-room
        # hold: peak 0.0351 clears PEAK_GATE 17x) yet carries no speech, and
        # large-v3-turbo answers that low-energy window with a stock 1-2 word
        # phrase ("Thank you", "you") — which the diarizer then clusters as a
        # phantom NEW remote speaker (field: a ghost "Speaker A" on silence,
        # 2026-08-23). The finalize pass already drops these by span
        # energy (stream_helpers ~630); the streaming worker never
        # had that gate, so the phantom appeared LIVE. Use the sanctioned
        # cross-pipeline frame-RMS floor (audio_energy.SILENCE_RMS=0.004),
        # which is STRONGER than a whole-clip peak test yet WEAKER than a
        # whole-clip RMS test — a quiet real sentence with one loud 30 ms
        # frame passes, so this does NOT re-open the FLEURS whole-clip-RMS
        # regression that PEAK_GATE was set tiny to avoid.
        from . import audio_energy
        if not audio_energy.has_speech_energy(clip):
            return "", None
        t0 = time.perf_counter()
        # Bounded granularity: a clip longer than MAX_LIVE_DECODE_S is
        # decoded as equal windows no longer than that, each under its own
        # lock acquisition, so a hold queued behind this channel waits for
        # at most one window.
        max_n = int(MAX_LIVE_DECODE_S * SAMPLE_RATE)
        if len(clip) > max_n:
            n_win = -(-len(clip) // max_n)
            edges = [round(i * len(clip) / n_win) for i in range(n_win + 1)]
            text, lang = "", None
            for a, b in zip(edges, edges[1:]):
                t, l = self._transcribe2(clip[a:b], language)
                if t:
                    text = _join_text(text, t) if text else t
                lang = lang or l
            _dbg(f"decode {len(clip)/SAMPLE_RATE:.1f}s in {n_win} windows -> "
                 f"{len(text)} chars in {time.perf_counter()-t0:.1f}s")
            return text, lang
        try:
            with _ENGINE_LOCK.held(self._live_decode_prio(), self.channel_tag):
                if self._torn_down_while_waiting():
                    return "", None
                if self.beam_size > 1:
                    # LIVE beam (the dictation architecture): beam-decode
                    # each turn as it happens, so quality is beam-grade live
                    # AND the finalize has nothing heavy to re-decode. Beam
                    # uses the bias trie, not the initial_prompt. Crash-proof:
                    # any beam failure degrades THIS utterance to greedy
                    # instead of propagating and killing the worker (field
                    # 2026-08-22: 4-speaker sys load killed the worker).
                    try:
                        r = self._transcribe_without_personalization(
                            clip, language=language, beam_size=self.beam_size,
                            bias_phrases=self.bias_phrases,
                            **self._preview_decode_kwargs())
                    except Exception:
                        _dbg("LIVE-BEAM failed -> greedy fallback\n"
                             + traceback.format_exc())
                        r = self._transcribe_without_personalization(
                            clip, language=language,
                            initial_prompt=self._prompt_for_decode(),
                            **self._preview_decode_kwargs())
                else:
                    r = self._transcribe_without_personalization(
                        clip, language=language,
                        initial_prompt=self._prompt_for_decode(),
                        **self._preview_decode_kwargs())
        except Exception:
            _dbg("DECODE ERROR\n" + traceback.format_exc())
            raise
        text = (r.get("text") or "").strip()
        lang = (r.get("stats") or {}).get("language")
        _dbg(f"decode {len(clip)/SAMPLE_RATE:.1f}s -> {len(text)} chars "
             f"in {time.perf_counter()-t0:.1f}s")
        return text, lang

    def _transcribe_live(self, clip: np.ndarray,
                         refresh_fn=None) -> tuple:
        """The periodic live decode. When commit-on-stable can use it, this
        is a TIMESTAMPED decode (transcribe_timed) so the completion can cut
        at whisper's own segment boundaries; otherwise it is exactly the old
        `_transcribe2` path. Returns (text, lang, spans) where spans are
        window-relative (start, end, text, lang) segments, or None when no
        timestamps exist for this round (fallback path / clip too long —
        the >MAX_LIVE_DECODE_S windowed decode stays lock-bounded and
        simply skips a stable commit that round; a healthy stable channel
        keeps its open window far below that anyway)."""
        # Timed ceiling: above this the round falls back to the windowed
        # span-less decode. Deliberately COMMIT_AFTER_S (22 s), NOT
        # MAX_LIVE_DECODE_S (12 s): real meeting turns run 8-16 s and a
        # 12 s ceiling blanked the boundary cache exactly on the turns that
        # need it. Whisper's encoder cost is fixed (30 s padded window), so
        # a 22 s call's engine-lock hold is only the extra decoder tokens —
        # comparable to the documented <=3 s p95 target, and far below the
        # rolling beam pass's own 30 s-window holds. DICTATION_NOTES_TIMED_MAX_S
        # tunes it back down if device holds measure worse.
        try:
            timed_max = float(os.environ.get("DICTATION_NOTES_TIMED_MAX_S",
                                             COMMIT_AFTER_S))
        except (TypeError, ValueError):
            timed_max = COMMIT_AFTER_S
        if (not _commit_on_stable_enabled()
                or not hasattr(self.engine, "transcribe_timed")
                or len(clip) > int(timed_max * SAMPLE_RATE)):
            text, lang = self._transcribe2(clip)
            return text, lang, None, clip
        # Same energy gates as _transcribe2: true digital silence and
        # speech-free room tone never reach the NPU (whisper hallucinates
        # on both; see the gates' docs up in _transcribe2).
        if not len(clip) or float(np.abs(clip).max()) < PEAK_GATE:
            return "", None, [], clip
        from . import audio_energy
        if not audio_energy.has_speech_energy(clip):
            return "", None, [], clip
        t0 = time.perf_counter()
        try:
            with _ENGINE_LOCK.held(self._live_decode_prio(), self.channel_tag):
                if self._torn_down_while_waiting():
                    return "", None, [], clip
                # LAST-MOMENT SNAPSHOT REFRESH: the snapshot was taken at
                # queue time, but this thread may have waited seconds for
                # the engine (a rolling beam window holds it for a whole
                # 30 s-window decode). Decoding the stale window wastes the
                # call and leaves the boundary cache never covering a cut —
                # so re-snapshot the open segment NOW (bounded by the timed
                # ceiling). The gates above ran on a subset, which a
                # superset passes by construction.
                if refresh_fn is not None:
                    # +6 s cap: refreshing exists to cover a turn that
                    # closed DURING this thread's lock wait — a bounded
                    # extension. Uncapped, a long wait produced a much
                    # longer window, whose longer decode raised everyone
                    # else's queueing in turn (measured on the AMI replay
                    # as a P50 regression).
                    cap = min(int(timed_max * SAMPLE_RATE),
                              len(clip) + 6 * SAMPLE_RATE)
                    fresh = refresh_fn(cap)
                    if fresh is not None and len(fresh) > len(clip):
                        clip = fresh
                if self.beam_size > 1:
                    # LIVE beam parity with _transcribe2: any beam failure
                    # degrades THIS decode to the greedy prompt path
                    # instead of killing the worker.
                    try:
                        r = self.engine.transcribe_timed(
                            clip, language=self.language,
                            beam_size=self.beam_size,
                            bias_phrases=self.bias_phrases)
                    except Exception:  # noqa: BLE001
                        _dbg("LIVE-BEAM timed failed -> greedy fallback\n"
                             + traceback.format_exc())
                        r = self.engine.transcribe_timed(
                            clip, language=self.language,
                            initial_prompt=self._prompt_for_decode())
                else:
                    r = self.engine.transcribe_timed(
                        clip, language=self.language,
                        initial_prompt=self._prompt_for_decode())
        except Exception:
            _dbg("DECODE ERROR (timed)\n" + traceback.format_exc())
            raise
        spans = [s for s in (r.get("spans") or [])
                 if len(s) >= 3 and str(s[2]).strip()]
        text = ""
        for s in spans:
            t = str(s[2]).strip()
            text = _join_text(text, t) if text else t
        lang = next((s[3] for s in spans if len(s) > 3 and s[3]), None)
        _dbg(f"decode-timed {len(clip)/SAMPLE_RATE:.1f}s -> {len(spans)} "
             f"spans, {len(text)} chars in {time.perf_counter()-t0:.1f}s")
        return text, lang, spans, clip

    def _snap_cut_locked(self, cut_abs: float, limit_abs: float) -> float:
        """Nudge an audio cut forward onto the quietest STABLE_SNAP_FRAME_S
        frame between the committed segment's end and the next segment (or
        the scan cap) — whisper's timestamp tokens are decoder guesses, and
        a cut landing on a word onset would garble the remainder window's
        first word. CALL UNDER self.lock (`history` contract)."""
        hist = self.history
        a = int(cut_abs * SAMPLE_RATE)
        b = min(int(limit_abs * SAMPLE_RATE), len(hist))
        frame = int(STABLE_SNAP_FRAME_S * SAMPLE_RATE)
        if b - a < frame:
            return cut_abs
        best_off, best_peak = 0, None
        for off in range(0, b - a - frame + 1, frame):
            peak = float(np.abs(hist[a + off:a + off + frame]).max())
            if best_peak is None or peak < best_peak:
                best_off, best_peak = off, peak
        return (a + best_off + frame // 2) / SAMPLE_RATE

    def _boundary_cache_locked(self, cut_s: float) -> tuple | None:
        """Cached (text, lang) for the turn window [seg_base, cut_s), or
        None when the boundary must re-decode. CALL UNDER self.lock. Usable
        only when the last timed decode started exactly at the current
        seg_base, heard THROUGH the cut, and no span straddles it (a
        straddler means the decode disagrees with the energy boundary —
        never split a span's words across a guess). Empty text is None on
        purpose: the re-decode path owns the empty/fragment carry."""
        if not _commit_on_stable_enabled() or self._last_timed is None:
            _dbg(f"boundary-cache miss [{self.channel_tag}]: no cache")
            return None
        base, end, spans, lang = self._last_timed
        if abs(base - self.seg_base) > 1e-6:
            _dbg(f"boundary-cache miss [{self.channel_tag}]: base {base:.2f}"
                 f" != seg_base {self.seg_base:.2f}")
            return None
        if end + 0.05 < cut_s:
            _dbg(f"boundary-cache miss [{self.channel_tag}]: end {end:.2f} <"
                 f" cut {cut_s:.2f} (stale by {cut_s - end:.2f}s)")
            return None
        cut_rel = cut_s - base
        # Trailing tolerance 0.6 s: whisper timestamp tokens wobble at
        # segment edges, so the final span routinely "ends" a few hundred
        # ms past the energy cut. Accepting that CANNOT capture another
        # turn's words — the tracker only cuts after TURN_GAP_S (0.7 s) of
        # silence, so the next turn's speech starts strictly later than
        # cut + 0.7. Anything past 0.6 is indistinguishable from real
        # next-turn content and stays a straddler.
        take, tol = [], 0.6
        for s in spans:
            a, b = float(s[0]), float(s[1])
            if b <= cut_rel + tol and a <= cut_rel:
                take.append(str(s[2]).strip())   # the turn's own span
            elif a >= cut_rel - tol:
                continue                          # remainder's span
            else:
                _dbg(f"boundary-cache miss [{self.channel_tag}]: span "
                     f"{a:.2f}-{b:.2f} straddles cut_rel {cut_rel:.2f}")
                return None   # straddler: decode vs energy boundary disagree
        text = ""
        for t in take:
            text = _join_text(text, t) if text else t
        if not text:
            return None
        # fragment choke parity: a long window yielding almost no text is
        # the re-decode path's (carry) business, not a cache hit.
        content = "".join(text.split())
        cjk = sum(1 for ch in text if "一" <= ch <= "鿿")
        looks_cjk = cjk >= max(1, len(content)) * 0.5
        if (not looks_cjk and cut_rel >= FRAGMENT_MIN_WINDOW_S
                and len(content) <= FRAGMENT_MAX_CHARS):
            return None
        return text, lang

    def _stable_cut_locked(self, spans: list, snap_base: float,
                           snap_end: float) -> dict | None:
        """Decide a commit-on-stable cut for this decode's segments, or None.
        CALL UNDER self.lock. Records this decode's signature for the next
        round's agreement check either way. Returns {"cut": absolute cut
        seconds, "text": committed text, "remainder": the still-mutable
        tail text, "start": absolute speech start} on success."""
        if not _commit_on_stable_enabled():
            return None
        if self.closed or self.pending_boundaries:
            # A queued speaker boundary owns the cut (same guard as the
            # long-monologue force commit): committing across it would put
            # two voices under one label.
            return None
        if abs(snap_base - self.seg_base) > 1e-6:
            return None   # base moved mid-flight (trim/cap) — next round
        sig = tuple((round(float(s[0]), 2), round(float(s[1]), 2),
                     str(s[2]).strip()) for s in spans)
        prev, self._stab_prev = self._stab_prev, (snap_base, sig)
        if prev is None or prev[0] != snap_base or not sig:
            return None
        if time.monotonic() - self._stable_last_commit < STABLE_MIN_INTERVAL_S:
            return None   # event-rate cap; the words just wait one round
        stable = []
        for cur, old in zip(sig, prev[1]):
            if (cur[2] != old[2]
                    or abs(cur[0] - old[0]) > STABLE_TS_TOL_S
                    or abs(cur[1] - old[1]) > STABLE_TS_TOL_S):
                break
            stable.append(cur)
        if not stable:
            return None
        win_dur = snap_end - snap_base
        cut_idx = None
        for i, (a, b, text) in enumerate(stable):
            if b > win_dur - STABLE_TAIL_GUARD_S:
                break   # too close to the edge: no real right-hand context
            nxt = sig[i + 1][0] if i + 1 < len(sig) else win_dur
            if (text.rstrip()[-1:] in STABLE_PUNCT
                    or nxt - b >= STABLE_PAUSE_S):
                cut_idx = i   # keep the LAST qualifying boundary
        if cut_idx is None:
            return None
        commit_text = ""
        for _, _, t in stable[:cut_idx + 1]:
            commit_text = _join_text(commit_text, t) if commit_text else t
        content = "".join(commit_text.split())
        cjk = sum(1 for ch in content if "一" <= ch <= "鿿")
        if cjk >= max(1, len(content)) * 0.5:
            if len(content) < STABLE_MIN_CHARS_CJK:
                return None
        elif len(commit_text.split()) == 1 and len(content) >= STABLE_MIN_CHARS:
            pass   # unspaced script (kana etc.): length is the only signal
        elif (len(commit_text.split()) < STABLE_MIN_WORDS
                or len(content) < STABLE_MIN_CHARS):
            return None   # no fragment lines — wait for more agreement
        cut_rel = stable[cut_idx][1]
        limit_rel = (sig[cut_idx + 1][0] - 0.05 if cut_idx + 1 < len(sig)
                     else win_dur)
        limit_rel = min(cut_rel + STABLE_SNAP_MAX_S, max(cut_rel, limit_rel))
        cut_abs = self._snap_cut_locked(snap_base + cut_rel,
                                        snap_base + limit_rel)
        remainder = ""
        for s in sig[cut_idx + 1:]:
            remainder = (_join_text(remainder, s[2])
                         if remainder else s[2])
        return {"cut": cut_abs, "text": commit_text, "remainder": remainder,
                "start": snap_base + stable[0][0]}

    def _commit_guarded(self, speaker: str, clip: np.ndarray,
                        text: str, lang: str | None,
                        span: tuple | None = None) -> None:
        """Commit = the live stream's final moment, so the offline pass's
        FG-7 language guard runs HERE too (accuracy over latency — product
        call 2026-08-18: a suspicious-language turn is worth a 2s pinned
        re-decode in the live stream)."""
        vote = self.language_vote
        if vote is not None and text:
            from . import stream_helpers as odt
            dur = len(clip) / SAMPLE_RATE
            if vote.suspect(lang, dur, odt.effective_word_count(text)):
                pin = vote.pin_for(text)
                if pin and pin != lang:
                    try:
                        redo, _ = self._transcribe2(clip, language=pin)
                        if redo:
                            text, lang = redo, pin
                    except Exception:  # noqa: BLE001
                        pass
            vote.record(lang)
        # Commit-time sourcing (2026-08-27): the rolling stable pass decodes
        # the same audio with beam-4 + the bias trie, strictly better than
        # this live greedy hypothesis — when it has ALREADY covered the
        # span, the committed line takes the beam text. Same for the last
        # re-cluster's settled speaker label (sys only; mic is pinned). Both
        # are instant lookups with a greedy/registry fallback: the latency
        # budget owns emission, never these.
        if text and span is not None and span[0] is not None:
            if self.beam_text_fn is not None:
                try:
                    better = self.beam_text_fn(span[0], span[1])
                except Exception:  # noqa: BLE001 — sourcing is best-effort
                    better = None
                if better:
                    text = better
            if self.settled_label_fn is not None and self.fixed_label is None:
                try:
                    settled = self.settled_label_fn(span[0], span[1])
                except Exception:  # noqa: BLE001
                    settled = None
                if settled:
                    speaker = settled
        self._note_ctx_tail(text)
        self.commits.add(speaker, text, channel=self.channel_tag, span=span)

    def _commit_boundary(self, speaker: str, cut_s: float,
                         turn_start: float | None = None) -> None:
        """Speaker changed at cut_s: decode [seg_base, cut_s), commit it
        under the PREVIOUS speaker, advance the segment base. turn_start is
        the closed turn's own speech start (None = unknown -> seg_base)."""
        try:
            with self.lock:
                if self.closed:
                    return
                a = int(self.seg_base * SAMPLE_RATE)
                b = int(cut_s * SAMPLE_RATE)
                if b <= a:
                    # Stale boundary: cut_s is at/behind seg_base (a prior
                    # commit already advanced the base past this queued cut).
                    # Decoding [a:b] would decode an empty/reversed clip and a
                    # blank commit can swallow the live word; skip, don't rewind.
                    _dbg(f"stale-cut boundary a={a} b={b} seg_base={self.seg_base:.2f} "
                         f"cut_s={cut_s:.2f} — skipped")
                    return
                # LIVE-DECODE WINDOW CAP (FR-001 / P0-A): a boundary decode of
                # a stuck, over-long open segment feeds the engine at most
                # MAX_LIVE_DECODE_S — the FRESHEST window ending at cut_s. seg_base
                # still advances to cut_s below, so the older audio is dropped
                # from the LIVE window (the finalize re-decodes the whole take),
                # exactly like the freshness cap. `decode_base` is the clip's
                # true start; the reported span and the empty/fragment win_s use
                # it, not seg_base. Normal turns (<=MAX_LIVE_DECODE_S) are a
                # no-op (a == the capped start). =0 restores the old whole-window.
                if _live_decode_cap_enabled():
                    a = max(a, b - int(MAX_LIVE_DECODE_S * SAMPLE_RATE))
                decode_base = a / SAMPLE_RATE
                clip = self.history[a:b].copy()
                # The DECODED window is [decode_base, cut_s) — uncommitted audio
                # inside it may never be skipped — but the REPORTED span is the
                # SPEECH: it starts at the turn's own tracker start (or the
                # first carried turn's, when an empty/fragment carry held
                # seg_base across windows), clamped into the window. This
                # closed the 2026-08-27 on-device duration_s finding
                # (span == decode-window length: leading dead air between
                # the previous commit and this turn inflated spoken_at /
                # duration_s). The other half of that finding — room tone
                # above the raw peak floor holding turns open until the
                # MAX_OPEN_TURN_S force-close (duration_s quantized to
                # 6.15/12.3 and the commit-latency P90 tail) — is fixed
                # upstream in live_speakers.TurnTracker (frame-RMS speech
                # evidence, DICTATION_NOTES_TURN_SPEECH_RMS), so turn ends land
                # on the speech envelope again.
                starts = [s for s in (self._carry_speech_start, turn_start)
                          if s is not None]
                span = (max(decode_base, min(starts)) if starts
                        else decode_base, cut_s)
                cached = self._boundary_cache_locked(cut_s)
            if cached is not None:
                # Turn-close fast path: the last periodic timed decode
                # already covered this whole turn WITH context past the cut
                # — its text is at least as good as an isolated re-decode
                # (the EMPTY_CARRY evidence says strictly better) and it is
                # already in hand, so the boundary skips the second decode
                # and its engine-lock wait entirely.
                text, lang = cached
                _dbg(f"boundary cache hit: [{self.channel_tag}] "
                     f"{span[0]:.2f}-{cut_s:.2f} ({len(text)} ch, no re-decode)")
            else:
                text, lang = self._transcribe2(clip)
            with self.lock:
                if self.closed:
                    return
                # SWALLOW FIX: a re-decode of this ISOLATED turn window that is
                # EMPTY, or a tiny FRAGMENT of a long window, is whisper choking
                # on a short/awkward cut — not real speech (the audio_energy gate
                # already zero-returns true silence up in _transcribe2). The
                # pre-fix path committed that empty/fragment and advanced seg_base
                # past [a:b] — permanently discarding the rest of the sentence,
                # which decodes fine once it rides into the next, longer window
                # (device-proven: empty windows recover; field: "And" left alone
                # when the sentence was swallowed). So KEEP the audio in the open
                # segment (do NOT advance seg_base) and let the next boundary
                # re-decode the superset. Bounded by EMPTY_CARRY_MAX_S, at which
                # point we advance-and-drop exactly like the pre-fix behavior so
                # the window can never grow unbounded nor starve the engine lock.
                # win_s is the DECODED window (decode_base, cut_s) — with the
                # live-decode cap that is <=MAX_LIVE_DECODE_S, so a full capped
                # window that decoded empty is NOT treated as a short awkward
                # cut (win_s >= EMPTY_CARRY_MAX_S); a paced short window keeps
                # decode_base == seg_base and behaves exactly as before.
                win_s = cut_s - decode_base
                content = "".join((text or "").split())
                is_empty = not text
                # FRAGMENT choke: a LONG window yielding almost no text — whisper
                # emitting one leading token ("And", "So.") and stopping on an
                # awkwardly-cut window, so the sentence's audio would be dropped
                # and only the stray word survives (field: "And for the sake of
                # the client…" left as just "And"). The discriminator is the
                # WINDOW being much longer than the text: a real short turn
                # ("Yeah"/"OK") comes from a SHORT window (< FRAGMENT_MIN_WINDOW_S)
                # and is never touched; only a >= FRAGMENT_MIN_WINDOW_S window
                # decoding to <= FRAGMENT_MAX_CHARS is treated as a choke. (This
                # is calibrated for the ENGLISH-only demo — English words are
                # multi-char, so <=6 chars from a multi-second window is
                # unambiguously a single stray token, not real content. NOTE: it
                # is deliberately NOT applied to CJK, where 6 chars is a whole
                # sentence — see the language guard below.)
                cjk = sum(1 for ch in (text or "") if "一" <= ch <= "鿿")
                looks_cjk = cjk >= max(1, len(content)) * 0.5
                is_fragment = (bool(text) and not looks_cjk
                               and win_s >= FRAGMENT_MIN_WINDOW_S
                               and len(content) <= FRAGMENT_MAX_CHARS)
                if (KEEP_EMPTY_WINDOW and (is_empty or is_fragment)
                        and win_s < EMPTY_CARRY_MAX_S):
                    # Remember the EARLIEST carried speech start: the next
                    # window's commit contains this turn's words, so its
                    # reported span must reach back to them.
                    carried = [s for s in (self._carry_speech_start,
                                           turn_start) if s is not None]
                    if carried:
                        self._carry_speech_start = min(carried)
                    retry = _carry_retry_s()
                    self._carry_retry_at = (
                        cut_s + retry
                        if retry > 0
                        and self._carry_retries_used < MAX_CARRY_RETRIES
                        else None)
                    _dbg(f"{'empty' if is_empty else 'fragment'}-window carry: "
                         f"seg_base={self.seg_base:.2f} cut_s={cut_s:.2f} "
                         f"({win_s:.1f}s) text={text[:20]!r} kept open for the "
                         f"next window (seg_base UNCHANGED)")
                    return  # finally: releases the slot; seg_base UNCHANGED
            self._commit_guarded(speaker, clip, text, lang, span=span)
            with self.lock:
                # max(): the freshness cap (_bound_open_segment_locked) may
                # have advanced seg_base past this cut while the decode ran —
                # rewinding to cut_s would re-open the capped window and make
                # the next push re-drop the same audio.
                self.seg_base = max(self.seg_base, cut_s)
                self._carry_speech_start = None
                self._carry_retry_at = None
                self._carry_retries_used = 0
                self.partial = ""
                self.decoded_upto = 0.0
        finally:
            self._finish_job()

    def _live_decode_prio(self) -> int:
        """Engine-lock class for this channel's LIVE decode. TURN-FIRST
        INVERSION (2026-09-02): in single-emission the sys channel's live
        decodes produce demoted internal carriers (fallback text only), yet
        ran at CAPTION while turn-decode — the producer of every line the
        user actually sees — waited at BACKGROUND (median 25.9s queue on
        dense meetings = the DESYNC miss). Swap them in mode: sys live
        yields (BACKGROUND), turn-decode is the caption producer (CAPTION).
        The mic channel keeps CAPTION — the user's own live echo stays
        snappy. Legacy mode (single_emission off) is byte-identical.
        DICTATION_NOTES_TURNFIRST=0 restores the old classes for A/B."""
        if (self.channel_tag == "sys"
                and getattr(self.commits, "single_emission", False)
                and os.environ.get("DICTATION_NOTES_TURNFIRST", "1") != "0"):
            from .engine_lock import BACKGROUND as _BG
            return _BG
        return CAPTION

    def _decode(self, snapshot: np.ndarray, speaker: str | None,
                snap_base: float | None = None) -> None:
        try:
            def _refresh(max_n: int):
                if snap_base is None:
                    return None
                with self.lock:
                    if self.closed or abs(self.seg_base - snap_base) > 1e-6:
                        return None
                    return self._open_segment_locked()[:max_n]

            text, lang, spans, snapshot = self._transcribe_live(
                snapshot, refresh_fn=_refresh)
            # Display-path energy classification for the partial line: RMS of
            # the NEWEST audio in this decode window (the tail is what this
            # decode added; the segment's older audio already had its say).
            # Computed outside the lock — pure numpy on a copy-free view.
            _tail = snapshot[-int(1.5 * SAMPLE_RATE):] if len(snapshot) else snapshot
            _win_rms = float(np.sqrt(np.mean(np.square(_tail)))) if len(_tail) else 0.0
            _win_quiet = self.partial_gate.note_window_rms(_win_rms)
            stable_job = None
            with self.lock:
                if self.closed:
                    return
                # Display gate BEFORE the anti-shrink rule: a quiet window's
                # new text (energy hold / prompt regurgitation) or a junk
                # decode never replaces the partial — the COMMIT path below
                # keeps using the raw `text` and has its own unified gate.
                from . import hallucination as _hall
                _disp_text, _supp = self.partial_gate.decide(
                    self.partial, text, _win_quiet,
                    is_junk=(lambda value: self.hallucination_guard
                             and _hall.is_junk_utterance(value)))
                if _supp is not None:
                    _now = time.monotonic()
                    if _now - self._partial_supp_log_at >= 2.0:
                        self._partial_supp_log_at = _now
                        _dbg(f"partial-gate: suppressed ({_supp}) "
                             f"rms={_win_rms:.4f} quiet={_win_quiet} "
                             f"[{self.channel_tag}] text={text[:80]!r}")
                # Anti-shrink guard: whisper over the same growing segment
                # sometimes returns LESS than the previous decode (stops at
                # an internal pause). Never let a much-shorter hypothesis
                # visually erase text mid-utterance — keep the longer one
                # until a commit settles it. (Field: "莫名其妙的删除".)
                if _supp is not None:
                    pass  # partial held; suppression already logged above
                elif len(_disp_text) >= int(len(self.partial) * 0.6) or not self.partial:
                    self.partial = _disp_text
                else:
                    _dbg(f"anti-shrink: kept partial ({len(self.partial)} ch) "
                         f"over new decode ({len(_disp_text)} ch)")
                # Reconcile against the CURRENT seg_base. decoded_upto is
                # seg_base-relative, and _bound_open_segment_locked (soak fix
                # C) may have advanced seg_base on the push thread WHILE this
                # decode ran (silence trim / MAX_OPEN_SEGMENT_S cap). The old
                # `decoded_upto = len(snapshot)/SR` assumed the base held
                # still; with a mid-decode advance of Δ it overstated
                # coverage by Δ, and the COMMIT_AFTER_S branch below then
                # committed a span shifted Δ into never-decoded audio and
                # pushed seg_base up to Δ PAST the end of history — skipping
                # real speech from the live window (the finalize recovered
                # it, but live lines went missing).
                if snap_base is None:
                    snap_base = self.seg_base   # legacy: assume base unmoved
                snap_end = snap_base + len(snapshot) / SAMPLE_RATE
                self.decoded_upto = max(0.0, snap_end - self.seg_base)
                if spans is not None:
                    # boundary decode cache (see _boundary_cache_locked):
                    # keyed by the window base, so any base move invalidates
                    self._last_timed = (snap_base, snap_end,
                                        tuple((float(s[0]), float(s[1]),
                                               str(s[2]).strip())
                                              for s in spans), lang)
                # COMMIT-ON-STABLE: two consecutive decodes agreeing on a
                # leading segment run commit it NOW, mid-turn — the feed
                # gets the line seconds before the pause even starts. Only
                # for a still-open monologue under the CARRIED speaker
                # (same ownership rule as the force commit below), and only
                # once a speaker EXISTS: before the sys channel's first
                # closed turn, seg_speaker is None and committing would put
                # remote speech under the "You" fallback label — the
                # force-close boundary (which labels the turn first) owns
                # the opening monologue instead, exactly as before.
                if (spans is not None and speaker is not None
                        and self.seg_speaker == speaker):
                    stable_job = self._stable_cut_locked(spans, snap_base,
                                                         snap_end)
                    if stable_job is not None:
                        self.seg_base = stable_job["cut"]
                        self.decoded_upto = max(0.0,
                                                snap_end - self.seg_base)
                        self.partial = stable_job["remainder"]
                        # The stable commit consumed the carried words up
                        # to its cut — a still-armed retry would re-decode
                        # audio the commit already settled.
                        self._carry_speech_start = None
                        self._carry_retry_at = None
                        self._carry_retries_used = 0
                        self._stab_prev = None
                        self._stable_last_commit = time.monotonic()
                        self.stable_commit_times.append(
                            self._stable_last_commit)
                        del self.stable_commit_times[:-512]  # bounded audit
                # Long monologue: commit so the decode window stays <30s —
                # but ONLY while it is still a monologue: with a boundary
                # queued (or the speaker already flipped) the snapshot spans
                # two people, and the boundary job owns the cut.
                if (stable_job is None
                        and self.decoded_upto >= COMMIT_AFTER_S
                        and not self.pending_boundaries
                        and self.seg_speaker == speaker):
                    commit_now = True
                    commit_span = (self.seg_base,
                                   self.seg_base + self.decoded_upto)
                    self.partial = ""
                    self.seg_base += self.decoded_upto
                    self.decoded_upto = 0.0
                else:
                    commit_now = False
                    commit_span = None
            if stable_job is not None:
                with self.lock:
                    # finish() may have taken over between the lock releases
                    # (10 s decode-drain deadline expired): its tail commit
                    # already covered this audio — committing now would put
                    # the sentence's first half BELOW its second half.
                    if self.closed:
                        return
                a = int((stable_job["start"] - snap_base) * SAMPLE_RATE)
                b = int((stable_job["cut"] - snap_base) * SAMPLE_RATE)
                self._commit_guarded(speaker,
                                     snapshot[max(0, a):max(0, b)],
                                     stable_job["text"], lang,
                                     span=(stable_job["start"],
                                           stable_job["cut"]))
            elif commit_now:
                self._commit_guarded(speaker or "You", snapshot, text, lang,
                                     span=commit_span)
        finally:
            self._finish_job()

    # -- shared --------------------------------------------------------
    def partial_line(self) -> str:
        from . import hallucination
        with self.lock:
            if (not self.partial
                    or (self.hallucination_guard and hallucination.is_junk_utterance(self.partial))
                    or self.commits.is_vocab_echo(self.partial)
                    or _loop_junk(self.partial, hallucination_guard=self.hallucination_guard)):
                # A silence/leak decode's pleasantry must not even FLASH on
                # screen (field: "我这边不说话就有 thank you" — partials
                # bypassed the commit-time blocklist; same for vocabulary
                # regurgitation and repetition loops, 2026-08-19).
                return ""
            spk = self.seg_speaker or self.fixed_label or "…"
            text = self.partial
        try:
            from . import stream_repetition
            text = stream_repetition.clean(text)
        except Exception:  # noqa: BLE001
            pass
        return f"[{spk}] {text}"

    def finish(self) -> None:
        """Settle the final turn, drain queued boundaries, then decode +
        commit whatever remains open. Skipping the flush/drain step decoded
        ACROSS the last uncommitted speaker boundary in one window and
        whisper silently dropped the first voice's text (device,
        2026-08-18)."""
        deadline = time.monotonic() + 10.0
        while time.monotonic() < deadline:
            with self.lock:
                if not self.decoding:
                    break
            time.sleep(0.05)
        with self.lock:
            for t_start, t_end in self.tracker.flush():
                if t_end - t_start < live_speakers.MIN_TURN_S:
                    continue
                spk = self._label_turn_locked(t_start, t_end)
                if self.seg_speaker is None:
                    self.seg_speaker = spk
                    self.seg_base = max(self.seg_base, t_start)
                elif spk != self.seg_speaker:
                    # Synthesized cut at the NEW turn's start; the committed
                    # segment's own speech start is unknown here -> None.
                    self._queue_boundary_locked(
                        (self.seg_speaker, t_start, None))
                    self.seg_speaker = spk
            boundaries = [b if len(b) >= 3 else (b[0], b[1], None)
                          for b in self.pending_boundaries]
            self.pending_boundaries.clear()
        for speaker, cut_s, turn_start in boundaries:
            with self.lock:
                a = int(self.seg_base * SAMPLE_RATE)
                b = int(cut_s * SAMPLE_RATE)
                if b <= a:
                    # Stale boundary (see _commit_boundary): base already past
                    # this cut — skip without an empty commit or a base rewind.
                    # "finish-drain" names the SITE: the same stale-cut guard
                    # exists on the live boundary path, and the test that
                    # pins this one tells them apart by this tag (it shipped
                    # asserting a tag the message never carried — repaired
                    # here on the message side, keeping the test's intent).
                    _dbg(f"stale-cut finish-drain a={a} b={b} seg_base={self.seg_base:.2f} "
                         f"cut_s={cut_s:.2f} — skipped")
                    continue
                clip = self.history[a:b].copy()
                clip_start = self.seg_base
                # Same speech-span rule as the live boundary path: report
                # from the turn's (or first carried turn's) speech start.
                starts = [s for s in (self._carry_speech_start, turn_start)
                          if s is not None]
                span_start = (max(clip_start, min(starts)) if starts
                              else clip_start)
            if self.clip_filter is not None:
                clip = self.clip_filter(clip, clip_start)
            if len(clip) / SAMPLE_RATE > 0.3:
                try:
                    self.commits.add(speaker, self._transcribe(clip),
                                     channel=self.channel_tag,
                                     span=(span_start, cut_s))
                except Exception:  # noqa: BLE001
                    pass
            with self.lock:
                self.seg_base = cut_s
                self._carry_speech_start = None
                self.decoded_upto = 0.0
        with self.lock:
            raw = self._open_segment_locked()
            raw_start = self.seg_base
            # A carried empty/fragment window may still hold the base back;
            # the tail commit's reported span starts at the carried speech.
            tail_start = (max(self.seg_base, self._carry_speech_start)
                          if self._carry_speech_start is not None
                          else self.seg_base)
        if self.clip_filter is not None:
            raw = self.clip_filter(raw, raw_start)
        with self.lock:
            clip = live_speakers.trim_trailing_silence(raw)
            trimmed_s = (len(raw) - len(clip)) / SAMPLE_RATE
            self.closed = True
            speaker = self.seg_speaker or self.fixed_label or "You"
        if len(clip) / SAMPLE_RATE > 0.3:
            lang = None
            try:
                text, lang = self._transcribe2(clip)
            except Exception:  # noqa: BLE001
                text = self.partial
            if trimmed_s > 1.0 and text:
                # A long fabricated-silence tail is exactly where whisper
                # appends "Thank you." — audio evidence says the trailing
                # pleasantry is not real speech.
                from . import hallucination
                if self.hallucination_guard:
                    text = hallucination.strip_trailing(text)
            self._commit_guarded(speaker, clip, text, lang,
                                 span=(tail_start,
                                       tail_start + len(clip) / SAMPLE_RATE))
        with self.lock:
            # The committed line replaces the running hypothesis — a stale
            # partial would render as a duplicate line in display().
            self.partial = ""


class _Session:
    def __init__(self, engine, embed_fn, bias_phrases=None, num_speakers=None,
                 languages=None, rolling_stable: bool = False,
                 stable_window_seconds: float = 30.0,
                 stable_hop_seconds: float = 12.0,
                 stable_lookahead_seconds: float = 6.0,
                 stable_beam_size: int = 4,
                 live_beam: int | None = None,
                 client: str = "notes"):
        # Known participant count from the meeting roster / Zoom, if the host
        # passed it. Drives the periodic GLOBAL re-cluster's k — the single
        # lever that turns the streaming diarizer from 57% DER (greedy-only,
        # 25.6% confusion) to ~24-32% (block-online with known k; validated on
        # AMI ES2004a/IS1009a 2026-08-21). None => fall back to NME/greedy.
        try:
            self._num_speakers = int(num_speakers) if num_speakers else None
        except (TypeError, ValueError):
            self._num_speakers = None
        if not self._num_speakers:
            # Manual override / device-test hook until the host wires the Zoom
            # participant count into the start request: DICTATION_NOTES_NUM_SPK=<N>.
            try:
                _env_k = int(os.environ.get("DICTATION_NOTES_NUM_SPK", "0") or "0")
                self._num_speakers = _env_k if _env_k > 0 else None
            except ValueError:
                self._num_speakers = None
        # Vocabulary conditioning via whisper's native <|startofprev|>
        # prompt — the mechanism the macOS dictation line uses. Greedy-path
        # equivalent of the accurate pass's beam-trie bias. Deduped and
        # capped to whole phrases (_vocab_prompt) so prompt_ids' 96-token
        # tail-truncation can never cut a term in half; the trie and the
        # regurgitation guard below keep the FULL list.
        prompt = _vocab_prompt(bias_phrases)
        self.initial_prompt = prompt
        self.client = client
        self.commits = _CommitLog()
        self.commits.hallucination_guard = client != "dictation"
        # SINGLE-EMISSION MODE (DICTATION_NOTES_SINGLE_EMISSION=1, plan
        # 2026-09-01-single-emission-diar): a caption line leaves ONCE —
        # complete text + speaker after the D=H hold — and is immutable.
        # self._hold is the mode's single source of truth everywhere
        # (None = legacy, byte-identical behavior).
        self._hold = None
        if _single_emission_enabled():
            from .emission_hold import EmissionHold
            # DICTATION_NOTES_OOO_AFTER_S: bounded out-of-order release (fix B for
            # a 25-min live freeze — dense meetings settle turns 14-20s
            # late/out-of-order and the strict barrier parked every ready
            # line behind one stuck frontier until flush). Ready turns may
            # pass a frontier stuck longer than this many seconds; the
            # watermark does not advance, so the frontier is never skipped.
            # Unset = strict in-order (pre-fix behavior).
            # CONFIDENCE-BASED RELEASE (2026-09-02, the shipped fix for the
            # 25-min live freeze):
            #   DICTATION_NOTES_LABEL_DEADLINE_S (default 12, "0" disables) —
            #     bounds the previously-UNBOUNDED label-missing wait; past
            #     hold+deadline the line releases with the turn's own
            #     diarizer speaker (flush semantics). With text_deadline
            #     already bounded, every gate is now bounded => the strict
            #     in-order pane can never freeze (worst ~hold+12s per line).
            #   DICTATION_NOTES_MARGIN_RELEASE (default 3.0, "0" disables) —
            #     margin fast path: winner/runner-up evidence ratio >= this
            #     releases before the full hold (quality replaces the time
            #     gamble).
            #   DICTATION_NOTES_MARGIN_MIN_AGE_S (default 3.0) — fast-path floor
            #     so a brand-new boundary still gets a settle beat.
            def _fenv(name, default, off_vals=("0", "off", "")):
                raw = os.environ.get(name)
                if raw is None:
                    return default
                if raw.strip().lower() in off_vals:
                    return None
                try:
                    return float(raw)
                except (TypeError, ValueError):
                    return default
            self._hold = EmissionHold(
                float(os.environ.get("DICTATION_NOTES_HOLD_S", "9.6")),  # H-MEASUREMENT
                debug_cb=_dbg,  # named no-line forensics
                label_deadline_s=_fenv("DICTATION_NOTES_LABEL_DEADLINE_S", 12.0),
                margin_hi=_fenv("DICTATION_NOTES_MARGIN_RELEASE", 3.0),
                margin_min_age_s=_fenv("DICTATION_NOTES_MARGIN_MIN_AGE_S", 3.0) or 3.0)
            self.commits.single_emission = True
        if prompt:
            self.commits.vocab_norm = "".join(
                ch for ch in ", ".join(bias_phrases).lower() if ch.isalnum())
            self.commits.vocab_phrases = list(bias_phrases)
        if bias_phrases:
            try:
                from .zh_phonetic import VocabPhonetic
                self.commits.vocab_phonetic = VocabPhonetic(bias_phrases)
            except Exception:  # noqa: BLE001 — enhancement layer
                pass
            try:
                from .en_phonetic import VocabPhoneticEn
                self.commits.vocab_phonetic_en = VocabPhoneticEn(bias_phrases)
            except Exception:  # noqa: BLE001 — enhancement layer
                pass
        self._watchdog_stop = threading.Event()
        threading.Thread(target=self._watchdog, daemon=True).start()
        # No background model-backed diarizer is part of dictation. Keep the
        # optional registry's pure relabel state for source-only experiments.
        self._rediar_last = 0.0
        self._rediar_cost = 0.0
        self._session_t0 = time.monotonic()
        self._dz_bank = None
        self._dz_warmup_failed = True
        self._warmup_thread = None
        self._rediar_thread = None
        self._bo_bank = None
        self._bo_letter_book: dict = {}
        self._line_label_rounds: dict = {}
        _floor_k = self._num_speakers
        _registry_threshold = live_speakers.SIM_THRESHOLD
        if "DICTATION_NOTES_SPK_SIM" in os.environ:
            try:
                _registry_threshold = float(os.environ["DICTATION_NOTES_SPK_SIM"])
            except ValueError:
                pass
        else:
            _registry_threshold = getattr(
                embed_fn, "_dictation_similarity_threshold", _registry_threshold)
        registry = (live_speakers.OnlineSpeakerRegistry(
                        embed_fn, sim_threshold=_registry_threshold,
                        num_speakers=_floor_k)
                    if embed_fn else None)
        self._registry = registry

        def letter(idx: int) -> str:
            return f"Speaker {chr(ord('A') + idx)}" if idx < 26 else f"Speaker {idx}"

        def registry_label(turn_audio: np.ndarray, t_start: float = 0.0,
                           t_end: float = 0.0) -> str:
            if registry is None:
                return "Them"
            idx = registry.assign(turn_audio)
            merged = registry.check_merge()
            if merged is not None:
                kept, absorbed = merged
                self.commits.relabel(letter(absorbed), letter(kept))
                # Lock-free ON PURPOSE. registry_label runs inside push(),
                # which ALREADY holds the pushing channel's self.lock. Doing
                # `with ch.lock` here re-acquired that same non-reentrant lock
                # on the same thread -> self-deadlock: the whole worker froze
                # at 0% CPU whenever a speaker-merge fired mid-stream (field
                # 2026-08-21, faulthandler: push_sys -> _label_turn_locked ->
                # registry_label waiting on self.sys.lock). The relabel is a
                # single compare-and-assign; a rare race only mislabels one
                # open segment, which the next commit/re-diar corrects.
                for ch in (self.sys, self.mic):
                    if ch.seg_speaker == letter(absorbed):
                        ch.seg_speaker = letter(kept)
                if idx == absorbed:
                    idx = kept
            return letter(idx)

        # PRODUCT CALL (2026-08-19): the mic channel IS the user —
        # channel-pure architecture, no voice matching. The echo gate (push
        # + decode-time) keeps far-end bleed out of this channel; if the far
        # end still lands here the fix is CAPTURE ROUTING, not labeling.

        try:
            from . import stream_helpers as odt
            declared_languages = tuple(languages or odt.DEFAULT_SEED_LANGUAGES)
            vote = odt.LanguageVote(seed=declared_languages)
        except ImportError:
            # Meeting diarization is not shipped on the dictation path. Its
            # suspicious-language vote is an enhancement, not a requirement:
            # a None vote is already skipped at commit time.
            declared_languages = tuple(languages or ("zh", "en"))
            vote = None
        language_pin = declared_languages[0] if len(declared_languages) == 1 else None
        # LIVE beam (the dictation architecture): beam-decode each turn AS
        # IT HAPPENS so live captions are beam-grade and the finalize is
        # instant (nothing heavy to re-decode). Cost is distributed over the
        # call instead of a batch at the end. Now a WIRE parameter
        # (stream_start live_beam) — the notes host requests 4 with the
        # rolling pass off (two-pass decision, 2026-08-30: the streaming
        # transcript is per-turn whisper full decodes, not overlapping-window
        # stable prefixes). DICTATION_NOTES_LIVE_BEAM stays as the operator
        # override for device experiments; unset, the wire value rules;
        # neither given -> 1 (the legacy greedy live path, byte-for-byte).
        # Risk note kept: notes runs BOTH channels + diarization on one NPU,
        # so beam on both must be measured against real-time — that is what
        # run_notes_stream_e2e --turn-stream certifies.
        _env_beam = os.environ.get("DICTATION_NOTES_LIVE_BEAM")
        try:
            _live_beam = int(_env_beam) if _env_beam else int(live_beam or 1)
        except (TypeError, ValueError):
            _live_beam = 1
        self.live_beam = _live_beam
        self.sys = _ChannelStream(engine, self.commits, registry_label,
                                  language_vote=vote, channel_tag="sys",
                                  language=language_pin,
                                  initial_prompt=prompt,
                                  bias_phrases=bias_phrases, beam_size=_live_beam,
                                  hallucination_guard=client != "dictation")

        self.mic = _ChannelStream(engine, self.commits, None,
                                  fixed_label="You", language_vote=vote,
                                  language=language_pin,
                                  channel_tag="mic", initial_prompt=prompt,
                                  bias_phrases=bias_phrases, beam_size=_live_beam,
                                  hallucination_guard=client != "dictation")
        # Commit-time sourcing state (see _beam_text_for/_settled_label_for):
        # the last re-cluster's settled turns, and the beam-vs-greedy /
        # settled-vs-registry split counters the replay report reads.
        self._settled_turns: list[tuple] = []
        self.commit_source_counts = {"beam": 0, "greedy": 0,
                                     "settled": 0, "registry": 0,
                                     "bank": 0}
        self.stable_mic = self.stable_sys = None
        if rolling_stable:
            from .rolling_stable import RollingStableChannel
            stable_args = dict(
                bias_phrases=bias_phrases,
                languages=declared_languages,
                window_s=float(stable_window_seconds),
                hop_s=float(stable_hop_seconds),
                lookahead_s=float(stable_lookahead_seconds),
                beam_size=int(stable_beam_size))
            self.stable_mic = RollingStableChannel(
                engine, channel="mic", speaker="You", **stable_args)
            self.stable_sys = RollingStableChannel(
                engine, channel="sys", speaker="Them", **stable_args)
        # Wire the commit-time sourcing hooks: beam text on both channels,
        # settled labels on sys only (the mic is pinned "You" by contract).
        self.sys.beam_text_fn = lambda a, b: self._beam_text_for("sys", a, b)
        self.mic.beam_text_fn = lambda a, b: self._beam_text_for("mic", a, b)
        self.sys.settled_label_fn = self._settled_label_for
        # LONG-SESSION BOUND (soak fix C, 2026-08-25): stable_snapshot() runs
        # on EVERY ~200ms push, and its attribution step scanned every rolling
        # span against every committed sys line — O(spans x lines) pure Python
        # per push, i.e. quadratic over the meeting (by minute 90 that is
        # ~10^5 tuple builds per push on-device). Two bounds, both exact:
        # (1) the whole reply is cached keyed on a cheap monotone state tuple
        #     (span counts / through marks / line count / relabel version) —
        #     a push during which nothing changed returns the previous reply;
        # (2) within a recompute, per-span attribution is FROZEN once no
        #     future commit can overlap the span (_STABLE_FREEZE_S), so only
        #     the recent tail is ever re-scanned. Relabels bump
        #     commits.meta_version, which clears the frozen map — retroactive
        #     speaker corrections still rewrite the whole transcript exactly
        #     as before. Only the wire thread touches these fields.
        self._stable_cache_key = None
        self._stable_cache = None
        self._attr_frozen: dict = {}
        self._attr_frozen_tag = None
        # Speakers-on echo gate (demo floors have no headphones): the sys
        # envelope records when the FAR END is audible; mic samples in that
        # window (+ reverb tail) are zeroed before they reach turn tracking
        # or decode, so the far end's room echo can't caption as [You].
        self.sys_envelope = live_speakers.EnergyEnvelope()
        self.mic_clock = 0.0
        # Streaming echo cancellation state. All of it lives on the wire
        # thread (push_mic/push_sys arrive serialized); only sys history is
        # shared and is read under self.sys.lock.
        self._aec_enabled = os.environ.get("DICTATION_NOTES_AEC") != "0"
        self._aec = None            # FdafAec while engaged; None = bypass
        self._aec_delay = 0         # samples; 0 = headset case, full no-op
        self._aec_pos = 0           # mic timeline position (samples received)
        self._aec_hold = np.zeros(0, dtype=np.float32)  # sub-BLOCK remainder
        self._aec_raw = np.zeros(0, dtype=np.float32)   # RAW mic tail for
        # delay re-estimation — the mic channel's history is post-AEC, so a
        # working filter would erase its own correlation evidence and a
        # re-estimate off cleaned audio would wrongly disengage it.
        self._aec_last_est = None
        self._aec_est_interval = AEC_EST_INTERVAL_S
        self._aec_energy: list[tuple] = []  # (n, e_in, e_out) per feed

    def configure_preview_pacing(self) -> None:
        """Pace only the Dictation preview when the selected engine is MLX."""
        enabled = (getattr(self, "client", "notes") == "dictation"
                   and getattr(self.mic.engine, "_engine_name", None) == "mlx")
        self.mic.configure_preview_pacing(enabled)
        self.sys.configure_preview_pacing(enabled)

    def push_mic(self, samples: np.ndarray) -> None:
        t0 = self.mic_clock
        t1 = t0 + len(samples) / SAMPLE_RATE
        self.mic_clock = t1
        # NO audio-domain gating: a continuously-loud far end (a playing
        # video) turned the old half-duplex gate into "always mute the
        # user" — correct text appeared once, then the next gated decode
        # replaced it with nothing (field 2026-08-19: "正确的一段然后自己
        # 删掉"). Echo duplicates are removed at the TEXT level instead
        # (see _CommitLog.add's dedup) — the user's own words can never be
        # eaten by a gate again. AEC below SUBTRACTS the far end's copy; it
        # never mutes, so it cannot re-open that failure.
        samples = self._aec_mic(samples)
        if len(samples):
            self.mic.push(samples)
            if self.stable_mic is not None:
                self.stable_mic.push(samples)

    def push_sys(self, samples: np.ndarray) -> None:
        self.sys_envelope.append(samples)
        self.sys.push(samples)
        if self.stable_sys is not None:
            self.stable_sys.push(samples)

    #: Beam spans must cover at least this fraction of a committed line's
    #: span before their text replaces the greedy hypothesis — thinner
    #: coverage means the beam pass saw a different (shorter) region and
    #: substituting would drop words the greedy line had.
    BEAM_TEXT_COVERAGE = 0.6

    def _beam_text_for(self, channel_tag: str, start_s: float,
                       end_s: float) -> str | None:
        """Beam-grade text for a committed line's span, or None to keep the
        greedy text. Never waits: the rolling stable pass must have ALREADY
        finalized text through the span's end (its `_through_s`), the spans
        picked are those whose midpoint falls inside the line (midpoint
        assignment partitions beam spans across adjacent lines — no word can
        surface on two feed lines), and their coverage must clear
        BEAM_TEXT_COVERAGE. Counted per outcome in commit_source_counts so
        the replay can report the beam/greedy split honestly."""
        if os.environ.get("DICTATION_NOTES_COMMIT_BEAM_TEXT", "1") == "0":
            return None
        stable = self.stable_sys if channel_tag == "sys" else self.stable_mic
        if stable is None or end_s is None or end_s <= start_s:
            return None
        with stable.lock:
            through = stable._through_s
            # bounded reverse scan: _spans grows for the whole meeting and
            # this runs per commit under the rolling pass's own lock — only
            # the tail overlapping the line is ever relevant (soak
            # discipline: no O(session) work on a hot path).
            spans = []
            for s in reversed(stable._spans):
                if s["end"] <= start_s - 1.0:
                    break
                spans.append((s["start"], s["end"], s["text"]))
            spans.reverse()
        if through + 1e-6 < end_s:
            self.commit_source_counts["greedy"] += 1
            return None   # beam hasn't caught up: the line ships greedy NOW
        picked = [s for s in spans
                  if start_s <= (s[0] + s[1]) / 2 < end_s]
        # containment: a picked span leaking past the line's edges belongs
        # partly to a NEIGHBORING line — substituting would duplicate (or
        # drop) the boundary words whenever that neighbor ships greedy.
        # Midpoint assignment alone only partitions when EVERY line
        # substitutes; mixed greedy/beam neighbors need containment.
        if any(a < start_s - 0.35 or b > end_s + 0.35 for a, b, _ in picked):
            self.commit_source_counts["greedy"] += 1
            return None
        cov = sum(max(0.0, min(end_s, b) - max(start_s, a))
                  for a, b, _ in picked)
        if not picked or cov < self.BEAM_TEXT_COVERAGE * (end_s - start_s):
            self.commit_source_counts["greedy"] += 1
            return None
        text = ""
        for _, _, t in sorted(picked):
            t = str(t).strip()
            text = _join_text(text, t) if text else t
        if not text:
            self.commit_source_counts["greedy"] += 1
            return None
        self.commit_source_counts["beam"] += 1
        return text

    def _settled_label_for(self, start_s: float, end_s: float) -> str | None:
        """The last re-cluster's label for a span, or None to keep the
        greedy registry assign. Majority overlap (>=0.5 of the span), the
        SAME rule the retroactive relabel applies — this just applies it
        BEFORE the line is emitted when the settled state already covers
        the span, instead of only after via relabel_events."""
        if os.environ.get("DICTATION_NOTES_COMMIT_SETTLED_LABEL", "1") == "0":
            return None
        if end_s is None or end_s <= start_s:
            return None
        turns = self._settled_turns
        if not turns:
            # No re-cluster published yet: the D2 bank tier (default OFF) is
            # the only alternative source; with its flag off this stays the
            # historical uncounted early return, byte-for-byte.
            return self._bank_label_for(start_s, end_s)
        best, best_ov = None, 0.0
        for a, b, name in reversed(turns):   # committed spans sit at the
            if b <= start_s:                 # tail; turns are time-ordered
                break
            ov = min(end_s, b) - max(start_s, a)
            if ov > best_ov:
                best, best_ov = name, ov
        if best is None or best_ov / max(0.1, end_s - start_s) < 0.5:
            # settled turns don't cover the span (the usual state for the
            # newest ~cadence of lines): try the D2 bank tier before falling
            # back to the greedy registry. Flag off -> None immediately, so
            # the registry count below is untouched relative to today.
            banked = self._bank_label_for(start_s, end_s)
            if banked is not None:
                return banked
            self.commit_source_counts["registry"] += 1
            return None
        self.commit_source_counts["settled"] += 1
        return best

    def _bank_label_for(self, start_s: float, end_s: float) -> str | None:
        """D2 arm: when the settled turns don't cover a committing span,
        attribute it against the DZ bank's CURRENT VBx state instead of the
        greedy registry — EmbeddingBank.label_for_span matches the span's
        cached segment-masked frame activity to the last clustering (no new
        model call). This replaces the clip-cosine probe removed earlier:
        no valid threshold exists for raw cosine on the ResNet clip embedder
        (distinct speakers sit at 0.86-0.93), whereas the bank's own frame
        evidence needs no similarity threshold at all.

        DEFAULT OFF (DICTATION_NOTES_COMMIT_BANK_LABEL=0): this is an unmeasured
        arm awaiting the A3 replay, and the house rule is measured-before-
        default. With the flag off this returns None before touching any
        state — commit sourcing is byte-for-byte unchanged."""
        if os.environ.get("DICTATION_NOTES_COMMIT_BANK_LABEL", "0") != "1":
            return None
        bank = self._dz_bank
        if bank is None or getattr(bank, "last_state", None) is None:
            return None
        try:
            res = bank.label_for_span(start_s, end_s)
        except Exception:  # noqa: BLE001 — sourcing is best-effort
            return None
        if res is None:
            return None
        # EmbeddingBank.label_for_span returns (speaker index, evidence,
        # margin). The bank owns evidence/margin thresholds; this consumer
        # only needs the index and evidence for its existing diagnostics.
        idx, ev_s = res[0], res[1]
        # the snapshot's centroid for idx lets the letter book be matched by
        # IDENTITY (same VBx round) rather than by position — see below.
        cent = None
        try:
            cents = getattr(bank.last_state, "centroids", None)
            if cents is not None and 0 <= int(idx) < len(cents):
                cent = np.asarray(cents[int(idx)], dtype=np.float64).ravel()
        except Exception:  # noqa: BLE001
            cent = None
        letter = self._bank_letter_for(int(idx), cent)
        self.commit_source_counts["bank"] += 1
        _dbg(f"commit-bank-label [{start_s:.2f},{end_s:.2f}s) idx={idx} "
             f"evidence={ev_s:.2f}s -> {letter}")
        return letter

    #: _bank_letter_for identity match: the letter book's centroid and the
    #: snapshot's centroid for the same speaker are the SAME VBx vector (one
    #: cluster round feeds both), so their cosine is 1.0 up to float error.
    #: This is an equality test, not a speaker-similarity threshold — the
    #: invalidated raw-cosine approach is not being reintroduced.
    _BANK_LETTER_IDENTITY_COS = 0.999

    def _bank_letter_for(self, idx: int, cent=None) -> str:
        """Bank output-speaker index -> display letter. MERGE EXPECTATION:
        wave1/dz-stable-letters populates self._bo_letter_book
        {letter: centroid} on the DZ path from the SAME cluster round that
        publishes the snapshot, with letters assigned by overlap continuity
        (so idx 0 is NOT necessarily "Speaker A"). Resolution order:
          1. an index-keyed book variant ({idx: letter}) is honored directly;
          2. with the snapshot centroid `cent` (L2-normalized), the book
             letter whose centroid is the same vector wins (identity match,
             _BANK_LETTER_IDENTITY_COS) — correct under permuted letters;
          3. a letter-keyed book forming the exact "Speaker A.." prefix (the
             registry's idx->letter scheme) maps positionally;
          4. otherwise the positional fallback (`Speaker <A+idx>`, or
             `Speaker <idx>` past Z — the file's convention).
        Works with an empty book (today's DZ path) and any foreign shape."""
        fallback = (f"Speaker {chr(ord('A') + idx)}" if 0 <= idx < 26
                    else f"Speaker {idx}")
        book = self._bo_letter_book
        try:
            if isinstance(book, dict) and book:
                if idx in book and isinstance(book[idx], str):
                    return book[idx]           # index-keyed book variant
                if cent is not None and cent.size and np.all(np.isfinite(cent)):
                    best, best_cos = None, 0.0
                    for name, v in book.items():
                        if not isinstance(name, str):
                            continue
                        v = np.asarray(v, dtype=np.float64).ravel()
                        n = float(np.linalg.norm(v))
                        if v.shape != cent.shape or n <= 0.0:
                            continue
                        c = float(np.dot(v, cent) / n)
                        if c > best_cos:
                            best, best_cos = name, c
                    if best is not None and best_cos >= self._BANK_LETTER_IDENTITY_COS:
                        return best
                letters = sorted(k for k in book if isinstance(k, str))
                prefix = [f"Speaker {chr(ord('A') + i)}"
                          for i in range(len(letters))]
                if letters == prefix and 0 <= idx < len(letters):
                    return letters[idx]        # letter-keyed, index-ordered
        except Exception:  # noqa: BLE001 — the book is advisory only
            pass
        return fallback

    #: A committed line's speech precedes its commit by at most the live
    #: path's own bounds (COMMIT_AFTER_S=22 + EMPTY_CARRY_MAX_S=11 + decode
    #: latency). A rolling span whose end sits this far behind the NEWEST
    #: committed sys line can therefore never gain a new overlapping line —
    #: its attribution is FINAL until a relabel (which bumps meta_version and
    #: clears the frozen map). ~3x the worst-case commit lag.
    _STABLE_FREEZE_S = 120.0

    def _stable_state_key(self):
        """Cheap monotone fingerprint of every input stable_snapshot reads.
        All components only ever move forward (span counts, through marks,
        line count, meta_version), so `current == cached` proves nothing
        changed since the cached reply was computed — a hit cannot be stale."""
        parts: list = [_wordsplit_enabled()]
        for c in (self.stable_mic, self.stable_sys):
            if c is None:
                parts.append(None)
                continue
            with c.lock:
                parts.append((len(c._spans), c._through_s, c._error))
        with self.commits.lock:
            parts.append((len(self.commits.line_meta),
                          self.commits.meta_version))
        return tuple(parts)

    def _attribute_sys_span(self, span: dict, sys_meta: list[dict],
                            wordsplit: bool) -> list[dict]:
        """Attribute ONE rolling sys span against the committed sys lines
        (factored out of stable_snapshot so settled spans' results can be
        frozen). Returns the output span(s); may set span['speaker']."""
        candidates = [(min(span["end"], m["end"]) -
                       max(span["start"], m["start"]),
                       m["start"], m["speaker"])
                      for m in sys_meta]
        candidates = [c for c in candidates if c[0] > 0]
        if not candidates:
            return [span]
        if wordsplit:
            pieces = _split_span_at_turn_boundaries(span, sys_meta)
            if pieces:
                return pieces
        dur = max(1e-6, span["end"] - span["start"])
        best_ov = max(c[0] for c in candidates)
        if best_ov / dur < MIN_ATTR_OVERLAP_RATIO:
            # Sliver gate (see MIN_ATTR_OVERLAP_RATIO): a turn that only
            # clips this span's edge is not evidence about the span —
            # keep the channel default instead of the sliver's label.
            return [span]
        # Deterministic tie-break: max overlap, then EARLIEST turn start.
        # The old `max()` over (overlap, speaker) tuples broke exact ties
        # alphabetically by speaker NAME — "Speaker B" beat "Speaker A"
        # for no acoustic reason. Earliest matching turn is a property of
        # the audio, not the label alphabet.
        candidates.sort(key=lambda c: (-c[0], c[1]))
        span["speaker"] = candidates[0][2]
        return [span]

    def stable_snapshot(self) -> dict:
        """Merged rolling transcript for the wire response. Cached per state
        (soak fix C): a push during which no span/line/relabel changed
        returns the previous reply; a recompute reuses frozen attribution
        for spans old enough to be settled (_STABLE_FREEZE_S)."""
        if self.stable_mic is None and self.stable_sys is None:
            return {"text": "", "timed": "", "spans": [], "through": 0.0}
        key = self._stable_state_key()
        if key == self._stable_cache_key and self._stable_cache is not None:
            return self._stable_cache
        channels = [c.snapshot() for c in (self.stable_mic, self.stable_sys)
                    if c is not None]
        spans = sorted((s for c in channels for s in c["spans"]),
                       key=lambda s: (s["start"], s["channel"]))
        # Reuse the online diarizer's time-aligned labels for remote spans.
        # The rolling ASR boundary is deliberately independent from speaker
        # turns, so attribution is an overlap operation, not a chunk label.
        with self.commits.lock:
            meta = [dict(m) for m in self.commits.line_meta]
        sys_meta = [m for m in meta if m["channel"] == "sys"
                    and m["start"] is not None and m["end"] is not None]
        wordsplit = _wordsplit_enabled()
        # frozen attribution: valid only for the current relabel state
        frozen_tag = (self.commits.meta_version, wordsplit)
        if frozen_tag != self._attr_frozen_tag:
            self._attr_frozen = {}
            self._attr_frozen_tag = frozen_tag
        newest_meta_end = max((m["end"] for m in sys_meta), default=None)
        out_spans: list[dict] = []
        for span in spans:
            if span["channel"] != "sys" or not sys_meta:
                out_spans.append(span)
                continue
            fkey = (span["start"], span["end"], span["text"])
            frozen = self._attr_frozen.get(fkey)
            if frozen is not None:
                out_spans.extend(frozen)
                continue
            attributed = self._attribute_sys_span(span, sys_meta, wordsplit)
            if (newest_meta_end is not None
                    and span["end"] <= newest_meta_end - self._STABLE_FREEZE_S):
                self._attr_frozen[fkey] = attributed
            out_spans.extend(attributed)
        spans = sorted(out_spans, key=lambda s: (s["start"], s["channel"]))

        def mmss(seconds: float) -> str:
            total = max(0, int(seconds))
            return f"{total // 60:02d}:{total % 60:02d}"

        text = "\n".join(f"[{s['speaker']}] {s['text']}" for s in spans)
        timed = "\n".join(
            f"[{mmss(s['start'])}] [{s['speaker']}] {s['text']}" for s in spans)
        result = {"text": text, "timed": timed, "spans": spans,
                  "through": min(c["through"] for c in channels)}
        self._stable_cache_key = key
        self._stable_cache = result
        return result

    # -- streaming echo cancellation ------------------------------------
    def _aec_mic(self, samples: np.ndarray) -> np.ndarray:
        """Echo-cancel one mic chunk against the sys reference. Fail-soft:
        ANY problem returns the raw audio — captions never die for AEC.
        FdafAec's overlap-save state is only valid on exact BLOCK
        boundaries, so a sub-block remainder rides in _aec_hold until the
        next chunk completes it (output trails input by <128ms; absolute
        positions stay exact via _aec_pos, so the reference never drifts)."""
        if not self._aec_enabled:
            return samples
        try:
            from . import aec
            self._aec_pos += len(samples)
            cap = int((AEC_EST_WINDOW_S + 2) * SAMPLE_RATE)
            self._aec_raw = np.concatenate([self._aec_raw, samples])[-cap:]
            self._aec_maybe_estimate()
            if self._aec is None:
                if len(self._aec_hold):     # just disengaged: flush raw
                    samples = np.concatenate([self._aec_hold, samples])
                    self._aec_hold = self._aec_hold[:0]
                return samples
            buf = (np.concatenate([self._aec_hold, samples])
                   if len(self._aec_hold) else samples)
            feed_n = (len(buf) // aec.BLOCK) * aec.BLOCK
            if not feed_n:
                self._aec_hold = buf
                return buf[:0]
            chunk = buf[:feed_n]
            start = self._aec_pos - len(buf)    # timeline pos of chunk[0]
            ref = self._sys_ref_slice(start - self._aec_delay, feed_n)
            out = self._aec.feed(chunk, ref)
            self._aec_hold = buf[feed_n:]
            self._aec_safety(chunk, out)
            return out
        except Exception:  # noqa: BLE001 — the caption pipeline outlives AEC
            _dbg("aec: mic-path ERROR, raw passthrough\n"
                 + traceback.format_exc())
            out = (np.concatenate([self._aec_hold, samples])
                   if len(self._aec_hold) else samples)
            self._aec_hold = self._aec_hold[:0]
            self._aec = None                    # bypass until re-estimation
            return out

    def _sys_ref_slice(self, start: int, n: int) -> np.ndarray:
        """sys history at absolute timeline [start, start+n) — the delay-
        compensated reference. The sys channel can lag the mic by a
        batching beat (~200ms): a missing tail is zero-padded, NEVER
        waited for (zero ref = zero coherence = the filter just idles on
        those bins for one block)."""
        with self.sys.lock:
            hist = self.sys.history
            a = max(0, start)
            b = min(len(hist), start + n)
            seg = hist[a:b].copy() if b > a else None
        out = np.zeros(n, dtype=np.float32)
        if seg is not None:
            out[a - start:a - start + len(seg)] = seg
        return out

    def _aec_maybe_estimate(self) -> None:
        """Periodic speaker→mic delay estimation — never assume it. First
        verdict once BOTH channels hold ~10s of the shared timeline, then
        every ~60s. delay==0 keeps the mic path a strict bypass (headset);
        a CHANGED delay resets the filter (a stale filter against a new
        alignment ADDS energy — the aec.py field trap, streaming form)."""
        from . import aec
        now = time.monotonic()
        if (self._aec_last_est is not None
                and now - self._aec_last_est < self._aec_est_interval):
            return
        win = int(AEC_EST_WINDOW_S * SAMPLE_RATE)
        with self.sys.lock:
            sys_len = self.sys._hist_len
        end = min(self._aec_pos, sys_len)
        if end < win:
            return
        lag = self._aec_pos - end       # sys batching lag, in samples
        if len(self._aec_raw) < win + lag:
            return
        self._aec_last_est = now
        with self.sys.lock:
            sys_seg = self.sys.history[end - win:end].copy()
        if float(np.abs(sys_seg).max()) < 1e-3:
            # far end silent so far (headset, or nobody has spoken yet):
            # keep polling on the short clock instead of burning a 60s beat
            self._aec_est_interval = AEC_EST_RETRY_S
            delay = 0
        else:
            self._aec_est_interval = AEC_EST_INTERVAL_S
            mic_seg = self._aec_raw[len(self._aec_raw) - win - lag:
                                    len(self._aec_raw) - lag]
            delay = aec.estimate_delay(mic_seg, sys_seg)
        if delay <= 0:
            if self._aec is not None or self._aec_delay:
                _dbg("aec: disengage (delay=0 — no coherent echo)")
            self._aec, self._aec_delay = None, 0
        elif delay != self._aec_delay or self._aec is None:
            _dbg(f"aec: engage delay={delay} "
                 f"({1000 * delay / SAMPLE_RATE:.0f}ms)"
                 + (" [filter reset]" if self._aec is not None else ""))
            self._aec = aec.FdafAec()
            self._aec_delay = delay
            self._aec_energy = []

    def _aec_safety(self, mic: np.ndarray, out: np.ndarray) -> None:
        """The known trap (aec.py: spurious delay peak => the filter ADDS
        energy). The offline pass reverts after the fact; live we watch a
        running ~5s in/out energy window and bail out until the next
        delay re-estimation rebuilds the filter."""
        e_in = float(np.dot(mic.astype(np.float64), mic.astype(np.float64)))
        e_out = float(np.dot(out.astype(np.float64), out.astype(np.float64)))
        self._aec_energy.append((len(mic), e_in, e_out))
        total = sum(n for n, _, _ in self._aec_energy)
        win = int(AEC_SAFETY_WINDOW_S * SAMPLE_RATE)
        while total - self._aec_energy[0][0] >= win:
            total -= self._aec_energy.pop(0)[0]
        if total < win:
            return                      # not enough evidence yet
        s_in = sum(e for _, e, _ in self._aec_energy)
        s_out = sum(e for _, _, e in self._aec_energy)
        if s_in > 1e-6 and s_out > AEC_SAFETY_RATIO * s_in:
            _dbg(f"aec: SAFETY disengage — out/in energy {s_out / s_in:.2f} "
                 f"over {total / SAMPLE_RATE:.1f}s (spurious delay?); "
                 f"bypass until re-estimation")
            self._aec = None

    def _aec_flush(self) -> None:
        """Emit the sub-block remainder (raw, <128ms) so finish() settles
        the complete take — a swallowed tail can clip the last word."""
        if len(self._aec_hold):
            tail, self._aec_hold = self._aec_hold, self._aec_hold[:0]
            self.mic.push(tail)

    REDIAR_INTERVAL_S = 60.0
    # First cheap re-cluster fires once this much speech exists. Lowered 12->6
    # (2026-08-23): with the diarizer pre-warmed at worker init (no 30s per-take
    # cold build), the bottleneck to sharp EARLY labels is this gate, so fire
    # the first settle sooner. Steady-state cadence (REDIAR_CHEAP_CADENCE_S) is
    # unchanged — this only speeds up the FIRST refinement at take start.
    REDIAR_MIN_SPEECH_S = 6.0
    # Cache-embed + frequent-recluster cadence (seconds). The SETTLED labels are
    # cadence-independent (the final re-cluster is identical), so cadence is a
    # pure latency-vs-flicker knob — NOT an accuracy one. The on-device live test
    # (the live preview experiment) showed 5 s re-clusters every ~8 s with the
    # speaker COUNT wobbling tick-to-tick (3<->2, 5<->4) => visible label flicker,
    # while 30 s makes the wobble invisible at no DER cost.
    # 30 -> 16 (2026-08-31): 16 s is the segmenter's own maturation period — a
    # frame's last overlapping 16 s window arrives 16 s after it, so 16 s is
    # the shortest cadence that never re-clusters immature segmentation (the
    # 5 s wobble was clustering half-voted frames, not a recluster-frequency
    # problem). Verified on-device: bank replay at 16 s vs 30 s produced
    # BIT-IDENTICAL settled output on all 8 NOTSOFAR held-out meetings, with
    # per-tick recluster cost 1.1-1.3 s (~8% of one core at 16 s cadence).
    # Halves how long a newly-joined speaker wears a borrowed label.
    # DICTATION_NOTES_REDIAR_CADENCE_S overrides.
    #: 4.8 s is the ARCHITECTURE cadence (2026-09-01 decision): freeze120
    #: was validated at 4.8 only; the legacy 16 s default leaked into an
    #: acceptance arm and scored 40.4 vs 26.5. Not a tuning knob anymore.
    REDIAR_CHEAP_CADENCE_S = 4.8   # legacy default; single-emission halves it
    # ADAPTIVE EARLY CADENCE (2026-08-27, 2a) — DEFAULT OFF, measured
    # harmful: on the AMI ES2004a replay a 10 s early cadence cut settle
    # time (280 s -> 112 s) but dropped EMIT-TIME attribution agreement
    # from 0.59 to ~0.4 — the extra early re-cluster runs on thin evidence
    # and replaces the live registry's centroids with worse ones, and feed
    # lines are immutable so emit-time labels are what Pokee keeps. Same
    # verdict family as the 5 s count-wobble that set the steady cadence.
    # The machinery stays env-tunable for device experiments:
    # DICTATION_NOTES_REDIAR_EARLY_S=<s> enables (e.g. 15-20), 0 = off (default);
    # DICTATION_NOTES_REDIAR_EARLY_WINDOW_S bounds the early phase.
    REDIAR_EARLY_CADENCE_S = 0.0
    REDIAR_EARLY_WINDOW_S = 120.0

    def _cheap_rediar_enabled(self) -> bool:
        """The cache-embed + frequent-recluster path is on by default
        (DICTATION_NOTES_CHEAP_REDIAR=1); =0 restores the old full re-diarize."""
        return os.environ.get("DICTATION_NOTES_CHEAP_REDIAR", "1") != "0"

    def _cheap_cadence_s(self) -> float:
        # ARM SANITY (2026-09-01): fractional env values pass through
        # verbatim — float() parse, no floor/int-cast/clamp (only <=0 falls
        # back to the default). DICTATION_NOTES_REDIAR_CADENCE_S=4.8 yields
        # exactly 4.8, as the label-age replay arms require.
        # Single-emission mode ships its own steady default: 4.8 s (3 window
        # hops) so a line's H=9.6 hold spans ~2 recluster ticks. Env wins as
        # ever; flag off, the constant is untouched.
        default_steady = self.REDIAR_CHEAP_CADENCE_S
        if _single_emission_enabled():
            # v5.1: halve the tick in mode — the tick quantizes BOTH turn
            # visibility (close->enq) and release granularity (ready->line);
            # 2.4s takes ~1.2s off each. Clustering runs 2x as often but the
            # bounded-cost machinery (age-freeze + backoff) absorbs it.
            default_steady = float(os.environ.get(
                "DICTATION_NOTES_TICK_S", "2.4"))
        try:
            steady = float(os.environ.get("DICTATION_NOTES_REDIAR_CADENCE_S",
                                          default_steady))
            if steady <= 0:
                steady = default_steady
        except (TypeError, ValueError):
            steady = default_steady
        try:
            early = float(os.environ.get("DICTATION_NOTES_REDIAR_EARLY_S",
                                         self.REDIAR_EARLY_CADENCE_S))
            window = float(os.environ.get("DICTATION_NOTES_REDIAR_EARLY_WINDOW_S",
                                          self.REDIAR_EARLY_WINDOW_S))
        except (TypeError, ValueError):
            early, window = self.REDIAR_EARLY_CADENCE_S, self.REDIAR_EARLY_WINDOW_S
        if early > 0 and time.monotonic() - self._session_t0 < window:
            return min(early, steady)
        return steady

    #: Diar-thread wake period. 1.6 s = the DiariZen window hop (a new
    #: complete 16 s window exists every 1.6 s), so each wake drains exactly
    #: ONE window and every cadence of interest is a whole number of wakes
    #: (4.8 s = 3, 8 s = 5, 16 s = 10). The previous 2.0 s wake quantized
    #: cadences UP to the next wake (4.8 -> 6.0) and drained 1-2 windows per
    #: wake; device smoke 2026-09-01 (X2, DICTATION_NOTES_REDIAR_CADENCE_S=4.8)
    #: measured 6.4-7.2 s tick spacing warm, 8-9 s while the 1.5x cost
    #: backoff (below) was binding. NPU work is unchanged: the same windows
    #: are embedded once either way.
    REDIAR_WAKE_S = 1.6


    def _consolidate_registry(self):
        """Non-destructive counterpart to _rediar_once: drain check_merge so any
        two centroids that drifted together (the same person split by a noisy
        early turn) are merged and history is relabeled — WITHOUT the global
        re-cluster that collapsed distinct speakers. check_merge also runs
        per-turn; this is a periodic safety sweep."""
        reg = self._registry
        if reg is None:
            return
        def _letter(idx):
            return f"Speaker {chr(ord('A') + idx)}" if idx < 26 else f"Speaker {idx + 1}"
        merges = 0
        for _ in range(8):
            merged = reg.check_merge()
            if not merged:
                break
            kept, absorbed = merged
            self.commits.relabel(_letter(absorbed), _letter(kept))
            for ch in (self.sys, self.mic):
                if ch.seg_speaker == _letter(absorbed):
                    ch.seg_speaker = _letter(kept)
            merges += 1
        if merges:
            _dbg(f"consolidate: merged {merges} drifted centroid(s), "
                 f"{reg.num_speakers} slots")


    def _apply_rediar_segs(self, segs, audio, tag: str = "rediar",
                           merge: bool = True, letter_of: dict | None = None):
        """Shared retro-relabel tail for the re-diar paths (blockonline,
        full, cheap cache-recluster): raw cluster segs -> provisional
        letters -> (optional) offline merge pass -> majority-overlap relabel
        of committed sys lines -> registry sync. `segs` is [(start_s, end_s,
        spk_idx)] from any diarizer; `audio` is the sys-channel slice those
        segs were computed over. `merge=False` for a diarizer whose segs are
        ALREADY globally clustered at the known count (blockonline) — the
        offline merge pass would re-merge distinct speakers there."""
        import json  # relabel_age/relabel_tick instrumentation lines below
        from .stream_helpers import merge_turns, merge_speaker_labels
        raw = merge_turns(segs)
        if not raw:
            return
        # raw clusters -> provisional letters, then the SAME offline merge
        # pass the accurate path runs (raw clusters over-split; without this
        # the converger made live WORSE: PBS 92.4 -> 67.9 agreement)
        prov: dict[int, str] = {}
        tdicts = []
        for a, b, spk in sorted(raw, key=lambda t: t[0]):
            if letter_of is not None:
                prov[spk] = letter_of.get(spk, f"Speaker {spk}")
            elif spk not in prov:
                prov[spk] = f"Speaker {chr(ord('A') + len(prov))}"
            tdicts.append({"start": a, "end": b, "speaker": prov[spk]})
        if merge:
            embed = self._registry._embed if self._registry is not None else None
            merge_speaker_labels(tdicts, audio, SAMPLE_RATE, embed_fn=embed,
                                 num_speakers=self._num_speakers)
        turns = [(t["start"], t["end"], t["speaker"]) for t in tdicts]
        order = {name: name for _, _, name in turns}
        # publish the settled turns for commit-time label sourcing (atomic
        # list swap; _settled_label_for only ever reads the whole binding)
        self._settled_turns = turns
        # relabel committed sys lines by majority span overlap
        relabels: dict[int, str] = {}
        # (idx -> (speaker-at-snapshot, start, end)) for the label-age
        # instrumentation below — captured from the SAME metas snapshot the
        # relabel decision reads, so old/new pairs match set_speakers'.
        prior: dict[int, tuple] = {}
        gate_skipped = span_missing = 0
        with self.commits.lock:
            metas = list(enumerate(self.commits.line_meta))
        for idx, m in metas:
            if m["channel"] != "sys":
                continue
            if m["start"] is None:
                span_missing += 1
                continue
            best, best_ov = None, 0.0
            for a, b, name in turns:
                ov = min(m["end"], b) - max(m["start"], a)
                if ov > best_ov:
                    best, best_ov = name, ov
            if best is None:
                continue
            dur = max(0.1, m["end"] - m["start"])
            if (best_ov / dur >= self.RELABEL_MIN_RATIO
                    or best_ov >= self.RELABEL_MIN_ABS_S):
                relabels[idx] = best
                prior[idx] = (m.get("speaker"), m["start"], m["end"])
            else:
                gate_skipped += 1
        changed = self.commits.set_speakers(relabels)
        # --- label-age instrumentation (wave1/label-age-arm, 2026-09-01) ---
        # LOGGING ONLY, zero behavior change. Feeds
        # an internal label-age analysis, which turns replay-arm runs
        # (DICTATION_NOTES_REDIAR_CADENCE_S=4.8/8/16) into the label-age ->
        # final-agreement curve used to pick the append-only partner-feed
        # emission hold H and to qualify the 4.8 s cadence arm.
        # STREAM-TIME CHOICE: ages are audio seconds, not wall clock. `audio`
        # here is the full sys-channel history slice BOTH re-diar callers pass
        # in (`hist = self.sys.history` from stream start — _rediar_once and
        # the cheap cache-recluster path), so len(audio)/SAMPLE_RATE is the
        # stream position of this tick on the SAME clock as the line_meta
        # start/end spans being aged. Wall clock would fold decode/queue/NPU
        # latency into the age and break replay-vs-live comparability.
        # Gating: same convention as every other diagnostic in this file —
        # _dbg itself no-ops unless DICTATION_NOTES_DIAG=1.
        tick_t = len(audio) / SAMPLE_RATE
        n_changed = 0
        for idx, new in sorted(relabels.items()):
            old, ls, le = prior[idx]
            if old == new:
                continue        # no-op relabel; set_speakers skipped it too
            n_changed += 1
            _dbg("relabel_age: " + json.dumps(
                {"line_start": round(ls, 3), "line_end": round(le, 3),
                 "age_s": round(tick_t - le, 3), "old": old, "new": new,
                 "tag": tag, "tick_t": round(tick_t, 3)}))
        # sys_line_s = committed sys line-seconds with known spans — the
        # analysis script's denominator (never-relabeled lines emit no
        # relabel_age line, so their mass is total minus relabeled).
        _dbg("relabel_tick: " + json.dumps(
            {"tag": tag, "n_relabels": n_changed,
             "n_lines": sum(1 for _, m in metas if m["channel"] == "sys"),
             "sys_line_s": round(sum(
                 m["end"] - m["start"] for _, m in metas
                 if m["channel"] == "sys" and m["start"] is not None), 3),
             "tick_t": round(tick_t, 3)}))
        # registry sync: future turns assign against the re-clustered voices
        if self._registry is not None:
            centroids, counts = [], []
            for spk in sorted({name for _, _, name in turns}):
                clips, total = [], 0
                for a, b, sp in sorted(turns, key=lambda t: -(t[1] - t[0])):
                    if sp != spk:
                        continue
                    clips.append(audio[int(a * SAMPLE_RATE):int(b * SAMPLE_RATE)])
                    total += clips[-1].shape[0]
                    if total >= 10 * SAMPLE_RATE:
                        break
                if not clips:
                    continue
                try:
                    v = np.asarray(self._registry._embed(np.concatenate(clips)),
                                   dtype=np.float32)
                    centroids.append(v)
                    counts.append(max(3, sum(1 for t in turns if t[2] == spk)))
                except Exception:  # noqa: BLE001
                    return
            if centroids:
                self._registry.replace_state(centroids, counts)
        _dbg(f"{tag}: {len(turns)} turns, {len(order)} speakers, "
             f"{changed} lines relabeled "
             f"(gate-skipped {gate_skipped}, span-missing {span_missing})")


    #: majority-overlap gate for retro-relabels: a line follows a corrector
    #: turn when the turn covers >= RELABEL_MIN_RATIO of it, or at least
    #: RELABEL_MIN_ABS_S seconds of it (long lines never reach a high ratio
    #: against fine-grained turns; replay showed ~16% of lines permanently
    #: gate-skipped at the old flat 0.5 ratio, stranding ghost letters).
    #: Defaults 0.0 (always follow the majority turn): replay-measured — the
    #: 0.5 flat ratio stranded ~16% of lines on stale ghost letters; at 0.0
    #: the final label set collapsed to exactly the true speaker count with
    #: equal attribution. The knobs stay for measurement.
    RELABEL_MIN_RATIO = 0.0
    RELABEL_MIN_ABS_S = 0.0

    def _bo_letters_by_overlap(self, segs, cents) -> dict:
        """Stable letters for a blockonline round: each cluster claims the
        letter that ALREADY labels most of the committed line-seconds it
        overlaps — the assignment that minimizes relabel churn by
        construction, and the one that stays stable when the clustering
        reorganizes between rounds (a centroid-similarity book drifted:
        replay grew 8-10 letters + hundreds of churning relabels on 13-min
        meetings). Greedy by overlap mass; a letter is claimed once; a
        cluster overlapping nothing labeled mints the next unused letter.
        Also refreshes _bo_letter_book {letter: centroid} for observability/
        registry sync."""
        with self.commits.lock:
            metas = []
            for _li, m in enumerate(self.commits.line_meta):
                if m.get("channel") == "sys" and m["start"] is not None:
                    metas.append((_li, m["speaker"], m["start"], m["end"]))
        # HYSTERESIS: update per-line stability, then claim from ANCHORED
        # lines only (label unchanged >=2 rounds). Fresh relabels are not
        # evidence — they are the previous round's OUTPUT, and counting them
        # is the feedback loop that sustained the C<->B letter oscillation.
        anchored = []
        anch_s = tot_s = 0.0
        # lazy init: test fakes construct sessions without __init__
        _rounds = getattr(self, "_line_label_rounds", None)
        if _rounds is None:
            _rounds = {}
            self._line_label_rounds = _rounds
        for _li, spk, ls, le in metas:
            prev = _rounds.get(_li)
            rounds = prev[1] + 1 if prev and prev[0] == spk else 1
            _rounds[_li] = (spk, rounds)
            dur = max(0.0, le - ls)
            tot_s += dur
            if rounds >= 2:
                anchored.append((spk, ls, le))
                anch_s += dur
        # bootstrap: while little is anchored (early meeting), use everything
        ev = anchored if (tot_s > 0 and anch_s >= 0.3 * tot_s) else             [(spk, ls, le) for _, spk, ls, le in metas]
        claims: dict[int, dict] = {}
        claims_full: dict[int, dict] = {}
        for a, b, idx in segs:
            per = claims.setdefault(idx, {})
            perf = claims_full.setdefault(idx, {})
            for spk, ls, le in ev:
                ov = min(b, le) - max(a, ls)
                if ov > 0:
                    per[spk] = per.get(spk, 0.0) + ov
            for _li2, spk, ls, le in metas:
                ov = min(b, le) - max(a, ls)
                if ov > 0:
                    perf[spk] = perf.get(spk, 0.0) + ov
        # Assignment with BOOK-CENTROID arbitration (2026-09-01 v2): mass
        # ranks the candidates, but when two clusters contest one letter the
        # winner is the one whose centroid matches the letter's PREVIOUS
        # centroid in the session book — orientation is decided by voice
        # continuity (stable across rounds) instead of by which claim-mass
        # is fractionally larger this round (the coin-flip that oscillated
        # C<->B). First round (empty book) and book-less letters fall back
        # to mass, preserving the demote->mint correction path (the v1
        # "share the letter" damping froze mislabeled lines and cost
        # conf +5-12 — removed).
        _arb_on = os.environ.get("DICTATION_NOTES_LB_ARB", "1") != "0"
        book_prev = dict(getattr(self, "_bo_letter_book", {}) or {})

        def _sim_to_book(cl_idx, letter):
            c = cents.get(cl_idx) if cents else None
            b = book_prev.get(letter)
            if c is None or b is None:
                return None
            cn = float(np.linalg.norm(c)); bn = float(np.linalg.norm(b))
            if cn <= 0 or bn <= 0:
                return None
            return float(np.dot(c, b)) / (cn * bn)

        order = sorted(claims, key=lambda i: -sum(claims[i].values()))
        letter_of = {}
        holder: dict = {}          # letter -> cluster idx currently holding it
        queue = list(order)
        guard = 0
        while queue and guard < 8 * (len(order) + 1):
            guard += 1
            idx = queue.pop(0)
            for spk in sorted(claims[idx], key=lambda k: -claims[idx][k]):
                cur = holder.get(spk)
                if cur is None:
                    holder[spk] = idx
                    letter_of[idx] = spk
                    break
                if _arb_on:
                    mine = _sim_to_book(idx, spk)
                    his = _sim_to_book(cur, spk)
                    if mine is not None and his is not None and mine > his + 0.02:
                        # voice continuity says the letter is MINE: take it,
                        # displaced holder re-queues for its next choice.
                        holder[spk] = idx
                        letter_of[idx] = spk
                        letter_of.pop(cur, None)
                        queue.append(cur)
                        break
                continue
        used = set(holder)
        existing = used | {spk for _, spk, _, _ in metas}
        nxt = 0
        for idx in {sp for _, _, sp in segs}:
            if idx in letter_of:
                continue
            # FULL-EVIDENCE fallback (2026-09-01): a cluster whose anchored
            # overlap lost every contest may still cover a letter that was
            # freshly (last round) written to its lines — not yet anchored,
            # but a far better identity than a fresh mint (minting here was
            # the F/G/H re-homing churn). Take the strongest UNUSED letter
            # from all-lines evidence first; mint only when nothing fits.
            _fb = [s for s in sorted(claims_full.get(idx, {}),
                                     key=lambda k: -claims_full[idx][k])
                   if s not in used]
            if _fb:
                letter_of[idx] = _fb[0]
                used.add(_fb[0])
                continue
            while True:
                cand = (f"Speaker {chr(ord('A') + nxt)}" if nxt < 26
                        else f"Speaker {nxt}")
                nxt += 1
                if cand not in existing:
                    break
            letter_of[idx] = cand
            existing.add(cand)
        self._bo_letter_book = {letter_of[i]: cents[i]
                                for i in letter_of if i in cents}
        return letter_of

    def _cheap_rediar_once(self) -> bool:
        """Cache-embed + frequent-recluster re-diar (the shipping streaming
        design; the reclustering experiment). Segments+embeds only the NEW
        audio into a growing DiariZen embedding bank (heavy WavLM runs ONCE per
        window, O(new audio), on the NPU), then re-clusters ONLY VBx over ALL
        accumulated embeddings (~1 s CPU regardless of meeting length) and
        retro-relabels. Settled labels reach the offline ceiling (DER 6.97
        test-12) at ~50x lower per-call cost than the full re-diarize, so it can
        run at a low cadence (fast label settle). Returns True if it ran the
        bank path; False to let the caller fall back to the full re-diar."""
        # The bank — and its ~30 s HTP seg/embed session build — is created
        # eagerly by _warmup_dz_bank at session start, because that build holds
        # the GIL and would freeze audio ingestion if it ran here mid-meeting
        # (the live-test stall). Until warmup finishes we skip (the registry
        # still labels every turn live meanwhile); if it failed, fall back to
        # the legacy full re-diar.
        if self._watchdog_stop.is_set():
            return True  # take stopping — don't start a heavy re-cluster
        if self._bo_bank is not None:
            with self.sys.lock:
                hist = self.sys.history
            audio = np.array(hist)
            if not len(audio):
                return True
            remote_k = (max(1, self._num_speakers - 1)
                        if self._num_speakers else None)
            ran = self._bo_bank.update(audio)
            segs, cents = self._bo_bank.diarize_with_centroids(
                audio, num_speakers=remote_k)
            if segs:
                # STABLE letters across rounds: cluster indices permute per
                # re-cluster, and letting each round mint fresh first-
                # appearance letters fragmented the namespace (replay: final
                # labels spanned A..J, 99 churning relabel events for 32
                # lines). Match this round's centroids to the session letter
                # book instead; only a genuinely new voice gets a new letter.
                letter_of = self._bo_letters_by_overlap(segs, cents)
                # already globally clustered at k — the offline merge pass
                # would re-merge distinct speakers, so skip it (merge=False)
                self._apply_rediar_segs(segs, audio, tag="blockonline_rediar",
                                        merge=False, letter_of=letter_of)
                # registry sync: future emit-time turns assign against the
                # book's voices so their letters agree with the corrected
                # history (book letters are minted in index order, matching
                # the registry's idx->letter scheme)
                book = self._bo_letter_book
                prefix = [f"Speaker {chr(ord('A') + i)}"
                          for i in range(len(book))]
                if (self._registry is not None and book
                        and sorted(book) == prefix):
                    try:
                        self._registry.replace_state(
                            [book[p] for p in prefix], [3] * len(book))
                    except Exception:  # noqa: BLE001
                        pass
            else:
                _dbg(f"blockonline_rediar: warming ({ran} new windows, "
                     f"{len(audio)/SAMPLE_RATE:.0f}s audio)")
            return True
        bank = self._dz_bank   # snapshot — teardown nulls the attr mid-tick
        if bank is None:
            return False if self._dz_warmup_failed else True
        with self.sys.lock:
            hist = self.sys.history     # view; stable after release (see
            # `history` — appends never touch a returned view's prefix)
        audio = np.array(hist)          # O(session) copy OFF the lock (the
        # old under-lock `.copy()` blocked pushes for the whole memcpy)
        if not len(audio):
            return True         # nothing to do yet, but the path IS active
        # remote_k = participants - 1 (sys carries the REMOTE speakers; the
        # local user is on mic). Same known-k contract as the full path.
        remote_k = max(1, self._num_speakers - 1) if self._num_speakers else None
        # incremental: embed each new full 16 s window ONCE (the heavy stage).
        _t_up = time.monotonic()
        _added = bank.update(audio)
        _dur_up = time.monotonic() - _t_up
        # frequent re-cluster: VBx over ALL accumulated embeddings -> global
        # labels (cheap). NO over-split prune on this path (it hurts — the
        # retro-relabel already fixes over-split; the reclustering experiment).
        total_dur = len(audio) / SAMPLE_RATE
        _t_cl = time.monotonic()
        segs, cents = bank.cluster_with_centroids(
            audio, total_dur=total_dur, num_speakers=remote_k)
        _dur_cl = time.monotonic() - _t_cl
        _dbg(f"rediar_split: embed_update={_dur_up:.2f}s ({_added} new win) "
             f"cluster={_dur_cl:.2f}s")
        if not segs:
            return True         # bank still warming up (sub-window / all-silent)
        if getattr(self, "_hold", None) is not None:
            # SINGLE-EMISSION MODE (DICTATION_NOTES_SINGLE_EMISSION=1): the
            # cluster's freshly published LastState snapshot is the ONLY
            # emission source. The whole retro-relabel tail below
            # (_bo_letters_by_overlap / _apply_rediar_segs / registry
            # sync) is SKIPPED — released lines are final by construction,
            # so there is nothing to correct and no relabel may ever land.
            self._single_emission_tick(bank, total_dur)
            return True
        # STABLE letters (2026-09-01): same overlap-continuity assignment as
        # the blockonline path above — VBx cluster indices permute on every
        # re-cluster, and leaving _apply_rediar_segs to its per-round first-
        # appearance minting fragmented the namespace on the DEFAULT
        # corrector too (replay: labels spanned A..J, 99 churning relabel
        # events for 32 lines). The DZ centroids live in the same ResNet
        # space the registry matches in (single embedder stack, 2026-08-31),
        # so the letter book they refresh is registry-valid.
        letter_of = self._bo_letters_by_overlap(segs, cents)
        # merge=False (2026-09-01): VBx has ALREADY globally clustered the
        # bank at known k — the offline merge pass would re-merge distinct
        # speakers, exactly the reason the blockonline path disabled it on
        # 2026-08-28. Latent with CAM++ (distinct-speaker sims ~0.3-0.5 sat
        # under HIGH_SIM 0.70); the ResNet clip embedder (single-stack,
        # 2026-08-31) puts distinct speakers at 0.86-0.93 and the per-tick
        # merge collapsed 5 real speakers to 2-4 on the night wrapper replay
        # (final k 2-4, conf 26-53%). The 0.70 threshold was never valid for
        # this embedder.
        self._apply_rediar_segs(segs, audio, tag="cheap_rediar", merge=False,
                                letter_of=letter_of)
        # registry sync: same guarded push as the blockonline path — future
        # emit-time turns assign against the book's voices so their letters
        # agree with the corrected history (book letters must form the exact
        # A..N prefix, since the registry names speakers by index).
        book = self._bo_letter_book
        prefix = [f"Speaker {chr(ord('A') + i)}"
                  for i in range(len(book))]
        if (self._registry is not None and book
                and sorted(book) == prefix):
            try:
                self._registry.replace_state(
                    [book[p] for p in prefix], [3] * len(book))
            except Exception:  # noqa: BLE001
                pass
        return True

    # ---- single-emission mode (DICTATION_NOTES_SINGLE_EMISSION=1) --------------
    # A caption line = text + speaker, emitted ONCE after the D=H hold,
    # immutable. Boundaries come from the bank's matured neural segmentation
    # (neural_turns), text from the internal carrier sys lines the legacy
    # ASR/turn machinery keeps committing to lines/line_meta (feed-suppressed
    # by _CommitLog.add). Mode's source of truth is self._hold (not None).

    def _text_for_span(self, start_s: float, end_s: float) -> str:
        """Text for a neural turn span: join, in time order, the internal
        carrier sys lines whose committed span overlaps [start_s, end_s) by
        MORE than half of the line's own duration (majority rule — the same
        overlap discipline the retro-relabel uses). `lines` stores the
        speaker prefix embedded ("[Speaker X] text"); strip it via the
        speaker line_meta names."""
        parts = []
        with self.commits.lock:
            for i, m in enumerate(self.commits.line_meta):
                if m.get("channel") != "sys":
                    continue
                ls, le = m.get("start"), m.get("end")
                if ls is None or le is None:
                    continue
                dur = le - ls
                # Midpoint rule: a carrier belongs to the turn holding its
                # midpoint — strictly more permissive than the old majority
                # (>50%-of-carrier) overlap and still exactly-once. The
                # majority rule silently vaporized every carrier straddling
                # a speaker change (<=50% on both sides), which halved the
                # emitted lines (semode device run: ~34 vs ~65 lines/meeting,
                # miss 29.6 DER).
                mid = ls if dur <= 0 else (ls + le) / 2.0
                if not (start_s <= mid < end_s):
                    continue
                text = self.commits.lines[i]
                prefix = f"[{m['speaker']}] "
                if text.startswith(prefix):
                    text = text[len(prefix):]
                parts.append((ls, i, text))
        parts.sort()
        return " ".join(t for _, _, t in parts).strip()

    def _emit_single_emission_line(self, line) -> None:
        """One hold-released Line -> ONE immutable partner-feed event.
        Routed through _CommitLog.add (feed_only=True) so the commit gate
        still fronts every emission and the AST ratchet holds."""
        self.commits.add(_spk_letter(int(line.speaker)), line.text,
                         channel="sys", span=(line.start, line.end),
                         feed_only=True)

    # ---- turn-driven span decode (DICTATION_NOTES_TURN_DECODE, default ON) ----
    # NEURAL TURNS are the single cadence for BOTH lanes (owner call,
    # 2026-09-01): when the diar lane closes a turn [t0,t1], the wire decodes
    # exactly that audio span and the text is born 1:1 with the turn — no
    # carrier midpoint join, no granularity mismatch, no silently-consumed
    # textless spans (the carrier rule vaporized matured turns whose span
    # held no carrier midpoint: device semode2 miss 27 with conf 8). The
    # 9.6 s hold gives ample time (1-3 s audio ~ 1 s decode); the hold's
    # text_grace + carrier-join fallback cover a failed/wedged decode.
    #: a turn is enqueued once its end has been stable for one hop tick
    _TURN_DECODE_STABLE_S = 1.6
    #: symmetric acoustic pad so edge words are not clipped mid-phone
    _TURN_DECODE_PAD_S = 0.12

    def _turn_decode_on(self) -> bool:
        return os.environ.get("DICTATION_NOTES_TURN_DECODE", "1") != "0"

    def _ensure_turn_decode_state(self) -> None:
        if getattr(self, "_turn_texts", None) is None:
            self._turn_texts = {}            # key -> str (decoded) | None (failed)
            self._turn_decode_q = []
            self._turn_decode_inflight = set()
            self._turn_decode_lock = threading.Lock()
            self._turn_decode_thread = None
            # key-drift rescue: first-ask stamp per start bucket (NOT per
            # exact key — drift mints a new key every tick, which would
            # reset the clock forever) + patience before midpoint assembly.
            self._turn_ask_first = {}
            self._turn_text_patience_s = float(os.environ.get(
                "DICTATION_NOTES_TURN_TEXT_PATIENCE_S", "4.0"))
            # SETTLE-TRIGGERED DECODE-ONCE (design of 2026-09-02):
            # decoding every drift variant doubled ASR demand (2378 decodes
            # for ~1197 real turns) and spawned the whole key-drift disease.
            # Now a turn is decoded EXACTLY ONCE, when its bounds have been
            # seen unchanged across two consecutive derivations (~settled;
            # empirically most turns stop moving within the hold window) or
            # forced at hold age as the deadline fallback. Results are ALSO
            # stored per start-bucket so the release-time lookup tolerates
            # micro-drift — including authoritative EMPTY (an overlap turn's
            # "" now consumes immediately instead of squatting 21.6s at the
            # head of the in-order queue).
            self._turn_bounds_seen = {}      # bucket -> [start, end, stable_ticks]
            self._turn_decoded_buckets = set()
            self._turn_decoded_starts = {}   # bucket -> decoded turn-start
                                             # (jitter-proof dedup, DEFECT-2)
            self._turn_bucket_texts = {}     # bucket -> (start, end, text|None)
            self._turn_enq_at = {}           # key -> now_s at enqueue (telemetry)

    @staticmethod
    def _turn_key(start_s: float, end_s: float):
        return (round(float(start_s), 2), round(float(end_s), 2))

    @staticmethod
    def _turn_bucket(start_s: float) -> float:
        return round(float(start_s) * 2.0) / 2.0

    def _decode_when_settled(self, s0: float, e0: float, now_s: float) -> None:
        """Request the ONE decode for this turn when its bounds settle
        (unchanged across two consecutive tick derivations, tolerance 0.25s)
        or force it at hold age (deadline fallback for turns that keep
        drifting near cluster churn). Never re-request a decoded bucket."""
        self._ensure_turn_decode_state()
        b = self._turn_bucket(s0)
        if b in self._turn_decoded_buckets:
            return
        # JITTER-PROOF DEDUP (DEFECT-2 fix, 2026-09-03): the settle check
        # below tolerates +/-0.25s start jitter, but the decode-once guard
        # above keys on the 0.5s START-BUCKET alone — a start jittering
        # ACROSS a bucket edge (43.23 -> 43.43: buckets 43.0 vs 43.5)
        # minted a "new" bucket and re-decoded the SAME turn (proof-run
        # ledgers: 8/115 and 21/208 decode events were such duplicates).
        # Before treating this bucket as new, check the neighbor buckets
        # for an already-decoded start within the settle tolerance — that
        # IS this turn, already decoded. DELIBERATE re-decodes are
        # untouched: the empty-carry superset re-decode lives on the
        # seg_base window path (KEEP_EMPTY_WINDOW, not here), a hold-FORCED
        # decode only ever fires for a never-decoded turn, and a genuinely
        # different / regrown start (> 0.25s away) still decodes.
        for nb in (b - 0.5, b + 0.5):
            ds = self._turn_decoded_starts.get(nb)
            if ds is not None and abs(ds - s0) <= 0.25:
                return
        # v5 (review of 2026-09-02): an INTERIOR turn — one whose
        # end sits safely behind the state frontier — was closed by a real
        # silence gap or speaker change, so its right boundary cannot grow;
        # decode it on FIRST sighting (the v4 2-sighting rule made even
        # trivially-final turns wait a median 13.2s, 44% never settling at
        # all). Only a FRONTIER turn (still accruing state) keeps the
        # conservative 2-sighting / hold-age path.
        interior = (now_s - e0) >= float(os.environ.get(
            "DICTATION_NOTES_FRONTIER_GUARD_S", "2.5"))
        rec = self._turn_bounds_seen.get(b)
        if interior:
            settled = True
        elif rec is None or abs(rec[0] - s0) > 0.25 or abs(rec[1] - e0) > 0.25:
            self._turn_bounds_seen[b] = [s0, e0, 0]
            settled = False
        else:
            rec[2] += 1
            rec[0], rec[1] = s0, e0
            settled = rec[2] >= 1
        hold_s = float(getattr(getattr(self, "_hold", None), "hold_s", 9.6))
        forced = (now_s - e0) >= hold_s
        if not settled and not forced:
            return
        self._turn_decoded_buckets.add(b)
        self._turn_decoded_starts[b] = float(s0)
        self._turn_enq_at[self._turn_key(s0, e0)] = now_s
        _dbg(f"turn_settled: {s0:.1f}-{e0:.1f} age={now_s - e0:.1f}"
             f" {'FORCED' if forced and not settled else 'stable'}")
        self._request_turn_decode(s0, e0)
        if len(self._turn_bounds_seen) > 512:
            with self._turn_decode_lock:
                wm = float(getattr(getattr(self, "_hold", None),
                                   "_released_until", 0.0) or 0.0)
                cut = wm - 600.0
                for d in (self._turn_bounds_seen, self._turn_bucket_texts):
                    for k in [k for k, v in d.items() if (v[1] if isinstance(v, (list, tuple)) else 0) < cut]:
                        d.pop(k, None)
                self._turn_decoded_buckets = {k for k in self._turn_decoded_buckets
                                              if k >= cut}
                self._turn_decoded_starts = {
                    k: v for k, v in self._turn_decoded_starts.items()
                    if k >= cut}

    def _request_turn_decode(self, start_s: float, end_s: float) -> None:
        self._ensure_turn_decode_state()
        key = self._turn_key(start_s, end_s)
        with self._turn_decode_lock:
            if key in self._turn_texts or key in self._turn_decode_inflight:
                return
            # DEDUP by turn-start (2026-09-02, server trace analysis): the turn
            # boundary drifts every tick, minting a new exact (start,end) key
            # that slips past the guard above — 892/2378 decodes (38%) were the
            # SAME turn-start re-decoded (435 pure end-jitter <1s), ~1249s of
            # engine-lock time that starved NEW turns' decodes (the late-text
            # miss driver). Allow only ONE in-flight decode per turn-start; a
            # genuinely-grown boundary re-enqueues on a later tick once this one
            # lands, and the rescue backfills the final turn from the partial.
            for (qs, _qe) in self._turn_decode_inflight:
                if abs(qs - float(start_s)) < 0.3:
                    return
            self._turn_decode_inflight.add(key)
            self._turn_decode_q.append((float(start_s), float(end_s)))
            alive = (self._turn_decode_thread is not None
                     and self._turn_decode_thread.is_alive())
            if not alive:
                self._turn_decode_thread = threading.Thread(
                    target=self._turn_decode_worker,
                    name="turn-decode", daemon=True)
                self._turn_decode_thread.start()

    def _turn_decode_worker(self) -> None:
        """Drains the span-decode queue at BACKGROUND priority (yields the
        engine to live-caption/finalize traffic) and exits when idle — a
        later request starts a fresh thread."""
        while True:
            with self._turn_decode_lock:
                # QUEUE ORDER FIX (2026-09-02): FIFO drowned fresh turns behind
                # a backlog of stale span requests (drift variants of already-
                # released/dropped turns kept re-enqueueing) — decode results
                # were landing 90-150s after their span while the hold's
                # deadline is 21.6s, so EVERY line ran ~25s late (measured
                # per-line median; design floor ~12s). Serve NEWEST end first
                # (nearest its own deadline with time still left) and prune
                # entries the emission watermark already passed (their line
                # was released or dropped — decoding them is pure waste).
                wm = -1.0
                hold = getattr(self, "_hold", None)
                if hold is not None:
                    wm = float(getattr(hold, "_released_until", -1.0))
                q = self._turn_decode_q
                while q:
                    # EDF, not newest-first (2026-09-02 burst anatomy): the
                    # in-order release always waits on the OLDEST pending
                    # turn, so newest-first starved exactly that turn — every
                    # ready line queued behind it (release bursts, p90 block
                    # 52-102s), old turns starved to deadline (30864: 54
                    # drops), and the freshest drift variant's unsettled
                    # boundaries garbled words (30861 WER +5.8, dup runs
                    # 5->9). Earliest END first = serve what the release
                    # line needs next, with settled boundaries; the queue
                    # hygiene (watermark gate + dedup + stale prune) is what
                    # makes EDF viable where the old FIFO drowned.
                    idx = min(range(len(q)), key=lambda i: q[i][1])
                    start_s, end_s = q.pop(idx)
                    if end_s > wm + 1e-6:
                        break
                    self._turn_decode_inflight.discard(
                        self._turn_key(start_s, end_s))   # stale: skip
                else:
                    self._turn_decode_thread = None
                    return
            key = self._turn_key(start_s, end_s)
            text = None
            try:
                ch = self.sys
                with ch.lock:
                    hist = ch.history
                audio = np.array(hist)
                a0 = max(0, int((start_s - self._TURN_DECODE_PAD_S)
                                * SAMPLE_RATE))
                a1 = min(len(audio), int((end_s + self._TURN_DECODE_PAD_S)
                                         * SAMPLE_RATE))
                clip = audio[a0:a1]
                if len(clip) >= int(0.2 * SAMPLE_RATE):
                    eng = getattr(ch, "engine", None)
                    lang = getattr(ch, "language", None)
                    if eng is not None:
                        from .engine_lock import BACKGROUND
                        _prio = (CAPTION if os.environ.get(
                            "DICTATION_NOTES_TURNFIRST", "1") != "0" else BACKGROUND)
                        with _ENGINE_LOCK.held(_prio, "turn_decode"):
                            r = eng.transcribe(clip, language=lang)
                        text = (r.get("text") or "").strip()
                else:
                    text = ""             # sub-perceptual span: no words
            except Exception:  # noqa: BLE001 — fall back to carrier join
                _dbg("turn_decode ERROR (carrier fallback)\n"
                     + traceback.format_exc())
                text = None
            with self._turn_decode_lock:
                self._turn_decode_inflight.discard(key)
                self._turn_bucket_texts[self._turn_bucket(start_s)] = (
                    start_s, end_s, text)
                _enq = self._turn_enq_at.pop(key, None)
                _prev = self._turn_texts.get(key, "__absent__")
                # OVERWRITE GUARD (2026-09-02): never let a later empty decode
                # clobber text already won for this exact span. Empties on
                # heavy-overlap meetings (half-B: 923/1536 decodes empty, same
                # duration profile as text ones — not short-span) were
                # overwriting good text on re-decode (257 spans re-decoded up
                # to 5x). Keep the first non-empty result for a key.
                if _prev not in ("__absent__", None) and _prev and not text:
                    _dbg(f"turn_decode_overwrite_blocked: span={start_s:.1f}-"
                         f"{end_s:.1f} kept={len(_prev)}ch dropped_empty")
                else:
                    self._turn_texts[key] = text
            # miss forensics (2026-09-02): name every no-line path. A span
            # that decodes to EMPTY becomes a silent skip in _release — count
            # it here so the residual miss can be attributed, not guessed.
            _dbg(f"turn_decode_result: span={start_s:.1f}-{end_s:.1f} "
                 f"chars={len(text) if text else 0}"
                 + (f" enq_at={_enq:.1f}" if _enq is not None else "")
                 + f"{' EMPTY' if text is not None and not text else ''}"
                 + f"{' FAILED' if text is None else ''}")

    def _turn_text_for_span(self, start_s: float, end_s: float):
        """span_text_fn for the hold: decoded text when ready; the carrier
        join when the decode FAILED; None while the decode is in flight
        (the hold waits within its grace)."""
        self._ensure_turn_decode_state()
        # BUCKET-FIRST (settle-decode contract): the one settled decode for
        # this turn serves any micro-drifted release key — coverage-guarded,
        # and an authoritative EMPTY ("") propagates immediately (no 21.6s
        # squat at the head of the in-order queue).
        with self._turn_decode_lock:
            for b in (self._turn_bucket(start_s),
                      self._turn_bucket(start_s) - 0.5,
                      self._turn_bucket(start_s) + 0.5):
                hit = self._turn_bucket_texts.get(b)
                if hit is None:
                    continue
                hs, he, ht = hit
                inter = min(he, end_s) - max(hs, start_s)
                union = max(he, end_s) - min(hs, start_s)
                if union > 0 and inter / union >= 0.7 and abs(hs - start_s) <= 0.6:
                    if ht is not None:
                        return ht          # incl. "" = authoritative empty
                    break                  # failed decode -> carrier fallback below
        key = self._turn_key(start_s, end_s)
        with self._turn_decode_lock:
            if key in self._turn_texts:
                v = self._turn_texts[key]
                pending = False
            elif key in self._turn_decode_inflight:
                v, pending = None, True
            else:
                v, pending = None, False
        if v is not None and v != "" :
            return v                       # decoded 1:1 with the turn
        if v == "":
            return ""                      # authoritative empty decode
        if not pending and key not in getattr(self, "_turn_texts", {}):
            # never enqueued (e.g. flush before any tick saw the turn):
            # enqueue for a later chance, wait within grace
            self._request_turn_decode(start_s, end_s)
        elif not pending:
            return self._text_for_span(start_s, end_s)  # failed -> carrier join
        # KEY-DRIFT RESCUE (2026-09-02). Per-hole log walk on the full-8
        # stitch run: 50-100% of clean-meeting miss holes were turns that
        # DEADLINE-dropped while overlapping decoded spans held real text
        # (chars 66-422) — the hold asks with the CURRENT (start,end) key,
        # but the turn boundary drifts every tick (stitching extends ends),
        # so the exact-key lookup chases and never lands. After a bounded
        # patience, hand the turn every decoded span whose MIDPOINT lies
        # inside it (turns are non-overlapping, so midpoint ownership is
        # 1:1 — no cross-turn steal, unlike the failed >50%-of-span rule),
        # whole spans joined in time order, never sliced.
        now_m = time.monotonic()
        bucket = round(start_s * 2.0) / 2.0
        first = self._turn_ask_first.setdefault(bucket, now_m)
        if now_m - first >= self._turn_text_patience_s:
            with self._turn_decode_lock:
                cands = [
                    (ks, ke, t) for (ks, ke), t in self._turn_texts.items()
                    if t and start_s <= (ks + ke) / 2.0 < end_s
                    and (min(ke, end_s) - max(ks, start_s)) >= 0.5 * (ke - ks)
                ]
            if cands:
                cands.sort()
                joined = " ".join(t for _, _, t in cands)
                _dbg(f"turn_text_rescue: {start_s:.1f}-{end_s:.1f} <- "
                     f"{len(cands)} midpoint span(s), {len(joined)} chars")
                return joined
        if len(self._turn_ask_first) > 4096:
            cut = start_s - 600.0
            self._turn_ask_first = {b: t for b, t in
                                    self._turn_ask_first.items() if b >= cut}
        return None                        # keep waiting (grace/deadline)

    def _single_emission_tick(self, bank, now_s: float) -> None:
        """Per-recluster emission step: derive turns from the bank's
        published snapshot, request span decodes for freshly-closed turns,
        offer everything to the hold, and emit whatever matured (turn tail
        older than hold_s, label evidence present, span text ready)."""
        from . import neural_turns
        state = getattr(bank, "last_state", None)
        if state is None:
            return
        # diarizer decision horizon = latest audio time the state covers.
        # Passed to the hold so the release watermark stays >= safe_lag_s
        # behind it (safe-horizon watermark, 2026-09-03): turns that only
        # MATERIALIZE in the state 20-40s after their audio (short
        # backchannels' appear_lag) must not find the watermark already past
        # them — that was Config A's coverage regression (75 all-live lines
        # but miss 37.5 vs strict 11.2 on MTG_30860).
        _frontier = ((state.starts[-1] / 16000.0
                      + state.num_frames * state.frame_dur)
                     if getattr(state, "starts", None) else 0.0)
        # Default flipped to "local" (2026-09-09): the legacy path
        # starves on real meetings — measured 0 live commits over two 23-min
        # device takes (and the 2026-09-02 X2 E2E in the carrier note:
        # "open-segment cap" x6541, stable_through 0.0, only the flush
        # emitted), while the 2026-09-03 local-geometry A/B on the same
        # device released lines from t=20s. The known caveat (geometry mixed
        # into confidence values) degrades confidence calibration, not
        # stability — strictly better than a live path that emits nothing.
        # DICTATION_NOTES_TURN_GEOMETRY=legacy restores the old path for A/B.
        if _turn_geometry_local():
            # 2026-09-02 turn architecture: geometry from LOCAL segmentation
            # activity (bins only — immune to global recluster churn),
            # persisted + frozen in the TurnBook; identity late-bound at
            # emission by the confidence ladder. turns carry speaker -1.
            if getattr(self, "_turn_book", None) is None:
                from .turn_book import TurnBookV2
                self._turn_book = TurnBookV2()
            cuts = neural_turns.turns_from_activity(state)
            turns = self._turn_book.observe(cuts, _frontier, now_s)
            if getattr(self, "_hold", None) is not None:
                self._turn_book.prune(self._hold._released_until)
        else:
            turns = neural_turns.turns_from_state(state)
        # miss forensics: how much speech do the TURNS themselves cover?
        _tc = sum(e - s0 for s0, e, _k in turns)
        _dbg(f"turns_cover: {_tc:.1f}s over {len(turns)} turns "
             f"(state to {getattr(state, 'starts', [0])[-1] / 16000 + 16:.0f}s)")
        if os.environ.get("DICTATION_NOTES_TURN_TRACE"):
            # Phase-0 settle profiling (2026-09-02): dump the per-tick turn
            # list so an offline pass can attribute every boundary move
            # (wiggle/split/merge/label-flip) and measure settle-age tails.
            # Append-only jsonl; diag-grade, default off.
            try:
                import json as _json
                rec = {"now": round(now_s, 2),
                       "turns": [[round(s0, 2), round(e, 2), int(k)]
                                 for s0, e, k in turns]}
                # Geometry-Layer A/B (2026-09-02 turn architecture): dump the
                # LOCAL-activity variant alongside, so one replay yields the
                # stability verdict for both geometries.
                try:
                    rec["turns_act"] = [
                        [round(s0, 2), round(e, 2), int(k)]
                        for s0, e, k in neural_turns.turns_from_activity(state)]
                except Exception:  # noqa: BLE001 — prototype, never break tick
                    pass
                with open(os.environ["DICTATION_NOTES_TURN_TRACE"], "a",
                          encoding="utf-8") as _tf:
                    _tf.write(_json.dumps(rec) + "\n")
            except OSError:
                pass
        span_fn = None
        if self._turn_decode_on():
            _wm_gate = float(getattr(getattr(self, '_hold', None), '_released_until', -1.0) or -1.0)
            for s0, e0, _spk in turns:
                if now_s - e0 >= self._TURN_DECODE_STABLE_S:
                    if e0 > _wm_gate + 1e-6:
                        self._decode_when_settled(s0, e0, now_s)
            span_fn = self._turn_text_for_span
        released = self._hold.offer(turns, now_s,
                                    label_fn=bank.label_for_span,
                                    text_fn=self._text_for_span,
                                    span_text_fn=span_fn,
                                    frontier_s=_frontier)
        for line in released:
            self._emit_single_emission_line(line)
        if released:
            _dbg(f"single_emission: released {len(released)} line(s) "
                 f"at now_s={now_s:.1f}")
        # miss forensics (2026-09-02): reconcile turns vs lines every tick —
        # matured turns that produced NO line are the residual-miss suspects;
        # name each with why (watermark-consumed vs still-pending).
        _mat = [(s, e) for s, e, _k in turns
                if now_s - e >= self._hold.hold_s
                and e > self._hold._released_until + 1e-6]
        if _mat:
            _rel = {(l.start, l.end) for l in released}
            _gone = [f"{s:.1f}-{e:.1f}" for s, e in _mat
                     if (round(s, 2), round(e, 2)) not in _rel
                     and (round(s, 2), round(e, 2)) in self._hold._emitted_spans]
            if _gone:
                _dbg(f"single_emission: consumed-without-line: {_gone}")

    def _single_emission_flush(self) -> None:
        """finish(): release everything the hold still buffers — the
        meeting is over, nothing will mature further. No-op flag-off;
        exactly-once is the hold's own guarantee (flush drains _pending)."""
        if getattr(self, "_hold", None) is None:
            return
        try:
            bank = self._dz_bank
            label_fn = bank.label_for_span if bank is not None else None
            span_fn = (self._turn_text_for_span if self._turn_decode_on()
                       else None)
            for line in self._hold.flush(label_fn, self._text_for_span,
                                         span_text_fn=span_fn):
                self._emit_single_emission_line(line)
        except Exception:  # noqa: BLE001 — flush must not break finish()
            _dbg("single_emission flush ERROR\n" + traceback.format_exc())

    def teardown(self) -> None:
        """Release this take's resources so a stop→restart starts CLEAN and
        cannot OOM by coexisting with the previous take (device, 2026-08-23:
        take-1's warmup/re-cluster kept running + holding its audio/bank/NPU
        tensors while take-2 started → both takes' memory added up → OOM →
        worker hung). Signals the diar threads, JOINS them (bounded), and frees
        the DiariZen bank + the retained channel audio. Idempotent."""
        self._watchdog_stop.set()
        # join the diar threads so their audio/bank/NPU work is finished (and
        # thus freeable) before the next take spins up. Bounded to 2 s: the
        # threads now check _watchdog_stop before AND after every heavy step
        # (warmup re-checks after the ~30 s HTP build), so they exit promptly
        # once stopped — stop must not stall the wire (BUG 4: the old 6 s join
        # x2 threads blocked finish() 6-12 s while a build that ignored the flag
        # ran to completion).
        for t in (self._warmup_thread, self._rediar_thread):
            if t is not None and t.is_alive():
                try:
                    t.join(timeout=2.0)
                except Exception:  # noqa: BLE001
                    pass
        self._dz_bank = None
        for stable in (self.stable_mic, self.stable_sys):
            if stable is not None:
                stable.cancel()
        # drop the whole-meeting audio both channels retain for the diarizer /
        # final pass — the biggest per-take heap object.
        for ch in (self.mic, self.sys):
            try:
                with ch.lock:
                    ch._chunks = []
                    # the folded buffer is where the take's audio actually
                    # lives now (see `history`); drop it too or the OOM guard
                    # frees nothing.
                    ch._hist_buf = np.zeros(0, dtype=np.float32)
                    ch._hist_filled = 0
                    ch.closed = True
                    ch.torn_down = True
            except Exception:  # noqa: BLE001
                pass

    def _watchdog(self):
        """Every 20s, log each channel's scheduler state — when the stream
        goes silent this names the stuck part instead of leaving a mystery."""
        while not self._watchdog_stop.wait(20.0):
            try:
                holder = dict(_ENGINE_LOCK_HOLDER)
                held_s = (time.monotonic() - holder["since"]) if holder["tag"] else 0
                # FR-007-B isolation (2026-09-11): tag each watchdog line with THIS
                # session's stream_id + the live-session count + this session's total
                # commits. On the reopen-loop every C# reopen mints a fresh worker
                # _Session but the old one is never finish/cancel'd (C# ForgetStream
                # is C#-local only), so `sessions_alive` climbing = the leak, and a
                # NEW session whose hist climbs while commits stays 0 pins the
                # answers-but-never-commits stall — with 6 leaked watchdog threads
                # logging, the stream_id makes "which session" unambiguous.
                sid = getattr(self, "stream_id", "?")
                try:
                    ncommits = len(self.commits.lines)
                except Exception:  # noqa: BLE001 — forensics must never break the loop
                    ncommits = -1
                lines = [f"watchdog[{sid}]: engine_lock={holder['tag']} held={held_s:.0f}s "
                         f"sessions_alive={len(_sessions)} commits={ncommits}"]
                for tag, ch in (("mic", self.mic), ("sys", self.sys)):
                    with ch.lock:
                        lines.append(
                            f"  {tag}: decoding={ch.decoding} pending={len(ch.pending_boundaries)} "
                            f"hist={ch._hist_len/SAMPLE_RATE:.0f}s seg_base={ch.seg_base:.0f} "
                            f"decoded_upto={ch.decoded_upto:.0f} closed={ch.closed}")
                _dbg("\n".join(lines))
                if holder["tag"] and held_s > 90:
                    _dbg(f"watchdog: ENGINE HANG suspected — {holder['tag']} "
                         f"has held the NPU for {held_s:.0f}s")
            except Exception:
                pass

    def display(self) -> str:
        parts = self.commits.snapshot()
        # DICTATION_NOTES_SHOW_PARTIAL (default OFF, owner call 2026-08-28): the
        # meeting transcript surfaces render COMMITTED lines only. Commits are
        # gate-guarded and now land in ~2 s, so the mutable partial's bridging
        # value is gone and it was the flicker/regurgitation stage ("把那一块
        # 全部扔掉"). Dictation shares this worker op but not that presentation
        # policy: its HUD exists specifically to show the mutable mic tail while
        # the key is still held. Keep Notes committed-only by default while
        # always returning Dictation's live hypothesis.
        is_dictation = getattr(self, "client", "notes") == "dictation"
        if (not is_dictation
                and os.environ.get("DICTATION_NOTES_SHOW_PARTIAL", "0") != "1"):
            return "\n".join(parts)
        sys_line = self.sys.partial_line()
        if sys_line:
            parts.append(sys_line)
        with self.mic.lock:
            raw = self.mic.partial
        # Check before repetition cleanup can turn a decoder loop into an
        # apparently valid short phrase. Match the channel's partial gate.
        if raw and not _loop_junk(raw, hallucination_guard=self.mic.hallucination_guard):
            cleaned = (self.commits.excise_echo(raw)
                       if os.environ.get("DICTATION_NOTES_ECHO_EXCISE", "0") == "1" else raw)
            if cleaned:
                from . import hallucination
                try:
                    from . import stream_repetition
                    cleaned = stream_repetition.clean(cleaned)
                except Exception:  # noqa: BLE001
                    pass
                if cleaned and (not self.mic.hallucination_guard
                                or not hallucination.is_junk_utterance(cleaned)):
                    with self.mic.lock:
                        spk = self.mic.seg_speaker or self.mic.fixed_label or "…"
                    parts.append(f"[{spk}] {cleaned}")
        return "\n".join(parts)


def _make_embed_fn():
    """Dictation uses channel labels and ships no speaker embedding model."""
    return None


#: Live whisper streams by stream_id (plan §3: streams are sessions; the
#: registry lets a notes caption stream and a dictation preview coexist,
#: and two whisper streams coexist too — the engine lock serializes their
#: decodes). `ops_stream` owns the id space and routes here by id.
_sessions: dict[str, "_Session"] = {}
_seq = [0]


def _live_diarization_info(session: "_Session") -> dict:
    """Report channel labels or an explicitly supplied source-only registry."""
    info = {
        "available": getattr(session, "_registry", None) is not None,
        "backend": "online_registry",
        "model_dir": None,
        "embedding_model": None,
        "embedding_provider": None,
        "similarity_threshold": None,
        "corrector": "none",
        "corrector_provider": None,
    }
    registry = getattr(session, "_registry", None)
    if registry is None:
        info["backend"] = "channel_only"
        return info

    embed = getattr(registry, "_embed", None)
    info["similarity_threshold"] = getattr(registry, "_threshold", None)
    tagged_model = getattr(embed, "_dictation_embedding_model", None)
    tagged_provider = getattr(embed, "_dictation_embedding_provider", None)
    if tagged_model is not None:
        info["embedding_model"] = tagged_model
        info["embedding_provider"] = tagged_provider
    info["corrector"] = "online_registry_only"
    info["corrector_provider"] = tagged_provider
    return info


def start(req: dict, stream_id: str | None = None) -> dict:
    engine = _get_engine()
    if engine is None:
        _dbg("start: selected Whisper engine is not initialized")
        return {"ok": False, "error": "whisper engine not initialized"}
    bias = req.get("bias_phrases")
    bias = list(bias) if bias else None
    try:
        from . import presentation_mode
        _pres = presentation_mode.session_from_env()  # None unless env-enabled
    except ImportError:
        # A notes-only presentation mode, not shipped on the dictation path.
        _pres = None
    if _pres is not None:
        bias = _pres.extend_bias(bias)  # caller vocab first, script appended
    session = _Session(engine, _make_embed_fn(),
                       bias_phrases=bias,
                       num_speakers=req.get("num_speakers"),
                       languages=req.get("languages"),
                       rolling_stable=bool(req.get("rolling_stable", False)),
                       stable_window_seconds=req.get("stable_window_seconds", 30),
                       stable_hop_seconds=req.get("stable_hop_seconds", 12),
                       stable_lookahead_seconds=req.get("stable_lookahead_seconds", 6),
                       stable_beam_size=req.get("stable_beam_size", 4),
                       live_beam=req.get("live_beam"),
                       client=req.get("client") or "notes")
    if _pres is not None:
        # AFTER construction: restores the regurgitation-guard sets to
        # caller-only vocab and installs the commit-time snap hook.
        _pres.attach(session.commits)
    if stream_id is None:
        _seq[0] += 1
        stream_id = f"w{int(time.time() * 1000)}-{_seq[0]}"
    session.stream_id = stream_id
    session.configure_preview_pacing()
    _sessions[stream_id] = session
    # FR-007-B (2026-09-11): there is only ONE live notes stream per meeting. A
    # wedge self-heal opens a FRESH stream, but the C# side cannot reliably tear
    # the old one down — its stream_cancel races the single-threaded wire and is
    # dropped (device: 5 reopens, sessions_alive 1->2->3->4, every old session
    # still ticking, ZERO worker-side cancels seen). stream_start, by contrast,
    # IS reliably delivered (each reopen created a new session), so the NEW start
    # SUPERSEDES the old here: evict any prior notes session (bounded teardown +
    # pop) so sessions never accumulate and the reopen loop cannot storm. Scoped
    # to notes so a concurrent dictation stream is untouched. teardown() is
    # idempotent and its thread-joins are bounded (2 s), so a wedged old session
    # whose threads are stuck is still POPPED (accumulation stops) — the join
    # just times out.
    _evicted = 0
    if session.client == "notes":
        for _old_id, _old in list(_sessions.items()):
            if _old is session or getattr(_old, "client", "notes") != "notes":
                continue
            try:
                _old.teardown()
            except Exception:  # noqa: BLE001 — eviction must never fail the start
                pass
            _sessions.pop(_old_id, None)
            _evicted += 1
            _dbg(f"start: evicted superseded notes session {_old_id} "
                 f"(replaced by {stream_id})")
    live_diarization = _live_diarization_info(session)
    # FR-007-B isolation: sessions_alive AFTER registering — on a clean reopen
    # this should read 1; if it climbs (2,3,..) the previous session was never
    # torn down (the leak the reopen-loop accumulates).
    _dbg(f"start: whisper dual-channel stream {stream_id} ({session.client}) "
         f"sessions_alive={len(_sessions)} evicted_prior={_evicted}")
    return {"ok": True, "stream_id": stream_id,
            "sample_rate": SAMPLE_RATE, "engine": "whisper",
            "rolling_stable": session.stable_mic is not None,
            "live_beam": session.live_beam,
            "live_diarization": live_diarization}


NO_SUCH_STREAM = {"ok": False, "reason": "no_such_stream", "error": "no_such_stream"}


def _check(req: dict):
    """The session for ``req["stream_id"]``, or the no_such_stream reply.
    The id is REQUIRED: there is no "current" stream any more."""
    session = _sessions.get(req.get("stream_id") or "")
    if session is None:
        return dict(NO_SUCH_STREAM)
    return session


def push(req: dict) -> dict:
    session = _check(req)
    if isinstance(session, dict):
        return session
    try:
        raw = base64.b64decode(req.get("pcm_b64") or "")
        if not raw:
            return {"ok": False, "error": "empty pcm_b64"}
        samples = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
        if req.get("channel") == "sys":
            session.push_sys(samples)
        else:
            session.push_mic(samples)
        text = session.display()
        stable = session.stable_snapshot()
    except Exception as e:  # noqa: BLE001 — wire boundary
        return {"ok": False, "error": f"{type(e).__name__}: {e}"}
    return {"ok": True, "text": text, "is_endpoint": False,
            "commits": session.commits.drain_feed(),
            "relabels": session.commits.drain_relabels(),
            "stable_text": stable["text"],
            "stable_timed_transcript": stable["timed"],
            "stable_spans": stable["spans"],
            "stable_through_seconds": stable["through"]}


def finish(req: dict) -> dict:
    session = _check(req)
    if isinstance(session, dict):
        return session
    try:
        session._watchdog_stop.set()
        session._aec_flush()
        session.mic.finish()
        session.sys.finish()
        for channel in (session.stable_mic, session.stable_sys):
            if channel is not None:
                channel.finish()
        # Single-emission mode: release everything the hold still buffers
        # BEFORE the final feed drain, so the reply carries the tail lines.
        # Flag off this is a no-op.
        session._single_emission_flush()
        text = session.display()
        commits = session.commits.drain_feed()
        relabels = session.commits.drain_relabels()
        stable = session.stable_snapshot()
        try:
            # FINAL post-relabel line state, for offline scoring/forensics
            # (DICTATION_NOTES_DIAG-gated like every _dbg). The partner feed's
            # lines are immutable by contract, so this trail is the only
            # place the CONVERGED labels are observable off-device.
            with session.commits.lock:
                _fl = list(zip(session.commits.lines,
                               session.commits.line_meta))
            for _i, (_ln, _m) in enumerate(_fl):
                _dbg(f"final-line {_i} ch={_m['channel']} "
                     f"span={_m['start']}-{_m['end']} {_ln}")
            # Commit-label sourcing split (settled / registry / bank ...):
            # counted all session but never surfaced before 2026-09-01, so
            # the D2 arm's "bank share of young lines" was unobservable.
            _dbg(f"commit_sources: {session.commit_source_counts}")
        except Exception:  # noqa: BLE001 — forensics must not break finish
            pass
    except Exception as e:  # noqa: BLE001 — wire boundary
        return {"ok": False, "error": f"{type(e).__name__}: {e}"}
    finally:
        # free the diar threads + bank + retained audio so the NEXT take starts
        # clean instead of coexisting with this one (OOM guard).
        session.teardown()
        _sessions.pop(session.stream_id, None)
    return {"ok": True, "text": text, "commits": commits,
            "relabels": relabels,
            "stable_text": stable["text"],
            "stable_timed_transcript": stable["timed"],
            "stable_spans": stable["spans"],
            "stable_through_seconds": stable["through"]}


def cancel(req: dict) -> dict:
    s = _check(req)
    if isinstance(s, dict):
        return s
    # teardown() sets the stop-event, joins the diar threads, marks the channels
    # closed, and frees the bank + retained audio — so a restart can't OOM by
    # piling on top of this take.
    s.teardown()
    _sessions.pop(s.stream_id, None)
    return {"ok": True}


def active() -> int:
    """Live whisper streams (for `stream_release` bookkeeping)."""
    return len(_sessions)
