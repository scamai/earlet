"""Acoustic echo cancellation for the speakers-on demo.

The mic channel picks up what the speakers play (the far end), so during
overlap the user's words are buried under a delayed copy of the sys
channel. We already capture the EXACT reference (the sys/loopback channel,
wall-clock aligned with the mic) — textbook AEC:

    mic[n] = near_end[n] + (h * ref)[n]        h = room+device response

Implementation: frequency-domain block LMS (FDAF, overlap-save) — one FFT
per 128ms block instead of per-sample updates, fully vectorized numpy;
processes 10 minutes of 16k audio in seconds on one core. Plus:

  - one-shot cross-correlation delay estimation (speaker→air→mic latency
    is device/room dependent; never assume it),
  - double-talk protection: adaptation FREEZES while the near end is
    active (residual-vs-estimate energy heuristic), so the filter cancels
    the echo but never learns to cancel the USER — the failure that got
    the old half-duplex gate removed.

Fail-soft everywhere: a silent reference (headset case) returns the mic
untouched.
"""
from __future__ import annotations

import numpy as np

SAMPLE_RATE = 16000
BLOCK = 2048                # 128ms filter block (overlap-save, 50%)
MU = 0.4                    # normalized step size while adapting
EPS = 1e-8
MAX_DELAY_S = 0.5           # search window for speaker->mic latency


def estimate_delay(mic: np.ndarray, ref: np.ndarray,
                   sample_rate: int = SAMPLE_RATE,
                   max_delay_s: float = MAX_DELAY_S) -> int:
    """Speaker→mic delay in samples via FFT cross-correlation over the
    loudest reference stretch. Returns 0 when no reliable peak exists
    (e.g. the reference is silent — headset case)."""
    n = min(len(mic), len(ref))
    if n < sample_rate:
        return 0
    win = min(n, 10 * sample_rate)
    if n > win:
        frame_rms = np.sqrt(np.convolve(ref[:n].astype(np.float64) ** 2,
                                        np.ones(sample_rate) / sample_rate,
                                        mode="valid"))
        start = int(np.argmax(frame_rms))
        start = min(start, n - win)
    else:
        start = 0
    m = mic[start:start + win].astype(np.float64)
    r = ref[start:start + win].astype(np.float64)
    if float(np.abs(r).max()) < 1e-3:
        return 0
    size = 1
    while size < 2 * win:
        size *= 2
    spec = np.fft.rfft(m, size) * np.conj(np.fft.rfft(r, size))
    corr = np.fft.irfft(spec, size)
    max_lag = int(max_delay_s * sample_rate)
    lags = corr[:max_lag]               # mic lags AFTER ref only
    peak = int(np.argmax(np.abs(lags)))
    floor = float(np.median(np.abs(lags))) + EPS
    if float(np.abs(lags[peak])) / floor < 6.0:
        return 0
    # A peak at the search-window edge is a truncated-correlation artifact,
    # not a real acoustic delay (field replay: "delay" 7979/8000 on a take
    # whose mic held no coherent echo — the filter then ADDED energy).
    if peak >= int(0.97 * max_lag):
        return 0
    return peak


class FdafAec:
    """Streaming frequency-domain adaptive echo canceller (overlap-save,
    block size BLOCK, filter length BLOCK). feed() takes equal-length mic
    and delay-compensated reference and returns the echo-reduced mic."""

    def __init__(self, block: int = BLOCK, mu: float = MU):
        self.B = block
        self.mu = mu
        self.W = np.zeros(block + 1, dtype=np.complex128)  # rfft bins of 2B
        self._ref_prev = np.zeros(block, dtype=np.float64)
        # smoothed per-bin spectra for coherence-weighted adaptation
        nb = block + 1
        self._Sxx = np.full(nb, EPS)
        self._Smm = np.full(nb, EPS)
        self._Sxm = np.zeros(nb, dtype=np.complex128)

    def feed(self, mic: np.ndarray, ref: np.ndarray) -> np.ndarray:
        B = self.B
        m = mic.astype(np.float64)
        r = ref.astype(np.float64)
        n = len(m)
        out = np.empty(n, dtype=np.float64)
        for i in range(0, n, B):
            mb = m[i:i + B]
            rb = r[i:i + B]
            if len(mb) < B:                      # tail: pad, process, trim
                pad = B - len(mb)
                mb = np.concatenate([mb, np.zeros(pad)])
                rb = np.concatenate([rb, np.zeros(pad)])
            x = np.concatenate([self._ref_prev, rb])       # 2B overlap-save
            X = np.fft.rfft(x)
            est = np.fft.irfft(X * self.W)[B:]             # last B = valid
            err = mb - est
            take = min(B, n - i)
            out[i:i + take] = err[:take]
            # Double-talk gate — WITH a bootstrap escape: at startup the
            # estimate is zero, so "residual >> estimate" is true forever
            # and a naive gate deadlocks adaptation at 0 dB (measured).
            # Until the filter is warm (estimate carries real energy vs the
            # mic) we always adapt — NLMS only fits the ref-correlated part
            # of the error, so near-end speech slows convergence but cannot
            # be learned into the filter. Once warm, freeze on double-talk.
            ref_pow = float(np.dot(x, x)) + EPS
            # Per-bin coherence-weighted adaptation (speex-style): each
            # frequency bin adapts with step mu * coherence(bin). Echo-
            # dominated bins (mic tracks the reference) get the full step;
            # bins carrying near-end speech (a voice, a tone) get ~zero —
            # the block-level ON/OFF gates tried first either deadlocked at
            # bootstrap or let narrowband user energy corrupt the filter
            # (both measured as negative ERLE on the mixed synthetic).
            M = np.fft.rfft(np.concatenate([np.zeros(B), mb]))
            lam = 0.9
            Px = np.abs(X) ** 2
            self._Sxx = lam * self._Sxx + (1 - lam) * Px
            self._Smm = lam * self._Smm + (1 - lam) * (np.abs(M) ** 2)
            self._Sxm = lam * self._Sxm + (1 - lam) * (np.conj(X) * M)
            coh = (np.abs(self._Sxm) ** 2) / (self._Sxx * self._Smm + EPS)
            if ref_pow > 1e-4:
                E = np.fft.rfft(np.concatenate([np.zeros(B), err]))
                grad = np.conj(X) * E / (Px + self._Sxx + EPS)
                grad *= np.clip(coh, 0.0, 1.0)
                # gradient constraint (keep the filter causal, length B)
                g = np.fft.irfft(grad)
                g[B:] = 0.0
                self.W += self.mu * np.fft.rfft(g)
            self._ref_prev = rb
        return out.astype(np.float32)


def cancel_echo(mic: np.ndarray, ref: np.ndarray,
                sample_rate: int = SAMPLE_RATE) -> tuple[np.ndarray, dict]:
    """Offline/batch API: delay-estimate + FDAF over the whole take.
    Returns (cleaned_mic, stats). Silent reference -> mic untouched."""
    if not len(mic) or not len(ref) or float(np.abs(ref).max()) < 1e-3:
        return mic, {"delay": 0, "applied": False}
    delay = estimate_delay(mic, ref, sample_rate)
    if delay <= 0:
        return mic, {"delay": 0, "applied": False}
    ref_shift = np.concatenate([np.zeros(delay, dtype=np.float64),
                                ref.astype(np.float64)])[:len(mic)]
    if len(ref_shift) < len(mic):
        ref_shift = np.concatenate(
            [ref_shift, np.zeros(len(mic) - len(ref_shift))])
    aec = FdafAec()
    cleaned = aec.feed(mic, ref_shift)
    before = float(np.dot(mic.astype(np.float64), mic.astype(np.float64)))
    after = float(np.dot(cleaned.astype(np.float64), cleaned.astype(np.float64)))
    erle_db = 10 * np.log10((before + EPS) / (after + EPS))
    if erle_db < 0.2:
        # Fail-safe: cancellation that does not reduce energy means there
        # was no coherent echo to cancel (a spurious delay peak that slipped
        # the gates) — never ship a mic the filter made WORSE.
        return mic, {"delay": delay, "applied": False,
                     "erle_db": round(erle_db, 2), "reverted": True}
    return cleaned, {"delay": delay, "applied": True,
                     "erle_db": round(erle_db, 2)}
