import Foundation

@main struct DiscoveryHost {
    static func main() throws {
        let responder = DiscoveryResponder()
        try responder.start(port: 0, name: "Test MacBook")
        print(responder.port)
        fflush(stdout)
        withExtendedLifetime(responder) { dispatchMain() }
    }
}
