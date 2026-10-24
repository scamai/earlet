#if canImport(Testing)
import CoreAudio
import Foundation
import Testing
@testable import DictationAudioCapture

/// Tests for the D13 capture-backend seam that need NO audio hardware.
///
/// Suite name deliberately contains "MicCapture" so it is inside the project's
/// standing filter
/// (`--filter "MicCapture|MicBind|InputFollow|DictationCapture|ColdStart|StaleAudio"`).
///
/// Nothing here opens a microphone or starts a unit. The one test that talks to
/// Core Audio registers and unregisters a PROPERTY LISTENER on the system
/// object, which neither opens a device nor takes the mic — the whole point of
/// the C listener API is that add and remove are pointer-identical, and that
/// claim is only worth anything if it is exercised against the real API.
@Suite("MicCapture backend seam (D13)")
struct MicCaptureBackendTests {

    // MARK: - S1: listener identity, and inertness after removal

    /// The finding this pins: the block-based listener API cannot be
    /// unregistered from Swift, because its listener parameter is a thick Swift
    /// function and every call site bridges a fresh block. The C API plus a
    /// `clientData` box is pointer-identical, so removal actually removes.
    ///
    /// The removal STATUS is asserted, but it is deliberately not the point —
    /// `AudioObjectRemovePropertyListener` is documented to be forgiving, so a
    /// status alone cannot tell a real unregistration from a no-op. What makes
    /// this a real test is the second half: after removal the box is
    /// invalidated, so an event driven straight through the registered
    /// `clientData` pointer — exactly what a HAL notification already in flight
    /// would do — no longer reaches the capture.
    @Test("a bound-device listener is removed by identity, and a later event cannot reach it")
    func boundDeviceListenerRemovalIsExactAndFinal() {
        final class Sink: @unchecked Sendable {
            private let lock = NSLock()
            private var seen: [AudioObjectPropertySelector] = []
            func note(_ s: AudioObjectPropertySelector) {
                lock.lock(); seen.append(s); lock.unlock()
            }
            var count: Int { lock.lock(); defer { lock.unlock() }; return seen.count }
        }
        let sink = Sink()
        // The box hops onto this queue, exactly as it does in production, so the
        // test drains it with a `sync` barrier rather than guessing at timing.
        let queue = DispatchQueue(label: "test.listener")
        let box = BoundDeviceListenerBox(queue: queue) { sink.note($0) }
        // The production lifetime: a deliberate `+1` so the box cannot be freed
        // under a notification the removal could not drain.
        let retained = Unmanaged.passRetained(box)
        let clientData = retained.toOpaque()
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        let system = AudioObjectID(kAudioObjectSystemObject)

        let addStatus = AudioObjectAddPropertyListener(
            system, &address, boundDeviceListenerProc, clientData)
        #expect(addStatus == noErr)

        // Live before removal: the C proc reaches the handler through the
        // clientData pointer the registration carries.
        #expect(box.isLive)
        #expect(boundDeviceListenerProc(system, 1, &address, clientData) == noErr)
        queue.sync {}
        #expect(sink.count == 1)

        var removeAddress = address
        let removeStatus = AudioObjectRemovePropertyListener(
            system, &removeAddress, boundDeviceListenerProc, clientData)
        #expect(removeStatus == noErr)

        // What the status cannot tell us. After the teardown the box carries no
        // handler, so an event the HAL had already dispatched — modelled here by
        // driving the proc and the box directly — reaches nothing.
        box.invalidate()
        #expect(!box.isLive)
        #expect(boundDeviceListenerProc(system, 1, &removeAddress, clientData) == noErr)
        box.deliver(selector: kAudioDevicePropertyNominalSampleRate)
        queue.sync {}
        #expect(sink.count == 1)
        retained.release()
    }

    /// N1: the box is registered with `Unmanaged.passRetained` and its `+1` is
    /// released only after a grace, because `AudioObjectRemovePropertyListener`
    /// does not synchronise with a notification already running. So an
    /// invalidated box is still valid MEMORY, and the C proc must find it,
    /// early-return on it, and deliver nothing. This is the interleaving that
    /// crashed inside Core Audio when the box could be freed at removal.
    @Test("driving the listener proc on an invalidated box delivers nothing and does not crash")
    func invalidatedBoxIsInertNotFreed() {
        final class Sink: @unchecked Sendable {
            private let lock = NSLock()
            private var count = 0
            func note() { lock.lock(); count += 1; lock.unlock() }
            var value: Int { lock.lock(); defer { lock.unlock() }; return count }
        }
        let sink = Sink()
        let box = BoundDeviceListenerBox(queue: DispatchQueue(label: "test.invalidated")) { _ in
            sink.note()
        }
        // The lifetime the real install uses: a deliberate `+1` that keeps the
        // memory alive regardless of any Swift reference.
        let retained = Unmanaged.passRetained(box)
        let clientData = retained.toOpaque()
        box.invalidate()
        #expect(!box.isLive)

        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        // Exactly what the HAL notification thread does with a pointer whose
        // registration has just been removed.
        for _ in 0..<50 {
            #expect(boundDeviceListenerProc(
                AudioObjectID(kAudioObjectSystemObject), 1, &address, clientData) == noErr)
        }
        // Deliver is also inert, so an event that had already made the queue hop
        // before `invalidate()` cannot reach the capture either.
        box.deliver(selector: kAudioDevicePropertyNominalSampleRate)
        #expect(sink.value == 0)
        retained.release()
    }

    @Test("the listener release grace outlives any in-flight notification")
    func releaseGraceIsGenerous() {
        // Not a magic number to tune down: there is no API to drain an in-flight
        // Core Audio notification, so the only defence is time, and the cost of
        // being wrong is a crash on a Core Audio thread.
        #expect(BoundDeviceListenerBox.releaseGrace >= 1.0)
    }

    @Test("a listener box with no clientData is a no-op rather than a crash")
    func listenerProcToleratesMissingClientData() {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        #expect(boundDeviceListenerProc(
            AudioObjectID(kAudioObjectSystemObject), 1, &address, nil) == noErr)
    }

    // MARK: - S2: the startup rate flip must not latch a capture as stale

    /// A nominal-rate event reporting the rate the client format is already on
    /// is an event, not a change. Crediting those is how every Bluetooth press
    /// came back flagged stale.
    @Test("MicCapture drops a nominal-rate event that reports the rate its baseline is on")
    func noOpRateFlipIsNotAChange() {
        #expect(MicCapture.nominalRateChangeIsNoOp(newRate: 16000, baselineRate: 16000))
        // Float round-tripping only, same tolerance the rest of the file uses.
        #expect(MicCapture.nominalRateChangeIsNoOp(newRate: 16000.4, baselineRate: 16000))
        // The real A2DP -> HFP flip.
        #expect(!MicCapture.nominalRateChangeIsNoOp(newRate: 16000, baselineRate: 44100))
        #expect(!MicCapture.nominalRateChangeIsNoOp(newRate: 44100, baselineRate: 16000))
        // "We do not know" is never "no change": a device that will not answer
        // must not be able to silence a real event.
        #expect(!MicCapture.nominalRateChangeIsNoOp(newRate: 0, baselineRate: 16000))
        #expect(!MicCapture.nominalRateChangeIsNoOp(newRate: 16000, baselineRate: 0))
    }

    /// The failure the client-format keying produced: on Bluetooth the client
    /// format stays at the PRE-flip rate (44 100) while the baseline settles on
    /// the post-flip one (16 000), so an HFP -> A2DP return posting 44 100 — the
    /// classic stale-format death — was dropped as "a rate we are already on"
    /// and the capture reported itself healthy. Keyed to the baseline, the same
    /// event is credited.
    @Test("an HFP to A2DP return is credited as a change, and a repeat of the baseline is not")
    func boundDeviceRateEventsAreJudgedAgainstTheSettledBaseline() {
        let mic = MicCapture(voiceProcessingAllowed: false)
        mic.markRunningForTesting()
        // What a real Bluetooth bind records: baseline = the rate the device
        // settled on after the unit opened it; client format = the pre-flip rate
        // the AUHAL is still converting from.
        mic.markBoundDeviceRatesForTesting(baselineRate: 16000, clientFormatRate: 44100)
        #expect(!mic.configurationChangedForTesting())

        // The device re-posting its settled rate is an event, not a change.
        mic.applyBoundDeviceEvent(
            selector: kAudioDevicePropertyNominalSampleRate, observedRate: 16000)
        #expect(!mic.configurationChangedForTesting())

        // The return to A2DP. Equal to the client format rate, which is exactly
        // why the old keying swallowed it.
        mic.applyBoundDeviceEvent(
            selector: kAudioDevicePropertyNominalSampleRate, observedRate: 44100)
        #expect(mic.configurationChangedForTesting())
    }

    @Test("a device-is-alive event is never filtered by the rate predicate")
    func livenessEventsAreAlwaysCredited() {
        let mic = MicCapture(voiceProcessingAllowed: false)
        mic.markRunningForTesting()
        mic.markBoundDeviceRatesForTesting(baselineRate: 16000, clientFormatRate: 16000)
        // `observedRate` is 0 for a non-rate selector, and the filter only ever
        // looks at rate events — a headset walking away must not be silenced by
        // the rate it happened to leave behind.
        mic.applyBoundDeviceEvent(
            selector: kAudioDevicePropertyDeviceIsAlive, observedRate: 0)
        #expect(mic.configurationChangedForTesting())
    }

    /// A capture that never started must not be able to latch itself stale — the
    /// same startup-churn gate the engine observer uses.
    @Test("a bound-device event on a capture that is not running changes nothing")
    func eventsBeforeRunningAreIgnored() {
        let mic = MicCapture(voiceProcessingAllowed: false)
        mic.markBoundDeviceRatesForTesting(baselineRate: 16000, clientFormatRate: 16000)
        mic.applyBoundDeviceEvent(
            selector: kAudioDevicePropertyDeviceIsAlive, observedRate: 0)
        #expect(!mic.configurationChangedForTesting())
    }

    /// The gate the listener body shares with the engine observer. Pinned here
    /// too because S2 was a failure of WHEN the listener was installed relative
    /// to this gate, not of the gate itself: with listeners installed before
    /// the unit starts, the startup flip always arrives with `isRunning` true.
    @Test("the startup-churn gate is still the only thing that admits a change")
    func startupChurnGateUnchanged() {
        #expect(!MicCapture.shouldInvalidateBindingForConfigurationChange(isRunning: false))
        #expect(MicCapture.shouldInvalidateBindingForConfigurationChange(isRunning: true))
    }

    // MARK: - backend selection matrix

    @Test("the HAL backend is selected only with the flag on and echo cancellation off")
    func backendSelectionMatrix() {
        let halEnabledSelection = MicCapture.backendSelection(halFlagEnabled: true, wantAEC: false)
        #expect(halEnabledSelection.useHAL)
        #expect(halEnabledSelection.backendName == "hal-input")
        #expect(halEnabledSelection.logLine == "[mic] backend=hal-input\n")

        let flagOff = MicCapture.backendSelection(halFlagEnabled: false, wantAEC: false)
        #expect(!flagOff.useHAL)
        #expect(flagOff.backendName == "avaudioengine")
        #expect(flagOff.logLine == "[mic] backend=avaudioengine (reason: flag off)\n")

        // AEC needs VoiceProcessingIO and an output reference, so it wins the
        // reason whenever it applies — including with the flag on.
        let aecOn = MicCapture.backendSelection(halFlagEnabled: true, wantAEC: true)
        #expect(!aecOn.useHAL)
        #expect(aecOn.logLine == "[mic] backend=avaudioengine (reason: aec on)\n")
        let both = MicCapture.backendSelection(halFlagEnabled: false, wantAEC: true)
        #expect(!both.useHAL)
        #expect(both.logLine == "[mic] backend=avaudioengine (reason: aec on)\n")
    }

    @Test("the HAL kill switch defaults ON and an explicit false still turns it off")
    func killSwitchDefaultsOn() {
        let key = "dictation.micCapture.halInput"
        let defaults = UserDefaults.standard
        let original = defaults.object(forKey: key) as? Bool
        defer {
            if let original { defaults.set(original, forKey: key) }
            else { defaults.removeObject(forKey: key) }
        }
        defaults.removeObject(forKey: key)
        #expect(MicCapture.halInputBackendEnabled)
        defaults.set(false, forKey: key)
        #expect(!MicCapture.halInputBackendEnabled)
        defaults.set(true, forKey: key)
        #expect(MicCapture.halInputBackendEnabled)
    }

    // MARK: - S3: which backends need an explicit device before they have a unit

    @Test("only the HAL backend has no input unit until a device is bound")
    func requiresExplicitBindDistinguishesTheBackends() {
        #expect(HALInputBackend().requiresExplicitBind)
        #expect(!EngineBackend(engine: .init()).requiresExplicitBind)
        // Voice processing is not a capability of an input-only unit, and the
        // backend must say so rather than silently capture without AEC.
        #expect(throws: MicCaptureError.self) {
            try HALInputBackend().setVoiceProcessingEnabled(true)
        }
        // Turning it OFF is what the selection rule guarantees is asked, and it
        // must not throw.
        #expect(throws: Never.self) {
            try HALInputBackend().setVoiceProcessingEnabled(false)
        }
        // No unit yet: every format accessor answers "we do not know" rather
        // than inventing a zero format.
        let backend = HALInputBackend()
        #expect(backend.inputFormat() == nil)
        #expect(backend.setStreamFormat(rate: 48000, channels: 1) == nil)
        #expect(backend.currentDevice() == nil)
        #expect(!backend.isRunning)
        #expect(backend.renderStats() == (0, noErr, 0))
    }

    // MARK: - S5: render-buffer sizing must cover the device's slice

    @Test("the pre-allocated render buffer covers the unit's own maximum slice")
    func renderCapacityCoversEveryClaimant() {
        let floor = HALInputBackend.renderCapacityFloor
        // The unit's pre-initialize default (1156) never shrinks the floor.
        #expect(HALInputBackend.renderCapacity(
            bufferSize: 4096, maximumFramesPerSlice: 1156) == floor)
        // An unanswered property is not an excuse to under-allocate.
        #expect(HALInputBackend.renderCapacity(
            bufferSize: 4096, maximumFramesPerSlice: nil) == floor)
        // A device or an Audio MIDI Setup buffer larger than the floor wins —
        // this is the case whose absence made an oversized render a silent
        // dead capture.
        #expect(HALInputBackend.renderCapacity(
            bufferSize: 4096, maximumFramesPerSlice: floor * 2) == floor * 2)
        #expect(HALInputBackend.renderCapacity(
            bufferSize: floor * 4, maximumFramesPerSlice: nil) == floor * 4)
    }

    // MARK: - S6: one start at a time, and an abandoned one cleans up after itself

    @Test("a second HAL start is refused while the first is still inside its 12s bound")
    func startGateRefusesAnOverlappingStart() {
        let gate = HALStartGate()
        let first = gate.begin()
        #expect(first != nil)
        // The abandoned-thread window: the caller has thrown, `isRunning` is
        // still false, so a second start() reaches the gate.
        #expect(gate.begin() == nil)
        #expect(gate.isCurrent(first!))
        #expect(gate.end(first!))
        // Released: the next start proceeds and gets a new generation.
        let second = gate.begin()
        #expect(second != nil)
        #expect(second! > first!)
        #expect(!gate.isCurrent(first!))
    }

    /// N3: the tap callback runs on the IO thread and cannot take the gate's
    /// lock, so it compares a captured generation against this lock-free read.
    /// It has to track `begin()` for a superseded unit's audio to be dropped.
    @Test("the gate publishes its generation for the tap callback to read without a lock")
    func publishedGenerationTracksTheClaim() {
        let gate = HALStartGate()
        #expect(gate.publishedGeneration == 0)
        let first = gate.begin()
        #expect(gate.publishedGeneration == first)
        #expect(gate.end(first!))
        let second = gate.begin()
        #expect(gate.publishedGeneration == second)
        // The check the tap makes: a buffer from the first unit is now dropped,
        // a buffer from the second is not.
        #expect(first! != gate.publishedGeneration)
        #expect(second! == gate.publishedGeneration)
    }

    @Test("a start abandoned past the bound stops blocking later starts, and knows it was superseded")
    func startGateGoesStaleAndReportsSupersession() {
        let gate = HALStartGate()
        let now = DispatchTime.now()
        let abandoned = gate.begin(now: now)
        #expect(abandoned != nil)
        // A body that blocks forever must not kill dictation permanently: past
        // `start()`'s own bound its caller has already given up on it.
        let past = DispatchTime(
            uptimeNanoseconds: now.uptimeNanoseconds
                + UInt64(HALStartGate.staleAfter * 1_000_000_000) + 1)
        let successor = gate.begin(now: past)
        #expect(successor != nil)
        // This is the check the abandoned body makes before publishing itself:
        // it must find that it is no longer current, and tear its own unit down
        // instead of overwriting the live capture.
        #expect(!gate.isCurrent(abandoned!))
        #expect(!gate.end(abandoned!))
        // Ending a superseded generation must not release the successor's claim.
        #expect(gate.isCurrent(successor!))
        #expect(gate.begin(now: past) == nil)
    }
}
#endif
