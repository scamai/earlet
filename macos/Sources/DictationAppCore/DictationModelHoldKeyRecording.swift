import Foundation

// Settings › Hold to talk › "Record a Key…" and Settings › Mouse button ›
// "Record a Mouse Button…": the model's side of recording a control. The
// recorders themselves (`DictationHoldKeyRecorder`,
// `DictationMouseSideButton.recorded`) are pure, and the Settings pane feeds
// them the window's own events.

extension DictationModel {
    /// Stops the live listener while Settings records a new key: a
    /// registered combination would otherwise be consumed before the window
    /// saw it, and a modifier would dictate. `false` while a dictation is
    /// under way.
    public func beginRecordingHoldKey() -> Bool {
        guard !recording, !busy, attempt == nil, !recordingHoldKey, !recordingMouseButton else { return false }
        keyCheck.stopTest()
        trigger?.stop()
        recordingHoldKey = true
        return true
    }

    /// What Settings should say about a recorded key before it is chosen:
    /// nil when it can be used, else why not.
    public func refusalForRecordedHoldKey(_ key: DictationHoldKey) -> String? {
        guard case .shortcut(let shortcut) = key, !env.canReserveShortcut(shortcut) else { return nil }
        return "macOS would not reserve \(shortcut.displayName). The system or another app may use it; try another."
    }

    /// Ends recording, choosing `key` when one was recorded, and turns the
    /// live listener back on.
    public func endRecordingHoldKey(choosing key: DictationHoldKey?) {
        guard recordingHoldKey else { return }
        recordingHoldKey = false
        let changed = key.map { $0 != holdKey } ?? false
        if let key, changed {
            env.settings.holdKey = key
            holdKey = key
            trigger?.update(key)
            keyCheck.select(key)
            refreshCaution()
            if enabled { status = enabledStatus }
        }
        if let trigger, !trigger.start() {
            env.log("the key listener did not restart after recording")
            status = holdKeyRefusedStatus
        }
        if changed, env.isDictationFrontmost() { keyCheck.startTest() }
    }

    /// When the listener for the chosen key cannot start.
    var holdKeyRefusedStatus: String {
        if case .shortcut(let shortcut) = holdKey {
            return "macOS would not reserve \(shortcut.displayName). Choose another hold key in Settings."
        }
        return "The shortcut needs Input Monitoring permission. Check access, then restart Earlet."
    }

    /// Stops the mouse button's listener while Settings records a new one:
    /// the listener swallows the bound button, so the window would never see
    /// it. `false` while a dictation is under way.
    public func beginRecordingMouseButton() -> Bool {
        guard !recording, !busy, attempt == nil, !recordingHoldKey, !recordingMouseButton else { return false }
        mouseTrigger?.stop()
        recordingMouseButton = true
        return true
    }

    /// Ends recording, choosing `button` when one was recorded, and turns
    /// the mouse button's listener back on.
    public func endRecordingMouseButton(choosing button: DictationMouseSideButton?) {
        guard recordingMouseButton else { return }
        recordingMouseButton = false
        if let button { selectMouseButton(button) }
        if enabled {
            armMouseTrigger()
            status = enabledStatus
        }
    }
}
