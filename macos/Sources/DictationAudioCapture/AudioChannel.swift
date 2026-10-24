import Foundation

/// Audio origin carried by the worker protocol. Dictation captures `.mic`;
/// `.system` remains readable for protocol compatibility.
public enum AudioChannel: String, Codable, Sendable, CaseIterable {
    case mic
    case system
}
