import AppKit
import ApplicationServices
import Network
import Foundation

final class Receiver: NSObject, NSApplicationDelegate {
    var window: NSWindow!
    var edgeBubble: EdgeBubble!
    var deskPreview: DeskPreview?
    let status = NSTextField(wrappingLabelWithString: "Stopped. Enable Accessibility, then start receiving.")
    let keyField = NSTextField()
    let startButton = NSButton(title: "Start receiving", target: nil, action: nil)
    let fileStatus = NSTextField(wrappingLabelWithString: "Connect Windows, then copy text or files to share them.")
    var fileServer: FileServer?
    var clipboardSeen = 0
    var textEnabled = false
    var listener: NWListener?
    let discovery = DiscoveryResponder()
    var connection: NWConnection?
    var wire: Wire?
    var buffer = Data()
    var token = Data()
    var authenticated = false
    var active = false
    var macOnRight = true
    var point = CGPoint.zero
    var buttons = Set<Int>()
    var lastSeen = Date()
    var timer: Timer?
    var displayBounds = CGRect.zero
    var receivingActivity: NSObjectProtocol?
    var lastClick = Date.distantPast
    var lastClickPoint = CGPoint.zero
    var lastClickButton = -1
    var clickCount: Int64 = 1

    func applicationDidFinishLaunching(_ notification: Notification) {
        edgeBubble = EdgeBubble()
        NSApp.setActivationPolicy(.regular)
        let menu = NSMenu()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit Seamlet", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let item = NSMenuItem(); item.submenu = appMenu; menu.addItem(item); NSApp.mainMenu = menu
        BrandUI.build(self)
        window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
            guard let self, self.connection != nil else { return }
            if Date().timeIntervalSince(self.lastSeen) > 3 { self.disconnect("Connection timed out. Waiting for Windows…"); return }
            // These system queries must not run for every mouse sample (up to
            // thousands per second). Still notice revoked permission promptly.
            if self.authenticated {
                self.checkClipboard()
                guard AXIsProcessTrusted() else { self.disconnect("Accessibility permission was removed."); return }
                self.displayBounds = CGDisplayBounds(CGMainDisplayID())
            }
        }
        RunLoop.main.add(timer!, forMode: .common)
    }

    func startFiles() -> [String: Any] {
        let server = FileServer()
        server.beginReceive = { [weak self, weak server] in
            DispatchQueue.main.sync { self?.fileServer === server ? NSPasteboard.general.changeCount : -1 }
        }
        server.received = { [weak self, weak server] paths, baseline in
            DispatchQueue.main.sync {
                guard let self, self.fileServer === server, baseline == NSPasteboard.general.changeCount else { return false }
                let board = NSPasteboard.general; board.clearContents()
                let success = board.writeObjects(paths.map { $0 as NSURL })
                self.clipboardSeen = board.changeCount
                server?.offer(UUID().uuidString, [])
                self.fileStatus.stringValue = success ? "Files ready to paste here (Cmd+V in Finder)." : "Could not update the clipboard. Copy the files again."
                return success
            }
        }
        server.receivedText = { [weak self, weak server] text, baseline in
            DispatchQueue.main.sync {
                guard let self, self.textEnabled, self.fileServer === server, baseline == NSPasteboard.general.changeCount else { return false }
                let board = NSPasteboard.general; board.clearContents()
                let success = board.setString(text, forType: .string)
                self.clipboardSeen = board.changeCount
                server?.offer(UUID().uuidString, [])
                self.fileStatus.stringValue = success ? "Text ready to paste here (Cmd+V)." : "Could not update the clipboard. Copy the text again."
                return success
            }
        }
        server.status = { [weak self, weak server] text in
            DispatchQueue.main.async { if let self, self.fileServer === server { self.fileStatus.stringValue = text } }
        }
        do {
            try server.start(); fileServer = server; clipboardSeen = NSPasteboard.general.changeCount
            let retained = (NSPasteboard.general.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
            DispatchQueue.global(qos: .utility).async { FileTransfer.cleanOldCache(keeping: retained) }
            fileStatus.stringValue = "Clipboard connected. Copy text or files, wait for Ready, then paste on the other computer."
            return ["filesKey": server.key.base64EncodedString()]
        } catch { fileStatus.stringValue = "File sharing could not start. Restart both apps and try again."; return [:] }
    }
    func checkClipboard() {
        guard let server = fileServer else { return }
        let board = NSPasteboard.general
        guard board.changeCount != clipboardSeen else { return }
        let snapshot = board.changeCount
        let paths = (board.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
        let text = paths.isEmpty ? board.string(forType: .string) : nil
        guard board.changeCount == snapshot else { return }
        clipboardSeen = snapshot
        let id = UUID().uuidString
        // Any newer local clipboard selection invalidates the previous offer.
        server.offer(id, [])
        if paths.isEmpty {
            guard let text else { return }
            guard textEnabled else { fileStatus.stringValue = "Update the Windows app to enable text copying."; return }
            guard text.utf8.count <= FileTransfer.maxTextBytes else { fileStatus.stringValue = "Text copies are limited to 1 MB."; return }
            server.offerText(id, text); send(["type": "files", "id": id, "kind": "text"])
            fileStatus.stringValue = "Sharing copied text with Windows…"
            return
        }
        guard !paths.allSatisfy({ $0.path.hasPrefix(FileTransfer.cacheRoot.path + "/") }) else { return }
        server.offer(id, paths); send(["type": "files", "id": id]); fileStatus.stringValue = "Sharing copied files with Windows…"
    }

    func localAddresses() -> [String] {
        var result: [String] = []; var first: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&first) == 0 else { return [] }; defer { freeifaddrs(first) }
        var next = first
        while let p = next {
            defer { next = p.pointee.ifa_next }
            guard let address = p.pointee.ifa_addr, address.pointee.sa_family == UInt8(AF_INET),
                  p.pointee.ifa_flags & UInt32(IFF_LOOPBACK) == 0 else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(address, socklen_t(address.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 { result.append(String(cString: host)) }
        }
        return result
    }
    @objc func copyKey() { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(keyField.stringValue, forType: .string) }
    @objc func accessibility() {
        _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
    @objc func toggle() {
        if listener != nil { stop(); return }
        do { token = try Wire.pairingToken(keyField.stringValue) }
        catch { status.stringValue = "Enter a pairing code (up to 256 UTF-8 bytes). Use the same capitalization on Windows."; return }
        guard AXIsProcessTrusted() else { status.stringValue = "Enable Seamlet in Accessibility settings, then try again."; accessibility(); return }
        do {
            let tcp = NWProtocolTCP.Options()
            tcp.noDelay = true
            tcp.disableAckStretching = true
            let parameters = NWParameters(tls: nil, tcp: tcp)
            parameters.allowLocalEndpointReuse = true
            let server = try NWListener(using: parameters, on: 24872)
            listener = server
            server.stateUpdateHandler = { [weak self, weak server] state in
                guard let self, self.listener === server else { return }
                switch state {
                case .ready:
                    do {
                        try self.discovery.start(name: Host.current().localizedName ?? "Mac")
                        self.status.stringValue = "Ready. Select this Mac in the Windows device dropdown."
                    } catch { self.status.stringValue = "Receiving, but discovery is unavailable. Enter this Mac’s IP on Windows." }
                case .failed(let error): self.stop(); self.status.stringValue = "Cannot listen: \(error.localizedDescription)"
                default: break
                }
            }
            server.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            server.start(queue: .main); startButton.title = "Stop receiving"; keyField.isEditable = false
        } catch { status.stringValue = error.localizedDescription }
    }
    func accept(_ peer: NWConnection) {
        guard connection == nil else { peer.cancel(); return }
        connection = peer; buffer = Data(); lastSeen = Date(); authenticated = false; wire = nil
        peer.stateUpdateHandler = { [weak self, weak peer] state in
            guard let self, self.connection === peer else { return }
            switch state { case .failed, .cancelled: self.disconnect("Disconnected. Waiting for Windows…"); default: break }
        }
        peer.start(queue: .main); receive(peer)
    }
    func receive(_ peer: NWConnection) {
        peer.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self, weak peer] data, _, complete, error in
            guard let self, let peer, self.connection === peer else { return }
            if let data { self.buffer.append(data) }
            do {
                while let end = self.buffer.firstIndex(of: 10) {
                    guard end < 8192 else { throw WireError.invalid }
                    let line = Data(self.buffer.prefix(upTo: end)); self.buffer.removeSubrange(...end)
                    try self.handle(line)
                }
                guard self.buffer.count < 8192 else { throw WireError.invalid }
            } catch { self.disconnect("Pairing or protocol failed. Enter the same code on both computers."); return }
            if complete || error != nil { self.disconnect("Disconnected. Waiting for Windows…") }
            else { self.receive(peer) }
        }
    }
    func rawSend(_ data: Data) {
        guard let peer = connection else { return }
        peer.send(content: data, completion: .contentProcessed { [weak self, weak peer] error in
            guard let self, self.connection === peer, error != nil else { return }
            self.disconnect("Connection lost. Waiting for Windows…")
        })
    }
    func send(_ message: [String: Any]) {
        do { guard let wire else { return }; rawSend(try wire.seal(message)) }
        catch { disconnect("Connection encryption failed.") }
    }
    func handle(_ line: Data) throws {
        if wire == nil {
            guard let hello = try JSONSerialization.jsonObject(with: line) as? [String: Any],
                  hello["v"] as? Int == 1, let value = hello["nonce"] as? String,
                  let client = Data(base64Encoded: value), client.count == 32 else { throw WireError.invalid }
            let server = Wire.random()
            wire = try Wire(token: token, client: client, server: server, isServer: true)
            rawSend(try JSONSerialization.data(withJSONObject: ["v": 1, "nonce": server.base64EncodedString()]) + Data([10]))
            return
        }
        let message = try wire!.open(line)
        guard let type = message["type"] as? String else { throw WireError.invalid }
        if !authenticated {
            guard type == "hello" else { throw WireError.invalid }
            guard AXIsProcessTrusted() else { disconnect("Accessibility permission was removed."); return }
            displayBounds = CGDisplayBounds(CGMainDisplayID())
            // The receiver is usually hidden behind the application being
            // controlled. Keep App Nap from delaying input while paired,
            // without preventing the user from putting the Mac to sleep.
            receivingActivity = ProcessInfo.processInfo.beginActivity(options: .userInitiatedAllowingIdleSystemSleep, reason: "Receive mouse input from paired Windows computer")
            authenticated = true; deskPreview?.connected = true; lastSeen = Date()
            textEnabled = message["text"] as? Int == 1
            var ready: [String: Any] = message["files"] as? Int == 1 ? startFiles() : [:]
            if textEnabled && fileServer != nil { ready["text"] = 1 }
            ready["type"] = "ready"; send(ready)
            status.stringValue = "Paired. Move through the configured Windows screen edge."; return
        }
        lastSeen = Date()
        // Geometry is refreshed by the watchdog; do not query WindowServer
        // synchronously while the pointer is crossing into this screen.
        let bounds = displayBounds
        func number(_ name: String) throws -> Double {
            guard let n = message[name] as? NSNumber, n.doubleValue.isFinite else { throw WireError.invalid }
            return n.doubleValue
        }
        switch type {
        case "ping": send(["type": "pong"])
        case "enter":
            releaseButtons(); macOnRight = message["right"] as? Bool ?? true; deskPreview?.macOnRight = macOnRight
            let y = min(1, max(0, try number("y")))
            point = CGPoint(x: macOnRight ? bounds.minX + 2 : bounds.maxX - 3, y: bounds.minY + y * (bounds.height - 1))
            active = true
            // Place the visible cursor directly at entry, then post the normal
            // movement event for application hover/interaction notifications.
            CGWarpMouseCursorPosition(point)
            postMove()
            if let id = message["id"] as? Int { send(["type": "entered", "id": id]) }
            status.stringValue = "Windows mouse is controlling this Mac."
        case "leave": releaseButtons(); active = false; status.stringValue = "Mouse returned to Windows."
        case "move":
            guard active else { return }
            let dx = try number("dx"), dy = try number("dy")
            guard abs(dx) <= 10000, abs(dy) <= 10000 else { throw WireError.invalid }
            let x = point.x + dx
            point.y = min(bounds.maxY - 1, max(bounds.minY, point.y + dy))
            // Keep a drag on its original machine until the user releases it.
            if buttons.isEmpty && ((macOnRight && x < bounds.minX) || (!macOnRight && x >= bounds.maxX)) {
                active = false; send(["type": "return", "y": (point.y - bounds.minY) / max(1, bounds.height - 1)])
                let departureY = (point.y - bounds.minY) / max(1, bounds.height - 1)
                let departureRight = !macOnRight
                DispatchQueue.main.async { [weak self] in self?.edgeBubble.play(right: departureRight, y: departureY) }
                status.stringValue = "Mouse returned to Windows."; return
            }
            point.x = min(bounds.maxX - 1, max(bounds.minX, x)); postMove()
        case "button":
            guard active else { return }
            guard let b = message["button"] as? Int, (0...2).contains(b), let down = message["down"] as? Bool else { throw WireError.invalid }
            if down {
                if buttons.contains(b) { return }
                let near = hypot(point.x - lastClickPoint.x, point.y - lastClickPoint.y) < 5
                clickCount = b == lastClickButton && near && Date().timeIntervalSince(lastClick) < NSEvent.doubleClickInterval ? clickCount + 1 : 1
                lastClick = Date(); lastClickPoint = point; lastClickButton = b; buttons.insert(b)
            } else { guard buttons.remove(b) != nil else { return } }
            postButton(b, down)
        case "scroll":
            guard active else { return }
            let delta = try number("delta"), horizontal = message["horizontal"] as? Bool ?? false
            guard abs(delta) <= 32768 else { throw WireError.invalid }
            let amount = Int32((delta / 120 * 3).rounded())
            let event = CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 2, wheel1: horizontal ? 0 : amount, wheel2: horizontal ? -amount : 0, wheel3: 0)
            event?.location = point; event?.post(tap: .cghidEventTap)
        default: throw WireError.invalid
        }
    }
    func postMove() {
        let b = buttons.contains(0) ? 0 : buttons.contains(1) ? 1 : buttons.contains(2) ? 2 : -1
        let type: CGEventType = b == 0 ? .leftMouseDragged : b == 1 ? .rightMouseDragged : b == 2 ? .otherMouseDragged : .mouseMoved
        let event = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: point, mouseButton: CGMouseButton(rawValue: UInt32(max(0, b)))!)
        event?.post(tap: .cghidEventTap)
    }
    func postButton(_ b: Int, _ down: Bool) {
        let types: [(CGEventType, CGEventType)] = [(.leftMouseDown, .leftMouseUp), (.rightMouseDown, .rightMouseUp), (.otherMouseDown, .otherMouseUp)]
        let event = CGEvent(mouseEventSource: nil, mouseType: down ? types[b].0 : types[b].1, mouseCursorPosition: point, mouseButton: CGMouseButton(rawValue: UInt32(b))!)
        event?.setIntegerValueField(.mouseEventClickState, value: clickCount); event?.post(tap: .cghidEventTap)
    }
    func releaseButtons() { for b in buttons { postButton(b, false) }; buttons.removeAll() }
    func disconnect(_ text: String) {
        fileServer?.stop(); fileServer = nil; textEnabled = false; fileStatus.stringValue = "File sharing disconnected."
        releaseButtons(); active = false; authenticated = false; deskPreview?.connected = false; wire = nil; buffer = Data()
        if let activity = receivingActivity { ProcessInfo.processInfo.endActivity(activity); receivingActivity = nil }
        let old = connection; connection = nil; old?.cancel(); status.stringValue = text
    }
    func stop() { edgeBubble?.hide(); discovery.stop(); let old = listener; listener = nil; old?.cancel(); disconnect("Stopped. You can change the pairing code before starting again."); startButton.title = "Start receiving"; keyField.isEditable = true; token = Data() }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) { stop() }
}

let delegate = Receiver()
NSApplication.shared.delegate = delegate
NSApplication.shared.run()
