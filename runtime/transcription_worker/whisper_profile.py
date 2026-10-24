"""Which Whisper is this? — derived from the loaded model, never from constants.

Every special-token id in a Whisper checkpoint moves between generations. base
and large-v3-turbo do not agree about a single one of them except
``<|endoftext|>`` (50257) and ``<|startoftranscript|>`` (50258), which are
pinned by the vocabulary layout itself. Everything after SOT shifts, because
large-v3 added a hundredth language (``<|yue|>``) and pushed the task and
control tokens up by one:

    token                     whisper-base    large-v3 / large-v3-turbo
    <|endoftext|>                 50257                     50257
    <|startoftranscript|>         50258                     50258
    <|en|>                        50259                     50259
    <|translate|>                 50358                     50359
    <|transcribe|>                50359                     50360
    <|startofprev|>               50361                     50362
    <|nospeech|>       <|nocaptions|> 50362                 50363
    <|notimestamps|>              50363                     50364
    <|0.00|>                      50364                     50365
    vocabulary                    51865                     51866
    mel bins                         80                       128

Read the middle column against the right one and the shape of the defect this
module exists to prevent is visible in one glance: base's ``<|nocaptions|>``
(50362) is turbo's ``<|startofprev|>``, and base's ``<|notimestamps|>`` (50363)
is turbo's no-speech token. Every one of those ids is IN RANGE of the other
model's softmax, so reading the wrong one raises nothing, logs nothing, and
tests green. The no-speech gate — the thing that stops a silent hold from
typing Korean — simply reads P(``<|startofprev|>``) ≈ 0 forever and never
fires again.

So this module does two things and nothing else:

* ``WhisperProfile`` resolves every id BY NAME through the loaded tokenizer,
  rejects an answer equal to ``unk_token_id`` (asking base's tokenizer for
  ``<|nospeech|>`` does not fail — it hands back 50257, which is unk AND EOT,
  so a gate built on it would read P(EOT) at the SOT step and fire on ordinary
  speech), and RAISES when a token the decode path needs is absent. A wrong id
  is unreachable; an absent one is loud at load, named.

* ``WhisperGraph`` reads the mel-bin count, the feature dtype, the decode
  context length, the encoder's input name and the logits width off the ONNX
  sessions themselves, and raises when they disagree with the tokenizer. These
  were five hardcoded constants written against one AI Hub export.

The right column above was measured directly from the cached
``openai/whisper-large-v3-turbo`` tokenizer (``added_tokens.json``); the left
from the shipped whisper-base tokenizer on an X Elite laptop, 2026-08-19. Nothing here
was measured on a device — this module derives, it does not assume.
"""

from __future__ import annotations

import sys
from dataclasses import dataclass

import numpy as np


class WhisperModelMismatch(RuntimeError):
    """The loaded tokenizer, feature extractor and graphs are not one model.

    Raised at LOAD, with the model named and both sides of the mismatch
    printed. The alternative — the one this replaces — is a decode that
    silently reads the wrong slot of the right-sized softmax.
    """


# Whisper spells its "this audio is not speech" slot two ways. base's tokenizer
# knows only `<|nocaptions|>`; large-v3 and turbo know only `<|nospeech|>`.
# Tried in this order and never by id.
NO_SPEECH_TOKEN_NAMES = ("<|nospeech|>", "<|nocaptions|>")

# The fingerprint of the checkpoint every calibration constant in this package
# was measured against: (no-speech id, vocabulary width, mel bins) for
# whisper-base. NOT a gate — a marker. `warn_if_uncalibrated()` prints one line
# to stderr when the loaded model is a different one, because SpeechGate's
# 0.75 / 0.10 / 0.90, BiasTrie's delta=1.0 and looks_like_junk's 2.4 all come
# from a single whisper-base-on-X-Elite table (2026-08-19) whose own docstring
# says an operating point does not transfer. Those numbers will not fail on
# another model; they will be mis-set in an unknown direction, silently, on the
# gate that exists to stop foreign-language hallucination. One stderr line is
# the cheapest thing that makes "we swapped the model and never re-measured"
# visible in the app's log.
CALIBRATED_ON = "openai/whisper-base"
CALIBRATED_FINGERPRINT = (50362, 51865, 80)

# ── The phrase-bias operating point, per checkpoint ──────────────────────────
#
# `BiasTrie`'s delta is the boost added to every token that could start or
# extend a personal-vocabulary match. It is NOT a property of the bias
# mechanism — it is a property of the CHECKPOINT the mechanism runs on. A
# larger model needs less persuasion before its own acoustic evidence stops
# being overridden, so one value is too weak on one model and dangerous on
# the other.
#
# This table exists because that fact was true and unrepresented. The QNN
# engine hardcoded 1.0 under a comment reading "this path loads
# `openai/whisper-base`", which was true of the DEFAULT argument and false of
# a shipped call that resolved `hf_id` to `<model_dir>/tokenizer` and offered
# whisper-large-v3-turbo-qnn FIRST. A user who installed Turbo therefore ran
# an 809M checkpoint at the 39M checkpoint's operating point — silently, and invisibly to every suite: every Turbo
# measurement was taken on the macOS decoder, and every delta measurement on
# base.
#
# Measured on `turbo`, 360 clips, 21-term arm, beam 4 — the table
# `trie_decode.py` reads its own value from:
#
#     delta   recovery   U-WER    (anchor U-WER 5.49%)
#       0       20.6%    5.49%
#       1       66.7%    5.07%    <- what the QNN path was running on Turbo
#       2       90.5%    5.04%
#       2.5     92.1%    5.09%
#       3       92.1%    9.69%    <- ordinary words break here
#
# THE TURBO ROW IS A CROSS-DECODER TRANSFER AND IS LABELLED ONE. 2.5 was
# measured on `trie_decode.BiasBeamSearchDecoder` (openai-whisper), not on
# `beam_decode.beam_search` (the hand-written ONNX loop this package runs on
# QNN). Internal research finding 30 flags that comparison — `A9-qnn` — as
# never run; it needs an X Elite. So this is not "the measured QNN value for
# Turbo". It is the value measured on THIS CHECKPOINT instead of on a
# different one, which is the better of the two available errors: Finding 20's
# rule is that an operating point does not transfer across configurations, and
# base -> turbo changes the checkpoint AND the decoder, while 2.5 changes only
# the decoder.
BASE_FINGERPRINT = CALIBRATED_FINGERPRINT

# From the cached `openai/whisper-large-v3-turbo` `added_tokens.json` and the
# staged 128-mel preprocessor — the same two sources as the table at the top
# of this module. Not invented here, and not carried over from any constant.
TURBO_FINGERPRINT = (50363, 51866, 128)

BIAS_DELTA_BY_FINGERPRINT: dict[tuple[int, int, int | None], float] = {
    BASE_FINGERPRINT: 1.0,
    TURBO_FINGERPRINT: 2.5,
}

# ORT type strings -> numpy dtype for the encoder's mel input. The old code
# hardcast features to float16 to pair with `enable_htp_fp16_precision=1`;
# AI Hub publishes fp32 encoders too, and feeding one float16 is an ORT type
# error at the first utterance.
_ORT_DTYPES = {
    "tensor(float16)": np.float16,
    "tensor(float)": np.float32,
    "tensor(double)": np.float64,
}

# Fallback decode context, used ONLY when the decoder graph declares a symbolic
# attention-mask width. This is the AI Hub `MEAN_DECODE_LEN` the Whisper
# exports have shipped at; it is a property of how the graph was COMPILED, so
# where the graph states it, the graph wins.
DEFAULT_DECODE_CONTEXT = 200


def _dim(shape, index: int) -> int | None:
    """A concrete integer dimension, or None for a symbolic/unknown one."""
    try:
        value = shape[index]
    except (TypeError, IndexError):
        return None
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return value if value > 0 else None


def resolve_token(tokenizer, name: str) -> int | None:
    """Id of a special token by NAME, or None when this tokenizer lacks it.

    The ``unk`` rejection is load-bearing, not defensive decoration: on
    whisper-base ``convert_tokens_to_ids("<|nospeech|>")`` returns 50257, which
    is ``unk_token_id`` AND ``<|endoftext|>`` (measured on an X Elite laptop, 2026-08-19).
    Without this check the caller gets EOT and believes it.
    """
    if tokenizer is None:
        return None
    tid = tokenizer.convert_tokens_to_ids(name)
    if tid is None:
        return None
    unk = getattr(tokenizer, "unk_token_id", None)
    if unk is not None and tid == unk:
        return None
    return int(tid)


def no_speech_token_id(tokenizer) -> int | None:
    """Id of ``<|nospeech|>``/``<|nocaptions|>``, or None if this tokenizer has
    neither. See ``NO_SPEECH_TOKEN_NAMES``."""
    for name in NO_SPEECH_TOKEN_NAMES:
        tid = resolve_token(tokenizer, name)
        if tid is not None:
            return tid
    return None


def _require(tokenizer, model: str, purpose: str, *names: str) -> int:
    for name in names:
        tid = resolve_token(tokenizer, name)
        if tid is not None:
            return tid
    spelled = " / ".join(names)
    raise WhisperModelMismatch(
        f"{model}: tokenizer knows none of {spelled}, which the decode path "
        f"needs for {purpose}. This is not a model this worker can drive — "
        f"refusing to fall back to another checkpoint's ids."
    )


def _end_of_text(tokenizer, model: str) -> int:
    """EOT, resolved WITHOUT the unk rejection.

    On whisper-base ``unk_token`` IS ``<|endoftext|>``, so the guard that keeps
    every other lookup honest would reject the correct answer here. Prefer the
    tokenizer's own ``eos_token_id``, which is unambiguous.
    """
    eos = getattr(tokenizer, "eos_token_id", None)
    if isinstance(eos, int):
        return int(eos)
    tid = tokenizer.convert_tokens_to_ids("<|endoftext|>")
    if tid is None:
        raise WhisperModelMismatch(
            f"{model}: tokenizer has neither eos_token_id nor <|endoftext|>."
        )
    return int(tid)


def _vocab_size(tokenizer, floor: int) -> int:
    """Total vocabulary width including added specials.

    ``len(tokenizer)`` is the right answer on a real ``WhisperTokenizer``
    (51865 on base, 51866 on turbo). The fake tokenizers the decoder unit
    tests build implement neither ``__len__`` nor ``get_added_vocab``, so the
    floor — one past the highest special this profile resolved — stands in.
    """
    for probe in (
        lambda: len(tokenizer),
        lambda: tokenizer.vocab_size + len(tokenizer.get_added_vocab()),
    ):
        try:
            value = int(probe())
        except Exception:  # noqa: BLE001 - any tokenizer that cannot answer
            continue
        if value >= floor:
            return value
    return floor


@dataclass(frozen=True)
class WhisperProfile:
    """One model's identity, derived once, at load.

    Every field is read from the loaded tokenizer/feature extractor. Nothing
    here is a constant, and nothing here has a default that could be inherited
    by a caller that never chose it — that inheritance is how this package
    shipped delta=4.0 twice (see ``BiasTrie.__init__``).
    """

    model: str
    sot: int
    eot: int
    no_speech: int
    transcribe: int
    translate: int
    notimestamps: int
    vocab_size: int
    startofprev: int | None = None
    timestamp_begin: int | None = None
    n_mels: int | None = None

    @property
    def language_range(self) -> tuple[int, int]:
        """Inclusive ``[<|en|>, last language]`` id range.

        Whisper lays the language tokens out contiguously between SOT and the
        first task token, so this is derived rather than tabulated — it is what
        made the range correct on turbo's hundred languages without anybody
        counting them.
        """
        return self.sot + 1, self.translate - 1

    @property
    def fingerprint(self) -> tuple[int, int, int | None]:
        """``(no_speech, vocab_size, n_mels)`` — enough to tell base from
        turbo, and what ``warn_if_uncalibrated`` compares."""
        return (self.no_speech, self.vocab_size, self.n_mels)

    @property
    def forced_prefix(self) -> list[int]:
        """``[sot, <language slot>, transcribe, notimestamps]``.

        The language slot is ``-1``: it is chosen by the MODEL at decode time
        (argmax over ``language_range``) or pinned by the caller, never baked
        in. The constant this replaces was ``[50258, 50259, 50359, 50363]`` —
        base's ``[sot, en, transcribe, notimestamps]``, which on turbo reads
        ``[sot, en, TRANSLATE, NOSPEECH]``, i.e. English-translate mode with a
        no-speech token in the prompt. That shipped, and measured en CER 17.12
        / zh CER 73.76 on 24 FLEURS clips.
        """
        return [self.sot, -1, self.transcribe, self.notimestamps]

    @classmethod
    def from_tokenizer(
        cls, tokenizer, *, model: str, feature_extractor=None
    ) -> "WhisperProfile":
        """Derive the profile, or raise ``WhisperModelMismatch`` naming what is
        missing. ``feature_extractor`` supplies the mel-bin count when given."""
        if tokenizer is None:
            raise WhisperModelMismatch(
                f"{model}: no tokenizer, so no model identity. Special-token "
                f"ids differ between every Whisper generation and cannot be "
                f"guessed."
            )
        sot = _require(tokenizer, model, "the decode prefix", "<|startoftranscript|>")
        eot = _end_of_text(tokenizer, model)
        # The one the whole gate hangs on. Absent => raise, because a gate
        # built on a missing token is a gate that never fires, which is
        # indistinguishable from no gate at all until a user's silence types
        # Korean.
        no_speech = _require(
            tokenizer, model, "the non-speech gate", *NO_SPEECH_TOKEN_NAMES
        )
        transcribe = _require(tokenizer, model, "the task slot", "<|transcribe|>")
        translate = _require(
            tokenizer, model, "the language-token range", "<|translate|>"
        )
        notimestamps = _require(
            tokenizer, model, "the decode prefix", "<|notimestamps|>"
        )
        startofprev = resolve_token(tokenizer, "<|startofprev|>")
        timestamp_begin = resolve_token(tokenizer, "<|0.00|>")
        specials = [
            sot, eot, no_speech, transcribe, translate, notimestamps,
            startofprev or 0, timestamp_begin or 0,
        ]
        n_mels = None
        if feature_extractor is not None:
            size = getattr(feature_extractor, "feature_size", None)
            if isinstance(size, int) and size > 0:
                n_mels = size
        profile = cls(
            model=model,
            sot=sot,
            eot=eot,
            no_speech=no_speech,
            transcribe=transcribe,
            translate=translate,
            notimestamps=notimestamps,
            vocab_size=_vocab_size(tokenizer, max(specials) + 1),
            startofprev=startofprev,
            timestamp_begin=timestamp_begin,
            n_mels=n_mels,
        )
        profile.check_layout()
        return profile

    def check_layout(self) -> None:
        """Refuse a layout the decode path cannot express.

        These are not paranoia. ``language_range`` is ``(sot + 1, translate -
        1)`` and ``prepare()`` suppresses everything at or above SOT; a
        checkpoint that ordered its specials differently would decode garbage
        under both rules without raising anything.
        """
        lo, hi = self.language_range
        if hi < lo:
            raise WhisperModelMismatch(
                f"{self.model}: <|translate|> ({self.translate}) is not above "
                f"<|startoftranscript|> ({self.sot}), so the language-token "
                f"range [{lo}, {hi}] is empty. This decode path assumes "
                f"whisper's contiguous language block."
            )
        if not (lo <= self.no_speech <= self.vocab_size - 1):
            raise WhisperModelMismatch(
                f"{self.model}: no-speech token {self.no_speech} is outside "
                f"the vocabulary [0, {self.vocab_size - 1}] — the gate would "
                f"index off the end of the softmax."
            )
        if self.transcribe <= hi or self.notimestamps <= hi:
            raise WhisperModelMismatch(
                f"{self.model}: task tokens (<|transcribe|>={self.transcribe}, "
                f"<|notimestamps|>={self.notimestamps}) fall inside the "
                f"language range [{lo}, {hi}]; the language slot's argmax "
                f"would be able to pick one."
            )

    def describe(self) -> str:
        return (
            f"{self.model}: sot={self.sot} eot={self.eot} "
            f"no_speech={self.no_speech} transcribe={self.transcribe} "
            f"translate={self.translate} notimestamps={self.notimestamps} "
            f"lang={self.language_range[0]}-{self.language_range[1]} "
            f"vocab={self.vocab_size} mels={self.n_mels}"
        )


@dataclass(frozen=True)
class WhisperGraph:
    """What the ONNX sessions say about themselves.

    Five constants used to live here: the encoder input name
    (``"input_features"``), the feature dtype (``float16``), the decode context
    (``MEAN_DECODE_LEN = 200``, duplicated across two modules), the mel count
    (implied by whichever tokenizer directory the host happened to pass) and
    the logits width. Each was written against one AI Hub export. They are now
    read off the graph, and where the graph is symbolic the fallback is stated
    rather than assumed.
    """

    encoder_input_name: str
    feature_dtype: type
    decode_context: int
    n_mels: int | None
    logits_width: int | None
    n_layers: int
    # The self-KV cache and the attention mask are allocated by this worker,
    # so their dtypes must match what the decoder declares. Both were
    # hardcoded float16 (which is what the AI Hub fp16 exports take); an fp32
    # export is an ORT type error at the first decoder step.
    kv_dtype: type = np.float16
    mask_dtype: type = np.float16

    @classmethod
    def from_sessions(
        cls, encoder, decoder, profile: WhisperProfile, *, model: str | None = None
    ) -> "WhisperGraph":
        model = model or profile.model
        enc_in = cls._encoder_input(encoder, model)
        enc_shape = getattr(enc_in, "shape", None)
        graph_mels = _dim(enc_shape, 1)

        dec_inputs = list(decoder.get_inputs())
        dec_names = {i.name for i in dec_inputs}
        missing = [
            n for n in ("input_ids", "attention_mask", "position_ids")
            if n not in dec_names
        ]
        if missing:
            raise WhisperModelMismatch(
                f"{model}: decoder graph has no {', '.join(missing)} — this "
                f"worker drives the AI Hub hf_whisper export shape and cannot "
                f"drive this one. Its inputs are: "
                f"{', '.join(sorted(dec_names))}."
            )
        n_layers = sum(1 for n in dec_names if n.startswith("k_cache_self_"))
        if n_layers == 0:
            raise WhisperModelMismatch(
                f"{model}: decoder graph exposes no k_cache_self_* inputs, so "
                f"there is no self-attention KV cache to carry between steps."
            )
        dec_out_names = [o.name for o in decoder.get_outputs()]
        if "logits" not in dec_out_names:
            raise WhisperModelMismatch(
                f"{model}: decoder graph has no 'logits' output; it emits "
                f"{', '.join(dec_out_names)}."
            )

        mask = next(i for i in dec_inputs if i.name == "attention_mask")
        context = _dim(getattr(mask, "shape", None), -1) or DEFAULT_DECODE_CONTEXT
        kv_in = next(
            (i for i in dec_inputs if "_cache_self_" in i.name), None
        )
        logits = next(o for o in decoder.get_outputs() if o.name == "logits")
        width = _dim(getattr(logits, "shape", None), 1)

        graph = cls(
            encoder_input_name=enc_in.name,
            feature_dtype=cls._dtype(enc_in, model),
            decode_context=context,
            n_mels=graph_mels,
            logits_width=width,
            n_layers=n_layers,
            kv_dtype=cls._dtype(kv_in, model) if kv_in is not None else np.float16,
            mask_dtype=cls._dtype(mask, model),
        )
        graph.check_against(profile, model=model)
        return graph

    @staticmethod
    def _encoder_input(encoder, model: str):
        inputs = list(encoder.get_inputs())
        if not inputs:
            raise WhisperModelMismatch(f"{model}: encoder graph has no inputs.")
        named = [i for i in inputs if i.name == "input_features"]
        if named:
            return named[0]
        if len(inputs) == 1:
            # AI Hub names it `input_features`; another exporter may not. One
            # input is unambiguous, so take it rather than throw on a name.
            return inputs[0]
        raise WhisperModelMismatch(
            f"{model}: encoder has {len(inputs)} inputs "
            f"({', '.join(i.name for i in inputs)}) and none is called "
            f"'input_features' — cannot tell which one takes the mel "
            f"spectrogram."
        )

    @staticmethod
    def _dtype(enc_in, model: str) -> type:
        declared = getattr(enc_in, "type", None)
        if declared is None:
            # Fake sessions in the unit suites carry no type. float16 is what
            # this path has always fed, so an unstated type keeps it.
            return np.float16
        if declared not in _ORT_DTYPES:
            raise WhisperModelMismatch(
                f"{model}: encoder input '{enc_in.name}' takes {declared}, "
                f"which this worker cannot build a mel spectrogram in "
                f"(knows: {', '.join(sorted(_ORT_DTYPES))})."
            )
        return _ORT_DTYPES[declared]

    def check_against(self, profile: WhisperProfile, *, model: str) -> None:
        """The mel and vocab pairings — the two that made this refactor.

        The mel one has a field history: a base tokenizer (80) feeding a turbo
        encoder (128) throws ORT INVALID_ARGUMENT at the first ``encoder.run``,
        every utterance, and the failure looks exactly like a broken
        microphone. It was hit live on the X Elite on 2026-08-18. It is loud
        AT DECODE; this makes it loud AT LOAD, with both numbers named, which
        is the difference between a packaging step that catches it and a user
        who does.
        """
        if (
            self.n_mels is not None
            and profile.n_mels is not None
            and self.n_mels != profile.n_mels
        ):
            raise WhisperModelMismatch(
                f"{model}: the tokenizer directory's feature extractor "
                f"produces {profile.n_mels}-mel spectrograms and the encoder "
                f"graph takes {self.n_mels}-mel. These are different Whisper "
                f"checkpoints. Every transcribe would throw ORT "
                f"INVALID_ARGUMENT and every transcript would come back empty "
                f"— which looks like a broken microphone and is not one. "
                f"Stage the tokenizer that belongs to these assets."
            )
        if self.logits_width is None:
            return
        for name, tid in (
            ("<|startoftranscript|>", profile.sot),
            ("<|endoftext|>", profile.eot),
            ("no-speech", profile.no_speech),
            ("<|transcribe|>", profile.transcribe),
            ("<|notimestamps|>", profile.notimestamps),
        ):
            if tid >= self.logits_width:
                raise WhisperModelMismatch(
                    f"{model}: {name} is token {tid} but the decoder emits "
                    f"only {self.logits_width} logits. The tokenizer and the "
                    f"decoder graph are different models."
                )


def bias_delta_for(profile: WhisperProfile, *, stream=None) -> float:
    """The measured phrase-bias delta for the checkpoint that actually loaded.

    An unknown checkpoint gets the most CONSERVATIVE delta in the table and
    says so on stderr — not the highest, and not a module-level default. The
    two failure directions are not symmetric. Too low costs recall: names the
    user has to retype. Too high costs the document: `BiasState.advance`'s own
    history records 26.1% of clips (94/360) coming back EMPTY when the boost
    outran what a beam could confirm, and the table above records ordinary
    words breaking at delta=3. A checkpoint nobody has measured is a
    checkpoint whose breaking point nobody knows, so it gets the gentlest
    value anybody has ever measured rather than a guess in the middle.

    Deliberately NOT a default on `BiasTrie`, for the reason
    `BiasTrie.__init__` spells out at length: a caller that can inherit an
    operating point it never chose is how this package shipped delta=4.0
    twice. Here the lookup is explicit, it is keyed on what actually loaded,
    and the unknown branch is loud.
    """
    delta = BIAS_DELTA_BY_FINGERPRINT.get(profile.fingerprint)
    if delta is not None:
        return delta

    floor = min(BIAS_DELTA_BY_FINGERPRINT.values())
    print(
        f"whisper_profile: {profile.describe()} has no measured phrase-bias "
        f"delta. Falling back to the most conservative measured value "
        f"({floor}); personal-vocabulary recall will be below what this "
        f"checkpoint can do. Measure it before shipping this model.",
        file=stream or sys.stderr,
        flush=True,
    )
    return floor


def warn_if_uncalibrated(profile: WhisperProfile, *, stream=None) -> bool:
    """One stderr line when the loaded model is not the one every threshold in
    this package was measured on. Returns True when it warned.

    Deliberately a warning and not a raise: the thresholds are not wrong on
    another model, they are UNMEASURED on it, and refusing to load would turn
    "re-measure this" into "cannot ship". The line names both models so the
    fact shows up in the app's log instead of in a field report.
    """
    if profile.fingerprint == CALIBRATED_FINGERPRINT:
        return False
    print(
        f"whisper_profile: {profile.describe()} — but SpeechGate's thresholds, "
        f"BiasTrie's delta and looks_like_junk's compression bar were all "
        f"measured on {CALIBRATED_ON} "
        f"(no_speech/vocab/mels {CALIBRATED_FINGERPRINT}). They are unmeasured "
        f"here; re-measure before trusting the non-speech gate.",
        file=stream or sys.stderr,
        flush=True,
    )
    return True
