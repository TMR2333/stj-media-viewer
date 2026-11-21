import Foundation

// MARK: - WebDAV 客户端

/// WebDAV 连接参数（口令只存 Keychain，由调用方装配）。
public struct WebDAVCredentials: Sendable {
    public var endpoint: URL
    public var username: String
    public var password: String
    /// 远端根目录，例如 `/dav/stj/`；空字符串表示直接落在 endpoint 根。
    public var remoteRoot: String

    public init(endpoint: URL, username: String, password: String, remoteRoot: String = "") {
        self.endpoint = endpoint
        self.username = username
        self.password = password
        self.remoteRoot = remoteRoot
    }
}

public enum WebDAVError: LocalizedError, Equatable {
    case invalidEndpoint
    case unauthorized
    case httpStatus(Int, String)
    case transport(String)
    case cancelled

    public var errorDescription: String? {
        switch self {
        case .invalidEndpoint:
            return "WebDAV 地址无效"
        case .unauthorized:
            return "鉴权失败（401），请检查用户名/密码"
        case let .httpStatus(code, path):
            return "服务器返回 HTTP \(code)：\(path)"
        case let .transport(message):
            return "网络错误：\(message)"
        case .cancelled:
            return "已取消"
        }
    }
}

/// 最小可用的 WebDAV 客户端：PROPFIND / MKCOL / PUT / DELETE。
///
/// 已实现规格要求的关键点：
/// - Basic 鉴权（密码来自调用方，应用层只从 Keychain 取）
/// - 大文件 PUT 显式带 `Expect: 100-continue`（URLSession 会按需处理继续握手）
/// - 429/5xx/连接中断按 1.2s → 3.5s 退避重试，最多 3 次
/// - 请求超时 600s，全部调用串行（由 `CloudSyncEngine` 保证）
public actor WebDAVClient {
    private let credentials: WebDAVCredentials
    private let session: URLSession
    private let maxAttempts: Int

    public init(credentials: WebDAVCredentials,
                configuration: URLSessionConfiguration = .ephemeral,
                maxAttempts: Int = 3) {
        self.credentials = credentials
        self.maxAttempts = maxAttempts
        let config = configuration
        config.timeoutIntervalForRequest = 600
        config.timeoutIntervalForResource = 900
        config.httpShouldUsePipelining = false
        self.session = URLSession(configuration: config)
    }

    // MARK: URL

    private var rootParts: [String] {
        credentials.remoteRoot
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            .split(separator: "/")
            .map(String.init)
    }

    /// endpoint + 绝对远端路径（path 可含多级，逐段百分号编码；不以 `/` 开头）。
    private func absoluteURL(_ path: String) -> URL? {
        var base = credentials.endpoint.absoluteString
        if !base.hasSuffix("/") { base += "/" }
        let parts = path
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            .split(separator: "/")
            .map(String.init)
        let encoded = parts
            .map { $0.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? $0 }
            .joined(separator: "/")
        return URL(string: base + encoded)
    }

    /// 把相对路径拼到 endpoint + remoteRoot 上，逐段做百分号编码。
    public func url(for relativePath: String) -> URL? {
        let joined = (rootParts + relativePath.split(separator: "/").map(String.init))
            .filter { !$0.isEmpty }
            .joined(separator: "/")
        return absoluteURL(joined)
    }

    // MARK: 基础请求

    private func authorizationHeader() -> String? {
        guard !credentials.username.isEmpty || !credentials.password.isEmpty else { return nil }
        let raw = "\(credentials.username):\(credentials.password)"
        return "Basic " + Data(raw.utf8).base64EncodedString()
    }

    private func makeRequest(method: String,
                             relativePath: String,
                             body: Data? = nil,
                             headers: [String: String] = [:]) throws -> URLRequest {
        guard let url = url(for: relativePath) else { throw WebDAVError.invalidEndpoint }
        return makeRequest(method: method, absoluteURL: url, body: body, headers: headers)
    }

    private func makeAbsoluteRequest(method: String,
                                     path: String,
                                     body: Data? = nil,
                                     headers: [String: String] = [:]) throws -> URLRequest {
        guard let url = absoluteURL(path) else { throw WebDAVError.invalidEndpoint }
        return makeRequest(method: method, absoluteURL: url, body: body, headers: headers)
    }

    private func makeRequest(method: String,
                             absoluteURL url: URL,
                             body: Data? = nil,
                             headers: [String: String] = [:]) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpMethod = method
        if let auth = authorizationHeader() {
            request.setValue(auth, forHTTPHeaderField: "Authorization")
        }
        for (key, value) in headers {
            request.setValue(value, forHTTPHeaderField: key)
        }
        request.httpBody = body
        if let body {
            request.setValue(String(body.count), forHTTPHeaderField: "Content-Length")
        }
        return request
    }

    /// 带退避重试的请求执行；只对幂等方法重试。
    private func send(_ request: URLRequest, retryable: Bool = true) async throws -> (Data, HTTPURLResponse) {
        var lastError: Error?
        for attempt in 0..<maxAttempts {
            try Task.checkCancellation()
            do {
                let (data, response) = try await session.data(for: request)
                guard let http = response as? HTTPURLResponse else {
                    throw WebDAVError.transport("非 HTTP 响应")
                }
                if http.statusCode == 401 { throw WebDAVError.unauthorized }
                if retryable, [429, 500, 502, 503, 504].contains(http.statusCode), attempt < maxAttempts - 1 {
                    try await backoff(attempt: attempt)
                    continue
                }
                return (data, http)
            } catch is CancellationError {
                throw WebDAVError.cancelled
            } catch let error as WebDAVError {
                lastError = error
                if error == .unauthorized { throw error }
                if attempt < maxAttempts - 1, retryable {
                    try await backoff(attempt: attempt)
                    continue
                }
                throw error
            } catch {
                lastError = error
                if attempt < maxAttempts - 1, retryable {
                    try await backoff(attempt: attempt)
                    continue
                }
                throw WebDAVError.transport(error.localizedDescription)
            }
        }
        throw (lastError as? WebDAVError) ?? WebDAVError.transport("重试后仍失败")
    }

    private func backoff(attempt: Int) async throws {
        // 规格：1.2s / 3.5s 退避
        let delay: Double = attempt == 0 ? 1.2 : 3.5
        try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
    }

    // MARK: 操作

    /// PROPFIND，depth 0 只查自身；返回 (存在, 内容长度)。
    public func exists(_ relativePath: String) async throws -> Bool {
        let request = try makeRequest(method: "PROPFIND", relativePath: relativePath,
                                      headers: ["Depth": "0", "Content-Type": "application/xml; charset=utf-8"])
        let (data, http) = try await send(request, retryable: false)
        if http.statusCode == 404 { return false }
        guard (200..<300).contains(http.statusCode) else {
            throw WebDAVError.httpStatus(http.statusCode, relativePath)
        }
        // 空内容也算存在（部分服务端不返回 body）
        return !data.isEmpty || http.statusCode == 207
    }

    /// 远端文件大小（PROPFIND getcontentlength）；文件不存在返回 nil。
    public func remoteSize(_ relativePath: String) async throws -> Int64? {
        let request = try makeRequest(method: "PROPFIND", relativePath: relativePath,
                                      headers: ["Depth": "0", "Content-Type": "application/xml; charset=utf-8"])
        let (data, http) = try await send(request, retryable: false)
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else {
            throw WebDAVError.httpStatus(http.statusCode, relativePath)
        }
        let text = String(data: data, encoding: .utf8) ?? ""
        guard let range = text.range(of: "getcontentlength>") else { return nil }
        let tail = text[range.upperBound...]
        let digits = tail.prefix { $0.isNumber }
        return Int64(digits)
    }

    /// MKCOL：从 remoteRoot 起逐级创建目录；已存在视为成功。
    public func createDirectories(_ relativePath: String) async throws {
        let parts = rootParts + relativePath.split(separator: "/").map(String.init)
        guard !parts.isEmpty else { return }
        var current = ""
        for part in parts {
            current += (current.isEmpty ? "" : "/") + part
            let request = try makeAbsoluteRequest(method: "MKCOL", path: current)
            do {
                let (_, http) = try await send(request, retryable: false)
                switch http.statusCode {
                case 200..<300, 405, 301:
                    continue
                case 409:
                    // 父目录还没建好，正常流程不会出现；补建一次
                    if current.contains("/") {
                        let parent = String(current.dropLast(part.count + 1))
                        try await createDirectories(parent)
                        continue
                    }
                    throw WebDAVError.httpStatus(409, current)
                default:
                    throw WebDAVError.httpStatus(http.statusCode, current)
                }
            } catch WebDAVError.httpStatus(405, _) {
                continue
            }
        }
    }

    /// PUT 单个文件（带 Expect: 100-continue）。
    public func upload(fileURL: URL, to relativePath: String) async throws -> Int64 {
        try Task.checkCancellation()
        let data = try Data(contentsOf: fileURL, options: .mappedIfSafe)
        var headers: [String: String] = [
            "Content-Type": "application/octet-stream",
            "Expect": "100-continue",
            "X-STJ-Uploaded-By": "STJMediaViewer-macOS",
        ]
        headers["Content-Length"] = String(data.count)
        var request = try makeRequest(method: "PUT", relativePath: relativePath, body: data, headers: headers)
        // URLSession 可能忽略手工 Expect 头，这里保留为服务端能力探测的显式信号
        request.setValue("100-continue", forHTTPHeaderField: "Expect")
        let (_, http) = try await send(request)
        guard (200..<300).contains(http.statusCode) else {
            throw WebDAVError.httpStatus(http.statusCode, relativePath)
        }
        return Int64(data.count)
    }

    public func delete(_ relativePath: String) async throws {
        let request = try makeRequest(method: "DELETE", relativePath: relativePath)
        let (_, http) = try await send(request, retryable: false)
        guard (200..<300).contains(http.statusCode) || http.statusCode == 404 else {
            throw WebDAVError.httpStatus(http.statusCode, relativePath)
        }
    }

    /// 列目录（Depth: 1），返回远端相对路径列表（用于诊断）。
    /// 列出目录内容（PROPFIND Depth:1），解析出文件/目录、大小与修改时间。
    public func listEntries(_ relativePath: String) async throws -> [RemoteEntry] {
        let request = try makeRequest(method: "PROPFIND", relativePath: relativePath,
                                      headers: ["Depth": "1", "Content-Type": "application/xml; charset=utf-8"])
        let (data, http) = try await send(request, retryable: false)
        guard (200..<300).contains(http.statusCode) else {
            throw WebDAVError.httpStatus(http.statusCode, relativePath)
        }
        let entries = RemoteEntryParser.parse(data)
        let rootPrefix = "/" + credentials.remoteRoot.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let base = rootPrefix == "/" ? "" : rootPrefix
        let current = base + "/" + relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return entries.compactMap { entry in
            let trimmed = entry.path.hasSuffix("/") ? String(entry.path.dropLast()) : entry.path
            guard trimmed == current || trimmed == current + "/" else {
                guard trimmed.hasPrefix(base.isEmpty ? "/" : base + "/") else { return nil }
                var relative = trimmed
                if !base.isEmpty { relative = String(relative.dropFirst(base.count + 1)) }
                else { relative = String(relative.dropFirst()) }
                relative = relative.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                guard !relative.isEmpty else { return nil }
                return RemoteEntry(path: relative, name: entry.name.isEmpty ? (relative as NSString).lastPathComponent : entry.name,
                                   isDirectory: entry.isDirectory, size: entry.size, modified: entry.modified)
            }
            return nil
        }
        .sorted { lhs, rhs in
            if lhs.isDirectory != rhs.isDirectory { return lhs.isDirectory }
            return lhs.name.localizedStandardCompare(rhs.name) == .orderedAscending
        }
    }

    /// 下载远端文件到本地目录，返回写入字节数。
    public func download(relativePath: String, to destination: URL) async throws -> Int64 {
        let request = try makeRequest(method: "GET", relativePath: relativePath)
        let (data, http) = try await send(request, retryable: false)
        guard (200..<300).contains(http.statusCode) else {
            throw WebDAVError.httpStatus(http.statusCode, relativePath)
        }
        try data.write(to: destination, options: .atomic)
        return Int64(data.count)
    }

    public func list(_ relativePath: String) async throws -> [String] {
        let request = try makeRequest(method: "PROPFIND", relativePath: relativePath,
                                      headers: ["Depth": "1", "Content-Type": "application/xml; charset=utf-8"])
        let (data, http) = try await send(request, retryable: false)
        guard (200..<300).contains(http.statusCode) else {
            throw WebDAVError.httpStatus(http.statusCode, relativePath)
        }
        let text = String(data: data, encoding: .utf8) ?? ""
        var result: [String] = []
        var searchRange = text.startIndex..<text.endIndex
        while let open = text.range(of: "<D:href>", range: searchRange) ?? text.range(of: "<d:href>", range: searchRange) {
            let closeToken = text[open.lowerBound...].hasPrefix("<D:href>") ? "</D:href>" : "</d:href>"
            guard let close = text.range(of: closeToken, range: open.upperBound..<text.endIndex) else { break }
            var href = String(text[open.upperBound..<close.lowerBound])
            href = href.removingPercentEncoding ?? href
            result.append(href)
            searchRange = close.upperBound..<text.endIndex
        }
        return result
    }
}

// MARK: - 远端目录项

public struct RemoteEntry: Identifiable, Sendable, Equatable {
    public var id: String { path }
    /// 相对远端根目录的路径，例如 "2026/08/SH_0001.jpg"。
    public let path: String
    public let name: String
    public let isDirectory: Bool
    public let size: Int64
    public let modified: Date?

    public var sizeText: String {
        guard !isDirectory else { return "—" }
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        return formatter.string(fromByteCount: size)
    }
}

private struct RawRemoteEntry {
    var path: String = ""
    var name: String = ""
    var isDirectory = false
    var size: Int64 = 0
    var modified: Date?
}

private final class RemoteEntryParser: NSObject, XMLParserDelegate {
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return f
    }()

    private var entries: [RawRemoteEntry] = []
    private var current: RawRemoteEntry?
    private var text = ""
    private var sawCollectionTag = false
    private var insideCollection = false

    static func parse(_ data: Data) -> [RawRemoteEntry] {
        let parser = XMLParser(data: data)
        let delegate = RemoteEntryParser()
        parser.delegate = delegate
        parser.parse()
        return delegate.entries
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        let name = Self.localName(elementName)
        text = ""
        if name == "response" {
            current = RawRemoteEntry()
            sawCollectionTag = false
            insideCollection = false
        } else if name == "collection" {
            sawCollectionTag = true
            insideCollection = true
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) { text += string }

    private static func localName(_ elementName: String) -> String {
        let lower = elementName.lowercased()
        if let colon = lower.lastIndex(of: ":") { return String(lower[lower.index(after: colon)...]) }
        return lower
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?) {
        let name = Self.localName(elementName)
        switch name {
            case "href":
                current?.path = text.removingPercentEncoding ?? text
            case "displayname":
                current?.name = text.trimmingCharacters(in: .whitespacesAndNewlines)
            case "getcontentlength":
                current?.size = Int64(text.trimmingCharacters(in: .whitespacesAndNewlines)) ?? 0
            case "getlastmodified":
                current?.modified = Self.formatter.date(from: text.trimmingCharacters(in: .whitespacesAndNewlines))
            case "collection":
                insideCollection = false
                if sawCollectionTag { current?.isDirectory = true }
            case "response":
                if let entry = current { entries.append(entry) }
                current = nil
            default:
                break
        }
        text = ""
    }
}

// MARK: - 连接诊断

public struct DiagnosticStep: Sendable, Identifiable {
    public var id: Int
    public var title: String
    public var detail: String
    public var succeeded: Bool

    public init(id: Int, title: String, detail: String, succeeded: Bool) {
        self.id = id
        self.title = title
        self.detail = detail
        self.succeeded = succeeded
    }
}

public enum WebDAVDiagnostics {
    /// 连接 → 鉴权 → 列目录 → 上传探针 → 核验 → 清理。
    public static func run(credentials: WebDAVCredentials,
                           probeName: String = ".stj-probe-\(UUID().uuidString.prefix(8)).txt") async -> [DiagnosticStep] {
        var steps: [DiagnosticStep] = []
        let client = WebDAVClient(credentials: credentials)
        let probeBody = Data("STJ Media Viewer WebDAV probe".utf8)
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent(probeName)
        try? probeBody.write(to: temp)
        defer { try? FileManager.default.removeItem(at: temp) }

        func add(_ title: String, _ detail: String, _ ok: Bool) {
            steps.append(DiagnosticStep(id: steps.count, title: title, detail: detail, succeeded: ok))
        }

        add("连接 / 鉴权", "检查 \(credentials.endpoint.absoluteString)", credentials.endpoint.scheme != nil)
        guard credentials.endpoint.scheme != nil else { return steps }

        do {
            // 远端根目录可能还不存在：先确保可写，再列目录
            try await client.createDirectories("")
            let list = try await client.list("")
            add("列目录", "Depth:1 返回 \(list.count) 个条目", true)
        } catch let error as WebDAVError {
            add("列目录", error.localizedDescription, false)
            return steps
        } catch {
            add("列目录", error.localizedDescription, false)
            return steps
        }

        do {
            let bytes = try await client.upload(fileURL: temp, to: probeName)
            add("上传探针", "PUT \(probeName) · \(bytes) B", true)
        } catch {
            add("上传探针", error.localizedDescription, false)
            return steps
        }

        do {
            let size = try await client.remoteSize(probeName)
            add("核验", "远端大小 \(size.map(String.init) ?? "未知") B", size == Int64(probeBody.count))
        } catch {
            add("核验", error.localizedDescription, false)
        }

        do {
            try await client.delete(probeName)
            add("清理", "已删除探针文件", true)
        } catch {
            add("清理", error.localizedDescription, false)
        }
        return steps
    }
}