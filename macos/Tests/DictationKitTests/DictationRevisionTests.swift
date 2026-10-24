import XCTest
@testable import DictationKit

final class DictationRevisionTests: XCTestCase {
    func testStableRevisionsIncludeRetractionAndNeverResettleIdenticalText() {
        var revisions = DictationStableRevisions()
        let original = "Use Mies today"
        XCTAssertNil(revisions.takeIfSettled(span: original, original: original, quietSeconds: 8, settleSeconds: 4))
        XCTAssertNil(revisions.takeIfSettled(span: "Use Miles today", original: original, quietSeconds: 3.9, settleSeconds: 4))
        XCTAssertEqual(revisions.takeIfSettled(span: "Use Miles today", original: original, quietSeconds: 4, settleSeconds: 4), "Use Miles today")
        XCTAssertFalse(revisions.needsSettlement(span: "Use Miles today", original: original))
        XCTAssertNil(revisions.takeIfSettled(span: "Use Miles today", original: original, quietSeconds: 8, settleSeconds: 4))
        XCTAssertEqual(revisions.takeIfSettled(span: "Use Mils today", original: original, quietSeconds: 4, settleSeconds: 4), "Use Mils today")
        XCTAssertTrue(revisions.needsSettlement(span: original, original: original))
        XCTAssertEqual(revisions.takeIfSettled(span: original, original: original, quietSeconds: 4, settleSeconds: 4), original)
        XCTAssertNil(revisions.takeIfSettled(span: nil, original: original, quietSeconds: 8, settleSeconds: 4))
        XCTAssertNil(revisions.takeIfSettled(span: original, original: original, quietSeconds: 8, settleSeconds: 4))
    }
}
