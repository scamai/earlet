"""ops_stream_whisper v2 (dual-channel + live speakers): pure-logic tests
with a fake engine and fake embedder (no NPU, no sherpa). The device run is
the integration gate; these pin buffering / turn-commit / speaker-labeling /
wire semantics."""

import base64
import time

import numpy as np
import pytest

from transcription_worker import ops_stream_whisper as osw
from transcription_worker import live_speakers


class FakeEngine:
    """Deterministic 'transcription': reports the clip length so tests can
    assert exactly which audio a decode saw."""

    def __init__(self, delay_s: float = 0.0):
        self.calls: list[int] = []
        self.delay_s = delay_s

    def transcribe(self, audio, **kw):
        self.calls.append(len(audio))
        if self.delay_s:
            time.sleep(self.delay_s)
        return {"text": f"seg{len(self.calls)}:{len(audio)}"}

    def transcribe_timed(self, audio, **kw):
        # The rolling channel refuses an engine without this at CONSTRUCTION
        # (TH-018 guard): a rolling-stable session over FakeEngine must build.
        # One whole-clip span, same deterministic text as transcribe().
        r = self.transcribe(audio, **kw)
        return {"spans": [(0.0, len(audio) / osw.SAMPLE_RATE, r["text"])]}


def loud(seconds: float, level: float = 0.1) -> np.ndarray:
    rng = np.random.default_rng(7)
    return (level * rng.standard_normal(int(seconds * osw.SAMPLE_RATE))
            ).astype(np.float32).clip(-1, 1)


def quiet(seconds: float) -> np.ndarray:
    return np.zeros(int(seconds * osw.SAMPLE_RATE), dtype=np.float32)


def wait_idle(ch, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with ch.lock:
            if not ch.decoding:
                return
        time.sleep(0.01)
    raise TimeoutError("decode thread never finished")


def drain(ch, timeout=5.0):
    """Wait for any in-flight background work on the channel."""
    time.sleep(0.05)
    wait_idle(ch, timeout)


def mean_voice_embed(audio):
    """Fake voice print: bucket by mean absolute level."""
    v = np.zeros(4, dtype=np.float32)
    v[int(float(np.abs(audio).mean()) * 10) % 4] = 1.0
    return v




def make_session(engine, embed=mean_voice_embed):
    return osw._Session(engine, embed)


def test_session_uses_request_declared_languages():
    s = osw._Session(FakeEngine(), None, languages=("zh", "fr"))
    s._watchdog_stop.set()
    assert s.sys.language_vote is s.mic.language_vote
    assert s.sys.language_vote.seed == {"zh", "fr"}


def test_single_declared_language_pins_both_live_channels():
    class LanguageEngine(FakeEngine):
        def __init__(self):
            super().__init__()
            self.languages = []

        def transcribe(self, audio, **kw):
            self.languages.append(kw.get("language"))
            return {"text": "English sentence", "stats": {"language": "en"}}

    engine = LanguageEngine()
    s = osw._Session(engine, None, languages=("en",))
    s._watchdog_stop.set()
    s.sys._transcribe2(loud(1.0))
    s.mic._transcribe2(loud(1.0))

    assert engine.languages == ["en", "en"]


def test_mic_channel_is_always_you():
    """Channel-pure architecture (product call 2026-08-19): the mic IS the
    user. Far-end bleed is the echo gate's / capture routing's problem,
    never labeling's."""
    s = make_session(FakeEngine())
    s.mic.push(loud(1.5, 0.1))
    s.mic.push(quiet(1.0))
    drain(s.mic)
    assert s.mic.seg_speaker == "You"


def test_mic_stays_you_through_rediar_relabel(monkeypatch):
    """The local user (mic) is labeled "You" and NO relabel path may turn it
    into "Speaker A/B". A periodic re-diar retro-relabels the SYS channel; it
    must leave mic commits untouched, and the registry merge sync must never
    rewrite mic.seg_speaker (it can only be "You"). This regression guard
    keeps local capture separate from remote-speaker relabeling."""

    eng = FakeEngine()
    s = osw._Session(eng, mean_voice_embed)
    s._watchdog_stop.set()  # freeze background loops; drive re-diar directly
    # a committed mic line ("You") and a committed sys line ("Speaker A")
    s.commits.add("You", "this is the local user talking", channel="mic",
                  span=(0.0, 5.0))
    s.commits.add("Speaker A", "and this is the far end", channel="sys",
                  span=(6.0, 11.0))
    s.sys.push(np.concatenate([loud(5.0, level=0.1), quiet(1.0),
                               loud(5.0, level=0.35)]))
    wait_idle(s.sys)
    s._apply_rediar_segs([(0.0, 5.0, 0), (6.0, 11.0, 1)], s.sys.history)
    labels = [m["speaker"] for m in s.commits.line_meta]
    # mic line UNTOUCHED as "You"; only the sys line is retro-relabeled
    assert labels[0] == "You", labels
    assert s.commits.lines[0].startswith("[You] ")
    # the open mic segment is "You" and no relabel can make it a Speaker letter
    assert s.mic.seg_speaker == "You"


def test_silence_never_reaches_engine():
    eng = FakeEngine()
    s = make_session(eng)
    s.mic.push(quiet(2.0))
    time.sleep(0.2)
    assert eng.calls == []
    assert s.display() == ""


def room_tone(seconds: float) -> np.ndarray:
    """Low-energy noise floor: peak CLEARS the digital-silence PEAK_GATE and
    the turn tracker's SILENCE_PEAK, but its 30 ms frame RMS stays below the
    speech floor (audio_energy.SILENCE_RMS). This is the sys/remote channel's
    idle room tone that a decoder could otherwise turn into a phantom line."""
    rng = np.random.default_rng(3)
    a = (0.0016 * rng.standard_normal(int(seconds * osw.SAMPLE_RATE))).astype(np.float32)
    a[len(a) // 3] = 0.015   # a click so PEAK clears 0.008/0.002, energy stays low
    a[2 * len(a) // 3] = 0.012
    return a


def test_room_tone_hallucination_gated_but_quiet_speech_decodes():
    """Silence-hallucination gate (frame-RMS, not just peak): a near-silent
    sys window must NOT reach the engine or produce text — otherwise whisper
    fabricates a phantom line and the diarizer seeds a ghost 'Speaker A'. The
    frame-RMS floor is chosen so a QUIET-but-real utterance (one loud word
    frame over a soft clip) still decodes."""
    from transcription_worker import audio_energy as ae

    ch = osw._ChannelStream(FakeEngine(), osw._CommitLog(), None,
                            fixed_label="You", channel_tag="mic")
    text, _ = ch._transcribe2(room_tone(2.0))
    assert text == "" and ch.engine.calls == []          # gated: no phantom

    # quiet real speech: low whole-clip RMS, one loud 0.1s word -> must decode
    rng = np.random.default_rng(3)
    speech = (0.0009 * rng.standard_normal(2 * osw.SAMPLE_RATE)).astype(np.float32)
    speech[16000:17600] = (0.09 * rng.standard_normal(1600)).astype(np.float32)
    assert ae.has_speech_energy(speech)                  # the floor's own verdict
    ch2 = osw._ChannelStream(FakeEngine(), osw._CommitLog(), None,
                             fixed_label="You", channel_tag="mic")
    text2, _ = ch2._transcribe2(speech)
    assert text2 and ch2.engine.calls                    # real speech survives


def test_room_tone_seeds_no_phantom_speaker_on_sys():
    """End-to-end: room tone pushed on the SYS channel commits NO line, so no
    phantom 'Speaker A' appears in the transcript (the field regression)."""
    eng = FakeEngine()
    s = make_session(eng)
    s.sys.push(room_tone(1.5))
    s.sys.push(quiet(1.0))       # close the (non-)turn
    drain(s.sys)
    time.sleep(0.2); drain(s.sys)
    assert s.commits.snapshot() == []
    assert s.display() == ""


def test_every_closed_turn_commits_a_line():
    eng = FakeEngine()
    s = make_session(eng)
    s.sys.push(loud(2.0, 0.1))
    s.sys.push(quiet(1.0))       # closes turn 1 -> commits under Speaker A
    drain(s.sys)
    s.sys.push(loud(2.0, 0.35))
    s.sys.push(quiet(1.0))       # closes turn 2 -> commits under Speaker B
    drain(s.sys)
    time.sleep(0.3); drain(s.sys)
    lines = s.commits.snapshot()
    assert len(lines) == 2, lines
    assert lines[0].startswith("[Speaker A] ")
    assert lines[1].startswith("[Speaker B] ")


def test_same_voice_commits_per_utterance():
    eng = FakeEngine()
    s = make_session(eng)
    for _ in range(2):
        s.sys.push(loud(1.5, 0.1))
        s.sys.push(quiet(1.0))
        drain(s.sys)
    time.sleep(0.3); drain(s.sys)
    lines = s.commits.snapshot()
    assert len(lines) == 2                 # append-only utterance stream
    assert all(l.startswith("[Speaker A] ") for l in lines)


def test_finish_commits_open_segments_with_labels():
    eng = FakeEngine()
    s = make_session(eng)
    s.mic.push(loud(1.2))
    drain(s.mic)
    s.mic.finish()
    lines = s.commits.snapshot()
    assert len(lines) == 1 and lines[0].startswith("[You] ")


def test_display_interleaves_commits_and_partials(monkeypatch):
    # Partials on the caption are opt-in since 2026-08-28
    # (DICTATION_NOTES_SHOW_PARTIAL, default OFF) — this test pins the =1 shape.
    monkeypatch.setenv("DICTATION_NOTES_SHOW_PARTIAL", "1")
    s = make_session(FakeEngine())
    s.commits.add("You", "hello")
    s.commits.add("Speaker A", "你好")
    with s.mic.lock:
        s.mic.partial = "tail"
        s.mic.seg_speaker = "You"
    out = s.display().split("\n")
    # committed text is VERBATIM since 2026-08-28 QA (the display-only
    # sentence-caser is no longer applied at commit); the partial line
    # still goes through stream_repetition.clean -> "Tail"
    assert out == ["[You] hello", "[Speaker A] 你好", "[You] Tail"]


def test_display_default_is_committed_lines_only(monkeypatch):
    """By default the caption
    string carries committed lines only — an in-flight partial (the
    flicker/regurgitation stage) must not render on the meeting transcript
    surfaces, and commit ORDER is preserved with new lines at the END."""
    monkeypatch.delenv("DICTATION_NOTES_SHOW_PARTIAL", raising=False)
    s = make_session(FakeEngine())
    s.commits.add("You", "hello")
    s.commits.add("Speaker A", "你好")
    with s.mic.lock:
        s.mic.partial = "IO do force snapdragon"   # the field junk, verbatim
        s.mic.seg_speaker = "You"
    with s.sys.lock:
        s.sys.partial = "thank you for watching"
        s.sys.seg_speaker = "Speaker A"
    out = s.display().split("\n")
    assert out == ["[You] hello", "[Speaker A] 你好"], (
        "partials leaked into the default caption:\n" + s.display())
    # Appending another commit lands at the BOTTOM (no top-insert inversion).
    s.commits.add("You", "next line")
    assert s.display().split("\n")[-1].lower() == "[you] next line"


def test_dictation_display_keeps_live_partial_when_notes_partials_are_off(monkeypatch):
    """The Notes display policy must not blank Dictation's live preview.

    Both clients share ``_Session.display()``.  Dictation deliberately asks
    this worker for the selected Whisper model and needs its mutable mic tail
    while the key is still held; ``DICTATION_NOTES_SHOW_PARTIAL`` only controls the
    meeting transcript surfaces.
    """
    monkeypatch.delenv("DICTATION_NOTES_SHOW_PARTIAL", raising=False)
    s = make_session(FakeEngine())
    s.client = "dictation"
    with s.mic.lock:
        s.mic.partial = "the live turbo hypothesis"
        s.mic.seg_speaker = "You"

    assert s.display().lower() == "[you] the live turbo hypothesis"


def test_wire_roundtrip_with_channels(monkeypatch):
    eng = FakeEngine()
    monkeypatch.setattr(osw, "_get_engine", lambda: eng)
    monkeypatch.setattr(osw, "_make_embed_fn", lambda: mean_voice_embed)
    r = osw.start({"op": "stream_start", "engine": "whisper",
                   "languages": ["zh", "fr"]})
    assert r["ok"] and r["engine"] == "whisper"
    sid = r["stream_id"]
    assert osw._sessions[sid].sys.language_vote.seed == {"zh", "fr"}
    pcm = (loud(1.2) * 32767).astype("<i2").tobytes()
    p1 = osw.push({"op": "stream_push", "stream_id": sid,
                   "pcm_b64": base64.b64encode(pcm).decode()})
    assert p1["ok"] and p1["is_endpoint"] is False
    p2 = osw.push({"op": "stream_push", "stream_id": sid, "channel": "sys",
                   "pcm_b64": base64.b64encode(pcm).decode()})
    assert p2["ok"]
    # stream_id is required (wire contract v2): no id, no stream.
    assert osw.push({"op": "stream_push", "pcm_b64": base64.b64encode(pcm).decode()}) \
        == {"ok": False, "reason": "no_such_stream", "error": "no_such_stream"}
    f = osw.finish({"op": "stream_finish", "stream_id": sid})
    assert f["ok"]
    assert sid not in osw._sessions


def test_stream_start_reports_live_diarization_configuration(monkeypatch):
    monkeypatch.setattr(osw, "_get_engine", lambda: FakeEngine())
    monkeypatch.setattr(osw, "_make_embed_fn", lambda: mean_voice_embed)
    expected = {
        "available": True,
        "backend": "online_registry",
        "model_dir": r"C:\models\diarization",
        "embedding_model": "embedding.onnx",
        "embedding_provider": "cpu",
        "corrector": "blockonline",
        "corrector_provider": "cpu",
    }
    monkeypatch.setattr(osw, "_live_diarization_info", lambda session: expected)

    reply = osw.start({"op": "stream_start", "engine": "whisper"})

    assert reply["live_diarization"] == expected
    osw.cancel({"stream_id": reply["stream_id"]})


def test_wire_start_without_engine_fails(monkeypatch):
    monkeypatch.setattr(osw, "_get_engine", lambda: None)
    r = osw.start({"op": "stream_start", "engine": "whisper"})
    assert not r["ok"]


def test_short_turn_inherits_no_new_speaker():
    eng = FakeEngine()
    s = make_session(eng)
    s.sys.push(loud(1.5, 0.1))
    s.sys.push(quiet(1.0))
    drain(s.sys)
    n_before = s.sys.seg_speaker
    # 0.4s sliver of a DIFFERENT voice: under MIN_TURN_S, must not relabel
    s.sys.push(loud(0.4, 0.35))
    s.sys.push(quiet(1.0))
    drain(s.sys)
    assert s.sys.seg_speaker == n_before


def test_rapid_double_speaker_change_commits_both(monkeypatch):
    """Two speaker changes while a decode is busy must produce TWO commits
    in order — the single-slot version lost the first boundary."""
    eng = FakeEngine(delay_s=0.25)     # keep the job slot busy
    s = make_session(eng)
    s.sys.push(loud(1.5, 0.05))        # voice 1
    s.sys.push(quiet(1.0))
    drain(s.sys, timeout=8)
    s.sys.push(loud(1.5, 0.15))        # voice 2 -> boundary 1
    s.sys.push(quiet(1.0))
    s.sys.push(loud(1.5, 0.32))        # voice 3 -> boundary 2 (while busy)
    s.sys.push(quiet(1.0))
    for _ in range(40):                # let queued jobs drain
        time.sleep(0.1)
        with s.sys.lock:
            if not s.sys.decoding and not s.sys.pending_boundaries:
                break
    lines = s.commits.snapshot()
    speakers = [l.split("]")[0] + "]" for l in lines]
    assert speakers == ["[Speaker A]", "[Speaker B]", "[Speaker C]"], lines


def test_mic_echo_text_deduped_but_unique_speech_kept(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_ECHO_EXCISE", "1")
    log = osw._CommitLog()
    log.add("Speaker A", "we should ship the feature tomorrow", channel="sys")
    # the mic hears the same sentence as room echo -> dropped
    log.add("You", "We should ship the feature tomorrow.", channel="mic")
    # the user's own distinct words -> kept
    log.add("You", "我觉得可以,明天发", channel="mic")
    lines = log.snapshot()
    assert len(lines) == 2
    assert lines[0].startswith("[Speaker A]")
    assert lines[1].startswith("[You] 我觉得可以")


def test_mixed_mic_segment_keeps_user_sentences(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_ECHO_EXCISE", "1")
    log = osw._CommitLog()
    log.add("Speaker A", "the quarterly numbers look strong this time.", channel="sys")
    # mic segment mixes the user's words with the far end's echo
    log.add("You", "我同意这个方案。 The quarterly numbers look strong this time. 我们明天发布吧。",
            channel="mic")
    lines = log.snapshot()
    assert len(lines) == 2
    assert "明天发布" in lines[1] and "quarterly" not in lines[1]


def test_trailing_pleasantry_stripped_at_commit():
    log = osw._CommitLog()
    log.add("You", "我们明天发布吧 Thank you.", channel="mic")
    log.add("You", "Thank you.", channel="mic")
    lines = log.snapshot()
    assert len(lines) == 1
    assert "Thank you" not in lines[0] and "明天发布" in lines[0]


# -- vocabulary prompt conditioning (borrowed from the macOS dictation line) --

class PromptCaptureEngine(FakeEngine):
    def __init__(self):
        super().__init__()
        self.prompts: list[str | None] = []

    def transcribe(self, audio, **kw):
        self.prompts.append(kw.get("initial_prompt"))
        return super().transcribe(audio, **kw)


def test_stream_bias_phrases_reach_every_decode():
    eng = PromptCaptureEngine()
    s = osw._Session(eng, mean_voice_embed,
                     bias_phrases=["Fable", "Snapdragon", "费德勒"])
    s.push_mic(loud(2.0))
    s.push_mic(quiet(1.0))
    drain(s.mic)
    assert eng.prompts, "no decode ran"
    assert all(p == "Fable, Snapdragon, 费德勒" for p in eng.prompts)


def test_stream_without_vocabulary_sends_no_prompt():
    eng = PromptCaptureEngine()
    s = osw._Session(eng, mean_voice_embed)
    s.push_mic(loud(2.0))
    s.push_mic(quiet(1.0))
    drain(s.mic)
    assert eng.prompts and all(p is None for p in eng.prompts)


class PersonalizeCaptureEngine:
    """Engine double that exposes the optional personalization switch."""

    def __init__(self, *, fail_beam=False):
        self.calls = []
        self.fail_beam = fail_beam

    def transcribe(self, audio, *, language=None, beam_size=1,
                   bias_phrases=None, initial_prompt=None,
                   personalize=True):
        call = {
            "language": language, "beam_size": beam_size,
            "bias_phrases": bias_phrases,
            "initial_prompt": initial_prompt,
            "personalize": personalize,
        }
        self.calls.append(call)
        if self.fail_beam and beam_size > 1:
            raise RuntimeError("beam unavailable")
        text = "ask gpt four oh"
        if personalize:
            from transcription_worker import spoken_aliases
            text, _ = spoken_aliases.apply(
                text, {"gpt": "ChatGPT", "gpt four oh": "GPT-4o"})
        return {"text": text}


def test_live_decode_disables_engine_text_personalization_and_commits_alias_once(monkeypatch):
    """The engine must leave aliases to the commit gate exactly once.

    The nested pair is deliberate: a second alias pass turns ``GPT-4o`` into
    ``ChatGPT-4o``. This drives the actual decode -> commit path rather than
    testing the matcher in isolation.
    """
    from transcription_worker import spoken_aliases

    aliases = {"gpt": "ChatGPT", "gpt four oh": "GPT-4o"}
    monkeypatch.setattr(spoken_aliases, "load", lambda: aliases)
    eng = PersonalizeCaptureEngine()
    commits = osw._CommitLog()
    ch = osw._ChannelStream(eng, commits, None, fixed_label="You",
                            channel_tag="mic", bias_phrases=["gpt four oh"],
                            initial_prompt="gpt four oh", beam_size=1)

    text, _ = ch._transcribe2(loud(1.0))
    commits.add("You", text, channel="mic")

    assert eng.calls == [{
        "language": None, "beam_size": 1,
        "bias_phrases": None, "initial_prompt": "gpt four oh",
        "personalize": False,
    }]
    assert commits.snapshot() == ["[You] ask GPT-4o"]


def test_live_beam_decode_passes_personalize_false_and_keeps_beam_inputs():
    eng = PersonalizeCaptureEngine()
    ch = osw._ChannelStream(eng, osw._CommitLog(), None,
                            fixed_label="You", channel_tag="mic",
                            bias_phrases=["Repo"], beam_size=4)

    ch._transcribe2(loud(1.0), language="en")

    assert eng.calls == [{
        "language": "en", "beam_size": 4,
        "bias_phrases": ["Repo"], "initial_prompt": None,
        "personalize": False,
    }]


def test_live_beam_fallback_and_plain_decode_pass_personalize_false():
    eng = PersonalizeCaptureEngine(fail_beam=True)
    ch = osw._ChannelStream(eng, osw._CommitLog(), None,
                            fixed_label="You", channel_tag="mic",
                            bias_phrases=["Repo"],
                            initial_prompt="Repo", beam_size=4)

    ch._transcribe2(loud(1.0), language="en")

    assert eng.calls == [
        {"language": "en", "beam_size": 4,
         "bias_phrases": ["Repo"], "initial_prompt": None,
         "personalize": False},
        {"language": "en", "beam_size": 1,
         "bias_phrases": None, "initial_prompt": "Repo",
         "personalize": False},
    ]

    plain = PersonalizeCaptureEngine()
    plain_ch = osw._ChannelStream(plain, osw._CommitLog(), None,
                                  fixed_label="You", channel_tag="mic",
                                  initial_prompt="Repo", beam_size=1)
    plain_ch._transcribe2(loud(1.0), language="en")
    assert plain.calls == [{
        "language": "en", "beam_size": 1,
        "bias_phrases": None, "initial_prompt": "Repo",
        "personalize": False,
    }]




def test_partial_anti_shrink_keeps_longer_hypothesis():
    """A re-decode of the SAME open segment that comes back much shorter must
    not visually erase the previous partial (the '莫名其妙的删除' class)."""
    ch = osw._ChannelStream(FakeEngine(), osw._CommitLog(), None,
                            fixed_label="You", channel_tag="mic")
    with ch.lock:
        ch.partial = "a fairly long partial hypothesis the user is reading"
    long_text = ch.partial
    # simulate the decode result path directly: short text should be rejected
    with ch.lock:
        new = "tiny"
        if len(new) >= int(len(ch.partial) * 0.6) or not ch.partial:
            ch.partial = new
    assert ch.partial == long_text


def test_vocab_echo_and_loops_never_commit_or_flash():
    """Field 2026-08-19: noise windows under a vocabulary prompt made live
    captions recite the vocabulary ('Speaker A' reading the tennis terms),
    and noise without a prompt loops phrases. Neither may commit or flash."""
    eng = PromptCaptureEngine()
    s = osw._Session(eng, mean_voice_embed,
                     bias_phrases=["Pro Staff", "费德勒", "桑普拉斯", "甜区"])
    log = s.commits
    log.add("Speaker A", "Pro Staff, 费德勒, 桑普拉斯, 甜区", channel="sys")
    log.add("Speaker A", "桑普拉斯, 桑普拉斯", channel="sys")   # echo subset
    log.add("You", "好 桑普拉斯 桑普拉斯 桑普拉斯 桑普拉斯 桑普拉斯 桑普拉斯 "
                   "桑普拉斯 桑普拉斯 桑普拉斯 桑普拉斯 桑普拉斯", channel="mic")
    assert log.lines == []
    # …but a REAL sentence that merely CONTAINS vocabulary terms survives
    log.add("Speaker A", "我觉得费德勒那个年代的 Pro Staff 拍面更小,"
                         "但是打起来甜区的手感完全不一样", channel="sys")
    assert len(log.lines) == 1
    # partials: vocabulary flash suppressed too
    ch = s.mic
    with ch.lock:
        ch.partial = "Pro Staff, 费德勒, 桑普拉斯"
    assert ch.partial_line() == ""


def test_push_reply_carries_structured_commits():
    """Partner feed contract: every committed line surfaces on the wire as a
    structured {speaker,text,channel} event exactly once (drain cursor)."""
    eng = FakeEngine()
    s = osw._Session(eng, mean_voice_embed)
    log = s.commits
    log.add("You", "hello from the mic", channel="mic")
    log.add("Speaker A", "hello from the far end", channel="sys")
    drained = log.drain_feed()
    assert [(d["speaker"], d["channel"]) for d in drained] == [
        ("You", "mic"), ("Speaker A", "sys")]
    assert all("committed_at" in d and d["text"] for d in drained)
    assert log.drain_feed() == []  # cursor advanced — no double delivery


def test_feed_commits_carry_speech_span():
    """Two-timestamp contract: a commit made with a span surfaces start/end
    (audio-offset seconds, the speech time) BESIDE committed_at (the
    finalization time) on the wire event; a span-less commit carries explicit
    None (wire null) for both — present keys, never missing ones, so the C#
    host's absent-and-null-are-the-same parsing reads "not measured"."""
    eng = FakeEngine()
    s = osw._Session(eng, mean_voice_embed)
    log = s.commits
    log.add("You", "a line with a span", channel="mic", span=(1.5, 4.25))
    log.add("Speaker A", "a line without one", channel="sys")
    with_span, without_span = log.drain_feed()
    assert (with_span["start"], with_span["end"]) == (1.5, 4.25)
    assert "committed_at" in with_span  # both clocks, on one event
    assert without_span["start"] is None and without_span["end"] is None


def test_stub_engine_completes_rolling_stable_stream(monkeypatch):
    """The modelless stub backend must carry a FULL rolling-stable stream
    (stream_start(rolling_stable=true) -> push both channels -> finish) —
    the plan's "scripted push sequence (fake engine)" acceptance check and
    the notes stream e2e harness's smoke path. The stub predated
    rolling-stable: it had no transcribe_timed and rejected language=,
    which killed stream_finish (agent H, 2026-08-24)."""
    from transcription_worker.whisper_mac import StubEngine

    monkeypatch.setattr(osw, "_get_engine", lambda: StubEngine())
    monkeypatch.setattr(osw, "_make_embed_fn", lambda: None)
    r = osw.start({"op": "stream_start", "engine": "whisper",
                   "rolling_stable": True, "stable_window_seconds": 2.0,
                   "stable_hop_seconds": 1.0, "stable_lookahead_seconds": 0.5})
    assert r["ok"] and r["rolling_stable"] is True
    sid = r["stream_id"]
    pcm = base64.b64encode((loud(1.5, 0.2) * 32767).astype("<i2").tobytes()).decode()
    for _ in range(3):
        assert osw.push({"op": "stream_push", "stream_id": sid,
                         "pcm_b64": pcm})["ok"]
        assert osw.push({"op": "stream_push", "stream_id": sid,
                         "channel": "sys", "pcm_b64": pcm})["ok"]
    f = osw.finish({"op": "stream_finish", "stream_id": sid})
    assert f["ok"], f
    assert f["stable_spans"], "stub stream produced no rolling stable spans"
    assert all(s["text"].startswith("stub stable sentence")
               for s in f["stable_spans"])
    assert f["stable_through_seconds"] > 0
    assert sid not in osw._sessions


# -- streaming echo cancellation (speakers-on live path) --------------------

def _echo_scene(dur_s: float, delay: int, rng_seed: int = 3):
    """sys = noise bursts; mic = quiet user tone + delayed sys echo."""
    sr = osw.SAMPLE_RATE
    n = int(dur_s * sr)
    rng = np.random.default_rng(rng_seed)
    sys_sig = (0.3 * rng.standard_normal(n)).astype(np.float32).clip(-1, 1)
    period = int(2.0 * sr)                       # 1.5s on / 0.5s off bursts
    mask = ((np.arange(n) % period) < int(1.5 * sr)).astype(np.float32)
    sys_sig *= mask
    echo = 0.5 * np.concatenate(
        [np.zeros(delay, dtype=np.float32), sys_sig])[:n]
    t = np.arange(n) / sr
    tone = (0.05 * np.sin(2 * np.pi * 440.0 * t)).astype(np.float32)
    return sys_sig, tone + echo


def _push_both(s, sys_sig, mic_sig, chunk=3200):
    for i in range(0, len(mic_sig), chunk):
        s.push_sys(sys_sig[i:i + chunk])
        s.push_mic(mic_sig[i:i + chunk])


def test_stream_aec_attenuates_far_end_echo():
    """Session-level FDAF: after the first delay estimate the mic chunks
    entering the channel must carry far LESS energy than the raw mic
    (echo dominated the input; the FakeEngine can't assert on words, so
    the AEC's own energy ledger is the evidence)."""
    s = make_session(FakeEngine())
    delay = 800                                   # 50ms speaker->mic
    sys_sig, mic_sig = _echo_scene(16.0, delay)
    _push_both(s, sys_sig, mic_sig)
    assert s._aec is not None, "AEC never engaged"
    assert s._aec_delay == delay
    s_in = sum(e for _, e, _ in s._aec_energy)
    s_out = sum(e for _, _, e in s._aec_energy)
    # echo is ~99% of the mic energy; a converged filter removes most of it
    assert s_out < 0.5 * s_in, (s_in, s_out)


def test_stream_aec_headset_is_byte_identical_noop():
    """Silent sys (headset): no delay verdict ever engages, and every mic
    sample reaches the channel untouched."""
    s = make_session(FakeEngine())
    mic_sig = loud(12.0, 0.1)
    _push_both(s, quiet(12.0), mic_sig)
    assert s._aec is None and s._aec_delay == 0
    with s.mic.lock:
        hist = s.mic.history.copy()
    assert np.array_equal(hist, mic_sig)


def test_stream_aec_failsoft_when_feed_raises(monkeypatch):
    """Any exception in the AEC path falls back to the raw chunk — the
    caption pipeline must never die because of AEC."""
    from transcription_worker import aec as aec_mod

    def boom(self, mic, ref):
        raise RuntimeError("synthetic AEC failure")

    monkeypatch.setattr(aec_mod.FdafAec, "feed", boom)
    eng = FakeEngine()
    s = make_session(eng)
    sys_sig, mic_sig = _echo_scene(12.0, 800)
    _push_both(s, sys_sig, mic_sig)
    # engaged at ~10s, feed blew up, fell back to raw + disengaged
    assert s._aec is None
    with s.mic.lock:
        hist = s.mic.history.copy()
    assert np.array_equal(hist, mic_sig)          # nothing lost, order kept
    assert eng.calls, "mic channel decodes stopped flowing"


def test_stream_aec_env_gate_disables(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_AEC", "0")
    s = make_session(FakeEngine())
    sys_sig, mic_sig = _echo_scene(12.0, 800)
    _push_both(s, sys_sig, mic_sig)
    assert not s._aec_enabled and s._aec is None
    with s.mic.lock:
        hist = s.mic.history.copy()
    assert np.array_equal(hist, mic_sig)


def test_stream_aec_safety_disengages_on_added_energy():
    """The recorded trap: a filter that ADDS energy (spurious delay) must
    bail out. Feed the running ledger a consistently-increasing window and
    watch the breaker trip."""
    s = make_session(FakeEngine())
    from transcription_worker import aec as aec_mod
    s._aec = aec_mod.FdafAec()
    s._aec_delay = 800
    blk = np.ones(3200, dtype=np.float32) * 0.1
    for _ in range(30):                           # 6s of out = 1.2x in energy
        s._aec_safety(blk, blk * 1.1)
        if s._aec is None:
            break
    assert s._aec is None, "safety breaker never tripped"


def _stable_session(monkeypatch=None):
    """A session with rolling-stable channels and frozen background loops."""
    s = osw._Session(FakeEngine(), None, rolling_stable=True,
                     stable_window_seconds=4.0, stable_hop_seconds=2.0,
                     stable_lookahead_seconds=1.0)
    s._watchdog_stop.set()
    return s


def _inject_stable_sys_span(s, start, end, text):
    with s.stable_sys.lock:
        s.stable_sys._spans.append({
            "start": start, "end": end, "text": text, "language": "en",
            "channel": "sys", "speaker": "Them"})


# -- relabels on the wire (TURNS-EXECUTION-PLAN.md 2026-08-24, task 1) -------

def test_set_speakers_appends_relabel_events_sys_only():
    """Every applied set_speakers change lands in the relabel log with its
    own cursor; mic lines are pinned "You" — never relabelled, never
    emitted."""
    log = osw._CommitLog()
    log.add("You", "local user line", channel="mic", span=(0.0, 2.0))
    log.add("Speaker A", "remote line one", channel="sys", span=(2.0, 4.0))
    log.add("Speaker A", "remote line two", channel="sys", span=(5.0, 7.0))
    changed = log.set_speakers({0: "Speaker Z", 1: "Speaker B", 2: "Speaker A"})
    assert changed == 1                       # mic skipped; idx2 already A
    assert log.lines[0].startswith("[You] ")  # the pin is structural
    assert log.drain_relabels() == [{"index": 1, "speaker": "Speaker B"}]
    assert log.drain_relabels() == []         # cursor advanced
    # the commits cursor is untouched by relabel drains
    assert len(log.drain_feed()) == 3


def test_registry_merge_relabel_emits_wire_events():
    """The relabel(old, new) path (registry merge / consolidate) also feeds
    the wire log, sys lines only."""
    log = osw._CommitLog()
    log.add("Speaker B", "line from the absorbed centroid", channel="sys",
            span=(0.0, 3.0))
    log.relabel("Speaker B", "Speaker A")
    assert log.drain_relabels() == [{"index": 0, "speaker": "Speaker A"}]


# -- rolling-span attribution gates (plan task 2) ----------------------------

def test_stable_sliver_overlap_keeps_channel_default():
    """Task 2a: a diarizer turn covering <MIN_ATTR_OVERLAP_RATIO of the span
    is edge evidence, not span evidence — the span keeps its channel
    default instead of adopting the sliver's label."""
    s = _stable_session()
    _inject_stable_sys_span(s, 0.0, 10.0, "a long stable sentence")
    # turn clips only the first second of the 10s span (10% < 30%)
    s.commits.add("Speaker A", "clip", channel="sys", span=(0.0, 1.0))
    snap = s.stable_snapshot()
    assert snap["spans"][0]["speaker"] == "Them"


def test_stable_majority_overlap_still_attributes():
    s = _stable_session()
    _inject_stable_sys_span(s, 0.0, 10.0, "a long stable sentence")
    s.commits.add("Speaker A", "most of it", channel="sys", span=(0.0, 6.0))
    snap = s.stable_snapshot()
    assert snap["spans"][0]["speaker"] == "Speaker A"


def test_stable_tiebreak_is_earliest_turn_not_alphabet():
    """Task 2b (updates the implicit old behavior): equal overlaps used to
    resolve via max() over (overlap, speaker) tuples — alphabetically last
    speaker won. Deterministic rule now: earliest matching turn start."""
    s = _stable_session()
    _inject_stable_sys_span(s, 0.0, 8.0, "two people share this window")
    # both turns overlap exactly 3s; the EARLIER one is "Speaker A" — the
    # old alphabetical tie-break would pick "Speaker B"
    s.commits.add("Speaker A", "first voice", channel="sys", span=(0.0, 3.0))
    s.commits.add("Speaker B", "second voice", channel="sys", span=(4.0, 7.0))
    snap = s.stable_snapshot()
    assert snap["spans"][0]["speaker"] == "Speaker A"


# -- word-level split (plan task 3, DICTATION_NOTES_WORDSPLIT) --------------------

def test_wordsplit_off_by_default_keeps_single_label():
    """No word timestamps exist on the QNN path (transcribe_timed emits
    timestamp-token segments only), so the split defaults OFF and the span
    stays whole under the majority-overlap label."""
    s = _stable_session()
    _inject_stable_sys_span(s, 0.0, 10.0, "first speaker words then second speaker words")
    s.commits.add("Speaker A", "first half", channel="sys", span=(0.0, 5.0))
    s.commits.add("Speaker B", "second half", channel="sys", span=(5.0, 10.0))
    snap = s.stable_snapshot()
    assert len(snap["spans"]) == 1
    assert snap["spans"][0]["speaker"] == "Speaker A"  # earliest-turn tiebreak


def test_wordsplit_splits_span_at_diarizer_boundary(monkeypatch):
    """DICTATION_NOTES_WORDSPLIT=1: a sys span crossing two speaker turns splits
    into per-speaker sub-spans at the turn boundary, whole words kept with
    the side owning each word's (estimated) midpoint."""
    monkeypatch.setenv("DICTATION_NOTES_WORDSPLIT", "1")
    s = _stable_session()
    _inject_stable_sys_span(s, 0.0, 10.0, "alpha bravo charlie delta echo foxtrot")
    s.commits.add("Speaker A", "first half", channel="sys", span=(0.0, 5.0))
    s.commits.add("Speaker B", "second half", channel="sys", span=(5.0, 10.0))
    snap = s.stable_snapshot()
    spans = [x for x in snap["spans"] if x["channel"] == "sys"]
    assert [x["speaker"] for x in spans] == ["Speaker A", "Speaker B"]
    assert spans[0]["text"] == "alpha bravo charlie"
    assert spans[1]["text"] == "delta echo foxtrot"
    assert spans[0]["end"] <= spans[1]["start"] + 1e-6
    # stable_text reflects the split lines
    assert "[Speaker A] alpha bravo charlie" in snap["text"]
    assert "[Speaker B] delta echo foxtrot" in snap["text"]


def test_wordsplit_single_span_stays_whole(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_WORDSPLIT", "1")
    s = _stable_session()
    _inject_stable_sys_span(s, 0.0, 4.0, "only one voice in here")
    s.commits.add("Speaker A", "the whole thing", channel="sys", span=(0.0, 4.0))
    snap = s.stable_snapshot()
    assert len(snap["spans"]) == 1
    assert snap["spans"][0]["speaker"] == "Speaker A"


def test_wordsplit_keeps_cjk_text_intact(monkeypatch):
    """zh has no spaces: CJK runs split per character for midpoint ownership
    but the sub-span text is sliced from the ORIGINAL string (no injected
    spaces)."""
    monkeypatch.setenv("DICTATION_NOTES_WORDSPLIT", "1")
    s = _stable_session()
    _inject_stable_sys_span(s, 0.0, 8.0, "我觉得没问题明天可以发布")
    s.commits.add("Speaker A", "前半", channel="sys", span=(0.0, 4.0))
    s.commits.add("Speaker B", "后半", channel="sys", span=(4.0, 8.0))
    snap = s.stable_snapshot()
    spans = [x for x in snap["spans"] if x["channel"] == "sys"]
    assert len(spans) == 2
    assert spans[0]["text"] + spans[1]["text"] == "我觉得没问题明天可以发布"
    assert " " not in spans[0]["text"] and " " not in spans[1]["text"]


def test_rediar_relabels_lines_and_syncs_registry(monkeypatch):
    """Live-converges-to-offline: a periodic re-clustering must retro-relabel
    committed sys lines by span overlap and replace the registry state."""

    eng = FakeEngine()
    s = osw._Session(eng, mean_voice_embed)
    s._watchdog_stop.set()  # freeze background loops; drive _rediar_once directly
    # two committed sys lines with spans, both initially "Speaker A"
    s.commits.add("Speaker A", "first speaker content here", channel="sys",
                  span=(0.0, 5.0))
    s.commits.add("Speaker A", "second speaker content here", channel="sys",
                  span=(6.0, 11.0))
    # offline re-clustering says they are two different people
    # two acoustically DISTINCT spans (mean_voice_embed buckets by level):
    # the shared merge pass must keep them apart, not fold them
    buf = np.concatenate([loud(5.0, level=0.1), quiet(1.0),
                          loud(5.0, level=0.35)])
    s.sys.push(buf)
    wait_idle(s.sys)
    s._apply_rediar_segs([(0.0, 5.0, 0), (6.0, 11.0, 1)], s.sys.history)
    metas = s.commits.line_meta
    assert metas[0]["speaker"] == "Speaker A"
    assert metas[1]["speaker"] == "Speaker B"
    assert s.commits.lines[1].startswith("[Speaker B] ")
    # registry now holds exactly the two re-clustered voices
    assert s._registry.num_speakers == 2


# ---------------------------------------------------------------------------
# FIX A: the live-registry embed_fn is backed by the DiariZen embedder
# (onnxruntime-qnn), NOT sherpa's vendored ORT. Two ORTs in one process
# corrupted the heap (0xC0000005). _make_embed_fn must (a) route through
# diarize_diarizen.embed_clip_fn and (b) never import sherpa_onnx.
# ---------------------------------------------------------------------------








def test_explicit_similarity_env_overrides_embedder_default(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_SPK_SIM", "0.67")
    embed = lambda audio: np.ones(4, dtype=np.float32)  # noqa: E731
    embed._dictation_similarity_threshold = 0.75

    session = make_session(FakeEngine(), embed)

    assert session._registry._threshold == 0.67
    session.teardown()


# ---------------------------------------------------------------------------
# FIX E: teardown() must free the bank + channel audio AND return promptly.
# A stop-respecting diar thread is joined within the bounded window; a thread
# that ignores the flag can't stall stop past the 2 s bound.
# ---------------------------------------------------------------------------
def test_teardown_frees_bank_and_channels_and_returns_fast():
    import threading
    s = make_session(FakeEngine())
    s.mic.push(loud(0.5))
    s.sys.push(loud(0.5))
    # a diar thread that WAITS on the stop flag (the fixed _warmup_dz_bank
    # checks _watchdog_stop and bails) — teardown must join it quickly.
    started = threading.Event()

    def good_warmup():
        started.set()
        # exits as soon as teardown sets the stop flag
        s._watchdog_stop.wait(10.0)

    t = threading.Thread(target=good_warmup, daemon=True)
    t.start()
    started.wait(1.0)
    s._warmup_thread = t
    s._dz_bank = object()

    t0 = time.monotonic()
    s.teardown()
    elapsed = time.monotonic() - t0

    assert not t.is_alive(), "stop-respecting thread was not joined"
    assert elapsed < 2.0, f"teardown stalled {elapsed:.2f}s"
    assert s._dz_bank is None
    for ch in (s.mic, s.sys):
        assert ch._chunks == []
        assert ch.closed is True


def test_teardown_bounded_even_if_thread_ignores_stop():
    """A diar thread that IGNORES the stop flag must not block stop beyond the
    2 s join bound (it stays alive as a daemon; the wire is freed regardless)."""
    import threading
    s = make_session(FakeEngine())
    release = threading.Event()

    def stuck():
        release.wait(30.0)      # ignores _watchdog_stop entirely

    t = threading.Thread(target=stuck, daemon=True)
    t.start()
    s._warmup_thread = t

    t0 = time.monotonic()
    s.teardown()
    elapsed = time.monotonic() - t0
    release.set()               # let the daemon exit

    # bounded: one 2 s join for the stuck thread, not 6 s+
    assert elapsed < 3.5, f"teardown blocked {elapsed:.2f}s past the 2 s bound"
    assert s._dz_bank is None


def test_registry_count_floor_blocks_collapse():
    """check_merge must NOT merge below (num_speakers-1) remote speakers even
    when centroids are similar — the live over-merge fix (device 2026-08-24)."""
    import numpy as np
    from transcription_worker.live_speakers import OnlineSpeakerRegistry
    reg = OnlineSpeakerRegistry(lambda a: np.zeros(4, np.float32),
                                num_speakers=4)  # remote floor = 3
    # 3 near-identical centroids (sim ~1.0, way above threshold)
    v = np.array([1, 0, 0, 0], np.float32)
    reg._centroids = [v.copy(), v.copy(), v.copy()]
    reg._counts = [5, 5, 5]
    assert reg._active() == 3
    # even across many checks, the floor holds
    assert reg.check_merge() is None and reg.check_merge() is None, \
        "floor=3 must block merging 3->2"
    # without a floor, the same MATURE state merges — but only after
    # MERGE_CONSEC consecutive agreeing checks (evidence accumulation), not one.
    reg2 = OnlineSpeakerRegistry(lambda a: np.zeros(4, np.float32))
    reg2._centroids = [v.copy(), v.copy(), v.copy()]; reg2._counts = [5, 5, 5]
    assert reg2.check_merge() is None, "1st check: streak building, no merge yet"
    assert reg2.check_merge() is not None, "2nd consecutive check -> merges"


def test_session_threads_num_speakers_into_registry():
    """The count-floor lived in the registry (check_merge) but _Session built
    it floor-less (device 2026-08-24: 3 live speakers -> 1). _Session must now
    thread its known participant count into the live registry so the online
    retro-merge honours the SAME floor the finalize pass already enforces."""
    s = osw._Session(FakeEngine(), mean_voice_embed, num_speakers=4)
    s._watchdog_stop.set()
    assert s._registry is not None
    assert s._registry._min_remote == 3, \
        "num_speakers=4 must arm the registry floor at 3 remote speakers"


def test_session_registry_floorless_without_count(monkeypatch):
    """No known count => registry floor stays None => behaviour is identical to
    before the fix (backward compatible; no num_speakers TypeError)."""
    monkeypatch.delenv("DICTATION_NOTES_NUM_SPK", raising=False)
    s = osw._Session(FakeEngine(), mean_voice_embed)
    s._watchdog_stop.set()
    assert s._registry is not None
    assert s._registry._min_remote is None


def test_registry_immature_centroids_dont_merge():
    """A 1-2-turn candidate is too noisy to trigger a merge even at sim~1.0."""
    import numpy as np
    from transcription_worker.live_speakers import OnlineSpeakerRegistry
    reg = OnlineSpeakerRegistry(lambda a: np.zeros(4, np.float32))
    v = np.array([1, 0, 0, 0], np.float32)
    reg._centroids = [v.copy(), v.copy()]; reg._counts = [2, 2]  # immature
    assert reg.check_merge() is None and reg.check_merge() is None, \
        "counts<MERGE_MIN_OBS must never merge"


def test_stale_cut_boundary_skips_without_empty_commit(monkeypatch):
    """A long-monologue commit in _decode advances seg_base independently of
    already-queued boundaries. If seg_base overtakes a queued boundary's cut_s,
    history[a:b] is empty/inverted -> decode "" -> the >0.3s gate silently drops
    the turn (device text-loss race). The guard must skip the stale boundary,
    emit a stale-cut _dbg line, and NOT commit an empty turn or rewind seg_base.
    """
    dbg: list[str] = []
    monkeypatch.setattr(osw, "_dbg", lambda m: dbg.append(m))
    ch = osw._ChannelStream(FakeEngine(), osw._CommitLog(), None,
                            fixed_label="You", channel_tag="mic")
    with ch.lock:
        ch._chunks.append(loud(10.0))       # real audio exists
        ch._hist_len = len(ch._chunks[0])
        # Monologue commit already consumed audio up to 8.0s: seg_base is now
        # PAST the boundary queued earlier at cut_s=5.0s.
        ch.seg_base = 8.0
        ch.seg_speaker = "You"
        ch.decoding = True                  # _commit_boundary was spawned
    ch._commit_boundary("You", 5.0)         # cut_s=5.0 < seg_base=8.0
    assert ch.commits.snapshot() == [], "stale boundary must not commit a line"
    assert any("stale-cut" in m for m in dbg), f"guard _dbg not fired: {dbg}"
    with ch.lock:
        assert ch.seg_base == 8.0, "seg_base must not rewind into committed audio"
        assert ch.decoding is False, "_finish_job must still release the slot"


def test_stale_cut_in_finish_drain_skips_without_empty_commit(monkeypatch):
    """Same stale-cut condition on the finish() drain path: a queued boundary
    whose cut_s is behind an already-advanced seg_base must be dropped with a
    stale-cut log, not turned into an empty (dropped) commit, and the following
    OPEN segment (real audio [seg_base:]) must still commit."""
    dbg: list[str] = []
    monkeypatch.setattr(osw, "_dbg", lambda m: dbg.append(m))
    ch = osw._ChannelStream(FakeEngine(), osw._CommitLog(), None,
                            fixed_label="You", channel_tag="mic")
    with ch.lock:
        ch._chunks.append(loud(10.0))
        ch._hist_len = len(ch._chunks[0])
        ch.seg_base = 8.0                    # advanced past the queued cut
        ch.seg_speaker = "You"
        ch.pending_boundaries.append(("You", 5.0))   # stale cut behind seg_base
    ch.finish()
    lines = ch.commits.snapshot()
    # exactly ONE commit: the trailing open segment [8.0s:10.0s]; the stale
    # boundary produced NO line (and, crucially, dropped nothing real).
    assert len(lines) == 1 and lines[0].startswith("[You] "), lines
    assert any("stale-cut" in m and "finish-drain" in m for m in dbg), \
        f"finish-drain guard _dbg not fired: {dbg}"


def test_a_sys_partial_never_clobbers_an_in_flight_mic_partial(monkeypatch):
    """The invariant fixed in the stream composition path must remain here, and nobody
    asserted afterwards.

    The defect: the MIC and SYS live-caption handlers shared one partial slot
    on the app side, so a SYS push cleared the mic's on-screen words while the
    mic partial was still in flight. The words survived in the transcript feed
    — it was a display-layer loss, which is the kind a person reports as "it
    keeps eating what I'm saying" and a test suite never notices.

    The earlier implementation added a host composition helper plus a test.
    A later cleanup deleted that helper — composition moved HERE, into
    `_Session.display()`, and the app now just mirrors `push.Text`. It added no
    Python test, the orphaned C# one was removed, and `README.md` §4.0a has
    carried "nothing asserts it on either side" since.

    This is that assertion, on the side that now owns the behaviour. The two
    channels must occupy SEPARATE lines: a far-end sentence arriving mid-word
    must not cost the user the half-sentence they are still speaking.
    """
    s = make_session(FakeEngine())
    s._watchdog_stop.set()
    monkeypatch.setenv("DICTATION_NOTES_SHOW_PARTIAL", "1")  # partials are opt-in now

    with s.mic.lock:
        s.mic.partial = "so what I was saying about the migration"
        s.mic.seg_speaker = "You"
    with s.sys.lock:
        s.sys.partial = "sorry, could you repeat that"
        s.sys.seg_speaker = "Speaker A"

    out = s.display()
    # Compared case-insensitively: the display layer sentence-cases what it
    # renders. What is under test is which CHANNELS survive, not their casing.
    folded = out.lower()

    assert "so what i was saying about the migration" in folded, (
        "the in-flight MIC partial vanished from the live caption:\n" + out)
    assert "sorry, could you repeat that" in folded, (
        "the in-flight SYS partial vanished from the live caption:\n" + out)

    # Separate lines, not one concatenated blob — the UI renders per line and
    # a merged line reads as one person saying both halves.
    lines = [line for line in out.split("\n") if line.strip()]
    assert len(lines) == 2, f"expected one line per channel, got {lines}"
    assert "[Speaker A]" in lines[0] and "[You]" in lines[1], (
        "far end above, local user last — the order the caption view renders:\n"
        + out)

    # And reading the display must not consume either partial: the next tick
    # composes from the same state until a commit clears it.
    assert s.display() == out, "display() is not idempotent — it mutates partial state"


def test_the_mic_partial_survives_with_committed_lines_above_it(monkeypatch):
    """Same invariant, with history present: commits first, then the far end's
    live line, then the user's. A regression that dropped the mic partial only
    once the log was non-empty would pass the test above.
    """
    monkeypatch.setenv("DICTATION_NOTES_SHOW_PARTIAL", "1")  # partials are opt-in now
    s = make_session(FakeEngine())
    s._watchdog_stop.set()
    s.commits.add("Speaker A", "we shipped it on Tuesday", channel="sys")

    with s.mic.lock:
        s.mic.partial = "and the rollback plan is"
        s.mic.seg_speaker = "You"
    with s.sys.lock:
        s.sys.partial = "what about the rollback"
        s.sys.seg_speaker = "Speaker A"

    lines = [line.lower() for line in s.display().split("\n") if line.strip()]
    assert len(lines) == 3, f"expected commit + two partials, got {lines}"
    assert "we shipped it on tuesday" in lines[0]
    assert "what about the rollback" in lines[1]
    assert "and the rollback plan is" in lines[2]


def test_rediar_cadence_is_adaptive_early_meeting(monkeypatch):
    """Speaker-label convergence (2a): the cheap re-cluster runs FASTER for
    the first ~2 minutes (labels settle while the meeting is young) and
    returns to the steady cadence afterwards. Env-tunable; early=0 disables;
    the steady override still caps the early value (never re-cluster more
    often late than early)."""
    s = osw._Session(FakeEngine(), None)
    s._watchdog_stop.set()
    # DEFAULT OFF (measured: early re-clusters on thin evidence cost
    # emit-time attribution): young session stays on the steady cadence
    s._session_t0 = time.monotonic()
    assert s._cheap_cadence_s() == osw._Session.REDIAR_CHEAP_CADENCE_S
    # old session: steady cadence
    s._session_t0 = time.monotonic() - 500.0
    assert s._cheap_cadence_s() == osw._Session.REDIAR_CHEAP_CADENCE_S
    # tunables
    monkeypatch.setenv("DICTATION_NOTES_REDIAR_EARLY_S", "15")
    monkeypatch.setenv("DICTATION_NOTES_REDIAR_EARLY_WINDOW_S", "60")
    s._session_t0 = time.monotonic() - 30.0
    # steady caps early (4.8 architecture steady beats a 15 s early tune)
    assert s._cheap_cadence_s() == min(15.0, osw._Session.REDIAR_CHEAP_CADENCE_S)
    s._session_t0 = time.monotonic() - 70.0
    assert s._cheap_cadence_s() == osw._Session.REDIAR_CHEAP_CADENCE_S
    # early=0 disables the adaptive phase entirely
    monkeypatch.setenv("DICTATION_NOTES_REDIAR_EARLY_S", "0")
    s._session_t0 = time.monotonic()
    assert s._cheap_cadence_s() == osw._Session.REDIAR_CHEAP_CADENCE_S
    # a steady override BELOW the early value wins in the early window too
    monkeypatch.setenv("DICTATION_NOTES_REDIAR_EARLY_S", "15")
    monkeypatch.setenv("DICTATION_NOTES_REDIAR_CADENCE_S", "8")
    s._session_t0 = time.monotonic()
    assert s._cheap_cadence_s() == 8.0


def test_first_cheap_recluster_not_gated_by_phantom_cost():
    """The 1.5x-cost backoff must be measured from the first REAL call: a
    phantom 30 s startup cost pushed the first cheap re-cluster to >=45 s
    into every take, exactly the early-label lag the adaptive cadence
    exists to remove. (The legacy path keeps its own 60 s interval floor.)"""
    s = osw._Session(FakeEngine(), None)
    s._watchdog_stop.set()
    assert s._rediar_cost == 0.0
    assert max(s._cheap_cadence_s(), 1.5 * s._rediar_cost) == \
        osw._Session.REDIAR_CHEAP_CADENCE_S


def test_prompt_vocabulary_hygiene_dedup_and_whole_phrase_cap():
    """Prompt hygiene (2c): the ``<|startofprev|>`` vocabulary prompt is
    deduped and capped to WHOLE phrases within the token budget —
    ``prompt_ids`` keeps the LAST 96 tokens, so an uncapped join of a large
    vocabulary file was silently truncated MID-PHRASE and dropped the
    earliest terms unpredictably. The regurgitation guard (vocab_norm) and
    the beam trie keep the FULL list: the guard must recognize echoes of any
    biased phrase, and the trie has no token-budget problem."""
    dup = ["Fable", "Snapdragon", "Fable", "  ", "Snapdragon"]
    s = osw._Session(FakeEngine(), None, bias_phrases=dup)
    s._watchdog_stop.set()
    assert s.initial_prompt == "Fable, Snapdragon"

    many = [f"Term{i:03d}" for i in range(400)]
    s2 = osw._Session(FakeEngine(), None, bias_phrases=many)
    s2._watchdog_stop.set()
    # capped: a strict front-prefix of whole phrases, under the budget
    kept = s2.initial_prompt.split(", ")
    assert 0 < len(kept) < 400
    assert kept == many[:len(kept)], "cap must keep a front-prefix, whole"
    assert len(s2.initial_prompt) <= osw.PROMPT_CHAR_BUDGET
    # the regurgitation guard still covers phrases the prompt dropped
    assert s2.commits.is_vocab_echo("Term399 Term398 Term399")
    # the trie/vocab-run list is the caller's full list, unchanged
    assert s2.commits.vocab_phrases == many
    # CJK weighting: CJK chars cost ~a token each, so far fewer fit
    zh = [f"名字{i:02d}" for i in range(200)]
    s3 = osw._Session(FakeEngine(), None, bias_phrases=zh)
    s3._watchdog_stop.set()
    assert len(s3.initial_prompt) < osw.PROMPT_CHAR_BUDGET / 2


class LevelScriptedEngine:
    """Returns empty text for quiet-marked clips (peak < 0.07) and real text
    otherwise — deterministic control of the empty-window carry from audio
    content, independent of call interleaving."""

    def __init__(self):
        self.calls: list[int] = []

    def transcribe(self, audio, **kw):
        self.calls.append(len(audio))
        if len(audio) and float(np.abs(audio).max()) < 0.07:
            return {"text": ""}
        return {"text": f"seg{len(self.calls)}:{len(audio)}"}


def test_boundary_span_is_the_turn_speech_not_the_window():
    """FIXME at _commit_boundary (2026-08-27 on-device): the reported span
    was the OPEN SEGMENT [seg_base, cut), so a turn that starts after
    leading silence inherited the silence into spoken_at/duration_s. The
    span must start at the turn's own speech start."""
    eng = FakeEngine()
    s = make_session(eng)
    s.sys.push(loud(1.0, 0.1))    # turn 0: [0, 1.0)
    s.sys.push(quiet(1.0))        # closes turn 0 at 1.0
    drain(s.sys)
    time.sleep(0.2); drain(s.sys)
    s.sys.push(quiet(1.5))        # dead air: seg_base stays at 1.0
    s.sys.push(loud(1.2, 0.1))    # turn 1 speech: [3.5, 4.7)
    s.sys.push(quiet(1.0))        # closes turn 1 at 4.7
    drain(s.sys)
    time.sleep(0.2); drain(s.sys)
    ev = s.commits.drain_feed()
    assert len(ev) == 2, ev
    assert abs(ev[0]["start"] - 0.0) < 0.05 and abs(ev[0]["end"] - 1.0) < 0.05
    assert abs(ev[1]["start"] - 3.5) < 0.05, \
        f"span start must be the turn's speech start, got {ev[1]['start']}"
    assert abs(ev[1]["end"] - 4.7) < 0.05


def test_carry_keeps_the_first_carried_turns_speech_start():
    """Empty-window carry holds seg_base — the NEXT commit's window spans two
    turns. Its reported span must start at the FIRST carried turn's speech
    (those words are in the committed text), not at seg_base's dead air, and
    not at the second turn's start (which would exclude the carried words)."""
    eng = LevelScriptedEngine()
    s = make_session(eng)
    s.sys.push(loud(1.0, 0.35))   # turn 0: [0, 1.0) -> commits
    s.sys.push(quiet(1.0))
    drain(s.sys)
    time.sleep(0.2); drain(s.sys)
    s.sys.push(loud(1.2, 0.015))  # turn A: [2.0, 3.2) peak~0.06 -> EMPTY -> carry
    s.sys.push(quiet(1.0))
    drain(s.sys)
    time.sleep(0.2); drain(s.sys)
    s.sys.push(loud(1.5, 0.35))   # turn B: [4.2, 5.7) -> commits the superset
    s.sys.push(quiet(1.0))
    drain(s.sys)
    time.sleep(0.3); drain(s.sys)
    ev = s.commits.drain_feed()
    assert len(ev) == 2, ev       # turn 0 + the carried superset (A stayed open)
    sup = ev[1]
    assert abs(sup["start"] - 2.0) < 0.05, \
        f"carried span must start at turn A's speech (2.0), got {sup['start']}"
    assert abs(sup["end"] - 5.7) < 0.05


def test_carry_rearms_a_retry_boundary_on_continuous_speech():
    """The on-device latency tail's worst lines: an empty/fragment carry
    under CONTINUOUS speech used to wait for the NEXT turn close — a full
    MAX_OPEN_TURN_S window (6.15 s) — before the carried words could commit
    (device: duration_s 12.3-15.1 s lines at 7.5-9.5 s word latency, 2-3%
    of turns choke but every choke manufactures one tail line). A carry now
    re-arms a bounded retry boundary CARRY_RETRY_S after the choked cut, so
    the superset window (with recovery context) decodes ~2.5 s later, not a
    window later. Retry only fires while a turn is OPEN (continuous voice);
    with a real gap coming, the gap boundary owns the recovery as before."""
    eng = LevelScriptedEngine()
    s = osw._Session(eng, mean_voice_embed)
    ch = s.sys
    ch.push(loud(1.0, 0.35))      # turn 0: [0, 1.0) -> commits
    ch.push(quiet(1.0))
    drain(ch)
    time.sleep(0.2); drain(ch)
    ch.push(loud(1.2, 0.015))     # turn A: [2.0, 3.2) peak~0.06 -> EMPTY -> carry
    ch.push(quiet(1.0))
    drain(ch)
    time.sleep(0.2); drain(ch)
    assert len(s.commits.drain_feed()) == 1          # only turn 0 so far
    # continuous speech resumes and NEVER pauses; commit must not wait for
    # a turn close (none comes until the 6 s force-close)
    for _ in range(9):            # 1.8 s of continuous voice: crosses
        ch.push(loud(0.2, 0.35))  # retry_at = cut(3.2) + 2.5 = 5.7 < 6.0
        time.sleep(0.02)
    drain(ch)
    time.sleep(0.2); drain(ch)
    ev = s.commits.drain_feed()
    assert len(ev) == 1, f"carry retry must commit the superset, got {ev}"
    assert abs(ev[0]["start"] - 2.0) < 0.05          # carried speech start
    assert ev[0]["end"] >= 5.6                        # retry cut, not a close


def test_carry_retry_fires_once_per_chain(monkeypatch):
    """The fragment heuristic cannot tell a choke from real filler ("So...",
    "um"), so an uncapped retry re-armed every 2.5 s on far-field audio —
    pure engine churn (AMI A/B, 2026-08-27). One retry per carry chain: a
    still-choked chain falls back to waiting for the real boundary."""
    fired = []
    monkeypatch.setattr(osw, "_dbg",
                        lambda m: fired.append(m) if "carry-retry" in m else None)
    eng = LevelScriptedEngine()
    s = osw._Session(eng, mean_voice_embed)
    ch = s.sys
    ch.push(loud(1.0, 0.35))      # turn 0 commits
    ch.push(quiet(1.0))
    drain(ch)
    time.sleep(0.2); drain(ch)
    ch.push(loud(1.2, 0.015))     # turn A -> EMPTY -> carry (retry arms)
    ch.push(quiet(1.0))
    drain(ch)
    time.sleep(0.2); drain(ch)
    # 6 s of continuous CHOKE-LEVEL voice: retry 1 fires, re-chokes (still
    # peak < 0.07) -> the chain must NOT re-arm a second retry
    for _ in range(30):
        ch.push(loud(0.2, 0.015))
        time.sleep(0.01)
    drain(ch)
    time.sleep(0.3); drain(ch)
    assert len(fired) == 1, fired


def test_carry_retry_expires_unused_in_silence(monkeypatch):
    """A retry armed under a choke whose speech then STOPS must expire, not
    fire 20 s later at the next voice (observed on ES2004a: cut 134.0 armed
    136.5, silence till ~154, retry fired 156.6 decoding a 25 s window that
    the next gap boundary already owned — a wasted decode that can also put
    the new turn's opening words under the old speaker). The pause itself
    disproves the continuous-speech premise the retry exists for."""
    fired = []
    monkeypatch.setattr(osw, "_dbg",
                        lambda m: fired.append(m) if "carry-retry" in m else None)
    eng = LevelScriptedEngine()
    s = osw._Session(eng, mean_voice_embed)
    ch = s.sys
    ch.push(loud(1.0, 0.35))      # turn 0 commits
    ch.push(quiet(1.0))
    drain(ch)
    time.sleep(0.2); drain(ch)
    ch.push(loud(1.2, 0.015))     # turn A -> EMPTY -> carry (retry arms)
    ch.push(quiet(1.0))           # gap closes turn A
    drain(ch)
    time.sleep(0.2); drain(ch)
    ch.push(quiet(4.0))           # retry_at passes ENTIRELY in silence
    ch.push(loud(1.5, 0.35))      # speech resumes: turn B
    ch.push(quiet(1.0))           # gap closes B -> boundary owns the superset
    drain(ch)
    time.sleep(0.3); drain(ch)
    assert fired == [], "retry must expire unused in silence"
    ev = s.commits.drain_feed()
    assert len(ev) == 2            # turn 0 + the gap-boundary superset
    assert abs(ev[1]["start"] - 2.0) < 0.05   # carried speech start survives




def test_live_beam_rides_the_wire_to_both_channels(monkeypatch):
    """Two-pass decision (2026-08-30): the notes host asks for per-turn
    beam-4 live decodes via stream_start's live_beam. The value must reach
    BOTH channel pipelines and echo in the reply; without it the legacy
    greedy default (1) holds; DICTATION_NOTES_LIVE_BEAM stays the operator
    override and outranks the wire."""
    eng = FakeEngine()
    monkeypatch.setattr(osw, "_get_engine", lambda: eng)
    monkeypatch.setattr(osw, "_make_embed_fn", lambda: mean_voice_embed)
    monkeypatch.delenv("DICTATION_NOTES_LIVE_BEAM", raising=False)

    r = osw.start({"op": "stream_start", "engine": "whisper", "live_beam": 4})
    assert r["ok"] and r["live_beam"] == 4
    s = osw._sessions[r["stream_id"]]
    assert s.mic.beam_size == 4 and s.sys.beam_size == 4
    osw.cancel({"op": "stream_cancel", "stream_id": r["stream_id"]})

    r = osw.start({"op": "stream_start", "engine": "whisper"})
    assert r["ok"] and r["live_beam"] == 1
    s = osw._sessions[r["stream_id"]]
    assert s.mic.beam_size == 1 and s.sys.beam_size == 1
    osw.cancel({"op": "stream_cancel", "stream_id": r["stream_id"]})

    monkeypatch.setenv("DICTATION_NOTES_LIVE_BEAM", "2")
    r = osw.start({"op": "stream_start", "engine": "whisper", "live_beam": 4})
    assert r["ok"] and r["live_beam"] == 2
    s = osw._sessions[r["stream_id"]]
    assert s.mic.beam_size == 2 and s.sys.beam_size == 2
    osw.cancel({"op": "stream_cancel", "stream_id": r["stream_id"]})

    monkeypatch.setenv("DICTATION_NOTES_LIVE_BEAM", "junk")
    r = osw.start({"op": "stream_start", "engine": "whisper", "live_beam": 4})
    assert r["ok"] and r["live_beam"] == 1
    osw.cancel({"op": "stream_cancel", "stream_id": r["stream_id"]})


# ---------------------------------------------------------------------------
# D2 arm: commit-time attribution from the DZ bank's current VBx state
# (DICTATION_NOTES_COMMIT_BANK_LABEL, default OFF). Exercised through the real
# _Session._settled_label_for on a stand-in carrying only what it touches.
# ---------------------------------------------------------------------------
class _D2FakeBank:
    """EmbeddingBank stand-in: a non-None last_state and a scripted
    label_for_span reply (idx, evidence, margin); records every call so
    'no bank calls' is provable."""

    class _State:
        def __init__(self, centroids):
            self.centroids = centroids

    def __init__(self, result, has_state=True, centroids=None):
        self.result = result
        self.calls: list[tuple] = []
        self.last_state = self._State(centroids) if has_state else None

    def label_for_span(self, a, b):
        self.calls.append((a, b))
        return self.result


class _D2Session:
    """_Session stand-in: the real sourcing methods over only the state they
    touch (settled turns, DZ bank, letter book, counters)."""
    _bank_label_for = osw._Session._bank_label_for
    _bank_letter_for = osw._Session._bank_letter_for
    _BANK_LETTER_IDENTITY_COS = osw._Session._BANK_LETTER_IDENTITY_COS

    def __init__(self, turns=(), bank=None, book=None):
        self._settled_turns = list(turns)
        self._dz_bank = bank
        self._bo_letter_book = {} if book is None else book
        self.commit_source_counts = {"beam": 0, "greedy": 0,
                                     "settled": 0, "registry": 0, "bank": 0}


def _counts(**kw):
    base = {"beam": 0, "greedy": 0, "settled": 0, "registry": 0, "bank": 0}
    base.update(kw)
    return base


def _settled(sess, a, b):
    return osw._Session._settled_label_for(sess, a, b)


def test_commit_bank_tier_off_is_inert(monkeypatch):
    """Flag off (the default): sourcing outcomes and counters are identical
    to the pre-D2 code and the bank is never consulted — even when it could
    have answered."""
    monkeypatch.delenv("DICTATION_NOTES_COMMIT_BANK_LABEL", raising=False)
    monkeypatch.delenv("DICTATION_NOTES_COMMIT_SETTLED_LABEL", raising=False)
    bank = _D2FakeBank((0, 2.0, 99.0))
    # no re-cluster published yet: uncounted early None (historical)
    s = _D2Session(bank=bank)
    assert _settled(s, 0.0, 2.0) is None
    assert s.commit_source_counts == _counts()
    # settled turns exist but don't cover the span: registry fallback
    s = _D2Session(turns=[(10.0, 12.0, "Speaker A")], bank=bank)
    assert _settled(s, 0.0, 2.0) is None
    assert s.commit_source_counts == _counts(registry=1)
    # settled coverage wins as before
    s = _D2Session(turns=[(0.0, 2.0, "Speaker A")], bank=bank)
    assert _settled(s, 0.5, 1.5) == "Speaker A"
    assert s.commit_source_counts == _counts(settled=1)
    # explicit "0" is the same as unset
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_BANK_LABEL", "0")
    s = _D2Session(turns=[(10.0, 12.0, "Speaker A")], bank=bank)
    assert _settled(s, 0.0, 2.0) is None
    assert s.commit_source_counts == _counts(registry=1)
    assert bank.calls == []


def test_commit_bank_tier_labels_uncovered_span(monkeypatch):
    """Flag on + no settled coverage + bank evidence -> the bank's speaker,
    counted under "bank" (not "registry"), lettered positionally when the
    session letter book is empty (today's DZ path) and via the book when it
    covers the index (post wave1/dz-stable-letters)."""
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_BANK_LABEL", "1")
    monkeypatch.delenv("DICTATION_NOTES_COMMIT_SETTLED_LABEL", raising=False)
    bank = _D2FakeBank((1, 1.7, 0.8))
    s = _D2Session(turns=[(10.0, 12.0, "Speaker A")], bank=bank)
    assert _settled(s, 0.0, 2.0) == "Speaker B"          # empty book
    assert s.commit_source_counts == _counts(bank=1)
    assert bank.calls == [(0.0, 2.0)]
    # no re-cluster published yet: the bank still answers
    s = _D2Session(bank=bank)
    assert _settled(s, 4.0, 6.0) == "Speaker B"
    assert s.commit_source_counts == _counts(bank=1)
    # letter-keyed, index-ordered book (the registry's idx->letter scheme)
    book = {"Speaker A": np.ones(4), "Speaker B": np.ones(4)}
    s = _D2Session(turns=[(10.0, 12.0, "Speaker A")], bank=bank, book=book)
    assert _settled(s, 0.0, 2.0) == "Speaker B"
    # index-keyed book variant is honored
    s = _D2Session(bank=bank, book={1: "Speaker Q"})
    assert _settled(s, 0.0, 2.0) == "Speaker Q"
    # a book that does NOT cover the index falls back positionally
    s = _D2Session(bank=bank, book={"Speaker A": np.ones(4)})
    assert _settled(s, 0.0, 2.0) == "Speaker B"
    # positional letters past Z follow the file's convention
    s = _D2Session(bank=_D2FakeBank((26, 1.0, 0.4)))
    assert _settled(s, 0.0, 2.0) == "Speaker 26"


def test_commit_bank_tier_letter_follows_book_by_centroid_identity(monkeypatch):
    """Post wave1/dz-stable-letters the DZ book's letters are assigned by
    overlap continuity, so idx 0 may be "Speaker B". The snapshot's centroid
    for idx and the book's centroid for that voice are the SAME VBx vector
    (one cluster round feeds both): the letter is resolved by identity, not
    by position. A book from a different round (no identical vector) falls
    back to the prefix/positional rule."""
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_BANK_LABEL", "1")
    monkeypatch.delenv("DICTATION_NOTES_COMMIT_SETTLED_LABEL", raising=False)
    v0 = np.array([3.0, 4.0, 0.0, 0.0])           # raw (unnormalized) sums
    v1 = np.array([0.0, 0.0, 5.0, 12.0])
    snap = np.stack([v0 / 5.0, v1 / 13.0])        # snapshot: L2-normalized
    bank = _D2FakeBank((0, 1.5, 0.7), centroids=snap)
    book = {"Speaker B": v0, "Speaker A": v1}     # permuted letters
    s = _D2Session(bank=bank, book=book)
    assert _settled(s, 0.0, 2.0) == "Speaker B"
    bank1 = _D2FakeBank((1, 1.5, 0.7), centroids=snap)
    s = _D2Session(bank=bank1, book=book)
    assert _settled(s, 0.0, 2.0) == "Speaker A"
    # a stale book (other round's vectors): identity fails -> prefix rule
    stale = {"Speaker A": np.array([1.0, 1.0, 1.0, 1.0]),
             "Speaker B": np.array([1.0, -1.0, 1.0, -1.0])}
    s = _D2Session(bank=bank1, book=stale)
    assert _settled(s, 0.0, 2.0) == "Speaker B"
    assert s.commit_source_counts == _counts(bank=1)


def test_commit_bank_tier_falls_through_without_coverage(monkeypatch):
    """Flag on but the bank has nothing for the span (None), no bank, or a
    bank with no clustering yet -> exactly the pre-D2 registry fallback."""
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_BANK_LABEL", "1")
    monkeypatch.delenv("DICTATION_NOTES_COMMIT_SETTLED_LABEL", raising=False)
    turns = [(10.0, 12.0, "Speaker A")]
    bank = _D2FakeBank(None)
    s = _D2Session(turns=turns, bank=bank)
    assert _settled(s, 0.0, 2.0) is None
    assert s.commit_source_counts == _counts(registry=1)
    assert bank.calls == [(0.0, 2.0)]
    s = _D2Session(turns=turns, bank=None)
    assert _settled(s, 0.0, 2.0) is None
    assert s.commit_source_counts == _counts(registry=1)
    cold = _D2FakeBank((0, 2.0, 99.0), has_state=False)
    s = _D2Session(turns=turns, bank=cold)
    assert _settled(s, 0.0, 2.0) is None
    assert s.commit_source_counts == _counts(registry=1)
    assert cold.calls == []                       # no last_state: not asked
    # a raising bank is best-effort: swallowed, registry fallback
    class Boom(_D2FakeBank):
        def label_for_span(self, a, b):
            raise RuntimeError("boom")
    s = _D2Session(turns=turns, bank=Boom(None))
    assert _settled(s, 0.0, 2.0) is None
    assert s.commit_source_counts == _counts(registry=1)


def test_commit_bank_tier_never_outranks_settled(monkeypatch):
    """The settled turns stay the first tier: when they cover the span the
    bank is not consulted, flag on or off."""
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_BANK_LABEL", "1")
    monkeypatch.delenv("DICTATION_NOTES_COMMIT_SETTLED_LABEL", raising=False)
    bank = _D2FakeBank((1, 2.0, 99.0))
    s = _D2Session(turns=[(0.0, 2.0, "Speaker A")], bank=bank)
    assert _settled(s, 0.5, 1.5) == "Speaker A"
    assert s.commit_source_counts == _counts(settled=1)
    assert bank.calls == []


def test_session_commit_source_counts_has_bank_counter():
    s = osw._Session(FakeEngine(), mean_voice_embed)
    s._watchdog_stop.set()
    assert s.commit_source_counts == _counts()


# ---------------------------------------------------------------------------
# A cancelled preview's queued decode never reaches the engine
# ---------------------------------------------------------------------------
# On release the host cancels the preview stream and then sends transcribe.
# A live decode that was already waiting for the engine used to run anyway,
# after the hold, on a stream nobody reads, and delay the next hold by up to
# one 12 s window. Re-checked under the channel lock once the engine is held.

@pytest.mark.parametrize("decode, empty", [
    (lambda ch: ch._transcribe2(loud(1.0)), ("", None)),
    (lambda ch: ch._transcribe_live(loud(1.0))[:3], ("", None, [])),
], ids=["transcribe2", "transcribe_live"])
def test_a_decode_queued_behind_a_hold_never_runs_once_the_stream_is_cancelled(monkeypatch, decode, empty):
    import threading

    from transcription_worker import engine_lock

    engine = FakeEngine()
    monkeypatch.setattr(osw, "_get_engine", lambda: engine)
    monkeypatch.setattr(osw, "_make_embed_fn", lambda: None)
    started = osw.start({"op": "stream_start", "engine": "whisper", "client": "dictation",
                         "languages": ["en"], "rolling_stable": False})
    channel = osw._sessions[started["stream_id"]].mic
    results = []
    # The hold's transcribe owns the engine, as op_transcribe does on release.
    with osw._ENGINE_LOCK.held(engine_lock.HOLD, "dictation:transcribe"):
        queued = threading.Thread(target=lambda: results.append(decode(channel)), daemon=True)
        queued.start()
        deadline = time.monotonic() + 5.0
        while osw._ENGINE_LOCK.waiting(engine_lock.CAPTION) == 0 and time.monotonic() < deadline:
            time.sleep(0.005)
        assert osw._ENGINE_LOCK.waiting(engine_lock.CAPTION) == 1, "the decode never queued"
        assert osw.cancel({"stream_id": started["stream_id"]}) == {"ok": True}
    queued.join(5.0)

    assert not queued.is_alive()
    assert engine.calls == []
    assert results == [empty]


def test_a_decode_on_a_live_stream_still_runs_after_waiting_for_a_hold(monkeypatch):
    """The mirror: waiting behind a hold is normal, and only a torn-down
    stream skips its decode."""
    import threading

    from transcription_worker import engine_lock

    engine = FakeEngine()
    monkeypatch.setattr(osw, "_get_engine", lambda: engine)
    monkeypatch.setattr(osw, "_make_embed_fn", lambda: None)
    started = osw.start({"op": "stream_start", "engine": "whisper", "client": "dictation",
                         "languages": ["en"], "rolling_stable": False})
    channel = osw._sessions[started["stream_id"]].mic
    results = []
    with osw._ENGINE_LOCK.held(engine_lock.HOLD, "dictation:transcribe"):
        queued = threading.Thread(target=lambda: results.append(channel._transcribe2(loud(1.0))),
                                  daemon=True)
        queued.start()
        deadline = time.monotonic() + 5.0
        while osw._ENGINE_LOCK.waiting(engine_lock.CAPTION) == 0 and time.monotonic() < deadline:
            time.sleep(0.005)
    queued.join(5.0)
    osw.cancel({"stream_id": started["stream_id"]})

    assert engine.calls == [len(loud(1.0))]
    assert results[0][0] == f"seg1:{len(loud(1.0))}"


def test_source_preview_has_no_implicit_speaker_model():
    assert osw._make_embed_fn() is None
