#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAudioCapture

@Suite("Microphone capture policy")
struct MicCapturePolicyTests {
    @Test("a startup configuration notification does not stale a fresh microphone binding")
    func startupConfigurationNotificationIsIgnored() {
        #expect(MicCapture.shouldInvalidateBindingForConfigurationChange(
            isRunning: false
        ) == false)
        #expect(MicCapture.shouldInvalidateBindingForConfigurationChange(
            isRunning: true
        ) == true)
    }

    @Test("voice processing is opt-in so the app does not alter input or speaker gain by default")
    func voiceProcessingDefaultsOff() {
        #expect(MicCapture.resolveEchoCancellationPreference(nil) == false)
    }

    @Test("capture reads its product-scoped preferences from the supplied store")
    func productPreferencesAreRead() throws {
        let suite = "dictation.capture-tests.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        #expect(MicCapture.echoCancellationKey == "dictation.echoCancellation")
        #expect(MicCapture.preferredDeviceKey == "dictation.micDeviceUID")
        #expect(MicCapture.echoCancellationPreference(defaults: defaults) == false)
        #expect(MicCapture.preferredInputDeviceUID(defaults: defaults) == nil)
        defaults.set(true, forKey: MicCapture.echoCancellationKey)
        defaults.set("fixture-device", forKey: MicCapture.preferredDeviceKey)
        #expect(MicCapture.echoCancellationPreference(defaults: defaults) == true)
        #expect(MicCapture.preferredInputDeviceUID(defaults: defaults) == "fixture-device")
        defaults.set(false, forKey: MicCapture.echoCancellationKey)
        defaults.set("", forKey: MicCapture.preferredDeviceKey)
        #expect(MicCapture.echoCancellationPreference(defaults: defaults) == false)
        #expect(MicCapture.preferredInputDeviceUID(defaults: defaults) == nil)
    }

    @Test("an explicit echo-cancellation preference is preserved")
    func explicitVoiceProcessingPreferenceWins() {
        #expect(MicCapture.resolveEchoCancellationPreference(true) == true)
        #expect(MicCapture.resolveEchoCancellationPreference(false) == false)
    }
}
#endif
