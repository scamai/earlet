import AppKit

// The app icon: a wheat-gold squircle with the product's mark in ink, the
// seven waveform bars whose tips draw an ear of wheat, on a short stalk.
// The mark's geometry is `DictationMarkShape` in
// Sources/DictationApp/DictationDesign.swift (a 48-unit box); the gold is the
// window palette's wheat (Sources/DictationAppCore/DictationPalette.swift).
// Flat, no text. Regenerate the packaged icon with:
//   swift macos/tools/generate-icon.swift /tmp/dictation-icon/AppIcon.iconset
//   iconutil -c icns /tmp/dictation-icon/AppIcon.iconset -o macos/Resources/AppIcon.icns
//   cp /tmp/dictation-icon/AppIcon-master.png macos/Resources/AppIcon-master.png

func srgb(_ hex: UInt32) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
}

func drawIcon(size: CGFloat) -> NSImage {
    let image = NSImage(size: NSSize(width: size, height: size))
    image.lockFocus()
    let ctx = NSGraphicsContext.current!.cgContext
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    let inset = size * 0.0625
    let body = NSRect(x: inset, y: inset, width: size - 2 * inset, height: size - 2 * inset)
    let radius = body.width * 0.225
    let path = NSBezierPath(roundedRect: body, xRadius: radius, yRadius: radius)

    // Background: wheat gold, brighter at the top.
    ctx.saveGState()
    path.addClip()
    let field = CGGradient(colorsSpace: space, colors: [srgb(0xF4C552), srgb(0xDB9213)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(field, start: CGPoint(x: body.midX, y: body.maxY),
                           end: CGPoint(x: body.midX, y: body.minY), options: [])
    ctx.restoreGState()

    // The mark, two thirds of the body, in the window's ink. Its geometry is
    // drawn y-down in a 48-unit box, as `DictationMarkShape` draws it.
    let scale = body.width * 0.66 / 48
    let originX = body.midX - 24 * scale
    let originY = body.midY - 24 * scale - body.height * 0.01
    func rect(_ x: CGFloat, _ top: CGFloat, _ width: CGFloat, _ height: CGFloat) -> NSRect {
        NSRect(x: originX + x * scale, y: originY + (48 - top - height) * scale,
               width: width * scale, height: height * scale)
    }
    let centreY: CGFloat = 48 * 0.44
    NSColor(srgbRed: 0x1D / 255, green: 0x1A / 255, blue: 0x15 / 255, alpha: 1).setFill()
    let stalk: CGFloat = 2.6
    NSBezierPath(roundedRect: rect(24 - stalk / 2, centreY + 12 - stalk / 2, stalk, 10 + stalk),
                 xRadius: stalk / 2 * scale, yRadius: stalk / 2 * scale).fill()
    let envelope: [CGFloat] = [0.35, 0.65, 0.9, 1.0, 0.9, 0.65, 0.35]
    let bar: CGFloat = 3.4
    for (index, height) in envelope.map({ 6 + 26 * $0 }).enumerated() {
        let x = 24 + CGFloat(index - 3) * 4.6 - bar / 2
        NSBezierPath(roundedRect: rect(x, centreY - height / 2 - bar / 2, bar, height - 3 + bar),
                     xRadius: bar / 2 * scale, yRadius: bar / 2 * scale).fill()
    }
    image.unlockFocus()
    return image
}

func writePNG(_ image: NSImage, to url: URL) throws {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(image.size.width),
                               pixelsHigh: Int(image.size.height), bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    image.draw(in: NSRect(origin: .zero, size: image.size))
    NSGraphicsContext.restoreGraphicsState()
    try rep.representation(using: .png, properties: [:])!.write(to: url)
}

let out = URL(fileURLWithPath: CommandLine.arguments[1])
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
// iconset: 16,32,128,256,512 @1x and @2x (32,64,256,512,1024)
let specs: [(String, CGFloat)] = [
    ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32),
    ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
    ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256),
    ("icon_256x256.png", 256), ("icon_256x256@2x.png", 512),
    ("icon_512x512.png", 512), ("icon_512x512@2x.png", 1024),
]
for (name, px) in specs {
    try writePNG(drawIcon(size: px), to: out.appendingPathComponent(name))
}
try writePNG(drawIcon(size: 1024), to: out.deletingLastPathComponent().appendingPathComponent("AppIcon-master.png"))
print("iconset written to \(out.path)")
