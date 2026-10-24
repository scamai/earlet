"""Presentation mode (env-gated demo fallback) — unit tests.

Covers: script loading (unset/empty/missing env => zero effect, no crash),
phrase extraction (proper nouns, dedup, first-appearance order), bias-merge
ordering (caller vocab keeps prompt-budget priority), the word-level local
aligner (exact hit, near hit under threshold -> snapped to EXACT script
wording, over-threshold untouched, short-line guard, two-sentence spans),
per-session counters, threshold env parsing, the vocab-echo interaction
(script phrases must NOT feed the regurgitation guards, or spoken script
lines get dropped/excised as "vocab echo"), and the consecutive-repeat
guard (the guards' deliberate script blindness let a silence-loop echo of
the same script line commit 4+ times — suppress_repeat drops the echoes in
presentation mode only. These are synthetic presentation fixtures.
"""

import os

import pytest

from . import presentation_mode as pm


SCRIPT = """\
Welcome to the Fable live demo, powered by Luma AI.
Our agent consumes the meeting transcript in real time.
Every committed line reaches the Luma agent in under two seconds.
Fable runs entirely on the CedarCore X Elite NPU.
The transcription worker commits speaker-attributed captions.
Latency stays low because nothing leaves the device.
Thank you for watching our demonstration today.
"""


@pytest.fixture
def script_file(tmp_path):
    p = tmp_path / "script.txt"
    p.write_text(SCRIPT, encoding="utf-8")
    return str(p)


@pytest.fixture
def clean_env(monkeypatch):
    monkeypatch.delenv(pm.ENV_SCRIPT, raising=False)
    monkeypatch.delenv(pm.ENV_SNAP_T, raising=False)
    monkeypatch.delenv(pm.ENV_REPEAT_GAP, raising=False)


# ---------------------------------------------------------------- loading

class TestLoading:
    def test_env_unset_is_none(self, clean_env):
        assert pm.session_from_env() is None

    def test_env_empty_is_none(self, clean_env, monkeypatch):
        monkeypatch.setenv(pm.ENV_SCRIPT, "")
        assert pm.session_from_env() is None

    def test_env_whitespace_is_none(self, clean_env, monkeypatch):
        monkeypatch.setenv(pm.ENV_SCRIPT, "   ")
        assert pm.session_from_env() is None

    def test_missing_file_no_crash(self, clean_env, monkeypatch):
        monkeypatch.setenv(pm.ENV_SCRIPT, "/nonexistent/path/script.txt")
        assert pm.session_from_env() is None  # must not raise

    def test_empty_file_is_none(self, clean_env, monkeypatch, tmp_path):
        p = tmp_path / "empty.txt"
        p.write_text("", encoding="utf-8")
        monkeypatch.setenv(pm.ENV_SCRIPT, str(p))
        assert pm.session_from_env() is None

    def test_loads_script(self, clean_env, monkeypatch, script_file):
        monkeypatch.setenv(pm.ENV_SCRIPT, script_file)
        ps = pm.session_from_env()
        assert ps is not None
        assert ps.script.n_words > 20

    def test_default_threshold(self, clean_env, monkeypatch, script_file):
        monkeypatch.setenv(pm.ENV_SCRIPT, script_file)
        ps = pm.session_from_env()
        assert ps.snap_t == pytest.approx(0.4)

    def test_threshold_env(self, clean_env, monkeypatch, script_file):
        monkeypatch.setenv(pm.ENV_SCRIPT, script_file)
        monkeypatch.setenv(pm.ENV_SNAP_T, "0.25")
        assert pm.session_from_env().snap_t == pytest.approx(0.25)

    def test_threshold_env_garbage_falls_back(self, clean_env, monkeypatch,
                                              script_file):
        monkeypatch.setenv(pm.ENV_SCRIPT, script_file)
        monkeypatch.setenv(pm.ENV_SNAP_T, "banana")
        assert pm.session_from_env().snap_t == pytest.approx(0.4)

    def test_threshold_env_clamped(self, clean_env, monkeypatch, script_file):
        monkeypatch.setenv(pm.ENV_SCRIPT, script_file)
        monkeypatch.setenv(pm.ENV_SNAP_T, "7.0")
        assert 0.0 <= pm.session_from_env().snap_t <= 1.0


# ------------------------------------------------------- phrase extraction

class TestPhraseExtraction:
    def test_proper_nouns_extracted(self):
        script = pm.Script.parse(SCRIPT)
        phrases = script.extract_phrases()
        joined = " | ".join(phrases)
        assert "Luma AI" in phrases
        assert "Fable" in phrases
        assert "CedarCore X Elite" in joined or "CedarCore" in joined

    def test_dedup_keeps_first_appearance_order(self):
        script = pm.Script.parse(
            "Luma AI builds agents. Fable detects fakes. "
            "Luma AI ships fast. Fable is on device.")
        phrases = script.extract_phrases()
        assert phrases.count("Luma AI") == 1
        assert phrases.count("Fable") == 1
        assert phrases.index("Luma AI") < phrases.index("Fable")

    def test_sentence_initial_common_words_not_extracted(self):
        script = pm.Script.parse("The quick fox. And then it ran. But so what.")
        phrases = script.extract_phrases()
        assert "The" not in phrases
        assert "And" not in phrases
        assert "But" not in phrases

    def test_salient_terms_extracted(self):
        script = pm.Script.parse(SCRIPT)
        phrases = [p.lower() for p in script.extract_phrases()]
        # distinctive long content words should be present in some form
        assert any("transcript" in p for p in phrases)

    def test_extraction_bounded(self):
        # a big script must not produce an unbounded phrase list
        big = " ".join(f"Word{i} Alpha{i} means something important." for i in range(500))
        phrases = pm.Script.parse(big).extract_phrases()
        assert len(phrases) <= pm.MAX_SCRIPT_PHRASES


# ------------------------------------------------------------- bias merge

class TestBiasMerge:
    def _session(self, script_text=SCRIPT):
        return pm.PresentationSession(pm.Script.parse(script_text), 0.4)

    def test_caller_vocab_stays_in_front(self):
        ps = self._session()
        merged = ps.extend_bias(["Jossie Lin", "Yurin Zhang"])
        assert merged[:2] == ["Jossie Lin", "Yurin Zhang"]
        assert len(merged) > 2  # script phrases appended after

    def test_none_caller_bias(self):
        ps = self._session()
        merged = ps.extend_bias(None)
        assert merged  # script phrases alone
        assert "Luma AI" in merged

    def test_no_dup_against_caller(self):
        ps = self._session()
        merged = ps.extend_bias(["Luma AI"])
        assert [m.lower() for m in merged].count("luma ai") == 1

    def test_caller_phrases_recorded(self):
        ps = self._session()
        ps.extend_bias(["Jossie Lin"])
        assert ps.caller_phrases == ["Jossie Lin"]


# ---------------------------------------------------------------- matcher

class TestMatcher:
    def _session(self, snap_t=0.4):
        return pm.PresentationSession(pm.Script.parse(SCRIPT), snap_t)

    def test_exact_hit_snaps(self):
        ps = self._session()
        out = ps.snap_line("Our agent consumes the meeting transcript in real time.")
        assert out == "Our agent consumes the meeting transcript in real time."
        assert ps.snapped == 1

    def test_near_hit_snaps_to_exact_wording(self):
        ps = self._session()
        # ASR-style errors: "luma ai" mis-heard, casing/punct lost
        out = ps.snap_line("welcome to the fable live demo powered by luma ai")
        assert out == "Welcome to the Fable live demo, powered by Luma AI."
        assert ps.snapped == 1

    def test_case_and_punct_restored(self):
        ps = self._session()
        out = ps.snap_line("fable runs entirely on the cedarcore x elite npu")
        assert out == "Fable runs entirely on the CedarCore X Elite NPU."

    def test_over_threshold_untouched(self):
        ps = self._session()
        free = "What is the pricing model for enterprise customers exactly"
        assert ps.snap_line(free) == free
        assert ps.snapped == 0
        assert ps.untouched == 1

    def test_unrelated_qa_untouched(self):
        ps = self._session()
        free = ("I was wondering whether you could share more details about "
                "the roadmap for next quarter and integrations")
        assert ps.snap_line(free) == free

    def test_short_line_guard(self):
        ps = self._session(snap_t=0.9)  # even a lax threshold must not snap
        for line in ("okay", "yeah", "thank you", "on the"):
            assert ps.snap_line(line) == line
        assert ps.snapped == 0
        assert ps.untouched == 4

    def test_partial_sentence_snaps_to_window(self):
        ps = self._session()
        out = ps.snap_line("consumes the meeting transcript in real time")
        # window-local: the snap must not drag in the whole sentence's
        # leading words, and must restore exact script tokens
        assert "consumes the meeting transcript in real time" in out
        assert ps.snapped == 1

    def test_span_across_two_sentences(self):
        ps = self._session()
        line = ("latency stays low because nothing leaves the device thank "
                "you for watching our demonstration today")
        out = ps.snap_line(line)
        assert "Latency stays low because nothing leaves the device." in out
        assert "Thank you for watching our demonstration today." in out
        assert ps.snapped == 1

    def test_empty_line_untouched(self):
        ps = self._session()
        assert ps.snap_line("") == ""

    def test_counters(self):
        ps = self._session()
        ps.snap_line("Our agent consumes the meeting transcript in real time.")
        ps.snap_line("completely unrelated words about gardening and cooking recipes")
        assert (ps.snapped, ps.untouched) == (1, 1)

    def test_snap_never_raises_on_weird_input(self):
        ps = self._session()
        for weird in ("...", "!!!", "a b c d e f", "你好世界",
                      "  spaced   out   words  "):
            ps.snap_line(weird)  # must not raise


# ----------------------------------------------- vocab-echo interaction

class TestVocabEchoInteraction:
    """Feeding the WHOLE script's phrases into the regurgitation guards is
    catastrophic: the presenter SPEAKS the script, so committed lines are
    legitimately made of "vocab" words and is_vocab_echo/strip_vocab_run
    would drop or gut them. Presentation phrases must stay OUT of the
    guard sets; caller-provided vocab keeps its guard exactly as before."""

    def _commitlog_with_merged_vocab(self, merged):
        # replicate what _Session.__init__ does with a merged bias list
        from .ops_stream_whisper import _CommitLog
        log = _CommitLog()
        log.vocab_norm = "".join(
            ch for ch in ", ".join(merged).lower() if ch.isalnum())
        log.vocab_phrases = list(merged)
        return log

    def test_interaction_exists_without_fix(self):
        """Proves the hazard is real: with script phrases in the guard set,
        a spoken script line IS dropped as vocab echo."""
        ps = pm.PresentationSession(pm.Script.parse(SCRIPT), 0.4)
        merged = ps.extend_bias(None)
        log = self._commitlog_with_merged_vocab(merged)
        # a real spoken script line: nearly every word is in the merged
        # vocab blob -> the guard mistakes speech for regurgitation
        assert log.is_vocab_echo("Luma AI Fable CedarCore transcript")

    def test_attach_restores_caller_only_guard(self):
        ps = pm.PresentationSession(pm.Script.parse(SCRIPT), 0.4)
        merged = ps.extend_bias(["Jossie Lin"])
        log = self._commitlog_with_merged_vocab(merged)
        ps.attach(log)
        assert log.vocab_phrases == ["Jossie Lin"]
        assert "luma" not in log.vocab_norm
        assert "jossielin" in log.vocab_norm

    def test_spoken_script_line_survives_add(self):
        """End-to-end through _CommitLog.add: a spoken script line must be
        committed (not dropped, not vocab-run-excised) and snapped to the
        exact script wording."""
        ps = pm.PresentationSession(pm.Script.parse(SCRIPT), 0.4)
        merged = ps.extend_bias(None)
        log = self._commitlog_with_merged_vocab(merged)
        ps.attach(log)
        log.add("Speaker A", "welcome to the fable live demo powered by luma ai")
        assert len(log.lines) == 1
        assert log.lines[0] == ("[Speaker A] Welcome to the Fable live demo, "
                                "powered by Luma AI.")

    def test_attach_with_no_caller_vocab_disables_guard(self):
        ps = pm.PresentationSession(pm.Script.parse(SCRIPT), 0.4)
        ps.extend_bias(None)
        from .ops_stream_whisper import _CommitLog
        log = self._commitlog_with_merged_vocab(ps.extend_bias(None))
        ps.attach(log)
        assert log.vocab_norm == ""
        assert log.vocab_phrases == []
        assert not log.is_vocab_echo("Luma AI Fable CedarCore transcript")

    def test_caller_vocab_guard_still_works_after_attach(self):
        """The original regurgitation protection for CALLER vocab must
        survive: a pure recitation of caller terms is still dropped."""
        ps = pm.PresentationSession(pm.Script.parse(SCRIPT), 0.4)
        merged = ps.extend_bias(["Jossie Lin", "Yurin Zhang"])
        log = self._commitlog_with_merged_vocab(merged)
        ps.attach(log)
        assert log.is_vocab_echo("Jossie Lin, Yurin Zhang")

    def test_add_untouched_when_no_presentation(self):
        """_CommitLog with no presentation attached behaves exactly as
        before — the hook must be inert: no snap, and consecutive
        duplicate commits land exactly as today (no repeat suppression
        outside presentation mode)."""
        from .ops_stream_whisper import _CommitLog
        log = _CommitLog()
        assert log.presentation is None
        log.add("You", "Welcome to the fable live demo powered by luma ai")
        assert len(log.lines) == 1
        assert "luma ai" in log.lines[0]  # NOT snapped
        log.add("You", "Welcome to the fable live demo powered by luma ai")
        assert len(log.lines) == 2      # duplicate NOT suppressed
        assert len(log.feed_events) == 2


# ------------------------------------------------- repeat suppression

class TestRepeatSuppression:
    """Presentation-mode echo regression fixture: during
    low-signal stretches the decode loop re-emits the SAME script line and
    the regurgitation guards deliberately let script phrases through, so
    it committed 4+ times on stage. suppress_repeat drops a consecutive
    near-duplicate of a SCRIPT-LIKE line within the repeat gap; short
    interjections and off-script lines always commit."""

    LINE = "Our agent consumes the meeting transcript in real time."
    OTHER = "Fable runs entirely on the CedarCore X Elite NPU."
    OFF_SCRIPT = ("I was wondering whether you could share more details "
                  "about the roadmap for next quarter and integrations")

    def _session(self, snap_t=0.4):
        return pm.PresentationSession(pm.Script.parse(SCRIPT), snap_t)

    def test_identical_script_line_within_gap_suppressed(self):
        ps = self._session()
        assert ps.suppress_repeat("sys", self.LINE, now=100.0) is False
        assert ps.suppress_repeat("sys", self.LINE, now=104.0) is True
        assert ps.suppressed == 1

    def test_near_identical_script_line_suppressed(self):
        # ASR wobble on the echo: one word off, still >= 0.9 similarity
        ps = self._session()
        assert not ps.suppress_repeat("sys", self.LINE, now=0.0)
        wobble = "Our agent consumes the meeting transcripts in real time"
        assert ps.suppress_repeat("sys", wobble, now=3.0) is True

    def test_intervening_different_line_allows_reutterance(self):
        ps = self._session()
        assert not ps.suppress_repeat("sys", self.LINE, now=0.0)
        assert not ps.suppress_repeat("sys", self.OTHER, now=2.0)
        # legitimate re-utterance: the previous commit is now a DIFFERENT
        # line, so the presenter repeating an earlier line must commit
        assert not ps.suppress_repeat("sys", self.LINE, now=4.0)

    def test_reutterance_after_gap_allowed(self):
        ps = self._session()
        assert not ps.suppress_repeat("sys", self.LINE, now=0.0)
        assert not ps.suppress_repeat("sys", self.LINE, now=11.0)  # > 10 s

    def test_sustained_loop_only_first_commits(self):
        # refresh-on-suppress: each suppressed echo renews the window, so
        # a loop at 6 s cadence never escapes until it actually pauses
        ps = self._session()
        assert not ps.suppress_repeat("sys", self.LINE, now=0.0)
        for t in (6.0, 12.0, 18.0):
            assert ps.suppress_repeat("sys", self.LINE, now=t) is True
        assert ps.suppressed == 3

    def test_short_interjection_never_suppressed(self):
        ps = self._session(snap_t=0.9)   # even a lax threshold
        for t in (0.0, 1.0, 2.0):
            assert ps.suppress_repeat("sys", "okay", now=t) is False
        assert ps.suppressed == 0

    def test_off_script_duplicate_not_suppressed(self):
        # free-form Q&A repeated: NOT script-like, must always commit
        ps = self._session()
        assert not ps.suppress_repeat("sys", self.OFF_SCRIPT, now=0.0)
        assert not ps.suppress_repeat("sys", self.OFF_SCRIPT, now=3.0)
        assert ps.suppressed == 0

    def test_channels_do_not_cross_suppress(self):
        ps = self._session()
        assert not ps.suppress_repeat("mic", self.LINE, now=0.0)
        assert not ps.suppress_repeat("sys", self.LINE, now=1.0)
        # sanity: the sys channel's own repeat IS suppressed
        assert ps.suppress_repeat("sys", self.LINE, now=2.0) is True

    def test_stats_counts_suppressed(self):
        ps = self._session()
        ps.suppress_repeat("sys", self.LINE, now=0.0)
        ps.suppress_repeat("sys", self.LINE, now=1.0)
        assert ps.stats()["suppressed"] == 1

    def test_repeat_gap_default(self, clean_env):
        assert self._session().repeat_gap_s == pytest.approx(10.0)

    def test_repeat_gap_env(self, clean_env, monkeypatch):
        monkeypatch.setenv(pm.ENV_REPEAT_GAP, "3.5")
        ps = self._session()
        assert ps.repeat_gap_s == pytest.approx(3.5)
        assert not ps.suppress_repeat("sys", self.LINE, now=0.0)
        assert not ps.suppress_repeat("sys", self.LINE, now=5.0)  # > 3.5

    def test_repeat_gap_env_garbage_falls_back(self, clean_env, monkeypatch):
        monkeypatch.setenv(pm.ENV_REPEAT_GAP, "banana")
        assert self._session().repeat_gap_s == pytest.approx(10.0)

    def test_repeat_gap_env_negative_clamped(self, clean_env, monkeypatch):
        monkeypatch.setenv(pm.ENV_REPEAT_GAP, "-4")
        assert self._session().repeat_gap_s == pytest.approx(0.0)

    def test_commitlog_repeat_lands_once(self):
        """_CommitLog.add-level: the same low-signal echo committed twice
        in a row lands in lines/feed_events exactly once."""
        from .ops_stream_whisper import _CommitLog
        ps = self._session()
        ps.extend_bias(None)
        log = _CommitLog()
        ps.attach(log)
        echo = "welcome to the fable live demo powered by luma ai"
        log.add("Speaker A", echo)
        log.add("Speaker A", echo)
        assert len(log.lines) == 1
        assert len(log.feed_events) == 1
        assert log.feed_events[0]["text"] == ("Welcome to the Fable live "
                                              "demo, powered by Luma AI.")

    def test_commitlog_different_channels_both_commit(self):
        from .ops_stream_whisper import _CommitLog
        ps = self._session()
        ps.extend_bias(None)
        log = _CommitLog()
        ps.attach(log)
        line = "Our agent consumes the meeting transcript in real time."
        log.add("You", line, channel="mic")
        log.add("Speaker A", line, channel="sys")
        assert len(log.feed_events) == 2


# ------------------------------------------------------------ zero effect

class TestZeroEffect:
    def test_commitlog_default_has_no_presentation(self):
        from .ops_stream_whisper import _CommitLog
        assert _CommitLog().presentation is None

    def test_session_from_env_never_raises(self, clean_env, monkeypatch,
                                           tmp_path):
        # a directory instead of a file must not crash the worker
        monkeypatch.setenv(pm.ENV_SCRIPT, str(tmp_path))
        assert pm.session_from_env() is None
