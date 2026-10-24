import CoreGraphics
import Foundation

/// One CGEvent tap's full teardown, shared by the three tap owners
/// (`DictationHotkey`, `DictationMouseButton`, `CGEventLatchStopTrap`) so the
/// sequence exists exactly once and none of the three can quietly lose a
/// step again.
public enum EventTapTeardown {

    /// Disable the tap, remove its run-loop source from the main run loop,
    /// then invalidate the port.
    public static func invalidate(tap: CFMachPort?, runLoopSource: CFRunLoopSource?) {
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        if let runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), runLoopSource, .commonModes)
        }
        if let tap { CFMachPortInvalidate(tap) }
    }
}
