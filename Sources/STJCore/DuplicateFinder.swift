import Foundation

// MARK: - 重复项检测

/// 一组疑似重复的素材。
public struct DuplicateGroup: Identifiable, Sendable {
    public let id: String
    /// 判定依据说明，例如「内容相同（首 64KB 哈希）」。
    public let reason: String
    public let assets: [Asset]
    /// 按建议保留（评分高、精选、体积最大、修改时间最新）排序后的顺序。
    public let keepFirst: Asset?

    public var count: Int { assets.count }
    /// 除建议保留的一张之外，其余体积之和。
    public var wastedBytes: Int64 {
        let total = assets.reduce(Int64(0)) { $0 + $1.sizeBytes }
        return max(0, total - (keepFirst?.sizeBytes ?? 0))
    }
    public var wastedText: String { ByteCountFormatter.string(fromByteCount: wastedBytes, countStyle: .file) }

    public init(id: String, reason: String, assets: [Asset], keepFirst: Asset?) {
        self.id = id
        self.reason = reason
        self.assets = assets
        self.keepFirst = keepFirst
    }
}

/// 近似重复检测：先按「大小 + 尺寸 + 时长」聚类，候选足够小时可选再比对文件头哈希，
/// 避免对整库做全量哈希。
public enum DuplicateFinder {
    public static func find(in assets: [Asset], verifyContent: Bool = false) -> [DuplicateGroup] {
        // 只有同尺寸、同像素、同时长（视频）才可能是同一张，先粗筛
        var buckets: [String: [Asset]] = [:]
        for asset in assets {
            let durationKey = asset.kind == 2 ? String(Int(asset.durationSeconds.rounded())) : "-"
            let key = "\(asset.kind)|\(asset.sizeBytes)|\(asset.width)x\(asset.height)|\(durationKey)"
            buckets[key, default: []].append(asset)
        }

        var groups: [DuplicateGroup] = []
        for (key, bucket) in buckets where bucket.count > 1 {
            var clusters: [[Asset]]
            var reason = "大小与像素尺寸相同"
            if verifyContent {
                var hashed: [String: [Asset]] = [:]
                for asset in bucket {
                    let hash = ContentHasher.quickHash(at: URL(fileURLWithPath: asset.path))
                        ?? "missing|\(asset.path)"
                    hashed[hash, default: []].append(asset)
                }
                clusters = hashed.values.filter { $0.count > 1 }
                reason = "文件内容相同（首 64KB 哈希）"
            } else {
                clusters = [bucket]
            }

            for cluster in clusters {
                guard cluster.count > 1 else { continue }
                let ordered = cluster.sorted(by: keepPriority)
                groups.append(DuplicateGroup(
                    id: key + "|" + (ordered.first?.id ?? UUID().uuidString),
                    reason: reason,
                    assets: ordered,
                    keepFirst: ordered.first
                ))
            }
        }
        return groups.sorted { $0.wastedBytes > $1.wastedBytes }
    }

    /// 建议保留优先级：精选 > 未标记 > 废片，评分高优先，其次体积大、修改时间新。
    static func keepPriority(_ lhs: Asset, _ rhs: Asset) -> Bool {
        func pickRank(_ value: Int?) -> Int {
            switch value {
            case .some(1): return 2
            case nil: return 1
            default: return 0
            }
        }
        if pickRank(lhs.isPick) != pickRank(rhs.isPick) {
            return pickRank(lhs.isPick) > pickRank(rhs.isPick)
        }
        if lhs.rating != rhs.rating { return lhs.rating > rhs.rating }
        if lhs.sizeBytes != rhs.sizeBytes { return lhs.sizeBytes > rhs.sizeBytes }
        let left = lhs.fileModifiedAt ?? .distantPast
        let right = rhs.fileModifiedAt ?? .distantPast
        return left > right
    }
}

/// 轻量内容哈希：文件大小 + 前 64KB，用于重复项二次确认。
public enum ContentHasher {
    public static let chunkSize = 64 * 1024

    public static func quickHash(at url: URL) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        let data = (try? handle.read(upToCount: chunkSize)) ?? Data()
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int64) ?? 0
        var seed = Data("\(size)".utf8)
        seed.append(data)
        let digest = SHA1.hexDigest(seed).map { String(format: "%02x", $0) }.joined()
        return digest
    }
}