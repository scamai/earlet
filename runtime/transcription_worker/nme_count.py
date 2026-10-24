"""Speaker-count estimation via NME-SC (Park et al., IEEE SPL 2019) — numpy only.

Why this exists: sherpa's FastClustering cuts an agglomerative dendrogram at a
single global cosine-distance threshold, and no fixed threshold is right for
both a quiet 2-person interview and an 8-voice debate — the calibrated 0.8
over-counted real meetings 2-3x (3-speaker panels reported as 6-7). NME-SC
instead estimates the count from the eigengap of the normalized Laplacian of
the window-embedding affinity matrix, auto-tuning the binarization p by the
NME ratio — no threshold to tune. Bench (5 real multi-speaker YouTube clips,
2026-08-20): threshold path 7/6/6/-/14 speakers vs truth 3/5/2/2/6-8; NME
k feeding cutree_k + the production embedding merge: 3/4/2/2/7.

The estimate is advisory: diarize_win runs the threshold pass first (spans +
fallback), asks this module for k, and only re-clusters when k differs and is
sane. Any failure here returns None and the threshold result stands.
"""
from __future__ import annotations

import numpy as np

SR = 16000
WIN_S = 2.0
HOP_S = 1.0
MIN_WIN_S = 0.8
MAX_SPK = 10
MIN_WINDOWS = 8


def merge_spans(segs, pad: float = 0.0):
    """[(s, e, spk)] -> merged [(s, e)] speech spans."""
    spans = sorted((s - pad, e + pad) for s, e, _ in segs)
    out: list[list[float]] = []
    for s, e in spans:
        if out and s <= out[-1][1]:
            out[-1][1] = max(out[-1][1], e)
        else:
            out.append([s, e])
    return out


def window_embeddings(audio: np.ndarray, spans, embed) -> np.ndarray | None:
    """2s/1s-hop windows over speech spans -> row-normalized embedding matrix.
    Windows that fail to embed are skipped (titanet's ONNX crashes on some
    exact input lengths; CAM++ doesn't, but the guard is cheap)."""
    rows = []
    for s, e in spans:
        t = s
        while t < e:
            t1 = min(t + WIN_S, e)
            if t1 - t >= MIN_WIN_S:
                a0, b0 = int(t * SR), min(len(audio), int(t1 * SR))
                if b0 > a0:
                    try:
                        v = embed(audio[a0:b0])
                    except RuntimeError:
                        v = None
                    if v is not None:
                        rows.append(v)
            t += HOP_S
    if len(rows) < MIN_WINDOWS:
        return None
    X = np.stack(rows)
    return X / (np.linalg.norm(X, axis=1, keepdims=True) + 1e-10)


def nme_estimate(A: np.ndarray, max_spk: int = MAX_SPK) -> int:
    """Normalized Maximum Eigengap over the affinity matrix A (cosine sims,
    clipped >= 0): for each top-p binarization, take the max eigengap of the
    normalized Laplacian; keep the p minimizing (p/n)/gap; return that p's
    arg-max-eigengap count."""
    n = A.shape[0]
    A = A - np.eye(n) * A.diagonal() + np.eye(n)
    best = None
    p_grid = np.unique(np.clip(
        np.round(np.linspace(2, max(3, n // 2), 20)).astype(int), 2, n - 1))
    for p in p_grid:
        B = np.zeros_like(A)
        idx = np.argsort(-A, axis=1)[:, :p]
        B[np.arange(n)[:, None], idx] = A[np.arange(n)[:, None], idx]
        B = np.maximum(B, B.T)
        d = B.sum(1)
        d[d <= 0] = 1e-10
        dm12 = 1.0 / np.sqrt(d)
        lap = np.eye(n) - (B * dm12[:, None]) * dm12[None, :]
        w = np.clip(np.linalg.eigvalsh(lap)[:max_spk + 1], 0, None)
        gaps = np.diff(w)
        k = int(np.argmax(gaps[1:max_spk])) + 2
        g = float(gaps[1:max_spk].max())
        ratio = (p / n) / (g + 1e-10)
        if best is None or ratio < best[0]:
            best = (ratio, k)
    return best[1]


def estimate_num_speakers(audio: np.ndarray, segs, embed) -> int | None:
    """Full estimate: spans -> windows -> embeddings -> NME. None on any
    shortfall (too little speech, embedding failures) — caller keeps the
    threshold clustering."""
    try:
        spans = merge_spans(segs)
        X = window_embeddings(audio, spans, embed)
        if X is None:
            return None
        A = np.clip(X @ X.T, 0, None)
        k = nme_estimate(A)
        return k if 1 <= k <= MAX_SPK else None
    except Exception:
        return None
