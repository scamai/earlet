import Foundation

/// Process-wide ownership ledger for every physical microphone The app opens —
/// the WS-A "shim" from spec v3 §7.3 / §14 #2: legacy capture constructors
/// register with a minimal lease table so later work (the full
/// `AudioSourceRegistry` / `LeaseTable`, a later workstream) has a live
/// table to mirror into and a construction-site allowlist to check,
/// WITHOUT changing what any existing capture path actually does.
/// `register`/`transition` never block, never refuse, and never change a
/// caller's control flow — see the `MicCapture.start()`/`stop()` call
/// sites, which are this shim's only production callers.
///
/// State vocabulary mirrors spec §6.2's lease lifecycle (this shim does
/// NOT implement that lifecycle — no `acquire`, no conflict detection, no
/// adoption; that is the later native `AudioSourceRegistry`'s
/// `LeaseTable`, which mirrors into this same table once it lands):
///
///   - `opening`: `start()` has been called; the device is not yet
///     confirmed running.
///   - `capturing`: `start()` returned successfully; the engine is running.
///   - `stopping`: `stop()` has been called; teardown is in flight.
///   - `free`: teardown completed (confirmed) — or nothing has ever opened
///     this key.
///   - `quarantined`: `start()` or `stop()` hit
///     `MicCapture.runWithTimeout`'s bound and abandoned its thread (a
///     blocked HAL mutex wait cannot be cancelled — see that function's doc
///     comment). Per spec D11, quarantine is released only by a later
///     confirmed transition for the same key, never by a timer; this shim
///     enforces nothing here, it only records what `MicCapture` reports.
///
/// Keyed by the Core Audio persistent device UID (`MicCapture.deviceUID`),
/// which is the closest thing to spec §5.2's `physical_key` this shim has
/// without the full `AudioSourceRegistry` — a device whose UID cannot be
/// read (unresolved bind) is recorded under one stable placeholder key
/// rather than silently dropped, so a lookup miss is still visible in
/// `snapshot()`.
public enum CaptureOwnershipShim {
    private static let tableLock = NSLock()
    // `nonisolated(unsafe)`: every access is guarded by `tableLock` below,
    // same discipline `MicCapture.boundDeviceName` and every other
    // lock-guarded static/instance var in this module already uses — the
    // lock, not the compiler, is what makes this safe.
    nonisolated(unsafe) private static var table: [String: (owner: String, state: String)] = [:]

    /// The placeholder key for a device whose UID could not be read at
    /// register time (an unresolved target, or a bind that never named a
    /// device). Public so a caller comparing `snapshot()` output does not
    /// have to guess the sentinel.
    public static let unresolvedKey = "(unresolved)"

    /// Register a physical key's owner and open it in the `opening` state.
    /// Called once per open attempt, before the device bind is attempted —
    /// see `MicCapture.start()`. A second `register` for the same key
    /// simply overwrites the previous entry; this shim does not detect or
    /// refuse a conflict (that is the later `LeaseTable`'s job).
    public static func register(owner: String, deviceUID: String?) {
        let key = physicalKey(deviceUID)
        tableLock.lock()
        table[key] = (owner: owner, state: "opening")
        tableLock.unlock()
    }

    /// Move an already-registered key to a new state, keeping its recorded
    /// owner. A transition for a key nothing ever `register`-ed creates it
    /// with an empty owner rather than refusing — this shim never blocks a
    /// caller on a bookkeeping gap.
    public static func transition(deviceUID: String?, to state: String) {
        let key = physicalKey(deviceUID)
        tableLock.lock()
        let owner = table[key]?.owner ?? ""
        table[key] = (owner: owner, state: state)
        tableLock.unlock()
    }

    /// A point-in-time copy, for diagnostics and tests only. Never read
    /// from an audio thread — this takes a lock, same rule as every other
    /// state this module guards.
    public static func snapshot() -> [String: (String, String)] {
        tableLock.lock(); defer { tableLock.unlock() }
        return table
    }

    /// Test seam: returns to the empty table, so a self-check does not leak
    /// state into whichever other check runs next in the same process.
    static func resetForTesting() {
        tableLock.lock(); table.removeAll(); tableLock.unlock()
    }

    private static func physicalKey(_ deviceUID: String?) -> String {
        guard let deviceUID, !deviceUID.isEmpty else { return unresolvedKey }
        return deviceUID
    }
}
