"""Wire-protocol contract for the shared speech worker (P0, Linux CI).

Spawns the REAL ``asr_server`` as a subprocess and drives it over the
length-prefixed frame protocol, asserting the contract the C# host relies on.
This is the test that would have caught the 2026-09-09 field incident: a host
speaking the framed/named-pipe protocol against a worker build that spoke the
old one produced a 4-minute SILENT pipe-connect hang. Every read here is
deadline-bounded, so a worker that stops answering fails this test in
seconds with a named error instead of hanging.

No NPU, no model download: ``backend="stub"`` (StubEngine). Runs anywhere
pytest runs. The Windows named-pipe reply path (``DICTATION_ASR_PROTOCOL_PIPE``)
is exercised on-device; here the worker uses its stdout-fallback, which
exercises the same frame codec, op dispatch and stdout-pollution guard.
"""

import json
import os
import queue
import struct
import subprocess
import sys
import threading
import time
import weakref
from pathlib import Path

import pytest

# The runtime directory — the dir that must be on sys.path for `-m
# transcription_worker.asr_server` to resolve. This file lives one level down.
_NATIVE_DIR = Path(__file__).resolve().parent.parent
_READ_TIMEOUT_S = 20.0


class _PipeReader:
    """Continuously drain a subprocess pipe without socket-only select()."""

    _EOF = object()

    def __init__(self, fileobj):
        self._fileobj_ref = weakref.ref(fileobj)
        self._chunks = queue.Queue()
        self._pending = bytearray()
        self._eof = False
        self._thread = threading.Thread(
            target=self._run, name="protocol-pipe-reader", daemon=True)
        self._thread.start()

    def _run(self):
        try:
            while True:
                fileobj = self._fileobj_ref()
                if fileobj is None:
                    self._chunks.put(self._EOF)
                    return
                chunk = fileobj.read(64 * 1024)
                if not chunk:
                    self._chunks.put(self._EOF)
                    return
                self._chunks.put(chunk)
        except Exception as exc:
            self._chunks.put(exc)

    def read(self, deadline: float) -> bytes:
        if self._pending:
            chunk = bytes(self._pending)
            self._pending.clear()
            return chunk
        if self._eof:
            return b""
        try:
            item = self._chunks.get(timeout=max(0.0, deadline - time.monotonic()))
        except queue.Empty:
            raise TimeoutError from None
        if item is self._EOF:
            self._eof = True
            return b""
        if isinstance(item, Exception):
            raise AssertionError(f"worker pipe read failed: {item}") from item
        return item

    def unread(self, chunk: bytes) -> None:
        self._pending[:0] = chunk

    def join(self, timeout: float = 5.0) -> None:
        self._thread.join(timeout=timeout)
        assert not self._thread.is_alive(), "protocol pipe reader did not stop"


_PIPE_READERS = weakref.WeakKeyDictionary()


def _pipe_reader(fileobj) -> _PipeReader:
    reader = _PIPE_READERS.get(fileobj)
    if reader is None:
        reader = _PipeReader(fileobj)
        _PIPE_READERS[fileobj] = reader
    return reader


def _close_pipe_reader(fileobj) -> None:
    reader = _PIPE_READERS.pop(fileobj, None)
    if reader is not None:
        reader.join()


def _read_exact(fileobj, n: int) -> bytes:
    """Read exactly n bytes under one deadline, including on Windows pipes."""
    buf = bytearray()
    deadline = time.monotonic() + _READ_TIMEOUT_S
    reader = _pipe_reader(fileobj)
    while len(buf) < n:
        try:
            chunk = reader.read(deadline)
        except TimeoutError:
            raise AssertionError(
                f"worker sent no reply within {_READ_TIMEOUT_S}s "
                f"(got {len(buf)}/{n} bytes) — the silent-hang this test guards"
            ) from None
        if chunk == b"":
            raise AssertionError(
                f"worker closed the pipe mid-frame (got {len(buf)}/{n} bytes)")
        needed = n - len(buf)
        buf.extend(chunk[:needed])
        if len(chunk) > needed:
            reader.unread(chunk[needed:])
    return bytes(buf)


def _read_frame(fileobj) -> dict:
    (length,) = struct.unpack(">I", _read_exact(fileobj, 4))
    assert 0 < length < 64 * 1024 * 1024, f"implausible frame length {length}"
    return json.loads(_read_exact(fileobj, length).decode("utf-8"))


def _write_frame(fileobj, msg: dict) -> None:
    payload = json.dumps(msg).encode("utf-8")
    fileobj.write(struct.pack(">I", len(payload)))
    fileobj.write(payload)
    fileobj.flush()


@pytest.fixture()
def worker():
    env = dict(os.environ)
    env["PYTHONUNBUFFERED"] = "1"
    env["PYTHONPATH"] = str(_NATIVE_DIR) + os.pathsep + env.get("PYTHONPATH", "")
    # No DICTATION_ASR_PROTOCOL_PIPE => stdout-fallback reply path.
    env.pop("DICTATION_ASR_PROTOCOL_PIPE", None)
    proc = subprocess.Popen(
        [sys.executable, "-m", "transcription_worker.asr_server"],
        cwd=str(_NATIVE_DIR),
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, bufsize=0, env=env)
    try:
        yield proc
    finally:
        try:
            if proc.poll() is None:
                _write_frame(proc.stdin, {"op": "shutdown"})
        except Exception:
            pass
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()
            proc.wait(timeout=5)
        for stream in (proc.stdin, proc.stdout):
            try:
                stream.close()
            except Exception:
                pass
        _close_pipe_reader(proc.stdout)


def test_pipe_reader_does_not_require_socket_select(monkeypatch):
    """The old implementation errored on Windows' socket-only select()."""
    import select

    def windows_pipe_select(*_args, **_kwargs):
        raise OSError(
            10038,
            "an operation was attempted on something that is not a socket",
        )

    monkeypatch.setattr(select, "select", windows_pipe_select)
    read_fd, write_fd = os.pipe()
    read_file = os.fdopen(read_fd, "rb", buffering=0)
    write_file = os.fdopen(write_fd, "wb", buffering=0)
    payload = b"select-free"
    try:
        write_file.write(payload)
        write_file.close()
        assert _read_exact(read_file, len(payload)) == payload
    finally:
        read_file.close()
        _close_pipe_reader(read_file)
        assert read_file not in _PIPE_READERS


def test_pipe_reader_keeps_eof_and_can_be_reaped():
    read_fd, write_fd = os.pipe()
    read_file = os.fdopen(read_fd, "rb", buffering=0)
    write_file = os.fdopen(write_fd, "wb", buffering=0)
    try:
        write_file.close()
        reader = _pipe_reader(read_file)
        assert reader.read(time.monotonic() + 2.0) == b""
        assert reader.read(time.monotonic() + 2.0) == b""
        reader.join()
        assert not reader._thread.is_alive()
    finally:
        read_file.close()
        _close_pipe_reader(read_file)
        assert read_file not in _PIPE_READERS


def test_real_worker_with_no_output_times_out(worker, monkeypatch):
    monkeypatch.setattr(sys.modules[__name__], "_READ_TIMEOUT_S", 0.05)
    with pytest.raises(AssertionError, match=r"no reply within 0\.05s"):
        _read_frame(worker.stdout)


def test_real_process_exit_is_reported_as_eof():
    process = subprocess.Popen(
        [sys.executable, "-c", "pass"], stdout=subprocess.PIPE, bufsize=0)
    try:
        with pytest.raises(AssertionError, match="closed the pipe mid-frame"):
            _read_exact(process.stdout, 4)
        assert process.wait(timeout=10) == 0
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=10)
        process.stdout.close()
        _close_pipe_reader(process.stdout)


def test_ping_carries_protocol_version(worker):
    from transcription_worker.asr_server import PROTOCOL_VERSION
    _write_frame(worker.stdin, {"op": "ping"})
    reply = _read_frame(worker.stdout)
    assert reply["ok"] is True
    assert reply["op"] == "ping"
    # The handshake field: absent => a host must refuse to proceed.
    assert reply["protocol"] == PROTOCOL_VERSION


def test_init_stub_roundtrip(worker):
    from transcription_worker.asr_server import PROTOCOL_VERSION
    _write_frame(worker.stdin, {"op": "init", "backend": "stub"})
    reply = _read_frame(worker.stdout)
    assert reply["ok"] is True, reply
    assert reply["backend"] == "stub"
    assert reply["on_npu"] is False
    assert reply["protocol"] == PROTOCOL_VERSION


def test_unknown_op_is_named_not_fatal(worker):
    # An unknown op must return a named error AND keep serving — the host,
    # not the worker, decides whether to restart.
    _write_frame(worker.stdin, {"op": "definitely-not-an-op"})
    reply = _read_frame(worker.stdout)
    assert reply["ok"] is False
    assert "unknown op" in reply["error"]
    # Still alive:
    _write_frame(worker.stdin, {"op": "ping"})
    assert _read_frame(worker.stdout)["op"] == "ping"


def test_frames_survive_worker_diagnostics(worker):
    # init imports engine/ops modules that print diagnostics; the pollution
    # guard must keep those off the frame stream. If a diagnostic leaked onto
    # the reply pipe, the length prefix would be garbage and _read_frame would
    # raise on the implausible-length assert.
    _write_frame(worker.stdin, {"op": "init", "backend": "stub"})
    assert _read_frame(worker.stdout)["ok"] is True
    for _ in range(3):
        _write_frame(worker.stdin, {"op": "ping"})
        assert _read_frame(worker.stdout)["protocol"] >= 2


def test_shutdown_is_acked_and_exits(worker):
    _write_frame(worker.stdin, {"op": "shutdown"})
    reply = _read_frame(worker.stdout)
    assert reply == {"ok": True, "op": "shutdown"}
    assert worker.wait(timeout=10) == 0


def test_protocol_version_is_pinned():
    # A bump is a breaking wire change: this line must move in the same commit
    # that changes the contract, forcing a reviewer to see it.
    from transcription_worker.asr_server import PROTOCOL_VERSION
    assert PROTOCOL_VERSION == 2
