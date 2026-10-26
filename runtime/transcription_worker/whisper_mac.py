"""Apple-silicon Whisper backend for the speech worker.

Presents the exact engine surface `asr_server.op_init`/`op_transcribe` bind
against the accelerator engine surface with — same constructor kwargs, same
`transcribe(audio, bias_phrases=, beam_size=, p_you=, ...) -> {text, stats}`
shape, same `on_npu` / `tokenizer` / `encoder.get_providers()` attributes —
so the wire contract and every byte of the host protocol stay identical
across platforms. The accelerator implementation cannot be imported on every
platform, which is why this is a sibling module rather than a subclass.

Engine resolution order (first importable wins):
  1. mlx_whisper      — Apple MLX, Metal/unified-memory; the intended path.
  2. faster_whisper   — CTranslate2 int8 on CPU; solid fallback, no Metal.
  3. whisper          — reference OpenAI impl (torch); last resort, slow.

Personalization status on this backend, mirroring the Windows tiers:
  L0 (plain decode)        — live, whichever engine resolved.
  L1 (bias-phrase beam)    — MLX Dictation finals use mlx_beam on Metal when
                             vocabulary_search is enabled. OpenAI retains the
                             older trie adapter. Both retain the provider's
                             transcription loop and temperature fallback;
                             faster-whisper uses its own ordinary beam.
  L1-prompt (vocabulary)   — MLX and faster-whisper: bias phrases become the
                             decode's `initial_prompt` (`_vocabulary_prompt`).
                             Measured on MLX only; unmeasured on faster.
  L2 (density-ratio)       — deferred on this backend.
The vendored `beam_decode`/`ngram_lm`/`corpus` stack stays platform-neutral;
`trie_decode.TrieTranscribeModel` is the product adapter around the upstream
long-audio pipeline, not a second implementation of that pipeline.

`StubEngine` is a modelless echo for plumbing/CI tests (no downloads, no
audio math beyond RMS) — select with {"op":"init","backend":"stub"}.
"""

from __future__ import annotations

import os
import re
import sys
import time
import zlib
from collections import Counter
from pathlib import Path

import numpy as np

try:  # package member (production) or loose script (ad-hoc runs)
    from . import audio_energy, mlx_cache
except ImportError:  # pragma: no cover
    import audio_energy
    import mlx_cache

SAMPLE_RATE = 16000

# ---------------------------------------------------------------------------
# Junk filter — kept in sync with the platform-neutral backend heuristics.
# ---------------------------------------------------------------------------
_ANNOTATION_RE = re.compile(r"[\(\[][^\)\]]*[\)\]]|♪+|\*[^*]*\*")

# Whisper's stock no-speech / music hallucinations: on near-silence the turbo
# model (whose no-speech token is unreliable) fills the gap with a small set
# of YouTube-ish stock phrases that read like ordinary English, so the
# repetition + compression-ratio rules below miss them. These ~never occur in
# a real meeting; a SHORT segment that is (or starts as) one is dropped.
# Field 2026-08-21: "tune in the middle of the road", "tune in the background".
_HALLUCINATION_STEMS = (
    "tune in",
    "thanks for watching", "thank you for watching", "thanks for listening",
    "please subscribe", "subscribe to", "don't forget to subscribe",
    "like and subscribe", "like comment and subscribe", "like and subscrib",
    "see you in the next", "see you next", "see you guys",
    "stay tuned", "for more information visit", "for more information, visit",
    "www.", "http",
)


# Above this many seconds of audio, a decode is LONG-FORM (a meeting), not a
# dictation utterance. Whisper's own window is 30s; 120s is a wide margin over
# anything a person dictates in one push-to-talk press.
LONG_FORM_AUDIO_S = 120.0


def drop_no_speech_segments(
    segments: list,
    no_speech_threshold: float = 0.6,
    logprob_threshold: float = -1.0,
) -> list:
    """Whisper's own junk heuristic, applied per segment: probable
    non-speech (`no_speech_prob` > 0.6) AND low decoder confidence
    (`avg_logprob` < -1.0) together mean the segment is a hallucination on
    near-silence. BOTH conditions, deliberately: quiet-but-real speech
    scores high no_speech_prob with good logprob and must survive; hard
    accents score low logprob with low no_speech_prob and must survive.
    Thresholds are OpenAI's reference defaults. Segments missing either
    field are kept — absence of evidence is not junk."""
    kept = []
    for seg in segments:
        no_speech = seg.get("no_speech_prob")
        logprob = seg.get("avg_logprob")
        if (isinstance(no_speech, (int, float)) and isinstance(logprob, (int, float))
                and no_speech > no_speech_threshold and logprob < logprob_threshold):
            continue
        kept.append(seg)
    return kept


def looks_like_junk(text: str, long_form: bool = False,
                    hallucination_guard: bool = True) -> bool:
    """True for hallucinated non-speech output: pure sound annotations,
    pathological single-word repetition, or hyper-compressible loops
    (OpenAI's compression-ratio heuristic, threshold 2.4).

    `hallucination_guard=False` disables ONLY the stock-phrase rule
    (`_HALLUCINATION_STEMS`), and `transcribe` passes the caller's own flag.

    Why: the stems were collected from meeting windows over near-silence,
    where a short decode that opens with "see you next" or "stay tuned" is
    the model filling a gap. Dictation is short by nature, and a sign-off is
    an ordinary message there: "See you next week, Casey." decoded correctly
    and was then emptied with `ok: true`, the host blamed the microphone, and
    saying it again gave the same result. Dictation already sends
    `hallucination_guard=false` so the phrase blocklist leaves real
    pleasantries alone (see `blocked_hallucination`); this rule ignored that
    opt-out until the flag reached it. The default stays True because the
    other callers judge meeting caption lines (`hallucination.loop_junk`,
    the diarized transcript), where the rule was built and still applies.

    `long_form=True` disables ONLY the compression-ratio rule.

    Why: that rule is calibrated for a single ≤30 s Whisper window, where a
    ratio > 2.4 really does mean "the decoder looped". It does not survive
    concatenation. Ordinary English crosses 2.4 at roughly 100 words and
    keeps climbing (measured: 100 w → 2.64, 500 w → 4.09, 4,269 w → 5.46),
    purely because zlib gets a bigger dictionary — nothing to do with
    repetition.

    That made this filter silently destroy the entire meeting-notes product:
    op_transcribe over a 24m48s recording decoded 4,269 words correctly
    (min_logprob −0.27, i.e. a confident decode) and then returned `text: ""`
    because the concatenated transcript compressed 5.46:1. Notes, transcript
    and diarization merge all came out empty, with `ok: true` and no error
    anywhere — the worst shape of failure. The annotation and
    word-repetition rules are length-robust and stay on in both modes.

    The stock-hallucination rule below arrived the other way round: it was
    added to the other backend on 2026-08-21 and never reached this copy, even
    though the header of this section says the two are "duplicated verbatim …
    keep the two copies in sync". Two files that claim to be identical and are
    not is worse than two that never claimed it, because nobody re-reads them.
    """
    stripped = _ANNOTATION_RE.sub("", text).strip(" \t\r\n.,!?-–—…")
    if not stripped:
        return True
    words = stripped.split()
    low = " ".join(w.lower() for w in words).strip(" .,!?-–—…\"'“”")
    if (hallucination_guard and len(words) <= 12
            and any(low.startswith(s) for s in _HALLUCINATION_STEMS)):
        return True
    if len(words) >= 6:
        _, most = Counter(w.lower().strip(".,!?") for w in words).most_common(1)[0]
        if most / len(words) > 0.6:
            return True
    if long_form:
        return False
    data = stripped.encode("utf-8")
    if len(data) > 60 and len(data) / len(zlib.compress(data)) > 2.4:
        return True
    return False


# ---------------------------------------------------------------------------
# Silence pre-check + hallucination-phrase blocklist (Task D)
# ---------------------------------------------------------------------------

# Frame length and RMS floor for the energy pre-check. Deliberately the SAME
# numbers as the Swift segmenter and gate (`SegmenterConstants.frameSeconds`
# 0.03 / `silenceRMS` 0.004) and the Python eval reference
# (the caption-evaluation harness's FRAME_S / SILENCE_RMS). Three
# copies of "what is audible" is how the two pipelines drifted apart in the
# first place; if you change one, change them all in the same commit.
#
# They now live in `audio_energy`, imported rather than restated, because the
# Windows QNN engine needs the SAME rule and importing `whisper_mac` to get it
# would drag an MLX-shaped module onto a Snapdragon. `has_speech_energy` is
# re-exported here unchanged: this module's callers, and
# `test_junk_filter_mac.py`, address it by this name.
FRAME_SECONDS = audio_energy.FRAME_SECONDS
SILENCE_RMS = audio_energy.SILENCE_RMS
has_speech_energy = audio_energy.has_speech_energy

# ---------------------------------------------------------------------------
# Decode-time non-speech gate — the macOS half of the shared SpeechGate
# ---------------------------------------------------------------------------
#
# `has_speech_energy` above is an RMS floor, and by construction an RMS floor
# cannot catch ENERGETIC non-speech: a keyboard, a chime, room tone sitting
# above 0.004. Handed that, Whisper does not fail. It picks a language at
# random and then transcribes FLUENTLY in it, which is why the field report
# was "I held the key, said nothing, and it typed Korean" rather than "it
# typed nonsense" — and why `looks_like_junk` sails straight past
# `날씨였습니다.`, which is well-formed Korean carrying none of the repetition
# or annotation shapes that filter looks for.
#
# That failure class is LIVE on this engine, not inherited from the Windows
# report. Measured on this machine, 2026-08-24, mlx-community/whisper-large-
# v3-turbo (the shipped default), fp16, through `mlx_whisper.transcribe`:
#
#     clip                       P(lang)   avg_logprob   emitted text
#     room tone, RMS 0.006        0.171      -0.276       " Thank you."
#     gaussian hiss 0.02          0.175      -0.589       " Thank you."
#     gaussian hiss 0.10          0.189      -0.904       " ."
#     keyboard click train        0.197      -0.851       " And I'll see you next time."
#     440 Hz pure tone            0.386      -0.583       " ."
#     TTS English sentence        0.480      -0.183       (correct transcript)
#
# Room tone at 0.006 RMS clears `has_speech_energy` (floor 0.004), decodes to
# " Thank you." at avg_logprob -0.276 — a CONFIDENT decode — and every other
# defence in this file lets it through: `drop_no_speech_segments` needs
# no_speech_prob > 0.6 and gets 1e-10, `looks_like_junk` sees two ordinary
# words with no annotation, no repetition and too few bytes to compress, and
# `blocked_hallucination` is OFF on the dictation path by design. So today a
# silent-room hold types "Thank you." into the user's editor, and a keyboard
# under the mic types a YouTube outro.
#
# The other platform shipped a rule for this shape in its speech gate, and
# `beam_decode.SpeechGate`). The STRUCTURE ports; the NUMBERS do not, and that
# distinction is the whole reason this block is long.

# Probability of the language token the chunk decoded under, below which the
# chunk is treated as non-speech.
#
# 0.25, rather than the other platform's 0.10. Those are two different checkpoints and
# the distributions do not overlap: whisper-base-qnn on an X Elite laptop put 0.925 of
# the SOT mass on <|nospeech|> for hiss, leaving 0.013-0.036 on the winning
# language and 0.475-0.501 on its worst real speech. large-v3-turbo's
# <|nospeech|> head is dead (see NO_SPEECH_HEAD_MIN_ON_SILENCE), so that mass
# lands on the language tokens instead and the whole distribution compresses:
# 0.11-0.20 on non-speech, 0.46-0.52 on speech. Transplanting 0.10 here would
# sit below EVERY reading in the table above — a gate that exists, runs, and
# decides nothing. Copying a threshold across checkpoints because the concept
# matches is the same mistake as copying it across denominators, one paragraph
# down.
#
# Where 0.25 comes from: it sits 1.27x above the highest realistic non-speech
# reading (0.197, the click train) and 1.83x below the lowest real-speech
# reading (0.457, the first 0.8 s of a synthetic speech sentence). The asymmetry is
# deliberate. The non-speech sample is synthetic and noise is noise, so that
# end is probably representative; the speech sample is macOS `say` with
# synthetic voices, which is CLEANER than a real mic capture, so real speech can
# plausibly read lower than 0.457 and the margin belongs on that side.
#
# WHAT THIS SAMPLE DOES NOT COVER, stated so nobody reads it as more than it
# is: no real microphone captures, no accented speakers, no code-switched
# holds, one checkpoint. If a real bilingual or accented hold ever comes back
# empty with `dropped_reason: language-prob-gate`, this is the number to
# lower, and the readings that would justify it are one `_MlxSotProbe.read()`
# away. The 440 Hz tone at 0.386 is deliberately NOT caught: reaching up for
# it would put the bar inside real speech's plausible range, and it emits
# " ." which `looks_like_junk` already deletes.
#
# THE DENOMINATOR IS PART OF THE THRESHOLD, and it is the second trap here.
# Every number above is a softmax over the FULL vocabulary. That is why
# `_MlxSotProbe` runs its own SOT step instead of calling
# `mlx_whisper.decoding.detect_language`, which is the obvious API and the
# wrong one: it masks every non-language token to -inf before the softmax
# (mlx_whisper/decoding.py:61-65), renormalising inside the language range.
# Measured on the same clips, that renormalisation pushes hiss from 0.175 to
# 0.328 and silence from 0.159 to 0.361 while the TTS sentence goes to 0.803 —
# a strictly worse separation, and a bar chosen against one denominator is
# meaningless against the other. A future edit that "simplifies" this back to
# `detect_language` must re-measure in the same commit.
LANGUAGE_PROB_THRESHOLD = 0.25

# P(<|nospeech|>) at or above which the language rule is ALLOWED to fire,
# consulted ONLY on checkpoints whose no-speech head works.
#
# Ported from `beam_decode.SpeechGate.language_corroboration_threshold` and
# load-bearing for the same reason: P(language) is read off the full-vocab
# softmax and is not renormalised inside the language range, so a chunk can
# read low on every individual language token merely because the mass is
# SPLIT. Accented or code-switched speech landing between <|en|> and <|zh|> is
# the ordinary case on this engine, not the exotic one — chunking at natural
# pauses (`auto_language_decode_spans`) exists BECAUSE this user dictates in
# two languages. When <|nospeech|> reads near zero the model is actively
# asserting this IS speech; dropping the chunk then deletes confident speech,
# which is worse than the mislabelling the rule exists to prevent.
LANGUAGE_CORROBORATION_THRESHOLD = 0.50

# P(<|nospeech|>) that a checkpoint must reach on THIRTY SECONDS OF DIGITAL
# SILENCE before its no-speech head is trusted as a second opinion.
#
# This exists because reading 0.0 from a broken head is indistinguishable from
# reading 0.0 from a working one, and the two mean opposite things. Measured
# on this machine, 2026-08-24: large-v3-turbo returns 1e-13 to 1e-10 for
# EVERY input tried — silence, hiss, a click train, a clean TTS sentence. Its
# head is not quiet, it is dead, as the backend's implementation documents
# out loud ("the turbo model, whose no-speech token is unreliable"). The X Elite's
# whisper-base reads 0.932 on the same input. A head that cannot put half its
# mass on <|nospeech|> when handed literal silence has nothing to corroborate
# with, and `_MlxSotProbe` reports its reading as ABSENT rather than as a
# confident assertion that noise is speech.
#
# Without this probe the corroboration rule silently vetoes every drop on the
# shipped checkpoint: the whole gate would be dead code that reads as live,
# which is the exact failure this file's own comments keep naming. The one-off
# cost is a single encoder pass at model load.
NO_SPEECH_HEAD_MIN_ON_SILENCE = 0.50


def no_speech_head_usable(silent_no_speech_prob: float | None) -> bool:
    """Whether a checkpoint's <|nospeech|> head may be used as a second
    opinion, given what it returned for thirty seconds of digital silence.

    A one-line predicate with its own name because the alternative — the same
    comparison inline inside `_MlxSotProbe.build` — is unreachable without a
    loaded model, and a constant that no self-check can move is a constant
    nobody can trust. `_selftest` pins it against both measured checkpoints.
    """
    return (silent_no_speech_prob is not None
            and silent_no_speech_prob >= NO_SPEECH_HEAD_MIN_ON_SILENCE)

# Second half of whisper's reference no-speech rule (decoding.py): a chunk is
# rejected on probability AND avg_logprob < this. NEVER a rule on its own.
# The backend consults it only alongside the probability, and
# `drop_no_speech_segments` above states the other direction of the same fact
# ("hard accents score low logprob with low no_speech_prob and must survive").
# Dropping an utterance because its mean avg_logprob fell under -1.0 would
# make `TestDropNoSpeechSegments.test_hard_accent_kept` false one level up:
# the segment survives the per-segment pair and then dies at the utterance.
AVG_LOGPROB_THRESHOLD = -1.0

# The other platform's no-speech threshold is deliberately NOT ported,
# and NOT because the probability is out of reach — `_MlxSotProbe` reads it
# and the corroboration bar above consumes it. It is not ported because this
# path already applies whisper's reference no-speech rule TWICE:
# `mlx_whisper.transcribe` skips a window on no_speech_prob > 0.6 AND
# avg_logprob < -1.0 (mlx_whisper/transcribe.py:301-311), and
# `drop_no_speech_segments` applies the same 0.6/-1.0 pair again to whatever
# survives that. A third bar at 0.75 would be a third number for one concept
# on one code path, which is the drift shape the junk-filter header at the top
# of this file was written after.


# Whether a verdict actually DROPS the chunk, or is only recorded.
#
# Default is SHADOW, and that is a deliberate refusal to ship a threshold on
# the evidence available. The operating point above was measured entirely on
# synthetic audio — macOS `say` with synthetic voices for speech, generated
# hiss / room tone / click train / sine for non-speech, one checkpoint, one
# machine, 2026-08-24. **No real microphone capture, no accented speaker, and
# no code-switched hold was ever put through it.** TTS speech is cleaner than
# mic speech, so real speech can plausibly read below the 0.4565 floor the
# table is built on.
#
# The two failure directions are not symmetric here, and that is what settles
# it. The bug this rule fixes produces WRONG WORDS ("held the key, said
# nothing, got Korean") — visible, and the user retries. Over-dropping
# produces NO WORDS, which on this platform is already the second-largest
# failure class: a known-issue count of 30 `nothingHeard`
# outcomes in 109 macOS dictation failures. And the user this engine ships to
# dictates in English AND Chinese — `auto_language_decode_spans` exists
# BECAUSE of that — which is exactly the case `LANGUAGE_CORROBORATION_THRESHOLD`
# says lands between <|en|> and <|zh|>, and exactly the case where the shipped
# checkpoint offers no second opinion, because turbo's <|nospeech|> head is
# dead and the language rule therefore stands alone.
#
# So: run the rule, log every verdict with BOTH readings (the call site
# already does), count them in `stats["gated_chunks"]`, and drop nothing.
# One real dictation session then produces the table that synthetic audio
# could not, and flipping this on is a one-line change backed by readings from
# the machine it ships to.
#
#   DICTATION_MLX_SPEECH_GATE=enforce   — drop flagged chunks
#   DICTATION_MLX_SPEECH_GATE=shadow    — record only (default)
def _speech_gate_enforce_from(raw: str | None) -> bool:
    """Only the literal word turns it on.

    Not `"1"`, not `"true"`, not any-non-empty. This switch decides whether
    the worker deletes audio the user spoke, so the value that enables it has to be
    something nobody sets by accident while exporting a debug flag.
    """
    return (raw or "shadow").strip().lower() == "enforce"


SPEECH_GATE_ENFORCE = _speech_gate_enforce_from(os.environ.get("DICTATION_MLX_SPEECH_GATE"))


def speech_gate_verdict(*, language_prob: float | None,
                        no_speech_prob: float | None,
                        avg_logprob: float | None,
                        prompt_conditioned: bool) -> str | None:
    """The non-speech verdict for ONE decode chunk: a `dropped_reason`
    string, or None to keep the chunk.

    A pure function of four numbers, deliberately, because the hazard here is
    not a wrong threshold — it is OVER-DROPPING, and every argument for
    moving a bar has to be arguable against a table of readings. A rule buried
    inside a decode loop cannot be; `_selftest()` runs this one against the
    X Elite table in both directions, including the readings that must SURVIVE.

    `prompt_conditioned` is the FLEURS interaction and it is not optional. A
    <|startofprev|> prompt whose language differs from the audio inflates the
    no-speech logit — an English vocabulary prompt over Chinese audio once
    zeroed every Chinese decode on FLEURS — and because P(language) is read
    off the SAME softmax, an inflated no-speech mass depresses it too, so both
    readings inherit the hazard. This engine prompts whenever the host sends
    bias phrases (`_vocabulary_prompt`, English seed plus mostly-Latin terms),
    over a user whose own dictation settings list English AND Chinese, so
    "prompted" is the shipped configuration and not a corner. The reference
    rule (decoding.py) answers it by pairing the probability with
    avg_logprob < -1.0: real speech under a mismatched prompt still decodes
    confidently, while noise regurgitating the prompt's vocabulary does not.

    Note what is NOT here: no bar on `avg_logprob` alone, and no bar on
    P(<|nospeech|>) alone. See the constants above for why each would be
    wrong on this engine specifically — the measured table settles the first
    outright (room tone decoded at -0.276, more confident than plenty of real
    speech, so a -1.0 bar would have kept it and an accent-safe bar cannot be
    drawn at all).

    `no_speech_prob=None` means the checkpoint HAS no usable no-speech head
    (`NO_SPEECH_HEAD_MIN_ON_SILENCE`), and then the language rule stands
    alone — the same stance `beam_decode.SpeechGate.suspects` takes for the
    same reason, that there is no second opinion to be had. It does NOT mean
    "the probe failed": a failed probe returns no reading at all, so this
    function is never reached. Keeping those two apart is the whole point of
    `_MlxSotProbe` returning None for the reading rather than zeros.
    """
    # An absent LANGUAGE reading is "unknown", never "suspicious" — that one
    # keeps the chunk, which is the only safe direction for a rule whose
    # failure mode is deleting speech the user actually said.
    if language_prob is None or language_prob >= LANGUAGE_PROB_THRESHOLD:
        return None
    if (no_speech_prob is not None
            and no_speech_prob < LANGUAGE_CORROBORATION_THRESHOLD):
        return None
    if not prompt_conditioned:
        return "language-prob-gate"
    if avg_logprob is None or avg_logprob >= AVG_LOGPROB_THRESHOLD:
        return None
    # Named after the two-condition rejection rule
    # ("no_speech+avg_logprob"), so a log line says WHICH rule fired and that
    # it was the confirmed form, not the bare probability.
    return "language-prob-gate+avg-logprob"


class _MlxSotProbe:
    """One SOT-step forward pass over a chunk, for the two probabilities
    `speech_gate_verdict` reads.

    This is `mlx_whisper.decoding.detect_language` minus its language mask —
    the same encoder pass, the same first-30s mel, the same `<|startoftranscript|>`
    step, and then a softmax over the WHOLE vocabulary. See
    LANGUAGE_PROB_THRESHOLD for why the mask cannot be inherited.

    It costs nothing net. When `language` is None, `mlx_whisper.transcribe`
    already runs its own detect_language encoder pass over this same window;
    handing it the language this probe resolved (identical by construction —
    masking to -inf then argmax picks the same token as argmax over the
    language ids of the unmasked softmax) makes it skip that pass. Two
    resolutions of one concept collapse into one, which is the shape of defect
    the backend no-speech probability's comment was written about.
    """

    def __init__(self, mx, model, tokenizer, audio) -> None:
        self._mx = mx
        self._model = model
        self._tokenizer = tokenizer
        self._audio = audio  # the mlx_whisper.audio module
        self._lang_ids = np.asarray(tokenizer.all_language_tokens, dtype=np.int64)
        self._lang_codes = tuple(tokenizer.all_language_codes)
        self._no_speech_id = int(tokenizer.no_speech)
        self._reported_failure = False
        # Set by `build` from the digital-silence capability probe. False
        # means every P(<|nospeech|>) this checkpoint produces is reported as
        # ABSENT, not as zero — see NO_SPEECH_HEAD_MIN_ON_SILENCE.
        self.no_speech_head_usable = False

    @classmethod
    def build(cls, repo: str) -> "_MlxSotProbe | None":
        """The probe for `repo`, or None when this build of mlx_whisper or
        this checkpoint cannot answer the question. None DISABLES the gate
        rather than failing the decode: an utterance that transcribes is
        always better than one that raises, and `stats["gated_chunks"]` says
        out loud that no gate ran."""
        try:
            import mlx.core as mx
            from mlx_whisper import audio as mlx_audio
            from mlx_whisper.tokenizer import get_tokenizer
            # NOT `mlx_whisper.transcribe.ModelHolder`: the package rebinds
            # that attribute to the transcribe FUNCTION in its __init__, so
            # the module is only reachable through the submodule import.
            from mlx_whisper.transcribe import ModelHolder
        except Exception as exc:  # noqa: BLE001
            print(f"whisper_mac: language gate OFF — mlx internals unavailable ({exc})",
                  file=sys.stderr, flush=True)
            return None
        try:
            # float16 must match what `transcribe(fp16=True)` asks ModelHolder
            # for. ModelHolder keys its cache on the PATH ALONE and ignores
            # the dtype on a hit, so probing first at float32 would silently
            # switch every subsequent decode in this process to float32 — a
            # precision change nobody asked for, arriving through a gate.
            model = ModelHolder.get_model(repo, mx.float16)
            if not model.is_multilingual:
                # An .en checkpoint has no language tokens, so "which language
                # did it pick" is not a question it can be asked. It also
                # cannot produce the failure this gate exists for.
                print("whisper_mac: language gate OFF — monolingual checkpoint",
                      file=sys.stderr, flush=True)
                return None
            tokenizer = get_tokenizer(True, num_languages=model.num_languages)
            probe = cls(mx, model, tokenizer, mlx_audio)
            # Capability probe, once, on THIS checkpoint: hand it literal
            # silence and see whether its no-speech head answers. A rule is
            # only trusted after it has been shown able to fire — a second
            # opinion that is structurally incapable of disagreeing is not a
            # second opinion, it is a permanent veto. See
            # NO_SPEECH_HEAD_MIN_ON_SILENCE for the two measured checkpoints.
            silence = np.zeros(mlx_audio.N_SAMPLES, dtype=np.float32)
            silent_reading = probe._read_raw(silence)
            probe.no_speech_head_usable = no_speech_head_usable(
                silent_reading[2] if silent_reading is not None else None)
            print(
                "whisper_mac: language gate ON "
                f"(P(lang) bar {LANGUAGE_PROB_THRESHOLD}; no-speech head "
                + ("usable" if probe.no_speech_head_usable else "DEAD — the "
                   "language rule stands alone on this checkpoint")
                + f"; P(nospeech) on digital silence = "
                + (f"{silent_reading[2]:.3g}" if silent_reading else "unreadable")
                + ")",
                file=sys.stderr, flush=True)
            return probe
        except Exception as exc:  # noqa: BLE001
            print(f"whisper_mac: language gate OFF — probe build failed ({exc})",
                  file=sys.stderr, flush=True)
            return None

    def read(self, chunk: np.ndarray) -> tuple[str, float, float | None] | None:
        """``(language_code, P(language), P(<|nospeech|>) or None)`` for
        `chunk`, or None if the pass failed.

        The third slot is None on a checkpoint whose no-speech head failed the
        capability probe. That is the difference between "the model says this
        is speech" and "this model cannot say", and collapsing them into a
        float is what would turn the corroboration rule into a permanent veto
        on the shipped turbo checkpoint.
        """
        raw = self._read_raw(chunk)
        if raw is None:
            return None
        code, language_prob, no_speech_prob = raw
        return (code, language_prob,
                no_speech_prob if self.no_speech_head_usable else None)

    def _read_raw(self, chunk: np.ndarray) -> tuple[str, float, float] | None:
        """The forward pass itself, with P(<|nospeech|>) reported RAW —
        `build`'s capability probe needs the unfiltered number, and `read`
        needs the filtered one."""
        mx = self._mx
        audio = self._audio
        try:
            mel = audio.log_mel_spectrogram(
                chunk, n_mels=self._model.dims.n_mels, padding=audio.N_SAMPLES)
            mel = audio.pad_or_trim(mel, audio.N_FRAMES, axis=-2).astype(mx.float16)
            features = self._model.encoder(mel[None])
            logits = self._model.logits(
                mx.array([[self._tokenizer.sot]]), features)[:, 0]
            # float32 before the softmax, and `np.array` rather than
            # `np.asarray`, both copied from mlx_whisper's own detect_language
            # (decoding.py:65-66): fp16 logits softmaxed in place round a
            # 0.036 reading into the same bucket as a 0.02 one, and this rule
            # is decided on the third decimal.
            probs = np.array(mx.softmax(logits.astype(mx.float32), axis=-1))[0]
        except Exception as exc:  # noqa: BLE001
            if not self._reported_failure:
                self._reported_failure = True
                print(f"whisper_mac: language gate skipped this decode — {exc}",
                      file=sys.stderr, flush=True)
            return None
        best = int(np.argmax(probs[self._lang_ids]))
        return (self._lang_codes[best],
                float(probs[self._lang_ids[best]]),
                float(probs[self._no_speech_id]))


# One build attempt per repo, negative result included: a fake or absent mlx
# must not re-raise (and re-print) once per chunk of every utterance for the
# life of the worker, and the capability probe must not re-encode 30 s of
# silence per decode.
#
# ONE SLOT, not a dict keyed by repo, and that is a memory decision. The probe
# holds a reference to the loaded model, so a dict would keep the PREVIOUS
# checkpoint's weights resident after an `op_init` switched models — mlx's own
# `ModelHolder` is single-slot for exactly this reason, and a cache beside it
# with a different retention policy is how a 1.6 GB leak gets built out of two
# individually reasonable caches. Idle-unload releases the worker process
# whole (`AsrWorkerClient`), so nothing else here needs to drop it.
_MLX_SOT_PROBE: "tuple[str, _MlxSotProbe | None] | None" = None


def _mlx_sot_probe(repo: str) -> "_MlxSotProbe | None":
    global _MLX_SOT_PROBE

    if _MLX_SOT_PROBE is None or _MLX_SOT_PROBE[0] != repo:
        _MLX_SOT_PROBE = (repo, _MlxSotProbe.build(repo))
    return _MLX_SOT_PROBE[1]


# A trie decode can return successfully while ending one 30-second beam window
# after only a handful of tokens.  That is materially different from ordinary
# recognition error: speech later in the window disappears wholesale, and no
# exception reaches the existing fallback.  One token per voiced second is an
# intentionally conservative floor (normal English and Chinese speech both
# clear it comfortably).  Crossing the floor does not judge transcript
# quality; falling below it merely asks the proven plain decoder to retry.
TRIE_COVERAGE_MIN_VOICED_SECONDS = 6.0
TRIE_COVERAGE_MIN_TOKENS_PER_VOICED_SECOND = 1.0

# Whisper chooses one language context for an entire transcribe call.  A
# 30-second dictation window that begins with English and ends with Chinese can
# therefore decode only the later Chinese speech while returning successfully.
# Give auto-language dictation a fresh decision at natural pauses, while
# keeping chunks long enough to preserve sentence context and throughput.
AUTO_LANGUAGE_MAX_CHUNK_SECONDS = 24.0
AUTO_LANGUAGE_MIN_CHUNK_SECONDS = 6.0
AUTO_LANGUAGE_MIN_PAUSE_SECONDS = 0.6


def auto_language_decode_spans(audio) -> list[tuple[int, int]]:
    """Split auto-language dictation at every qualifying natural pause.

    Each pause creates a fresh language-decision run when both adjacent runs
    can remain at least six seconds.  If speech is continuous, a 24-second
    hard limit still bounds each run.  Every sample belongs to exactly one
    span; silence is split at its midpoint rather than discarded.
    """
    samples = np.asarray(audio, dtype=np.float32)
    if samples.size == 0:
        return []

    maximum = int(AUTO_LANGUAGE_MAX_CHUNK_SECONDS * SAMPLE_RATE)
    minimum = int(AUTO_LANGUAGE_MIN_CHUNK_SECONDS * SAMPLE_RATE)
    frame_samples = int(SAMPLE_RATE * FRAME_SECONDS)
    frame_count = len(samples) // frame_samples
    frames = samples[:frame_count * frame_samples].reshape(
        frame_count, frame_samples)
    quiet = np.sqrt(np.mean(frames * frames, axis=1)) <= SILENCE_RMS
    minimum_pause_frames = max(
        1, int(np.ceil(AUTO_LANGUAGE_MIN_PAUSE_SECONDS / FRAME_SECONDS)))

    pause_midpoints: list[int] = []
    pause_start = None
    for frame_index, is_quiet in enumerate(quiet):
        if is_quiet and pause_start is None:
            pause_start = frame_index
        if not is_quiet and pause_start is not None:
            if frame_index - pause_start >= minimum_pause_frames:
                pause_midpoints.append(
                    ((pause_start + frame_index) * frame_samples) // 2)
            pause_start = None
    if pause_start is not None and frame_count - pause_start >= minimum_pause_frames:
        pause_midpoints.append(
            ((pause_start + frame_count) * frame_samples) // 2)

    spans: list[tuple[int, int]] = []
    start = 0
    while True:
        # A qualifying pause begins the next language-decision run.  Scan
        # forward rather than taking the latest pause: otherwise a bilingual
        # first run can survive until that later pause.  Keep both this run
        # and the final tail large enough for Whisper's language detection.
        lower = start + minimum
        upper = min(start + maximum, len(samples) - minimum)
        candidates = [cut for cut in pause_midpoints if lower <= cut <= upper]
        if candidates:
            cut = candidates[0]
            spans.append((start, cut))
            start = cut
            continue
        if len(samples) - start > maximum:
            # Do not create a tiny final tail.  This is the deterministic
            # fallback for continuous speech, where no natural pause exists.
            spans.append((start, upper))
            start = upper
            continue
        spans.append((start, len(samples)))
        break
    return spans


def _join_span_texts(left: str, right: str) -> str:
    """Join two decoded spans: no space where both sides of the seam are CJK
    (a pause in Chinese speech is not a word boundary), one space otherwise.
    A downstream caption joiner applies this rule to spans. Keep the shared
    implementation here so platform callers do not drift."""
    if left and right and "\u4e00" <= left[-1] <= "\u9fff" and "\u4e00" <= right[0] <= "\u9fff":
        return left + right
    return (left + " " + right).strip()


def trie_coverage_gap(audio, segments: list[dict]) -> dict | None:
    """Return aggregate diagnostics for a voiced window that ended too early.

    OpenAI Whisper reports one segment per fixed window when the trie path
    runs with ``without_timestamps=True``.  The field failure that motivated
    this guard returned 14 tokens for a window containing 18.3 seconds of
    voiced audio, while the unchanged plain decoder returned the missing
    speech.  Never include text here: this result is logged for diagnosis.
    """
    samples = np.asarray(audio, dtype=np.float32)
    frame_samples = int(SAMPLE_RATE * FRAME_SECONDS)
    for index, segment in enumerate(segments):
        start = max(0, int(float(segment.get("start", 0.0)) * SAMPLE_RATE))
        end = min(
            len(samples),
            int(float(segment.get("end", 0.0)) * SAMPLE_RATE),
        )
        window = samples[start:end]
        frame_count = len(window) // frame_samples
        if frame_count == 0:
            continue
        frames = window[:frame_count * frame_samples].reshape(
            frame_count, frame_samples)
        rms = np.sqrt(np.mean(frames * frames, axis=1))
        voiced_seconds = float(np.count_nonzero(rms > SILENCE_RMS) * FRAME_SECONDS)
        token_count = len(segment.get("tokens") or [])
        if (
            voiced_seconds >= TRIE_COVERAGE_MIN_VOICED_SECONDS
            and token_count
            < voiced_seconds * TRIE_COVERAGE_MIN_TOKENS_PER_VOICED_SECOND
        ):
            return {
                "window": index + 1,
                "tokens": token_count,
                "voiced_seconds": round(voiced_seconds, 1),
            }
    return None


def _normalize_phrase(text: str) -> str:
    """Case-, punctuation- and whitespace-insensitive form used for blocklist
    lookups. Strips ASCII *and* CJK/full-width terminal punctuation, because
    the decoder emits "谢谢。" and "ご視聴ありがとうございました。" as readily
    as the bare phrase."""
    stripped = text.strip().strip(" \t\r\n.,!?-–—…\"'。、！？「」（）")
    return " ".join(stripped.split()).casefold()


# Whisper's greatest hits on silence. Every one of these is a training-set
# artifact (YouTube outros, caption credits) the decoder free-runs into when
# handed near-silence — not something a meeting participant said. Stored
# already-normalized; `test_blocklist_is_non_empty_and_normalized` pins that,
# because an un-normalized entry silently never matches.
HALLUCINATION_PHRASES = frozenset(
    _normalize_phrase(p)
    for p in (
        "Thank you",
        "Thanks for watching",
        "Thank you for watching",
        "Please subscribe",
        "Thank you very much",
        "谢谢",
        "谢谢大家",
        "字幕由 Amara.org 社区提供",
        "ご視聴ありがとうございました",
    )
)

# A blocked output must be SHORT. Above this many words the text is a real
# transcript that merely contains a polite phrase, not the artifact itself.
MAX_BLOCKABLE_WORDS = 4

def blocked_hallucination(text: str, hallucination_guard: bool = False) -> bool:
    """True when `text` is a known silence artifact and the CALLER has asked
    for the blocklist to apply.

    **Applicability is the caller's decision, not this function's.** The
    first cut inferred it from `voiced_fraction`, which was exactly backwards
    on both pipelines and is the reason the parameter is gone:

      - Meeting notes' own gate refuses to forward anything below its
        `minVoicedFraction` (0.15) — so a rule keyed on `< 0.15` could NEVER
        fire on the notes path, the one path the blocklist exists for. A 0.16
        clip decoding to "Thank you." sailed through.
      - Dictation gates with NO floors by design, so a real push-to-talk
        "谢谢" spoken inside a long key-hold measures well under 0.15 — and
        got silently deleted. Eating real words on the feature deliberately
        built to never eat them.

    A threshold shared with a Swift gate constant is a coupling that inverts
    the moment either side moves. So the wire now carries an explicit
    `hallucination_guard` flag: notes sends true (background-queued, a
    fabricated sentence in a saved document is the worst outcome), dictation
    sends false (interactive, the user is watching, and eating a real word is
    the worst outcome). `PipelineParitySelfCheck` pins both.

    When the guard is on, the phrase must additionally be the ENTIRE output
    and at most `MAX_BLOCKABLE_WORDS` words — a real transcript that merely
    contains "thank you for joining" is untouched.
    """
    if not text or not hallucination_guard:
        return False
    normalized = _normalize_phrase(text)
    if normalized not in HALLUCINATION_PHRASES:
        return False
    return len(normalized.split()) <= MAX_BLOCKABLE_WORDS


def _is_mlx_whisper_dir(path: str) -> bool:
    """True only for a real MLX Whisper export: config.json plus weights.

    Deliberately strict — a wrong positive here is silent and total (every
    transcribe raises FileNotFoundError), while a wrong negative merely
    falls back to the published repo, which works.
    """
    d = Path(path)
    if not d.is_dir() or not (d / "config.json").exists():
        return False
    return (
        any(d.glob("weights*.safetensors"))
        or any(d.glob("weights*.npz"))
        or (d / "model.safetensors").is_file()
        or (d / "model.npz").is_file()
    )


def _mlx_repo_for(hf_id: str) -> str:
    """openai/whisper-base -> mlx-community/whisper-base-mlx (etc.).

    The turbo checkpoints are published WITHOUT the -mlx suffix
    (mlx-community/whisper-large-v3-turbo), unlike the classic sizes.
    An id that is ALREADY an mlx-community repo passes through untouched, so
    `DICTATION_ASR_MODEL=mlx-community/whisper-large-v3-turbo-4bit` selects exactly
    that repo instead of being mangled into a second `-mlx` suffix.
    """
    if hf_id.startswith("mlx-community/"):
        return hf_id
    name = hf_id.rsplit("/", 1)[-1]
    if "turbo" in name:
        return f"mlx-community/{name}"
    return f"mlx-community/{name}-mlx"


def _classic_model_name(hf_id: str) -> str:
    """Return the OpenAI/faster-whisper model name for any shipped checkpoint id.

    Dictation deliberately ships an MLX repository id such as
    ``mlx-community/whisper-large-v3-turbo-4bit``.  The CPU engines do not
    consume that repository; they load the equivalent upstream model by name.
    Taking only the text after the final ``-`` turns that id into the bogus
    model name ``4bit``.  Strip the repository/prefix and MLX representation
    suffix while preserving compound Whisper names such as
    ``large-v3-turbo``.
    """
    name = hf_id.rsplit("/", 1)[-1]
    if name.startswith("whisper-"):
        name = name[len("whisper-"):]
    for suffix in ("-4bit", "-8bit", "-q4", "-q8", "-fp16", "-bf16"):
        if name.endswith(suffix):
            name = name[:-len(suffix)]
            break
    return name


class MelBinMismatch(RuntimeError):
    """The CTranslate2 model and the features faster-whisper builds for it
    disagree on the number of mel bins, so no decode can succeed."""


def check_faster_mel_bins(model) -> None:
    """Fail at init when a faster-whisper model cannot decode anything.

    Large v3 and Turbo take 128 mel bins. faster-whisper (1.2.1) sizes its
    feature extractor from the folder's preprocessor_config.json and falls
    back to 80 bins when that file is absent, so a converted Turbo folder
    holding only model.bin, config.json and tokenizer.json loads cleanly and
    then hands the model 80-bin features at every decode. Checked here so
    the host learns it at startup, with a named error, rather than
    from every hold failing.

    Compares `model.model.n_mels` (the CTranslate2 model's own input size)
    with the rows of `model.feature_extractor.mel_filters`. Either value
    missing means a provider that does not expose it, and nothing is
    claimed.
    """
    model_bins = getattr(getattr(model, "model", None), "n_mels", None)
    filters = getattr(getattr(model, "feature_extractor", None), "mel_filters", None)
    shape = getattr(filters, "shape", None)
    feature_bins = shape[0] if shape else None
    if (isinstance(model_bins, int) and isinstance(feature_bins, int)
            and model_bins != feature_bins):
        raise MelBinMismatch(
            f"the model takes {model_bins} mel bins but its folder gives the feature "
            f"extractor {feature_bins}; preprocessor_config.json is missing or belongs "
            "to another model")


# ---------------------------------------------------------------------------
# Weight-filename compatibility shim
# ---------------------------------------------------------------------------
# mlx_whisper's loader (`load_models.load_model`) looks for EXACTLY
# `weights.safetensors`, falls back to `weights.npz`, and hands whichever name
# it landed on to `mx.load()` unconditionally -- it never checks that the
# fallback exists. Several mlx-community whisper repos (notably the recent
# QUANTIZED exports, e.g. `whisper-large-v3-turbo-4bit`) publish their weights
# as `model.safetensors` instead. On those repos the loader falls through to
# the absent `weights.npz`, `mx.load()` dispatches on the `.npz` extension, and
# the load dies with
#
#     ValueError: [load_npz] Input must be a zip file or a file-like object
#     that can be opened with zipfile.ZipFile
#
# which reads like a corrupt download but is purely a filename-convention
# mismatch: the cache, mlx and the repo are all fine. (Measured 2026-08-07:
# the same snapshot loads and decodes correctly the moment the file is
# reachable under the expected name.)
#
# Fix: materialize a tiny shim directory of SYMLINKS (config.json +
# weights.safetensors -> model.safetensors) and hand mlx_whisper that path.
# Symlinks, not copies -- these files are hundreds of megabytes. The tokenizer
# is unaffected: mlx_whisper reads its tiktoken vocab from the package's own
# `assets/`, never from the model repo.
_MLX_WEIGHT_NAMES = ("weights.safetensors", "weights.npz")
_MLX_ALT_WEIGHT_NAMES = {"model.safetensors": "weights.safetensors",
                         "model.npz": "weights.npz"}

_MLX_SHIM_ROOT = Path.home() / ".cache" / "dictation" / "mlx_whisper_repos"


def _mlx_loadable_dir(snapshot_dir: Path, slug: str) -> Path:
    """Return a directory mlx_whisper can load: `snapshot_dir` itself when it
    already uses an expected weight filename, else a symlink shim.

    Raises FileNotFoundError naming the directory contents when neither the
    expected nor any known alternative weight file is present -- a loud,
    actionable failure instead of the `[load_npz]` red herring.
    """
    if any((snapshot_dir / n).exists() for n in _MLX_WEIGHT_NAMES):
        return snapshot_dir
    for alt, expected in _MLX_ALT_WEIGHT_NAMES.items():
        src = snapshot_dir / alt
        if not src.exists():
            continue
        shim = _MLX_SHIM_ROOT / slug
        shim.mkdir(parents=True, exist_ok=True)
        for name, target in (("config.json", snapshot_dir / "config.json"),
                             (expected, src)):
            link = shim / name
            if link.is_symlink() or link.exists():
                link.unlink()
            link.symlink_to(target)
        return shim
    present = sorted(p.name for p in snapshot_dir.iterdir()) if snapshot_dir.is_dir() else []
    raise FileNotFoundError(
        f"no MLX Whisper weights in {snapshot_dir}: expected one of "
        f"{list(_MLX_WEIGHT_NAMES) + list(_MLX_ALT_WEIGHT_NAMES)}, found {present}")


#: Whisper keeps only the LAST `n_ctx // 2 - 1` = 223 tokens of a prompt
#: (`decoding.py:609`). Everything earlier is silently dropped, which is why
#: ordering matters more than length: research measured a 100-term prompt at
#: 0.3366 with the wanted terms FIRST (truncated away, indistinguishable from
#: sending no vocabulary at all) and 0.7778 with the same terms LAST — 44
#: points, purely from position.
MLX_PROMPT_TOKEN_BUDGET = 223

#: A short, ordinary Simplified sentence placed in front of the vocabulary.
#:
#: WHY: `initial_prompt` conditions script as well as content, and a prompt
#: of nothing but Latin terms gives the model no signal about which Chinese
#: script to emit. A synthetic audio check found 3 of 17 Chinese
#: clips came back in TRADITIONAL characters (`你看看今天这么` ->
#: `你看看今後這麼`). Adding this line fixed 3/3, left 16 Chinese controls
#: byte-identical, and — the check that mattered before making it
#: unconditional — harmed 0 of 10 ENGLISH-only clips, pulling no Chinese into
#: them and changing their length by at most 6 characters.
#:
#: It is prose, not a list, deliberately: Iteration 8 established that
#: `initial_prompt` is read as text preceding the audio, so a frame that
#: reads like a sentence is a better continuation prior than the bare list.
#:
#: **This seed depends on Whisper's temperature fallback and must not be
#: separated from it.** A representative utterance check found
#: 2026-08-18: at `temperature=0.0` one 15.7 s clip came back as
#: `以下的一段简体中文和音简体中文, 以下的一段简体中文, …` — the seed itself
#: regurgitated in a degenerate loop — and the DEFAULT temperature ladder
#: recovered the correct English on retry. The fallback fires on 4% of real
#: utterances and costs a full re-decode of the 30 s window each time
#: (~790 ms here, up to 5.3 s observed), which makes it a tempting latency
#: saving: p50 854 -> 849 ms, max 5262 -> 1357 ms for apparently nothing.
#: It is not nothing. Conditioning the decode on a prompt RAISES the
#: probability of a degenerate loop, and the fallback is the existing net
#: under it. Anyone changing either one must consider the other.
#: Overridable so the seed's COST can be measured against its benefit. It is
#: not free: research run A79 measured the seeded arm at 0.4641 usable against
#: 0.5033 with no seed on 306 code-mixed clips — 7 discordant against 19,
#: exact p = 0.029, roughly 4 points of recovery spent on ~24 tokens of a
#: 223-token budget. On THAT corpus it also bought nothing: zero Traditional
#: characters in either arm, counted with script-distinguishing glyphs only.
#:
#: That does not settle it here. The 3-of-17 Traditional flip this seed was
#: added for was measured on `whisper-large-v3-turbo-4bit` with real speech,
#: which is a different model and different audio from A79's. Both halves have
#: to be weighed on the same corpus before the seed is changed, and this
#: variable is what makes that runnable (`DICTATION_MLX_SCRIPT_SEED=""` disables it).
MLX_SCRIPT_SEED = os.environ.get(
    "DICTATION_MLX_SCRIPT_SEED", "以下是一段简体中文和英文混合的口述记录。")
MLX_ENGLISH_SEED = os.environ.get(
    "DICTATION_MLX_SCRIPT_SEED", "The following is an English dictation.")


def _vocabulary_prompt(phrases, budget: int = MLX_PROMPT_TOKEN_BUDGET, *, language=None):
    """Bias phrases -> an `initial_prompt`, seeded and budget-bounded.

    Callers pass the vocabulary in RELEVANCE ORDER, **most relevant FIRST** —
    the order `DemoSourceScanner.ranked` already produces and the order the
    Swift host already sends. This function keeps the most relevant terms and
    emits them with the most relevant LAST, because Whisper keeps only the
    final `n_ctx // 2 - 1` = 223 prompt tokens and discards the head.

    The contract used to be the opposite ("least relevant first"), and nothing
    on the wire honoured it: the host has always sent distinctive terms first,
    so the terms that mattered sat at the end that gets thrown away. Measured
    by a research run on 306 clips, identical vocabulary, only position changed:

        terms   targets FIRST   targets LAST
          100     0.3366          0.7778
          250     0.3170          0.7614
          500     0.3235          0.7712

    Targets-first lands at 0.32-0.34 at every size, which IS the no-vocabulary
    baseline of 0.3660 — past the budget the prompt is not weakened, it is
    absent. Putting the ranking here rather than asking callers to pre-invert
    keeps the one place that knows about the 223-token window as the one place
    that reasons about it.

    **Unverified in the register this path actually runs.** Those numbers are
    English; macOS dictation is ~82% code-mixed. Truncation should be a
    token-budget mechanic rather than a linguistic one, but "should" is not a
    measurement and the code-mixed ladder was still in flight when this landed.
    This function is the whole rollback surface.

    The budget is a CEILING, never a target. Research measured a 17-term
    prompt beating a 250-term prompt by 8.8 points on the same scored terms,
    so carrying terms the utterance does not contain has a real cost and
    filling spare room makes recovery worse, not better.

    Token counting is approximate on purpose — importing a tokenizer here
    would pull a heavy dependency onto the decode path for a bound that only
    needs to be conservative. Latin terms measured 2.8 tokens each on the
    shipped tokenizer and CJK runs about one token per character, so the
    estimate below over-counts slightly and truncates early rather than
    late.
    """
    terms = [p.strip() for p in (phrases or []) if p and p.strip()]
    if not terms:
        return None
    written = _prompt_terms(terms, budget)

    # Say what was dropped. Silent truncation is what let a 1000-term ceiling
    # coexist with a ~33-74 term capacity for months without anyone noticing:
    # the vocabulary appeared to be configured and simply was not sent.
    dropped = len(terms) - len(written)
    if dropped:
        print(f"whisper_mac: vocabulary truncated — {len(written)} of {len(terms)} "
              f"terms fit the {budget}-token prompt window, {dropped} dropped",
              file=sys.stderr, flush=True)

    # A Chinese carrier can make even an English-pinned decode translate or
    # loop instead of listening. Reproduced with exactly repo + repository
    # on the installed model, on both greedy and beam paths. Match the known
    # chunk language; an explicit DICTATION_MLX_SCRIPT_SEED still wins.
    seed = MLX_ENGLISH_SEED if language == "en" else MLX_SCRIPT_SEED
    if not written:
        return seed
    return seed + " " + ", ".join(written)


#: The Chinese-facing prompt's carrier sentence. The terms sit inside it
#: 、-separated and it closes with "。", so a chunk decoded under it sees
#: Chinese punctuation style in its own context window.
#:
#: WHY a separate prompt per chunk language: `initial_prompt` conditions
#: style as well as content, and the Latin prompt's 223-token window is
#: mostly comma-separated English terms — Chinese decoded under it loses its
#: punctuation. Measured on representative held dictations
#: (mlx-community/whisper-large-v3-turbo-4bit, the production span loop):
#: three zh clips under the Latin prompt kept at most half-width commas and
#: no "。" (matching the 107 Chinese-bearing rows in this machine's history:
#: 76 carry no punctuation at all, and a "，" occurs in none); under this
#: carrier one of the three closes with "。" and the others read identically.
#: That is a PARTIAL recovery at the source — the guaranteed terminal stop
#: lives in the host-side formatter (DictationFormatter v5), which never
#: waits on the model. The reverse
#: direction is why this is a SEPARATE prompt and not a replacement: the
#: bare seed on en audio regurgitates in a degenerate loop, so en chunks
#: keep the Latin prompt unchanged. The carrier, not the terms, is what
#: changes; a term's recovery odds do not move with its separator.
#:
#: Applied only to chunks the SOT probe read as zh (or a caller-pinned
#: language="zh"): the pipeline already makes one language decision per
#: natural-pause span (`auto_language_decode_spans`), which is what makes a
#: code-switching user safe — each span gets the prompt of the language it
#: was actually heard in. A chunk the probe could not read keeps the Latin
#: prompt, so a broken probe degrades to today's behavior, never to the
#: seed-only loop.
MLX_ZH_PROMPT_HEAD = "以下是一段简体中文和英文混合的口述记录，其中会提到 "
MLX_ZH_PROMPT_TAIL = " 这些名称。"


def _zh_vocabulary_prompt(phrases, budget: int = MLX_PROMPT_TOKEN_BUDGET):
    """The zh-chunk variant of `_vocabulary_prompt`: same terms, same budget,
    same order, wrapped in `MLX_ZH_PROMPT_HEAD/TAIL` instead of the Latin
    seed-and-comma-list. No truncation log of its own — the Latin build in
    the same `transcribe` call already reported the dropped terms."""
    terms = [p.strip() for p in (phrases or []) if p and p.strip()]
    if not terms:
        return None
    written = _prompt_terms(terms, budget)
    if not written:
        return MLX_SCRIPT_SEED
    return MLX_ZH_PROMPT_HEAD + "、".join(written) + MLX_ZH_PROMPT_TAIL


def _prompt_terms(phrases, budget: int = MLX_PROMPT_TOKEN_BUDGET) -> list[str]:
    """The terms `_vocabulary_prompt` writes, IN THE ORDER IT WRITES THEM.

    That is the most relevant terms that fit the budget, most relevant LAST:
    the reverse of `phrases`, minus whatever the budget dropped. One function
    for both readers, so the prompt and the regurgitation guard below cannot
    disagree about what the prompt says. They did: the guard's order check was
    written while the prompt kept the caller's order, and the reversal that
    landed the same day left it checking the opposite order, so a real echo
    was kept and a list spoken in the caller's order was deleted.
    """
    terms = [p.strip() for p in (phrases or []) if p and p.strip()]

    def cost(s: str) -> int:
        cjk = sum(1 for c in s if "\u4e00" <= c <= "\u9fff")
        return cjk + max(1, round((len(s) - cjk) / 3.5)) + 1  # +1 for ", "

    remaining = budget - cost(MLX_SCRIPT_SEED)
    kept: list[str] = []
    for term in terms:                    # most relevant first: keep the best
        c = cost(term)
        if c > remaining:
            break
        remaining -= c
        kept.append(term)
    kept.reverse()                        # most relevant LAST: the tail survives
    return kept


#: A comma-run of at least this many vocabulary terms, IN PROMPT ORDER, is
#: the prompt coming back rather than speech.
#:
#: Four, not three, and order-checked — both because this function also runs
#: on the MEETING NOTES path, whose `bias_phrases` are ATTENDEE NAMES
#: (a downstream bias-prompt builder). "今天参加的有 Jossie Lin、Yurin Zhang、Kari
#: Homenick" is three comma-separated vocabulary terms and a completely
#: ordinary sentence; eating it would be a silent corruption of a meeting
#: transcript, which is far worse than the 1%-of-clips defect this guard
#: exists to catch. Regurgitation is the model CONTINUING the prompt, so the
#: terms come back in the order the prompt WRITES them (`_prompt_terms`: most
#: relevant last, the reverse of the order they were sent); a person naming
#: attendees does not recite them in that order.
REGURGITATION_RUN = 4


def _strip_prompt_regurgitation(text: str, phrases) -> tuple[str, int]:
    """Remove a run of >=REGURGITATION_RUN comma-separated vocabulary terms.

    Deliberately narrow. It matches the LIST shape — terms separated by
    nothing but commas and whitespace — rather than "the output contains N
    vocabulary terms", because a real sentence can legitimately name three
    of them and must survive. A trailing partial run is stripped along with
    the rest; the surrounding speech is kept exactly.

    Returns the cleaned text and how many terms were dropped, so the caller
    can report a fact rather than silently rewriting the user's words.
    """
    terms = sorted({p.strip() for p in (phrases or []) if p and p.strip()},
                   key=len, reverse=True)
    if not terms or not text:
        return text, 0
    alt = "|".join(re.escape(t) for t in terms)
    # term (, term){2,} with only commas/whitespace between
    pattern = re.compile(
        rf"(?:{alt})(?:\s*[,、]\s*(?:{alt})){{{REGURGITATION_RUN - 1},}}"
        rf"(?:\s*[,、]\s*)?",
        re.IGNORECASE)
    # Positions in the prompt as written, not in `phrases`: the prompt lists
    # the terms in reverse, and a term the budget dropped was never in it.
    order = {t.lower(): i for i, t in enumerate(_prompt_terms(phrases))}

    def _in_prompt_order(run: list) -> bool:
        """True when the run follows the prompt's own ordering.

        Strictly increasing positions, allowing gaps: the decoder skips
        terms but does not reorder them when it is continuing the list.
        """
        seen = [order.get(t.lower(), -1) for t in run]
        if any(i < 0 for i in seen):
            return False
        return all(a < b for a, b in zip(seen, seen[1:]))

    dropped = 0

    def _count(match: "re.Match") -> str:
        nonlocal dropped
        run = re.split(r"\s*[,、]\s*", match.group(0).strip(" ,、"))
        if not _in_prompt_order(run):
            return match.group(0)          # a person listing things; leave it
        dropped += len(run)
        return ""
    cleaned = pattern.sub(_count, text)
    if not dropped:
        return text, 0
    # Tidy the punctuation the removal left behind without touching words.
    cleaned = re.sub(r"\s{2,}", " ", cleaned)
    cleaned = re.sub(r"[ \t]*([,，、.。?？!！])", r"\1", cleaned)
    return cleaned.strip(" ,、"), dropped


def _resolve_mlx_repo(
    repo: str,
    *,
    revision: str | None = None,
    allow_download: bool = True,
) -> str:
    """`repo` (a local directory or an HF id) -> a path mlx_whisper can load.

    A local directory already holding an expected weight filename is returned
    untouched (no network, no shim). Anything else resolves through the HF
    cache (`snapshot_download`, a no-op when already cached) and is shimmed
    when its weight file is named differently.
    """
    local = Path(repo)
    if local.is_dir():
        return str(_mlx_loadable_dir(local, local.name))
    from huggingface_hub import snapshot_download

    snapshot = Path(snapshot_download(
        repo_id=repo,
        revision=revision,
        local_files_only=not allow_download,
    ))
    return str(_mlx_loadable_dir(snapshot, repo.replace("/", "--")))


class _ProvidersShim:
    """`op_init` reports `engine.encoder.get_providers()`; give it something
    truthful without pretending to be an ORT session."""

    def __init__(self, names: list[str]):
        self._names = names

    def get_providers(self) -> list[str]:
        return self._names


class WhisperMac:
    """Whisper on Apple silicon behind the WhisperQnn engine surface."""

    # Default model: large-v3-turbo. Measured on the 3-meeting BlueDot-parity
    # benchmark (76 min real 4-speaker audio): coverage-masked WER
    # 11.8/14.5/12.1% vs 19.9/24.0/14.8% for whisper-base — every meeting
    # under the 15% North-Star bar — at RTF 0.04-0.21 (target <= 0.5).
    # Cost: ~1.6 GB weights vs ~150 MB; acceptable under the lazy-load +
    # idle-unload invariant (spike doc). The QNN/Windows side keeps base.
    def __init__(self, model_dir: str | Path | None = None,
                 hf_id: str = "openai/whisper-large-v3-turbo",
                 hf_revision: str | None = None,
                 allow_model_download: bool = True,
                 use_qnn: bool = True,  # accepted + ignored: wire-compat
                 p_ilm_path: str | Path | None = None):
        self.hf_id = hf_id
        self.hf_revision = hf_revision
        self.allow_model_download = allow_model_download
        self.model_dir = str(model_dir) if model_dir else None
        self._engine_name = None
        # Filename-shim resolution stays LAZY, like the weights themselves: it
        # may need a snapshot_download, and init must not pay (or fail on)
        # network I/O that the first transcribe would do anyway. Declared here
        # so the attribute exists on every engine branch, not just mlx.
        self._mlx_repo_resolved = None
        self._fw_model = None
        self._torch_model = None
        self._tokenizer = None
        # Decodes this engine has run since it loaded, preview ones included:
        # the first after a load is the slow one (stats["engine_warm"]).
        self._decodes = 0

        # Kept for surface parity + the future L2 wiring; the current
        # transcribe path never reads it (density ratio needs stepwise
        # decoding, see module docstring).
        self.p_ilm = None

        # Resolve an engine now so a broken environment fails at init (the
        # host surfaces init errors loudly) rather than mid-meeting.
        # `DICTATION_ASR_ENGINE` pins the engine instead of taking the first that
        # imports. Added for the trie: `BiasTrie` needs beam search, and of the
        # three engines only openai-whisper exposes one -- mlx raises
        # NotImplementedError and faster-whisper's is inside CTranslate2. Without
        # a pin, an mlx-capable machine can never exercise the biased path, so
        # the feature would be untestable on exactly the hardware it ships on.
        # Unset behaves exactly as before.
        forced = (os.environ.get("DICTATION_ASR_ENGINE") or "").strip().lower()
        if forced and forced not in ("mlx", "faster", "openai"):
            raise ValueError(
                f"DICTATION_ASR_ENGINE={forced!r} is not one of mlx/faster/openai")

        # The independent Windows product packages CTranslate2, while macOS
        # packages MLX. Never mistake an arbitrary directory for either model.
        has_local_model = bool(self.model_dir and (
            _is_mlx_whisper_dir(self.model_dir)
            or (forced == "faster" and all(
                (Path(self.model_dir) / name).is_file()
                for name in ("model.bin", "config.json", "tokenizer.json")
            ))
        ))
        if not self.allow_model_download and not has_local_model:
            raise FileNotFoundError(
                "no active speech-model generation; choose, download, and enable "
                "a model in Dictation before starting transcription")

        try:
            if forced and forced != "mlx":
                raise ImportError(f"engine pinned to {forced}")
            import mlx_whisper  # noqa: F401
            self._engine_name = "mlx"
            # Model weights resolve lazily on first transcribe (mlx_whisper
            # caches per path_or_hf_repo); record the repo now.
            # A model_dir counts as an MLX Whisper repo ONLY if it actually
            # holds one. A host may pass a shared model directory that holds
            # other models, and treating any existing path as a Whisper repo made every transcribe fail instantly
            # with FileNotFoundError on weights.safetensors — the 2026-08-05
            # "notes produce nothing" field failure. Fall back to the
            # published repo when the directory isn't a Whisper export.
            self._mlx_repo = (self.model_dir
                              if self.model_dir and _is_mlx_whisper_dir(self.model_dir)
                              else _mlx_repo_for(hf_id))
        except ImportError:
            if forced == "mlx":
                raise  # An explicit provider never falls back to another engine.
            try:
                if forced and forced != "faster":
                    raise ImportError(f"engine pinned to {forced}")
                from faster_whisper import WhisperModel
                self._engine_name = "faster"
                size = _classic_model_name(hf_id)
                self._fw_model = WhisperModel(
                    self.model_dir or size, device="cpu", compute_type="int8")
                check_faster_mel_bins(self._fw_model)
            except ImportError:
                if forced == "faster":
                    raise  # Missing packaged provider is an installation error.
                import whisper as openai_whisper
                self._engine_name = "openai"
                size = _classic_model_name(hf_id)
                self._torch_model = openai_whisper.load_model(size)

        self.encoder = _ProvidersShim([f"WhisperMac[{self._engine_name}]"])

    # -- surface parity with WhisperQnn ------------------------------------
    @property
    def on_npu(self) -> bool:
        # Honest reading of "accelerated": MLX runs on the GPU/unified
        # memory; the fallbacks are CPU.
        return self._engine_name == "mlx"

    @property
    def tokenizer(self):
        """Only needed when the host passes db_path (L2 corpus build).
        transformers is not a hard dependency of the L0 path."""
        if self._tokenizer is None:
            from transformers import WhisperTokenizer
            source = self.model_dir if self.model_dir and _is_mlx_whisper_dir(
                self.model_dir) else self.hf_id
            self._tokenizer = WhisperTokenizer.from_pretrained(
                source,
                revision=self.hf_revision,
                local_files_only=not self.allow_model_download,
            )
        return self._tokenizer

    def transcribe(self, audio: np.ndarray,
                   bias_phrases: list[str] | None = None,
                   beam_size: int = 1,
                   p_you=None,
                   lam: float = 0.1,
                   mu: float = 0.1,
                   return_words: bool = False,
                   language: str | None = None,
                   voiced_fraction: float | None = None,
                   hallucination_guard: bool = False,
                   personalize: bool = True,
                   initial_prompt: str | None = None,
                   case_only_phrases=None,
                   vocabulary_search: bool = False,
                   phrase_deltas=None,
                   temperature: float | None = None) -> dict:
        # x86 lane fix (2026-08-29): the live caption path
        # (a live caption caller) passes initial_prompt on every
        # decode; the stub already accepts it (see its comment, agent H
        # 2026-08-24) but the real class was never updated, so every live
        # decode on the x64 'faster' engine raised TypeError. Accepted here
        # and forwarded only where the engine supports it (faster_whisper).
        """audio: float32 mono 16 kHz. Returns {text, stats} — the same
        contract as WhisperQnn.transcribe. MLX vocabulary_search enables the
        small GPU beam for final dictations; callers leave it off for previews
        and word timestamps. L2 remains deferred on this backend.

        return_words (default False, additive — existing callers unchanged):
        when true, also returns "words": [{"word","start","end"}, ...],
        word-level timestamps for the diarization merge in attribute.py.
        Only wired for the mlx engine (mlx_whisper's own
        `word_timestamps=True`, DTW-aligned) — the faster/openai fallbacks
        return an empty word list rather than erroring, same
        graceful-degradation shape as the L1/L2 deferral above.

        language: BCP-47-ish Whisper language code ("en", "zh", ...) or None
        (default) to let Whisper AUTO-DETECT from the audio.

        This used to be hardcoded `language="en"` at all three call sites,
        which was silently catastrophic for anyone who does not dictate only
        in English. Measured 2026-08-06 over this user's own 1,657-utterance
        corpus (development iteration 14): on the Chinese-bearing
        utterances, forcing an English decode produced **WER 8.75** — worse
        than emitting nothing, because Whisper does not fail on a language
        mismatch. It TRANSLATES: it paraphrases the gist into English rather
        than transcribing what was said, so the user gets fluent English text
        they never uttered pasted into their editor. Their own settings in
        another dictation app list English and Chinese, so this was not a
        hypothetical.

        Default None (auto-detect) rather than a configured default, because
        the failure mode of guessing wrong is this severe and Whisper's own
        detector is the best signal available at decode time.

        voiced_fraction (default None = unknown): the share of the clip's
        analysis frames that cleared the silence threshold, measured by the
        caller's own gate (the Mac's `AudioGate`) on the samples it
        sent. DIAGNOSTIC ONLY — reported back in stats so a dropped or
        suspicious utterance can be explained after the fact. It deliberately
        does NOT arm the blocklist: see `blocked_hallucination` for why
        inferring applicability from this number was wrong on both pipelines.

        hallucination_guard (default False = do not block): the caller
        declaring that on ITS pipeline, a short known-artifact phrase should
        be dropped rather than emitted. Notes sends true, dictation sends
        false. Default False because the safe answer for an unknown caller is
        to emit what was decoded. It gates both the phrase blocklist and
        looks_like_junk's stock-phrase stems ("see you next", "stay tuned").

        case_only_phrases (default None = none): entries of `bias_phrases`
        whose declared spelling restores case only (`declared_casing.apply`'s
        `case_only`). asr_server sends the words the host learned from a
        single correction, so this pass and the server's apply the same rule
        (LEARNING.md §4.2: a spaced form is rewritten only by an alias seen
        on two dictations).

        temperature (default None = Whisper's own fallback ladder): a single
        sampling temperature for every window instead of the ladder. Only the
        Dictation live preview passes 0.0: its text is display-only, and on
        quiet or low-confidence audio the ladder re-decodes the window up to
        five more times while the final transcription of the same hold
        waits for the engine. Final decodes keep the ladder; see
        MLX_SCRIPT_SEED for why their prompt depends on it."""
        t0 = time.monotonic()
        audio = np.asarray(audio, dtype=np.float32)
        words: list[dict] = []
        # Reason this decode produced nothing, when it produced nothing on
        # PURPOSE. Reaches the op response (and therefore the logs) so a
        # dropped utterance is visible rather than an unexplained blank —
        # `text: ""` with `ok: true` and no explanation is the shape of
        # failure this file has already been burned by once (see
        # looks_like_junk's docstring).
        dropped_reason = None
        min_logprob = None
        mean_logprob = None
        # The language the decode actually RAN under — Whisper's own
        # detection when `language` is None (the shipped default), the pinned
        # code otherwise. Reported in stats as METADATA (a BCP-47-ish code
        # like "en"/"zh", never transcript content) so the host can say
        # honestly what the last dictation was heard as, instead of the UI
        # asserting "Auto-detect" with no evidence. None on engines that do
        # not expose it; a consumer must treat absence as "unknown", never
        # as a detection.
        detected_language = None
        # Detection confidence, WHERE the engine exposes it. Absence still
        # means "unknown" and never "unconfident" — the contract the
        # `detected_language` comment above states applies to this field too,
        # and a consumer that reads None as a low score would mark every
        # openai-engine decode suspicious.
        #
        # Per-engine sourcing, because the number does NOT mean the same thing
        # on both: faster_whisper reports its own first-window detection for
        # each call (one call per span when the language is automatic, one
        # whole-clip call when pinned) and this reports the weakest span's,
        # while the mlx path takes its own reading of each chunk (see
        # `auto_language_decode_spans`) and reports the WEAKEST of them —
        # including chunks the gate then dropped. Min, not mean, for the same
        # reason `min_logprob` is a min: one bad chunk inside an otherwise
        # fine utterance is exactly the case worth surfacing, and a mean
        # averages it away.
        language_probability = None
        # How many chunks `speech_gate_verdict` dropped. None is NOT zero:
        # None means no gate ran on this call (pinned language, a non-mlx
        # engine, or an mlx build whose internals `_MlxSotProbe` could not
        # reach), and zero means the gate ran and kept everything. Anything
        # asserting that the gate works must refuse to accept None as
        # evidence — a probe that never ran finds nothing, and "found
        # nothing" is what a working gate over clean speech also looks like.
        gated_chunks = None

        # Pre-decode silence check — the macOS parity port of the shared
        # SOT-step no-speech guard. Handed audio with nothing audible in it,
        # Whisper's decoder free-runs and emits training-set artifacts; the
        # only reliable fix is not to hand it such audio, and the cheapest
        # place to enforce that on EVERY client (including non-Swift ones
        # that have no gate) is right here, before a ~1s decode is spent.
        if not has_speech_energy(audio):
            return {
                "text": "",
                "words": [] if return_words else None,
                "dropped_reason": "silence-precheck",
                "stats": {
                    "chunks": 0,
                    "tokens": 0,
                    "engine": self._engine_name,
                    "decode_s": round(time.monotonic() - t0, 3),
                    "min_logprob": None,
                    "mean_logprob": None,
                    "language": None,
                    "language_probability": None,
                    # None, not 0: the energy floor answered first and the
                    # decode-time gate never got a chunk to look at.
                    "gated_chunks": None,
                    "personalization": "L0",
                },
            }
        engine_warm = self._decodes > 0
        self._decodes += 1

        audio_windows = int(np.ceil(len(audio) / (SAMPLE_RATE * 30)))

        # Set by the openai branch when the trie ran; reported in stats so a

        # decode that silently fell back is visible rather than assumed.

        stats_bias_note = None
        # Whether `_vocabulary_prompt` built this decode's prompt from the
        # vocabulary: what `stats["personalization"]` reports as L1-prompt.
        vocabulary_prompted = False
        search_stats = None

        if self._engine_name == "mlx":
            import mlx_whisper
            # Preserve an explicit live-preview prompt. Final holds build
            # their prompt from the saved vocabulary, and can combine that
            # conditioning with the GPU vocabulary beam below.
            vocabulary = None if initial_prompt else _vocabulary_prompt(bias_phrases, language=language)
            vocabulary_prompted = vocabulary is not None
            initial_prompt = initial_prompt or vocabulary
            # The zh-chunk variant of the vocabulary prompt, picked per chunk
            # below (see MLX_ZH_PROMPT_HEAD for the measurement). Built once
            # here, never per chunk; None exactly when the vocabulary built
            # no prompt, so an explicit caller prompt is never displaced.
            zh_prompt = _zh_vocabulary_prompt(bias_phrases) if vocabulary_prompted else None
            search_enabled = vocabulary_search and not return_words
            if search_enabled:
                from . import mlx_beam
                # The prompt's most relevant terms, from the same learned
                # vocabulary. Bound search work even when the store grows.
                search_terms = list(reversed(_prompt_terms(bias_phrases)))[:mlx_beam.MAX_TERMS]
                by_term = {str(bias_phrases[i]).lower(): delta for i, delta in (phrase_deltas or {}).items()
                           if isinstance(i, int) and not isinstance(i, bool) and 0 <= i < len(bias_phrases or [])}
                search_deltas = {i: by_term[term.lower()] for i, term in enumerate(search_terms)
                                 if term.lower() in by_term}
                search_stats = dict(engine="mlx", beam_size=beam_size,
                                    terms=len(search_terms), beam_windows=0,
                                    matched_terms=0, sampling_fallbacks=0, fallback=None)
            if self._mlx_repo_resolved is None:
                self._mlx_repo_resolved = _resolve_mlx_repo(
                    self._mlx_repo,
                    revision=self.hf_revision,
                    allow_download=self.allow_model_download,
                )
            # Chunk at natural pauses, exactly as the trie path does.
            #
            # A whole-utterance decode makes ONE language decision from the
            # first 30 s and applies it to everything after, so speech in the
            # other language is destroyed rather than mis-spelled. Measured on
            # a representative 46.2 s clip (~23 s English, then Chinese)
            # through THIS engine:
            #
            #     whole utterance   145 chars   language=zh      4.7 s
            #     chunked           420 chars   en,en,zh,zh,zh   4.4 s
            #
            # 74% of the utterance gone — and chunking is FREE here, slightly
            # faster, because five short decodes beat one long degenerate one.
            #
            # This matters more than the trie path's version of the same bug:
            # mlx wins the import race, so it is what runs on every machine.
            out = {"segments": [], "language": None}
            languages: list[str] = []
            # The non-speech gate runs PER CHUNK, and that is not an
            # implementation detail — it is the difference between this rule
            # and a rule that eats bilingual dictation. The spans above exist
            # because this user code-switches mid-utterance; a gate that
            # judged the whole utterance from one reading would drop every
            # minority-language span the moment the majority language read
            # confidently, or drop the entire utterance on one noisy pause.
            # Each span already gets its own language decision, so each span
            # gets its own verdict on that decision.
            #
            # Only when the caller pinned nothing. With a pinned language the
            # decoder is not choosing, so "how confident was the choice" has
            # no answer to gate on — the same reason the greedy path
            # passes `language_prob=None` when its language slot is forced.
            probe = _mlx_sot_probe(self._mlx_repo_resolved) if language is None else None
            chunk_language_probs: list[float] = []
            gate_reason = None
            for start, end in (auto_language_decode_spans(audio) or [(0, len(audio))]):
                chunk = audio[start:end]
                reading = probe.read(chunk) if probe is not None else None
                if reading is not None:
                    gated_chunks = gated_chunks or 0
                    chunk_language_probs.append(reading[1])
                chunk_language = reading[0] if reading is not None else language
                # A chunk heard as Chinese decodes under the Chinese carrier
                # prompt (its punctuation style survives — see
                # MLX_ZH_PROMPT_HEAD); a chunk heard as English re-seeds the
                # Latin vocabulary prompt so the zh carrier cannot steer it.
                # Anything else — another language, an unreadable probe, an
                # explicit caller prompt — keeps `initial_prompt`. A
                # caller-pinned language picks one side for every chunk, as
                # it already did for the decode itself.
                chunk_prompt = (
                    zh_prompt
                    if zh_prompt is not None and chunk_language == "zh"
                    else (_vocabulary_prompt(bias_phrases, language="en")
                          if vocabulary_prompted and chunk_language == "en" and language != "en"
                          else initial_prompt))
                decode_options = dict(path_or_hf_repo=self._mlx_repo_resolved,
                    # The language the probe already resolved from this exact
                    # window, so mlx_whisper does not run a SECOND detection
                    # pass over it. Identical by construction (its masked
                    # argmax and this unmasked argmax-over-language-ids pick
                    # the same token), which is what makes the gate free
                    # rather than a doubled encoder pass. `language` — the
                    # caller's pin, or None — whenever no probe ran, so the
                    # ungated path behaves exactly as it did before.
                    language=chunk_language,
                    fp16=True, verbose=None,
                    initial_prompt=chunk_prompt,
                    # Anti-hallucination guard 1 (2026-08-09 field report: a
                    # voice-processor-gated quiet mic produced ONLY "Thank
                    # you." loops and a Vietnamese YouTube-outro phrase).
                    # Conditioning on previous text is what turns ONE
                    # hallucinated segment into a repeating loop — each bad
                    # segment becomes the prompt for the next. Verified
                    # against the reference meeting before shipping: WER
                    # unchanged with this off.
                    condition_on_previous_text=False,
                    word_timestamps=return_words)
                if temperature is not None:
                    decode_options["temperature"] = temperature
                if search_enabled:
                    try:
                        part = mlx_beam.transcribe(
                            chunk, bias_phrases=search_terms, beam_size=beam_size,
                            phrase_deltas=search_deltas, **decode_options)
                        for key in ("beam_windows", "matched_terms", "sampling_fallbacks"):
                            search_stats[key] += part["vocabulary_search"][key]
                    except Exception as exc:
                        # Keep dictation usable on provider/API failures. Do
                        # not retry search for later chunks of this hold, and
                        # never put the exception's text (possibly user data)
                        # in a log. The caller can distinguish a fallback.
                        search_stats["fallback"] = type(exc).__name__
                        search_enabled = False
                        print(f"whisper_mac: vocabulary search fallback ({type(exc).__name__})",
                              file=sys.stderr, flush=True)
                        mlx_cache.clear_cache()
                        part = mlx_whisper.transcribe(chunk, **decode_options)
                else:
                    part = mlx_whisper.transcribe(chunk, **decode_options)
                if reading is not None:
                    # The verdict is taken AFTER the decode even though the
                    # unprompted half of it is knowable before. The greedy
                    # greedy path short-circuits there and saves the decode;
                    # the prompted half cannot, because `confirms()` needs the
                    # avg_logprob the decode produces. One call site for one
                    # rule was worth more here than skipping a decode on the
                    # failure path — the hold produces nothing either way, and
                    # two evaluation points is how a rule ends up applied on
                    # one path and not the other (the defect that put
                    # `SpeechGate` in beam_decode in the first place).
                    #
                    # The chunk's OWN confidence, not the utterance's: the
                    # confirmation half of the reference rule asks whether
                    # THIS decode was confident, and averaging in the
                    # neighbouring spans would let one clean sentence vouch
                    # for the noise beside it.
                    chunk_logprobs = [
                        s.get("avg_logprob") for s in (part.get("segments") or [])
                        if s.get("avg_logprob") is not None]
                    verdict = speech_gate_verdict(
                        language_prob=reading[1],
                        no_speech_prob=reading[2],
                        avg_logprob=(float(sum(chunk_logprobs) / len(chunk_logprobs))
                                     if chunk_logprobs else None),
                        prompt_conditioned=initial_prompt is not None)
                    if verdict is not None:
                        gated_chunks += 1
                        gate_reason = gate_reason or verdict
                        # Both readings, not just the verdict. A rejection at
                        # P(lang)=0.09 and one at 0.004 are a tuning question
                        # and a correct call, and a log line that says only
                        # "dropped" cannot tell them apart after the fact.
                        print(
                            f"whisper_mac: {verdict} "
                            + ("dropped" if SPEECH_GATE_ENFORCE
                               else "WOULD HAVE DROPPED (shadow)")
                            + f" {start / SAMPLE_RATE:.1f}-{end / SAMPLE_RATE:.1f}s "
                            f"— P(lang={reading[0]})={reading[1]:.4f}, "
                            "P(nospeech)="
                            + (f"{reading[2]:.4f}" if reading[2] is not None
                               else "unusable-on-this-checkpoint"),
                            file=sys.stderr, flush=True)
                        # SHADOW is the default — see SPEECH_GATE_ENFORCE.
                        # The operating point above has never seen a real
                        # microphone, and over-dropping deletes words the user
                        # actually said. Recording the verdict is what earns
                        # the right to enforce it later; until then the chunk
                        # falls through and is transcribed exactly as before.
                        if SPEECH_GATE_ENFORCE:
                            # Its segments AND its language never join the
                            # utterance. Letting the language through would
                            # put "ko" in stats["language"] for a hold that
                            # produced no Korean, which is the reported bug
                            # wearing a different hat.
                            continue
                # Segment times are chunk-relative. Offset them back into the
                # utterance's own timeline or every downstream consumer is
                # silently wrong: `drop_no_speech_segments` is unaffected, but
                # the word timestamps below feed the meeting-notes
                # diarization merge, which would align speech to the wrong
                # speaker. Dictation never sets `return_words`, so this is a
                # Notes-side correctness requirement, not a dictation one.
                offset = start / SAMPLE_RATE
                for seg in part.get("segments") or []:
                    seg["start"] = float(seg.get("start", 0.0)) + offset
                    seg["end"] = float(seg.get("end", 0.0)) + offset
                    for w in seg.get("words") or []:
                        if w.get("start") is not None:
                            w["start"] = float(w["start"]) + offset
                        if w.get("end") is not None:
                            w["end"] = float(w["end"]) + offset
                    out["segments"].append(seg)
                if part.get("language"):
                    languages.append(part["language"])
            distinct = list(dict.fromkeys(languages))
            # One language for the whole utterance, or "mul" when the user
            # code-switched — the same contract the trie path reports.
            out["language"] = distinct[0] if len(distinct) == 1 else (
                "mul" if distinct else None)
            # Anti-hallucination guard 2: drop segments the decoder itself
            # marks as probable non-speech. Whisper's own heuristic pair —
            # a segment is junk when no_speech_prob is high AND avg_logprob
            # is low; either alone is not enough (quiet-but-real speech has
            # high no_speech_prob with GOOD logprob, and hard accents have
            # low logprob with LOW no_speech_prob).
            out["segments"] = drop_no_speech_segments(out.get("segments") or [])
            text = " ".join(
                t for t in ((seg.get("text") or "").strip() for seg in out["segments"]) if t
            )
            detected_language = out.get("language")
            if chunk_language_probs:
                language_probability = min(chunk_language_probs)
            # Name the drop, and only when there is nothing left to show for
            # the hold. A bilingual utterance that lost one noisy span still
            # returns text, and calling THAT a dropped utterance would tell
            # the host the opposite of what happened; `gated_chunks` in stats
            # is where the partial case is visible. When nothing survived,
            # `text: ""` with `ok: true` and no explanation is the exact shape
            # of failure this file has already been burned by (see
            # looks_like_junk's docstring), so the reason travels.
            if gate_reason is not None and not text:
                dropped_reason = gate_reason
            # Decoder confidence, already computed per segment and thrown away
            # until now. Iteration 35 measured this as the ONLY usable
            # decode-time signal for "this utterance came out badly":
            # ~1.6-2.2x precision lift over the base rate, ~55% precision at a
            # 20%-flagged operating point. Weak enough that it must never drive
            # a silent auto-correction (wrong nearly as often as right) and
            # strong enough to mark an utterance as low-confidence in the UI.
            #
            # MIN across segments, not mean: errors are concentrated
            # (Iteration 35 measured a Gini of 0.50-0.85, with the worst 10% of
            # utterances carrying 31-69% of all edit burden), so one bad
            # segment inside an otherwise fine utterance is exactly the case
            # worth surfacing, and a mean would average it away.
            seg_logprobs = [s.get("avg_logprob") for s in out.get("segments", [])
                            if s.get("avg_logprob") is not None]
            if seg_logprobs:
                min_logprob = float(min(seg_logprobs))
                mean_logprob = float(sum(seg_logprobs) / len(seg_logprobs))
            if return_words:
                for seg in out.get("segments", []):
                    for w in seg.get("words", []) or []:
                        words.append({
                            "word": (w.get("word") or "").strip(),
                            "start": float(w.get("start", seg.get("start", 0.0))),
                            "end": float(w.get("end", seg.get("end", 0.0))),
                        })
            # Flatten MLX's Metal buffer-cache ratchet after each utterance
            # (see mlx_cache.py). The model itself stays resident by policy —
            # this only returns the decode's intermediate buffers to the OS.
            mlx_cache.clear_cache()
        elif self._engine_name == "faster":
            # The vocabulary prompt, built exactly as the mlx branch builds it
            # (`_vocabulary_prompt`: same 223-token budget, most relevant kept
            # and written last, same script seed). A caller's own prompt (the
            # live caption path) wins, as it always did here. With no
            # vocabulary and no caller prompt this is None, and the call below
            # is the one this engine made before learned words existed.
            #
            # Differences from mlx that are the ENGINE's, stated rather than
            # papered over (faster-whisper 1.2.1, `transcribe.py`):
            # - within one call the prompt conditions only the first 30 s
            #   window. With condition_on_previous_text=False the library
            #   moves `prompt_reset_since` past the prompt after each window.
            #   Auto-language holds are now decoded per span (below) and each
            #   span call is prompted, as mlx prompts every chunk; a pinned
            #   language is still one whole-utterance call;
            # - the seed's safety net is present: no `temperature` is passed,
            #   so the library's default 0.0..1.0 fallback ladder applies (see
            #   MLX_SCRIPT_SEED for why the two must not be separated);
            # - the prompted non-speech rule (`speech_gate_verdict`) is NOT
            #   ported, because it is not engine-independent: it needs the
            #   per-chunk SOT-step readings only `_MlxSotProbe` can take from
            #   mlx internals. Its prompted half -- act on a no-speech reading
            #   only when avg_logprob is also at or below -1.0 -- is what the
            #   library already applies to every window under the prompt
            #   (no_speech_threshold/log_prob_threshold below).
            # Its accuracy effect on this CPU engine is NOT measured
            # (LEARNING.md §6); the prompt measurements are all on mlx.
            vocabulary = None if initial_prompt else _vocabulary_prompt(bias_phrases)
            vocabulary_prompted = vocabulary is not None
            # One language decision per span when the caller pinned none, on
            # the spans the mlx and openai branches decode
            # (`auto_language_decode_spans`). faster-whisper with
            # language=None detects ONCE, from the first window, and decodes
            # the whole call under that answer (its `multilingual` option
            # re-detects only per 30 s window), so a code-switching hold lost
            # or translated the speech in its second language: the failure
            # the mlx branch's chunking measured at 74% of a representative 46 s
            # English-then-Chinese clip. THAT number is mlx's. The gain on
            # this engine is NOT measured on hardware.
            #
            # A span is not always a natural-pause span. It ends at the first
            # pause of 0.6 s or more that leaves 6 s on both sides; speech
            # that runs on with no such pause is cut where the span reaches
            # 24 s (AUTO_LANGUAGE_MAX_CHUNK_SECONDS), or sooner so the rest
            # keeps 6 s, and that cut can fall inside a word. The mlx branch
            # is cut there too. On this engine it is new and also unmeasured:
            # one whole-hold call let faster-whisper restart each 30 s window
            # at the last timestamp it decoded, which rarely splits a word.
            # A hold under 12 s, or of at most 24 s with no qualifying pause,
            # is one span and decodes as before.
            spans = ((auto_language_decode_spans(audio) or [(0, len(audio))])
                     if language is None else [(0, len(audio))])
            span_languages: list[str] = []
            span_probabilities: list[float] = []
            text = ""
            for start, end in spans:
                segments, info = self._fw_model.transcribe(
                    audio[start:end], language=language, beam_size=max(1, beam_size),
                    initial_prompt=initial_prompt or vocabulary,
                    # Same anti-hallucination pair as the mlx path (see the
                    # comment there). faster_whisper applies its own
                    # no_speech/logprob segment filter internally when these
                    # thresholds are set; conditioning off kills the loop
                    # amplifier.
                    condition_on_previous_text=False,
                    no_speech_threshold=0.6, log_prob_threshold=-1.0)
                part = " ".join(s.text.strip() for s in segments).strip()
                if part:
                    text = _join_span_texts(text, part) if text else part
                if getattr(info, "language", None):
                    span_languages.append(info.language)
                probability = getattr(info, "language_probability", None)
                if isinstance(probability, (int, float)):
                    span_probabilities.append(float(probability))
            # One language, or "mul" when the spans disagree: the contract
            # the mlx and trie paths report. The probability is the weakest
            # span's, for the reason `min_logprob` is a min.
            distinct = list(dict.fromkeys(span_languages))
            detected_language = distinct[0] if len(distinct) == 1 else (
                "mul" if distinct else None)
            language_probability = (min(span_probabilities)
                                    if span_probabilities else None)
        else:
            # The openai engine is the only Mac path that can run the trie:
            # mlx has no beam search at all and faster-whisper's lives inside
            # CTranslate2. Slower than both, and the only one where
            # `bias_phrases` means what the research module measured rather
            # than degrading into the `initial_prompt` path this repo closed.
            text = None
            if not bias_phrases and self._engine_name == "openai":
                stats_bias_note = "no-phrases"
            if bias_phrases:
                try:
                    from . import trie_decode
                except ImportError:
                    import trie_decode

                # ALWAYS chunk.  A pinned language stays authoritative
                # because it is forwarded to every chunk below.
                #
                # This used to carve out pinned languages as ONE
                # whole-utterance decode, and that carve-out lost speech.
                # Measured on a representative 47.9 s clip through the shipped
                # trie path:
                #
                #     language=None   416 chars   (complete)
                #     language="en"   224 chars   (opening paragraph gone)
                #     after chunking  567 chars   (nothing lost)
                #
                # WHAT IS ESTABLISHED: a long whole-utterance decode on the
                # TRIE path degenerates on this audio, and bounding each
                # decode to <=24 s at a natural pause prevents it.  That is an
                # outcome measurement, and it is why this line is the way it
                # is.
                #
                # WHAT IS NOT: the mechanism.  A first explanation blamed
                # whisper's 30 s read-head advance discarding an
                # under-decoded window; the arithmetic does not support it
                # (the cited degenerate segment spanned 23.5 s of DECODED
                # audio, so at most 4.7 s could have been forfeited by seek).
                # `condition_on_previous_text` was the other candidate — the
                # mlx and faster branches disable it and call it "the loop
                # amplifier" while this branch never sets it — and it is
                # RULED OUT here: on the same clip, plain whisper returns 620
                # chars with it on and 619 with it off.  Notably that plain
                # whole-utterance decode does not degenerate at all, so the
                # collapse is specific to the trie path, not to whisper's
                # windowing.  Chunking is justified by what it prevents, not
                # by a mechanism anyone has isolated.
                decode_spans = auto_language_decode_spans(audio) or [(0, len(audio))]
                decoded_parts: list[str] = []
                decoded_languages: list[str] = []
                first_coverage_gap = None
                first_failure = None
                n_biased = len(bias_phrases)

                for chunk_index, (start, end) in enumerate(decode_spans, 1):
                    chunk = audio[start:end]
                    chunk_text = None
                    chunk_language = None
                    try:
                        # The repaired variant runs a second, offset decode only
                        # when the chunk is longer than one Whisper window AND a
                        # vocabulary term went missing -- otherwise it returns the
                        # plain result untouched. `BiasTrie` state does not survive
                        # the 30 s encoder boundary, and a term straddling it
                        # recovers 38-52% against 71% inside a window; the offset
                        # pass moves the seam and puts it back to 76-81%.
                        (chunk_text, chunk_language, n_biased,
                         biased_segments) = trie_decode.transcribe_with_trie_repaired(
                            self._torch_model, chunk,
                            bias_phrases=bias_phrases,
                            language=language,
                            beam_size=max(2, beam_size or 4),
                            phrase_deltas=phrase_deltas,
                        )
                        coverage_gap = trie_coverage_gap(chunk, biased_segments)
                        if coverage_gap is not None:
                            if first_coverage_gap is None:
                                first_coverage_gap = coverage_gap
                            print(
                                "whisper_mac: trie decode under-covered, "
                                f"falling back — chunk {chunk_index}, "
                                f"window {coverage_gap['window']}: "
                                f"{coverage_gap['tokens']} tokens / "
                                f"{coverage_gap['voiced_seconds']:.1f} voiced s",
                                file=sys.stderr,
                                flush=True,
                            )
                            chunk_text = None
                    except Exception as exc:  # noqa: BLE001
                        if first_failure is None:
                            first_failure = type(exc).__name__
                        print(
                            "whisper_mac: trie decode failed, falling back — "
                            f"chunk {chunk_index}: {exc}",
                            file=sys.stderr,
                            flush=True,
                        )
                        chunk_text = None

                    if chunk_text is None:
                        plain = self._torch_model.transcribe(
                            chunk, language=language)
                        chunk_text = (plain.get("text") or "").strip()
                        chunk_language = plain.get("language")
                    if chunk_text:
                        decoded_parts.append(chunk_text)
                    if chunk_language:
                        decoded_languages.append(chunk_language)

                text = " ".join(decoded_parts).strip()
                distinct_languages = list(dict.fromkeys(decoded_languages))
                if len(distinct_languages) == 1:
                    detected_language = distinct_languages[0]
                elif len(distinct_languages) > 1:
                    detected_language = "mul"

                if first_coverage_gap is not None:
                    stats_bias_note = (
                        "trie-coverage-fallback:"
                        f"window={first_coverage_gap['window']}")
                elif first_failure is not None:
                    stats_bias_note = f"trie-failed:{first_failure}"
                else:
                    stats_bias_note = (
                        f"trie:{n_biased}:windows={audio_windows}:"
                        f"chunks={len(decode_spans)}")
                    # Success has to be as loud as failure. Two rounds of
                    # field testing were lost reading "no trie lines in the
                    # log" as "it worked", when it meant the branch was never
                    # entered -- first a bad import, then an unset flag.
                    print(
                        f"whisper_mac: trie decode ok — {n_biased} phrases "
                        f"across {audio_windows} windows in "
                        f"{len(decode_spans)} speech chunks "
                        # The engine, on the same line as the evidence it
                        # produced. `trie decode ok` proves a trie-capable
                        # path ran; it does NOT say which engine served it,
                        # and that gap made a whole class of question
                        # unanswerable from the record. Internal research
                        # asked for this field on 2026-08-17 and the same day
                        # an A/B on representative audio could not be
                        # attributed without it: the trie arm and the mlx arm
                        # disagree sharply on vocabulary ("ElmTree" vs "our
                        # tree", a proper name vs a near-homophone, "QNN" vs
                        # "Q&N") and
                        # nothing in the log said which one a given session
                        # had been running. A silent fallback that still
                        # reaches the trie would be invisible without it.
                        # `getattr` with a fallback, not `self.hf_id`. A log
                        # line must never be able to break the thing it is
                        # reporting on, and the direct attribute did exactly
                        # that: a test double that does not run the full
                        # `__init__` raised `AttributeError` from inside the
                        # success branch, turning a diagnostic into a crash on
                        # the decode path.
                        f"[engine={getattr(self, '_engine_name', '?')} "
                        f"model={getattr(self, 'hf_id', '?')}]",
                        file=sys.stderr,
                        flush=True,
                    )
            if text is None:
                # Chunked for the same reason every other decode path is: a
                # whole-utterance call makes ONE language decision from the
                # first 30 s and destroys the speech that disagrees with it.
                # This branch is reached on the openai engine whenever the
                # bias vocabulary is empty, which is every new user.
                parts: list[str] = []
                langs: list[str] = []
                for start, end in (auto_language_decode_spans(audio)
                                   or [(0, len(audio))]):
                    out = self._torch_model.transcribe(
                        audio[start:end], language=language)
                    chunk = (out.get("text") or "").strip()
                    if chunk:
                        parts.append(chunk)
                    if out.get("language"):
                        langs.append(out["language"])
                text = " ".join(parts).strip()
                distinct = list(dict.fromkeys(langs))
                detected_language = distinct[0] if len(distinct) == 1 else (
                    "mul" if distinct else None)

        # Spoken-form aliases, applied before the junk filter so a rewritten
        # term is judged on the text the user will actually see.
        #
        # An explicitly declared alias can map a spoken form to a written
        # term with a silent initial letter, such as synthetic `Nemo` ->
        # `Knemo`. It does not depend on that initial letter winning a trie
        # token. This example does not establish model recovery accuracy.
        #
        # Distinct from phonetic_match, which rewrites anything within a radius
        # and therefore cannot carry a term that sounds like an ordinary word.
        # An alias fires only on the literal string the user declared.
        # `personalize=False` is the true-baseline pass: no aliases, no casing,
        # no trie. It exists because the comparison view was showing a
        # "baseline" that had quietly been through the alias table — the
        # unbiased decode produced `Ask Nemo`, the alias rewrote it to
        # `Ask Knemo`, and the column labelled "without your vocabulary" was
        # displaying a term recovered BY the vocabulary.
        #
        # That is wrong in both directions at once: it understates what the
        # personalization is worth, and it is not the thing the label claims.
        # A comparison whose baseline is secretly helped is not a comparison.
        #
        # `alias_stat` names the stats flag that tells asr_server this pass
        # ran, so the server does not run the table over its output a second
        # time: a second pass is not a no-op when one alias's spoken form sits
        # inside another's written form (`spoken_aliases`, "Matching").
        alias_stat = None
        if text and personalize:
            try:
                try:
                    from . import spoken_aliases
                except ImportError:
                    import spoken_aliases
                aliases = spoken_aliases.load()
                if aliases:
                    text, applied = spoken_aliases.apply(text, aliases)
                    if applied:
                        # A count, never the words: they are the user's
                        # vocabulary, and the hosts log this stream.
                        print(f"whisper_mac: aliases applied — {len(applied)} rewrite(s)",
                              file=sys.stderr, flush=True)
                alias_stat = spoken_aliases.APPLIED_STAT
            except Exception as exc:  # noqa: BLE001
                # An alias failure must never cost the transcript.
                print(f"whisper_mac: alias pass skipped — {type(exc).__name__}",
                      file=sys.stderr, flush=True)

        # Prompt regurgitation guard — BEFORE any cosmetic stage, because a
        # regurgitated list must not be case-corrected into something that
        # looks deliberate.
        #
        # `initial_prompt` is text the model believes preceded the audio, so
        # it can decide the audio CONTINUES it and emit the vocabulary list
        # instead of the speech:
        #
        # A synthetic example is a spoken sentence followed by a comma-run
        # of `Trovioo, Knemo, PadForge, ElmTree`. The guard recognizes repeated
        # prompt terms; test fixtures exercise the shape, not its field rate.
        if text and bias_phrases:
            cleaned, dropped = _strip_prompt_regurgitation(text, bias_phrases)
            if dropped:
                print(f"whisper_mac: dropped {dropped} regurgitated vocabulary "
                      f"term(s) echoed from the prompt", file=sys.stderr)
                text = cleaned

        # Declared casing, AFTER aliases — an alias already emits the declared
        # form, so this stage never sees one and cannot fight it.
        #
        # Closes a defect neither side's metrics can see. Internal research
        # finding 39: through this very function, case-folded recovery is
        # 92.06% but the exact display form is 82.54%. `CedarStack` comes
        # back as `cedarstack` and scores as a HIT — the decoder did find
        # the word — while still being a hand-fix for the user. Finding 38 adds
        # a second source with the same signature: `ct-punc` sentence-cases and
        # destroys camelCase (`ElmTree` -> `Elmtree`) in 10 of 20 terms.
        #
        # Priced like the alias table: 31 of 33 declared terms never occur in
        # 1.2M words of ordinary English. `whisper` occurs 6 times and is
        # excluded by default.
        if text and bias_phrases and personalize:
            try:
                try:
                    from . import declared_casing
                except ImportError:
                    import declared_casing
                text, restored = declared_casing.apply(text, bias_phrases,
                                                       case_only=case_only_phrases or ())
                if restored:
                    print(f"whisper_mac: casing restored — {len(restored)} word(s)",
                          file=sys.stderr, flush=True)
            except Exception as exc:  # noqa: BLE001
                print(f"whisper_mac: casing pass skipped — {type(exc).__name__}",
                      file=sys.stderr, flush=True)

        # Long-form (meeting) decodes must not be judged by the
        # compression-ratio rule — see looks_like_junk's docstring for the
        # measured reason and the failure it caused. The stock-phrase rule
        # follows the caller's hallucination_guard, as the blocklist below
        # does: a dictated "See you next week, Casey." is a message.
        long_form = len(audio) > LONG_FORM_AUDIO_S * SAMPLE_RATE
        if text and looks_like_junk(text, long_form=long_form,
                                    hallucination_guard=hallucination_guard):
            text = ""
            words = []
            dropped_reason = "junk-filter"
        # Hallucination-phrase backstop. `drop_no_speech_segments` cannot
        # catch this (it needs high no_speech AND low logprob; a confident
        # "Thank you." decode trips neither) and `looks_like_junk` has no rule
        # that fires on two words. Guarded twice over so a real polite
        # utterance survives — see blocked_hallucination.
        elif text and blocked_hallucination(text, hallucination_guard=hallucination_guard):
            dropped_reason = f"hallucination-phrase:{text.strip()!r}"
            text = ""
            words = []

        stats = {
            "engine_warm": engine_warm,
            "chunks": audio_windows,
            "tokens": len(text.split()),  # word-count proxy; QNN reports real tokens
            "engine": self._engine_name,
            "bias": stats_bias_note,
            "decode_s": round(time.monotonic() - t0, 3),
            # None on the faster/openai fallbacks, which do not expose it —
            # a consumer must treat absence as "no confidence signal", never
            # as "confident".
            "min_logprob": min_logprob,
            "mean_logprob": mean_logprob,
            # Metadata only (a language code, never content) — see the
            # declarations above for the per-engine sourcing, and for why
            # `language_probability` is a per-chunk MINIMUM on mlx and a
            # whole-clip figure on faster_whisper.
            "language": detected_language,
            "language_probability": language_probability,
            # How many decode chunks the non-speech gate dropped, or None
            # when no gate ran on this call — see the declaration above for
            # why those two are not the same answer.
            "gated_chunks": gated_chunks,
            # Echoed straight back (None when the caller measured nothing) so
            # a surprising transcript can be explained after the fact without
            # re-deriving it from audio nobody kept.
            "voiced_fraction": voiced_fraction,
            "personalization": (
                (f"L1-prompt: initial_prompt seeded ({self._engine_name} engine)"
                 if vocabulary_prompted else
                 "L0-only: L1/L2 deferred on this backend (needs stepwise "
                 "decoder access)")
                if (bias_phrases or p_you is not None or beam_size > 1)
                else "L0"),
        }
        if alias_stat:
            stats[alias_stat] = True
        if search_stats is not None:
            stats["vocabulary_search"] = search_stats
            stats["personalization"] = (
                "L1-hybrid: prompt + MLX vocabulary beam"
                if search_stats["beam_windows"] else "L1-prompt: MLX search unavailable")
            print("whisper_mac: vocabulary search "
                  f"beam={search_stats['beam_size']} terms={search_stats['terms']} "
                  f"windows={search_stats['beam_windows']} matches={search_stats['matched_terms']} "
                  f"sampling={search_stats['sampling_fallbacks']} fallback={search_stats['fallback'] or 'none'} "
                  f"decode_s={stats['decode_s']}", file=sys.stderr, flush=True)
        result = {"text": text, "stats": stats, "dropped_reason": dropped_reason}
        if return_words:
            result["words"] = words
        return result


class StubEngine:
    """Modelless engine for plumbing tests: proves framing, dispatch, PCM
    decode and stats plumbing with zero downloads. Echoes a deterministic
    sentence for any audio that clears a trivial RMS gate."""

    def __init__(self, **_ignored):
        self.p_ilm = None
        self.encoder = _ProvidersShim(["StubEngine"])
        self._timed_calls = 0  # transcribe_timed call counter (determinism)

    @property
    def on_npu(self) -> bool:
        return False

    @property
    def tokenizer(self):
        raise RuntimeError("StubEngine has no tokenizer (don't pass db_path)")

    def transcribe(self, audio: np.ndarray, bias_phrases=None, beam_size: int = 1,
                   p_you=None, lam: float = 0.1, mu: float = 0.1,
                   return_words: bool = False, language: str | None = None,
                   initial_prompt: str | None = None) -> dict:
        # `language=` / `initial_prompt=` are accepted (and echoed in stats)
        # because a live caption path
        # passes them on every decode — the stub predated rolling-stable and
        # rejecting them broke stream_finish modelless (agent H, 2026-08-24).
        audio = np.asarray(audio, dtype=np.float32)
        rms = float(np.sqrt(np.mean(audio ** 2))) if len(audio) else 0.0
        text = "stub transcript" if rms > 1e-4 else ""
        result = {"text": text,
                  "stats": {"chunks": 1 if len(audio) else 0,
                            "tokens": len(text.split()),
                            "engine": "stub", "rms": round(rms, 6)}}
        if language is not None:
            # echo a caller-pinned language, same as WhisperMac; absence
            # stays a valid state ("unknown") when nothing was pinned — see
            # test_stub_stats_carry_no_language_key.
            result["stats"]["language"] = language
        if return_words:
            # Evenly-spaced fake word timestamps — enough for plumbing tests
            # (attribute.py merge, ops_diarize.py dispatch) with no model.
            dur = len(audio) / SAMPLE_RATE if len(audio) else 0.0
            toks = text.split()
            step = dur / len(toks) if toks else 0.0
            result["words"] = [
                {"word": w, "start": round(i * step, 3), "end": round((i + 1) * step, 3)}
                for i, w in enumerate(toks)
            ]
        return result

    def transcribe_timed(self, audio: np.ndarray,
                         initial_prompt: str | None = None,
                         language: str | None = None,
                         speech: list | None = None,
                         condition_on_prev: bool | None = None,
                         beam_size: int = 1,
                         bias_phrases: list | None = None) -> dict:
        """Deterministic fake timed spans, mirroring WhisperQnn.transcribe_timed's
        return shape ({"spans": [(start_s, end_s, text, lang)], "stats"}), so a
        full stream_start(rolling_stable=true) -> push -> finish round-trip —
        and the notes stream e2e harness — completes with zero models. One
        span per call over the voiced clip; the running counter makes each
        window's text distinct (the rolling committer dedups identical
        adjacent texts, which would otherwise hide hops)."""
        audio = np.asarray(audio, dtype=np.float32)
        dur = len(audio) / SAMPLE_RATE if len(audio) else 0.0
        rms = float(np.sqrt(np.mean(audio ** 2))) if len(audio) else 0.0
        spans: list[tuple] = []
        if dur > 0 and rms > 1e-4:
            self._timed_calls += 1
            spans.append((0.0, round(dur, 3),
                          f"stub stable sentence {self._timed_calls}",
                          language or "en"))
        return {"spans": spans,
                "stats": {"engine": "stub", "chunks": len(spans),
                          "path": "timed", "rms": round(rms, 6)}}


def _selftest() -> int:
    """Model-free checks of the repo/weight-filename shim and of the
    non-speech gate's decision rule.

    No downloads, no audio, no mlx import. The shim half is pure filesystem
    logic, which is exactly where the `[load_npz]` failure lived. The gate
    half is pure arithmetic over the X Elite reading table plus a source-level
    drift check against the platform that measured it — the MEASUREMENT lives
    on Windows, so the only thing this side can honestly assert is that it
    still copies the same numbers and still decides the same way. Run with
    `python -m transcription_worker.whisper_mac --selftest`.
    """
    import ast
    import tempfile

    failures = []

    def check(name: str, cond: bool, detail: str = "") -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}{(' — ' + detail) if detail else ''}")
        if not cond:
            failures.append(name)

    check("classic size gets the -mlx suffix",
          _mlx_repo_for("openai/whisper-small") == "mlx-community/whisper-small-mlx")
    check("turbo gets no suffix",
          _mlx_repo_for("openai/whisper-large-v3-turbo")
          == "mlx-community/whisper-large-v3-turbo")
    check("an mlx-community id passes through untouched",
          _mlx_repo_for("mlx-community/whisper-large-v3-turbo-4bit")
          == "mlx-community/whisper-large-v3-turbo-4bit")

    root = Path(tempfile.mkdtemp())

    bare = root / "no-weights"
    bare.mkdir()
    (bare / "config.json").write_text("{}")
    try:
        _mlx_loadable_dir(bare, "selftest-bare")
        check("missing weights raise a NAMED error", False, "no exception")
    except FileNotFoundError as e:
        check("missing weights raise a NAMED error", "config.json" in str(e))

    native = root / "native"
    native.mkdir()
    (native / "config.json").write_text("{}")
    (native / "weights.npz").write_text("")
    check("expected filename is returned untouched (no shim)",
          _mlx_loadable_dir(native, "selftest-native") == native)

    alt = root / "alt"
    alt.mkdir()
    (alt / "config.json").write_text("{}")
    (alt / "model.safetensors").write_text("")
    shim = _mlx_loadable_dir(alt, "selftest-alt")
    check("alternative filename is shimmed", shim != alt)
    check("shim links weights.safetensors -> model.safetensors",
          (shim / "weights.safetensors").is_symlink()
          and (shim / "weights.safetensors").resolve() == (alt / "model.safetensors").resolve())
    check("shim links config.json", (shim / "config.json").is_symlink())
    check("shim is idempotent", _mlx_loadable_dir(alt, "selftest-alt") == shim)
    check("a local dir resolves without any network",
          _resolve_mlx_repo(str(native)) == str(native))

    # --- the non-speech gate ------------------------------------------------
    #
    # Three separate claims, and none substitutes for the others: the bars sit
    # where the macOS measurement put them, the checkpoint-independent half is
    # still shared with Windows, and the rule still DECIDES the way the
    # measurement says it should. A correctly-copied constant feeding an
    # inverted comparison passes the first two and fails the third.

    # This adapter serves both native hosts. Pin the reference log-probability
    # gate directly; there is no second backend implementation to compare.
    check("AVG_LOGPROB_THRESHOLD keeps the reference value",
          AVG_LOGPROB_THRESHOLD == -1.0,
          f"{AVG_LOGPROB_THRESHOLD} vs -1.0")

    # The corroboration bar lives in beam_decode, which IS importable here
    # (numpy plus whisper_profile, no onnxruntime), so it is read from the
    # real object rather than from text.
    from . import beam_decode

    shared_gate = beam_decode.SpeechGate(
        no_speech_id=None, no_speech_threshold=None,
        language_prob_threshold=LANGUAGE_PROB_THRESHOLD,
        avg_logprob_threshold=AVG_LOGPROB_THRESHOLD,
        prompt_conditioned=False)
    check("LANGUAGE_CORROBORATION_THRESHOLD matches beam_decode's",
          shared_gate.language_corroboration_threshold
          == LANGUAGE_CORROBORATION_THRESHOLD,
          f"beam_decode={shared_gate.language_corroboration_threshold} "
          f"mac={LANGUAGE_CORROBORATION_THRESHOLD}")

    # The bar, held to the macOS measurement it was chosen from rather than to
    # another file. Agreement between two files only proves they agree; these
    # two are the extremes of the table in LANGUAGE_PROB_THRESHOLD's comment,
    # measured on mlx-community/whisper-large-v3-turbo on 2026-08-24, and they
    # are what makes the number defensible rather than merely consistent.
    check("the language bar stays under the lowest measured real-speech reading",
          LANGUAGE_PROB_THRESHOLD < 0.4565,
          f"{LANGUAGE_PROB_THRESHOLD} vs 0.4565 (first 0.8s of a TTS sentence)")
    check("the language bar stays over the highest realistic non-speech reading",
          LANGUAGE_PROB_THRESHOLD > 0.1965,
          f"{LANGUAGE_PROB_THRESHOLD} vs 0.1965 (keyboard click train)")
    # Deliberately NOT above the 440 Hz pure tone at 0.3855: reaching for it
    # would put the bar inside real speech's plausible range, and that clip
    # emits " ." which `looks_like_junk` already deletes. If someone raises
    # the bar past the tone, this line is what says the trade was made.
    check("the language bar does not reach up for the pure-tone reading",
          LANGUAGE_PROB_THRESHOLD < 0.3855,
          f"{LANGUAGE_PROB_THRESHOLD} vs 0.3855 (440 Hz sine) — catching that "
          "clip costs more real speech than it saves")

    def verdict(lang_p, no_speech_p, logprob=None, prompted=False):
        return speech_gate_verdict(language_prob=lang_p,
                                   no_speech_prob=no_speech_p,
                                   avg_logprob=logprob,
                                   prompt_conditioned=prompted)

    # The measured rows, on the checkpoint that actually ships. `no_speech_p`
    # is None throughout because large-v3-turbo's head failed the capability
    # probe (NO_SPEECH_HEAD_MIN_ON_SILENCE) — that IS the shipped
    # configuration, so it is the one the table is written in.
    check("room tone that decoded to \"Thank you.\" is dropped",
          verdict(0.1708, None) == "language-prob-gate")
    check("hiss that decoded to \"Thank you.\" is dropped",
          verdict(0.1754, None) == "language-prob-gate")
    check("a keyboard that decoded to a YouTube outro is dropped",
          verdict(0.1965, None) == "language-prob-gate")

    # The rows that must SURVIVE. Over-dropping is the hazard this gate
    # carries, and TTS speech is the cleanest speech there is — if these ever
    # go red the bar has moved into the speech distribution.
    check("a TTS English sentence survives", verdict(0.4804, None) is None)
    check("a TTS Chinese sentence survives", verdict(0.4917, None) is None)
    check("the same sentences at 0.05 peak survive",
          verdict(0.4765, None) is None and verdict(0.5230, None) is None)
    check("the first 0.8s of a sentence survives", verdict(0.4565, None) is None)
    check("speech buried in hiss survives", verdict(0.4879, None) is None)

    # A checkpoint WITH a working no-speech head keeps the corroboration rule,
    # and this is the row it exists for: a code-switched or heavily accented
    # chunk splits its mass across two language tokens, so P(language) reads
    # low while <|nospeech|> reads near zero — the model actively asserting
    # this IS speech. Dropping it deletes exactly the minority-language span
    # `auto_language_decode_spans` exists to preserve.
    check("split language mass with a LIVE no-speech head survives",
          verdict(0.05, 0.02) is None)
    check("the same reading with a DEAD head is not vetoed",
          verdict(0.05, None) == "language-prob-gate")
    check("a live head that agrees still drops", verdict(0.05, 0.93) == "language-prob-gate")

    # Under a <|startofprev|> prompt neither reading is evidence on its own
    # (the FLEURS interaction), so the reference rule's second half is
    # required — and a confident decode under a mismatched prompt is the case
    # that must not die.
    check("prompted: confident decode survives the same reading",
          verdict(0.03, None, logprob=-0.30, prompted=True) is None)
    check("prompted: an unconfident decode is dropped, and says so",
          verdict(0.03, None, logprob=-1.40, prompted=True)
          == "language-prob-gate+avg-logprob")
    check("prompted: no logprob at all is not a confirmation",
          verdict(0.03, None, logprob=None, prompted=True) is None)

    # An absent LANGUAGE reading is "unknown", never "suspicious" — a failed
    # probe must not become a drop.
    check("a missing language probability keeps the chunk",
          verdict(None, 0.99) is None)

    # The capability probe's decision, against both checkpoints anyone has
    # measured. Without these the bar could be set to 0.0 — trusting a head
    # that answers 1e-10 to literal silence — and nothing would notice until
    # the corroboration rule started vetoing every drop in the field.
    check("large-v3-turbo's dead head is rejected",
          no_speech_head_usable(1.53e-10) is False,
          "measured on this machine 2026-08-24")
    check("whisper-base's live head is accepted",
          no_speech_head_usable(0.932) is True,
          "measured on an X Elite laptop 2026-08-19")
    check("an unreadable probe is not a usable head",
          no_speech_head_usable(None) is False)

    # The boundaries, in both directions, so an edit from > to >= is visible.
    check("exactly AT the language bar survives",
          verdict(LANGUAGE_PROB_THRESHOLD, None) is None)
    check("exactly AT the corroboration bar drops",
          verdict(0.01, LANGUAGE_CORROBORATION_THRESHOLD) == "language-prob-gate")
    check("just under the corroboration bar survives",
          verdict(0.01, LANGUAGE_CORROBORATION_THRESHOLD - 1e-6) is None)

    # The rule RUNS by default and DROPS nothing by default. Both halves are
    # asserted, because either one alone is a different product: enforcing on
    # an operating point measured only on `say` output would delete real
    # words, and not running it at all would collect no evidence to set the
    # bar with. Flipping this default is a decision that needs readings from a
    # real microphone attached to it — see SPEECH_GATE_ENFORCE.
    # Asked of the DEFAULT, not of `SPEECH_GATE_ENFORCE`, so the assertion
    # means the same thing on a machine that happens to export the override.
    # Reading the ambient value here would make this check pass trivially in
    # exactly the environment where somebody had turned enforcement on.
    check("the speech gate ships in SHADOW mode",
          _speech_gate_enforce_from(None) is False,
          "with no env var set the gate must record and not drop")
    check("enforce is reachable, and only by that exact word",
          _speech_gate_enforce_from("enforce") is True
          and _speech_gate_enforce_from("shadow") is False
          and _speech_gate_enforce_from("1") is False
          and _speech_gate_enforce_from("") is False)

    print(f"whisper_mac selftest: {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    import sys

    if "--selftest" in sys.argv:
        raise SystemExit(_selftest())
    raise SystemExit("usage: python -m transcription_worker.whisper_mac --selftest")
