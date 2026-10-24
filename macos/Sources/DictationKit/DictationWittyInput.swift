import Foundation

/// A narrow adapter for the Codex composer observed in Witty. Witty exposes
/// the entire terminal screen as AXValue and its mouse selection as the caret.
/// Neither is an editable document. Never fall back to that screen as a field.
///
/// The supported frame has a final `› ` composer, an empty separator row and
/// a recognized Codex footer. This is a display contract, not terminal-wide
/// support. Changed layouts, clipped drafts and collapsed pastes fail closed.
/// Codex's composer renders its own word-wrapped rows with a two-column gutter:
/// https://github.com/openai/codex/blob/main/codex-rs/tui/src/bottom_pane/chat_composer.rs
/// Only whitespace is canonicalized; an exact before/after paste proof still
/// has to succeed. Boundary words must remain stable during observation: AX
/// cannot distinguish a word split by rendering from a user inserting a space.
struct DictationWittyInput: Sendable, Equatable {
    static let bundleID = "ai.scam.witty"

    /// Public release identities only; a similarly named app is not this adapter.
    static func supports(bundleID: String) -> Bool {
        bundleID == Self.bundleID || bundleID == "dev.shenxingy.witty"
    }
    static let pollSeconds: TimeInterval = 0.6 // Witty caches AXValue for 500 ms.

    let value: String
    let boundaryWords: [String]

    /// Shared boundary spaces make insertion independent of display padding
    /// and line wrapping. Only the inserted token sequence becomes the span.
    static func pasted(_ text: String) -> String {
        let words = text.split(whereSeparator: \.isWhitespace)
        return words.isEmpty ? "" : words.joined(separator: " ") + " "
    }

    static func parse(_ screen: String) -> Self? {
        guard screen.utf16.count <= 1_000_000 else { return nil }
        var lines = screen.components(separatedBy: "\n")
        while lines.last?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == true {
            lines.removeLast()
        }
        guard lines.count >= 3 else { return nil }
        if lines.count >= 4, isTwoRowFooter(status: lines[lines.count - 2], navigation: lines[lines.count - 1]) {
            lines.removeLast(2)
        } else {
            guard let footer = lines.last, isFooter(footer) else { return nil }
            lines.removeLast()
        }
        guard lines.last?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == true else { return nil }
        // Composer/footer padding is not part of the editable text.
        while lines.last?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == true {
            lines.removeLast()
        }
        guard let start = lines.lastIndex(where: { $0.hasPrefix("› ") || $0 == "›" }),
              lines.count - start <= 32,
              start == 0 || lines[start - 1].trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        var rows = [String(lines[start].dropFirst(2))]
        for line in lines.dropFirst(start + 1) {
            if line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { rows.append(""); continue }
            guard line.hasPrefix("  ") else { return nil }
            rows.append(String(line.dropFirst(2)))
        }
        let text = rows.joined(separator: "\n")
        guard text.utf16.count <= 8_192,
              !text.contains("[Pasted Content"), !text.contains("[Image #"),
              !containsFooterPair(rows),
              !rows.contains(where: {
                  let row = $0.trimmingCharacters(in: .whitespacesAndNewlines)
                  return row.hasSuffix("↑") || row.hasSuffix("↓")
              }) else { return nil }
        let words = rows.map { $0.split(whereSeparator: \.isWhitespace).map(String.init) }
        let flattened = words.flatMap { $0 }.joined(separator: " ")
        if flattened == "Ask Codex to do anything" { return Self(value: " ", boundaryWords: []) }
        var boundaries: [String] = []
        for index in 1..<words.count {
            if let previous = words[index - 1].last, let next = words[index].first {
                boundaries.append(previous + "\n" + next)
            }
        }
        return Self(value: flattened.isEmpty ? " " : " " + flattened + " ", boundaryWords: boundaries)
    }

    private static func isFooter(_ line: String) -> Bool {
        guard line.hasPrefix("  ") else { return false }
        let footer = line.trimmingCharacters(in: .whitespacesAndNewlines)
        if (footer.hasPrefix("Main [") && footer.contains("← for agents"))
            || footer.hasPrefix("tab to queue message")
            || footer.hasPrefix("enter to send")
            || footer.hasPrefix("? for shortcuts") { return true }
        // An idle Codex with a draft hides its left-hand shortcuts/mode hint.
        // Only the right-hand context percentage (and optional warnings) stays.
        // Reproduced with the installed 0.160.1 TUI; a busy-only fixture missed
        // this empty → pasted transition. Keep the whole footer grammar exact.
        guard footer.range(of: #"^[0-9]{1,3}% context left(?:\s+⚠ [0-9]+ warnings? · f2 to view)?$"#,
                           options: .regularExpression) != nil,
              let percentage = Int(footer.prefix(while: { $0.isNumber })) else { return false }
        return percentage <= 100
    }

    /// Newer terminal layouts separate model/context status from navigation.
    /// Require both rows: a status fragment alone cannot delimit a composer.
    /// The status may be visually truncated after the context percentage; the
    /// editable rows still pass the same clipping and exact-paste checks above.
    private static func isTwoRowFooter(status: String, navigation: String) -> Bool {
        guard status.hasPrefix("  "), navigation.hasPrefix("  ") else { return false }
        let parts = status.trimmingCharacters(in: .whitespaces).components(separatedBy: " · Context ")
        guard parts.count == 2,
              parts[0].range(of: #"^[A-Za-z0-9][A-Za-z0-9 ._/-]*$"#, options: .regularExpression) != nil,
              parts[1].range(of: #"^[0-9]{1,3}% (?:left|l…)(?: · [^\r\n]+)?$"#, options: .regularExpression) != nil,
              let percentage = Int(parts[1].prefix(while: { $0.isNumber })), percentage <= 100 else { return false }
        // Typing into the live composer replaces the navigation row with
        // the existing queue/send hint, while the model row stays visible.
        let nav = navigation.trimmingCharacters(in: .whitespaces)
        return isFooter(navigation)
            || nav.range(of: #"^← for agents · \? for shortcuts?$"#,
                         options: .regularExpression) != nil
            || nav.range(of: #"^← for agents · \? for(?: shortcuts?)?\s*(?:⚠ [0-9]+(?: warnings?)?)?\s*· f2(?: to view)?$"#,
                         options: .regularExpression) != nil
    }

    private static func containsFooterPair(_ rows: [String]) -> Bool {
        guard rows.count > 1 else { return false }
        return zip(rows, rows.dropFirst()).contains { status, navigation in
            isTwoRowFooter(status: "  " + status, navigation: "  " + navigation)
        }
    }

    /// Rendering changes at a row boundary are ambiguous; do not learn a
    /// split or joined word as if the user had changed its spelling.
    func value(matching initial: Self) -> String? {
        boundaryWords == initial.boundaryWords ? value : nil
    }
}
