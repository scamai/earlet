"""Deterministic concurrency contracts for the turn decode scheduler."""

import threading

import numpy as np

from transcription_worker import ops_stream_whisper as osw


class _Engine:
    def __init__(self):
        self.calls = []
        self.second_call = threading.Event()

    def transcribe(self, audio, **_kwargs):
        self.calls.append(len(audio))
        if len(self.calls) == 2:
            self.second_call.set()
        return {"text": "decoded"}


class _Sys:
    def __init__(self):
        self.history = np.zeros(osw.SAMPLE_RATE, dtype=np.float32)
        self.engine = _Engine()
        self.language = None
        self.lock = threading.Lock()


def _session():
    session = object.__new__(osw._Session)
    session.sys = _Sys()
    session._turn_texts = {}
    session._turn_decode_q = []
    session._turn_decode_inflight = set()
    session._turn_decode_lock = threading.Lock()
    session._turn_decode_thread = None
    session._turn_bucket_texts = {}
    session._turn_enq_at = {}
    session._turn_decoded_buckets = set()
    session._turn_decoded_starts = {}
    session._turn_bounds_seen = {}
    session._turn_ask_first = {}
    session._turn_text_patience_s = 4.0
    session._hold = None
    return session


def test_worker_restarts_after_return_before_old_thread_exits():
    session = _session()
    returned = threading.Event()
    release = threading.Event()
    real_worker = osw._Session._turn_decode_worker
    worker_threads = []

    def wrapped_worker():
        worker_threads.append(threading.current_thread())
        real_worker(session)
        returned.set()
        release.wait(timeout=5.0)

    session._turn_decode_worker = wrapped_worker
    try:
        session._request_turn_decode(0.0, 0.5)
        assert returned.wait(timeout=5.0)
        first_thread = worker_threads[0]
        assert first_thread.is_alive()

        # The first worker has returned, but its outer target is still alive.
        # A correct empty-queue transition clears the stale thread slot,
        # allowing this request to start a replacement worker immediately.
        session._request_turn_decode(0.5, 1.0)
        assert session.sys.engine.second_call.wait(timeout=5.0)
        assert len(session.sys.engine.calls) == 2
    finally:
        release.set()
        for thread in worker_threads:
            thread.join(timeout=5.0)
        assert worker_threads and all(not thread.is_alive() for thread in worker_threads)


class _TrackingLock:
    def __init__(self):
        self._lock = threading.RLock()
        self.held = False

    def __enter__(self):
        self._lock.acquire()
        self.held = True
        return self

    def __exit__(self, *args):
        self.held = False
        self._lock.release()


class _LockedDict(dict):
    def __init__(self, lock, *args, **kwargs):
        self.lock = lock
        super().__init__(*args, **kwargs)

    def items(self):
        assert self.lock.held, "bucket snapshot must be read under decode lock"
        return super().items()

    def pop(self, key, default=None):
        assert self.lock.held, "bucket snapshot must be pruned under decode lock"
        return super().pop(key, default)


def test_bounds_prune_reads_and_writes_bucket_cache_under_decode_lock():
    session = _session()
    lock = _TrackingLock()
    session._turn_decode_lock = lock
    session._turn_bucket_texts = _LockedDict(lock, {
        1.0: (0.0, 1.0, "stale"),
        2.0: (0.0, 700.0, "fresh"),
    })
    session._turn_bounds_seen = {float(i): [0.0, 1.0, 0] for i in range(513)}
    session._hold = type("Hold", (), {"_released_until": 1000.0})()
    session._turn_decode_thread = type("Alive", (), {"is_alive": lambda self: True})()

    session._decode_when_settled(512.0, 513.0, 1000.0)

    assert 1.0 not in session._turn_bucket_texts
    assert 2.0 in session._turn_bucket_texts
