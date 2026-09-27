import Foundation
import CryptoKit
import Darwin

enum FileCopyError: Error { case invalid, io }

// Blocking sockets live only on transfer workers, never on the mouse/UI queue.
final class FileSocket {
    let fd: Int32
    var buffer = Data()
    init(_ fd: Int32) {
        self.fd = fd
        var timeout = timeval(tv_sec: 20, tv_usec: 0); var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &yes, socklen_t(MemoryLayout<Int32>.size))
    }
    func interrupt() { shutdown(fd, SHUT_RDWR) }
    deinit { close(fd) }
    func line(limit: Int = 131072) throws -> Data {
        while true {
            if let end = buffer.firstIndex(of: 10) {
                let count = buffer.distance(from: buffer.startIndex, to: end)
                guard count <= limit else { throw FileCopyError.invalid }
                let result = Data(buffer.prefix(count)); buffer.removeFirst(count + 1); return result
            }
            guard buffer.count <= limit else { throw FileCopyError.invalid }
            var bytes = [UInt8](repeating: 0, count: 16384)
            let count = recv(fd, &bytes, bytes.count, 0)
            guard count > 0 else { throw FileCopyError.io }; buffer.append(contentsOf: bytes.prefix(count))
        }
    }
    func write(_ data: Data) throws {
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.send(fd, bytes.baseAddress!.advanced(by: offset), bytes.count - offset, 0)
                guard count > 0 else { throw FileCopyError.io }; offset += count
            }
        }
    }
}

struct FileEntry: Codable {
    let name: String
    let size: Int64
    var json: [String: Any] { ["name": name, "size": size] }
}

enum FileTransfer {
    static let maxBytes: Int64 = 1024 * 1024 * 1024
    static let chunkSize = 32768
    static let cacheRoot = FileManager.default.temporaryDirectory.appendingPathComponent("MultipleMouse-Files", isDirectory: true)
    static func cleanOldCache(keeping paths: [URL]) {
        let keep = Set(paths.map { $0.deletingLastPathComponent().path })
        guard let folders = try? FileManager.default.contentsOfDirectory(at: cacheRoot, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey, .contentModificationDateKey]) else { return }
        for folder in folders {
            guard UUID(uuidString: folder.lastPathComponent) != nil, !keep.contains(folder.path),
                  let values = try? folder.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey, .contentModificationDateKey]),
                  values.isDirectory == true, values.isSymbolicLink != true,
                  let modified = values.contentModificationDate, modified < Date().addingTimeInterval(-86400) else { continue }
            try? FileManager.default.removeItem(at: folder)
        }
    }
    static func validName(_ name: String) -> Bool {
        if name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || name == "." || name == ".." || name.utf8.count > 240 || name.hasSuffix(".") || name.hasSuffix(" ") { return false }
        if name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || "<>:\"/\\|?*".unicodeScalars.contains($0) }) { return false }
        let stem = String(name.split(separator: ".", omittingEmptySubsequences: false)[0]).uppercased()
        if ["CON", "PRN", "AUX", "NUL", "CLOCK$"].contains(stem) { return false }
        if stem.count == 4 && (stem.hasPrefix("COM") || stem.hasPrefix("LPT")) && "123456789¹²³".contains(stem.last!) { return false }
        return true
    }
    static func validate(_ entries: [FileEntry]) throws {
        guard (1...100).contains(entries.count) else { throw FileCopyError.invalid }
        var names = Set<String>(); var total: Int64 = 0
        for entry in entries {
            guard validName(entry.name), names.insert(entry.name.precomposedStringWithCanonicalMapping.uppercased()).inserted,
                  entry.size >= 0, entry.size <= maxBytes else { throw FileCopyError.invalid }
            total += entry.size
        }
        guard total <= maxBytes else { throw FileCopyError.invalid }
    }
    static func describe(_ paths: [URL]) throws -> [FileEntry] {
        let entries = try paths.map { path -> FileEntry in
            let values = try path.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true, let size = values.fileSize else { throw FileCopyError.invalid }
            return FileEntry(name: path.lastPathComponent.precomposedStringWithCanonicalMapping, size: Int64(size))
        }
        try validate(entries); return entries
    }
    static func read(_ socket: FileSocket, _ wire: Wire) throws -> [String: Any] {
        let message = try wire.open(socket.line())
        guard message["type"] as? String != "error" else { throw FileCopyError.invalid }; return message
    }
    static func sendFiles(_ socket: FileSocket, _ wire: Wire, _ paths: [URL], _ entries: [FileEntry], valid: () -> Bool) throws {
        for (index, path) in paths.enumerated() {
            let fd = Darwin.open(path.path, O_RDONLY | O_NOFOLLOW)
            guard fd >= 0 else { throw FileCopyError.io }
            let input = FileHandle(fileDescriptor: fd, closeOnDealloc: true); defer { try? input.close() }
            var details = stat()
            guard fstat(fd, &details) == 0, details.st_mode & S_IFMT == S_IFREG else { throw FileCopyError.invalid }
            guard try input.seekToEnd() == UInt64(entries[index].size) else { throw FileCopyError.invalid }
            try input.seek(toOffset: 0)
            var hash = SHA256(); var sent: Int64 = 0
            while sent < entries[index].size {
                guard valid() else { throw FileCopyError.invalid }
                let data = try input.read(upToCount: min(chunkSize, Int(entries[index].size - sent))) ?? Data()
                guard !data.isEmpty else { throw FileCopyError.io }; sent += Int64(data.count); hash.update(data: data)
                try socket.write(wire.seal(["type": "chunk", "index": index, "data": data.base64EncodedString()]))
            }
            try socket.write(wire.seal(["type": "fileEnd", "index": index, "sha256": hash.finalize().map { String(format: "%02x", $0) }.joined()]))
        }
        guard valid() else { throw FileCopyError.invalid }; try socket.write(wire.seal(["type": "end"]))
    }
    static let maxTextBytes = 1024 * 1024
    static func sendText(_ socket: FileSocket, _ wire: Wire, _ text: String, valid: () -> Bool) throws {
        let data = Data(text.utf8)
        guard data.count <= maxTextBytes else { throw FileCopyError.invalid }
        for offset in stride(from: 0, to: data.count, by: chunkSize) {
            guard valid() else { throw FileCopyError.invalid }
            try socket.write(wire.seal(["type": "textChunk", "data": data.subdata(in: offset..<min(data.count, offset + chunkSize)).base64EncodedString()]))
        }
        guard valid() else { throw FileCopyError.invalid }
        try socket.write(wire.seal(["type": "end"]))
    }
    static func receiveText(_ socket: FileSocket, _ wire: Wire, _ size: Int) throws -> String {
        guard (0...maxTextBytes).contains(size) else { throw FileCopyError.invalid }
        var data = Data()
        while data.count < size {
            let chunk = try read(socket, wire)
            guard chunk["type"] as? String == "textChunk", let value = chunk["data"] as? String,
                  let bytes = Data(base64Encoded: value), !bytes.isEmpty, bytes.count <= chunkSize,
                  data.count + bytes.count <= size else { throw FileCopyError.invalid }
            data.append(bytes)
        }
        guard try read(socket, wire)["type"] as? String == "end", let text = String(data: data, encoding: .utf8) else { throw FileCopyError.invalid }
        return text
    }
    static func receiveFiles(_ socket: FileSocket, _ wire: Wire, _ entries: [FileEntry]) throws -> [URL] {
        try validate(entries)
        let folder = cacheRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        var complete = false
        defer { if !complete { try? FileManager.default.removeItem(at: folder) } }
        var paths: [URL] = []
        for (index, entry) in entries.enumerated() {
            let path = folder.appendingPathComponent(entry.name)
            let fd = Darwin.open(path.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
            guard fd >= 0 else { throw FileCopyError.io }
            let output = FileHandle(fileDescriptor: fd, closeOnDealloc: true); defer { try? output.close() }
            var hash = SHA256(); var received: Int64 = 0
            while true {
                let item = try read(socket, wire)
                guard item["index"] as? Int == index else { throw FileCopyError.invalid }
                if item["type"] as? String == "fileEnd" {
                    guard received == entry.size, item["sha256"] as? String == hash.finalize().map({ String(format: "%02x", $0) }).joined() else { throw FileCopyError.invalid }; break
                }
                guard item["type"] as? String == "chunk", let text = item["data"] as? String, let data = Data(base64Encoded: text),
                      !data.isEmpty, data.count <= chunkSize, received + Int64(data.count) <= entry.size else { throw FileCopyError.invalid }
                try output.write(contentsOf: data); hash.update(data: data); received += Int64(data.count)
            }
            try output.close(); paths.append(path)
        }
        guard try read(socket, wire)["type"] as? String == "end" else { throw FileCopyError.invalid }
        complete = true; return paths
    }
}

// One short-lived socket per upload/download. Only the currently paired mouse
// session receives this service's random secret over its encrypted control link.
final class FileServer {
    private let lock = NSLock()
    private var listener: FileSocket?
    private var clients: [UUID: FileSocket] = [:]
    private var offerID = ""
    private var offered: [URL] = []
    private var offeredText: String?
    private var stopped = false
    let key = Wire.random()
    private(set) var port: UInt16 = 0
    var beginReceive: () -> Int = { 0 }
    var received: ([URL], Int) -> Bool = { _, _ in false }
    var receivedText: (String, Int) -> Bool = { _, _ in false }
    var status: (String) -> Void = { _ in }
    func offer(_ id: String, _ paths: [URL]) { lock.lock(); offerID = id; offered = paths; offeredText = nil; lock.unlock() }
    func offerText(_ id: String, _ text: String) { lock.lock(); offerID = id; offered = []; offeredText = text; lock.unlock() }
    func start(port requestedPort: UInt16 = 24874) throws {
        let fd = socket(AF_INET, SOCK_STREAM, 0); guard fd >= 0 else { throw FileCopyError.io }
        let listening = FileSocket(fd); var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in(); address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size); address.sin_family = sa_family_t(AF_INET); address.sin_port = requestedPort.bigEndian
        let bound = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        guard bound == 0, listen(fd, 4) == 0 else { throw FileCopyError.io }
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        _ = withUnsafeMutablePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) } }
        port = UInt16(bigEndian: address.sin_port); listener = listening
        DispatchQueue.global(qos: .utility).async { [weak self, listening] in
            while let self {
                self.lock.lock(); let closed = self.stopped; self.lock.unlock(); if closed { break }
                let peerFD = accept(listening.fd, nil, nil)
                if peerFD < 0 { continue }
                let peer = FileSocket(peerFD); let id = UUID()
                self.lock.lock()
                let allowed = !self.stopped && self.clients.count < 4
                if allowed { self.clients[id] = peer }; self.lock.unlock()
                if allowed {
                    DispatchQueue.global(qos: .utility).async { [weak self, peer] in
                        guard let self else { return }
                        self.serve(peer)
                        self.lock.lock(); self.clients.removeValue(forKey: id); self.lock.unlock()
                    }
                }
            }
        }
    }
    private func serve(_ socket: FileSocket) {
        var wire: Wire?
        var authenticatedRequest = false
        do {
            guard let hello = try JSONSerialization.jsonObject(with: socket.line(limit: 1024)) as? [String: Any], hello["v"] as? Int == 1,
                  let text = hello["nonce"] as? String, let clientNonce = Data(base64Encoded: text), clientNonce.count == 32 else { throw FileCopyError.invalid }
            let serverNonce = Wire.random()
            try socket.write(JSONSerialization.data(withJSONObject: ["v": 1, "nonce": serverNonce.base64EncodedString()]) + Data([10]))
            let session = try Wire(token: key, client: clientNonce, server: serverNonce, isServer: true); wire = session
            let request = try FileTransfer.read(socket, session)
            authenticatedRequest = true
            switch request["type"] as? String {
            case "putText":
                guard let size = request["size"] as? Int, (0...FileTransfer.maxTextBytes).contains(size) else { throw FileCopyError.invalid }
                let baseline = beginReceive()
                try socket.write(session.seal(["type": "ready"]))
                let text = try FileTransfer.receiveText(socket, session, size)
                lock.lock(); let alive = !stopped; lock.unlock()
                let applied = alive && receivedText(text, baseline)
                try socket.write(session.seal(["type": "done", "applied": applied]))
            case "getText":
                guard let id = request["id"] as? String else { throw FileCopyError.invalid }
                lock.lock(); let text = id == offerID ? offeredText : nil; lock.unlock()
                guard let text, text.utf8.count <= FileTransfer.maxTextBytes else { throw FileCopyError.invalid }
                try socket.write(session.seal(["type": "textBegin", "size": text.utf8.count]))
                try FileTransfer.sendText(socket, session, text) {
                    self.lock.lock(); defer { self.lock.unlock() }; return !self.stopped && self.offerID == id
                }
            case "put":
                guard let raw = request["entries"] else { throw FileCopyError.invalid }
                let entries = try JSONDecoder().decode([FileEntry].self, from: JSONSerialization.data(withJSONObject: raw)); try FileTransfer.validate(entries)
                let baseline = beginReceive(); status("Receiving copied files from Windows…")
                try socket.write(session.seal(["type": "ready"]))
                let paths = try FileTransfer.receiveFiles(socket, session, entries)
                lock.lock(); let alive = !stopped; lock.unlock()
                let applied = alive && received(paths, baseline)
                try socket.write(session.seal(["type": "done", "applied": applied]))
            case "get":
                guard let id = request["id"] as? String else { throw FileCopyError.invalid }
                lock.lock(); let paths = id == offerID ? offered : []; lock.unlock()
                let entries = try FileTransfer.describe(paths); status("Syncing copied files to Windows…")
                try socket.write(session.seal(["type": "begin", "entries": entries.map(\.json)]))
                try FileTransfer.sendFiles(socket, session, paths, entries) {
                    self.lock.lock(); defer { self.lock.unlock() }; return !self.stopped && self.offerID == id
                }
                status("Files sent to Windows. Paste with Ctrl+V when ready.")
            default: throw FileCopyError.invalid
            }
        } catch {
            if let wire { try? socket.write(wire.seal(["type": "error"])) }
            // Authentication failures should not display a misleading file error.
            if authenticatedRequest { status("Clipboard transfer stopped. Copy again to retry (text up to 1 MB; files up to 1 GB).") }
        }
    }
    func stop() {
        lock.lock(); stopped = true; offered = []; offeredText = nil; listener?.interrupt(); for peer in clients.values { peer.interrupt() }; listener = nil; lock.unlock()
    }
    deinit { stop() }
}
