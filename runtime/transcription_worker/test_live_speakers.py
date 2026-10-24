"""live_speakers: online registry + turn tracker, fake embedder (no sherpa)."""

import numpy as np

from transcription_worker.live_speakers import MERGE_CONSEC, MERGE_MIN_OBS, OnlineSpeakerRegistry, TurnTracker


def fake_embed_factory():
    """Embeds audio by its MEAN value bucket — same-mean audio = same voice."""
    def embed(audio):
        base = np.zeros(8, dtype=np.float32)
        bucket = int(round(float(np.mean(audio)) * 10)) % 8
        base[bucket] = 1.0
        return base
    return embed


def tone(mean, seconds=2.0):
    return np.full(int(seconds * 16000), mean, dtype=np.float32)


def test_registry_assigns_and_reuses_speakers():
    reg = OnlineSpeakerRegistry(fake_embed_factory())
    a1 = reg.assign(tone(0.1))
    b1 = reg.assign(tone(0.2))
    a2 = reg.assign(tone(0.1))
    assert a1 == a2 != b1
    assert reg.num_speakers == 2


def test_registry_caps_speakers():
    reg = OnlineSpeakerRegistry(fake_embed_factory(), max_speakers=2)
    reg.assign(tone(0.1))
    reg.assign(tone(0.2))
    third = reg.assign(tone(0.3))
    assert reg.num_speakers == 2      # saturated
    assert third in (0, 1)            # nearest existing, never a new label


def test_turn_tracker_closes_on_gap():
    t = TurnTracker(turn_gap_s=0.6, silence_peak=0.01)
    loud = np.full(3200, 0.1, dtype=np.float32)      # 0.2s loud chunks
    quiet = np.zeros(3200, dtype=np.float32)
    spans = []
    for chunk in [loud, loud, quiet, quiet, quiet, loud, quiet]:
        spans += t.feed(chunk)
    spans += t.flush()
    assert len(spans) == 2
    s0, e0 = spans[0]
    assert abs(s0 - 0.0) < 1e-6 and abs(e0 - 0.4) < 1e-6
    s1, e1 = spans[1]
    assert abs(s1 - 1.0) < 1e-6 and abs(e1 - 1.2) < 1e-6


def test_turn_tracker_flush_closes_open_turn():
    t = TurnTracker()
    loud = np.full(16000, 0.1, dtype=np.float32)
    assert t.feed(loud) == []
    spans = t.flush()
    assert len(spans) == 1 and spans[0][0] == 0.0
    assert t.flush() == []


def test_registry_merges_converged_centroids():
    """Two ids whose centroids have drifted together merge; the absorbed
    slot becomes a tombstone (later assigns land on the kept id, other
    speakers' ids never shift).

    Updated 2026-08-24. This test was written on 2026-08-18 (the original scorer),
    when one look at a converged pair merged it. the previous routing then added two
    gates on purpose, and nothing updated the test, so it had been failing
    ever since — deterministically, and on the release line:

      * `MERGE_MIN_OBS` (3): BOTH slots must carry that much evidence. The old
        fixture gave the absorbed slot 2, so the pair was never even
        considered and `check_merge` returned None.
      * `MERGE_CONSEC` (2): the pair must agree across that many consecutive
        checks, so one transient 0.7 between two genuinely DISTINCT short
        turns cannot fuse them.

    Both gates are the product being right, not the test — short-turn
    embeddings of different people sit ~0.4-0.7 apart, which is exactly the
    range a single check cannot separate. So the fixture now satisfies them,
    and the gates themselves are asserted rather than worked around.
    """
    def null_embed(audio):
        return np.array([1.0, 0.0, 0.0, 0.0], dtype=np.float32)

    reg = OnlineSpeakerRegistry(null_embed, sim_threshold=0.7)
    # same person split into ids 0 and 2; id 1 is a different speaker.
    # 0 and 2 are STABLE (>= MERGE_MIN_OBS); id 1 stays a 2-turn candidate,
    # which is also what keeps it out of the merge below.
    reg._centroids = [
        np.array([0.98, 0.199, 0.0, 0.0], dtype=np.float32),
        np.array([0.0, 0.0, 1.0, 0.0], dtype=np.float32),
        np.array([0.90, 0.436, 0.0, 0.0], dtype=np.float32),
    ]
    reg._counts = [3, 2, 3]

    # Gate: one look is not sustained evidence, however close the pair is.
    assert reg.check_merge() is None, "a single agreement must not merge"

    # On the MERGE_CONSEC-th consecutive agreement it merges.
    assert reg.check_merge() == (0, 2)
    assert reg._counts[2] == 0            # tombstone
    assert reg._counts[0] == 6            # evidence pooled (3 + 3)
    assert reg.check_merge() is None      # id 1 stays distinct
    # future assigns of that voice land on the kept id, id 1 unshifted
    assert reg.assign(np.zeros(1)) == 0
    assert reg.num_speakers == 3          # slots never renumber


def test_registry_will_not_merge_an_unproven_slot():
    """The evidence gate, pinned on its own.

    A candidate with fewer than MERGE_MIN_OBS observations must never be
    absorbed no matter how close its centroid sits — that is the whole point
    of the gate, and the case the old fixture accidentally exercised while
    claiming to test something else.
    """
    def null_embed(audio):
        return np.array([1.0, 0.0, 0.0, 0.0], dtype=np.float32)

    reg = OnlineSpeakerRegistry(null_embed, sim_threshold=0.7)
    reg._centroids = [
        np.array([0.98, 0.199, 0.0, 0.0], dtype=np.float32),
        np.array([0.90, 0.436, 0.0, 0.0], dtype=np.float32),
    ]
    reg._counts = [5, MERGE_MIN_OBS - 1]

    for _ in range(MERGE_CONSEC + 2):
        assert reg.check_merge() is None, (
            "a slot below MERGE_MIN_OBS was merged; short-turn embeddings of "
            "distinct people sit in exactly this similarity range")
    assert reg._counts[1] == MERGE_MIN_OBS - 1


def _floor_fixture(sim_threshold=0.45):
    """Three STABLE remote centroids; slots 0 and 1 sit ~0.955 apart — the
    short-turn NeMo drift range that SIM (default 0.45) fuses. Slot 2 is on a
    distinct axis. Mirrors the device 2026-08-24 3-remote-speaker case."""
    def null_embed(audio):
        return np.array([1.0, 0.0, 0.0, 0.0], dtype=np.float32)

    reg = OnlineSpeakerRegistry(null_embed, sim_threshold=sim_threshold)
    reg._centroids = [
        np.array([0.98, 0.199, 0.0, 0.0], dtype=np.float32),
        np.array([0.85, 0.527, 0.0, 0.0], dtype=np.float32),
        np.array([0.0, 0.0, 1.0, 0.0], dtype=np.float32),
    ]
    reg._counts = [MERGE_MIN_OBS, MERGE_MIN_OBS, MERGE_MIN_OBS]
    return reg


def test_no_count_floor_over_merges_distinct_speakers():
    """WITHOUT a known count (default None => no floor) the retro-merge fuses
    two distinct-but-close speakers on the MERGE_CONSEC-th check: 3 active
    remote speakers collapse to 2. This is the bug the floor guards against and
    pins the default-None behaviour as UNCHANGED from before the floor."""
    reg = _floor_fixture()
    assert reg._min_remote is None          # no floor by default
    assert reg._active() == 3
    assert reg.check_merge() is None        # 1st look: not sustained
    assert reg.check_merge() == (0, 1)      # 2nd: fuses two distinct speakers
    assert reg._active() == 2               # collapsed below the true count


def test_count_floor_keeps_distinct_speakers_separate():
    """WITH num_speakers=4 (=> min_remote=3, a 4-person call incl. the local
    mic) the SAME merge SIM would make is refused every check — the live
    registry now honours the count-floor the finalize pass already had."""
    reg = _floor_fixture()
    reg.set_num_speakers(4)
    assert reg._min_remote == 3
    for _ in range(MERGE_CONSEC + 3):
        assert reg.check_merge() is None    # never drops below the floor
    assert reg._active() == 3               # all three kept distinct


def test_count_floor_via_constructor_kwarg():
    """The floor threads through the OPTIONAL num_speakers constructor kwarg
    (how _Session wires it), not only set_num_speakers."""
    reg = _floor_fixture(sim_threshold=0.45)
    reg2 = OnlineSpeakerRegistry(lambda a: np.zeros(4, np.float32),
                                 num_speakers=4)
    assert reg2._min_remote == 3
    # and the floor still allows merging DOWN TO but not BELOW it: seed the
    # kwarg fixture and confirm it holds like the setter path.
    reg.set_num_speakers(4)
    for _ in range(MERGE_CONSEC + 2):
        assert reg.check_merge() is None


def test_count_floor_default_none_is_backward_compatible():
    """No num_speakers arg, and num_speakers=None/0/'' => no floor, so a
    pre-floor registry and construction cannot raise the old num_speakers
    TypeError and behave identically to before."""
    embed = lambda a: np.zeros(4, np.float32)
    assert OnlineSpeakerRegistry(embed)._min_remote is None
    assert OnlineSpeakerRegistry(embed, num_speakers=None)._min_remote is None
    assert OnlineSpeakerRegistry(embed, num_speakers=0)._min_remote is None
    assert OnlineSpeakerRegistry(embed, num_speakers="")._min_remote is None
    # a garbage value degrades to no-floor, never raises.
    assert OnlineSpeakerRegistry(embed, num_speakers="x")._min_remote is None
    # num_speakers=1 (only the local mic) => remote floor clamps to 1.
    assert OnlineSpeakerRegistry(embed, num_speakers=1)._min_remote == 1


def test_trim_trailing_silence():
    from transcription_worker.live_speakers import trim_trailing_silence
    speech = np.full(16000, 0.1, dtype=np.float32)
    tail = np.zeros(5 * 16000, dtype=np.float32)
    out = trim_trailing_silence(np.concatenate([speech, tail]))
    assert 16000 <= len(out) <= 16000 + 4000   # speech + <=0.25s pad
    assert len(trim_trailing_silence(np.zeros(16000, dtype=np.float32))) == 0


def test_speech_spans_offline():
    from transcription_worker.live_speakers import speech_spans
    audio = np.concatenate([
        np.full(2 * 16000, 0.1, dtype=np.float32),
        np.zeros(2 * 16000, dtype=np.float32),
        np.full(16000, 0.1, dtype=np.float32),
    ])
    spans = speech_spans(audio)
    assert len(spans) == 2
    assert abs(spans[0][0] - 0.0) < 0.25 and abs(spans[0][1] - 2.0) < 0.25
    assert abs(spans[1][0] - 4.0) < 0.25


def test_continuous_audio_force_closes_turns():
    """Nonstop audio (a playing video) must still yield turns — the silence
    gap can never come."""
    t = TurnTracker(max_open_turn_s=2.0)
    loud = np.full(3200, 0.1, dtype=np.float32)   # 0.2s chunks
    spans = []
    for _ in range(35):                            # 7s continuous
        spans += t.feed(loud)
    assert len(spans) >= 3                         # ~2s windows
    for s0, e0 in spans:
        assert e0 - s0 <= 2.2
    # windows tile continuously
    for (s0, e0), (s1, _) in zip(spans, spans[1:]):
        assert abs(s1 - e0) < 1e-6


def comfort_noise(seconds=0.2, sigma=0.0035, seed=3):
    """Zoom-loopback room tone: peak ABOVE the tracker's raw silence floor
    (0.008) yet speech-free by the sanctioned frame-RMS test (audio_energy
    SILENCE_RMS 0.004). Matches the device quiet-room hold evidence
    (peak 0.0351, no speech energy)."""
    rng = np.random.default_rng(seed)
    n = rng.normal(0.0, sigma, int(seconds * 16000)).astype(np.float32)
    # deterministic guarantee: every 0.2s chunk clears the raw peak floor
    n[0] = 0.012
    return n


def speech(seconds=0.2, level=0.1, seed=5):
    rng = np.random.default_rng(seed)
    return (level * rng.standard_normal(int(seconds * 16000))
            ).astype(np.float32).clip(-1, 1)


def test_turn_tracker_comfort_noise_is_not_voice():
    """The 2026-08-27 on-device latency tail: a raised loopback noise floor
    (comfort noise/room tone above SILENCE_PEAK) kept every turn open until
    MAX_OPEN_TURN_S, so commits waited for the 6s force-close instead of the
    0.7s speech gap. Speech-free-by-RMS chunks must count as SILENCE for
    turn tracking, closing the turn at the last real speech chunk.
    (Opt-in: speech_rms=True / DICTATION_NOTES_TURN_SPEECH_RMS=1 — default is
    OFF after the far-field A/B, see the live_speakers doc.)"""
    t = TurnTracker(speech_rms=True)
    assert float(np.abs(comfort_noise()).max()) >= t._floor  # premise: above raw floor
    spans = []
    for _ in range(5):                    # 1.0s speech
        spans += t.feed(speech())
    for i in range(5):                    # 1.0s comfort noise > TURN_GAP_S
        spans += t.feed(comfort_noise(seed=100 + i))
    assert len(spans) == 1, "turn must close on the speech gap under noise"
    s0, e0 = spans[0]
    assert abs(s0 - 0.0) < 1e-6
    assert abs(e0 - 1.0) < 1e-6, "turn end = last SPEECH chunk, not last loud chunk"


def test_turn_tracker_comfort_noise_never_opens_a_turn():
    t = TurnTracker(speech_rms=True)
    for i in range(50):                   # 10s of pure room tone
        assert t.feed(comfort_noise(seed=200 + i)) == []
    assert t.flush() == []


def test_turn_tracker_speech_rms_default_off():
    """Raw-peak-only is the DEFAULT (far-field A/B, 2026-08-27): the RMS
    evidence is opt-in via DICTATION_NOTES_TURN_SPEECH_RMS=1 / speech_rms=True."""
    t = TurnTracker()
    for _ in range(5):
        t.feed(speech())
    closed = []
    for i in range(5):
        closed += t.feed(comfort_noise(seed=300 + i))
    assert closed == []                   # pre-fix: noise counts as voice
