import Carbon.HIToolbox
import CoreGraphics
import DictationKit
import Foundation

// The live listeners behind `DictationHoldTrigger`, one per kind of hold
// key. The modifier-key listener, `HotkeyHoldTrigger`, sits with the rest of
// the live environment in DictationEnvironment.swift.

// ─── The gesture on a combination ───

/// Hold-to-talk and press-twice-to-latch on a key that reports plain down
/// and up edges: a recorded key combination.
///
/// The same two detectors `DictationHotkey` runs on a modifier key, fed the
/// same way, so the model sees one gesture whatever the key: the hold edge
/// first, then (on the second press of a pair) the latch edge.
public struct HoldGestureState: Sendable, Equatable {
    private var hold = HoldToTalkState()
    private var doubleTap: DoubleTapModifierState
    public private(set) var isDown = false

    public init(window: TimeInterval = DictationHoldKey.doubleTapWindow) {
        doubleTap = DoubleTapModifierState(window: window)
    }

    public mutating func handle(down: Bool, timestamp: TimeInterval) -> [DictationHotkeyEvent] {
        isDown = down
        return [hold.handle(down: down), doubleTap.handle(down: down, timestamp: timestamp)].compactMap { $0 }
    }
}

/// Main-actor callback fan-out for a listener that reports plain edges.
@MainActor
class GestureHoldTrigger {
    var onHoldStart: (() -> Void)?
    var onHoldEnd: (() -> Void)?
    /// Never fired: the key is not a modifier.
    var onModifierFlags: ((CGEventFlags) -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    var onLatchStart: (() -> Void)?
    var onLatchEnd: (() -> Void)?
    private var gesture = HoldGestureState()

    func edge(down: Bool, timestamp: TimeInterval) {
        for event in gesture.handle(down: down, timestamp: timestamp) {
            switch event {
            case .pushToTalkStart: onHoldStart?()
            case .pushToTalkEnd: onHoldEnd?()
            case .doubleTapActivate: onLatchStart?()
            case .doubleTapDeactivate: onLatchEnd?()
            }
        }
    }

    /// Ends a hold still down (the key changed or the listener stopped
    /// mid-press) and forgets any first tap.
    func resetGesture() {
        if gesture.isDown { edge(down: false, timestamp: ProcessInfo.processInfo.systemUptime) }
        gesture = HoldGestureState()
    }
}

// ─── Recorded key combinations ───

/// A recorded combination behind `DictationHoldTrigger`, registered with
/// macOS as a hot key. Needs no Input Monitoring: macOS tells the app about
/// this combination and nothing else.
@MainActor
final class ShortcutHoldTrigger: GestureHoldTrigger, DictationHoldTrigger {
    private(set) var shortcut: DictationKeyShortcut
    private var subscription: ShortcutHotKeyCenter.Subscription?
    private let center: ShortcutHotKeyCenter

    init(shortcut: DictationKeyShortcut, center: ShortcutHotKeyCenter = .shared) {
        self.shortcut = shortcut
        self.center = center
    }

    /// `false` when macOS would not register the combination: the system
    /// or another app may hold it.
    func start() -> Bool {
        guard subscription == nil else { return true }
        subscription = center.subscribe(shortcut) { [weak self] down in
            self?.edge(down: down, timestamp: ProcessInfo.processInfo.systemUptime)
        }
        return subscription != nil
    }

    func stop() {
        if let subscription { center.unsubscribe(subscription) }
        subscription = nil
        resetGesture()
    }

    func update(_ key: DictationHoldKey) {
        guard case .shortcut(let newShortcut) = key, newShortcut != shortcut else { return }
        let wasRunning = subscription != nil
        stop()
        shortcut = newShortcut
        if wasRunning { _ = start() }
    }
}

/// The app's hot-key registrations. One registration per combination,
/// shared by every listener that wants it (the live trigger and a key test
/// at once), and released when the last one stops.
@MainActor
final class ShortcutHotKeyCenter {
    static let shared = ShortcutHotKeyCenter()

    struct Subscription: Hashable {
        fileprivate let id: Int
        fileprivate let shortcut: DictationKeyShortcut
    }

    private struct Registration {
        var reference: EventHotKeyRef
        var id: UInt32
        var listeners: [Int: (Bool) -> Void]
    }

    /// "Erlt", the signature on every hot key this app registers.
    private static let signature: OSType = 0x4572_6C74
    /// macOS takes one handler per process for these events (a second
    /// identical one is refused as already installed), so the handler is
    /// the process's and routes each event by its hot-key id, which is
    /// unique in the process, to the center that registered it.
    private static var handler: EventHandlerRef?
    private static var routes: [UInt32: (Bool) -> Void] = [:]
    private static var nextHotKeyID: UInt32 = 0
    private var registrations: [DictationKeyShortcut: Registration] = [:]
    private var nextListener = 0

    /// How many combinations this center has registered with macOS now.
    var registeredCount: Int { registrations.count }

    /// The id macOS reports the combination's presses with, while
    /// registered.
    func hotKeyID(for shortcut: DictationKeyShortcut) -> UInt32? { registrations[shortcut]?.id }

    /// Nil when macOS refused the combination.
    func subscribe(_ shortcut: DictationKeyShortcut, onEdge: @escaping (Bool) -> Void) -> Subscription? {
        guard Self.installHandler() else { return nil }
        if registrations[shortcut] == nil {
            Self.nextHotKeyID += 1
            let id = Self.nextHotKeyID
            guard let reference = Self.register(shortcut, id: id) else { return nil }
            registrations[shortcut] = Registration(reference: reference, id: id, listeners: [:])
            Self.routes[id] = { [weak self] down in self?.deliver(id: id, down: down) }
        }
        nextListener += 1
        registrations[shortcut]?.listeners[nextListener] = onEdge
        return Subscription(id: nextListener, shortcut: shortcut)
    }

    func unsubscribe(_ subscription: Subscription) {
        guard var registration = registrations[subscription.shortcut] else { return }
        registration.listeners[subscription.id] = nil
        if registration.listeners.isEmpty {
            UnregisterEventHotKey(registration.reference)
            Self.routes[registration.id] = nil
            registrations[subscription.shortcut] = nil
        } else {
            registrations[subscription.shortcut] = registration
        }
    }

    /// Whether macOS would register the combination now: already held by
    /// this center, or registered and released again at once. Asked when
    /// the user records it, so a refused combination is refused there.
    func canRegister(_ shortcut: DictationKeyShortcut) -> Bool {
        if registrations[shortcut] != nil { return true }
        guard let reference = Self.register(shortcut, id: 0) else { return false }
        UnregisterEventHotKey(reference)
        return true
    }

    private static func register(_ shortcut: DictationKeyShortcut, id: UInt32) -> EventHotKeyRef? {
        var reference: EventHotKeyRef?
        let status = RegisterEventHotKey(UInt32(shortcut.keyCode), shortcut.modifiers.rawValue,
                                         EventHotKeyID(signature: signature, id: id),
                                         GetEventDispatcherTarget(), 0, &reference)
        return status == noErr ? reference : nil
    }

    private func deliver(id: UInt32, down: Bool) {
        guard let registration = registrations.values.first(where: { $0.id == id }) else { return }
        for listener in registration.listeners.values { listener(down) }
    }

    /// The handler for the app's hot-key presses and releases, on the event
    /// dispatcher so it also runs while a menu is open.
    private static func installHandler() -> Bool {
        guard handler == nil else { return true }
        let kinds = [
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased)),
        ]
        let status = InstallEventHandler(GetEventDispatcherTarget(), { _, event, _ -> OSStatus in
            guard let event else { return OSStatus(eventNotHandledErr) }
            var hotKey = EventHotKeyID()
            let read = GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                         EventParamType(typeEventHotKeyID), nil,
                                         MemoryLayout<EventHotKeyID>.size, nil, &hotKey)
            guard read == noErr, hotKey.signature == ShortcutHotKeyCenter.signature else {
                return OSStatus(eventNotHandledErr)
            }
            let id = hotKey.id
            let down = GetEventKind(event) == UInt32(kEventHotKeyPressed)
            // Carbon dispatches hot-key events on the main thread.
            MainActor.assumeIsolated { ShortcutHotKeyCenter.routes[id]?(down) }
            return noErr
        }, kinds.count, kinds, nil, &handler)
        return status == noErr
    }
}

// ─── Whichever the user chose ───

/// The live hold listener: the modifier-key tap or a registered combination,
/// swapped for the other kind when the user's choice changes kind, so the
/// model keeps one trigger and one set of callbacks.
@MainActor
final class LiveHoldTrigger: DictationHoldTrigger {
    var onHoldStart: (() -> Void)?
    var onHoldEnd: (() -> Void)?
    var onModifierFlags: ((CGEventFlags) -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    var onLatchStart: (() -> Void)?
    var onLatchEnd: (() -> Void)?
    private(set) var backend: any DictationHoldTrigger
    private var key: DictationHoldKey
    private var running = false

    init(key: DictationHoldKey) {
        self.key = key
        backend = Self.backend(for: key)
        wire()
    }

    static func backend(for key: DictationHoldKey) -> any DictationHoldTrigger {
        switch key {
        case .modifier(let modifier): return HotkeyHoldTrigger(key: modifier)
        case .shortcut(let shortcut): return ShortcutHoldTrigger(shortcut: shortcut)
        }
    }

    func start() -> Bool {
        running = backend.start()
        return running
    }

    func stop() {
        running = false
        backend.stop()
    }

    func update(_ newKey: DictationHoldKey) {
        defer { key = newKey }
        guard !Self.sameKind(key, newKey) else {
            backend.update(newKey)
            return
        }
        backend.stop()
        backend = Self.backend(for: newKey)
        wire()
        if running { running = backend.start() }
    }

    private static func sameKind(_ a: DictationHoldKey, _ b: DictationHoldKey) -> Bool {
        switch (a, b) {
        case (.modifier, .modifier), (.shortcut, .shortcut): return true
        default: return false
        }
    }

    private func wire() {
        backend.onHoldStart = { [weak self] in self?.onHoldStart?() }
        backend.onHoldEnd = { [weak self] in self?.onHoldEnd?() }
        backend.onModifierFlags = { [weak self] flags in self?.onModifierFlags?(flags) }
        backend.onDegraded = { [weak self] status in self?.onDegraded?(status) }
        backend.onLatchStart = { [weak self] in self?.onLatchStart?() }
        backend.onLatchEnd = { [weak self] in self?.onLatchEnd?() }
    }
}
