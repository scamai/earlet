import Foundation

/// A narrow adapter for the Codex and Claude Code composers observed in
/// Witty. Witty exposes the entire terminal screen as AXValue and its mouse
/// selection as the caret. Neither is an editable document. Never fall back
/// to that screen as a field.
///
/// The supported Codex frame has a final `› ` composer, an empty separator
/// row and a recognized Codex footer; the Claude Code frame is described at
/// `claudeCode(_:)`. These are display contracts, not terminal-wide support.
/// Changed layouts, clipped drafts and collapsed pastes fail closed.
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
        return codex(lines) ?? claudeCode(lines) ?? tmuxPanes(lines)
    }

    // ─── Codex ───

    private static func codex(_ screenLines: [String]) -> Self? {
        var lines = screenLines
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

    // ─── tmux panes ───

    /// tmux draws its panes into one screen: vertical borders (`│`, with
    /// `├ ┤ ┬ ┴ ┼` where borders meet) at the same column on every row, a
    /// horizontal border across a column of stacked panes, and its status
    /// line above or below them. Each pane is read as a screen of its own and
    /// the value joins every pane's composer (an unreadable pane counts as
    /// empty text), so the before/after proof also shows which pane the paste
    /// went to and only that pane's span is ever reported. Without a vertical
    /// border a horizontal one cannot be told from a composer's rule, so a
    /// screen of stacked panes alone fails closed.
    private static func tmuxPanes(_ screenLines: [String]) -> Self? {
        let grid = screenLines.map(cells)
        let borders = grid.map { row in Set(row.filter { Self.borderGlyphs.contains($0.character) }.map(\.column)) }
        guard let widest = borders.max(by: { $0.count < $1.count }), !widest.isEmpty else { return nil }
        let columns = borders.filter { !$0.isEmpty }.reduce(widest) { $0.intersection($1) }.sorted()
        // Pane rows carry every border column; only a status line above or
        // below them may not.
        let inside = borders.indices.filter { columns.allSatisfy(borders[$0].contains) }
        guard !columns.isEmpty, let first = inside.first, let last = inside.last,
              last - first + 1 == inside.count, first <= 1, screenLines.count - 1 - last <= 1 else { return nil }
        var panes: [Self?] = []
        let edges = [-1] + columns + [Int.max]
        for (left, right) in zip(edges, edges.dropFirst()) {
            var stack: [String] = []
            for index in first...last {
                guard let text = slice(grid[index], from: left + 1, to: right) else { return nil }
                let joins = [left, right].contains { edge in
                    grid[index].first { $0.column == edge }.map { "├┤┼".contains($0.character) } ?? false
                }
                if joins, !text.isEmpty, text.allSatisfy({ $0 == "─" }) {
                    panes.append(pane(stack))
                    stack = []
                } else {
                    stack.append(text)
                }
            }
            panes.append(pane(stack))
        }
        guard panes.contains(where: { $0 != nil }) else { return nil }
        let value = panes.map { $0?.value ?? "" }.joined(separator: "\u{1E}")
        let boundaries = panes.enumerated().flatMap { index, pane in
            (pane?.boundaryWords ?? []).map { "\(index):" + $0 }
        }
        return Self(value: value, boundaryWords: boundaries)
    }

    private static let borderGlyphs: Set<Character> = ["│", "├", "┤", "┬", "┴", "┼"]

    private static func pane(_ rows: [String]) -> Self? {
        var lines = rows
        while lines.last?.isEmpty == true { lines.removeLast() }
        return codex(lines) ?? claudeCode(lines)
    }

    /// Each character with the terminal column it starts at.
    private static func cells(_ line: String) -> [(character: Character, column: Int)] {
        var column = 0
        return line.map { character in
            defer { column += displayWidth(String(character)) }
            return (character, column)
        }
    }

    /// The text between two columns, trailing spaces trimmed; `nil` when a
    /// wide character straddles an edge, which a real pane border never cuts.
    private static func slice(_ row: [(character: Character, column: Int)], from start: Int, to end: Int) -> String? {
        var text = ""
        for cell in row where cell.column >= start && cell.column < end {
            guard cell.column + displayWidth(String(cell.character)) <= end else { return nil }
            text.append(cell.character)
        }
        return String(text.reversed().drop(while: \.isWhitespace).reversed())
    }

    // ─── Claude Code ───

    /// Claude Code draws its composer between two horizontal rules as wide as
    /// the terminal (the upper one may carry a label such as a mode name).
    /// The first row starts with `❯` and a no-break space; later rows are
    /// indented two columns; up to eight status rows, each indented two
    /// columns, follow the lower rule at the bottom of the screen. Observed
    /// with Claude Code 2.1 at 40 and 80 columns.
    ///
    /// The composer wraps rows itself, at four columns less than the
    /// terminal: words at a space, which the wrap consumes, and a run of
    /// Chinese, Japanese or Korean characters (or a word longer than a row)
    /// anywhere, consuming nothing. A row break therefore reads as a space,
    /// except after a full row when either side is such a character. A full
    /// row whose break touches a Latin character is ambiguous (a long word
    /// split, or a word that just fitted), so its boundary words must stay
    /// unchanged while the span is watched, as in the Codex frame.
    private static func claudeCode(_ lines: [String]) -> Self? {
        guard let bottom = lines.lastIndex(where: isClaudeRule), lines.count - bottom - 1 <= 8,
              lines[(bottom + 1)...].allSatisfy({ $0.isEmpty || $0.hasPrefix("  ") }) else { return nil }
        let columns = lines[bottom].count
        guard let start = lines[..<bottom].lastIndex(where: {
                  $0 == "❯" || $0.hasPrefix("❯\u{00A0}") || $0.hasPrefix("❯ ")
              }),
              start >= 1, bottom - start <= 40, isClaudeUpperRule(lines[start - 1], columns: columns) else { return nil }
        var rows = [String(lines[start].dropFirst(2))]
        for line in lines[(start + 1)..<bottom] {
            if line.trimmingCharacters(in: .whitespaces).isEmpty { rows.append(""); continue }
            guard line.hasPrefix("  ") else { return nil }
            rows.append(String(line.dropFirst(2)))
        }
        rows = rows.map { String($0.reversed().drop(while: \.isWhitespace).reversed()) }
        guard rows.joined().utf16.count <= 8_192,
              !rows.contains(where: { $0.contains("[Pasted text #") || $0.contains("[Image #") }) else { return nil }
        if rows.count == 1, rows[0].hasPrefix("Try \"") || rows[0].hasPrefix("Press up to ") {
            return Self(value: " ", boundaryWords: [])
        }
        var text = rows[0]
        var boundaries: [String] = []
        for (previous, row) in zip(rows, rows.dropFirst()) {
            guard let last = previous.last, let next = row.first, !next.isWhitespace else {
                text += " " + row
                continue
            }
            let full = displayWidth(previous) >= columns - 5
            let spaceless = (isSpaceless(last), isSpaceless(next))
            text += full && (spaceless.0 || spaceless.1) ? row : " " + row
            if full, !(spaceless.0 && spaceless.1),
               let before = previous.split(whereSeparator: \.isWhitespace).last,
               let after = row.split(whereSeparator: \.isWhitespace).first {
                boundaries.append(before + "\n" + after)
            }
        }
        let words = text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        return Self(value: words.isEmpty ? " " : " " + words + " ", boundaryWords: boundaries)
    }

    private static func isClaudeRule(_ line: String) -> Bool {
        line.count >= 20 && line.allSatisfy { $0 == "─" }
    }

    /// The upper rule is as wide as the lower one. A label replaces part of
    /// it and ends one dash from the right; in a narrow terminal the label
    /// (a branch or mode name, truncated with `…`) can take the whole left
    /// side, leaving a single leading space.
    private static func isClaudeUpperRule(_ line: String, columns: Int) -> Bool {
        displayWidth(line) == columns && line.hasSuffix("─")
            && (line.allSatisfy { $0 == "─" } || line.hasSuffix(" ─"))
            && (line.hasPrefix("─") || line.hasPrefix(" "))
    }

    /// Scripts written without spaces between words, which a composer may
    /// wrap between any two characters: CJK ideographs and their
    /// punctuation, kana, Hangul and fullwidth forms.
    static func isSpaceless(_ character: Character) -> Bool {
        guard let scalar = character.unicodeScalars.first?.value else { return false }
        switch scalar {
        case 0x1100...0x115F, 0x2E80...0x303E, 0x3041...0x33FF, 0x3400...0x4DBF, 0x4E00...0x9FFF,
             0xA960...0xA97F, 0xAC00...0xD7A3, 0xF900...0xFAFF, 0xFE30...0xFE4F, 0xFF01...0xFF60,
             0xFFE0...0xFFE6, 0x20000...0x3FFFD:
            return true
        default:
            return false
        }
    }

    /// Terminal columns: two for the spaceless scripts above and for emoji
    /// presentation, none for combining marks, one otherwise.
    static func displayWidth(_ text: String) -> Int {
        text.reduce(0) { width, character in
            if isSpaceless(character) { return width + 2 }
            guard let scalar = character.unicodeScalars.first else { return width }
            if scalar.properties.isEmojiPresentation { return width + 2 }
            return width + (scalar.properties.generalCategory == .nonspacingMark ? 0 : 1)
        }
    }

    private static func containsFooterPair(_ rows: [String]) -> Bool {
        guard rows.count > 1 else { return false }
        return zip(rows, rows.dropFirst()).contains { status, navigation in
            isTwoRowFooter(status: "  " + status, navigation: "  " + navigation)
        }
    }

    /// Rendering changes at a row boundary are ambiguous; do not learn a
    /// split or joined word as if the user had changed its spelling.
    ///
    /// An empty composer has no rows to misread, so it is always returned:
    /// that is how a send, which empties it, is told from a changed layout.
    func value(matching initial: Self) -> String? {
        boundaryWords == initial.boundaryWords || value == " " ? value : nil
    }
}
