import Foundation

/// Per-device input gain, in `UserDefaults`, alongside
/// `MicCapture.preferredDeviceKey`.
///
/// **Per device, never global.** The XM5 in the field report needs something
/// like +16 dB to be usable; applying that same +16 dB to the built-in
/// MacBook Pro microphone — already at 0.50 system volume with a much hotter
/// capsule — would drive it into the knee on every normal sentence. One
/// number shared by both devices is a number that is wrong for one of them,
/// and the user switching headsets is exactly when they would not think to
/// look.
///
/// The store is deliberately dumb: no cache, no notifications, no observer.
/// `MicCapture` reads it at `start()` when the device is actually bound, and
/// a live slider change pushes the new value at the running tap directly —
/// so there is no second copy of this state that can drift from the
/// preference.
public struct MicGainStore: @unchecked Sendable {
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// `dictation.micGain.<deviceUID>`, in the `ai.scam.dictation` defaults
    /// domain (Dictation's own namespace). The UID is the same one
    /// `MicCapture.preferredInputDeviceID()` matches on, so the key survives
    /// an unplug/replug and does NOT survive a genuinely different headset.
    public static func key(deviceUID: String) -> String {
        "dictation.micGain.\(deviceUID)"
    }

    /// The stored gain, or 0 dB (unity) for anything that is not a value we
    /// wrote.
    ///
    /// **Fails safe, deliberately.** A preferences file is a text file a user
    /// or a script can edit, and whatever comes back from it is about to
    /// become a multiplier on live audio. NaN would poison every sample in
    /// the buffer; a negative value would attenuate a mic that is already too
    /// quiet; a large positive value would clip everything into the knee.
    /// All three read back as 0 — the shipped default, and the one value that
    /// cannot make things worse than not having this feature.
    ///
    /// An empty UID means "follow the system default device" (the same
    /// convention `preferredDeviceKey` uses) and has no per-device gain to
    /// look up, so it too is 0.
    public func gainDB(deviceUID: String) -> Float {
        guard !deviceUID.isEmpty else { return InputGain.minDB }
        let key = Self.key(deviceUID: deviceUID)
        guard defaults.object(forKey: key) != nil else { return InputGain.minDB }
        let stored = defaults.float(forKey: key)
        guard stored.isFinite,
              stored >= InputGain.minDB,
              stored <= InputGain.maxDB else { return InputGain.minDB }
        return stored
    }

    /// Clamps to `InputGain`'s range before writing, so an out-of-range value
    /// cannot enter the file through this door either.
    ///
    /// Writing the default REMOVES the key rather than storing `0`. That
    /// keeps "this device has never been calibrated" and "this device was
    /// calibrated back to unity" indistinguishable — which they are, for
    /// every consumer — and stops the preferences accumulating a row per
    /// device the user ever plugged in.
    public func setGainDB(_ dB: Float, deviceUID: String) {
        guard !deviceUID.isEmpty else { return }
        let key = Self.key(deviceUID: deviceUID)
        let clamped = InputGain.clampDB(dB)
        if clamped == InputGain.minDB {
            defaults.removeObject(forKey: key)
        } else {
            defaults.set(clamped, forKey: key)
        }
    }
}
