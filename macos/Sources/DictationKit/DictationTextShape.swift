import Foundation

/// Aggregate diagnostics only. No text, scalar value, hash or word is retained.
/// Runs count equal Unicode scalars, excluding whitespace itself. The compact
/// count also catches a run separated by spaces or newlines. These measurements
/// do not classify, remove or rewrite anything the user dictated.
public struct DictationTextShape: Sendable, Equatable {
    public let utf16Count: Int
    public let longestRun: Int
    public let longestCompactRun: Int

    public init(_ text: String) {
        var units = 0
        var run = 0
        var compactRun = 0
        var longest = 0
        var longestCompact = 0
        var previous: Unicode.Scalar?
        var previousNonWhitespace: Unicode.Scalar?
        for scalar in text.unicodeScalars {
            units += scalar.value > 0xFFFF ? 2 : 1
            if scalar.properties.isWhitespace {
                previous = nil
                run = 0
                continue
            }
            run = scalar == previous ? run + 1 : 1
            compactRun = scalar == previousNonWhitespace ? compactRun + 1 : 1
            longest = max(longest, run)
            longestCompact = max(longestCompact, compactRun)
            previous = scalar
            previousNonWhitespace = scalar
        }
        utf16Count = units
        longestRun = longest
        longestCompactRun = longestCompact
    }
}
