import Foundation

/// Outcome of waiting for the delayed-render pasteboard provider to be
/// asked for its data. `.pending` is a real third state, not just
/// "not yet failed" — it means the caller should keep waiting, distinct
/// from `.timedOut` (stop waiting, classify as a failed paste).
public enum PasteOutcome: Sendable, Equatable {
    case requested
    case timedOut
    case pending
}

/// Decides whether a paste failed from one fact: did the target app ask the
/// delayed pasteboard item for its data before the timeout? An app that
/// pastes has to read the item, so **no request in time means no paste**.
/// Reading the target field afterwards would depend on a third-party app's
/// accessibility tree, and could not tell a failed paste from a field that
/// reformatted what it received (rich text, a terminal echoing control
/// sequences).
///
/// Pure function — no timers, no pasteboard, no TCC. `DictationSelfCheck`
/// exercises this directly.
public enum PasteTimeoutClassifier {
    /// - Parameters:
    ///   - wasAsked: whether `NSPasteboardItemDataProvider.pasteboard(_:item:provideDataForType:)`
    ///     has fired at least once for our delayed item.
    ///   - elapsedMs: milliseconds elapsed since we posted the synthesized ⌘V.
    ///   - timeoutMs: the failed-paste timeout budget.
    public static func classify(wasAsked: Bool, elapsedMs: Double, timeoutMs: Double) -> PasteOutcome {
        if wasAsked { return .requested }
        return elapsedMs >= timeoutMs ? .timedOut : .pending
    }
}

/// Whether it's safe to restore the user's PRE-dictation clipboard contents,
/// guarded by `NSPasteboard.changeCount`, so that something another app
/// copied during the paste is never overwritten.
///
/// Pure function over the two change-count observations; no `NSPasteboard`
/// dependency, so `DictationSelfCheck` can exercise every boundary without
/// touching the real system clipboard (which would pollute whatever the
/// test runner's clipboard actually holds).
public enum ClipboardRestorePolicy {
    public enum Decision: Sendable, Equatable {
        /// Nothing else touched the pasteboard since our delayed-render
        /// write — safe to put the user's prior contents back.
        case restore
        /// The change count moved since our write (a third-party app, or
        /// the user's own real ⌘C, wrote to the pasteboard in between) —
        /// restoring now would silently clobber THAT content, which is
        /// worse than leaving our dictation text sitting in the clipboard.
        case skipThirdPartyWrite
    }

    /// - Parameters:
    ///   - changeCountAfterOurWrite: `NSPasteboard.changeCount` observed
    ///     immediately after we registered our delayed-render item.
    ///   - changeCountNow: `NSPasteboard.changeCount` observed right before
    ///     the restore would happen.
    public static func decide(changeCountAfterOurWrite: Int, changeCountNow: Int) -> Decision {
        changeCountNow == changeCountAfterOurWrite ? .restore : .skipThirdPartyWrite
    }
}
