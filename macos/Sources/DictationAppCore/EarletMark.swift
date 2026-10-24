import SwiftUI

/// A microphone first, with three pairs of wheat grains in its grille.
/// The window and generated app icons share these paths; no raster tracing
/// or separate small-icon drawing can drift from the native mark.
public struct EarletMark: View {
    public init() {}

    public var body: some View {
        ZStack {
            EarletMarkShape(part: .base).fill(.foreground)
            EarletMarkShape(part: .support).fill(.foreground)
            EarletMarkShape(part: .head).fill(.foreground, style: FillStyle(eoFill: true))
        }
        .accessibilityHidden(true)
    }
}

private struct EarletMarkShape: Shape {
    enum Part { case head, support, base }
    let part: Part

    func path(in rect: CGRect) -> Path {
        let scale = min(rect.width, rect.height) / 48
        let transform = CGAffineTransform(a: scale, b: 0, c: 0, d: scale,
                                         tx: rect.midX - 24 * scale,
                                         ty: rect.midY - 24 * scale)
        var path = Path()
        switch part {
        case .head:
            path.addRoundedRect(in: CGRect(x: 15, y: 4, width: 18, height: 29),
                                cornerSize: CGSize(width: 9, height: 9))
            for row in 0..<3 {
                let y = CGFloat(row) * 6.4 + 9
                var grain = Path()
                grain.move(to: CGPoint(x: 18.1, y: y))
                grain.addCurve(to: CGPoint(x: 22.7, y: y + 5.5),
                               control1: CGPoint(x: 17.7, y: y + 3.0),
                               control2: CGPoint(x: 19.5, y: y + 4.8))
                grain.addCurve(to: CGPoint(x: 18.1, y: y),
                               control1: CGPoint(x: 23.2, y: y + 2.8),
                               control2: CGPoint(x: 20.7, y: y + 0.5))
                grain.closeSubpath()
                path.addPath(grain)
                path.addPath(grain, transform: CGAffineTransform(a: -1, b: 0, c: 0, d: 1,
                                                                tx: 48, ty: 0))
            }
        case .support:
            var cradle = Path()
            cradle.move(to: CGPoint(x: 10.5, y: 21.5))
            cradle.addLine(to: CGPoint(x: 10.5, y: 26))
            cradle.addCurve(to: CGPoint(x: 24, y: 37.2),
                            control1: CGPoint(x: 10.5, y: 33.5),
                            control2: CGPoint(x: 16, y: 37.2))
            cradle.addCurve(to: CGPoint(x: 37.5, y: 26),
                            control1: CGPoint(x: 32, y: 37.2),
                            control2: CGPoint(x: 37.5, y: 33.5))
            cradle.addLine(to: CGPoint(x: 37.5, y: 21.5))
            path.addPath(cradle.strokedPath(StrokeStyle(lineWidth: 3.6, lineCap: .round)))
        case .base:
            path.addRect(CGRect(x: 22.2, y: 36.5, width: 3.6, height: 6.5))
            path.addRoundedRect(in: CGRect(x: 16.3, y: 41.5, width: 15.4, height: 3.6),
                                cornerSize: CGSize(width: 1.8, height: 1.8))
        }
        return path.applying(transform)
    }
}
