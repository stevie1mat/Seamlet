import Foundation
import Darwin

// Discovery advertises only a device name. Pairing and encrypted control still
// happen on the TCP connection; no pairing material is broadcast.
final class DiscoveryResponder {
    private var source: DispatchSourceRead?
    private var socketFD: Int32 = -1
    private var windowStart = ProcessInfo.processInfo.systemUptime
    private var replies = 0
    private(set) var port: UInt16 = 0

    func start(port requestedPort: UInt16 = 24873, name: String) throws {
        stop()
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { throw POSIXError(.EIO) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = requestedPort.bigEndian
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard bound == 0, fcntl(fd, F_SETFL, O_NONBLOCK) == 0 else {
            let error = POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO); close(fd); throw error
        }
        var size = socklen_t(MemoryLayout<sockaddr_in>.size)
        _ = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &size) }
        }
        port = UInt16(bigEndian: address.sin_port); socketFD = fd
        let reader = DispatchSource.makeReadSource(fileDescriptor: fd, queue: .main)
        reader.setEventHandler { [weak self] in self?.receive(name: String(name.prefix(80))) }
        reader.setCancelHandler { close(fd) }
        source = reader; reader.resume()
    }
    private func receive(name: String) {
        // Limit work per callback so discovery cannot monopolize mouse input.
        for _ in 0..<32 {
            var bytes = [UInt8](repeating: 0, count: 1025)
            var remote = sockaddr_in(); var size = socklen_t(MemoryLayout<sockaddr_in>.size)
            let count = withUnsafeMutablePointer(to: &remote) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(socketFD, &bytes, bytes.count, 0, $0, &size) }
            }
            if count < 0 { return }
            guard count <= 1024, remote.sin_family == sa_family_t(AF_INET), remote.sin_port != 0,
                  let query = try? JSONSerialization.jsonObject(with: Data(bytes.prefix(count))) as? [String: Any],
                  query["service"] as? String == "Seamlet", query["v"] as? Int == 1,
                  query["type"] as? String == "discover", let nonce = query["nonce"] as? String,
                  nonce.count == 32, nonce.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else { continue }
            let now = ProcessInfo.processInfo.systemUptime
            if now - windowStart >= 1 { windowStart = now; replies = 0 }
            guard replies < 20 else { continue }; replies += 1
            guard let response = try? JSONSerialization.data(withJSONObject: ["service": "Seamlet", "v": 1, "type": "offer", "nonce": nonce, "name": name]) else { continue }
            response.withUnsafeBytes { data in
                _ = withUnsafePointer(to: &remote) {
                    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { sendto(socketFD, data.baseAddress, data.count, 0, $0, size) }
                }
            }
        }
    }
    func stop() { source?.cancel(); source = nil; socketFD = -1; port = 0 }
    deinit { stop() }
}
