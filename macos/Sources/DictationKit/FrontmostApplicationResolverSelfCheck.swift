import Foundation

/// Pure-logic self-check for `FrontmostApplicationResolver` — no TCC, no
/// window-server round trip, no real `NSWorkspace`/AX call. Two layers,
/// matching the two layers the type itself ended up with:
///   1. `resolveChain(axResolver:workspaceResolver:)` — the fallback
///      ordering, driven with synthetic closures so every combination
///      (AX present, AX absent + workspace present, both absent) is
///      exercised deterministically, independent of whatever this process
///      actually has AX permission for.
///   2. `resolve()` itself, called for real in THIS environment — cannot
///      assert a specific bundle id (depends on whatever process is
///      actually focused when `--self-check` runs), but must never crash
///      or hang, and its result must satisfy the bundleID<->source
///      invariant `resolveChain` establishes.
/// Registered in the app entry point's `--self-check` chain next to
/// `runDictationSelfCheck`.
public func runFrontmostApplicationResolverSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }

    // MARK: fallback-chain ordering — synthetic inputs, pure logic

    do {
        let result = FrontmostApplicationResolver.resolveChain(
            axResolver: { "com.example.ax" }, workspaceResolver: { "com.example.workspace" })
        check(result.bundleID == "com.example.ax", "chain/ax-wins: AX result must win when both sources answer")
        check(result.source == .axFocusedElement, "chain/ax-wins: source must report .axFocusedElement")
    }

    do {
        let result = FrontmostApplicationResolver.resolveChain(
            axResolver: { nil }, workspaceResolver: { "com.example.workspace" })
        check(result.bundleID == "com.example.workspace", "chain/workspace-fallback: must fall through to NSWorkspace when AX returns nil")
        check(result.source == .nsWorkspace, "chain/workspace-fallback: source must report .nsWorkspace")
    }

    do {
        // Models: no focused element, no permission, a helper process with
        // no bundle id, or a hung target bounded by the messaging timeout —
        // every one of these collapses to "AX resolver returned nil" by
        // construction, so this single case covers all of them at the
        // chain-ordering level (their individual behavior is folded into
        // `resolveViaAXFocusedElement` itself, exercised live below).
        let result = FrontmostApplicationResolver.resolveChain(axResolver: { nil }, workspaceResolver: { nil })
        check(result.bundleID == nil, "chain/both-absent: bundleID must be nil when neither source answers")
        check(result.source == .none, "chain/both-absent: source must report .none")
    }

    do {
        // AX empty-string edge case: an AX resolver that produces an empty
        // (not nil) string is still treated as "answered" by the chain —
        // emptiness classification is `DictationFormatter.profile(forApp:)`'s
        // job (it already treats "" like nil), not this resolver's.
        let result = FrontmostApplicationResolver.resolveChain(axResolver: { "" }, workspaceResolver: { "com.example.workspace" })
        check(result.bundleID == "", "chain/ax-empty-string: an empty-but-non-nil AX answer must still win over workspace (classification is the formatter's job)")
        check(result.source == .axFocusedElement, "chain/ax-empty-string: source must still report .axFocusedElement")
    }

    // MARK: resolve() — the real chain, real system sources, THIS environment

    do {
        // Must never crash or hang regardless of TCC state in this
        // environment — the entire point of the messaging timeout and the
        // permission check inside `resolveViaAXFocusedElement()`.
        let result = FrontmostApplicationResolver.resolve()
        switch result.source {
        case .none:
            check(result.bundleID == nil, "resolve/none-invariant: source .none must carry a nil bundleID")
        case .axFocusedElement, .nsWorkspace:
            check(result.bundleID != nil, "resolve/answered-invariant: a non-.none source must carry a non-nil bundleID")
        }
    }

    do {
        // Calling twice in immediate succession must not crash or hang
        // either — guards against any one-shot-only assumption sneaking
        // into the AX path (e.g. a systemWide element that can only be
        // queried once). Reaching this line at all is the point; the
        // invariant check below is the same one the single-call case
        // already covers, re-applied to prove the SECOND call is equally
        // well-formed, not just the first.
        let second = FrontmostApplicationResolver.resolve()
        switch second.source {
        case .none:
            check(second.bundleID == nil, "resolve/repeatable: second call's .none source must carry a nil bundleID")
        case .axFocusedElement, .nsWorkspace:
            check(second.bundleID != nil, "resolve/repeatable: second call's non-.none source must carry a non-nil bundleID")
        }
    }

    guard failures.isEmpty else {
        let first = failures[0]
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("frontmost-app-resolver-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("frontmost-app-resolver-check: PASS")
    return true
}
