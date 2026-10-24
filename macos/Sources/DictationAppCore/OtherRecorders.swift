import CoreAudio
import Darwin
import Foundation

public struct DictationOtherRecorderSnapshot: Equatable, Sendable {
    public let available: Bool
    public let pids: Set<pid_t>
    public init(available: Bool, pids: Set<pid_t> = []) {
        self.available = available
        self.pids = pids
    }
}

/// The small read-only seam makes the policy testable without invoking Core
/// Audio. Implementations return process objects, their PIDs, and input state;
/// they never start a device or request microphone access.
public protocol DictationOtherRecorderPropertyReader: Sendable {
    func processObjects() throws -> [AudioObjectID]
    func pid(of process: AudioObjectID) throws -> pid_t?
    func isRunningInput(of process: AudioObjectID) throws -> Bool
}

@available(macOS 14.2, *)
public final class DictationOtherRecorders: @unchecked Sendable {
    private let reader: any DictationOtherRecorderPropertyReader

    public init() {
        self.reader = SystemReader()
    }

    public init(reader: any DictationOtherRecorderPropertyReader) {
        self.reader = reader
    }

    /// Returns a distinct empty result when Core Audio is available but no
    /// other process is recording. Any unsupported/permission/property error
    /// is unavailable, never an empty success.
    public func snapshot(excluding ownPID: pid_t) -> DictationOtherRecorderSnapshot {
        do {
            var pids = Set<pid_t>()
            for process in try reader.processObjects() {
                guard let pid = try reader.pid(of: process), pid != ownPID else { continue }
                if try reader.isRunningInput(of: process) {
                    pids.insert(pid)
                }
            }
            return DictationOtherRecorderSnapshot(available: true, pids: pids)
        } catch {
            return DictationOtherRecorderSnapshot(available: false)
        }
    }

    private struct SystemReader: DictationOtherRecorderPropertyReader {
        func processObjects() throws -> [AudioObjectID] {
            var address = AudioObjectPropertyAddress(
                mSelector: kAudioHardwarePropertyProcessObjectList,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            var size: UInt32 = 0
            try status(AudioObjectGetPropertyDataSize(
                AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size))
            guard size > 0 else { return [] }
            var ids = [AudioObjectID](repeating: AudioObjectID(kAudioObjectUnknown),
                                      count: Int(size) / MemoryLayout<AudioObjectID>.stride)
            try status(AudioObjectGetPropertyData(
                AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids))
            return ids
        }

        func pid(of process: AudioObjectID) throws -> pid_t? {
            var address = AudioObjectPropertyAddress(
                mSelector: kAudioProcessPropertyPID,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            var pid: pid_t = 0
            var size = UInt32(MemoryLayout<pid_t>.size)
            try status(AudioObjectGetPropertyData(process, &address, 0, nil, &size, &pid))
            return pid > 0 ? pid : nil
        }

        func isRunningInput(of process: AudioObjectID) throws -> Bool {
            var address = AudioObjectPropertyAddress(
                mSelector: kAudioProcessPropertyIsRunningInput,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            var running: UInt32 = 0
            var size = UInt32(MemoryLayout<UInt32>.size)
            try status(AudioObjectGetPropertyData(process, &address, 0, nil, &size, &running))
            return running != 0
        }

        private func status(_ value: OSStatus) throws {
            guard value == noErr else { throw CocoaError(.coderReadCorrupt) }
        }
    }
}
