import XCTest
@testable import DictationKit

final class DictationTextShapeTests: XCTestCase {
    func testCountsRunsWithoutRetainingOrChangingText() {
        let text = "private prefix " + String(repeating: "A", count: 37) + " private suffix"
        let shape = DictationTextShape(text)
        XCTAssertEqual(shape.utf16Count, text.utf16.count)
        XCTAssertEqual(shape.longestRun, 37)
        XCTAssertEqual(shape.longestCompactRun, 37)
        XCTAssertFalse(String(reflecting: shape).contains("private"))
        XCTAssertFalse(String(reflecting: shape).contains(String(repeating: "A", count: 4)))
    }

    func testWhitespaceDoesNotBecomeARepeatButSeparatesTheTwoMeasures() {
        let empty = DictationTextShape("")
        XCTAssertEqual(empty.utf16Count, 0)
        XCTAssertEqual(empty.longestRun, 0)
        XCTAssertEqual(empty.longestCompactRun, 0)
        let whitespace = DictationTextShape(" \n\t\u{2003}")
        XCTAssertEqual(whitespace.longestRun, 0)
        XCTAssertEqual(whitespace.longestCompactRun, 0)
        let separated = DictationTextShape("AA A\nA\tA\u{2003}A")
        XCTAssertEqual(separated.longestRun, 2)
        XCTAssertEqual(separated.longestCompactRun, 6)
    }

    func testCountsUnicodeScalarsAndUtf16WithoutSplittingSurrogates() {
        let emoji = DictationTextShape("🙂🙂🙂")
        XCTAssertEqual(emoji.utf16Count, 6)
        XCTAssertEqual(emoji.longestRun, 3)
        let mixed = DictationTextShape("你你你aaAa")
        XCTAssertEqual(mixed.utf16Count, 7)
        XCTAssertEqual(mixed.longestRun, 3)
        XCTAssertEqual(DictationTextShape("AA,AA").longestCompactRun, 2)
        XCTAssertEqual(DictationTextShape("e\u{301}e\u{301}").longestRun, 1)
    }
}
