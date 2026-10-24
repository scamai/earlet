import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation

/// One bound-device property listener registration, owned by `MicCapture` and
/// handed to Core Audio as `clientData`.
///
/// **Why a C listener and a box instead of the block API.** Fix round 1, S1:
/// Core Audio's `AudioObjectAddPropertyListenerBlock` takes its listener as a
/// THICK SWIFT FUNCTION — the imported parameter type is
/// `@escaping (UInt32, UnsafePointer<AudioObjectPropertyAddress>) -> ()`, not a
/// block — so SILGen re-bridges whatever it is handed into a *fresh* ObjC block
/// at every call site. Add and Remove therefore receive different block
/// objects, `AudioObjectRemovePropertyListenerBlock` returns `noErr` for the
/// one it never knew, and the listener stays installed for the life of the
/// process with nothing to say so. Declaring the value `@convention(block)`
/// does not help: it converts block → thick at the argument and the C boundary
/// bridges thick → block again (measured: three `init_block_storage_header`
/// instructions for one add-then-remove pair, against two for the naive
/// version).
///
/// `AudioObjectAddPropertyListener` / `AudioObjectRemovePropertyListener` take
/// a `@convention(c)` function pointer plus a `clientData` pointer. Both are
/// raw pointers with no bridging anywhere, so identity is guaranteed by
/// construction: the same proc and the same box pointer that registered are
/// the ones that unregister.
///
/// The box also makes the registration *revocable independently of removal*.
/// `invalidate()` drops the handler, so a notification already in flight when
/// `stop()` runs cannot reach a capture that is being torn down — which is the
/// property the S1 test asserts, because a removal status cannot.
///
/// **Ownership, and the use-after-free it closes (fix round 2, N1).** Core Audio
/// holds nothing but the raw `clientData` pointer, and
/// `AudioObjectRemovePropertyListener` does NOT synchronise with a listener
/// invocation already in flight. So a box whose last strong reference is dropped
/// at removal can be FREED while the HAL's notification thread is inside
/// `boundDeviceListenerProc` holding its address — a crash on a Core Audio
/// thread, uncatchable from Swift, and provoked by exactly the event this
/// backend exists to survive: releasing a Bluetooth microphone makes the headset
/// fall back to A2DP, which changes the nominal sample rate, which fires the
/// listener the teardown is in the middle of removing.
///
/// (The block API did not have this problem, because `Block_copy` gave Core
/// Audio its own reference. Moving to the C API for identity therefore had to
/// take over that job.)
///
/// So the registration is made with `Unmanaged.passRetained` — a deliberate +1
/// that keeps the memory alive no matter what `MicCapture` does with its own
/// reference — and `MicCapture.removeBoundDeviceListenersLocked` releases that
/// +1 only after `releaseGrace` seconds, long after any in-flight notification
/// has drained. In between, the box is valid memory with no handler, which is
/// precisely what `boundDeviceListenerProc`'s `isLive` check is for. There is no
/// API to drain in-flight notifications, so a prompt release re-opens the
/// window; anything that "tidies up" this lifetime must keep that in mind.
final class BoundDeviceListenerBox: @unchecked Sendable {
    /// How long an invalidated box's memory is kept alive after its listener has
    /// been unregistered. Two seconds is orders of magnitude more than a HAL
    /// notification takes to run, and the cost of being wrong is a crash inside
    /// Core Audio while the cost of being generous is a few dozen bytes per
    /// capture.
    static let releaseGrace: TimeInterval = 2.0

    private let lock = NSLock()
    private var handler: ((AudioObjectPropertySelector) -> Void)?
    /// Where the work runs. The C proc is called on one of the HAL's own
    /// notification threads; the handler takes `MicCapture.lock`, which
    /// `start()`/`stop()` can hold for seconds under HAL contention, so it must
    /// not run there. A serial queue also means two property events can never
    /// run the body concurrently.
    private let queue: DispatchQueue

    init(queue: DispatchQueue, handler: @escaping (AudioObjectPropertySelector) -> Void) {
        self.queue = queue
        self.handler = handler
    }

    /// Stop delivering. Called after `AudioObjectRemovePropertyListener`, so
    /// that an event the HAL had already dispatched cannot land afterwards.
    func invalidate() {
        lock.lock()
        handler = nil
        lock.unlock()
    }

    /// True while this box still has a handler — what a test asserts after
    /// `invalidate()`.
    var isLive: Bool {
        lock.lock(); defer { lock.unlock() }
        return handler != nil
    }

    /// Hop off the HAL's notification thread, then deliver.
    func enqueue(selector: AudioObjectPropertySelector) {
        queue.async { [weak self] in self?.deliver(selector: selector) }
    }

    /// Deliver synchronously. The direct-drive seam: a test can call this (or
    /// the C proc with this box's pointer) to prove that a post-removal event
    /// no longer reaches the capture, without changing a real device's rate.
    func deliver(selector: AudioObjectPropertySelector) {
        lock.lock()
        let handler = self.handler
        lock.unlock()
        handler?(selector)
    }
}

/// The `@convention(c)` listener Core Audio calls. Does nothing but recover the
/// box from `clientData`, drop the event if that box has been invalidated, and
/// hand each changed selector over; the box owns the thread hop.
///
/// `takeUnretainedValue` is safe ONLY because the box is registered with
/// `Unmanaged.passRetained` and that reference is released a full grace period
/// AFTER `AudioObjectRemovePropertyListener` returns — see
/// `BoundDeviceListenerBox` for the use-after-free this exists to prevent.
/// An invalidated box is therefore still valid MEMORY; it simply has no handler
/// and returns here immediately.
let boundDeviceListenerProc: AudioObjectPropertyListenerProc = {
    _, addressCount, addresses, clientData in
    guard let clientData else { return noErr }
    let box = Unmanaged<BoundDeviceListenerBox>.fromOpaque(clientData).takeUnretainedValue()
    // Early-return on a box whose registration has been torn down. This runs on
    // one of the HAL's notification threads (never the IO thread), so the short
    // lock inside `isLive` is fine here — what is NOT fine is doing any work
    // that reaches a capture being destroyed.
    guard box.isLive else { return noErr }
    for index in 0..<Int(addressCount) {
        box.enqueue(selector: addresses[index].mSelector)
    }
    return noErr
}

/// Serializes `start()` bodies for the HAL backend and lets an ABANDONED one
/// discover that it has been superseded (fix round 1, S6).
///
/// `MicCapture.start()` bounds its body at 12 s and, on timeout, ABANDONS the
/// detached thread while leaving `isRunning` false — the file documents that
/// this happens and that the timeouts are usually false. Pre-D13 both the
/// abandoned body and the next one reconfigured the SAME `AVAudioEngine`, so a
/// second live input unit was impossible. A fresh `HALInputBackend` per start
/// makes it possible: the abandoned body's unit is reachable only from its own
/// local, and no `stop()` would ever dispose it, leaving two AUHAL units on one
/// microphone with one of them untracked.
///
/// Two rules, both enforced here:
/// 1. While a start is in flight and still inside the 12 s bound its caller
///    gave it, a second start is REFUSED rather than allowed to create a second
///    unit. Bounded, because a body that blocks forever must not kill dictation
///    permanently.
/// 2. A body that finds it is no longer the current generation disposes its own
///    unit and does not publish itself — the abandoned thread cleans up after
///    itself, on its own thread, with no cross-thread disposal race.
final class HALStartGate: @unchecked Sendable {
    /// `MicCapture.start()`'s own bound. Past it the caller has already been
    /// told the start failed, so a later start may proceed.
    static let staleAfter: TimeInterval = 12.0

    /// The current generation, readable WITHOUT taking `lock`.
    ///
    /// For the tap callback, which runs on the HAL's IO thread where an `NSLock`
    /// is not acceptable (fix round 2, N3: past `staleAfter` two units can
    /// legitimately be live at once, and a superseded unit's audio must never
    /// reach the stream, or the consumer gets two captures interleaved).
    /// Word-sized, naturally aligned, written only under `lock` by `begin()`,
    /// and a reader that sees a stale value for a single buffer is harmless —
    /// worst case one buffer of a superseded unit is yielded, or one buffer of
    /// the new one is dropped, during a handover that already discards its
    /// partial hold.
    private(set) var publishedGeneration = 0

    private let lock = NSLock()
    private var generation = 0
    private var inFlightSince: DispatchTime?

    /// Claim the gate. Returns the new generation, or nil when a previous start
    /// is still in flight and still inside `staleAfter`.
    func begin(now: DispatchTime = .now()) -> Int? {
        lock.lock(); defer { lock.unlock() }
        if let since = inFlightSince {
            let elapsed = Double(now.uptimeNanoseconds &- since.uptimeNanoseconds) / 1_000_000_000
            if elapsed < Self.staleAfter { return nil }
        }
        generation += 1
        publishedGeneration = generation
        inFlightSince = now
        return generation
    }

    /// Release the gate for `generation`. Returns whether it is still the
    /// current one; a false means a later start has superseded this body.
    @discardableResult
    func end(_ generation: Int) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let current = self.generation == generation
        if current { inFlightSince = nil }
        return current
    }

    func isCurrent(_ generation: Int) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return self.generation == generation
    }
}

/// The two format operations `MicCapture.reconcileInputFormat` needs from
/// whatever is holding the input unit.
///
/// Its own protocol, narrower than `MicInputBackend`, for one reason: the
/// PUBLIC `MicCapture.reconcileInputFormat(_ input: AVAudioInputNode, …)`
/// entry point still exists and is still called from outside this module
/// (`MicSweepCheck`, `MicFormatCheck`), and it has only a node — no engine,
/// no backend. Both paths must run the SAME reconciliation, because the one
/// time they diverged (`--mic-sweep` carrying its own copy of the
/// bind-then-read-format sequence) the diagnostic built to find a silent
/// microphone reported a rate-mismatched device as silent. So the public
/// node entry point wraps its node in `EngineNodeFormatTarget` and calls the
/// same function the backend path does.
protocol MicInputFormatTarget: AnyObject {
    /// The format the tap will be fed from — `AVAudioEngine`'s
    /// `inputNode.outputFormat(forBus: 0)`, or the AUHAL's client format on
    /// the input bus.
    ///
    /// Optional because the AUHAL backend can be asked before a unit exists
    /// (a refused bind), and there is no such thing as a zero
    /// `AVAudioFormat` — `AVAudioFormat(streamDescription:)` returns nil
    /// rather than a 0 Hz format. `nil` means "we do not know", exactly like
    /// `nominalSampleRate`'s 0 and `inputVolume`'s nil. The engine path never
    /// returns nil, so every existing log line and error message is
    /// unchanged there.
    func inputFormat() -> AVAudioFormat?

    /// Write the client-side format onto the input unit's input bus
    /// (element 1, output scope — the host side of the AUHAL).
    ///
    /// - Returns: the `OSStatus` of the write, or `nil` when there is no
    ///   audio unit to write to at all. The distinction is load-bearing:
    ///   `reconcileInputFormat` must return early WITHOUT logging when there
    ///   is no unit (today's `guard let unit = input.audioUnit else { return
    ///   deviceRate }`), and must log when a real write returned a status.
    func setStreamFormat(rate: Double, channels: UInt32) -> OSStatus?
}

/// One microphone capture backend: the thing that binds a device, hands us a
/// format, delivers buffers to a tap, and starts and stops.
///
/// AVAudioEngine owns an output node as well as input. An input-only Core
/// Audio unit avoids that output interaction when echo cancellation is off;
/// the engine path remains available when voice processing needs an output
/// reference.
///
/// The seam is deliberately shaped around what `startEngineLocked` ALREADY
/// did, call for call and in order, so that `EngineBackend` is a pure
/// refactor: bind, voice processing, reconcile format, read format, install
/// tap, start. Nothing here is a new abstraction over audio; it is the
/// existing sequence with one implementation swapped in behind it.
protocol MicInputBackend: MicInputFormatTarget {
    /// `"hal-input"` / `"avaudioengine"` — the token in the `[mic] backend=`
    /// line and in `MicCapture.activeBackendName()`.
    var backendName: String { get }

    /// False when there is nothing to bind a device to at all — the engine's
    /// `inputNode.audioUnit` being nil. `startEngineLocked` skips its whole
    /// bind block in that case rather than failing, which is what it did
    /// before this seam existed (`if let unit = input.audioUnit { … }`).
    var canBind: Bool { get }

    /// True when the backend has NO usable input unit until `bind` has been
    /// called — the AUHAL, which is created inside `bind`.
    ///
    /// `AVAudioEngine` is the opposite: its input node exists and is latched
    /// to whatever device CoreAudio left it on, so a start with no resolvable
    /// target still captured. Fix round 1, S3: without this distinction the
    /// HAL path threw "no usable input device" the instant device enumeration
    /// was transiently empty — precisely when a headset is connecting or
    /// disconnecting — where the engine path had served the press from the
    /// system default.
    var requiresExplicitBind: Bool { get }

    /// Point the unit at `device`. Non-`noErr` leaves the backend in the
    /// same state a refused `kAudioOutputUnitProperty_CurrentDevice` write
    /// left the engine in: nothing bound, and the caller's fallback may try
    /// another device with a second `bind`.
    ///
    /// `kAudioObjectUnknown` means "no resolvable target": the backend must
    /// still end up with a usable unit, on whatever device the HAL has it on
    /// (S3). `currentDevice()` then reports what that turned out to be.
    func bind(device: AudioDeviceID) -> OSStatus

    /// The device the unit is actually pointed at, read back from the unit —
    /// nil when there is no unit or it will not say. Used to record
    /// `boundDeviceID` when no explicit device could be resolved.
    func currentDevice() -> AudioDeviceID?

    /// Enable or disable `AVAudioEngine`'s VoiceProcessingIO (AEC + the
    /// noise gate/AGC bundled with it).
    ///
    /// Throws when the backend cannot honour the request. `HALInputBackend`
    /// throws for `true` unconditionally: AEC needs the VoiceProcessingIO
    /// unit and an output reference, and an input-only unit has neither by
    /// construction. That is not a limitation to work around — it is why the
    /// backend is only selected when the AEC decision is already false.
    func setVoiceProcessingEnabled(_ enabled: Bool) throws

    /// Install the tap. `format` must be the format `inputFormat()` reports
    /// AFTER reconciliation; the handler is called once per buffer on the
    /// HAL's IO thread and must not allocate, lock for long, or log.
    func installTap(
        bufferSize: AVAudioFrameCount,
        format: AVAudioFormat,
        handler: @escaping (AVAudioPCMBuffer) -> Void
    )

    /// Detach the tap. Always called AFTER `stop()` — see the
    /// `isSink || tap != nullptr` crash `MicCapture.stop()` documents.
    func removeTap()

    func start() throws

    /// Stop capturing. For the AUHAL backend this is also the teardown: the
    /// unit is uninitialized and DISPOSED here, so every `start()` gets a
    /// fresh unit (D13). The engine backend keeps its engine — the
    /// `.AVAudioEngineConfigurationChange` observer is registered against
    /// that one instance for the whole life of the `MicCapture`.
    func stop()

    var isRunning: Bool { get }

    /// What the render callback recorded: how many renders failed, the last
    /// failing status, and the worst time a single tap-handler call took.
    ///
    /// The render callback must not log (fix round 1, S5): a
    /// `kAudioUnitErr_TooManyFramesToProcess` on a device with a very large
    /// buffer, or a frame count past the pre-allocated capacity, would
    /// otherwise be a completely silent dead capture — no buffer, no
    /// first-audible figure, nothing in the trace, and the press reported as
    /// "no audio". So the callback counts and a non-realtime reader logs.
    ///
    /// `maxHandlerNanos` is the number the decision to leave this backend
    /// default-ON for other people should rest on: the tap handler does real
    /// work inside the HAL's IO deadline (it allocates, converts, and takes
    /// three short locks — see the callback's doc comment), and until now that
    /// cost was an argument rather than a measurement.
    ///
    /// Read in `stop()`, AFTER `AudioOutputUnitStop`, which is a barrier for the
    /// IO thread — reading while the unit runs would be a real data race on
    /// these single-writer fields (fix round 2, N4). Survives `removeTap()`
    /// (N5). Always zeroes on the engine backend, which has no such callback.
    func renderStats() -> (failures: Int, lastStatus: OSStatus, maxHandlerNanos: UInt64)
}

// MARK: - AVAudioEngine

/// `AVAudioEngine` as a `MicInputBackend` — the behaviour `MicCapture` had
/// before D13, wrapped 1:1 and nothing more.
///
/// Holds the engine it was constructed with rather than making its own: the
/// `.AVAudioEngineConfigurationChange` observer in `MicCapture.init` is
/// registered with `object: engine`, so a second engine instance would
/// observe nothing. One engine per `MicCapture`, one `EngineBackend` over it,
/// for that instance's whole lifetime.
final class EngineBackend: MicInputBackend {
    private let engine: AVAudioEngine

    init(engine: AVAudioEngine) { self.engine = engine }

    var backendName: String { "avaudioengine" }

    private var input: AVAudioInputNode { engine.inputNode }

    var canBind: Bool { input.audioUnit != nil }

    /// False: the input node exists before any bind and is latched to whatever
    /// CoreAudio left it on. That is exactly the pre-D13 behaviour S3 restores
    /// for the HAL path.
    var requiresExplicitBind: Bool { false }

    func bind(device: AudioDeviceID) -> OSStatus {
        guard let unit = input.audioUnit else { return kAudioUnitErr_Uninitialized }
        var deviceID = device
        return AudioUnitSetProperty(
            unit, kAudioOutputUnitProperty_CurrentDevice,
            kAudioUnitScope_Global, 0,
            &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size))
    }

    func currentDevice() -> AudioDeviceID? {
        guard let unit = input.audioUnit else { return nil }
        return MicInputUnit.currentDevice(of: unit)
    }

    func setVoiceProcessingEnabled(_ enabled: Bool) throws {
        if #available(macOS 14.0, *) {
            try input.setVoiceProcessingEnabled(enabled)
        }
    }

    func inputFormat() -> AVAudioFormat? { EngineNodeFormatTarget.format(of: input) }

    func setStreamFormat(rate: Double, channels: UInt32) -> OSStatus? {
        EngineNodeFormatTarget.setStreamFormat(on: input, rate: rate, channels: channels)
    }

    func installTap(
        bufferSize: AVAudioFrameCount,
        format: AVAudioFormat,
        handler: @escaping (AVAudioPCMBuffer) -> Void
    ) {
        input.installTap(onBus: 0, bufferSize: bufferSize, format: format) { buffer, _ in
            handler(buffer)
        }
    }

    func removeTap() { input.removeTap(onBus: 0) }

    func start() throws { try engine.start() }

    func stop() { engine.stop() }

    var isRunning: Bool { engine.isRunning }

    func renderStats() -> (failures: Int, lastStatus: OSStatus, maxHandlerNanos: UInt64) {
        (0, noErr, 0)
    }
}

/// A bare `AVAudioInputNode` as a format target, for the public
/// `MicCapture.reconcileInputFormat(_ input: AVAudioInputNode, …)` entry
/// point that has a node and no backend. Also the single implementation
/// `EngineBackend` delegates its own two format methods to, so the node path
/// and the backend path cannot drift apart.
final class EngineNodeFormatTarget: MicInputFormatTarget {
    private let input: AVAudioInputNode

    init(input: AVAudioInputNode) { self.input = input }

    func inputFormat() -> AVAudioFormat? { Self.format(of: input) }

    func setStreamFormat(rate: Double, channels: UInt32) -> OSStatus? {
        Self.setStreamFormat(on: input, rate: rate, channels: channels)
    }

    static func format(of input: AVAudioInputNode) -> AVAudioFormat? {
        input.outputFormat(forBus: 0)
    }

    static func setStreamFormat(
        on input: AVAudioInputNode, rate: Double, channels: UInt32
    ) -> OSStatus? {
        guard let unit = input.audioUnit else { return nil }
        return MicInputUnit.setStreamFormat(on: unit, rate: rate, channels: channels)
    }
}

/// The AUHAL property reads and writes both backends share, so the engine's
/// stale-format repair and the HAL backend's own bind cannot end up asking for
/// subtly different things.
enum MicInputUnit {
    static func nonInterleavedFloat32(rate: Double, channels: UInt32) -> AudioStreamBasicDescription {
        AudioStreamBasicDescription(
            mSampleRate: rate,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked
                | kAudioFormatFlagIsNonInterleaved,
            mBytesPerPacket: 4,
            mFramesPerPacket: 1,
            mBytesPerFrame: 4,
            mChannelsPerFrame: channels,
            mBitsPerChannel: 32,
            mReserved: 0)
    }

    /// Element 1 / output scope is the AUHAL's input bus as seen by the host —
    /// the format the tap will be fed from.
    static func setStreamFormat(
        on unit: AudioUnit, rate: Double, channels: UInt32
    ) -> OSStatus {
        var asbd = nonInterleavedFloat32(rate: rate, channels: channels)
        return AudioUnitSetProperty(
            unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1,
            &asbd, UInt32(MemoryLayout<AudioStreamBasicDescription>.size))
    }

    static func streamFormat(of unit: AudioUnit) -> AVAudioFormat? {
        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        guard AudioUnitGetProperty(
            unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1,
            &asbd, &size) == noErr, asbd.mSampleRate > 0 else { return nil }
        return AVAudioFormat(streamDescription: &asbd)
    }

    static func currentDevice(of unit: AudioUnit) -> AudioDeviceID? {
        var device = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioUnitGetProperty(
            unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
            &device, &size) == noErr, device != kAudioObjectUnknown else { return nil }
        return device
    }

    static func maximumFramesPerSlice(of unit: AudioUnit) -> AVAudioFrameCount? {
        var frames: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioUnitGetProperty(
            unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0,
            &frames, &size) == noErr, frames > 0 else { return nil }
        return AVAudioFrameCount(frames)
    }

    @discardableResult
    static func setMaximumFramesPerSlice(
        on unit: AudioUnit, _ frames: AVAudioFrameCount
    ) -> OSStatus {
        var value = UInt32(frames)
        return AudioUnitSetProperty(
            unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0,
            &value, UInt32(MemoryLayout<UInt32>.size))
    }
}

// MARK: - input-only AUHAL

/// Everything the render callback is allowed to touch, in one object handed
/// to it as `inputProcRefCon`.
///
/// The callback runs on the HAL's IO thread and takes NO lock — the same
/// realtime discipline `FirstBufferLatch` and `InputGainBox` document, one
/// step further because there is nothing here it needs to synchronize with.
/// `unit` and `handler` are written only before `AudioOutputUnitStart` and
/// cleared only after `AudioOutputUnitStop`, both of which are barriers for
/// the IO thread: while callbacks can run, these fields do not change.
/// `unit` is cleared before the unit is uninitialized so that even a
/// pathological late callback cannot render through a disposed instance.
///
/// `renderFailures`/`lastRenderStatus` follow the same discipline in the other
/// direction: written ONLY by the IO thread, read only by a non-realtime
/// reader that tolerates a stale value and never feeds it back into the audio
/// path. Word-sized, naturally aligned, single-writer — the same trade
/// `unit`/`handler` already make, and the alternative (a lock or
/// `Synchronization.Atomic`, which needs macOS 15) is not available at this
/// deployment target.
final class HALRenderContext {
    /// Pre-allocated once per tap install, never per callback. The tap
    /// handler is handed THIS object every time; `handleInputBuffer` converts
    /// out of it synchronously and copies what it keeps
    /// (`Array(UnsafeBufferPointer(...))`), so reusing it is safe and is what
    /// makes the callback allocation-free.
    let buffer: AVAudioPCMBuffer
    /// An `AudioBufferList` whose `mData` pointers alias `buffer`'s own
    /// channel storage, so `AudioUnitRender` writes straight into the
    /// `AVAudioPCMBuffer` and there is no copy at all. Freed with `free()`,
    /// as `AudioBufferList.allocate` requires; the sample memory belongs to
    /// `buffer` and must not be freed here.
    let bufferList: UnsafeMutableAudioBufferListPointer
    let maxFrames: AVAudioFrameCount
    let bytesPerFrame: UInt32
    var unit: AudioUnit?
    var handler: ((AVAudioPCMBuffer) -> Void)?
    var renderFailures = 0
    var lastRenderStatus: OSStatus = noErr
    /// Worst wall time a single tap-handler call took, in nanoseconds — the
    /// number the "is the handler safe inside the IO deadline" question needs.
    var maxHandlerNanos: UInt64 = 0

    init(
        buffer: AVAudioPCMBuffer, bufferList: UnsafeMutableAudioBufferListPointer,
        maxFrames: AVAudioFrameCount, bytesPerFrame: UInt32
    ) {
        self.buffer = buffer
        self.bufferList = bufferList
        self.maxFrames = maxFrames
        self.bytesPerFrame = bytesPerFrame
    }

    deinit { free(bufferList.unsafeMutablePointer) }
}

/// Input-only AUHAL (Core Audio output audio unit, output bus disabled) —
/// D13's capture backend, and a direct transcription of the measured
/// reference implementation in
/// `docs/superpowers/specs/2026-09-14-xm5-route-flip-experiments/auhal_probe.swift`.
///
/// **The property sequence is the measurement.** EnableIO input=1 on bus 1,
/// EnableIO output=0 on bus 0, `CurrentDevice`, `StreamFormat` on the output
/// scope of bus 1, `SetInputCallback`, `AudioUnitInitialize`,
/// `AudioOutputUnitStart` — in that order, and with the reference's error
/// handling too: it gates ONLY on `CurrentDevice` and ignores both `EnableIO`
/// statuses (fix round 1, S4 — treating those as fatal refused starts on
/// aggregate/virtual inputs that the measured sequence would have served).
/// `auhal_probe` in this order delivered continuously in 6 of 6 phases (first
/// buffer 63-130 ms) where `AVAudioEngine` died or hung in 5 of 5 comparable
/// phases. Do not reorder it, and in particular do not let anything touch an
/// output bus: the entire point is that this unit never provokes the headset's
/// A2DP⇄HFP switch.
///
/// Voice processing is NEVER available here — see
/// `setVoiceProcessingEnabled`.
final class HALInputBackend: MicInputBackend {
    /// Frame capacity floor for the pre-allocated render buffer. Device buffer
    /// frame sizes on this codebase's measurements run 512-4096; Audio MIDI
    /// Setup and aggregate devices can go higher, so the unit is also TOLD to
    /// accept a slice this large (`MaximumFramesPerSlice`) rather than left at
    /// its 1156-frame default. 16384 is ~128 KB for a stereo device.
    static let renderCapacityFloor: AVAudioFrameCount = 16384

    /// Pure: how big the pre-allocated render buffer must be. Its own function
    /// so the S5 sizing rule is testable without a device.
    static func renderCapacity(
        bufferSize: AVAudioFrameCount, maximumFramesPerSlice: AVAudioFrameCount?
    ) -> AVAudioFrameCount {
        max(renderCapacityFloor, max(bufferSize, maximumFramesPerSlice ?? 0))
    }

    private var unit: AudioUnit?
    /// One strong reference, not two (fix round 1, m3). It is what keeps the
    /// context alive under the IO thread, which holds only an UNRETAINED
    /// opaque pointer to it.
    private var context: HALRenderContext?
    /// The last stats read out of a context before it was released, so a
    /// `removeTap()` does not throw away everything the capture recorded
    /// (fix round 2, N5): a capture that starts clean and then drops every
    /// render for a minute used to report nothing at all.
    private var lastRenderStats: (failures: Int, lastStatus: OSStatus, maxHandlerNanos: UInt64)
        = (0, noErr, 0)
    private var running = false
    /// The device `bind` pointed the unit at, or the device the unit reported
    /// when no explicit target could be resolved (S3). Read by
    /// `currentDevice()`'s fallback and by `installTap`'s diagnostics.
    private var boundDevice = AudioDeviceID(kAudioObjectUnknown)

    var backendName: String { "hal-input" }

    /// Always true: this backend creates its unit inside `bind`, so a
    /// failure to create one is reported as a bind STATUS (which the caller
    /// logs and can retry on another device) rather than as "there is
    /// nothing to bind", which would silently skip the bind entirely.
    var canBind: Bool { true }

    var requiresExplicitBind: Bool { true }

    var isRunning: Bool { running }

    func bind(device: AudioDeviceID) -> OSStatus {
        // A fresh unit per bind, and therefore per start: the whole D13
        // finding is about what happens on the SECOND open of a device, and
        // a reused instance carries whatever state the first open left.
        disposeUnit()

        var desc = AudioComponentDescription(
            componentType: kAudioUnitType_Output,
            componentSubType: 0x6168616C, // Core Audio output subtype ('ahal')
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0, componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &desc) else {
            return kAudioUnitErr_InvalidElement
        }
        var created: AudioUnit?
        let newStatus = AudioComponentInstanceNew(component, &created)
        guard newStatus == noErr, let au = created else {
            return newStatus == noErr ? kAudioUnitErr_FailedInitialization : newStatus
        }

        var one: UInt32 = 1
        var zero: UInt32 = 0
        let enableIn = AudioUnitSetProperty(
            au, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1,
            &one, UInt32(MemoryLayout<UInt32>.size))
        // The half that makes this backend exist. Leaving the output bus
        // enabled would re-introduce exactly the output-node coupling that
        // stops AVAudioEngine on a Bluetooth route flip.
        let disableOut = AudioUnitSetProperty(
            au, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0,
            &zero, UInt32(MemoryLayout<UInt32>.size))
        // Traced, never fatal — `auhal_probe` ignores both of these and the
        // sequence it ignores them in is the measurement (S4). An aggregate or
        // virtual input that refuses the output-element write captured fine in
        // the reference; refusing the start here killed dictation on it.
        if enableIn != noErr {
            MicCapture.trace("hal-input: EnableIO(input) status \(enableIn) (continuing)")
        }
        if disableOut != noErr {
            MicCapture.trace("hal-input: EnableIO(output=0) status \(disableOut) (continuing)")
        }

        // `kAudioObjectUnknown` means nothing could be resolved. The engine
        // path captured anyway from whatever its input node was latched to, so
        // the unit is kept and its own current device stands (S3); only an
        // explicit target is written.
        if device != kAudioObjectUnknown {
            var deviceID = device
            let bindStatus = AudioUnitSetProperty(
                au, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size))
            if bindStatus != noErr {
                AudioComponentInstanceDispose(au)
                return bindStatus
            }
        }

        unit = au
        // What the unit is ACTUALLY on: the explicit device, or whatever the
        // HAL had it on when no target resolved.
        boundDevice = device != kAudioObjectUnknown
            ? device
            : (MicInputUnit.currentDevice(of: au) ?? AudioDeviceID(kAudioObjectUnknown))

        // Non-interleaved float32 at the device's OWN nominal rate and input
        // channel count, so the existing `AVAudioConverter` path in
        // `handleInputBuffer` keeps working unchanged. A device that does not
        // answer for its rate keeps the unit's default format rather than
        // being handed a 0 Hz one.
        if boundDevice != kAudioObjectUnknown {
            let deviceRate = MicCapture.nominalSampleRate(boundDevice)
            if deviceRate > 0 {
                let channels = UInt32(max(1, MicCapture.inputChannelCount(boundDevice)))
                let formatStatus = MicInputUnit.setStreamFormat(
                    on: au, rate: deviceRate, channels: channels)
                if formatStatus != noErr {
                    // Not fatal, and not silent. The AUHAL converts on the
                    // input bus, so a refused client format means we capture at
                    // the unit's own idea of the format and
                    // `reconcileInputFormat` gets the next word — but a refusal
                    // here is the sort of thing that shows up later as an
                    // unexplained silent capture.
                    MicCapture.trace("hal-input: client stream format refused "
                                     + "(status \(formatStatus)) — keeping the unit's own")
                }
            }
        }
        return noErr
    }

    func currentDevice() -> AudioDeviceID? {
        guard let unit else {
            return boundDevice == kAudioObjectUnknown ? nil : boundDevice
        }
        return MicInputUnit.currentDevice(of: unit)
            ?? (boundDevice == kAudioObjectUnknown ? nil : boundDevice)
    }

    /// Always throws for `true`. AEC lives in Core Audio's VoiceProcessingIO
    /// unit and uses the system RENDER signal as its echo reference; an
    /// input-only unit has no output side to reference, so there is nothing
    /// to enable. `MicCapture` only selects this backend when the AEC
    /// decision is already false, so the throwing branch is a guard against
    /// a future caller, not a path anything takes today.
    func setVoiceProcessingEnabled(_ enabled: Bool) throws {
        guard enabled else { return }
        throw MicCaptureError.engineStartFailed(
            "voice processing is never available on the input-only HAL backend "
            + "(AEC needs VoiceProcessingIO and an output reference)")
    }

    func inputFormat() -> AVAudioFormat? {
        guard let unit else { return nil }
        return MicInputUnit.streamFormat(of: unit)
    }

    func setStreamFormat(rate: Double, channels: UInt32) -> OSStatus? {
        guard let unit else { return nil }
        return MicInputUnit.setStreamFormat(on: unit, rate: rate, channels: channels)
    }

    func installTap(
        bufferSize: AVAudioFrameCount,
        format: AVAudioFormat,
        handler: @escaping (AVAudioPCMBuffer) -> Void
    ) {
        guard let unit else { return }

        let capacity = Self.renderCapacity(
            bufferSize: bufferSize,
            maximumFramesPerSlice: MicInputUnit.maximumFramesPerSlice(of: unit))
        // TELL the unit, do not merely ask it. `MaximumFramesPerSlice` defaults
        // to 1156 and this runs BEFORE `AudioUnitInitialize`, which is the only
        // point at which the value can still be set — leaving it alone meant a
        // device with a 8192- or 16384-frame buffer answered every
        // `AudioUnitRender` with `kAudioUnitErr_TooManyFramesToProcess`, which
        // the callback cannot log, so the press failed as "no audio" with
        // nothing anywhere in the trace (S5). `start()` re-reads it after
        // `AudioUnitInitialize` and complains loudly if the unit refused.
        let sliceStatus = MicInputUnit.setMaximumFramesPerSlice(on: unit, capacity)
        if sliceStatus != noErr {
            MicCapture.trace("hal-input: MaximumFramesPerSlice=\(capacity) refused "
                             + "(status \(sliceStatus))")
        }

        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity),
              let channelData = buffer.floatChannelData else {
            MicCapture.trace("hal-input: could not pre-allocate a \(capacity)-frame render buffer")
            return
        }

        // Non-interleaved: one AudioBuffer per channel, each single-channel.
        // Interleaved would be one buffer carrying every channel. The format
        // we ask for is always non-interleaved, but the unit has the last
        // word on what it reports back, so both shapes are handled rather
        // than assumed.
        let interleaved = format.isInterleaved
        let channelCount = Int(format.channelCount)
        let bufferCount = interleaved ? 1 : channelCount
        let channelsPerBuffer = UInt32(interleaved ? channelCount : 1)
        let bytesPerFrame = channelsPerBuffer * 4
        let list = AudioBufferList.allocate(maximumBuffers: bufferCount)
        for i in 0..<bufferCount {
            list[i].mNumberChannels = channelsPerBuffer
            list[i].mDataByteSize = capacity * bytesPerFrame
            list[i].mData = UnsafeMutableRawPointer(channelData[i])
        }

        let ctx = HALRenderContext(
            buffer: buffer, bufferList: list,
            maxFrames: capacity, bytesPerFrame: bytesPerFrame)
        ctx.unit = unit
        ctx.handler = handler
        context = ctx

        var callback = AURenderCallbackStruct(
            inputProc: halInputRenderCallback,
            inputProcRefCon: Unmanaged.passUnretained(ctx).toOpaque())
        let status = AudioUnitSetProperty(
            unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0,
            &callback, UInt32(MemoryLayout<AURenderCallbackStruct>.size))
        if status != noErr {
            MicCapture.trace("hal-input: SetInputCallback failed (status \(status))")
        }
    }

    func removeTap() {
        // Only ever reached after `stop()`, which has already stopped and
        // disposed the unit, so there is no live IO thread to race here —
        // the same ordering rule `MicCapture.stop()` documents for the
        // engine (`isSink || tap != nullptr`).
        if let ctx = context {
            lastRenderStats = (ctx.renderFailures, ctx.lastRenderStatus, ctx.maxHandlerNanos)
        }
        context?.handler = nil
        context?.unit = nil
        context = nil
    }

    func start() throws {
        guard let unit else {
            throw MicCaptureError.engineStartFailed(
                "the input-only HAL unit was never created — nothing to start")
        }
        let initStatus = AudioUnitInitialize(unit)
        guard initStatus == noErr else {
            throw MicCaptureError.engineStartFailed(
                "AudioUnitInitialize failed for the input-only HAL unit "
                + "(status \(initStatus))")
        }
        // The other half of S5: initialization is when the unit finally
        // commits to a slice size. If it kept a smaller one than we
        // pre-allocated for, every oversized render would be dropped by the
        // callback's bounds guard with nothing to show for it, so say so now,
        // once, off the IO thread.
        if let ctx = context,
           let committed = MicInputUnit.maximumFramesPerSlice(of: unit),
           committed > ctx.maxFrames {
            let line = "[mic] hal-input: the unit kept MaximumFramesPerSlice=\(committed) "
                + "above the \(ctx.maxFrames)-frame render buffer — renders larger than "
                + "that will be DROPPED and this capture may deliver nothing\n"
            FileHandle.standardError.write(Data(line.utf8))
        }
        let startStatus = AudioOutputUnitStart(unit)
        guard startStatus == noErr else {
            AudioUnitUninitialize(unit)
            throw MicCaptureError.engineStartFailed(
                "AudioOutputUnitStart failed for the input-only HAL unit "
                + "(status \(startStatus))")
        }
        running = true
    }

    func stop() {
        disposeUnit()
        running = false
    }

    func renderStats() -> (failures: Int, lastStatus: OSStatus, maxHandlerNanos: UInt64) {
        guard let ctx = context else { return lastRenderStats }
        return (ctx.renderFailures, ctx.lastRenderStatus, ctx.maxHandlerNanos)
    }

    private func disposeUnit() {
        guard let unit else { return }
        AudioOutputUnitStop(unit)
        // Cleared between the stop and the dispose: after `AudioOutputUnitStop`
        // no further callback can begin, and this makes a callback that was
        // already inside the guard return instead of rendering through an
        // instance that is about to go away.
        context?.unit = nil
        AudioUnitUninitialize(unit)
        AudioComponentInstanceDispose(unit)
        self.unit = nil
        boundDevice = AudioDeviceID(kAudioObjectUnknown)
    }

    deinit { disposeUnit() }
}

/// The render callback. Allocates nothing, locks nothing, logs nothing — it
/// runs on the HAL's IO thread, where any of those three is a dropout in the
/// audio rather than a slow function.
///
/// `AudioUnitRender` is called on bus 1 (the input bus) into the
/// pre-allocated buffer list, whose `mData` already points at the
/// pre-allocated `AVAudioPCMBuffer`'s channels, so the "copy into it" step
/// is the render itself. The handler then receives exactly the kind of
/// `AVAudioPCMBuffer` an `AVAudioEngine` tap would have handed it, which is
/// what lets `FirstBufferLatch` and `handleInputBuffer` stay untouched.
///
/// Every failure path bumps `renderFailures` and records the status instead of
/// disappearing — see `MicInputBackend.renderFailures()` for why (S5).
private let halInputRenderCallback: AURenderCallback = {
    refCon, actionFlags, timeStamp, _, frameCount, _ in
    let ctx = Unmanaged<HALRenderContext>.fromOpaque(refCon).takeUnretainedValue()
    guard let unit = ctx.unit else { return noErr }
    guard frameCount > 0, frameCount <= ctx.maxFrames else {
        ctx.renderFailures += 1
        ctx.lastRenderStatus = kAudio_ParamError
        return kAudio_ParamError
    }
    let list = ctx.bufferList
    let bytes = frameCount * ctx.bytesPerFrame
    for i in 0..<list.count { list[i].mDataByteSize = bytes }
    let status = AudioUnitRender(
        unit, actionFlags, timeStamp, 1, frameCount, list.unsafeMutablePointer)
    guard status == noErr else {
        ctx.renderFailures += 1
        ctx.lastRenderStatus = status
        return status
    }
    ctx.buffer.frameLength = frameCount
    guard let handler = ctx.handler else { return noErr }
    // Two uptime reads (no syscall, ~20 ns each) around the handler, so the one
    // open risk in this design — that the tap handler's allocation, conversion
    // and three short locks do not fit the HAL's IO deadline — is a measured
    // number by the time anyone has to decide whether this backend stays
    // default-ON for other people. Nothing here branches on it.
    let began = DispatchTime.now().uptimeNanoseconds
    handler(ctx.buffer)
    let elapsed = DispatchTime.now().uptimeNanoseconds &- began
    if elapsed > ctx.maxHandlerNanos { ctx.maxHandlerNanos = elapsed }
    return noErr
}
