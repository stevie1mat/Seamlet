import Foundation

@main struct ProtocolTests {
    static func main() throws {
        let token = try Wire.pairingToken("Mouse123"), client = Data(32..<64), server = Data(64..<96)
        let incoming = try Wire(token: token, client: client, server: server, isServer: true)
        let line = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
        let message = try incoming.open(Data(line.utf8))
        precondition(message["dx"] as? Int == -13 && message["dy"] as? Int == 9)
        func rejected(_ action: () throws -> Void) {
            do { try action(); fatalError("Expected rejection") } catch { }
        }
        let vectorsData = try Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1] + ".pairing.json"))
        let vectors = try JSONSerialization.jsonObject(with: vectorsData) as! [[String: String]]
        for vector in vectors {
            let derived = try Wire.pairingToken(vector["code"]!)
            precondition(derived.base64EncodedString() == vector["token"]!)
        }
        rejected { _ = try Wire.pairingToken(" \t\r\n") }
        rejected { _ = try Wire.pairingToken(String(repeating: "a", count: 257)) }
        print("PASS: C# ↔ Swift chosen codes (numbers, letters, mixed, Unicode, whitespace, case); empty/oversized rejection.")
        rejected { _ = try incoming.open(Data(line.utf8)) }
        let outgoing = try Wire(token: token, client: client, server: server, isServer: false)
        let first = try outgoing.seal(["type": "hello"])
        let second = try outgoing.seal(["type": "ping"])
        let fresh = try Wire(token: token, client: client, server: server, isServer: true)
        let firstLine = Data(first.dropLast()), secondLine = Data(second.dropLast())
        rejected { _ = try fresh.open(secondLine) }
        var bad = Data(base64Encoded: firstLine)!; bad[bad.count - 1] ^= 1
        rejected { _ = try fresh.open(Data(bad.base64EncodedString().utf8)) }
        _ = try fresh.open(firstLine); _ = try fresh.open(secondLine)
        let wrong = try Wire(token: Data(repeating: 0, count: 32), client: client, server: server, isServer: true)
        rejected { _ = try wrong.open(firstLine) }
        rejected { _ = try fresh.open(Data("malformed".utf8)) }
        let response = try incoming.seal(["type": "ready"])
        try response.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
        print("PASS: C# → Swift message; Swift tampering, wrong key, replay, order, malformed input.")
    }
}
