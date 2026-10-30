"""Dictation's speech worker: a JSON-over-stdio IPC server --
4-byte-BE-length-prefixed JSON frames, an op dispatch table, never dies on a
bad request, and runs as its own Python process beside the app.

op_init takes no database path: a personal prior's database comes per request
(`profile.db_path`; no host sends one today), and corpus.py reads its
`transcript_segments` table's (text, utc_start) columns.

Framing: 4-byte big-endian length prefix + UTF-8 JSON payload, both directions.
Ops:
  {"op":"ping"}                          -> {"ok":true,"op":"ping"}
  {"op":"init","model_dir":...,          -> {"ok":true,"on_npu":bool,...}
      "hf_id":"openai/whisper-base","use_qnn":true,"backend":"qnn"}
      Wire contract v2 (the worker contract §3): init carries
      ONLY {backend, model_dir, hf_id, use_qnn}. "default_bias_phrases" and
      "db_path" are IGNORED WITH A WARNING for one release -- adaptation is
      per request now (see `client`/`profile`/`text_passes` below), nothing
      is process-global. The static P_ilm artifact (transcription_worker/
      models/p_ilm.json.gz, if present) still loads here.
  Per-request adaptation fields, accepted by transcribe / rescore / score:
      "client": "dictation" | "notes"  (default "dictation" when absent);
          namespaces the encoder cache ("<client>:<id>", capacity 4 per
          client) and tags the engine-lock holder.
      "text_passes": bool (default true): false skips the spoken-alias and
          declared-casing rewrites (notes sends false).
      "profile": {"you": bool, "db_path": str} -- when "you" is true the
          worker builds P_you from db_path's transcript history the way
          init used to, cached per db_path for the process lifetime.
          Absent/false = no personal prior.
      "bias_phrases" is the host's list for this request. The only thing
          put in front of it is the vocabulary the host learned from the
          user's corrections (learning.json, LEARNING.md §6), deduplicated
          case-insensitively with the learned spelling first; phrase_deltas
          and `fired` indices are remapped so they stay the host's.
          stats.learned = {"words": n, "aliases": m} counts what was read.
      Every NPU decode takes the one engine lock (engine_lock.py); holds
      (these three ops + diarized_transcribe) enter ahead of queued live
      caption decodes. transcribe/rescore report the wait as
      stats.waited_ms, score as timing_ms.waited.
  {"op":"transcribe","pcm_b64":...,      -> {"ok":true,"text":...,"stats":{...},
      "sample_rate":16000,"channel":"mic","utc_start":...,     echoes channel/utc_start,
      "bias_phrases":["Kensworth Rey",...],"beam_size":4,           "baseline_text":str|null,
      "phrase_deltas":{"0":2.5,"3":1.0},                        "avg_logprob":float|null,
      "compare_baseline":false}                                 "nbest":[{"text","score",
                                                                  "avg_logprob"}]|null}
     bias_phrases/beam_size optional; defaults keep the original greedy path.
     phrase_deltas (optional, continual-personal-learning setup 2): per-phrase
     bias strength keyed by the phrase's index in THIS request's
     bias_phrases (JSON object keys are strings; ints are accepted too).
     Phrases not listed keep the engine's scalar delta; an absent/empty map is
     byte-for-byte the uniform path. Values are clamped to
     [PHRASE_DELTA_MIN, PHRASE_DELTA_MAX] = [-10, 6]; non-finite values and
     bad indices are dropped; either case is counted in
     stats.phrase_deltas_rejected. Every wire number (avg_logprob, nbest[].
     score/avg_logprob) is finite or null -- never Infinity/NaN. Indices are
     remapped onto the deduped request list before they reach the trie.
     nbest / avg_logprob (same setup): the beam path's surviving hypotheses,
     best first, and the winner's token-weighted raw acoustic log-prob.
     Both are null on the greedy path, on a gate-rejected/empty decode, and
     from any engine that does not report them -- a host must treat null as
     "not measured", never as "no alternatives". nbest[0].text == text
     except when the junk filter emptied text.
     beam_size>1 = L1 beam search with biasing (+ L2 density-ratio rescoring
     when P_you was built at init). compare_baseline (optional, default
     false): when true AND P_you is available, also runs a pure-greedy L0
     pass and returns it as baseline_text alongside the personalized text.
     pcm_b64 is base64 of int16 little-endian mono PCM.
     return_fired (optional, loop-v2 P6): when true, the reply and every
     nbest[i] carry "fired": [{"i":host index into THIS request's
     bias_phrases or null,"phrase","pos","n"}] -- the bias phrases that
     COMPLETED on that hypothesis' path (null = not measured: greedy path,
     no trie, old engine; [] = trie live, nothing completed). Computed
     BEFORE the alias/casing passes and never re-derived from the text.
     keep_encoder (optional, P6): when true, the clip's encoder output is
     retained under "encoder_id" (reply key; null when skipped --
     multi-chunk/greedy/gated -- with the reason in stats.encoder_cache).
     A request without return_fired/keep_encoder gets yesterday's reply,
     key for key, unless the decode came back empty: then the reply also
     carries "dropped", why, as one word a host can put to the user --
     "quiet" (the silence pre-check), "language" (the language-probability
     gate), "junk" (the junk filter) or "other" (any other filter, or a
     decode that produced no words without one). Never the words:
     `dropped_reason` stays the log's detail, and one of its forms quotes
     the decoded text.
     recovery (optional, true only): a Recover Last Dictation decode of a
     take an earlier decode may have emptied. The gates that judge whether
     the audio was speech -- the language-probability gate, the no-speech
     segment drop and the junk filter -- are skipped, so the take is not
     emptied again and deleted; the silence pre-check and the request's
     language still apply. Safe only because a recovered result is shown
     and copied, never typed into a field. Transcribe only.
     command_prefix (optional, L1b closed-set constraint = "option A";
        letters A-G are defined in docs/speech/README.md; voice workflows
     only): {"phrases":[...],"max_tokens":3,"mode":"soft"|"hard",
     "delta":6.0}. Once the HOST's wake word has matched it knows something
     the decoder does not -- the next one to three tokens come from the
     closed set of catalogue trigger/alias phrases -- and this is how it
     says so. At generated-content positions 0..max_tokens-1, tokens that
     cannot continue one of those phrases are penalised (soft: -delta;
     hard: MASK_NEG); the constraint releases the moment a phrase completes
     or the position passes max_tokens, so the PAYLOAD after the trigger
     decodes completely unconstrained. Fixes `note` -> `No`, which the L1
     bias trie structurally cannot (beam_decode.CommandPrefix says why).
     Defaults: max_tokens 3, mode "soft", delta 6.0 -- soft on purpose, so a
     hold that asks for nothing in the catalogue still decodes to what was
     said. Beam path only. Applies to the first decoded 30 s window only.
     ABSENT = byte-identical to a request without it: no trie is built, no
     kwarg is passed, and the reply grows no key. Meeting notes never sends
     it; there is no init-frame or process-global form of this field, and
     `compare_baseline`'s unassisted pass drops it with the rest of the
     personalisation. Malformed values are dropped (never fatal) and
     counted in stats.command_prefix_rejected; stats.command_prefix echoes
     {phrases,max_tokens,mode,delta,release} when it was applied, or
     {"applied":false,"reason":...} when it was not.
  {"op":"score","clip":{"pcm_b64":...,"sample_rate":16000} | {"encoder_id":...},
      "language":"en","orig":"...","corr":"...",        -> {"ok":true,"encoder_id","language",
      "distractors":[...] (<=8, optional),                  "delta","acoustic","ilm":null,
      "lambda":0.0 (optional; only 0 is implemented),      "window":{"orig":[s,e],"corr":[s,e],"len"},
      "keep_encoder":true (optional, default true),        "sums":{...},"full":{...},"tokens":{...},
      "tag":"..." (optional, echoed)}                       "distractors":[{"text","delta",...}],
                                                            "margin":float|null,"timing_ms":{...},"tag"}
     Forced scoring (loop-v2 verifier primitive): the unbiased decoder,
     teacher-forced over orig and corr, window = minimal differing token
     span in both + 1 right token (EOT included), sums unnormalised --
     the arithmetic of loop-v2/verifier/forced_score.py. Single 30 s
     window only. Error strings are exact (the scoring contract 2.2).
  {"op":"score_release","encoder_id":...} -> {"ok":true,"released":bool}
  {"op":"rescore","encoder_id":... | "pcm_b64":...,"language":"en",
      "bias_phrases":[...] (1..8),"phrase_deltas":{...},  -> transcribe reply shape
      "beam_size":4,"completion_bonus":0.0,"tag":...}        + "encoder_reused":bool,
                                                             "fired", "timing_ms", "tag"
     Second pass on a cached clip with ONLY the caller's small phrase set
     (neither init defaults nor learned words are merged: it probes exactly
     the phrases named); aliases/casing applied as in transcribe. No engine
     the Dictation hosts select implements it (WhisperMac has no rescore).
  {"op":"shutdown"}                      -> {"ok":true} then clean exit

All errors return {"ok":false,"error":...} and the server keeps serving.
Run:  python -m transcription_worker.asr_server
"""

from __future__ import annotations

import base64
import json
import math
import os
import struct
import sys
import traceback
from pathlib import Path

# Static L2 artifact shipped in the repo. Resolved relative to this file, not
# cwd, so it doesn't matter how the worker was spawned. The accelerator
# engine initializer
# no-ops gracefully if the path doesn't exist.
_P_ILM_PATH = Path(__file__).resolve().parent / "models" / "p_ilm.json.gz"

_engine = None
# Per-user L2 priors, built on first use from a request's
# `profile.db_path` transcript history and cached per db_path for the life
# of the process (the same once-per-process cost init used to pay, now paid
# by the first dictation request that asks). A db_path that was unreadable
# or had no history caches as None -- density ratio simply never activates
# for it (the engine's own guard: it only turns on when both
# p_you is given AND self.p_ilm was loaded).
_p_you_cache: dict[str, object] = {}

#: Client namespaces the worker knows (plan §3). Any other non-empty string
#: is accepted as its own namespace; absent means "dictation" (old clients).
DEFAULT_CLIENT = "dictation"
KNOWN_CLIENTS = ("dictation", "notes")

# Production gives replies their own current-user-only named pipe.  Native QNN
# libraries are then free to write to process stdout without ever sharing bytes
# with the framed protocol.
_PROTOCOL_PIPE_ENV = "DICTATION_ASR_PROTOCOL_PIPE"


def _engine_lock():
    """The process-wide engine lock (engine_lock.ENGINE_LOCK). Imported
    lazily like every other sibling here so `python asr_server.py` (the dev
    spawn that fixes __package__ at the bottom of this file) still starts."""
    from . import engine_lock

    return engine_lock.ENGINE_LOCK


def _client(req: dict) -> str:
    """The request's client namespace; absent/empty = dictation (old hosts)."""
    client = req.get("client")
    return client if isinstance(client, str) and client else DEFAULT_CLIENT


def _qualify_encoder_id(client: str, encoder_id) -> tuple[str, bool]:
    """``"<client>:<id>"`` for an id the host sent. Returns
    ``(qualified_id, foreign)``: an id already carrying THIS client's
    prefix passes through, a bare id gets the prefix added, and an id
    carrying ANOTHER known client's prefix is reported foreign -- the
    learning ops never touch a namespace they do not own."""
    raw = str(encoder_id or "")
    if raw.startswith(client + ":"):
        return raw, False
    head = raw.split(":", 1)[0]
    if head in KNOWN_CLIENTS and head != client:
        return raw, True
    return f"{client}:{raw}", False


def _profile_prior(req: dict):
    """P_you for ``req["profile"]`` (``{"you": bool, "db_path": str}``), or
    None. Built on first sight of a db_path and cached (see _p_you_cache)."""
    profile = req.get("profile")
    if not isinstance(profile, dict) or not profile.get("you"):
        return None
    db_path = profile.get("db_path")
    if not db_path:
        return None
    key = str(db_path)
    if key in _p_you_cache:
        return _p_you_cache[key]
    try:
        from . import corpus

        segments = corpus.load_user_segments(db_path)
        prior = corpus.build_p_you(segments, _engine.tokenizer)
    except Exception as e:  # noqa: BLE001 -- a missing prior must not cost the transcript
        print(f"L2 P_you build skipped: {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        prior = None
    _p_you_cache[key] = prior
    return prior


def _read_payload(stdin) -> bytes | None:
    """One frame's payload, or None once the host has closed the stream."""
    header = stdin.read(4)
    if len(header) < 4:
        return None
    (length,) = struct.unpack(">I", header)
    payload = stdin.read(length)
    if len(payload) < length:
        return None
    return payload


def _decode_frame(payload: bytes):
    return json.loads(payload.decode("utf-8"))


def _read_frame(stdin) -> dict | None:
    payload = _read_payload(stdin)
    return None if payload is None else _decode_frame(payload)


def _write_frame(stdout, msg: dict) -> None:
    payload = json.dumps(msg, separators=(",", ":")).encode("utf-8")
    stdout.write(struct.pack(">I", len(payload)))
    stdout.write(payload)
    stdout.flush()


def _reserve_protocol_stdout():
    """Open the dedicated host reply pipe, or protect legacy stdout framing.

    The app's launcher passes a current-user-only named-pipe path in
    ``DICTATION_ASR_PROTOCOL_PIPE``.  That pipe has never been process stdout, so
    even a native library with a startup-cached C ``stdout`` FILE cannot put a
    byte into a JSON frame.  Process stdout remains a diagnostics channel the
    host drains alongside stderr.

    Assigning ``sys.stdout = sys.stderr`` protects framing from Python
    ``print()`` calls, but not from ONNX Runtime / QNN native libraries.  Those
    libraries write to the process-level stdout descriptor directly while a
    cold HTP context is being compiled.  A four-byte prefix such as ``Star``
    (from ``Starting...``) is then interpreted by the host as a multi-gigabyte
    JSON frame length and the shared speech worker hangs.

    The descriptor-duplication path remains for direct developer harnesses
    that do not provide the dedicated handle.  It catches ordinary fd writes;
    production does not rely on it because separately linked Windows CRTs can
    retain the original stdout handle from process startup.
    """
    protocol_pipe = os.environ.get(_PROTOCOL_PIPE_ENV)
    if protocol_pipe:
        sys.stdout = sys.stderr
        return open(protocol_pipe, "wb", buffering=0)

    protocol = sys.stdout.buffer
    try:
        stdout_fd = protocol.fileno()
        stderr_fd = sys.stderr.fileno()
        protocol_fd = os.dup(stdout_fd)
    except (AttributeError, OSError):
        # StringIO-like test hosts have no process descriptor.  Retain the old
        # Python-level protection; production redirected pipes always have one.
        sys.stdout = sys.stderr
        return protocol

    try:
        if sys.platform == "win32":
            import msvcrt

            msvcrt.setmode(protocol_fd, os.O_BINARY)
        os.dup2(stderr_fd, stdout_fd)
    except Exception:
        os.close(protocol_fd)
        raise

    # Some native Windows libraries ask GetStdHandle(STD_OUTPUT_HANDLE)
    # instead of writing CRT fd 1.  Keep that route on stderr as well; the
    # duplicated CRT descriptor above still owns the original protocol pipe.
    if sys.platform == "win32":
        try:
            import ctypes

            kernel32 = ctypes.windll.kernel32
            kernel32.GetStdHandle.argtypes = [ctypes.c_ulong]
            kernel32.GetStdHandle.restype = ctypes.c_void_p
            kernel32.SetStdHandle.argtypes = [ctypes.c_ulong, ctypes.c_void_p]
            kernel32.SetStdHandle.restype = ctypes.c_int
            stderr_handle = kernel32.GetStdHandle(0xFFFFFFF4)  # STD_ERROR_HANDLE
            if stderr_handle:
                kernel32.SetStdHandle(0xFFFFFFF5, stderr_handle)  # STD_OUTPUT_HANDLE
        except Exception:
            # CRT fd 1 is already protected.  SetStdHandle is the extra guard
            # for libraries that bypass it, not a reason to lose the worker.
            pass

    sys.stdout = sys.stderr
    return os.fdopen(protocol_fd, "wb", buffering=0)


# Wire-protocol version. Bump ONLY on a breaking frame/op contract change.
# The host should `ping` first and refuse to proceed if this is absent or
# newer than it understands — that turns a mismatch into an instant, named
# error instead of the 4-minute silent pipe-connect hang a host/worker
# protocol skew produced in the field (2026-09-09). v2 = the current
# length-prefixed framed contract with the dedicated reply pipe.
PROTOCOL_VERSION = 2


def op_ping(req: dict) -> dict:
    # Carries the protocol version so a host can handshake on the cheapest
    # op, before any model init.
    return {"ok": True, "op": "ping", "protocol": PROTOCOL_VERSION}


def op_init(req: dict) -> dict:
    global _engine

    # Wire contract v2: adaptation left the init frame. One release of
    # tolerance for hosts that still send the old fields -- warn, ignore.
    for legacy in ("default_bias_phrases", "db_path"):
        if req.get(legacy) is not None:
            print(f"asr_server: init.{legacy} is ignored (adaptation is per request: "
                  "send bias_phrases / profile on each transcribe)",
                  file=sys.stderr, flush=True)

    # Both native hosts use this adapter: MLX on macOS, faster-whisper on
    # Windows. The legacy wire value "mac" is retained for compatibility.
    backend = req.get("backend") or "mac"
    if backend == "mac":
        from . import whisper_mac

        # The engine owns its model default; forward only an explicit choice.
        mac_kwargs = {
            "model_dir": req.get("model_dir"),
            "hf_revision": req.get("hf_revision"),
            "allow_model_download": bool(req.get("allow_model_download", True)),
            "p_ilm_path": _P_ILM_PATH,
        }
        if req.get("hf_id"):
            mac_kwargs["hf_id"] = req["hf_id"]
        _engine = whisper_mac.WhisperMac(**mac_kwargs)
    elif backend == "stub":
        from . import whisper_mac

        _engine = whisper_mac.StubEngine()
    else:
        return {"ok": False, "error": f"unknown backend {backend!r}"}

    # A re-init is a new engine (possibly a new tokenizer): priors built
    # against the old one must not be reused.
    _p_you_cache.clear()

    return {
        "ok": True,
        "backend": backend,
        "on_npu": _engine.on_npu,
        # Which engine actually won the import race, reported at INIT rather
        # than only after the first decode.
        #
        # The host decides from this whether a vocabulary is safe to send, and
        # it used to learn the answer only from a completed transcribe. That
        # was harmless while the host pinned the engine itself; once it
        # stopped pinning (2026-08-18, so the worker can reach the accelerated
        # engine), "unknown until the first decode" meant the user's FIRST
        # utterance of every session went out with no vocabulary at all —
        # exactly the un-biased warm-up utterance `asr-model-resolution-check`
        # exists to forbid. `encoder_providers` already carried the name
        # inside a display string; this is the same fact as a field a caller
        # may parse.
        "engine": getattr(_engine, "_engine_name", None),
        "encoder_providers": _engine.encoder.get_providers(),
        # Same version the host learns from ping; repeated here so a host that
        # skipped ping still gets it before sending any stream frame.
        "protocol": PROTOCOL_VERSION,
    }


#: The kwargs that ARE the personalisation. `compare_baseline`'s unassisted
#: pass is the personalised call with exactly these removed -- nothing else
#: may differ between the two decodes, or the comparison measures the wrong
#: thing.
_PERSONALISATION_KWARGS = frozenset(
    {"bias_phrases", "phrase_deltas", "p_you", "return_fired", "keep_encoder",
     # Qualifies bias_phrases, so it leaves with them.
     "case_only_phrases",
     # The catalogue closed set is personalisation of the strongest kind: a
     # baseline that decoded under it would credit the constraint's win to the
     # vocabulary and show the two arms as agreeing.
     "command_prefix"})

#: `command_prefix.max_tokens` ceiling. The longest catalogue trigger is five
#: words, and a Whisper BPE word is one to three tokens; 8 is past any real
#: trigger while still bounding how much of a payload a mis-armed constraint
#: could reach. Values above it are clamped, not rejected -- a host that asks
#: for more gets the most this worker will do, and is told so.
COMMAND_PREFIX_MAX_TOKENS = 8
#: `command_prefix.delta` ceiling for soft mode. Above MASK_NEG's magnitude a
#: "soft" constraint is a hard one wearing the wrong name, and the mode is the
#: field the decoder reasons about.
COMMAND_PREFIX_DELTA_MAX = 50.0


def _command_prefix(req: dict) -> tuple[dict | None, list[str]]:
    """The request's `command_prefix`, normalised, plus what was dropped.

    Absent, null, or empty returns ``(None, [])`` and NOTHING is forwarded --
    that is the byte-identical path, and it is the only behaviour meeting
    notes will ever see. Malformed pieces are dropped rather than raised, for
    the same reason `_remap_phrase_deltas` drops stale indices: a host whose
    catalogue just changed must not lose the hold.
    """
    spec = req.get("command_prefix")
    if not spec:
        return None, []
    rejected: list[str] = []
    if not isinstance(spec, dict):
        return None, ["command_prefix: expected an object"]

    phrases: list[str] = []
    seen: set[str] = set()
    for raw in spec.get("phrases") or []:
        if not isinstance(raw, str) or not raw.strip():
            rejected.append(f"phrase: {raw!r}")
            continue
        if raw not in seen:
            seen.add(raw)
            phrases.append(raw)
    if not phrases:
        rejected.append("phrases: empty")
        return None, rejected

    max_tokens = spec.get("max_tokens", _beam_command_prefix().DEFAULT_MAX_TOKENS)
    try:
        max_tokens = int(max_tokens)
    except (TypeError, ValueError):
        rejected.append(f"max_tokens: {max_tokens!r}")
        max_tokens = _beam_command_prefix().DEFAULT_MAX_TOKENS
    if max_tokens < 1 or max_tokens > COMMAND_PREFIX_MAX_TOKENS:
        rejected.append(f"max_tokens: {max_tokens} clamped")
        max_tokens = min(max(max_tokens, 1), COMMAND_PREFIX_MAX_TOKENS)

    mode = spec.get("mode", _beam_command_prefix().SOFT)
    if mode not in _beam_command_prefix().MODES:
        # Soft, not hard, is the fallback: an unrecognised mode must fail
        # toward the behaviour that degrades gracefully.
        rejected.append(f"mode: {mode!r}")
        mode = _beam_command_prefix().SOFT

    import math

    default_delta = _beam_command_prefix().DEFAULT_DELTA
    raw_delta = spec.get("delta", default_delta)
    try:
        delta = float(raw_delta)
    except (TypeError, ValueError):
        delta = float("nan")
    if not math.isfinite(delta) or delta <= 0.0:
        # Non-numeric, non-finite, zero or negative: there is no sane clamp
        # for any of these -- clamping a negative delta to a floor would ship
        # a constraint that LOOKS armed and is inert, which is the failure
        # `CommandPrefix.__init__` refuses outright. Fall back to the default
        # and say so.
        if raw_delta != default_delta:
            rejected.append(f"delta: {raw_delta!r}")
        delta = default_delta
    elif delta > COMMAND_PREFIX_DELTA_MAX:
        rejected.append(f"delta: {delta} clamped")
        delta = COMMAND_PREFIX_DELTA_MAX

    return ({"phrases": phrases, "max_tokens": max_tokens,
             "mode": mode, "delta": delta}, rejected)


def _beam_command_prefix():
    """`beam_decode.CommandPrefix`, imported lazily.

    asr_server must stay importable (and this parser testable) on a box with
    no ONNX, and `beam_decode` is numpy-only -- but the import still belongs
    inside the call, next to every other engine import in this module."""
    from .beam_decode import CommandPrefix

    return CommandPrefix


def op_transcribe(req: dict) -> dict:
    import numpy as np

    if _engine is None:
        return {"ok": False, "error": "not initialized"}
    sample_rate = req.get("sample_rate", 16000)
    if sample_rate != 16000:
        return {"ok": False, "error": f"expected 16kHz PCM, got {sample_rate}"}
    pcm = np.frombuffer(base64.b64decode(req["pcm_b64"]), dtype=np.int16)
    audio = pcm.astype(np.float32) / 32768.0
    if len(audio) == 0:
        # Empty audio would feature-extract to 30s of pure padding and invite
        # hallucinated text. (Reachable via external IPC clients; the C# host
        # never sends empty utterances.)
        return {
            "ok": True,
            "text": "",
            "stats": {"chunks": 0, "tokens": 0},
            "channel": req.get("channel"),
            "utc_start": req.get("utc_start"),
            "baseline_text": None,
            "baseline_ran": False,
            "avg_logprob": None,
            "nbest": None,
        }
    # The vocabulary for this decode: what the host learned from the user's
    # corrections (LEARNING.md §6), then the request's bias_phrases. Deduped
    # case-insensitively, order-preserving, so a host that lists a phrase
    # twice, or lists a word the user already taught, gets one slot --
    # `_remap_phrase_deltas` and `_wire_fired` fold the indices. With no
    # learning file this is the request's list as before, except that two
    # spellings differing only in case now share one slot and a non-string
    # entry is dropped.
    # `_client`, not the raw field: the Mac's final frame names no client
    # (absent means dictation), and its press-time scene must rank the words
    # as the preview's and Windows' frames do (LEARNING.md §4.7, §6).
    client = _client(req)
    learned_words, case_only, learned_aliases, learned_deltas = _learned_vocabulary(req.get("bias_phrases"), scene=req.get("vocabulary_scene") if client == "dictation" else None)
    merged_bias = _merge_vocabulary(learned_words, req.get("bias_phrases"))
    p_you = _profile_prior(req)

    # Comparison mode (opt-in, roughly doubles decode work per utterance): also
    # run a pure-greedy L0 pass -- no bias, no p_you -- so the UI can show it
    # next to the personalized text.
    #
    # The gate is "is there anything to compare AGAINST", and that is either
    # side of the personalization: the bias vocabulary (L1) or P_you (L2).
    #
    # It used to require P_you alone. That made the comparison silently empty
    # in the case it is most wanted -- a user with a vocabulary and no history,
    # which is every new user and the entire premise of showing what the
    # vocabulary buys. The caller asked for a comparison, got `baseline_text:
    # null`, and had no way to tell that from "the baseline agreed".
    run_compare = bool(req.get("compare_baseline", False)) and (
        p_you is not None or bool(merged_bias)
    )
    baseline_text = None

    # The personalized call: L1 biasing (merged_bias) plus L2 density-ratio
    # rescoring whenever the request's profile built a P_you (transcribe()
    # itself only activates the ratio when beam_size>1 and self.p_ilm was
    # also loaded).
    kwargs = dict(
        bias_phrases=merged_bias or None,
        beam_size=int(req.get("beam_size", 1)),
        p_you=p_you,
    )
    # Per-phrase bias strength (see the module docstring). The host keys by
    # index into ITS bias_phrases list; the engine keys by index into the
    # MERGED list, which is prefixed by the learned words and deduped, so the
    # two indexings only coincide when nothing was learned and the host sent
    # no duplicates. Remapped here -- the one place that knows both lists --
    # rather than making the host replicate the merge. Forwarded only when the
    # engine's transcribe() accepts the kwarg (capability-checked like
    # return_words/language), and only when non-empty, so an engine without
    # the parameter and a host that sent nothing both see today's call.
    import inspect as _inspect

    # Final Dictation holds use the existing MLX model with a small GPU beam
    # once there is vocabulary. Live previews stay greedy, and other engines
    # keep their own beam implementation. The optional false switch is for
    # reproducible prompt-only comparisons; ordinary hosts need no new field.
    if (client == "dictation" and merged_bias and not req.get("return_words")
            and req.get("vocabulary_search", True) is True
            and getattr(_engine, "_engine_name", None) == "mlx"
            and "vocabulary_search" in _inspect.signature(_engine.transcribe).parameters):
        from .mlx_beam import DEFAULT_BEAM_SIZE
        kwargs["vocabulary_search"] = True
        kwargs["beam_size"] = DEFAULT_BEAM_SIZE

    phrase_deltas, delta_rejects = _remap_phrase_deltas(
        req.get("phrase_deltas"), list(req.get("bias_phrases") or []), merged_bias)
    # Automatic evidence supplies a bounded prior. Explicit request phrases
    # keep the engine default unless the caller supplied its own delta.
    declared = {_phrase_key(p) for p in req.get("bias_phrases") or [] if isinstance(p, str)}
    learned_by_key = {_phrase_key(p): delta for p, delta in learned_deltas.items()}
    adaptive_deltas = {i: learned_by_key[_phrase_key(p)] for i, p in enumerate(merged_bias)
                       if _phrase_key(p) in learned_by_key and _phrase_key(p) not in declared}
    adaptive_deltas.update(phrase_deltas or {})
    phrase_deltas = adaptive_deltas or None
    if phrase_deltas and "phrase_deltas" in _inspect.signature(_engine.transcribe).parameters:
        kwargs["phrase_deltas"] = phrase_deltas
    # The learned words whose declared spelling restores case only (see
    # `_case_only`). An engine that runs its own casing pass (WhisperMac) must
    # apply the same rule as `_apply_text_passes` below, or it would join a
    # spaced form that this pass then cannot undo. Capability-checked and
    # sent only when non-empty, like phrase_deltas.
    if case_only and "case_only_phrases" in _inspect.signature(_engine.transcribe).parameters:
        kwargs["case_only_phrases"] = case_only
    # Optional word timestamps for a downstream speaker-segmentation merge.
    # Forwarded only when the engine's transcribe() accepts the kwarg, so a
    # client sending it to the QNN engine gets a normal transcript instead
    # of a TypeError — additive, never breaking.
    if req.get("return_words"):
        import inspect

        if "return_words" in inspect.signature(_engine.transcribe).parameters:
            kwargs["return_words"] = True

    # Decode language. Forwarded the same capability-checked way as
    # return_words, so a client sending it to the QNN engine (which has no
    # such parameter) still gets a transcript rather than a TypeError.
    #
    # Absent/None means AUTO-DETECT, and that is the intended default. The
    # mac engine used to hardcode English at every call site, which is
    # silently catastrophic rather than merely wrong: Whisper does not fail
    # on a language mismatch, it TRANSLATES. Measured over this user's own
    # 1,657-utterance corpus (development iteration 14), Chinese
    # speech forced through an English decode scored WER 8.75 — the user got
    # fluent English they never said, pasted into their editor.
    import inspect as _inspect

    if "language" in _inspect.signature(_engine.transcribe).parameters:
        kwargs["language"] = req.get("language")

    # Caller-measured voiced fraction (the Mac's AudioGate). Forwarded
    # the same capability-checked way as return_words/language, so a client
    # sending it to the QNN engine (which has no such parameter) still gets a
    # transcript rather than a TypeError. Absent means UNKNOWN -- the engine
    # must not treat a missing value as either thin or full.
    if "voiced_fraction" in _inspect.signature(_engine.transcribe).parameters:
        voiced = req.get("voiced_fraction")
        if isinstance(voiced, (int, float)):
            kwargs["voiced_fraction"] = float(voiced)

    # The caller declaring whether the hallucination-phrase blocklist applies
    # on ITS pipeline (notes: yes; dictation: no). Explicit rather than
    # inferred from voiced_fraction, which was backwards on BOTH pipelines --
    # see blocked_hallucination's docstring. Same capability check, and the
    # default when absent is "do not block": an unknown caller must get the
    # text that was actually decoded.
    if "hallucination_guard" in _inspect.signature(_engine.transcribe).parameters:
        kwargs["hallucination_guard"] = bool(req.get("hallucination_guard", False))

    # Recover Last Dictation (see `recovery` in the module docstring). The
    # literal true only, and capability-checked like the fields above: an
    # engine without the parameter decodes under its usual gates.
    if req.get("recovery") is True and "recovery" in _inspect.signature(_engine.transcribe).parameters:
        kwargs["recovery"] = True

    # loop-v2 P6 additions, both opt-in and both capability-checked like
    # return_words: a request without them produces today's engine call and
    # today's reply keys, exactly. `fired` is what the trie COMPLETED on each
    # hypothesis (the ledger's "did the boost do the work" signal);
    # `keep_encoder` retains the clip's encoder output for a later
    # `score`/`rescore` on the same audio without re-running the encoder.
    want_fired = bool(req.get("return_fired", False))
    if want_fired and "return_fired" in _inspect.signature(_engine.transcribe).parameters:
        kwargs["return_fired"] = True
    # L1b closed-set command prefix. Capability-checked like every other
    # additive field, and forwarded ONLY when the request carried a usable
    # one: an absent field means the kwarg is never passed, so the engine call
    # is the call it was before this layer existed.
    cmd_prefix, cmd_rejects = _command_prefix(req)
    if (cmd_prefix is not None
            and "command_prefix" in _inspect.signature(_engine.transcribe).parameters):
        kwargs["command_prefix"] = cmd_prefix
    elif cmd_prefix is not None:
        cmd_rejects.append("engine: no command_prefix support")

    want_encoder = bool(req.get("keep_encoder", False))
    encoder_id = None
    if want_encoder and "keep_encoder" in _inspect.signature(_engine.transcribe).parameters:
        encoder_id = _encoder_id(pcm.tobytes(), req.get("language"), client)
        kwargs["keep_encoder"] = encoder_id

    # The baseline is the SAME decode minus the personalisation (one clip,
    # two decodes). Built from the personalised call's kwargs,
    # so beam_size, language, voiced_fraction and hallucination_guard are
    # identical on both sides and the only difference shown on stage is the
    # vocabulary: bias_phrases, phrase_deltas and p_you are dropped (and the
    # loop-only return_fired/keep_encoder, which have nothing to report or
    # keep without a trie). A baseline that decoded greedily while the
    # personalised pass ran a beam would credit the beam to the vocabulary.
    baseline_kwargs = {}
    if run_compare:
        baseline_kwargs = {
            k: v for k, v in kwargs.items()
            if k not in _PERSONALISATION_KWARGS
        }
        # `personalize=False` where the engine understands it, so the baseline
        # is a genuine unassisted decode rather than one the alias table and
        # the casing pass have already helped. Capability-checked the same way
        # as return_words/language: an engine without the parameter still
        # returns a transcript instead of a TypeError.
        if "personalize" in _inspect.signature(_engine.transcribe).parameters:
            baseline_kwargs["personalize"] = False

    # One engine lock for every NPU decode in the process, HOLD priority:
    # a queued live-caption decode yields to this. Both passes of a
    # comparison run under one acquisition so nothing interleaves them.
    with _engine_lock().held(tag=f"{client}:transcribe") as waited_ms:
        if run_compare:
            baseline_text = _engine.transcribe(audio, **baseline_kwargs)["text"]
        result = _engine.transcribe(audio, **kwargs)
    stats = result.setdefault("stats", {})
    stats["waited_ms"] = round(waited_ms, 2)
    # Whether the engine already ran the spoken-alias table over its text
    # (WhisperMac does; the QNN engine does not). A signal from the engine to
    # `_apply_text_passes`, so it is taken off the reply here whether or not
    # the text passes run.
    engine_aliased = _engine_aliased(stats)
    # Counts only: the words themselves are the user's vocabulary, and the
    # hosts copy this reply's stats and the worker's stderr into their logs.
    stats["learned"] = {"words": len(learned_words), "aliases": learned_aliases}
    if delta_rejects:
        # Surfaced in stats so a host "confidence dial" that sent garbage can
        # see it was ignored, rather than wondering why the boost did nothing.
        stats["phrase_deltas_rejected"] = delta_rejects
    if cmd_rejects:
        # Same contract as phrase_deltas_rejected: a host that sent garbage
        # sees that it was ignored instead of wondering why the constraint
        # did nothing.
        stats["command_prefix_rejected"] = cmd_rejects
    # `text_passes: false` (notes) = the decoder's text as decoded: no spoken
    # aliases, no declared casing. Absent = true (old clients, dictation).
    if _text_passes(req):
        _apply_text_passes(result, merged_bias, case_only, engine_aliased=engine_aliased)
    reply = _transcribe_reply(result, req, baseline_text, run_compare)
    if want_fired:
        request_phrases = list(req.get("bias_phrases") or [])
        stats = result.get("stats") or {}
        reply["fired"] = _wire_fired(stats.get("fired"), merged_bias, request_phrases)
        if reply["nbest"] is not None:
            for hyp, src in zip(reply["nbest"], stats.get("nbest") or []):
                hyp["fired"] = _wire_fired(src.get("fired"), merged_bias, request_phrases)
    if want_encoder:
        stored = (result.get("stats") or {}).get("encoder_id")
        reply["encoder_id"] = stored if stored == encoder_id else None
    return reply


def _learned_vocabulary(requested, *, scene=None) -> tuple[list[str], frozenset[str], int, dict[str, float]]:
    """``(learned words, case-only words, alias count, word deltas)`` from the
    host's learning.json, for a request that sent ``requested``.

    Read on every transcribe; `learned_vocabulary` re-parses only when the
    file's size or mtime changed. The aliases themselves reach the text
    through `spoken_aliases.load`; only their count is needed here. Like the
    alias pass, a failure must never cost the transcript.
    """
    try:
        from . import learned_vocabulary, spoken_aliases

        learned = learned_vocabulary.load(spoken_aliases.learning_path(), scene=scene)
        return learned.words, _case_only(learned, requested), len(learned.aliases), dict(learned.deltas or {})
    except Exception as exc:  # noqa: BLE001
        print(f"asr_server: learned vocabulary skipped — {type(exc).__name__}",
              file=sys.stderr, flush=True)
        return [], frozenset(), 0, {}


def _case_only(learned, requested) -> frozenset[str]:
    """Keys (`_phrase_key`) of the learned words that restore CASE only.

    Declared casing also joins a spaced form and folds -ise/-ize, which is
    safe only for a term somebody declared (declared_casing's own safety
    argument). A word the host learned from one correction was not declared:
    one fix of "restart the service now" to "ServiceNow" would otherwise
    rewrite every later "service now" in prose, although LEARNING.md §4.2
    holds that very rewrite back until the same fix is seen on two
    dictations, when the learned alias does it exactly. So every learned word
    whose origin is not `taught` (a correction, or an origin this version
    does not know) is case-only, unless the request itself sends the phrase,
    which declares it for this utterance.
    """
    from . import learned_vocabulary

    declared = {_phrase_key(p) for p in requested or [] if isinstance(p, str)}
    return frozenset(_phrase_key(word) for word, origin in zip(learned.words, learned.origins)
                     if origin != learned_vocabulary.TAUGHT and _phrase_key(word) not in declared)


def _phrase_key(phrase: str) -> str:
    """How two bias phrases are compared: case-insensitively, as the alias
    table and declared casing compare the words they carry."""
    return phrase.lower()


def _merge_vocabulary(learned: list[str], requested) -> list[str]:
    """Learned words first, then the request's phrases, deduplicated
    case-insensitively; the first spelling seen is the one kept.

    Learned words lead because `learned_vocabulary` ranks them by evidence and
    scene (LEARNING.md §6) and `_vocabulary_prompt` keeps the head of the list
    when the prompt window is full. A non-string phrase is dropped: the prompt and casing passes
    below can do nothing with it but fail.
    """
    from . import learned_vocabulary
    return learned_vocabulary.merge_words(learned, requested)


def _slot(phrases: list, phrase) -> int | None:
    """Index of ``phrase`` in ``phrases``: the exact entry when present,
    otherwise the entry it was folded into by `_merge_vocabulary`."""
    try:
        return phrases.index(phrase)
    except ValueError:
        pass
    if isinstance(phrase, str):
        for index, candidate in enumerate(phrases):
            if isinstance(candidate, str) and _phrase_key(candidate) == _phrase_key(phrase):
                return index
    return None


def _text_passes(req: dict) -> bool:
    """``text_passes`` as sent; absent = true (yesterday's behaviour)."""
    value = req.get("text_passes")
    return True if value is None else bool(value)


def _engine_aliased(stats: dict) -> bool:
    """Take the engine's "alias table already applied" flag
    (`spoken_aliases.APPLIED_STAT`) off ``stats`` and return it."""
    from . import spoken_aliases

    return bool(stats.pop(spoken_aliases.APPLIED_STAT, False))


def _apply_text_passes(result: dict, merged_bias: list[str],
                       case_only: frozenset[str] = frozenset(), *,
                       engine_aliased: bool = False) -> None:
    """The two engine-agnostic text rewrites (aliases, then declared casing),
    applied in place to ``result["text"]`` and every nbest text. Shared by
    `transcribe` and `rescore` so the second pass is comparable with the
    first. ``case_only``: the vocabulary entries that restore case only
    (`_case_only`). ``engine_aliased``: the engine already applied the alias
    table (`_engine_aliased`), so it is not applied again. Neither pass may
    cost the transcript, so each fails soft."""

    # Spoken-form aliases (shipped, learned and user-declared spoken->written
    # rewrites), applied ENGINE-AGNOSTICALLY here so the QNN path gets the
    # same treatment the mac engine already applies internally
    # (whisper_mac.transcribe). ONCE per decode: an engine that already ran
    # the table says so, and its text is left alone here. A second pass is
    # not a no-op. `apply` shelters a match only inside its OWN written form
    # (`flow -> Local Flow` survives it), so when one alias's spoken form
    # sits inside another's written form, `ask gpt four oh` -> `ask GPT-4o`
    # became `ask ChatGPT-4o` on the second pass (`gpt -> ChatGPT`). This is
    # the layer that carries terms decode-time biasing
    # structurally cannot (silent letters: Knemo; declared spellings: Trovioo,
    # Local Flow) — the trie recovers some terms while aliases carry the rest.
    # carry the rest. A failure must never cost the transcript.
    try:
        from . import spoken_aliases
        _aliases = {} if engine_aliased else spoken_aliases.load()
        if _aliases and result.get("text"):
            result["text"], _applied = spoken_aliases.apply(result["text"], _aliases)
            if _applied:
                # A count, never the words: they are the user's vocabulary.
                print(f"asr_server: aliases applied — {len(_applied)} rewrite(s)",
                      file=sys.stderr, flush=True)
            # The alternatives get the same rewrite, or the host would compare
            # an aliased winner against un-aliased runners-up and read a
            # spelling difference as a decode difference.
            for _hyp in (result.get("stats") or {}).get("nbest") or []:
                if _hyp.get("text"):
                    _hyp["text"], _ = spoken_aliases.apply(_hyp["text"], _aliases)
    except Exception as _exc:  # noqa: BLE001
        print(f"asr_server: alias pass skipped — {type(_exc).__name__}",
              file=sys.stderr, flush=True)

    # Declared-form casing, applied ENGINE-AGNOSTICALLY here for the same
    # reason the alias pass above is: it lived only in `whisper_mac.transcribe`,
    # so the QNN path had no casing restoration at all. Windows is where Turbo
    # is now installable, and Turbo is the checkpoint whose recovery is high
    # enough for the casing gap to be what the user is left holding.
    #
    # The gap this closes is invisible to the programme's own headline metric:
    # Internal research finding 39 measured case-folded recovery at 92.06% and
    # exact display form at 82.54% through the product decoder — 9.5 points,
    # every one of them scored as a HIT for recovery and every one a hand-fix
    # for the user. `Soliton`→`soliton`, `QuestPDF`→`questpdf`,
    # `SelfBlended`→`selfblended`.
    #
    # AFTER the alias pass, not before: aliases rewrite a spoken form to a
    # written one and that written form is the declared spelling already, so
    # running casing first would have nothing to correct and would then be
    # undone by the rewrite. It still runs after the mac engine's own casing
    # pass: once the declared form is in the text there is no case-only
    # mismatch left to find.
    #
    # `merged_bias` is the host's learned words and THIS request's
    # bias_phrases (op_transcribe; rescore sends only its own): the declared
    # spelling of a term is keyed off what reaches this utterance, nothing
    # global. The learned words in `case_only` restore case and nothing more.
    #
    # A failure must never cost the transcript.
    try:
        from . import declared_casing
        if merged_bias and result.get("text"):
            _vocab = list(merged_bias)
            result["text"], _restored = declared_casing.apply(
                result["text"], _vocab, case_only=case_only)
            if _restored:
                print(f"asr_server: casing restored — {len(_restored)} word(s)",
                      file=sys.stderr, flush=True)
            # The alternatives get the same treatment, for the reason the alias
            # pass states above and for one more that is written into this
            # module's own contract: `nbest[0].text == text`. Re-casing only the
            # winner would break that equality on the exact terms this stage
            # exists to fix, and leave the host comparing `QuestPDF` against a
            # runner-up spelled `questpdf` — a casing difference read as a
            # decode difference.
            for _hyp in (result.get("stats") or {}).get("nbest") or []:
                if _hyp.get("text"):
                    _hyp["text"], _ = declared_casing.apply(_hyp["text"], _vocab, case_only=case_only)
    except Exception as _exc:  # noqa: BLE001
        print(f"asr_server: casing pass skipped — {type(_exc).__name__}",
              file=sys.stderr, flush=True)


def _dropped(result: dict) -> str | None:
    """The reply's `dropped` word for an empty decode, or None when it has
    words. Derived from the engine's `dropped_reason`, never from the text."""
    if (result.get("text") or "").strip():
        return None
    reason = result.get("dropped_reason") or ""
    if reason == "silence-precheck":
        return "quiet"
    # "language-prob-gate", and its prompted form "+avg-logprob".
    if reason.startswith("language-prob-gate"):
        return "language"
    if reason == "junk-filter":
        return "junk"
    return "other"


def _transcribe_reply(result: dict, req: dict, baseline_text, run_compare: bool) -> dict:
    """The `transcribe` wire shape. One function so `rescore` cannot drift
    from it."""
    reply = {
        "ok": True,
        "text": result["text"],
        "words": result.get("words"),
        "stats": _sanitize_floats(result["stats"]),
        "channel": req.get("channel"),
        "utc_start": req.get("utc_start"),
        "baseline_text": baseline_text,
        # Whether the unbiased pass actually RAN. `baseline_text: null` alone
        # cannot distinguish "we did not decode a baseline" from "the baseline
        # was empty", and a comparison UI that cannot tell those apart will
        # confidently show an empty column as though the baseline had failed.
        # On stage that reads as a rigged demo, which is worse than showing
        # nothing.
        "baseline_ran": run_compare,
        # Why this decode produced nothing, when it produced nothing on
        # PURPOSE (silence pre-check, junk filter, hallucination blocklist).
        # None on a normal decode. Surfaced so a dropped utterance is visible
        # in the host's logs instead of arriving as an unexplained empty
        # string with ok:true -- the exact shape of failure that hid the
        # long-form junk-filter bug for a full 24-minute meeting.
        "dropped_reason": result.get("dropped_reason"),
        # Decode confidence + alternatives (continual-personal-learning
        # setup 2). Lifted out of stats into top-level fields because they
        # are a CONTRACT a host parses, not a diagnostic it logs; stats keeps
        # its copy so an existing stats dump is unchanged. null = the engine
        # did not measure them (greedy path, gate drop, old engine).
        "avg_logprob": _finite_or_none((result.get("stats") or {}).get("avg_logprob")),
        "nbest": _wire_nbest((result.get("stats") or {}).get("nbest")),
    }
    # Only on an empty decode, so a reply with words keeps yesterday's keys.
    # The Mac's ending says which it was, and keeps the take for a gate.
    dropped = _dropped(result)
    if dropped is not None:
        reply["dropped"] = dropped
    return reply


# Admissible per-phrase delta, in nats added per matched token.
#
# MAX = 6 = the highest delta at which representative decodes stayed whole when this
# was measured, which was on whisper-base-qnn; 7 and 8 were observed to
# truncate two independent clips (the decoder emits the boosted phrase first
# and drops everything before it: "note-taking functionality" for a full
# sentence; delta=10 gave "addition of"). The research sweep went to 8 on a
# PyTorch decoder -- that ceiling does not transfer. A value the wire can
# carry is not the same as a value that is safe to decode with; the fence
# keeps the dial inside what has been measured to decode sanely HERE.
#
# The fence is GLOBAL, like the engine's scalar delta: it is applied to
# whichever checkpoint `ResolveEffectiveDirName` resolved, and on a normal
# install that is whisper-large-v3-turbo-qnn, not the base the ceiling was
# measured on. Nothing keys this bound to the loaded model, and naming base
# here is provenance, not scope. The truncation ceiling on turbo is UNMEASURED;
# treat 6 as the bound we can defend, not as turbo's bound.
#
# MIN = -10. A negative delta SUPPRESSES a phrase. Nothing has been measured
# there, but nothing has been observed to break either; the fence keeps it
# symmetric-ish with the old headroom until someone sweeps it.
#
# Why a fence at all: a host sending more (a future "confidence dial" with a
# bad scale, or 1e300 -- which System.Text.Json serialises happily) used to
# get the abyss -- on the real NPU, inf and 1e300 both collapsed the
# transcript to a single token and put `"score": Infinity` on the wire, which
# JsonDocument.Parse rejects, losing the whole reply in the client (verifier
# finding F1). Non-finite values have no sane clamp and are dropped. The C#
# client clamps to the SAME range before serialising
# (NotesWorkerClient.PhraseDeltaMin/Max) -- two fences for one fact, because
# either side may be the older one.
PHRASE_DELTA_MIN = -10.0
PHRASE_DELTA_MAX = 6.0


def _remap_phrase_deltas(raw, request_phrases: list[str],
                         merged: list[str]) -> tuple[dict[int, float] | None, int]:
    """Host-indexed {phrase index -> delta} to merged-list-indexed.

    Returns (mapping-or-None, rejected_count). Tolerant on purpose: JSON
    object keys are strings, a hand-written client may send ints, and a host
    whose vocabulary changed under it may send an index past the end.
    Unparseable, out-of-range-index or non-finite entries are skipped and
    counted, never raised -- a bad delta map must not cost the transcript.
    Finite values outside [PHRASE_DELTA_MIN, PHRASE_DELTA_MAX] are clamped to
    the edge (and counted). The mapping is None (not {}) when nothing
    survives so the caller's truthiness check keeps the uniform path.
    """
    if not raw or not isinstance(raw, dict):
        return None, 0
    out: dict[int, float] = {}
    rejected = 0
    for key, value in raw.items():
        try:
            i = int(key)
            delta = float(value)
        except (TypeError, ValueError):
            rejected += 1
            continue
        if not 0 <= i < len(request_phrases) or not math.isfinite(delta):
            rejected += 1
            continue
        if not PHRASE_DELTA_MIN <= delta <= PHRASE_DELTA_MAX:
            rejected += 1
            delta = min(max(delta, PHRASE_DELTA_MIN), PHRASE_DELTA_MAX)
        # Case-insensitive fallback: a host phrase that differs from a learned
        # word only in case was folded into the learned word's slot.
        j = _slot(merged, request_phrases[i])
        if j is None:  # a non-string phrase, dropped from merged
            rejected += 1
            continue
        # A phrase the host listed twice maps both indices onto one merged
        # slot; the larger delta wins, matching the trie's own max-over-phrases
        # rule for shared nodes.
        out[j] = max(delta, out.get(j, delta))
    return (out or None), rejected


def _finite_or_none(value) -> float | None:
    """A wire number must be a JSON number. Python's json.dumps writes inf/nan
    as the bare literals `Infinity`/`NaN`, which are not JSON and which the
    C# host's JsonDocument.Parse rejects -- one such value loses the entire
    reply. null is the contract's "not measured", so that is what a
    non-finite measurement becomes."""
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    return f if math.isfinite(f) else None


def _sanitize_floats(obj):
    """Recursively map every non-finite float in a JSON-ish structure to None.

    The `stats` dict goes out as the engine produced it -- a diagnostic, not a
    contract -- but one non-finite float anywhere in it turns the WHOLE frame
    into non-JSON for the host (JsonDocument.Parse rejects `NaN`/`Infinity`),
    which loses the transcript too. The contract fields are sanitised one by
    one above; this is the defence-in-depth pass over everything else, so the
    frame is strict JSON regardless of what an engine emits. Other types are
    passed through untouched (json.dumps will still reject anything it cannot
    serialise, which is the right failure for a real bug).
    """
    if isinstance(obj, float):
        return obj if math.isfinite(obj) else None
    if isinstance(obj, dict):
        return {k: _sanitize_floats(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [_sanitize_floats(v) for v in obj]
    return obj


def _wire_nbest(nbest) -> list[dict] | None:
    """Whitelist the per-hypothesis fields that cross the wire.

    The engine's entries may carry internals (token ids, per-chunk detail);
    the host contract is text + the two numbers, and pinning the shape here
    means an engine growing a field does not silently widen the protocol.
    """
    if not nbest:
        return None
    return [{"text": h.get("text", ""), "score": _finite_or_none(h.get("score")),
             "avg_logprob": _finite_or_none(h.get("avg_logprob"))} for h in nbest]


def _wire_fired(fired, merged: list[str], request_phrases: list[str]):
    """Engine `fired` entries (merged-index `phrase_id` + phrase text) to the
    wire shape: ``{"i": host index into THIS request's bias_phrases or null
    (a learned word the host did not send), "phrase", "pos", "n"}``. None
    passes through as null ("not measured")."""
    if fired is None:
        return None
    out = []
    for hit in fired:
        pid = hit.get("phrase_id")
        phrase = hit.get("phrase")
        if phrase is None and pid is not None and 0 <= pid < len(merged):
            phrase = merged[pid]
        # A learned word the host also sent, in any case, keeps its host index.
        host_index = _slot(request_phrases, phrase) if phrase is not None else None
        out.append({"i": host_index, "phrase": phrase,
                    "pos": hit.get("pos"), "n": hit.get("n")})
    return out


def _encoder_id(pcm_bytes: bytes, language, client: str = DEFAULT_CLIENT) -> str:
    """``<client>:enc:<sha1(pcm)[:16]>:<language>`` -- content-addressed so
    the same clip sent twice names the same entry; ``auto`` when no language
    was pinned (the decode then chose one, and a `score` must pin its own).
    The client prefix is the cache namespace (capacity per client, and a
    client can only release its own)."""
    import hashlib

    return f"{client}:enc:{hashlib.sha1(pcm_bytes).hexdigest()[:16]}:{language or 'auto'}"


def _clip_audio(clip: dict, op: str):
    """Decode a ``{"pcm_b64", "sample_rate"}`` clip. Returns
    ``(audio float32, pcm bytes)`` or an error reply dict."""
    import numpy as np

    if clip.get("sample_rate", 16000) != 16000:
        return {"ok": False, "error": f"{op}: expected 16kHz PCM"}
    try:
        raw = base64.b64decode(clip.get("pcm_b64") or "")
    except Exception:  # noqa: BLE001
        return {"ok": False, "error": f"{op}: expected 16kHz PCM"}
    pcm = np.frombuffer(raw, dtype=np.int16)
    if len(pcm) == 0:
        return {"ok": False, "error": f"{op}: empty audio"}
    return pcm.astype(np.float32) / 32768.0, pcm.tobytes()


def _resolve_clip(req: dict, op: str, keep_default: bool):
    """The cross-KV for a `score`/`rescore` request: from the cache by id, or
    by encoding the sent audio (cached under its id when asked). Returns
    ``(cross_kv, encoder_id, encoder_ms, cached)`` or an error reply."""
    clip = req.get("clip") if isinstance(req.get("clip"), dict) else {}
    src = clip if clip else req
    pcm_b64, encoder_id = src.get("pcm_b64"), src.get("encoder_id")
    # Presence, not truthiness: an EMPTY pcm_b64 is a clip that was sent
    # (and is reported as "empty audio"), not a clip that was omitted.
    if (pcm_b64 is not None) == (encoder_id is not None):
        return {"ok": False, "error": f"{op}: clip needs exactly one of pcm_b64 or encoder_id"}
    client = _client(req)
    if encoder_id:
        encoder_id, foreign = _qualify_encoder_id(client, encoder_id)
        if foreign:
            return {"ok": False, "reason": "foreign",
                    "error": f"{op}: encoder_id belongs to another client"}
        cross_kv = _engine.encoder_cache_get(encoder_id)
        if cross_kv is None:
            return {"ok": False, "error": f"{op}: encoder_id not cached"}
        return cross_kv, encoder_id, 0.0, True
    decoded = _clip_audio(clip or {"pcm_b64": pcm_b64, "sample_rate": req.get("sample_rate", 16000)}, op)
    if isinstance(decoded, dict):
        return decoded
    audio, pcm_bytes = decoded
    try:
        cross_kv, enc_ms = _engine.encode_clip(audio)
    except ValueError as exc:
        return {"ok": False, "error": f"{op}: {exc}"}
    encoder_id = _encoder_id(pcm_bytes, req.get("language"), client)
    if bool(req.get("keep_encoder", keep_default)):
        _engine.encoder_cache_put(encoder_id, cross_kv)
    return cross_kv, encoder_id, enc_ms, False


MAX_DISTRACTORS = 8
MAX_RESCORE_PHRASES = 8


def op_score(req: dict) -> dict:
    """Forced scoring -- see the module docstring and the scoring contract 2."""
    import time

    if _engine is None:
        return {"ok": False, "error": "not initialized"}
    if not hasattr(_engine, "forced_score"):
        return {"ok": False, "error": "score: engine does not support forced scoring"}
    language = req.get("language")
    if not language:
        return {"ok": False, "error": "score: language required"}
    if _engine.lang_token_id(language) is None:
        return {"ok": False, "error": "score: unknown language"}
    lam = req.get("lambda", 0.0)
    try:
        lam = float(lam)
    except (TypeError, ValueError):
        return {"ok": False, "error": "score: lambda out of range"}
    if not (math.isfinite(lam) and 0.0 <= lam <= 1.0):
        return {"ok": False, "error": "score: lambda out of range"}
    if lam > 0.0:
        # The x0 (zeroed-encoder) pass has no equivalent on a cross-KV graph
        # until the per-layer v_proj biases are exported (the scoring contract 2.5).
        # E1 chose lambda = 0, so this is refused rather than approximated.
        return {"ok": False, "error": "score: lambda > 0 not available (x0 pass unresolved)"}
    distractors = list(req.get("distractors") or [])
    if len(distractors) > MAX_DISTRACTORS:
        return {"ok": False, "error": "score: too many distractors"}
    orig, corr = str(req.get("orig") or ""), str(req.get("corr") or "")
    with _engine_lock().held(tag=f"{_client(req)}:score") as waited_ms:
        resolved = _resolve_clip(req, "score", keep_default=True)
        if isinstance(resolved, dict):
            return resolved
        cross_kv, encoder_id, enc_ms, cached = resolved
        t0 = time.perf_counter()
        try:
            body = _engine.forced_score(cross_kv, language, orig, corr, distractors)
        except ValueError as exc:
            return {"ok": False, "error": f"score: {exc}"}
        wall_ms = (time.perf_counter() - t0) * 1000
    timing = dict(body.pop("timing_ms", {}))
    timing.update({"encoder": enc_ms, "encoder_cached": cached,
                   "decoder": timing.get("decoder", wall_ms),
                   "waited": round(waited_ms, 2)})
    reply = {"ok": True, "encoder_id": encoder_id, **body, "timing_ms": timing,
             "tag": req.get("tag")}
    return _sanitize_floats(reply)


def op_score_release(req: dict) -> dict:
    if _engine is None:
        return {"ok": False, "error": "not initialized"}
    encoder_id, foreign = _qualify_encoder_id(_client(req), req.get("encoder_id"))
    if foreign:
        # Another client's entry: a no-op, reported as such. Dictation must
        # not be able to evict a notes entry or vice versa.
        return {"ok": False, "reason": "foreign", "released": False,
                "error": "score_release: encoder_id belongs to another client"}
    released = bool(hasattr(_engine, "encoder_cache_release")
                    and _engine.encoder_cache_release(encoder_id))
    return {"ok": True, "released": released}


def op_rescore(req: dict) -> dict:
    """Second pass with a small boosted set -- the scoring contract 3."""
    import time

    if _engine is None:
        return {"ok": False, "error": "not initialized"}
    if not hasattr(_engine, "rescore"):
        return {"ok": False, "error": "rescore: engine does not support rescore"}
    phrases = list(req.get("bias_phrases") or [])
    if not 1 <= len(phrases) <= MAX_RESCORE_PHRASES:
        return {"ok": False, "error": "rescore: bias_phrases required (1..8)"}
    beam_size = int(req.get("beam_size", 4))
    if beam_size <= 1:
        return {"ok": False, "error": "rescore: beam_size must be > 1"}
    language = req.get("language")
    if not language:
        return {"ok": False, "error": "rescore: language required"}
    # The caller's list IS the merged list here (no init defaults and no
    # learned words: a probe must score exactly the phrases it names), so the
    # host's indices are the trie's; the clamp/reject rules are transcribe's.
    phrase_deltas, delta_rejects = _remap_phrase_deltas(req.get("phrase_deltas"), phrases, phrases)
    kwargs = {"beam_size": beam_size, "phrase_deltas": phrase_deltas}
    if req.get("completion_bonus") is not None:
        kwargs["completion_bonus"] = float(req["completion_bonus"])
    if req.get("delta") is not None:
        kwargs["delta"] = float(req["delta"])
    with _engine_lock().held(tag=f"{_client(req)}:rescore") as waited_ms:
        resolved = _resolve_clip(req, "rescore", keep_default=False)
        if isinstance(resolved, dict):
            return resolved
        cross_kv, encoder_id, enc_ms, cached = resolved
        t0 = time.perf_counter()
        result = _engine.rescore(cross_kv, language, phrases, **kwargs)
        dec_ms = (time.perf_counter() - t0) * 1000
    stats = result.setdefault("stats", {})
    stats["waited_ms"] = round(waited_ms, 2)
    if delta_rejects:
        stats["phrase_deltas_rejected"] = delta_rejects
    if _text_passes(req):
        _apply_text_passes(result, phrases)
    reply = _transcribe_reply(result, req, None, False)
    reply["fired"] = _wire_fired(stats.get("fired"), phrases, phrases)
    if reply["nbest"] is not None:
        for hyp, src in zip(reply["nbest"], stats.get("nbest") or []):
            hyp["fired"] = _wire_fired(src.get("fired"), phrases, phrases)
    reply["encoder_id"] = encoder_id
    reply["encoder_reused"] = cached
    reply["timing_ms"] = {"encoder": enc_ms, "encoder_cached": cached,
                          "decoder": stats.get("dec_ms", dec_ms),
                          "tokens": stats.get("tokens")}
    reply["tag"] = req.get("tag")
    return _sanitize_floats(reply)


_OPS = {"ping": op_ping, "init": op_init, "transcribe": op_transcribe,
        # loop-v2 P6 learning ops. Registered unconditionally -- each checks
        # the engine's capability at call time and answers an error string,
        # so a backend without forced scoring (mac, stub) degrades to "op
        # unavailable", never to a crash.
        "score": op_score, "score_release": op_score_release, "rescore": op_rescore}

# Live preview is part of dictation. A missing module is a packaging error.
from . import ops_stream
_OPS.update(ops_stream.OPS)


def main() -> None:
    stdin = sys.stdin.buffer
    stdout = _reserve_protocol_stdout()

    while True:
        # A frame that is not a JSON object is answered, not fatal: the
        # header says "never dies on a bad request", and this read used to sit
        # outside every handler, so one undecodable payload or a JSON array
        # ended the process. The length prefix is honoured before decoding,
        # so the next frame is still in step. The reply names only the error
        # type: a payload can carry audio or the user's vocabulary.
        payload = _read_payload(stdin)
        if payload is None:
            break
        # Every exception the decode raises is this one frame's, so all of
        # them are answered. Catching ValueError alone (JSONDecodeError,
        # UnicodeDecodeError) still let a deeply nested array end the process:
        # json.loads raises RecursionError for it, which is not a ValueError.
        # A failed read above stays fatal, because the stream is then out of step.
        try:
            req = _decode_frame(payload)
        except Exception as e:  # noqa: BLE001 -- see above
            _write_frame(stdout, {"ok": False, "error": f"malformed frame: {type(e).__name__}"})
            continue
        if not isinstance(req, dict):
            _write_frame(stdout, {"ok": False,
                                  "error": f"malformed frame: a JSON {type(req).__name__}, not an object"})
            continue
        op = req.get("op")
        if op == "shutdown":
            _write_frame(stdout, {"ok": True, "op": "shutdown"})
            break
        handler = _OPS.get(op)
        if handler is None:
            _write_frame(stdout, {"ok": False, "error": f"unknown op: {op}"})
            continue
        try:
            _write_frame(stdout, handler(req))
        except Exception as e:  # keep serving; the host decides whether to restart
            traceback.print_exc(file=sys.stderr)
            _write_frame(stdout, {"ok": False, "error": f"{type(e).__name__}: {e}"})


if __name__ == "__main__":
    if __package__ is None or __package__ == "":
        # allow `python transcription_worker/asr_server.py` for dev
        from pathlib import Path

        sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
        __package__ = "transcription_worker"
    main()
