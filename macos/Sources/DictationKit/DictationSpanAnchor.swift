import Foundation

/// Where the text Dictation just pasted sits in the field (LEARNING.md §7:
/// the observer watches "only the span it pasted").
///
/// ## Establishing it
///
/// Right after a paste the selection is a zero-length caret at the end of
/// the pasted text, so the span is `[caret − pasted.utf16.count, caret]` in
/// the UTF-16 offsets Accessibility reports. A caret-only approach read the caret as if
/// it were the span, which gave an empty string: the first notification then
/// ended the observation as `field_cleared` and nothing was ever learned.
///
/// The derived span is accepted only when the field holds exactly the pasted
/// text there, compared unit for unit. Anything else (an app that reformats
/// on paste or a value that has not caught up with the paste yet) is
/// unobservable. When a before-paste snapshot is available, an exact unique
/// insertion/replacement proves the span without relying on the caret.
///
/// ## Following it
///
/// The anchor keeps the text before the span and the text after it, and a
/// later read takes the span to be whatever lies between them. An edit inside
/// the span is followed whatever it does to the length; an edit anywhere
/// outside it breaks the anchor, and a broken anchor reports no span at all.
/// Plain offsets would instead read a shifted substring after
/// an edit earlier in the field and could hand that to learning as if the
/// user had corrected the dictation.
///
/// The prefix and suffix are the field's own text, held in memory for the
/// length of one observation and never reported: `span(in:)` returns the
/// middle only.
public struct DictationSpanAnchor: Sendable, Equatable {
    private let prefix: [UInt16]
    private let suffix: [UInt16]

    /// The anchor for `pasted`, given the field's value and its selection
    /// right after the paste, or `nil` when the field does not hold exactly
    /// `pasted` where the selection says it should.
    ///
    /// A zero-length selection is the caret after the paste. A selection as
    /// long as the pasted text is accepted too, for a field that leaves what
    /// it pasted selected. Any other selection is not a paste this anchor can
    /// explain.
    public static func establish(
        fieldValue: String, selectionLocation: Int, selectionLength: Int, pasted: String
    ) -> DictationSpanAnchor? {
        let field = Array(fieldValue.utf16)
        let pastedUnits = Array(pasted.utf16)
        guard !pastedUnits.isEmpty, selectionLocation >= 0, selectionLocation <= field.count else { return nil }
        let start: Int
        switch selectionLength {
        case 0: start = selectionLocation - pastedUnits.count
        case pastedUnits.count: start = selectionLocation
        default: return nil
        }
        let end = start + pastedUnits.count
        guard start >= 0, end <= field.count, field[start..<end].elementsEqual(pastedUnits) else {
            return nil
        }
        return DictationSpanAnchor(prefix: Array(field[..<start]), suffix: Array(field[end...]))
    }

    /// Proves the paste from one field's before/after values when its caret
    /// is unavailable or unreliable. No old occurrence alone proves a paste.
    public static func establish(
        beforePaste: DictationAnchorSearch.FieldRead, fieldValue: String, pasted: String
    ) -> DictationSpanAnchor? {
        guard let beforeValue = beforePaste.value else { return nil }
        let before = Array(beforeValue.utf16), field = Array(fieldValue.utf16), units = Array(pasted.utf16)
        guard !units.isEmpty, !before.elementsEqual(field) else { return nil }

        // A nonempty selection can explain a replacement, but only if every
        // unit outside it is unchanged. Validate offsets without overflowing.
        if let start = beforePaste.selectionLocation, let length = beforePaste.selectionLength,
           start >= 0, start <= before.count, length > 0, length <= before.count - start,
           field.count >= units.count, field.count - units.count == before.count - length,
           field[..<start].elementsEqual(before[..<start]),
           field[start..<(start + units.count)].elementsEqual(units),
           field[(start + units.count)...].elementsEqual(before[(start + length)...]) {
            return DictationSpanAnchor(prefix: Array(before[..<start]), suffix: Array(before[(start + length)...]))
        }

        guard field.count >= units.count, field.count - units.count == before.count else { return nil }
        var prefix = 0, suffix = 0
        while prefix < before.count && before[prefix] == field[prefix] { prefix += 1 }
        while suffix < before.count && before[before.count - 1 - suffix] == field[field.count - 1 - suffix] {
            suffix += 1
        }
        let earliest = before.count - suffix
        guard earliest <= prefix else { return nil }
        // All possible insertion positions preserve the complete old value.
        // Repeated/overlapping text may leave more than one: refuse ambiguity.
        var found: Int?
        for start in earliest...prefix where field[start..<(start + units.count)].elementsEqual(units) {
            guard found == nil else { return nil }
            found = start
        }
        guard let start = found else { return nil }
        return DictationSpanAnchor(prefix: Array(field[..<start]), suffix: Array(field[(start + units.count)...]))
    }

    /// The span's current text in `fieldValue`, or `nil` when text outside
    /// the span has changed and the span can no longer be told apart from
    /// the rest of the field.
    public func span(in fieldValue: String) -> String? {
        let field = Array(fieldValue.utf16)
        guard field.count >= prefix.count + suffix.count,
              field.starts(with: prefix),
              field[(field.count - suffix.count)...].elementsEqual(suffix)
        else { return nil }
        return String(decoding: field[prefix.count..<(field.count - suffix.count)], as: UTF16.self)
    }
}

/// Whether a paste target may be observed at all, decided before anything is
/// read from it. Pure, so every refusal is testable without Accessibility.
public enum DictationObservationAdmission {

    /// `nil` to observe, or the condition the observation ends with at once.
    ///
    /// Secure Input and a password field are checked first and refuse
    /// whatever else is true: a password is never read, not even to find out
    /// which app it belongs to. Then the field must take text, and it must
    /// belong to `expectedBundleID`, the app the paste went to: focus that
    /// moved to another app between the paste and this check would otherwise
    /// have that app's field watched in place of the one Dictation typed into.
    public static func refusal(
        secureInput: Bool, focus: FocusCheckResult?, bundleID: String?, expectedBundleID: String
    ) -> DictationTerminalCondition? {
        if secureInput { return .secureField }
        switch focus {
        case .secureField?: return .secureField
        case .editable?: break
        case .notEditable?, .noFocusedElement?, .permissionMissing?, nil: return .noFocusedField
        }
        guard let bundleID, bundleID == expectedBundleID else { return .otherApplication }
        return nil
    }
}

/// Finds the pasted span right after a paste, retrying briefly: the paste is
/// confirmed when the target app asks for the pasteboard data, which can be a
/// moment before its field's value includes the text.
///
/// The field reads are injected, so the retries and the give-up are testable
/// without Accessibility; the observer passes its AX reads.
public enum DictationAnchorSearch {

    public enum Outcome: Sendable, Equatable {
        /// The anchor, and the field's value when it was found.
        case anchored(DictationSpanAnchor, fieldValue: String)
        /// Every attempt read something other than the pasted text where the
        /// selection put it, or could not read the field: unobservable.
        case notFound
        /// `isCurrent` turned false (the caller abandoned or began another
        /// observation) before the anchor was found.
        case superseded
    }

    /// Partial reads stay distinguishable: an app may expose text but no
    /// selection, or a selection but no readable value.
    public struct FieldRead: Sendable, Equatable {
        public var value: String?
        public var selectionLocation: Int?
        public var selectionLength: Int?

        public init(value: String?, selectionLocation: Int?, selectionLength: Int?) {
            self.value = value
            self.selectionLocation = selectionLocation
            self.selectionLength = selectionLength
        }
    }

    /// Bounded metadata from the last read. Neither the dictated text nor
    /// any field content can enter the diagnostic sink through this type.
    public struct Diagnostic: Sendable, Equatable {
        public enum Result: String, Sendable {
            case anchored, superseded
            case anchoredFromChange = "anchored_from_change"
            case changeMismatch = "change_mismatch"
            case valueUnavailable = "value_unavailable"
            case selectionUnavailable = "selection_unavailable"
            case selectionMismatch = "selection_mismatch"
            case textNotFound = "text_not_found"
        }
        public let result: Result
        public let attempts: Int
        public let beforeValueLength: Int?
        public let valueLength: Int?
        public let pastedLength: Int
        public let selectionLocation: Int?
        public let selectionLength: Int?

        public var logSummary: String {
            "anchor=\(result.rawValue) attempts=\(attempts) value_utf16=\(valueLength.map(String.init) ?? "n/a")"
                + " before_utf16=\(beforeValueLength.map(String.init) ?? "n/a")"
                + " pasted_utf16=\(pastedLength) selection_location=\(selectionLocation.map(String.init) ?? "n/a")"
                + " selection_length=\(selectionLength.map(String.init) ?? "n/a")"
        }
    }

    /// Reads up to `attempts` times, `retrySeconds` apart, on `queue`, and
    /// calls `completion` exactly once, on `queue`. `isCurrent` is asked
    /// before every read.
    public static func run(
        pasted: String, beforePaste: FieldRead? = nil,
        attempts: Int, retrySeconds: TimeInterval, queue: DispatchQueue,
        read: @escaping @Sendable () -> FieldRead?,
        isCurrent: @escaping @Sendable () -> Bool,
        diagnostic: @escaping @Sendable (Diagnostic) -> Void = { _ in },
        completion: @escaping @Sendable (Outcome) -> Void
    ) {
        queue.async {
            attempt(1, pasted: pasted, beforePaste: beforePaste,
                    attempts: max(1, attempts), retrySeconds: retrySeconds, queue: queue,
                    read: read, isCurrent: isCurrent, diagnostic: diagnostic, completion: completion)
        }
    }

    private static func attempt(
        _ number: Int, pasted: String, beforePaste: FieldRead?,
        attempts: Int, retrySeconds: TimeInterval, queue: DispatchQueue,
        read: @escaping @Sendable () -> FieldRead?,
        isCurrent: @escaping @Sendable () -> Bool,
        diagnostic: @escaping @Sendable (Diagnostic) -> Void,
        completion: @escaping @Sendable (Outcome) -> Void
    ) {
        func report(_ result: Diagnostic.Result, field: FieldRead?, attempts: Int) {
            diagnostic(Diagnostic(result: result, attempts: attempts, beforeValueLength: beforePaste?.value?.utf16.count,
                                  valueLength: field?.value?.utf16.count,
                                  pastedLength: pasted.utf16.count, selectionLocation: field?.selectionLocation,
                                  selectionLength: field?.selectionLength))
        }
        guard isCurrent() else {
            report(.superseded, field: nil, attempts: number - 1)
            completion(.superseded)
            return
        }
        let field = read()
        // Abandonment can happen during a slow AX round trip too.
        guard isCurrent() else {
            report(.superseded, field: nil, attempts: number)
            completion(.superseded)
            return
        }
        if let beforePaste, let value = field?.value, let anchor = DictationSpanAnchor.establish(
            beforePaste: beforePaste, fieldValue: value, pasted: pasted) {
            report(.anchoredFromChange, field: field, attempts: number)
            completion(.anchored(anchor, fieldValue: value))
            return
        }
        if beforePaste == nil, let value = field?.value, let location = field?.selectionLocation,
           let length = field?.selectionLength, let anchor = DictationSpanAnchor.establish(
            fieldValue: value, selectionLocation: location, selectionLength: length, pasted: pasted) {
            report(.anchored, field: field, attempts: number)
            completion(.anchored(anchor, fieldValue: value))
            return
        }
        guard number < attempts else {
            let reason: Diagnostic.Result
            if field?.value == nil { reason = .valueUnavailable }
            else if beforePaste != nil { reason = .changeMismatch }
            else if field?.selectionLocation == nil || field?.selectionLength == nil { reason = .selectionUnavailable }
            else if field?.value?.contains(pasted) == true { reason = .selectionMismatch }
            else { reason = .textNotFound }
            report(reason, field: field, attempts: number)
            completion(.notFound)
            return
        }
        queue.asyncAfter(deadline: .now() + retrySeconds) {
            attempt(number + 1, pasted: pasted, beforePaste: beforePaste,
                    attempts: attempts, retrySeconds: retrySeconds, queue: queue,
                    read: read, isCurrent: isCurrent, diagnostic: diagnostic, completion: completion)
        }
    }
}
