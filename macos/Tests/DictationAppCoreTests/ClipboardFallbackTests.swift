// A refused paste leaves the words on the clipboard with a ⌘V hint, except
// in a password field or under Secure Input, where keeping them is the leak.

#if canImport(Testing)
import AppKit
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("A failed paste keeps the text on the clipboard")
struct ClipboardFallbackTests {

    private func dictate(failing failure: DictationInsertFailure,
                         _ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        harness.inserts.outcome.mutate { $0 = .failed(failure) }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        return model
    }

    @Test("a paste the target never asked for leaves the text on the clipboard")
    func pasteNeverRequestedFallsBack() async {
        let harness = Harness()
        let model = await dictate(failing: .pasteNeverRequested, harness)
        #expect(harness.clipboard.get == ["Hello world"])
        #expect(model.status.contains("⌘V"))
    }

    @Test("missing Accessibility says so, and still keeps the text")
    func accessibilityMissingFallsBack() async {
        let harness = Harness()
        let model = await dictate(failing: .accessibilityPermissionMissing, harness)
        #expect(harness.clipboard.get == ["Hello world"])
        #expect(model.status.contains("Accessibility"))
    }

    @Test("a password field or Secure Input keeps nothing")
    func secureTargetsKeepNothing() async {
        for failure in [DictationInsertFailure.secureFieldFocused,
                        .secureInputActive(heldByOtherProcess: true)] {
            let harness = Harness()
            let model = await dictate(failing: failure, harness)
            #expect(harness.clipboard.get.isEmpty, "\(failure)")
            #expect(model.status.contains("Nothing was"), "\(failure)")
        }
    }

    @Test("a clipboard that refuses the text is reported, not claimed")
    func clipboardFailureIsHonest() async {
        let harness = Harness()
        harness.clipboardWorks.mutate { $0 = false }
        let model = await dictate(failing: .focusedElementNotEditable, harness)
        #expect(!model.status.contains("⌘V"))
        #expect(model.status == "The text could not be typed here. Your words are still available below, "
                + "or from Earlet's menu bar icon → Copy last text.")
    }

    @Test("missing Accessibility with a clipboard that refuses too points at Copy last text where it is")
    func accessibilityMissingWithoutClipboardPointsAtCopy() async {
        let harness = Harness()
        harness.clipboardWorks.mutate { $0 = false }
        let model = await dictate(failing: .accessibilityPermissionMissing, harness)
        #expect(model.recentResult.entry?.needsRecovery == true)
        #expect(model.status == "Allow \(harness.accessibilitySettingsName) to type here. "
                + "Your words are kept: use Copy last text on Home or from Earlet's menu bar icon.")
    }

    @Test("the live fallback writes the text and the concealed marker to the pasteboard it is given")
    func liveFallbackWritesConcealedText() async throws {
        // A private named pasteboard: the user's general pasteboard is not
        // touched by this test.
        let name = NSPasteboard.Name("ai.scam.dictation.tests.\(UUID().uuidString)")
        let pasteboard = NSPasteboard(name: name)
        defer { pasteboard.releaseGlobally() }
        #expect(await TextInserter.preserveOnClipboard("hello world", pasteboard: pasteboard))
        #expect(pasteboard.string(forType: .string) == "hello world")
        // Data, not just a declared type: an undeclared marker is refused
        // silently, and a declared one without data marks nothing.
        #expect(pasteboard.data(forType: concealedPasteboardType) != nil)
        #expect(await !TextInserter.preserveOnClipboard("", pasteboard: pasteboard))
    }
}
#endif
