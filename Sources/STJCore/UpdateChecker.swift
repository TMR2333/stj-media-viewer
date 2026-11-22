import Foundation

/// 新版本信息（来自 GitCode Releases API）。
public struct UpdateInfo: Sendable, Equatable {
    /// 去掉前缀/后缀的纯版本号，例如 "1.37.0"。
    public let version: String
    /// 远端 tag，例如 "v1.37.0-macos"。
    public let tag: String
    /// 发行版标题。
    public let name: String
    /// 发行版页面地址（用户点「下载」跳这里）。
    public let releaseURL: URL
    /// 该版本是否比当前版本新。
    public let isNewer: Bool
}

/// 检查更新：查 GitCode 发行版列表，取比当前版本新的最高版本。
///
/// 不依赖 Sparkle：应用是 ad-hoc 签名，自动替换自身会打断 TCC 授权，
/// 所以这里做「检查 + 引导下载」，由用户手动安装（与 macOS 版的分发方式一致）。
public enum UpdateChecker {
    public static let repository = "TMR2333/stj-media-viewer-macos"

    public enum Failure: LocalizedError {
        case badResponse(Int)
        case empty
        case malformed

        public var errorDescription: String? {
            switch self {
            case .badResponse(let code): return "发行版接口返回 \(code)"
            case .empty: return "没有找到任何发行版"
            case .malformed: return "发行版数据格式异常"
            }
        }
    }

    /// 拉取发行版列表（默认取前 20 个）。
    public static func fetchReleases(session: URLSession = .shared, pageSize: Int = 20) async throws -> [UpdateInfo] {
        var components = URLComponents(string: "https://api.gitcode.com/api/v5/repos/\(repository)/releases")!
        components.queryItems = [URLQueryItem(name: "per_page", value: String(pageSize))]
        var request = URLRequest(url: components.url!)
        request.timeoutInterval = 15
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw Failure.badResponse(http.statusCode)
        }
        guard let raw = try JSONSerialization.jsonObject(with: data) as? [[String: Any]], !raw.isEmpty else {
            throw Failure.empty
        }
        var infos: [UpdateInfo] = []
        for item in raw {
            guard let tag = item["tag_name"] as? String,
                  let version = versionNumber(from: tag),
                  let name = item["name"] as? String else { continue }
            let url = URL(string: "https://gitcode.com/\(repository)/releases/tag/\(tag)")!
            infos.append(UpdateInfo(version: version, tag: tag, name: name, releaseURL: url, isNewer: false))
        }
        guard !infos.isEmpty else { throw Failure.malformed }
        return infos
    }

    /// 取最新版本；`current` 为当前版本号。没有更新时返回 nil。
    public static func check(current: String, session: URLSession = .shared) async throws -> UpdateInfo? {
        let infos = try await fetchReleases(session: session)
        guard let newest = infos.max(by: { compareVersions($0.version, $1.version) < 0 }) else { return nil }
        guard compareVersions(newest.version, current) > 0 else { return nil }
        return UpdateInfo(version: newest.version, tag: newest.tag, name: newest.name,
                          releaseURL: newest.releaseURL, isNewer: true)
    }

    /// 从 tag 里抠版本号："v1.37.0-macos" → "1.37.0"。
    public static func versionNumber(from tag: String) -> String? {
        let pattern = #"(\d+)\.(\d+)\.(\d+)"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return nil }
        let range = NSRange(tag.startIndex..., in: tag)
        guard let match = regex.firstMatch(in: tag, range: range),
              let r = Range(match.range, in: tag) else { return nil }
        return String(tag[r])
    }

    /// 语义化版本比较：a > b 返回 1，相等 0，否则 -1。缺失段按 0 处理。
    public static func compareVersions(_ a: String, _ b: String) -> Int {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        let count = max(pa.count, pb.count)
        for index in 0..<count {
            let va = index < pa.count ? pa[index] : 0
            let vb = index < pb.count ? pb[index] : 0
            if va != vb { return va > vb ? 1 : -1 }
        }
        return 0
    }
}
