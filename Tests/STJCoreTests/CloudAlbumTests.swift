import Foundation
import XCTest
@testable import STJCore

final class CloudAlbumTests: XCTestCase {
    private func multistatus(_ entries: [(href: String, isCollection: Bool, size: Int)]) -> Data {
        var xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:">
        """
        for entry in entries {
            xml += "<D:response><D:href>\(entry.href)</D:href>"
            if entry.isCollection {
                xml += "<D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop></D:propstat>"
            } else {
                xml += "<D:propstat><D:prop><D:resourcetype/><D:getcontentlength>\(entry.size)</D:getcontentlength>"
                xml += "<D:getlastmodified>Wed, 19 Aug 2026 12:41:00 GMT</D:getlastmodified></D:prop></D:propstat>"
            }
            xml += "</D:response>"
        }
        xml += "</D:multistatus>"
        return Data(xml.utf8)
    }

    private func credentials() -> WebDAVCredentials {
        WebDAVCredentials(endpoint: URL(string: "http://dav.test/")!,
                          username: "tester",
                          password: "secret",
                          remoteRoot: "album")
    }

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
    }

    override func tearDown() {
        MockURLProtocol.reset()
        super.tearDown()
    }

    // MARK: - 分类与缓存键

    func testMediaClassification() {
        let jpg = CloudAlbumItem(path: "a/IMG_0001.JPG", name: "IMG_0001.JPG", size: 1024, modified: nil, isDirectory: false)
        XCTAssertTrue(jpg.isImage)
        XCTAssertFalse(jpg.isVideo)
        XCTAssertTrue(jpg.isMedia)
        XCTAssertEqual(jpg.fileExtension, "jpg")

        let mov = CloudAlbumItem(path: "a/VID_0001.mov", name: "VID_0001.mov", size: 2048, modified: nil, isDirectory: false)
        XCTAssertTrue(mov.isVideo)

        let txt = CloudAlbumItem(path: "a/readme.txt", name: "readme.txt", size: 10, modified: nil, isDirectory: false)
        XCTAssertFalse(txt.isMedia)
    }

    func testCacheURLIsStableAndKeepsExtension() {
        let item = CloudAlbumItem(path: "2026/08/SH_0001.jpg", name: "SH_0001.jpg", size: 42, modified: nil, isDirectory: false)
        let first = CloudAlbum.cacheURL(for: item)
        let second = CloudAlbum.cacheURL(for: item)
        XCTAssertEqual(first, second)
        XCTAssertEqual(first.pathExtension, "jpg")
        XCTAssertTrue(first.path.contains("CloudCache"))

        let renamed = CloudAlbumItem(path: "2026/08/SH_0002.jpg", name: "SH_0002.jpg", size: 42, modified: nil, isDirectory: false)
        XCTAssertNotEqual(first, CloudAlbum.cacheURL(for: renamed))
    }

    // MARK: - endpoint 带路径前缀（123pan 等）

    func testListEntriesStripsEndpointPathPrefix() async throws {
        let creds = WebDAVCredentials(endpoint: URL(string: "https://webdav.example.cn/webdav")!,
                                      username: "u", password: "p", remoteRoot: "")
        let client = WebDAVClient(credentials: creds, configuration: MockURLProtocol.configuration())
        MockURLProtocol.handler = { request, _ in
            XCTAssertTrue(request.url?.path.hasSuffix("/webdav") == true, "实际路径：\(request.url?.path ?? "nil")")
            return (207, [:], self.multistatus([
                ("/webdav/", true, 0),
                ("/webdav/%E5%AA%92%E4%BD%93%E6%96%87%E4%BB%B6/", true, 0),
                ("/webdav/cover.jpg", false, 512),
            ]))
        }
        let entries = try await client.listEntries("")
        XCTAssertEqual(entries.map(\.path), ["媒体文件", "cover.jpg"])
        XCTAssertEqual(entries.first?.isDirectory, true)
        XCTAssertEqual(entries.last?.size, 512)
    }

    func testListMediaJoinsNestedPaths() async throws {
        MockURLProtocol.handler = { request, _ in
            let path = request.url?.path ?? ""
            if path.hasSuffix("/album") {
                return (207, [:], self.multistatus([
                    ("/album/", true, 0),
                    ("/album/Sony%20A7C%20II/", true, 0),
                ]))
            }
            if path.contains("Sony A7C II") {
                return (207, [:], self.multistatus([
                    ("/album/Sony%20A7C%20II/", true, 0),
                    ("/album/Sony%20A7C%20II/DSC01165.jpg", false, 1234),
                ]))
            }
            return (404, [:], Data())
        }
        let client = WebDAVClient(credentials: credentials(), configuration: MockURLProtocol.configuration())
        let items = try await CloudAlbum.listMedia(client, root: "", maxDepth: 4, limit: 50)
        XCTAssertEqual(items.map(\.path), ["Sony A7C II/DSC01165.jpg"])
        XCTAssertEqual(items.first?.name, "DSC01165.jpg")
    }

    // MARK: - 远端列举

    func testListMediaFiltersNonMediaAndRecurses() async throws {
        MockURLProtocol.handler = { request, _ in
            let path = request.url?.path ?? ""
            if path.hasSuffix("/2026/") || path.hasSuffix("/2026") {
                return (207, [:], self.multistatus([
                    ("/album/2026/c.heic", false, 300),
                    ("/album/2026/readme.txt", false, 12),
                ]))
            }
            return (207, [:], self.multistatus([
                ("/album/", true, 0),
                ("/album/2026/", true, 0),
                ("/album/a.jpg", false, 100),
                ("/album/b.mov", false, 200),
                ("/album/notes.txt", false, 5),
            ]))
        }

        let client = WebDAVClient(credentials: credentials(), configuration: MockURLProtocol.configuration())
        let items = try await CloudAlbum.listMedia(client, root: "")
        let names = Set(items.map(\.name))
        XCTAssertEqual(names, ["a.jpg", "b.mov", "c.heic"])
        XCTAssertTrue(items.allSatisfy(\.isMedia))
        // PROPFIND 至少发过两次（根目录 + 子目录）
        XCTAssertGreaterThanOrEqual(MockURLProtocol.records.filter { $0.method == "PROPFIND" }.count, 2)
    }

    func testListMediaRespectsDepthLimit() async throws {
        MockURLProtocol.handler = { request, _ in
            let path = request.url?.path ?? ""
            if path.contains("/deep/") {
                return (207, [:], self.multistatus([("/album/deep/deep2/x.jpg", false, 9)]))
            }
            return (207, [:], self.multistatus([
                ("/album/", true, 0),
                ("/album/deep/", true, 0),
            ]))
        }
        let client = WebDAVClient(credentials: credentials(), configuration: MockURLProtocol.configuration())
        let items = try await CloudAlbum.listMedia(client, root: "", maxDepth: 1)
        XCTAssertTrue(items.isEmpty, "maxDepth=1 时不应下钻子目录")
    }

    // MARK: - 导入

    func testImportDownloadsItemsIntoFolder() async throws {
        let jpegData = Data([0xFF, 0xD8, 0xFF, 0xD9])
        MockURLProtocol.handler = { request, _ in
            if request.httpMethod == "GET" {
                return (200, ["Content-Type": "image/jpeg"], jpegData)
            }
            return (207, [:], self.multistatus([]))
        }
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stj-cloud-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }

        let client = WebDAVClient(credentials: credentials(), configuration: MockURLProtocol.configuration())
        let items = [
            CloudAlbumItem(path: "a.jpg", name: "a.jpg", size: 4, modified: nil, isDirectory: false),
            CloudAlbumItem(path: "b.mov", name: "b.mov", size: 4, modified: nil, isDirectory: false),
        ]
        let summary = try await CloudAlbum.importItems(items, client: client, destinationRoot: root)

        XCTAssertEqual(summary.downloaded, 2)
        XCTAssertEqual(summary.failed, 0)
        XCTAssertTrue(summary.directory.lastPathComponent.hasPrefix("云相册导入-"))
        let files = try FileManager.default.contentsOfDirectory(atPath: summary.directory.path).sorted()
        XCTAssertEqual(files, ["a.jpg", "b.mov"])
        XCTAssertEqual(try Data(contentsOf: summary.directory.appendingPathComponent("a.jpg")), jpegData)
    }

    func testImportRecordsFailures() async throws {
        MockURLProtocol.handler = { request, _ in
            if request.httpMethod == "GET" { return (404, [:], nil) }
            return (207, [:], self.multistatus([]))
        }
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stj-cloud-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }

        let client = WebDAVClient(credentials: credentials(), configuration: MockURLProtocol.configuration())
        let items = [CloudAlbumItem(path: "missing.jpg", name: "missing.jpg", size: 4, modified: nil, isDirectory: false)]
        let summary = try await CloudAlbum.importItems(items, client: client, destinationRoot: root)
        XCTAssertEqual(summary.downloaded, 0)
        XCTAssertEqual(summary.failed, 1)
        XCTAssertEqual(summary.failures, ["missing.jpg"])
    }
}