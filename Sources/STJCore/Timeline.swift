import Foundation

/// 时间线视图中的一天（或「未标注日期」分组）。
public struct TimelineSection: Identifiable, Sendable {
    /// 分组键：`yyyy-MM-dd`，未标注日期时为 `Timeline.undatedKey`。
    public var id: String
    public var day: String
    public var title: String
    public var assets: [Asset]

    public var count: Int { assets.count }

    public init(id: String, day: String, title: String, assets: [Asset]) {
        self.id = id
        self.day = day
        self.title = title
        self.assets = assets
    }
}

/// 时间线分组：按拍摄日倒序，未标注日期的素材统一放到最后。
public enum Timeline {
    public static let undatedKey = "未标注日期"

    private static let dayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter
    }()

    private static let titleFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "yyyy 年 M 月 d 日 EEEE"
        return formatter
    }()

    /// 单个素材的分组键：优先 `captured_day`，其次文件修改时间的日期。
    public static func dayKey(for asset: Asset) -> String {
        if let day = asset.capturedDay?.trimmingCharacters(in: .whitespacesAndNewlines), !day.isEmpty {
            return String(day.prefix(10))
        }
        if let modified = asset.fileModifiedAt {
            return dayFormatter.string(from: modified)
        }
        return undatedKey
    }

    public static func title(for key: String) -> String {
        if key == undatedKey { return undatedKey }
        guard let date = dayFormatter.date(from: key) else { return key }
        return titleFormatter.string(from: date)
    }

    /// 只保留照片/视频顺序，按天分桶，天按倒序；同一天内保持传入顺序（通常是当前排序）。
    public static func sections(from assets: [Asset]) -> [TimelineSection] {
        var buckets: [String: [Asset]] = [:]
        var order: [String] = []
        for asset in assets {
            let key = dayKey(for: asset)
            if buckets[key] == nil { order.append(key) }
            buckets[key, default: []].append(asset)
        }
        let sorted = order.sorted { lhs, rhs in
            if lhs == undatedKey { return false }
            if rhs == undatedKey { return true }
            return lhs > rhs
        }
        return sorted.map { key in
            TimelineSection(id: key, day: key, title: title(for: key), assets: buckets[key] ?? [])
        }
    }
}
