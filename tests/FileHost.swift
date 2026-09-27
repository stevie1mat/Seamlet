import Foundation
import AppKit

@main struct FileHost {
    static func main() throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let paths = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent("source"), includingPropertiesForKeys: nil).sorted { $0.lastPathComponent < $1.lastPathComponent }
        // Use a private pasteboard so tests never replace the user's clipboard.
        let board = NSPasteboard.withUniqueName(); defer { board.releaseGlobally() }
        guard board.writeObjects(paths.map { $0 as NSURL }),
              let restored = board.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
              Set(restored.map(\.path)) == Set(paths.map(\.path)) else { throw FileCopyError.invalid }
        let server = FileServer()
        server.offer("fixture", paths)
        server.received = { paths, _ in
            do {
                try JSONSerialization.data(withJSONObject: paths.map(\.path)).write(to: root.appendingPathComponent("uploaded.json"), options: .atomic)
                return true
            } catch { return false }
        }
        server.receivedText = { [weak server] text, _ in
            DispatchQueue.main.sync {
                board.clearContents()
                guard board.setString(text, forType: .string), board.string(forType: .string) == text else { return false }
                server?.offerText("text-fixture", text)
                return true
            }
        }
        try server.start(port: 0)
        let info = try JSONSerialization.data(withJSONObject: ["port": server.port, "key": server.key.base64EncodedString()])
        print(String(data: info, encoding: .utf8)!); fflush(stdout)
        withExtendedLifetime(server) { dispatchMain() }
    }
}
