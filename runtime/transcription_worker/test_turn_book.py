# test_turn_book.py — persistent turn objects (2026-09-02 turn architecture)
from transcription_worker.turn_book import TurnBook


def test_mint_needs_two_consistent_sightings():
    b = TurnBook()
    assert b.observe([(1.0, 3.0, -1)], frontier_s=10.0, now_s=10.0) == []
    out = b.observe([(1.0, 3.0, -1)], frontier_s=12.4, now_s=12.4)
    assert out == [(1.0, 3.0, -1)]


def test_transient_cut_never_mints():
    b = TurnBook()
    b.observe([(1.0, 3.0, -1)], frontier_s=10.0, now_s=10.0)
    # cut vanished next tick -> candidate dropped; reappearing starts over
    b.observe([], frontier_s=12.4, now_s=12.4)
    assert b.observe([(1.0, 3.0, -1)], frontier_s=14.8, now_s=14.8) == []


def test_interior_mint_is_frozen_and_immutable():
    b = TurnBook()
    b.observe([(1.0, 3.0, -1)], frontier_s=10.0, now_s=10.0)
    b.observe([(1.0, 3.0, -1)], frontier_s=12.4, now_s=12.4)
    # later churn-like reshapes must NOT move frozen geometry
    out = b.observe([(0.5, 3.5, -1)], frontier_s=14.8, now_s=14.8)
    assert out[0] == (1.0, 3.0, -1)


def test_frontier_turn_grows_forward_then_freezes():
    b = TurnBook()
    # end rides the frontier -> minted OPEN (start frozen, end growing)
    b.observe([(1.0, 9.5, -1)], frontier_s=10.0, now_s=10.0)
    out = b.observe([(1.0, 11.9, -1)], frontier_s=12.4, now_s=12.4)
    assert out == [(1.0, 11.9, -1)]
    out = b.observe([(1.0, 12.5, -1)], frontier_s=14.8, now_s=14.8)
    assert out == [(1.0, 12.5, -1)]          # grew forward
    # speech stopped; frontier moves on -> end falls interior -> freeze
    out = b.observe([(1.0, 12.5, -1)], frontier_s=17.2, now_s=17.2)
    assert out == [(1.0, 12.5, -1)]
    # frozen: a would-be re-cut cannot shrink it
    out = b.observe([(1.0, 8.0, -1)], frontier_s=19.6, now_s=19.6)
    assert out == [(1.0, 12.5, -1)]


def test_prune_forgets_released_frozen_turns():
    b = TurnBook()
    b.observe([(1.0, 3.0, -1)], frontier_s=10.0, now_s=10.0)
    b.observe([(1.0, 3.0, -1)], frontier_s=12.4, now_s=12.4)
    b.prune(released_until=5.0)
    assert b.observe([], frontier_s=14.8, now_s=14.8) == []


def test_two_speakers_two_turns():
    b = TurnBook()
    cuts = [(1.0, 3.0, -1), (3.2, 6.0, -1)]
    b.observe(cuts, frontier_s=10.0, now_s=10.0)
    out = b.observe(cuts, frontier_s=12.4, now_s=12.4)
    assert out == [(1.0, 3.0, -1), (3.2, 6.0, -1)]


def test_v2_mints_between_stable_boundaries_and_is_immutable():
    from transcription_worker.turn_book import TurnBookV2
    b = TurnBookV2()
    cuts = [(1.0, 3.0, -1), (3.0, 6.0, -1)]
    b.observe(cuts, frontier=20.0, now=20.0)
    out = b.observe(cuts, frontier=22.4, now=22.4)      # 2nd sighting -> mint
    assert (1.0, 3.0, -1) in out and (3.0, 6.0, -1) in out
    # churn-like reshuffle cannot move minted geometry
    out = b.observe([(0.5, 4.0, -1)], frontier=24.8, now=24.8)
    assert (1.0, 3.0, -1) in out and (3.0, 6.0, -1) in out


def test_v2_two_hour_history_does_not_retain_or_rematch_minted_boundaries():
    from transcription_worker.turn_book import TurnBookV2
    book = TurnBookV2()
    cuts = []
    max_boundaries = 0
    for start in range(0, 7200, 30):
        cuts.append((float(start), float(start + 10), -1))
        # The activity extractor returns the full timeline every tick.
        book.observe(cuts, frontier=start + 20.0, now=start + 20.0)
        out = book.observe(cuts, frontier=start + 22.4, now=start + 22.4)
        assert (float(start), float(start + 10), -1) in out
        book.prune(start + 10.0)
        max_boundaries = max(max_boundaries, len(book.bnd))
    assert max_boundaries <= 4
    assert book.minted_until == 7180.0
    assert book.observe(cuts, frontier=7220.0, now=7220.0) == []
    assert len(book.bnd) <= 4


def test_v2_pruning_preserves_an_unreleased_turn_and_open_frontier():
    from transcription_worker.turn_book import TurnBookV2
    book = TurnBookV2()
    cuts = [(1.0, 3.0, -1), (4.0, 10.0, -1)]
    book.observe(cuts, 20.0, 20.0)
    book.observe(cuts, 22.4, 22.4)
    book.prune(3.0)
    out = book.observe(cuts + [(11.0, 24.0, -1)], 24.0, 24.0)
    assert (4.0, 10.0, -1) in out
    assert (1.0, 3.0, -1) not in out
    assert max(e for _, e, _ in out) == 24.0
