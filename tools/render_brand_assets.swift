import AppKit
import SwiftUI

/// Compile with macos/Sources/DictationAppCore/EarletMark.swift, then pass a
/// fresh output directory. The iconset is consumed by macOS's iconutil.
@main
struct RenderBrandAssets {
    @MainActor
    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            throw NSError(domain: "EarletBrand", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Pass a fresh output directory"])
        }
        let destination = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            throw NSError(domain: "EarletBrand", code: 2,
                          userInfo: [NSLocalizedDescriptionKey: "Output already exists"])
        }
        let iconset = destination.appendingPathComponent("Earlet.iconset", isDirectory: true)
        try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
        for size in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let name = "icon_\(size)x\(size)\(scale == 2 ? "@2x" : "").png"
                try render(size: size * scale, to: iconset.appendingPathComponent(name))
            }
        }
        try render(size: 1024, to: destination.appendingPathComponent("AppIcon-master.png"))
    }

    @MainActor
    private static func render(size: Int, to url: URL) throws {
        let side = CGFloat(size)
        let view = ZStack {
            RoundedRectangle(cornerRadius: side * 0.192, style: .continuous)
                .fill(LinearGradient(colors: [Color(red: 0.97, green: 0.79, blue: 0.33),
                                               Color(red: 0.86, green: 0.59, blue: 0.08)],
                                     startPoint: .top, endPoint: .bottom))
                .padding(side / 16)
            EarletMark()
                .foregroundStyle(Color(red: 0.12, green: 0.105, blue: 0.08))
                .frame(width: side * 0.65, height: side * 0.65)
        }
        .frame(width: side, height: side)
        .environment(\.colorScheme, .light)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        renderer.isOpaque = false
        guard let image = renderer.cgImage,
              let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
            throw NSError(domain: "EarletBrand", code: 3,
                          userInfo: [NSLocalizedDescriptionKey: "Could not render the app icon"])
        }
        try data.write(to: url, options: .atomic)
    }
}
