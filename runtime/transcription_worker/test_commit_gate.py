"""THE unified commit-text gate (regression fixture):
silence hallucinations reached the caption stream through this week's new
decode/commit paths. The fix is structural, not another spot-check: ONE
named gate (`_CommitLog.gate`) that every committed line must pass —
legacy turn-close, commit-on-stable, boundary-cache reuse, carry-retry,
finish drain/tail — and `add()` is the only appender, so a future decode
path cannot emit unguarded text by construction.

Two enforcement layers, both tested here:
- functional: with the gate rejecting everything, NO path can commit;
- structural (AST ratchet): the commit-log lists are appended to ONLY
  inside `_CommitLog.add`, which consults the gate first.
"""

import ast
import inspect
import time

import numpy as np

from transcription_worker import ops_stream_whisper as osw
from transcription_worker import hallucination


SR = osw.SAMPLE_RATE


def loud(seconds: float, level: float = 0.1) -> np.ndarray:
    rng = np.random.default_rng(7)
    return (level * rng.standard_normal(int(seconds * SR))
            ).astype(np.float32).clip(-1, 1)


def quiet(seconds: float) -> np.ndarray:
    return np.zeros(int(seconds * SR), dtype=np.float32)


def drain(ch, timeout=5.0):
    time.sleep(0.05)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with ch.lock:
            if not ch.decoding and not ch.pending_boundaries:
                return
        time.sleep(0.01)
    raise TimeoutError("decode thread never finished")


# ---------------------------------------------------------------------------
# the gate itself
# ---------------------------------------------------------------------------

def test_gate_exists_and_add_routes_through_it(monkeypatch):
    """`add` must consult the gate and append EXACTLY what it returns."""
    log = osw._CommitLog()
    seen = []

    def fake_gate(self, speaker, text, channel="sys"):
        seen.append((speaker, text, channel))
        return "gated text"

    monkeypatch.setattr(osw._CommitLog, "gate", fake_gate)
    log.add("You", "raw text", channel="mic", span=(0.0, 1.0))
    assert seen == [("You", "raw text", "mic")]
    assert log.snapshot() == ["[You] gated text"]


def test_gate_drops_hallucination_pleasantries_and_filler():
    """The previously-shipped guard set, reachable through the ONE gate:
    whole-utterance pleasantries, repeated-filler loops of them, and stray
    single-word coughs are dropped; real speech passes."""
    log = osw._CommitLog()
    for junk in ("Thank you for watching.",
                 "Thanks for watching!",
                 "Thank you. Thank you. Thank you.",
                 "you",
                 "谢谢观看"):
        assert log.gate("You", junk, channel="mic") == "", junk
    kept = log.gate("You", "the numbers look good this week", channel="mic")
    assert "numbers look good" in kept


def test_gate_rejection_is_a_hard_drop_for_add():
    log = osw._CommitLog()
    log.add("You", "Thank you for watching.", channel="mic", span=(0.0, 1.0))
    assert log.snapshot() == []
    assert log.drain_feed() == []


# ---------------------------------------------------------------------------
# no path can emit around the gate (functional enforcement)
# ---------------------------------------------------------------------------

class ScriptableEngine:
    """Greedy+timed engine: loud clips decode to scripted text (popped in
    call order), sub-0.07-peak clips choke to "" (the carry trigger)."""

    def __init__(self, script):
        self.script = list(script)
        self.calls = []

    def _text(self, clip):
        if float(np.abs(clip).max()) < 0.07:
            return ""
        return self.script.pop(0) if self.script else "steady real words"

    def transcribe(self, clip, **kw):
        self.calls.append(("greedy", kw.get("language"),
                           kw.get("initial_prompt")))
        return {"text": self._text(clip), "stats": {"language": "en"}}

    def transcribe_timed(self, clip, **kw):
        self.calls.append(("timed", kw.get("language"),
                           kw.get("initial_prompt")))
        text = self._text(clip)
        if not text:
            return {"spans": [], "stats": {}}
        dur = len(clip) / SR
        return {"spans": [(0.1, max(0.2, dur - 0.5), text, "en")],
                "stats": {}}


def test_no_commit_path_bypasses_the_gate(monkeypatch):
    """With the gate rejecting EVERYTHING, a session driven through the
    legacy turn-close path, the periodic decode, and the finish drain+tail
    must emit NOTHING — proving every commit path consults the gate."""
    monkeypatch.setattr(osw._CommitLog, "gate",
                        lambda self, speaker, text, channel="sys": "")
    monkeypatch.setattr(osw, "DECODE_COOLDOWN_S", 0.0)
    monkeypatch.setattr(osw, "MIN_NEW_AUDIO_S", 0.05)
    eng = ScriptableEngine(["hello there team"] * 20)
    s = osw._Session(eng, None)
    s._watchdog_stop.set()
    ch = s.mic
    ch.push(loud(1.5, 0.35))          # legacy periodic decode
    ch.push(quiet(1.0))               # turn close -> boundary commit
    drain(ch)
    time.sleep(0.2)
    drain(ch)
    ch.push(loud(1.0, 0.35))
    ch.finish()                       # drain + tail commit paths
    assert ch.commits.snapshot() == []
    assert ch.commits.drain_feed() == []
    assert eng.calls, "sanity: the engine was actually exercised"
    s.teardown()


# ---------------------------------------------------------------------------
# structural ratchet: add() is the ONLY appender
# ---------------------------------------------------------------------------

def test_commit_lists_are_appended_only_inside_add():
    """AST ratchet: `lines` / `line_meta` / `feed_events` grow ONLY inside
    `_CommitLog.add`. A future decode path that appends directly — skipping
    the gate — fails this test before it can ship."""
    tree = ast.parse(inspect.getsource(osw))
    owners = []

    def walk(node, func_stack):
        for child in ast.iter_child_nodes(node):
            stack = func_stack
            if isinstance(child, (ast.FunctionDef, ast.AsyncFunctionDef)):
                stack = func_stack + [child.name]
            if (isinstance(child, ast.Call)
                    and isinstance(child.func, ast.Attribute)
                    and child.func.attr == "append"
                    and isinstance(child.func.value, ast.Attribute)
                    and child.func.value.attr in
                    ("lines", "line_meta", "feed_events")):
                owners.append((child.func.value.attr,
                               stack[-1] if stack else "<module>"))
            walk(child, stack)

    walk(tree, [])
    assert owners, "sanity: found no commit-list appends at all"
    outside = [o for o in owners if o[1] != "add"]
    assert outside == [], (
        f"commit-log lists appended outside _CommitLog.add: {outside} — "
        f"every commit must route through _CommitLog.add -> gate")


def test_add_source_consults_gate_first():
    """add()'s first act is the gate — no cleanup or append precedes it."""
    src = inspect.getsource(osw._CommitLog.add)
    gate_at = src.find("self.gate(")
    append_at = src.find(".append(")
    assert gate_at != -1, "add() does not call the gate"
    assert append_at == -1 or gate_at < append_at


# ---------------------------------------------------------------------------
# silence-hallucination scenarios through the NEW paths
# ---------------------------------------------------------------------------

def mean_voice_embed(audio):
    v = np.zeros(4, dtype=np.float32)
    v[int(float(np.abs(audio).mean()) * 10) % 4] = 1.0
    return v


def test_carry_retry_hallucination_is_dropped_not_committed(monkeypatch):
    """Regression fixture: a low-signal window chokes,
    the carry RETRY re-decodes the superset ~2.5s later, and whisper answers
    the still-low-signal window with a stock pleasantry. The retry's commit
    must pass the gate and be dropped — never captioned."""
    monkeypatch.setattr(osw, "DECODE_COOLDOWN_S", 0.0)
    monkeypatch.setattr(osw, "MIN_NEW_AUDIO_S", 0.05)
    # call order: turn-0 commit text, then the retry superset's decode
    eng = ScriptableEngine(["hello there team",
                            "Thank you for watching."])
    s = osw._Session(eng, mean_voice_embed)
    s._watchdog_stop.set()
    ch = s.sys
    ch.push(loud(1.0, 0.35))      # turn 0 -> commits real text
    ch.push(quiet(1.0))
    drain(ch)
    time.sleep(0.2)
    drain(ch)
    ch.push(loud(1.2, 0.015))     # low-signal turn -> "" -> carry (retry arms)
    ch.push(quiet(1.0))
    drain(ch)
    time.sleep(0.2)
    drain(ch)
    for _ in range(9):            # continuous voice crosses retry_at
        ch.push(loud(0.2, 0.35))
        time.sleep(0.02)
    drain(ch)
    time.sleep(0.2)
    drain(ch)
    lines = ch.commits.snapshot()
    assert not any("thank you" in l.lower() for l in lines), lines
    assert any("hello there team" in l.lower() for l in lines), lines
    s.teardown()


def test_junk_utterance_matches_longest_phrase_first():
    """Pre-existing order bug found building the gate: replacing 'thank
    you' first broke 'thank you for watching' mid-match, so the flagship
    pleasantry was never a junk UTTERANCE (only strip_trailing caught it,
    and only with audio evidence). Longest-first replacement fixes the
    whole family."""
    assert hallucination.is_junk_utterance("Thank you for watching.")
    assert hallucination.is_junk_utterance("Thank you for watching!")
    assert hallucination.is_junk_utterance(
        "Thanks for watching. Please subscribe!")
    assert hallucination.is_junk_utterance("谢谢大家观看。")
    # real speech CONTAINING a pleasantry is not junk
    assert not hallucination.is_junk_utterance(
        "thank you for watching the numbers so closely this week")


# ---------------------------------------------------------------------------
# regurgitation guard vs NATURAL speech containing vocab terms:
# a speaker legitimately SAYS the product terms; the guard must only fire on
# prompt-echo — bare term lists / repetition — never on real fragments)
# ---------------------------------------------------------------------------

DEVICE_VOCAB = ["Fable", "Knemo", "Trovioo", "PadForge", "CedarCore",
                "Asteron", "deepfake", "diarization", "SignalMixer",
                "CedarTagger"]


def vocab_log(phrases=DEVICE_VOCAB):
    log = osw._CommitLog()
    log.vocab_norm = "".join(
        ch for ch in ", ".join(phrases).lower() if ch.isalnum())
    log.vocab_phrases = list(phrases)
    return log


class TestVocabEchoNaturalSpeech:
    def test_two_term_fragment_survives(self):
        """A 2-word noun phrase of vocab terms is normal speech, not a
        recitation — the stream pipeline commits fragments constantly."""
        log = vocab_log()
        assert not log.is_vocab_echo("Asteron CedarCore")
        assert log.gate("You", "Asteron CedarCore", channel="mic")

    def test_substring_into_concatenated_vocab_never_counts(self):
        """'core' is NOT a vocab word even though 'cedarcore' contains it —
        the old substring-into-the-joined-blob check ate real fragments."""
        log = vocab_log()
        assert not log.is_vocab_echo("Fable on CedarCore")
        assert log.gate("You", "Fable on CedarCore", channel="mic")

    def test_natural_sentences_with_terms_survive(self):
        log = vocab_log()
        for line in ("We're testing Fable on CedarCore.",
                     "The Knemo integration works.",
                     "It runs on Asteron",
                     "我们在 CedarCore 上测试 Fable"):
            assert not log.is_vocab_echo(line)
            assert log.gate("You", line, channel="mic")

    def test_full_list_recitation_still_drops(self):
        log = vocab_log()
        assert log.is_vocab_echo(
            "Fable, Knemo, Trovioo, PadForge, CedarCore, Asteron, "
            "deepfake, diarization, SignalMixer, CedarTagger")
        assert log.gate("Speaker A", "Fable, Knemo, Trovioo, PadForge, "
                        "CedarCore", channel="sys") == ""

    def test_bare_term_list_out_of_prompt_order_still_drops(self):
        log = vocab_log()
        assert log.is_vocab_echo("Fable CedarCore Knemo PadForge")

    def test_repeated_term_still_drops(self):
        """Regurgitation repeats vocab items with separators
        ('桑普拉斯, 桑普拉斯') — the repetition branch must survive the
        natural-speech fix."""
        log = vocab_log(["桑普拉斯", "德约科维奇"])
        assert log.is_vocab_echo("桑普拉斯, 桑普拉斯")

    def test_multiword_phrase_name_list_still_drops(self):
        """Parity with test_presentation_mode's caller-vocab guard: a bare
        recitation of two multi-word vocab names (4 word pieces) is echo."""
        log = vocab_log(["Jossie Lin", "Yurin Zhang"])
        assert log.is_vocab_echo("Jossie Lin, Yurin Zhang")


# ---------------------------------------------------------------------------
# the commit gate must not run DISPLAY-ONLY cleanup on transcript text
# (stream_repetition.clean is display-only cosmetics: it collapses Chinese
# reduplication and lowercases acronyms.)
# ---------------------------------------------------------------------------

class TestCommitTextNotDisplayCleaned:
    def test_chinese_reduplication_survives_commit(self):
        log = osw._CommitLog()
        for line in ("谢谢大家", "我们好好聊聊", "看看这个", "等等我"):
            assert log.gate("You", line, channel="mic") == line

    def test_midsentence_i_and_acronyms_keep_case(self):
        log = osw._CommitLog()
        assert log.gate("You", "Yes, I agree", channel="mic") == "Yes, I agree"
        assert log.gate("You", "the NPU is busy right now",
                        channel="mic") == "the NPU is busy right now"

    def test_legitimate_emphasis_survives_commit(self):
        log = osw._CommitLog()
        assert log.gate("You", "it is very very good",
                        channel="mic") == "it is very very good"

    def test_loop_tail_still_collapses_without_display_cleaner(self):
        """Whisper loop-tails (3+ repeats) stay covered by
        collapse_phrase_loops — removing the display cleaner loses nothing."""
        log = osw._CommitLog()
        out = log.gate("You", "这套餐都不贵的, 都不贵的, 都不贵的, 都不贵的",
                       channel="mic")
        # collapse_phrase_loops keeps one loop unit + the un-separated tail
        # occurrence — the loop itself is gone (same as before the display
        # cleaner was removed; that cleaner never collapsed separated CJK
        # runs anyway)
        assert out == "这套餐都不贵的, 都不贵的"
