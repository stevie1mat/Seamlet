import AppKit

private func ink(_ hex: Int) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 255) / 255, green: CGFloat((hex >> 8) & 255) / 255, blue: CGFloat(hex & 255) / 255, alpha: 1)
}

enum BrandUI {
    static let foreground = ink(0x173536)
    static let muted = ink(0x607171)
    static let accent = ink(0x137968)
    static let paper = ink(0xF4F3EE)
    static func label(_ text: String, size: CGFloat = 13, weight: NSFont.Weight = .regular, color: NSColor = muted) -> NSTextField {
        let view = NSTextField(wrappingLabelWithString: text)
        view.font = .systemFont(ofSize: size, weight: weight); view.textColor = color
        return view
    }
    static func column(_ items: [NSView], spacing: CGFloat = 12) -> NSStackView {
        let stack = NSStackView(views: items); stack.orientation = .vertical
        stack.alignment = .leading; stack.spacing = spacing
        return stack
    }
    static func card(_ items: [NSView]) -> NSView {
        let card = NSView(); card.wantsLayer = true; card.layer?.backgroundColor = NSColor.white.cgColor
        card.layer?.cornerRadius = 16; card.layer?.borderWidth = 1; card.layer?.borderColor = ink(0xE1E6E0).cgColor
        let stack = column(items, spacing: 10); stack.translatesAutoresizingMaskIntoConstraints = false
        card.addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 20), stack.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -20), stack.topAnchor.constraint(equalTo: card.topAnchor, constant: 18), stack.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -18)])
        for item in items { item.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true }
        return card
    }
    static func build(_ receiver: Receiver) {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 700, height: 780), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        receiver.window = window; window.title = "Seamlet"; window.center()
        window.appearance = NSAppearance(named: .aqua); window.backgroundColor = paper
        let header = NSStackView(); header.orientation = .horizontal; header.spacing = 12
        let icon = NSImageView(); icon.image = NSImage(contentsOf: Bundle.main.url(forResource: "seamlet-icon", withExtension: "png") ?? URL(fileURLWithPath: "assets/brand/seamlet-icon.png"))
        icon.imageScaling = .scaleProportionallyUpOrDown; icon.widthAnchor.constraint(equalToConstant: 54).isActive = true; icon.heightAnchor.constraint(equalToConstant: 54).isActive = true
        let wordmark = column([label("Seamlet", size: 25, weight: .bold, color: foreground), label("A little less between you and your screens.", size: 12)], spacing: 3)
        header.addArrangedSubview(icon); header.addArrangedSubview(wordmark)
        let hero = column([label("MAC RECEIVER", size: 10, weight: .bold, color: accent), label("Your desk, connected.", size: 30, weight: .bold, color: foreground), label("Bring your Windows mouse, copied text, and files onto this Mac.")], spacing: 6)
        let diagram = DeskPreview(); diagram.heightAnchor.constraint(equalToConstant: 128).isActive = true; receiver.deskPreview = diagram
        receiver.keyField.placeholderString = "Choose a code to use on both computers"
        receiver.keyField.font = .systemFont(ofSize: 14); receiver.keyField.bezelStyle = .roundedBezel
        receiver.keyField.heightAnchor.constraint(equalToConstant: 30).isActive = true
        receiver.keyField.setAccessibilityLabel("Pairing code")
        receiver.startButton.target = receiver; receiver.startButton.action = #selector(Receiver.toggle)
        receiver.startButton.bezelStyle = .rounded; receiver.startButton.controlSize = .large; receiver.startButton.bezelColor = accent; receiver.startButton.contentTintColor = .white
        let access = NSButton(title: "Accessibility settings…", target: receiver, action: #selector(Receiver.accessibility)); access.bezelStyle = .rounded
        let copy = NSButton(title: "Copy code", target: receiver, action: #selector(Receiver.copyKey)); copy.bezelStyle = .rounded
        let buttons = NSStackView(views: [receiver.startButton, copy, access]); buttons.spacing = 10
        let addresses = receiver.localAddresses().joined(separator: ", ")
        let setup = card([label("01  /  PAIR YOUR COMPUTERS", size: 10, weight: .bold, color: accent), label("One code. Both computers.", size: 17, weight: .semibold, color: foreground), receiver.keyField, buttons, label("Start receiving, then select this Mac on Windows.\nManual IP: \(addresses.isEmpty ? "See Wi-Fi settings" : addresses)", size: 12)])
        receiver.status.font = .systemFont(ofSize: 13); receiver.status.textColor = foreground
        receiver.fileStatus.font = .systemFont(ofSize: 13); receiver.fileStatus.textColor = muted
        let live = card([label("02  /  CONNECTION & CLIPBOARD", size: 10, weight: .bold, color: accent), receiver.status, receiver.fileStatus])
        let footer = label("Ctrl + Alt + Esc returns the mouse to Windows.\nUse each computer’s keyboard to copy and paste. Closing Seamlet stops sharing.", size: 11)
        let stack = column([header, hero, diagram, setup, live, footer], spacing: 18)
        stack.translatesAutoresizingMaskIntoConstraints = false; window.contentView!.addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor, constant: 28), stack.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor, constant: -28), stack.topAnchor.constraint(equalTo: window.contentView!.topAnchor, constant: 24), stack.bottomAnchor.constraint(lessThanOrEqualTo: window.contentView!.bottomAnchor, constant: -18)])
        for item in [header, hero, diagram, setup, live, footer] { item.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true }
        window.initialFirstResponder = receiver.keyField
    }
}

final class DeskPreview: NSView {
    var connected = false { didSet { if oldValue != connected { needsDisplay = true } } }
    var macOnRight = true { didSet { if oldValue != macOnRight { needsDisplay = true } } }
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        ink(0xE7EEE7).setFill(); NSBezierPath(roundedRect: bounds, xRadius: 16, yRadius: 16).fill()
        let center = bounds.midX
        let left = NSRect(x: center - 188, y: 20, width: 148, height: 70)
        let right = NSRect(x: center + 40, y: 20, width: 148, height: 70)
        for (frame, title) in [(left, macOnRight ? "Windows · mouse" : "This Mac"), (right, macOnRight ? "This Mac" : "Windows · mouse")] {
            BrandUI.foreground.setFill(); NSBezierPath(roundedRect: frame, xRadius: 9, yRadius: 9).fill()
            ink(0x65D6BB).setFill(); NSBezierPath(roundedRect: frame.insetBy(dx: 7, dy: 7), xRadius: 4, yRadius: 4).fill()
            let text = NSAttributedString(string: title, attributes: [.font: NSFont.systemFont(ofSize: 11, weight: .medium), .foregroundColor: BrandUI.foreground])
            text.draw(at: NSPoint(x: frame.midX - text.size().width / 2, y: 101))
        }
        let line = NSBezierPath(); line.move(to: NSPoint(x: left.maxX + 10, y: 55)); line.line(to: NSPoint(x: right.minX - 10, y: 55)); line.lineWidth = 2
        BrandUI.accent.setStroke(); line.stroke()
        let dot = NSRect(x: center - 15, y: 40, width: 30, height: 30)
        (connected ? BrandUI.accent : NSColor.white).setFill(); NSBezierPath(ovalIn: dot).fill()
        let symbol = NSAttributedString(string: connected ? "↔" : "+", attributes: [.font: NSFont.systemFont(ofSize: 19, weight: .medium), .foregroundColor: connected ? NSColor.white : BrandUI.accent])
        symbol.draw(at: NSPoint(x: center - symbol.size().width / 2, y: 43))
    }
}
