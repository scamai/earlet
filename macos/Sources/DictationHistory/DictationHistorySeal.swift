import CryptoKit
import Foundation

// The sealed file format: every history file is authenticated encryption of
// its plaintext under the history key, bound to what the file is and to the
// dictation it belongs to. The key lives in the login keychain
// (DictationHistoryKeys.swift); nothing here touches the disk.

/// One sealed history file:
///
/// | offset | bytes | field |
/// |---|---|---|
/// | 0 | 8 | `DICTHIST` |
/// | 8 | 1 | format, 1 |
/// | 9 | 1 | kind: 1 a dictation, 2 its recording |
/// | 10 | 16 | the key's id (`keyID(of:)`) |
/// | 26 | … | AES-256-GCM: a 12-byte random nonce, the ciphertext, a 16-byte tag |
///
/// The additional authenticated data is the 26-byte header followed by the
/// dictation's id as text, so a file cannot be passed off as another kind,
/// another dictation's, or sealed under another key without the tag failing.
/// A fresh random nonce per seal; at the few thousand seals a year one
/// person makes, a 96-bit random nonce is far inside AES-GCM's bound.
public enum DictationHistorySeal {
    public enum Kind: UInt8, Sendable {
        case entry = 1
        case audio = 2
    }

    public enum OpenFailure: Error, Equatable, Sendable {
        /// Not a sealed history file of this format.
        case notSealed
        /// Sealed under another key: one this keychain no longer holds.
        case otherKey
        /// The kind or the dictation the file names is not the one asked for.
        case mismatched
        /// The tag does not verify: the file was changed or damaged.
        case damaged
    }

    static let magic = Data("DICTHIST".utf8)
    static let format: UInt8 = 1
    public static let headerBytes = 26

    /// The key's id: the first 16 bytes of SHA-256 over a fixed label and the
    /// key. It names the key without revealing it, so a file sealed under a
    /// key the keychain no longer holds is told apart from a damaged one.
    public static func keyID(of key: SymmetricKey) -> Data {
        var hash = SHA256()
        hash.update(data: Data("dictation.history.key-id.v1".utf8))
        key.withUnsafeBytes { hash.update(bufferPointer: $0) }
        return Data(hash.finalize().prefix(16))
    }

    public static func seal(_ plaintext: Data, kind: Kind, id: UUID, key: SymmetricKey) throws -> Data {
        let header = header(kind: kind, keyID: keyID(of: key))
        let box = try AES.GCM.seal(plaintext, using: key, authenticating: header + Data(id.uuidString.utf8))
        guard let combined = box.combined else { throw OpenFailure.notSealed }
        return header + combined
    }

    public static func open(_ sealed: Data, kind: Kind, id: UUID, key: SymmetricKey) throws -> Data {
        guard sealed.count > headerBytes + 12 + 16 else { throw OpenFailure.notSealed }
        let bytes = Data(sealed) // a slice's indices start at 0 from here
        let header = bytes.prefix(headerBytes)
        guard header.prefix(8) == magic, header[8] == format else { throw OpenFailure.notSealed }
        guard header[9] == kind.rawValue else { throw OpenFailure.mismatched }
        guard header.subdata(in: 10..<26) == keyID(of: key) else { throw OpenFailure.otherKey }
        do {
            let box = try AES.GCM.SealedBox(combined: bytes.dropFirst(headerBytes))
            return try AES.GCM.open(box, using: key, authenticating: Data(header) + Data(id.uuidString.utf8))
        } catch {
            throw OpenFailure.damaged
        }
    }

    /// The key id a sealed file names, or nil when it is not one.
    public static func keyID(ofSealed sealed: Data) -> Data? {
        let bytes = Data(sealed.prefix(headerBytes))
        guard bytes.count == headerBytes, bytes.prefix(8) == magic, bytes[8] == format else { return nil }
        return bytes.subdata(in: 10..<26)
    }

    static func header(kind: Kind, keyID: Data) -> Data {
        var header = magic
        header.append(format)
        header.append(kind.rawValue)
        header.append(keyID)
        return header
    }
}
