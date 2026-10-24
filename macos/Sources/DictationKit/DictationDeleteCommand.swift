import Foundation

/// "Scratch that" — the spoken command that removes the line the app just typed.
///
/// ## Why this is a fixed, exact-match list and not a classifier
///
/// The cost function here is asymmetric to the point of being one-sided. A
/// MISSED delete command types four extra words the user then removes by
/// hand — annoying, recoverable in one keystroke. A FALSE delete command
/// silently backspaces over text the user meant to keep, in an app the product does
/// not own, with no visible undo stack of its own. So the recognizer is
/// deliberately the least clever thing that can work: normalize the
/// utterance, compare it to a short hand-written list, require an EXACT
/// match of the whole utterance.
///
/// That means "scratch that whole paragraph" is NOT a delete command — it is
/// text. Only an utterance that is nothing but the command counts. This is
/// also what makes the feature explainable in one sentence in Settings,
/// where the phrase list is rendered read-only from `phrases` below rather
/// than retyped into a string the code cannot check.
///
/// ## Normalization
///
/// Lowercase, then drop everything that is not a letter or a digit. This
/// absorbs the two things ASR reliably varies on for a short utterance:
/// trailing punctuation ("Scratch that." / "Scratch that!") and spacing
/// (Whisper emits Chinese with and without a leading space depending on the
/// preceding context). It deliberately does NOT do stemming, synonym
/// expansion, or edit-distance matching — each of those widens the false
/// positive surface, which is the one thing this type must not do.
public enum DictationDeleteCommand {

    /// The exact set, post-normalization. Keep it SHORT: every entry is a
    /// phrase the user can no longer dictate as literal text.
    ///
    /// English entries are the two phrases every dictation product on the
    /// market already trains users on ("scratch that" is Dragon's, "delete
    /// that" is Apple's), so they cost no new muscle memory. The Chinese
    /// entries are the direct equivalents; they are here because this app's
    /// primary user dictates in both languages and Whisper's zh output for a
    /// two-character command is stable.
    ///
    /// NOT included, on purpose: "undo", "no no no", "oops", "cancel". Each
    /// is something a person says mid-sentence as content.
    public static let phrases: Set<String> = [
        // en
        "scratchthat",
        "deletethat",
        "scratchthatline",
        "deletethatline",
        // zh
        "删掉",
        "删除",
        "删掉那句",
        "删掉这句",
    ]

    /// The phrase list as a person says it — for Settings, which must show
    /// what it will actually match. Derived by hand from `phrases` and
    /// PINNED against it by the self-check (same count, and every display
    /// string normalizes back into the set), so the two cannot drift.
    public static let displayPhrases: [String] = [
        "scratch that",
        "delete that",
        "scratch that line",
        "delete that line",
        "删掉",
        "删除",
        "删掉那句",
        "删掉这句",
    ]

    /// Lowercase; keep only letters and digits. See the type doc comment.
    ///
    /// Unicode-aware by construction (`Character.isLetter`/`isNumber`, not an
    /// ASCII range), so CJK survives and CJK punctuation ("。" "，") is
    /// stripped exactly like "." and ",".
    public static func normalize(_ text: String) -> String {
        var out = String()
        out.reserveCapacity(text.count)
        for character in text.lowercased() where character.isLetter || character.isNumber {
            out.append(character)
        }
        return out
    }

    /// The whole test: does this utterance consist of nothing but a delete
    /// phrase?
    public static func isDeleteCommand(_ text: String) -> Bool {
        phrases.contains(normalize(text))
    }
}

/// How many backspaces it takes to remove a string the app typed.
///
/// ## The honest limits of counting characters
///
/// The app inserts text by pasting it, and removes text by synthesizing that
/// many ⌫ key events. There is no API that says "undo the paste you just
/// did" in an arbitrary third-party app, so this is a count, and a count can
/// be wrong wherever a backspace does not consume exactly one Swift
/// `Character` (extended grapheme cluster):
///
///   - **Terminals and terminal-shaped inputs (Claude Code, iTerm, Warp,
///     the Xcode console): reliable.** These delete by grapheme, one ⌫ per
///     cluster, and they do not autocorrect or reflow. This is the target
///     case for the feature and the case it is measured against.
///   - **Emoji built from ZWJ sequences ("👩‍👩‍👧" is ONE Swift `Character`
///     but several code points): app-dependent.** Some editors delete the
///     whole cluster per ⌫, others peel one code point at a time. Counting
///     clusters is the right answer for the first kind and leaves residue
///     in the second.
///   - **Rich editors that autocorrect or auto-pair (Pages, Notion, a
///     browser text field with autocomplete): best-effort.** If the app
///     mutated the text after the paste, the count no longer describes what
///     is on screen.
///
/// The mitigation is not a cleverer count — it is that nothing is ever lost:
/// the deleted text stays in dictation history, flagged, with a Restore
/// control. Say this plainly in the UI rather than implying the deletion is
/// transactional.
public enum DictationUndoPlan {

    /// Backspaces required to remove `text`.
    ///
    /// `String.count` IS the grapheme-cluster count in Swift, which is the
    /// number that matches how a text field's ⌫ behaves in the terminal
    /// case above — "café" (composed or decomposed), "你好", and "🙂" are 4,
    /// 2 and 1 respectively, not 4/5, 6 and 4 (UTF-8 bytes) or 4/5, 2 and 2
    /// (UTF-16 units). Spelled out as its own function so the self-check can
    /// pin those cases; the alternative — an inline `text.count` at the call
    /// site — is exactly how a later "optimization" to `utf16.count` would
    /// ship unnoticed and start eating the user's real text.
    public static func backspaceCount(for text: String) -> Int {
        text.count
    }

    /// Hard ceiling on one undo, in characters.
    ///
    /// A dictation is one utterance; 2000 characters is far more than a
    /// person says in a single push-to-talk press (the longest in this
    /// project's own 14k-utterance corpus is well under 1000). The bound
    /// exists so that a corrupted or absurd last-insertion record can never
    /// turn into thousands of synthesized keystrokes hammering an app the
    /// user is in the middle of using.
    public static let maxBackspaces = 2000

    /// Whether an undo of this length is allowed to proceed at all.
    public static func isWithinBound(_ count: Int) -> Bool {
        count > 0 && count <= maxBackspaces
    }
}
