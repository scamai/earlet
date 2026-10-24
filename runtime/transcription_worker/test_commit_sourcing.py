"""Commit-time text/label sourcing (accuracy beside the latency work):

1. BEAM TEXT: committed lines used to carry the fast GREEDY decode's text
   while the rolling-stable pass re-decodes the same audio with beam-4 (plus
   the bias trie) strictly better, trailing by seconds. When the beam pass
   has ALREADY covered a line's span at commit time, the line takes the beam
   text; otherwise greedy stands — the commit NEVER waits (latency budget
   rules; the split is counted so the replay can report it).

2. SETTLED LABELS: a sys line's speaker used to be the instantaneous greedy
   registry assign; the periodic re-cluster's settled turns are strictly
   better once they cover the span. Same contract: use when covering,
   greedy fallback, never wait.

Both hooks are env-gated (DICTATION_NOTES_COMMIT_BEAM_TEXT /
DICTATION_NOTES_COMMIT_SETTLED_LABEL, default ON) and change ONLY line content —
no wire shape or timing changes.
"""

import numpy as np

from transcription_worker import ops_stream_whisper as osw


class NullEngine:
    def transcribe(self, clip, **kw):
        return {"text": "greedy words here", "stats": {}}

    def transcribe_timed(self, clip, **kw):
        """Satisfy the rolling channel's timed-engine construction contract.

        These tests plant spans directly and never decode, but the double must
        still represent an engine the production object is allowed to accept.
        """
        return {"spans": [], "stats": {}}


def make_session():
    s = osw._Session(NullEngine(), None)
    s._watchdog_stop.set()
    return s


def stable_with_spans(session, channel, spans, through):
    """Plant a rolling-stable channel state without running decodes."""
    from transcription_worker.rolling_stable import RollingStableChannel
    ch = RollingStableChannel(NullEngine(), channel=channel,
                              speaker="Them" if channel == "sys" else "You")
    ch._spans = [dict(s) for s in spans]
    ch._through_s = through
    if channel == "sys":
        session.stable_sys = ch
    else:
        session.stable_mic = ch
    return ch


def test_beam_text_used_when_stable_pass_covers_span():
    s = make_session()
    stable_with_spans(s, "mic", [
        {"start": 1.0, "end": 3.0, "text": "the beam grade first half",
         "channel": "mic", "speaker": "You"},
        {"start": 3.2, "end": 5.0, "text": "and the second half",
         "channel": "mic", "speaker": "You"},
        {"start": 5.5, "end": 7.0, "text": "beyond the line",
         "channel": "mic", "speaker": "You"},
    ], through=6.0)
    text = s._beam_text_for("mic", 0.9, 5.1)
    assert text == "the beam grade first half and the second half"
    # split accounting for the replay report
    assert s.commit_source_counts["beam"] == 1


def test_beam_text_declines_when_pass_has_not_caught_up():
    s = make_session()
    stable_with_spans(s, "mic", [
        {"start": 1.0, "end": 3.0, "text": "beam text",
         "channel": "mic", "speaker": "You"}], through=3.5)
    # span end (5.0) is beyond the beam pass's through mark: fall back NOW
    assert s._beam_text_for("mic", 1.0, 5.0) is None
    assert s.commit_source_counts["greedy"] == 1
    # coverage too thin (spans overlap <60% of the line) also declines
    s2 = make_session()
    stable_with_spans(s2, "mic", [
        {"start": 4.4, "end": 5.0, "text": "sliver",
         "channel": "mic", "speaker": "You"}], through=10.0)
    assert s2._beam_text_for("mic", 0.0, 5.0) is None


def test_beam_text_gate_off(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_BEAM_TEXT", "0")
    s = make_session()
    stable_with_spans(s, "mic", [
        {"start": 0.0, "end": 5.0, "text": "beam text",
         "channel": "mic", "speaker": "You"}], through=10.0)
    assert s._beam_text_for("mic", 0.0, 5.0) is None


def test_beam_spans_partition_across_adjacent_lines():
    """Midpoint assignment: two adjacent committed lines can never both
    claim one beam span — no duplicated words across feed lines."""
    s = make_session()
    stable_with_spans(s, "mic", [
        {"start": 0.0, "end": 4.4, "text": "one two three four",
         "channel": "mic", "speaker": "You"},
        {"start": 4.6, "end": 8.0, "text": "five six seven",
         "channel": "mic", "speaker": "You"}], through=10.0)
    a = s._beam_text_for("mic", 0.0, 4.5)
    b = s._beam_text_for("mic", 4.5, 8.0)
    assert a == "one two three four"
    assert b == "five six seven"


def test_settled_label_used_when_recluster_covers_span():
    s = make_session()
    s._settled_turns = [(0.0, 6.0, "Speaker B"), (6.5, 9.0, "Speaker A")]
    assert s._settled_label_for(1.0, 5.0) == "Speaker B"
    assert s._settled_label_for(6.6, 8.9) == "Speaker A"
    # span past the settled coverage: fall back (never wait for a re-cluster)
    assert s._settled_label_for(9.5, 12.0) is None
    # thin overlap (<50% of the span) is not evidence — same rule as the
    # retroactive relabel
    assert s._settled_label_for(5.5, 12.0) is None


def test_settled_label_gate_off(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_COMMIT_SETTLED_LABEL", "0")
    s = make_session()
    s._settled_turns = [(0.0, 6.0, "Speaker B")]
    assert s._settled_label_for(1.0, 5.0) is None


def test_commit_applies_beam_text_and_settled_label():
    """End-to-end at the commit choke point: a sys commit with a span picks
    up both the beam text and the settled label; the feed event carries
    them; mic commits never take a settled label (pinned You)."""
    s = make_session()
    stable_with_spans(s, "sys", [
        {"start": 10.0, "end": 13.9, "text": "beam quality sentence",
         "channel": "sys", "speaker": "Them"}], through=15.0)
    s._settled_turns = [(9.8, 14.2, "Speaker C")]
    s.sys._commit_guarded("Speaker A", np.zeros(0, np.float32),
                          "greedy words here", None, span=(10.0, 14.0))
    evt = s.commits.drain_feed()[-1]
    assert evt["text"] == "beam quality sentence".capitalize() or \
        evt["text"] == "beam quality sentence"
    assert evt["speaker"] == "Speaker C"
    # mic: label pinned, beam text still allowed
    stable_with_spans(s, "mic", [
        {"start": 10.0, "end": 13.9, "text": "mic beam sentence",
         "channel": "mic", "speaker": "You"}], through=15.0)
    s.mic._commit_guarded("You", np.zeros(0, np.float32),
                          "greedy words here", None, span=(10.0, 14.0))
    evt = s.commits.drain_feed()[-1]
    assert evt["speaker"] == "You"
