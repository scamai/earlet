import AppKit
import CoreGraphics
import Foundation

extension DictationKeySnapshot {
    @MainActor
    public static func live() async -> Self {
        let ownPID = ProcessInfo.processInfo.processIdentifier
        let reading = await Task.detached(priority: .utility) {
            let recorders: DictationOtherRecorderSnapshot
            if #available(macOS 14.2, *) { recorders = DictationOtherRecorders().snapshot(excluding: ownPID) }
            else { recorders = .init(available: false) }
            let windows = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]]) ?? []
            let owners = Set(windows.compactMap { window -> Int32? in
                guard let bounds = window[kCGWindowBounds as String] as? [String: CGFloat],
                      (bounds["Width"] ?? 0) >= 40, (bounds["Height"] ?? 0) >= 20,
                      let pid = window[kCGWindowOwnerPID as String] as? Int32, pid != ownPID else { return nil }
                return pid
            })
            return (recorders, owners)
        }.value
        let foreground = NSWorkspace.shared.frontmostApplication?.processIdentifier
        var names: [Int32: String] = [:]
        var pids = reading.0.pids.union(reading.1)
        if let foreground { pids.insert(foreground) }
        for pid in pids {
            if let name = NSRunningApplication(processIdentifier: pid)?.localizedName { names[pid] = name }
        }
        return .init(time: ProcessInfo.processInfo.systemUptime, recorders: reading.0,
                     foreground: foreground, windows: reading.1, names: names)
    }
}
