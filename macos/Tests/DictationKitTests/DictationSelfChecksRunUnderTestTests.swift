import XCTest
@testable import DictationKit

/// Runs `DictationKit`'s own self-checks under `swift test`.
///
/// These two functions carry a large share of this module's real coverage —
/// the ring buffer's eviction and wraparound, the paste-timeout
/// classification, the permission-status combinatorics, the report builder's
/// per-stage `n` accounting — and until now the ONLY way to execute them was
/// `DictationApp --self-check`.
///
/// That entry point is unavailable exactly when a developer is most likely to
/// be working on dictation, and it refuses on purpose:
///
///     DictationApp: another instance appears to be running, and --self-check
///     talks to the microphone directly. Running both at once can deadlock:
///     two processes configuring AVAudioEngine against the same mic hang
///     forever inside Core Audio with no error.
///
/// `AudioConcurrencyGuard` is right to refuse — that deadlock is documented,
/// reproducible and unrecoverable. But the consequence was that a whole tier
/// of checks silently did not run whenever the app was open, and "did not
/// run" is indistinguishable from "passed" in a log you skim.
///
/// Neither function touches the microphone, the pasteboard, TCC or the
/// accessibility API: `runDictationSelfCheck` constructs a `DictationCapture`
/// and drives its bookkeeping without ever calling `start()`/`warmUp()`, and
/// `runDictationSessionReportSelfCheck` is pure arithmetic over records. So
/// there is no reason for them to be reachable only from the one binary that
/// is not allowed to run.
final class DictationSelfChecksRunUnderTestTests: XCTestCase {

    func testDictationSelfCheckPasses() {
        XCTAssertTrue(runDictationSelfCheck(),
                      "runDictationSelfCheck() reported failures — see stderr above")
    }

    func testDictationSessionReportSelfCheckPasses() {
        XCTAssertTrue(runDictationSessionReportSelfCheck(),
                      "runDictationSessionReportSelfCheck() reported failures — see stderr above")
    }

    // ─── Checks that previously ran only from the app self-check ───
    //
    // Dictation has no `--self-check` entry point, so until these lines they
    // ran nowhere. Each is pure logic: no microphone, pasteboard, TCC or
    // accessibility call, and no write outside a temporary directory.

    /// The correction observer's anchor tiers, terminal conditions and diff.
    func testDictationCorrectionObserverSelfCheckPasses() {
        XCTAssertTrue(runDictationCorrectionObserverSelfCheck(),
                      "runDictationCorrectionObserverSelfCheck() reported failures — see stdout above")
    }

    /// A failed insert never observes, and one observer serves every dictation.
    func testDictationSessionCorrectionWiringSelfCheckPasses() {
        XCTAssertTrue(runDictationSessionCorrectionWiringSelfCheck(),
                      "runDictationSessionCorrectionWiringSelfCheck() reported failures — see stdout above")
    }

    /// Vocabulary hygiene, the engine policy, and `bias_phrases` on the wire.
    func testDictationBiasVocabularySelfCheckPasses() {
        XCTAssertTrue(runDictationBiasVocabularySelfCheck(),
                      "runDictationBiasVocabularySelfCheck() reported failures — see stdout above")
    }

    /// The formatter the app now applies to every insertion.
    func testDictationFormatterSelfCheckPasses() {
        XCTAssertTrue(runDictationFormatterSelfCheck(),
                      "runDictationFormatterSelfCheck() reported failures — see stdout above")
    }

    /// A chosen rule set reaches the formatter through `insertionText`.
    func testDictationStyleWiringSelfCheckPasses() {
        XCTAssertTrue(runDictationStyleWiringSelfCheck(),
                      "runDictationStyleWiringSelfCheck() reported failures — see stdout above")
    }
}
