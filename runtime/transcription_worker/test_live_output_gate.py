"""Live-output hard gate (P0): continuous speech must produce live lines.

Guards the live_chars=0 class of regression at the layer FakeEngine can reach
deterministically on a Linux host — the turn->decode->commit pipeline. A build where
turns never mint / decodes never schedule / commits never fire produces zero
lines here and fails, instead of shipping a silent blank caption pane.

Reuses the proven fakes/helpers from test_ops_stream_whisper (same engine
contract the rest of the suite trusts) so this gate can't drift from them.
The full '25-min replay yields >= N lines on the real engine under load' gate
is device-side (L4); this is its Linux sentinel.
"""

import time

from transcription_worker.test_ops_stream_whisper import (
    FakeEngine,
    drain,
    loud,
    make_session,
    quiet,
)


def test_continuous_speech_yields_live_lines():
    s = make_session(FakeEngine())
    # Six ~2s utterances, each closed by a short silence — a plausible
    # one-speaker monologue. The pipeline must emit a line per closed turn.
    for i in range(6):
        s.sys.push(loud(2.0, level=0.1 + 0.03 * i))
        s.sys.push(quiet(1.0))
        drain(s.sys)
    time.sleep(0.3)
    drain(s.sys)
    lines = s.commits.snapshot()
    assert len(lines) >= 4, f"live pipeline starved: only {len(lines)} lines: {lines}"
    assert all(ln.strip() for ln in lines), lines


def test_silence_only_yields_no_phantom_lines():
    # The complement: pure silence must NOT invent lines, so the gate above
    # is measuring real speech, not a hallucinated speaker.
    s = make_session(FakeEngine())
    for _ in range(4):
        s.sys.push(quiet(2.0))
        drain(s.sys)
    assert s.commits.snapshot() == [], s.commits.snapshot()
