"""Best-effort reset of MLX's Metal buffer cache.

MLX caches Metal buffers for reuse across inference calls. The cache is a
one-way ratchet: it grows to the largest allocation ever seen and is never
returned to the OS, so a long-lived worker's RSS climbs monotonically even
though model weights stay resident (by policy, the worker never unloads a
model while it runs). Clearing the cache between top-level inference calls (one
utterance transcribe, one meeting summary) flattens the ratchet at the
cost of re-allocating buffers on the next call.

Version tolerance: requirements_mac.txt leaves mlx unpinned. Newer mlx
exposes `mx.clear_cache()`; older releases only `mx.metal.clear_cache()`;
very old ones have neither (no-op). Import failure (non-mac dev machines,
CI without mlx) is likewise a no-op.
"""

from __future__ import annotations


def clear_cache() -> None:
    """Release MLX's cached Metal buffers back to the OS; never raises."""
    try:
        import mlx.core as mx
    except Exception:
        return
    clear = getattr(mx, "clear_cache", None)
    if clear is None:
        clear = getattr(getattr(mx, "metal", None), "clear_cache", None)
    if clear is None:
        return
    try:
        clear()
    except Exception:
        pass
