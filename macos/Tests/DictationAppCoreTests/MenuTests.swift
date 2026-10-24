// The menu bar menu brings the window back and reaches the log, and so does
// the window. The items are decided by the model; the scene renders them.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("The menu bar menu and the log folder")
struct MenuTests {

    @Test("idle, the menu offers the toggle, Show Dictation, Open Log Folder and Quit, in that order")
    func idleMenu() async {
        let harness = Harness()
        let model = harness.model()
        #expect(model.menuItems == [.toggle, .showWindow, .openLogFolder, .quit])
        await model.toggle()
        #expect(model.menuItems == [.toggle, .showWindow, .openLogFolder, .quit])
    }

    @Test("while recording, Cancel follows the toggle", .timeLimit(.minutes(1)))
    func recordingMenu() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(model.menuItems == [.toggle, .cancel, .showWindow, .openLogFolder, .quit])
        await model.cancel()
        #expect(await eventually { !model.recording })
        harness.trigger.release()
    }

    @Test("Open Log Folder opens it and leaves the status alone")
    func openLogFolder() {
        let harness = Harness()
        let model = harness.model()
        let before = model.status
        model.openLogFolder()
        #expect(harness.logFolderOpens.get == 1)
        #expect(model.status == before)
    }

    @Test("a log folder that will not open is named, so the user can find it")
    func logFolderThatWillNotOpen() {
        let harness = Harness()
        harness.logFolderOpenWorks.mutate { $0 = false }
        let model = harness.model()
        model.openLogFolder()
        #expect(model.status.contains("~/Library/Logs/Dictation"))
    }
}
#endif
