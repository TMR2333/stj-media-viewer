import Foundation
import XCTest
@testable import STJCore

// MARK: - URLProtocol 拦截

/// 用 URLProtocol 拦截真实 URLSession 请求，验证 WebDAV 客户端发出的方法与头。
final class MockURLProtocol: URLProtocol {
    struct Recorded: Sendable {
        var method: String
        var path: String
        var authorization: String?
        var expect: String?
        var contentLength: String?
        var bodySize: Int
    }

    nonisolated(unsafe) static var records: [Recorded] = []
    nonisolated(unsafe) static var handler: ((URLRequest, Data?) -> (status: Int, headers: [String: String], body: Data?))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        // URLProtocol 拿不到 upload task 的 body，但 data(for:) 的 httpBody 可用
        let body = request.httpBody ?? request.httpBodyStream.flatMap(Self.drain)
        Self.records.append(Self.Recorded(method: request.httpMethod ?? "?",
                                          path: request.url?.path ?? "?",
                                          authorization: request.value(forHTTPHeaderField: "Authorization"),
                                          expect: request.value(forHTTPHeaderField: "Expect"),
                                          contentLength: request.value(forHTTPHeaderField: "Content-Length"),
                                          bodySize: body?.count ?? 0))
        let reply = Self.handler?(request, body) ?? (status: 200, headers: [:], body: nil)
        let response = HTTPURLResponse(url: request.url!,
                                       statusCode: reply.status,
                                       httpVersion: "HTTP/1.1",
                                       headerFields: reply.headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if let out = reply.body ?? (reply.status == 200 ? body : nil) {
            client?.urlProtocol(self, didLoad: out)
        }
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func drain(_ stream: InputStream) -> Data? {
        stream.open()
        defer { stream.close() }
        var data = Data()
        let bufferSize = 4096
        var buffer = [UInt8](repeating: 0, count: bufferSize)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: bufferSize)
            if read <= 0 { break }
            data.append(buffer, count: read)
        }
        return data
    }

    static func reset() {
        records = []
        handler = nil
    }

    static func configuration() -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockURLProtocol.self]
        return config
    }
}

final class CloudSyncTests: XCTestCase {
    private func asset(id: String,
                       name: String,
                       directory: URL,
                       capturedDay: String? = "2026-08-19",
                       country: String? = "中国",
                       city: String? = "上海",
                       device: String? = "ILCE-7RM5") -> Asset {
        Asset(id: id,
              path: directory.appendingPathComponent(name).path,
              fileName: name,
              capturedAt: capturedDay.flatMap { ISO8601DateFormatter().date(from: "\($0)T20:41:00Z") },
              capturedDay: capturedDay,
              country: country,
              province: "上海市",
              city: city,
              deviceKey: device)
    }

    private func temporaryDirectory() throws -> URL {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("stj-dav-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        return url
    }

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
    }

    override func tearDown() {
        MockURLProtocol.reset()
        super.tearDown()
    }

    func testPlanLayoutsAndSanitizing() throws {
        let directory = try temporaryDirectory()
        for name in ["IMG_0001.jpg", "VID 2.mov"] {
            try Data("x".utf8).write(to: directory.appendingPathComponent(name))
        }
        let assets = [asset(id: "a", name: "IMG_0001.jpg", directory: directory),
                      asset(id: "b", name: "VID 2.mov", directory: directory)]

        let flat = CloudSync.plan(assets: assets, layout: .flat)
        XCTAssertEqual(flat.0.map(\.remotePath), ["IMG_0001.jpg", "VID 2.mov"])

        let byDate = CloudSync.plan(assets: assets, layout: .byDate)
        XCTAssertEqual(byDate.0.map(\.remotePath), ["2026/08/IMG_0001.jpg", "2026/08/VID 2.mov"])

        let byLocation = CloudSync.plan(assets: assets, layout: .byLocation)
        XCTAssertEqual(byLocation.0.map(\.remotePath), ["中国/上海/IMG_0001.jpg", "中国/上海/VID 2.mov"])

        let byDevice = CloudSync.plan(assets: assets, layout: .byDateAndDevice)
        XCTAssertEqual(byDevice.0.first?.remotePath, "2026/08/ILCE-7RM5/IMG_0001.jpg")

        // 非法字符清洗 + 缺文件进跳过列表
        XCTAssertEqual(CloudSync.sanitized("a/b:c?.jpg"), "a-b-c-.jpg")
        let missing = asset(id: "c", name: "GONE.jpg", directory: directory)
        let planned = CloudSync.plan(assets: [missing], layout: .flat)
        XCTAssertTrue(planned.0.isEmpty)
        XCTAssertEqual(planned.skipped, ["GONE.jpg"])
    }

    func testPlanIncludesXMPAndFallsBackWithoutLocation() throws {
        let directory = try temporaryDirectory()
        let jpeg = directory.appendingPathComponent("XMP_0001.jpg")
        try Data("x".utf8).write(to: jpeg)
        try Data("<x:xmpmeta/>".utf8).write(to: directory.appendingPathComponent("XMP_0001.xmp"))

        let withSidecar = asset(id: "a", name: "XMP_0001.jpg", directory: directory)
        let plans = CloudSync.plan(assets: [withSidecar], layout: .flat)
        XCTAssertEqual(plans.0.count, 1)
        XCTAssertNotNil(plans.0[0].sidecarURL)
        XCTAssertEqual(plans.0[0].sidecarRemotePath, "XMP_0001.xmp")

        let noLocation = asset(id: "b", name: "XMP_0001.jpg", directory: directory,
                               capturedDay: nil, country: nil, city: nil, device: nil)
        let byLocation = CloudSync.plan(assets: [noLocation], layout: .byLocation)
        XCTAssertEqual(byLocation.0.first?.remotePath, "XMP_0001.jpg")
        let byDate = CloudSync.plan(assets: [noLocation], layout: .byDate)
        XCTAssertEqual(byDate.0.first?.remotePath, "未分类/00/XMP_0001.jpg")
    }

    func testUploadCreatesDirectoriesSendsAuthAndVerifies() async throws {
        let directory = try temporaryDirectory()
        let file = directory.appendingPathComponent("IMG_0001.jpg")
        try Data(repeating: 0x41, count: 2048).write(to: file)
        let asset = asset(id: "a", name: "IMG_0001.jpg", directory: directory)

        MockURLProtocol.handler = { request, body in
            let method = request.httpMethod ?? ""
            if method == "MKCOL" { return (201, [:], nil) }
            if method == "PUT" { return (200, [:], nil) }
            if method == "PROPFIND" {
                let size = body?.count ?? 2048
                let xml = """
                <D:multistatus xmlns:D="DAV:"><D:response><D:href>/</D:href>
                <D:propstat><D:prop><D:getcontentlength>\(size)</D:getcontentlength></D:prop></D:propstat>
                </D:response></D:multistatus>
                """
                return (207, ["Content-Type": "application/xml"], Data(xml.utf8))
            }
            return (200, [:], nil)
        }

        let credentials = WebDAVCredentials(endpoint: URL(string: "https://dav.example.test/")!,
                                            username: "u",
                                            password: "p",
                                            remoteRoot: "stj")
        let items = CloudSync.plan(assets: [asset], layout: .flat).0
        let result = await CloudSync.upload(items: items,
                                            credentials: credentials,
                                            configuration: MockURLProtocol.configuration())

        XCTAssertTrue(result.failed.isEmpty, "上传失败：\(result.failed)")
        XCTAssertEqual(result.uploaded, ["IMG_0001.jpg"])
        XCTAssertEqual(result.bytesUploaded, 2048)

        let records = MockURLProtocol.records
        let methods = records.map(\.method)
        XCTAssertTrue(methods.contains("MKCOL"), "应先建目录（remoteRoot）")
        XCTAssertTrue(methods.contains("PUT"))
        XCTAssertTrue(methods.contains("PROPFIND"), "上传后应核验远端大小")

        let put = try XCTUnwrap(records.first { $0.method == "PUT" })
        XCTAssertEqual(put.path, "/stj/IMG_0001.jpg")
        XCTAssertEqual(put.authorization, "Basic " + Data("u:p".utf8).base64EncodedString())
        XCTAssertEqual(put.expect, "100-continue")
        XCTAssertEqual(put.contentLength, "2048")
        XCTAssertGreaterThan(put.bodySize, 0)

        let mkcol = records.filter { $0.method == "MKCOL" }.map(\.path)
        XCTAssertEqual(mkcol, ["/stj"], "应只创建 remoteRoot 一级")
    }

    func testUploadToNestedLayoutCreatesEachLevel() async throws {
        let directory = try temporaryDirectory()
        try Data("x".utf8).write(to: directory.appendingPathComponent("IMG_0001.jpg"))
        MockURLProtocol.handler = { request, _ in
            switch request.httpMethod {
            case "MKCOL": return (201, [:], nil)
            case "PUT": return (200, [:], nil)
            default: return (200, [:], nil)
            }
        }
        let credentials = WebDAVCredentials(endpoint: URL(string: "https://dav.example.test/dav/")!,
                                            username: "u", password: "p", remoteRoot: "stj")
        let asset = asset(id: "a", name: "IMG_0001.jpg", directory: directory)
        let items = CloudSync.plan(assets: [asset], layout: .byDate).0
        _ = await CloudSync.upload(items: items,
                                   credentials: credentials,
                                   configuration: MockURLProtocol.configuration())
        let mkcol = MockURLProtocol.records.filter { $0.method == "MKCOL" }.map(\.path)
        XCTAssertEqual(mkcol, ["/dav/stj", "/dav/stj/2026", "/dav/stj/2026/08"])
    }

    func testUploadRetriesOnServerError() async throws {
        let directory = try temporaryDirectory()
        try Data("y".utf8).write(to: directory.appendingPathComponent("IMG_0001.jpg"))
        var putAttempts = 0
        MockURLProtocol.handler = { request, _ in
            if request.httpMethod == "PUT" {
                putAttempts += 1
                return putAttempts == 1 ? (503, [:], nil) : (200, [:], nil)
            }
            return (201, [:], nil)
        }
        let credentials = WebDAVCredentials(endpoint: URL(string: "https://dav.example.test/")!,
                                            username: "u", password: "p")
        let asset = asset(id: "a", name: "IMG_0001.jpg", directory: directory)
        let result = await CloudSync.upload(items: CloudSync.plan(assets: [asset], layout: .flat).0,
                                            credentials: credentials,
                                            configuration: MockURLProtocol.configuration())
        XCTAssertEqual(putAttempts, 2, "遇到 503 应退避重试一次")
        XCTAssertTrue(result.failed.isEmpty)
        XCTAssertEqual(result.uploaded, ["IMG_0001.jpg"])
    }

    func testUploadReportsUnauthorized() async throws {
        let directory = try temporaryDirectory()
        try Data("z".utf8).write(to: directory.appendingPathComponent("IMG_0001.jpg"))
        MockURLProtocol.handler = { request, _ in
            request.httpMethod == "MKCOL" ? (201, [:], nil) : (401, [:], nil)
        }
        let credentials = WebDAVCredentials(endpoint: URL(string: "https://dav.example.test/")!,
                                            username: "u", password: "wrong")
        let asset = asset(id: "a", name: "IMG_0001.jpg", directory: directory)
        let result = await CloudSync.upload(items: CloudSync.plan(assets: [asset], layout: .flat).0,
                                            credentials: credentials,
                                            configuration: MockURLProtocol.configuration())
        XCTAssertEqual(result.uploaded.count, 0)
        XCTAssertEqual(result.failed.count, 1)
        XCTAssertTrue(result.failed[0].message.contains("401"))
    }
}
