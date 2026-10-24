"""`mlx_cache.clear_cache()` must be import-safe and side-effect-free on
machines without mlx (CI, dev boxes off Apple silicon) — it is called from
the hot paths of whisper_mac and summarize_mac.
"""

from __future__ import annotations

from transcription_worker import mlx_cache


def test_clear_cache_never_raises():
    mlx_cache.clear_cache()
