import CoreGraphics
import XCTest
@testable import DictationKit

/// The mouse button's tap swallows the bound button, so its Back or Forward
/// never fires underneath a dictation, and reports raw edges. The live tap
/// needs TCC grants, so the swallow decision is pinned as the pure function
/// the callback runs, and the rebind through the test seam.
final class DictationMouseButtonTests: XCTestCase {

    /// Button 4 in CG numbering.
    private let back = 3

    func testTheBoundButtonsPressIsSwallowed() {
        XCTAssertEqual(
            DictationMouseButton.decide(type: .otherMouseDown, buttonNumber: 3, bound: back, isDown: false),
            .swallowAndHandle(down: true))
    }

    func testTheReleaseOfATrackedPressIsSwallowed() {
        XCTAssertEqual(
            DictationMouseButton.decide(type: .otherMouseUp, buttonNumber: 3, bound: back, isDown: true),
            .swallowAndHandle(down: false))
    }

    /// A release whose press this tap never saw (held before the binding)
    /// belongs to the frontmost app.
    func testAnOrphanReleasePassesThrough() {
        XCTAssertEqual(
            DictationMouseButton.decide(type: .otherMouseUp, buttonNumber: 3, bound: back, isDown: false),
            .passThrough)
    }

    func testOtherButtonsAndNoBindingPassThrough() {
        XCTAssertEqual(
            DictationMouseButton.decide(type: .otherMouseDown, buttonNumber: 4, bound: back, isDown: false),
            .passThrough, "button 5 while button 4 is bound")
        XCTAssertEqual(
            DictationMouseButton.decide(type: .otherMouseDown, buttonNumber: 3, bound: nil, isDown: false),
            .passThrough, "nothing bound")
        XCTAssertEqual(
            DictationMouseButton.decide(type: .leftMouseDown, buttonNumber: 3, bound: back, isDown: false),
            .passThrough, "a left click is never this tap's")
    }

    func testEdgesAreReportedOncePerPress() {
        let mouse = DictationMouseButton()
        mouse.updateBinding(MouseButtonBinding(buttonNumber: back))
        var edges: [String] = []
        mouse.onPress = { edges.append("press") }
        mouse.onRelease = { edges.append("release") }

        mouse.simulateEdgeForTesting(down: true)
        mouse.simulateEdgeForTesting(down: true)
        mouse.simulateEdgeForTesting(down: false)
        mouse.simulateEdgeForTesting(down: false)

        XCTAssertEqual(edges, ["press", "release"], "a repeated edge is swallowed, not reported twice")
    }

    /// A rebind or clear while the button is down must not leave the hold it
    /// started waiting for a release the new binding will never report.
    func testARebindMidPressReportsTheRelease() {
        let mouse = DictationMouseButton()
        mouse.updateBinding(MouseButtonBinding(buttonNumber: back))
        var releases = 0
        mouse.onRelease = { releases += 1 }

        mouse.simulateEdgeForTesting(down: true)
        mouse.updateBinding(MouseButtonBinding(buttonNumber: 4))
        XCTAssertEqual(releases, 1)

        mouse.updateBinding(nil)
        XCTAssertEqual(releases, 1, "nothing was down the second time")
    }
}
