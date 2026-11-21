import Foundation

// MARK: - 整理动作（复制 / 移动 + 重命名模板）

public enum OrganizeMode: String, Sendable, CaseIterable {
    case copy
    case move

    public var title: String {
        switch self {
        case .copy: return "复制"
        case .move: return "移动"
        }
    }
}

public enum ConflictPolicy: String, Sendable, CaseIterable {
    /// 目标已存在时自动加序号，例如 `IMG_0001 (2).jpg`。
    case autoRename
    /// 目标已存在时跳过。
    case skip

    public var title: String {
        switch self {
        case .autoRename: return "自动改名"
        case .skip: return "跳过"
        }
    }
}

/// 重命名模板规则。
/// 可用占位符：`{original}` 原文件名、`{name}` 去扩展名的名字、`{ext}` 扩展名、
/// `{date}` 拍摄日期（yyyy-MM-dd）、`{year}` `{month}` `{day}`、`{city}`、`{country}`、
/// `{device}`、`{rating}`、`{theme}`；`/` 表示子目录。
public struct OrganizeRules: Sendable {
    public var template: String
    public var conflictPolicy: ConflictPolicy

    public init(template: String = "{date}/{original}", conflictPolicy: ConflictPolicy = .autoRename) {
        self.template = template
        self.conflictPolicy = conflictPolicy
    }

    /// 计算相对目标目录的路径（已做非法字符清理）。
    public func relativePath(for asset: Asset, fileManager: FileManager = .default) -> String {
        let original = asset.fileName.isEmpty
            ? URL(fileURLWithPath: asset.path).lastPathComponent
            : asset.fileName
        let url = URL(fileURLWithPath: original)
        let name = url.deletingPathExtension().lastPathComponent
        let ext = self.ext(for: asset, fallback: url.pathExtension)

        let date = captureDate(for: asset, fileManager: fileManager)
        let calendar = Calendar(identifier: .gregorian)
        var components = calendar.dateComponents([.year, .month, .day], from: date)
        if components.year == nil { components = calendar.dateComponents([.year, .month, .day], from: asset.fileModifiedAt ?? Date()) }
        let year = components.year.map(String.init) ?? "0000"
        let month = components.month.map { String(format: "%02d", $0) } ?? "00"
        let day = components.day.map { String(format: "%02d", $0) } ?? "00"
        let dateText = "\(year)-\(month)-\(day)"

        let tokens: [String: String] = [
            "original": original,
            "name": name,
            "ext": ext,
            "date": dateText,
            "year": year,
            "month": month,
            "day": day,
            "city": asset.city ?? "",
            "country": asset.country ?? "",
            "device": asset.deviceKey ?? asset.cameraModel ?? "",
            "rating": asset.rating > 0 ? String(repeating: "★", count: min(asset.rating, 5)) : "",
            "theme": asset.themes.first ?? asset.tone ?? "",
        ]

        var rendered = template
        for (key, value) in tokens {
            rendered = rendered.replacingOccurrences(of: "{\(key)}", with: sanitizeComponent(value))
        }
        // 去掉未识别的占位符与多余分隔符
        if let regex = try? NSRegularExpression(pattern: "\\{[a-zA-Z]+\\}") {
            let range = NSRange(rendered.startIndex..<rendered.endIndex, in: rendered)
            rendered = regex.stringByReplacingMatches(in: rendered, range: range, withTemplate: "")
        }
        var pieces = rendered.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        pieces = pieces.map { sanitizeComponent($0) }.filter { !$0.isEmpty }
        guard var last = pieces.last else { return "未命名" }
        if last.isEmpty { last = "未命名" }
        if !last.lowercased().hasSuffix("." + ext.lowercased()), !ext.isEmpty {
            last += "." + ext
        }
        pieces[pieces.count - 1] = last
        return pieces.joined(separator: "/")
    }

    private func ext(for asset: Asset, fallback: String) -> String {
        if !asset.formatCode.isEmpty { return asset.formatCode.lowercased() }
        return fallback.lowercased()
    }

    private func captureDate(for asset: Asset, fileManager: FileManager) -> Date {
        if let captured = asset.capturedAt { return captured }
        if let day = asset.capturedDay, let parsed = Self.dayFormatter.date(from: day) { return parsed }
        if let modified = asset.fileModifiedAt { return modified }
        if let attributes = try? fileManager.attributesOfItem(atPath: asset.path),
           let modified = attributes[.modificationDate] as? Date {
            return modified
        }
        return Date()
    }

    static let dayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter
    }()

    /// 清理单个路径片段里的非法字符。
    private func sanitizeComponent(_ raw: String) -> String {
        var value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        for bad in ["\\", ":", "*", "?", "\"", "<", ">", "|", "\n", "\r", "\t"] {
            value = value.replacingOccurrences(of: bad, with: "-")
        }
        if value == "." || value == ".." { value = "" }
        return value
    }
}

public struct OrganizeItem: Sendable {
    public let asset: Asset
    public let source: URL
    public var destination: URL

    public init(asset: Asset, source: URL, destination: URL) {
        self.asset = asset
        self.source = source
        self.destination = destination
    }
}

public struct OrganizeFailure: Sendable {
    public let assetID: String
    public let message: String
}

public struct OrganizeSummary: Sendable {
    public var mode: OrganizeMode
    public var destinationRoot: URL
    public var copied: Int = 0
    public var moved: Int = 0
    public var skipped: Int = 0
    public var failures: [OrganizeFailure] = []
    /// 实际落盘的位置，供调用方回写数据库。
    public var relocated: [String: URL] = [:]
    public var duration: TimeInterval = 0

    public var total: Int { copied + moved + skipped + failures.count }

    public var descriptionText: String {
        var parts: [String] = ["\(mode.title) \(copied + moved) 个文件"]
        if skipped > 0 { parts.append("跳过 \(skipped)") }
        if !failures.isEmpty { parts.append("失败 \(failures.count)") }
        parts.append(String(format: "用时 %.1f 秒", duration))
        return parts.joined(separator: " · ")
    }
}

public enum Organizer {
    /// 计算目标路径：模板渲染 + 冲突改名（目标已存在或与前序条目冲突时加 ` (n)`）。
    public static func plan(
        assets: [Asset],
        to destinationRoot: URL,
        rules: OrganizeRules,
        fileManager: FileManager = .default
    ) -> [OrganizeItem] {
        var claimed = Set<String>()
        var items: [OrganizeItem] = []
        for asset in assets {
            let relative = rules.relativePath(for: asset, fileManager: fileManager)
            var destination = destinationRoot.appendingPathComponent(relative)
            if rules.conflictPolicy == .autoRename {
                destination = uniqueDestination(
                    for: destination,
                    claimed: &claimed,
                    fileManager: fileManager
                )
            } else {
                claimed.insert(destination.standardizedFileURL.path)
            }
            items.append(OrganizeItem(
                asset: asset,
                source: URL(fileURLWithPath: asset.path),
                destination: destination
            ))
        }
        return items
    }

    /// 执行计划；复制/移动失败会记入 failures，不影响其它条目。
    public static func execute(
        _ plan: [OrganizeItem],
        mode: OrganizeMode,
        rules: OrganizeRules,
        fileManager: FileManager = .default
    ) -> OrganizeSummary {
        let start = Date()
        var summary = OrganizeSummary(mode: mode, destinationRoot: plan.first?.destination.deletingLastPathComponent() ?? URL(fileURLWithPath: NSTemporaryDirectory()))
        for var item in plan {
            let source = item.source
            let parent = item.destination.deletingLastPathComponent()
            do {
                try fileManager.createDirectory(at: parent, withIntermediateDirectories: true)
                if fileManager.fileExists(atPath: item.destination.path) {
                    switch rules.conflictPolicy {
                    case .skip:
                        summary.skipped += 1
                        continue
                    case .autoRename:
                        var claimed = Set<String>()
                        item.destination = uniqueDestination(for: item.destination, claimed: &claimed, fileManager: fileManager)
                    }
                }
                switch mode {
                case .copy:
                    try fileManager.copyItem(at: source, to: item.destination)
                    summary.copied += 1
                case .move:
                    try fileManager.moveItem(at: source, to: item.destination)
                    summary.moved += 1
                }
                summary.relocated[item.asset.id] = item.destination
            } catch {
                summary.failures.append(OrganizeFailure(
                    assetID: item.asset.id,
                    message: "\(item.asset.fileName)：\(error.localizedDescription)"
                ))
            }
        }
        summary.duration = Date().timeIntervalSince(start)
        return summary
    }

    /// 一步完成：plan + execute。
    public static func perform(
        assets: [Asset],
        to destinationRoot: URL,
        mode: OrganizeMode,
        rules: OrganizeRules,
        fileManager: FileManager = .default
    ) -> OrganizeSummary {
        let items = plan(assets: assets, to: destinationRoot, rules: rules, fileManager: fileManager)
        return execute(items, mode: mode, rules: rules, fileManager: fileManager)
    }

    public static func uniqueDestination(
        for destination: URL,
        claimed: inout Set<String>,
        fileManager: FileManager = .default
    ) -> URL {
        let directory = destination.deletingLastPathComponent()
        let base = destination.deletingPathExtension().lastPathComponent
        let ext = destination.pathExtension
        var candidate = destination
        var index = 1
        while claimed.contains(candidate.standardizedFileURL.path) || fileManager.fileExists(atPath: candidate.path) {
            index += 1
            let name = "\(base) (\(index))" + (ext.isEmpty ? "" : "." + ext)
            candidate = directory.appendingPathComponent(name)
            if index > 5000 { break }
        }
        claimed.insert(candidate.standardizedFileURL.path)
        return candidate
    }
}