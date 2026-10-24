#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Mac engine status glyph")
struct DictationEngineStatusTests {
    @Test("failed enable publishes the engine unavailable glyph")
    func failedEnableShowsEngineFailure() async {
        let harness = Harness()
        harness.workers.configure = { $0.state.mutate { $0.startReply = .fail } }
        let model = harness.model()
        model.refreshPermissions()

        await model.toggle()

        #expect(!model.enabled)
        #expect(model.engineFailed)
        #expect(model.menuGlyph == .engineFailed)
    }

    @Test("a successful retry clears the engine failure glyph")
    func successfulRetryClearsEngineFailure() async {
        let harness = Harness()
        harness.workers.configure = { $0.state.mutate { $0.startReply = .fail } }
        let model = harness.model()
        model.refreshPermissions()
        await model.toggle()
        #expect(model.menuGlyph == .engineFailed)

        harness.workers.configure = { _ in }
        await model.toggle()

        #expect(model.enabled)
        #expect(!model.engineFailed)
        #expect(model.menuGlyph == .ready)
    }

    @Test("intentional pause clears a prior engine failure")
    func intentionalPauseShowsPaused() async {
        let harness = Harness()
        let model = harness.model()
        model.refreshPermissions()
        await model.toggle()
        #expect(model.enabled)
        model.engineFailed = true

        await model.toggle()

        #expect(!model.enabled)
        #expect(!model.engineFailed)
        #expect(model.menuGlyph == .paused)
    }
}
#endif
