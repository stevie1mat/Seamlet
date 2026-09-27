import Foundation
import CryptoKit
import CommonCrypto

enum WireError: Error { case invalid }

// Each connection has fresh randomness from BOTH peers. Keys are directional;
// monotonically increasing nonces also reject replay and reordering.
final class Wire {
    // Human-entered codes are stretched once when receiving starts, never on
    // the mouse-input path. Both apps normalize and derive the same 32 bytes.
    static func pairingToken(_ code: String) throws -> Data {
        let normalized = code.trimmingCharacters(in: CharacterSet(charactersIn: " \t\r\n")).precomposedStringWithCanonicalMapping
        let password = Array(normalized.utf8)
        guard !password.isEmpty, password.count <= 256 else { throw WireError.invalid }
        let salt = Array("MultipleMouse/pairing-code/v1".utf8)
        var result = [UInt8](repeating: 0, count: 32)
        let status = password.withUnsafeBytes { bytes in
            salt.withUnsafeBufferPointer { saltBytes in
                CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), bytes.baseAddress!.assumingMemoryBound(to: Int8.self), password.count,
                                     saltBytes.baseAddress!, salt.count, CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), 600_000, &result, result.count)
            }
        }
        guard status == kCCSuccess else { throw WireError.invalid }
        return Data(result)
    }
    let outgoing: SymmetricKey
    let incoming: SymmetricKey
    var sent: UInt64 = 0
    var received: UInt64 = 0

    init(token: Data, client: Data, server: Data, isServer: Bool) throws {
        guard token.count == 32, client.count == 32, server.count == 32 else { throw WireError.invalid }
        func key(_ direction: String) -> SymmetricKey {
            let material = Data(("MultipleMouse/1/" + direction).utf8) + client + server
            return SymmetricKey(data: Data(HMAC<SHA256>.authenticationCode(for: material, using: SymmetricKey(data: token))))
        }
        outgoing = key(isServer ? "s2c" : "c2s")
        incoming = key(isServer ? "c2s" : "s2c")
    }

    static func random() -> Data {
        var bytes = [UInt8](repeating: 0, count: 32)
        precondition(SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess)
        return Data(bytes)
    }

    static func nonce(_ counter: UInt64) -> Data {
        var big = counter.bigEndian
        return Data(repeating: 0, count: 4) + withUnsafeBytes(of: &big) { Data($0) }
    }

    func seal(_ message: [String: Any]) throws -> Data {
        guard sent < UInt64.max else { throw WireError.invalid }
        let plain = try JSONSerialization.data(withJSONObject: message, options: [.sortedKeys])
        let box = try AES.GCM.seal(plain, using: outgoing, nonce: AES.GCM.Nonce(data: Self.nonce(sent)))
        sent += 1
        return Data((box.combined!.base64EncodedString() + "\n").utf8)
    }

    func open(_ line: Data) throws -> [String: Any] {
        guard received < UInt64.max, let bytes = Data(base64Encoded: line), bytes.count >= 28,
              bytes.prefix(12) == Self.nonce(received) else { throw WireError.invalid }
        let plain = try AES.GCM.open(AES.GCM.SealedBox(combined: bytes), using: incoming)
        guard let message = try JSONSerialization.jsonObject(with: plain) as? [String: Any] else { throw WireError.invalid }
        received += 1
        return message
    }
}
