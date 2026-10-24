import AppKit

/// Marks a pasteboard item as one that clipboard history tools should not
/// record. The identifier is the published nspasteboard.org convention,
/// which clipboard managers and password managers already honour, so it
/// has to be spelled exactly this way to be recognised.
public let concealedPasteboardType = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")

/// Registers dictated text for DELAYED rendering: nothing is written up
/// front, and the string is produced only when the target app's paste
/// handler asks for it through `provideDataForType`. That request is also
/// the evidence that the paste landed, which is what makes `wasAsked` the
/// failed-paste signal `PasteTimeoutClassifier` consumes; nothing has to be
/// inferred from the target field afterwards.
///
/// `@unchecked Sendable` + `NSLock`, matching the house style in
/// microphone capture, rather than
/// fighting Swift 6 strict concurrency over an `NSObject` subclass that
/// AppKit calls back into from an arbitrary thread.
final class DelayedClipboardProvider: NSObject, NSPasteboardItemDataProvider, @unchecked Sendable {
    private let text: String
    private let lock = NSLock()
    private var _wasAsked = false
    private var _otherTypeAsks = 0
    private var _handOverFailures = 0

    /// TRUE ONLY WHEN THE TEXT WAS ACTUALLY HANDED OVER.
    ///
    /// This flag is the failed-paste signal and, once the restore is
    /// scheduled off the critical path, the thing that decides how long the
    /// clipboard is protected. It used to be set on entry, before the type
    /// check and before `setString` — so a clipboard manager asking for
    /// `ConcealedType` (which this item advertises precisely so managers
    /// notice it) flipped it true while we handed over nothing, and the
    /// paste was reported as landed. Under a 500 ms restore delay the
    /// mistake was invisible; anything shorter turns it into the target
    /// reading a clipboard we already gave back.
    var wasAsked: Bool {
        lock.lock()
        defer { lock.unlock() }
        return _wasAsked
    }

    /// Asks for a type OTHER than `.string` — a clipboard/password manager
    /// sniffing `ConcealedType`, not the paste we are waiting for. Counted
    /// rather than dropped because it is the one observation that separates
    /// "nobody wanted our data" from "something read the item but not the
    /// text", and the two have different fixes.
    var otherTypeAskCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return _otherTypeAsks
    }

    /// `setString` refusing the item — the item was invalidated (the
    /// pasteboard was cleared out from under us) before the target got
    /// there. Nothing was handed over, so `wasAsked` must stay false.
    var handOverFailureCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return _handOverFailures
    }

    init(text: String) {
        self.text = text
    }

    func pasteboard(
        _ pasteboard: NSPasteboard?,
        item: NSPasteboardItem,
        provideDataForType type: NSPasteboard.PasteboardType
    ) {
        guard type == .string else {
            lock.lock()
            _otherTypeAsks += 1
            lock.unlock()
            return
        }
        let handedOver = item.setString(text, forType: .string)
        lock.lock()
        if handedOver {
            _wasAsked = true
        } else {
            _handOverFailures += 1
        }
        lock.unlock()
    }
}

/// A byte-level snapshot of every item currently on a pasteboard, keyed by
/// type — used to prove "clipboard byte-identical after every dictation"
/// (an exit metric for the paste path). `NSPasteboard.changeCount`
/// alone only proves "somebody wrote something"; comparing actual `Data`
/// per type is what proves the RESTORED content is byte-identical to what
/// was there before, not merely present.
///
/// `Sendable` because the restore now runs on a detached task — the
/// snapshot is captured on the main actor and read back on it, but it
/// crosses the boundary in between. It is a value type over `Data`, so
/// nothing is shared.
struct PasteboardSnapshot: Equatable, Sendable {
    var items: [[NSPasteboard.PasteboardType: Data]]

    static func capture(_ pasteboard: NSPasteboard) -> PasteboardSnapshot {
        let items = (pasteboard.pasteboardItems ?? []).map { item -> [NSPasteboard.PasteboardType: Data] in
            var dict: [NSPasteboard.PasteboardType: Data] = [:]
            for type in item.types {
                if let data = item.data(forType: type) { dict[type] = data }
            }
            return dict
        }
        return PasteboardSnapshot(items: items)
    }

    /// Writes this snapshot back verbatim (eager write — restoring the
    /// user's PRIOR clipboard is not itself a dictation payload, so it does
    /// not need delayed rendering).
    func restore(to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        let newItems = items.map { dict -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in dict { item.setData(data, forType: type) }
            return item
        }
        pasteboard.writeObjects(newItems)
    }
}
