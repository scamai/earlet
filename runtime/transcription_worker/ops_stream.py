"""Live ASR wire routing for the one selected Whisper model.

Dictation preview and meeting live captions are two independent sessions over
``ops_stream_whisper``.  They intentionally share the worker's initialized
Whisper model (Large v3 Turbo on the Windows product) and differ only by
``client`` and per-request context.

There is no automatic or product-selectable small streaming recognizer here.
If Whisper is unavailable, ``stream_start`` fails explicitly and the caller
renders/retries that state.  Returning text from a different model would make
the model audit trail and the product's quality promise false.

The wire remains one request / one reply: partials are pulled by
``stream_push`` and every stateful op requires the worker-issued ``stream_id``.
``ops_stream_whisper`` owns that registry and answers ``no_such_stream`` for a
missing, cancelled, finished, or stale id.
"""

from __future__ import annotations

# Host push cadence. Kept here because the protocol contract and its tests
# refer to the public router rather than the implementation module.
CHUNK_SECONDS = 0.16
SAMPLE_RATE = 16000

WHISPER_ENGINE = "whisper"


def _whisper():
    from . import ops_stream_whisper

    return ops_stream_whisper


def op_stream_start(req: dict) -> dict:
    requested = req.get("engine") or WHISPER_ENGINE
    if requested != WHISPER_ENGINE:
        return {
            "ok": False,
            "error": f"unsupported stream engine {requested!r}; whisper is required",
            "engine": requested,
        }
    # Live preview starts before the final transcribe loads learning.json.
    # Make a private request copy so the wire caller's dictionary stays intact;
    # notes streams retain their existing bias behavior exactly.
    request = req
    if req.get("client") == "dictation":
        from . import learned_vocabulary, spoken_aliases

        request = dict(req)
        learned = learned_vocabulary.load(spoken_aliases.learning_path(), scene=req.get("vocabulary_scene"))
        request["bias_phrases"] = learned_vocabulary.merge_words(
            learned.words, req.get("bias_phrases"))
    reply = _whisper().start(request)
    # Include the named engine on failure as well as success so the host log
    # never has to infer whether a different recognizer was attempted.
    reply.setdefault("engine", WHISPER_ENGINE)
    return reply


def op_stream_push(req: dict) -> dict:
    return _whisper().push(req)


def op_stream_finish(req: dict) -> dict:
    return _whisper().finish(req)


def op_stream_cancel(req: dict) -> dict:
    return _whisper().cancel(req)


def op_stream_release(req: dict) -> dict:
    """Report whether all live sessions are gone.

    The shared model itself belongs to the process-wide worker host and is not
    unloaded by one preview client's idle timer.  This op is retained for wire
    compatibility and truthful session bookkeeping.
    """
    active = _whisper().active()
    return {"ok": True, "released": active == 0, "active_streams": active}


OPS = {
    "stream_start": op_stream_start,
    "stream_push": op_stream_push,
    "stream_finish": op_stream_finish,
    "stream_cancel": op_stream_cancel,
    "stream_release": op_stream_release,
}
