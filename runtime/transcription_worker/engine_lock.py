"""The ONE engine lock every NPU decode in this process takes (plan
the worker contract §3, row "engine lock").

A single Whisper engine serves dictation holds, learning probes (`score` /
`rescore`), the notes finalize (`diarized_transcribe`) and the live-caption
decode threads (`ops_stream_whisper`). The Hexagon is one device, so these
must serialize -- and dictation must serialize FIRST: a hold is a user with
a key down waiting for text, a caption tick is a preview that loses nothing
by arriving one segment later.

Why this is its own module and not an attribute of `asr_server`: the worker
runs as `python -m transcription_worker.asr_server`, which makes the live
server module `__main__`; a `from . import asr_server` elsewhere imports a
SECOND copy (the dual-module trap `ops_diarized_transcript._get_engine`
documents). A lock that can exist twice is not a lock. This module is never
`__main__`, so every importer shares one object.

Semantics: a reentrant mutex (one thread may nest `score` inside a hold's
lock, as `op_rescore` -> `_resolve_clip` does) with two priority classes.
A waiter of class HOLD is admitted before any waiter of class CAPTION, no
matter the arrival order; within a class the order is whatever the
condition variable gives (fair enough at these request rates). A CAPTION
waiter never starves a HOLD, and a HOLD never waits longer than the
in-flight decode -- which `ops_stream_whisper.MAX_LIVE_DECODE_S` bounds.
"""

from __future__ import annotations

import contextlib
import threading
import time

#: Priority classes, lower is served first.
HOLD = 0        # transcribe / rescore / score / diarized_transcribe
CAPTION = 1     # low-latency live-caption decode threads
BACKGROUND = 2  # rolling meeting stabilization; may always wait


class EngineLock:
    def __init__(self) -> None:
        self._cv = threading.Condition()
        self._owner: int | None = None
        self._depth = 0
        self._waiting = {HOLD: 0, CAPTION: 0, BACKGROUND: 0}
        #: Evidence for the hang watchdog (field 2026-08-19: a stuck NPU call
        #: under the lock starved every channel and nothing logged).
        self.holder: dict = {"tag": None, "since": 0.0}

    def acquire(self, priority: int = HOLD, tag: str | None = None) -> float:
        """Block until held; returns the wait in milliseconds."""
        me = threading.get_ident()
        t0 = time.perf_counter()
        with self._cv:
            if self._owner == me:
                self._depth += 1
                return 0.0
            self._waiting[priority] += 1
            try:
                while self._owner is not None or any(
                        self._waiting[p] for p in range(priority)):
                    self._cv.wait()
            finally:
                self._waiting[priority] -= 1
            self._owner = me
            self._depth = 1
            self.holder["tag"] = tag
            self.holder["since"] = time.monotonic()
        return (time.perf_counter() - t0) * 1000.0

    def release(self) -> None:
        with self._cv:
            if self._owner != threading.get_ident():
                raise RuntimeError("engine lock released by a non-owner")
            self._depth -= 1
            if self._depth == 0:
                self._owner = None
                self.holder["tag"] = None
                self._cv.notify_all()

    def waiting(self, priority: int) -> int:
        with self._cv:
            return self._waiting[priority]

    def yield_if_contended(self, priority: int = BACKGROUND,
                           tag: str | None = None) -> bool:
        """Cooperative preemption point for a long-running holder (the
        rolling stabilization pass, 2026-08-27): between decoder steps the
        engine's per-token state lives entirely in Python and the ORT
        sessions are stateless across calls, so a BACKGROUND window decode
        can safely hand the engine to a waiting CAPTION/HOLD request and
        resume afterwards. Without this, a ~30 s-window beam decode held
        the lock for its whole duration and live-caption commits queued
        behind it -- the measured bound on spoken->committed latency
        (AMI replay 2026-08-27: BACKGROUND occupied the engine ~68% and a
        caption wait behind one hold reached ~8 s device-scale).

        Yields only when (a) the CALLER currently owns the lock at depth 1
        (never inside a reentrant nest -- an inner release would tear the
        outer critical section), and (b) a STRICTLY higher class is
        waiting. Returns True after re-acquiring (per-token Python state is
        structurally untouched; only wall time passed); False = kept the
        lock, nothing happened."""
        me = threading.get_ident()
        with self._cv:
            if self._owner != me or self._depth != 1:
                return False
            if not any(self._waiting[p] for p in range(priority)):
                return False
        self.release()
        self.acquire(priority, tag)
        return True

    @contextlib.contextmanager
    def held(self, priority: int = HOLD, tag: str | None = None):
        """``with lock.held(HOLD, "transcribe") as waited_ms:``"""
        waited = self.acquire(priority, tag)
        try:
            yield waited
        finally:
            self.release()


#: The process-wide instance. `asr_server`, `ops_stream_whisper` and
#: `ops_diarized_transcript` all import THIS name.
ENGINE_LOCK = EngineLock()
