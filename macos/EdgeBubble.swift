import AppKit
import QuartzCore

private final class BubblePanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

// Main-thread setup only; Core Animation renders the ripple independently of
// input handling. The panel is clipped to the departing screen's edge.
final class EdgeBubble {
    private let panel: NSPanel
    private let canvas = CALayer()
    private var hideTimer: Timer?
    init() {
        panel = BubblePanel(contentRect: NSRect(x: 0, y: 0, width: 180, height: 180), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = false
        panel.ignoresMouseEvents = true; panel.hidesOnDeactivate = false
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        let view = NSView(frame: NSRect(x: 0, y: 0, width: 180, height: 180))
        view.wantsLayer = true; view.layer = canvas; canvas.masksToBounds = true
        panel.contentView = view
    }
    func play(right: Bool, y: Double) {
        guard let screen = NSScreen.screens.first(where: { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == CGMainDisplayID() }) ?? NSScreen.main else { return }
        hideTimer?.invalidate()
        let frame = screen.frame
        let side = min(180.0, frame.width, frame.height)
        let exitY = frame.maxY - min(1, max(0, y)) * frame.height
        let bottom = min(frame.maxY - side, max(frame.minY, exitY - side / 2))
        panel.setFrame(NSRect(x: right ? frame.maxX - side : frame.minX, y: bottom, width: side, height: side), display: false)
        let center = CGPoint(x: right ? side : 0, y: exitY - bottom)
        CATransaction.begin(); CATransaction.setDisableActions(true)
        canvas.sublayers?.forEach { $0.removeFromSuperlayer() }
        canvas.contentsScale = screen.backingScaleFactor
        let reduceMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        let duration = reduceMotion ? 0.2 : 0.46
        for i in 0..<3 {
            let radius: CGFloat = i == 0 ? 78 : i == 1 ? 61 : 43
            let bubble = CAShapeLayer()
            bubble.bounds = CGRect(x: -radius, y: -radius, width: radius * 2, height: radius * 2)
            bubble.position = center
            bubble.path = CGPath(ellipseIn: CGRect(x: -radius * 0.72, y: -radius, width: radius * 1.44, height: radius * 2), transform: nil)
            bubble.fillColor = NSColor(calibratedRed: 0.25, green: 0.78, blue: 1, alpha: i == 0 ? 0.12 : 0.05).cgColor
            bubble.strokeColor = NSColor(calibratedRed: 0.55, green: 0.91, blue: 1, alpha: i == 0 ? 0.9 : 0.5).cgColor
            bubble.lineWidth = i == 0 ? 2 : 1
            bubble.opacity = 0
            canvas.addSublayer(bubble)
            let expansion = CABasicAnimation(keyPath: "transform.scale")
            expansion.fromValue = reduceMotion ? 1 : 0.12 + Double(i) * 0.04; expansion.toValue = 1
            expansion.timingFunction = CAMediaTimingFunction(name: .easeOut)
            let fade = CAKeyframeAnimation(keyPath: "opacity")
            fade.values = [0, 1, 0.7, 0]; fade.keyTimes = [0, 0.12, 0.5, 1]
            let animation = CAAnimationGroup(); animation.animations = [expansion, fade]; animation.duration = duration
            bubble.add(animation, forKey: "departure")
        }
        CATransaction.commit()
        panel.orderFrontRegardless()
        let timer = Timer(timeInterval: duration + 0.02, repeats: false) { [weak self] _ in self?.hide() }
        hideTimer = timer; RunLoop.main.add(timer, forMode: .common)
    }
    func hide() { hideTimer?.invalidate(); hideTimer = nil; panel.orderOut(nil); canvas.sublayers?.forEach { $0.removeFromSuperlayer() } }
}
