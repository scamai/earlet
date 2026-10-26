"""Bounded scheduling for the Dictation macOS MLX live preview."""

import numpy as np

from transcription_worker import ops_stream_whisper as osw


class Engine:
    def __init__(self, name):
        self._engine_name = name


def channel(engine=None, fixed_label="You", tag="mic"):
    return osw._ChannelStream(
        engine or Engine("mlx"), osw._CommitLog(), None,
        fixed_label=fixed_label, channel_tag=tag)


def test_mlx_dictation_paces_slow_preview_jobs(monkeypatch):
    now = [100.0]
    monkeypatch.setattr(osw.time, "monotonic", lambda: now[0])
    ch = channel()
    ch.configure_preview_pacing(True)
    spawned = []
    ch._spawn = spawned.append
    with ch.lock:
        ch.pending_boundaries.append(("You", 1.0, 0.0))
    ch.push(np.zeros(160, dtype=np.float32))
    assert len(spawned) == 1
    assert ch._preview_job_started == 100.0

    now[0] += 1.5
    ch._finish_job()
    assert ch._preview_next_start == 106.0  # max(2, 3 * 1.5)

    with ch.lock:
        ch.pending_boundaries.append(("You", 2.0, 1.2))
    ch.push(np.zeros(160, dtype=np.float32))
    assert len([job for job in spawned if job is not None]) == 1
    now[0] = 106.0
    ch.push(np.zeros(160, dtype=np.float32))
    assert len([job for job in spawned if job is not None]) == 2


def test_mic_preview_boundaries_coalesce_to_latest_cut_and_earliest_start():
    ch = channel()
    ch.configure_preview_pacing(True)
    with ch.lock:
        ch._queue_boundary_locked(("You", 2.0, 1.5))
        ch._queue_boundary_locked(("You", 4.0, 3.0))
        ch._queue_boundary_locked(("You", 5.0, 1.0))
        assert ch.pending_boundaries == [("You", 5.0, 1.0)]


def test_notes_and_non_mlx_keep_existing_scheduler():
    for client, engine_name in (("notes", "mlx"),
                                ("dictation", "faster"),
                                ("dictation", "openai")):
        mic = channel(Engine(engine_name))
        sys = channel(Engine(engine_name), fixed_label=None, tag="sys")
        session = object.__new__(osw._Session)
        session.client, session.mic, session.sys = client, mic, sys
        session.configure_preview_pacing()
        assert not mic._preview_pacing
        assert not sys._preview_pacing
        with mic.lock:
            mic._queue_boundary_locked(("You", 2.0, 1.0))
            mic._queue_boundary_locked(("You", 3.0, 2.0))
            assert len(mic.pending_boundaries) == 2


def test_cancelled_preview_cannot_chain_a_deferred_job(monkeypatch):
    now = [10.0]
    monkeypatch.setattr(osw.time, "monotonic", lambda: now[0])
    ch = channel()
    ch.configure_preview_pacing(True)
    spawned = []
    ch._spawn = spawned.append
    with ch.lock:
        ch.pending_boundaries.append(("You", 1.0, 0.0))
    ch.push(np.zeros(160, dtype=np.float32))
    with ch.lock:
        ch.torn_down = True
        ch.pending_boundaries.append(("You", 2.0, 1.0))
    now[0] += 10.0
    ch._finish_job()
    assert len([job for job in spawned if job is not None]) == 1


def test_finish_does_not_wait_for_preview_cooldown():
    class Transcriber(Engine):
        def __init__(self):
            super().__init__("mlx")
            self.samples = []

        def transcribe(self, audio, **kwargs):
            self.samples.append(len(audio))
            return {"text": "The complete spoken sentence.", "stats": {"language": "en"}}

    engine = Transcriber()
    ch = channel(engine)
    ch.configure_preview_pacing(True)
    ch._preview_next_start = float("inf")
    ch.push(np.full(32000, .1, dtype=np.float32))
    assert not ch.decoding
    ch.finish()
    assert ch.closed
    assert sum(engine.samples) == 32000
    assert any("complete spoken sentence" in text for text in ch.commits.snapshot())


def test_wire_start_enables_budget_for_the_actual_mlx_dictation_session(monkeypatch):
    monkeypatch.setattr(osw, "_get_engine", lambda: Engine("mlx"))
    monkeypatch.setattr(osw, "_make_embed_fn", lambda: None)
    monkeypatch.setattr(osw, "_live_diarization_info", lambda session: {})
    reply = osw.start({"client": "dictation"})
    assert reply["ok"]
    try:
        assert osw._sessions[reply["stream_id"]].mic._preview_pacing
    finally:
        assert osw.cancel({"stream_id": reply["stream_id"]})["ok"]



class RecordingEngine(Engine):
    """Records the keyword arguments of every live decode."""

    def __init__(self, name="mlx"):
        super().__init__(name)
        self.calls = []

    def transcribe(self, audio, language=None, initial_prompt=None,
                   personalize=True, temperature=None, **kwargs):
        self.calls.append({"temperature": temperature, "personalize": personalize})
        return {"text": "a caption", "stats": {"language": "en"}}


def test_paced_preview_decodes_once_at_temperature_zero():
    engine = RecordingEngine()
    ch = channel(engine)
    ch.configure_preview_pacing(True)
    ch._transcribe2(np.full(32000, .1, dtype=np.float32))
    assert engine.calls == [{"temperature": 0.0, "personalize": False}]


def test_unpaced_channel_keeps_the_engine_temperature_ladder():
    engine = RecordingEngine()
    ch = channel(engine)
    ch._transcribe2(np.full(32000, .1, dtype=np.float32))
    assert engine.calls == [{"temperature": None, "personalize": False}]


def test_engine_without_a_temperature_parameter_gets_the_legacy_call():
    class Legacy(Engine):
        def __init__(self):
            super().__init__("mlx")
            self.kwargs = []

        def transcribe(self, audio, language=None, initial_prompt=None):
            self.kwargs.append(sorted(k for k, v in (("language", language),
                                                     ("initial_prompt", initial_prompt))))
            return {"text": "x", "stats": {}}

    engine = Legacy()
    ch = channel(engine)
    ch.configure_preview_pacing(True)
    ch._transcribe2(np.full(32000, .1, dtype=np.float32))
    assert len(engine.kwargs) == 1


def _decode_jobs_after(ch, seconds):
    spawned = []
    ch._spawn = lambda job: spawned.append(job) if job is not None else None
    chunk = np.full(1600, .1, dtype=np.float32)  # 100 ms of audible input
    for _ in range(int(seconds * 10)):
        ch.push(chunk)
    return [job for job in spawned if job[0] == "decode"]


def test_paced_preview_waits_for_a_window_worth_decoding(monkeypatch):
    monkeypatch.setattr(osw.time, "monotonic", lambda: 1000.0)
    ch = channel()
    ch.configure_preview_pacing(True)
    assert _decode_jobs_after(ch, 1.4) == []
    ch2 = channel()
    ch2.configure_preview_pacing(True)
    jobs = _decode_jobs_after(ch2, 1.6)
    assert len(jobs) == 1 and len(jobs[0][1]) >= osw.PREVIEW_MIN_WINDOW_S * osw.SAMPLE_RATE


def test_unpaced_channel_still_decodes_its_first_half_second(monkeypatch):
    monkeypatch.setattr(osw.time, "monotonic", lambda: 1000.0)
    jobs = _decode_jobs_after(channel(), 0.7)
    assert len(jobs) == 1


def test_paced_preview_shows_a_quiet_voice_and_notes_keep_the_floor():
    paced = channel()
    paced.configure_preview_pacing(True)
    assert paced.partial_gate.track_floor is False
    # A quiet voice from the first window on (RMS measured on the owner's
    # Mac holds: 0.004-0.008) is speech for the hold-to-talk preview...
    assert paced.partial_gate.note_window_rms(0.006) is False
    assert paced.partial_gate.note_window_rms(0.005) is False
    # ...while a channel that tracks its floor still treats it as the floor.
    notes = channel()
    assert notes.partial_gate.track_floor is True
    notes.partial_gate.note_window_rms(0.006)
    assert notes.partial_gate.note_window_rms(0.005) is True
    paced.configure_preview_pacing(False)
    assert paced.partial_gate.track_floor is True
