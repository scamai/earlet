import CryptoKit
import Foundation
import Security

// Where the history key lives. The product keeps it in the login keychain;
// tests and the window's render mode keep it in memory, so neither can reach
// the user's keychain.

/// The key the history is sealed with: one random 256-bit key, made on the
/// first save and kept only by this store. Losing it makes every sealed file
/// unreadable, which is what Delete All History relies on.
public protocol DictationHistoryKeys: Sendable {
    /// The key, or nil when none has been made yet.
    func existing() throws(DictationHistoryKeyError) -> SymmetricKey?
    /// Makes a new random key and keeps it. When a key already exists (a race
    /// with another process), that one is returned.
    func create() throws(DictationHistoryKeyError) -> SymmetricKey
    /// Destroys the key. Destroying one that does not exist succeeds.
    func destroy() throws(DictationHistoryKeyError)
}

public enum DictationHistoryKeyError: Error, Equatable, Sendable {
    /// The store refused or could not answer: the keychain is locked, the
    /// user denied access, or another Security error (its `OSStatus`).
    case unavailable(Int32)
    /// The store holds something that is not a 256-bit key.
    case malformed
}

/// The product's key store: a generic password in the login keychain, under
/// the service the app names (its defaults domain and `.history`) and the
/// account `history-key-v1`. Not synchronizable, so iCloud Keychain does not
/// copy it to another device. The item's access list is the app that made
/// it: a build signed with the same Developer ID reads it silently; an
/// ad-hoc build, whose code signature changes with every build, is asked
/// about by macOS first, and one the user denies sees the history as
/// locked, never as empty.
public struct KeychainHistoryKeys: DictationHistoryKeys {
    public static let account = "history-key-v1"

    private let service: String
    private let account: String
    /// Tests only: a keychain file of their own instead of the login
    /// keychain's search list. A keychain reference is never mutated here,
    /// and the Security calls that take it are thread-safe.
    nonisolated(unsafe) private let keychain: SecKeychain?

    public init(service: String, account: String = KeychainHistoryKeys.account) {
        self.init(service: service, account: account, keychain: nil)
    }

    init(service: String, account: String, keychain: SecKeychain?) {
        self.service = service
        self.account = account
        self.keychain = keychain
    }

    /// The item, in the test's keychain when there is one.
    private var identity: [CFString: Any] {
        var query: [CFString: Any] = [kSecClass: kSecClassGenericPassword, kSecAttrService: service,
                                      kSecAttrAccount: account]
        if let keychain { query[kSecMatchSearchList] = [keychain] }
        return query
    }

    public func existing() throws(DictationHistoryKeyError) -> SymmetricKey? {
        var query = identity
        query[kSecReturnData] = true
        query[kSecMatchLimit] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess:
            guard let data = item as? Data, data.count == 32 else { throw .malformed }
            return SymmetricKey(data: data)
        case errSecItemNotFound:
            return nil
        default:
            throw .unavailable(status)
        }
    }

    public func create() throws(DictationHistoryKeyError) -> SymmetricKey {
        let key = SymmetricKey(size: .bits256)
        var attributes = identity
        attributes[kSecMatchSearchList] = nil
        if let keychain { attributes[kSecUseKeychain] = keychain }
        attributes[kSecValueData] = key.withUnsafeBytes { Data($0) }
        attributes[kSecAttrLabel] = "Dictation history key"
        attributes[kSecAttrDescription] = "Encrypts the dictation history on this Mac"
        attributes[kSecAttrSynchronizable] = false
        let status = SecItemAdd(attributes as CFDictionary, nil)
        switch status {
        case errSecSuccess:
            return key
        case errSecDuplicateItem:
            if let other = try existing() { return other }
            throw .unavailable(status)
        default:
            throw .unavailable(status)
        }
    }

    public func destroy() throws(DictationHistoryKeyError) {
        let status = SecItemDelete(identity as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw .unavailable(status) }
    }
}

/// A key held in memory, for tests and the window's render mode. A test can
/// make it refuse, as a locked or denied keychain does.
public final class MemoryHistoryKeys: DictationHistoryKeys, @unchecked Sendable {
    private let lock = NSLock()
    private var key: SymmetricKey?
    private var refusal: DictationHistoryKeyError?
    private var made = 0
    private var delay: TimeInterval = 0
    private var asked = 0

    public init(key: SymmetricKey? = nil) {
        self.key = key
    }

    /// Every later call fails with `error` until it is cleared with nil.
    public func refuse(_ error: DictationHistoryKeyError?) {
        lock.lock(); refusal = error; lock.unlock()
    }

    /// Every later call first waits `seconds`, as a keychain that asks the
    /// user does: a test can then tell a wait from no wait.
    public func slowDown(_ seconds: TimeInterval) {
        lock.lock(); delay = seconds; lock.unlock()
    }

    private func pause() {
        lock.lock(); let seconds = delay; asked += 1; lock.unlock()
        if seconds > 0 { Thread.sleep(forTimeInterval: seconds) }
    }

    /// How many times the store has been asked anything.
    public var calls: Int { lock.lock(); defer { lock.unlock() }; return asked }

    /// How many keys `create()` has made.
    public var created: Int { lock.lock(); defer { lock.unlock() }; return made }

    public var current: SymmetricKey? { lock.lock(); defer { lock.unlock() }; return key }

    public func existing() throws(DictationHistoryKeyError) -> SymmetricKey? {
        pause()
        lock.lock(); defer { lock.unlock() }
        if let refusal { throw refusal }
        return key
    }

    public func create() throws(DictationHistoryKeyError) -> SymmetricKey {
        pause()
        lock.lock(); defer { lock.unlock() }
        if let refusal { throw refusal }
        if let key { return key }
        let made = SymmetricKey(size: .bits256)
        key = made
        self.made += 1
        return made
    }

    public func destroy() throws(DictationHistoryKeyError) {
        pause()
        lock.lock(); defer { lock.unlock() }
        if let refusal { throw refusal }
        key = nil
    }
}
