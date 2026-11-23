import XCTest
@testable import STJCore

final class TempDav2Tests: XCTestCase {
    func testRootAlbum() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let endpoint = env["DAV_ENDPOINT"], let user = env["DAV_USER"], let pass = env["DAV_PASS"] else { throw XCTSkip("no env") }
        let root = env["DAV_ROOT"] ?? ""
        let creds = WebDAVCredentials(endpoint: URL(string: endpoint)!, username: user, password: pass, remoteRoot: root)
        let client = WebDAVClient(credentials: creds)
        let entries = try await client.listEntries("")
        print("ROOT[\(root)] entries=\(entries.count)")
        for e in entries { print("  \(e.isDirectory ? "D" : "F") [\(e.path)] size=\(e.size)") }
        let items = try await CloudAlbum.listMedia(client, root: "", maxDepth: 4, limit: 100)
        print("MEDIA=\(items.count)")
        for i in items.prefix(5) { print("  \(i.path) \(i.size)") }
    }
}
