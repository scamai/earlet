# test_neural_turns.py
import numpy as np
from collections import namedtuple
from transcription_worker import neural_turns as nt

Snap = namedtuple("Snap", "starts bins assign num_frames frame_dur n_out")

def _snap_two_speakers():
    # one 16s window at sample 0; F=100 frames (0.16s/frame for test math);
    # track0 active frames 0..49 -> speaker 0; track1 active 50..99 -> speaker 1
    F = 100
    b = np.zeros((F, 4), dtype=np.int8)
    b[:50, 0] = 1
    b[50:, 1] = 1
    assign = np.full((1, 4), -1, dtype=np.int64)
    assign[0, 0] = 0
    assign[0, 1] = 1
    return Snap(starts=(0,), bins=(b,), assign=assign,
                num_frames=F, frame_dur=0.16, n_out=2)

def test_dominant_frames_maps_tracks_to_output_speakers():
    grid, step = nt.dominant_frames(_snap_two_speakers(), sr=16000)
    assert step == 0.16
    assert grid[0] == 0 and grid[49] == 0
    assert grid[50] == 1 and grid[99] == 1

def test_turns_cut_on_speaker_change():
    turns = nt.turns_from_state(_snap_two_speakers(), sr=16000)
    assert len(turns) == 2
    (s0, e0, k0), (s1, e1, k1) = turns
    assert k0 == 0 and k1 == 1
    assert abs(e0 - 8.0) < 0.5 and abs(s1 - 8.0) < 0.5   # split at frame 50

def test_turns_cut_on_silence_gap():
    F = 100
    b = np.zeros((F, 4), dtype=np.int8)
    b[:30, 0] = 1          # speech
    b[40:, 0] = 1          # 10-frame (1.6s) silence gap >= min_gap
    assign = np.full((1, 4), -1, dtype=np.int64); assign[0, 0] = 0
    snap = Snap(starts=(0,), bins=(b,), assign=assign,
                num_frames=F, frame_dur=0.16, n_out=1)
    turns = nt.turns_from_state(snap, sr=16000)
    assert len(turns) == 2 and turns[0][2] == turns[1][2] == 0

def test_max_turn_hard_split():
    F = 100
    b = np.zeros((F, 4), dtype=np.int8); b[:, 0] = 1     # 16s continuous
    assign = np.full((1, 4), -1, dtype=np.int64); assign[0, 0] = 0
    snap = Snap(starts=(0,), bins=(b,), assign=assign,
                num_frames=F, frame_dur=0.16, n_out=1)
    turns = nt.turns_from_state(snap, sr=16000, max_turn_s=6.0)
    assert all(e - s <= 6.0 + 1e-6 for s, e, _ in turns) and len(turns) >= 2

def test_rapid_alternation_drops_sub_min_turn_pieces():
    # A/B alternate in 2-frame (0.32s) bursts — every burst < min_turn 0.4s
    # so ALL of them are dropped; then one solid 6.4s A run (frames 20..59)
    # survives as the ONLY turn, ending exactly after its last speech frame
    # (trailing silence 60..99 belongs to nobody).
    F = 100
    b = np.zeros((F, 4), dtype=np.int8)
    for k in range(0, 20, 4):
        b[k:k + 2, 0] = 1          # A burst
        b[k + 2:k + 4, 1] = 1      # B burst
    b[20:60, 0] = 1                # solid A
    assign = np.full((1, 4), -1, dtype=np.int64)
    assign[0, 0] = 0
    assign[0, 1] = 1
    snap = Snap(starts=(0,), bins=(b,), assign=assign,
                num_frames=F, frame_dur=0.16, n_out=2)
    turns = nt.turns_from_state(snap, sr=16000)
    assert len(turns) == 1
    s, e, k = turns[0]
    assert k == 0
    assert abs(s - 3.2) < 1e-6     # frame 20
    assert abs(e - 9.6) < 1e-6     # frame 60 exclusive = last speech frame 59

def test_overlap_votes_average_across_windows():
    # two overlapping windows disagree on one frame region; majority wins
    F = 100
    b0 = np.zeros((F, 4), dtype=np.int8); b0[:, 0] = 1
    b1 = np.zeros((F, 4), dtype=np.int8); b1[:, 1] = 1
    assign = np.full((2, 4), -1, dtype=np.int64)
    assign[0, 0] = 0; assign[1, 1] = 0     # SAME output speaker both windows
    snap = Snap(starts=(0, 0), bins=(b0, b1), assign=assign,
                num_frames=F, frame_dur=0.16, n_out=1)
    grid, _ = nt.dominant_frames(snap, sr=16000)
    assert (grid[:F] == 0).all()


def _state_from_grid(grid, step=0.1, n_out=None):
    """Build a minimal fake bank state whose dominant_frames() reproduces
    `grid` exactly: one window covering all frames, one track per speaker id."""
    import numpy as np
    from transcription_worker.neural_turns import turns_from_state
    ids = sorted(set(g for g in grid if g >= 0))
    n_out = n_out or (max(ids) + 1 if ids else 1)
    F = len(grid); S = n_out
    b = np.zeros((F, S), dtype=bool)
    for f, g in enumerate(grid):
        if g >= 0: b[f, g] = True
    class St:
        bins=[b]; starts=[0]; assign=[np.arange(S)]; frame_dur=step; n_out=S
    return St()


def test_interruption_does_not_punch_hole():
    """A speaker's continuous run briefly interrupted by another's short
    backchannel must stay ONE covered span, not split into a hole (device
    30862 Ernie / 30865 Olivia). grid: A for 30 frames, B for 4, A for 30."""
    from transcription_worker.neural_turns import turns_from_state
    grid = [0]*30 + [1]*4 + [0]*30
    turns = turns_from_state(_state_from_grid(grid), min_gap_s=0.6,
                             min_turn_s=0.4, max_turn_s=12.0)
    a = [t for t in turns if t[2] == 0]
    # A's speech spans 0.0-6.4s continuously (the 0.4s B blip is bridged)
    assert a, "speaker A must be present"
    assert min(s for s,_,_ in a) < 0.05 and max(e for _,e,_ in a) > 6.3, \
        f"A should cover through the interruption, got {a}"
    # no uncovered hole between A's start and end
    cov = sorted((s,e) for s,e,sp in turns if sp==0)
    assert len(cov) == 1, f"A must be one bridged span, got {cov}"


def test_genuine_short_flicker_still_dropped():
    """An isolated sub-min_turn flicker with no same-speaker neighbor is still
    dropped (we didn't just keep every short run)."""
    from transcription_worker.neural_turns import turns_from_state
    grid = [0]*30 + [-1]*10 + [2]*2 + [-1]*10 + [1]*30   # spk2 = 0.2s lone blip
    turns = turns_from_state(_state_from_grid(grid), min_gap_s=0.6,
                             min_turn_s=0.4, max_turn_s=12.0)
    assert not any(sp == 2 for _,_,sp in turns), f"lone flicker kept: {turns}"


def test_dense_flicker_chain_is_not_bridged():
    """Heavy-overlap regression (30880): MULTIPLE short other-speaker pieces
    between two same-speaker runs are the other speaker's real speech —
    bridging across them relabels it (miss traded into conf). Only a SINGLE
    interposed piece (an isolated backchannel) may be bridged."""
    from transcription_worker.neural_turns import turns_from_state
    grid = [0]*30 + [1]*3 + [0]*3 + [1]*3 + [0]*30
    turns = turns_from_state(_state_from_grid(grid), min_gap_s=0.6,
                             min_turn_s=0.4, max_turn_s=12.0)
    a_spans = sorted((s, e) for s, e, sp in turns if sp == 0)
    assert len(a_spans) == 2, f"flicker chain wrongly bridged: {a_spans}"
    assert a_spans[0][1] <= 3.05 and a_spans[1][0] >= 3.85, a_spans
