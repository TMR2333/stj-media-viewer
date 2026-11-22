import Foundation

// MARK: - 跨平台设置交换文件（设置 + 图片数据）
//
// 约定：macOS 版与 Windows 版共用同一份 JSON 文件格式（`stj-media-viewer/exchange`），
// 以便在两台机器/两个平台之间迁移「应用设置」与「图片数据设置」（评分、色标、选用、
// 地点、主题、影调、关键词、标题、调色状态）以及合集归属。
//
// 设计要点：
// - 字段全部使用平台中立的名称与取值；Windows 侧的 compact/comfortable/spacious
//   分别映射到 small/medium/large。
// - 只导出相对默认值发生变化的素材字段（partial），未改动的字段省略，避免把默认值覆盖到另一端。
// - **任何密钥都不进入交换文件**（API Key、WebDAV 口令、DPAPI 密文）。
// - 读取端必须忽略未知字段与未知枚举值；仅当 `format` 或 `formatVersion` 不兼容时报错。

public enum ExchangeFormat {
    public static let id = "stj-media-viewer/exchange"
    public static let version = 1
    public static let fileExtension = "json"
    public static let defaultFileNamePrefix = "STJ-设置与图片数据"
}

public struct ExchangeAppInfo: Codable, Equatable, Sendable {
    public var name: String
    public var version: String
    public var platform: String
    public var exportedAt: String

    public init(name: String = "STJ Media Viewer", version: String, platform: String, exportedAt: String) {
        self.name = name
        self.version = version
        self.platform = platform
        self.exportedAt = exportedAt
    }
}

public struct ExchangeSort: Codable, Equatable, Sendable {
    public var key: String
    public var ascending: Bool

    public init(key: String, ascending: Bool) {
        self.key = key
        self.ascending = ascending
    }
}

public struct ExchangePanels: Codable, Equatable, Sendable {
    public var sidebar: Bool
    public var inspector: Bool

    public init(sidebar: Bool, inspector: Bool) {
        self.sidebar = sidebar
        self.inspector = inspector
    }
}

/// 跨平台可迁移的设置子集（不含密钥、不含平台专属外观参数）。
public struct ExchangeSettings: Codable, Equatable, Sendable {
    public var themeMode: String?
    public var density: String?
    public var sort: ExchangeSort?
    public var panels: ExchangePanels?
    public var foldRawPairs: Bool?

    public init(themeMode: String? = nil, density: String? = nil, sort: ExchangeSort? = nil,
                panels: ExchangePanels? = nil, foldRawPairs: Bool? = nil) {
        self.themeMode = themeMode
        self.density = density
        self.sort = sort
        self.panels = panels
        self.foldRawPairs = foldRawPairs
    }

    public var isEmpty: Bool {
        themeMode == nil && density == nil && sort == nil && panels == nil && foldRawPairs == nil
    }
}

/// 素材匹配键：文件名（大小写不敏感）+ 字节数，relativePath 作为消歧补充。
public struct ExchangeAssetKey: Codable, Equatable, Hashable, Sendable {
    public var fileName: String
    public var sizeBytes: Int64
    public var relativePath: String?

    public init(fileName: String, sizeBytes: Int64, relativePath: String? = nil) {
        self.fileName = fileName
        self.sizeBytes = sizeBytes
        self.relativePath = relativePath
    }

    public var normalizedFileName: String { fileName.lowercased() }

    public init(asset: Asset) {
        self.init(fileName: asset.fileName, sizeBytes: asset.sizeBytes,
                  relativePath: (asset.relativePath?.isEmpty == false) ? asset.relativePath : nil)
    }
}

/// 一条素材的「图片数据设置」。字段为 nil 表示该端没有这项数据（保持目标端原样）。
public struct ExchangeAsset: Codable, Equatable, Sendable {
    public var fileName: String
    public var sizeBytes: Int64
    public var relativePath: String?
    public var rating: Int?
    public var colorLabel: String?
    public var isPick: Int?
    public var country: String?
    public var province: String?
    public var city: String?
    public var themes: [String]?
    public var tone: String?
    public var keywords: [String]?
    public var title: String?
    public var postProcess: Int?

    public init(fileName: String, sizeBytes: Int64, relativePath: String? = nil,
                rating: Int? = nil, colorLabel: String? = nil, isPick: Int? = nil,
                country: String? = nil, province: String? = nil, city: String? = nil,
                themes: [String]? = nil, tone: String? = nil,
                keywords: [String]? = nil, title: String? = nil, postProcess: Int? = nil) {
        self.fileName = fileName
        self.sizeBytes = sizeBytes
        self.relativePath = relativePath
        self.rating = rating
        self.colorLabel = colorLabel
        self.isPick = isPick
        self.country = country
        self.province = province
        self.city = city
        self.themes = themes
        self.tone = tone
        self.keywords = keywords
        self.title = title
        self.postProcess = postProcess
    }

    public var key: ExchangeAssetKey {
        ExchangeAssetKey(fileName: fileName, sizeBytes: sizeBytes, relativePath: relativePath)
    }

    /// 是否有任何用户可编辑字段被设置（用于判断是否值得导出）。
    public var hasUserData: Bool {
        rating != nil || colorLabel != nil || isPick != nil || country != nil || province != nil
            || city != nil || themes != nil || tone != nil || keywords != nil || title != nil
            || postProcess != nil
    }

    /// 从素材快照构造：只保留与默认值不同的字段。
    public init(asset: Asset) {
        let themes = asset.themes
        let keywords = asset.keywords
        let trimmedTitle = asset.title?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedCity = asset.city?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedProvince = asset.province?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedCountry = asset.country?.trimmingCharacters(in: .whitespacesAndNewlines)

        self.init(
            fileName: asset.fileName,
            sizeBytes: asset.sizeBytes,
            relativePath: (asset.relativePath?.isEmpty == false) ? asset.relativePath : nil,
            rating: (asset.rating != 0) ? asset.rating : nil,
            colorLabel: (asset.colorLabel?.isEmpty == false) ? asset.colorLabel : nil,
            isPick: asset.isPick,
            country: (trimmedCountry?.isEmpty == false) ? trimmedCountry : nil,
            province: (trimmedProvince?.isEmpty == false) ? trimmedProvince : nil,
            city: (trimmedCity?.isEmpty == false) ? trimmedCity : nil,
            themes: themes.isEmpty ? nil : themes,
            tone: (asset.tone?.isEmpty == false) ? asset.tone : nil,
            keywords: keywords.isEmpty ? nil : keywords,
            title: (trimmedTitle?.isEmpty == false) ? trimmedTitle : nil,
            postProcess: (asset.postProcess > 1) ? asset.postProcess : nil
        )
    }
}

public struct ExchangeCollection: Codable, Equatable, Sendable {
    public var name: String
    public var assets: [ExchangeAssetKey]

    public init(name: String, assets: [ExchangeAssetKey]) {
        self.name = name
        self.assets = assets
    }
}

public struct ExchangeDocument: Codable, Equatable, Sendable {
    public var format: String
    public var formatVersion: Int
    public var app: ExchangeAppInfo
    public var settings: ExchangeSettings
    public var assets: [ExchangeAsset]
    public var collections: [ExchangeCollection]

    public init(format: String = ExchangeFormat.id,
                formatVersion: Int = ExchangeFormat.version,
                app: ExchangeAppInfo,
                settings: ExchangeSettings,
                assets: [ExchangeAsset],
                collections: [ExchangeCollection] = []) {
        self.format = format
        self.formatVersion = formatVersion
        self.app = app
        self.settings = settings
        self.assets = assets
        self.collections = collections
    }
}

// MARK: - 编解码

public enum ExchangeError: LocalizedError, Equatable {
    case badFormat(String)
    case unsupportedVersion(Int)
    case malformed(String)

    public var errorDescription: String? {
        switch self {
        case .badFormat(let value):
            return "不是 STJ 设置交换文件（format=\(value.isEmpty ? "空" : value)）"
        case .unsupportedVersion(let version):
            return "交换文件版本 \(version) 不受支持（当前支持 \(ExchangeFormat.version)）"
        case .malformed(let detail):
            return "交换文件内容无法解析：\(detail)"
        }
    }
}

public enum SettingsExchange {
    public static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return encoder
    }

    public static func encode(_ document: ExchangeDocument) throws -> Data {
        try encoder().encode(document)
    }

    public static func decode(_ data: Data) throws -> ExchangeDocument {
        let document: ExchangeDocument
        do {
            document = try JSONDecoder().decode(ExchangeDocument.self, from: data)
        } catch {
            throw ExchangeError.malformed(error.localizedDescription)
        }
        guard document.format == ExchangeFormat.id else {
            throw ExchangeError.badFormat(document.format)
        }
        guard document.formatVersion <= ExchangeFormat.version, document.formatVersion >= 1 else {
            throw ExchangeError.unsupportedVersion(document.formatVersion)
        }
        return document
    }

    /// 生成交换文件内容。`assets` 与 `collections` 由调用方给定。
    public static func makeDocument(settings: ExchangeSettings,
                                    assets: [ExchangeAsset],
                                    collections: [ExchangeCollection] = [],
                                    appVersion: String,
                                    platform: String = "macos",
                                    exportedAt: Date = Date()) throws -> ExchangeDocument {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return ExchangeDocument(
            app: ExchangeAppInfo(version: appVersion, platform: platform,
                                 exportedAt: formatter.string(from: exportedAt)),
            settings: settings,
            assets: assets,
            collections: collections
        )
    }
}

// MARK: - 导入计划与执行

public enum ExchangeMergeMode: String, CaseIterable, Sendable {
    case fillEmpty
    case overwrite

    public var title: String {
        switch self {
        case .fillEmpty: return "只填空字段"
        case .overwrite: return "覆盖已有值"
        }
    }
}

public struct ExchangeMatchedAsset: Sendable {
    public let assetID: String
    public let payload: ExchangeAsset
    public let current: Asset
}

public struct ExchangeImportPlan: Sendable {
    public let settings: ExchangeSettings
    public let matched: [ExchangeMatchedAsset]
    public let unmatched: [ExchangeAssetKey]
    public let collections: [(name: String, assetIDs: [String])]
    public let sourceApp: ExchangeAppInfo

    public var matchedCount: Int { matched.count }
    public var unmatchedCount: Int { unmatched.count }
}

public struct ExchangeImportSummary: Sendable {
    public var updatedAssets: Int
    public var skippedAssets: Int
    public var createdCollections: Int
    public var addedCollectionMembers: Int
    public var settingsApplied: Bool

    public var descriptionText: String {
        var parts = ["更新 \(updatedAssets) 条素材"]
        if skippedAssets > 0 { parts.append("跳过 \(skippedAssets) 条（无变化）") }
        if createdCollections > 0 { parts.append("新建合集 \(createdCollections) 个") }
        if addedCollectionMembers > 0 { parts.append("补入合集 \(addedCollectionMembers) 条") }
        if settingsApplied { parts.append("应用设置") }
        return parts.joined(separator: " · ")
    }
}

extension SettingsExchange {
    /// 从素材库生成交换文档（命令行与 App 共用）。`settings` 为可迁移设置子集，命令行可传空。
    public static func exportDocument(catalog: Catalog,
                                      appVersion: String,
                                      settings: ExchangeSettings = ExchangeSettings()) throws -> ExchangeDocument {
        let all = try catalog.loadAssets(limit: 200_000, includeGroupMembers: true)
        let members = try catalog.collectionMembers()
        let byID = Dictionary(uniqueKeysWithValues: all.map { ($0.id, $0) })
        var exported: [ExchangeAsset] = []
        var seen = Set<ExchangeAssetKey>()

        for asset in all {
            let payload = ExchangeAsset(asset: asset)
            guard payload.hasUserData else { continue }
            exported.append(payload)
            seen.insert(payload.key)
        }
        var collections: [ExchangeCollection] = []
        for collection in try catalog.collections() {
            guard let ids = members[collection.id], !ids.isEmpty else { continue }
            var keys: [ExchangeAssetKey] = []
            for id in ids {
                guard let asset = byID[id] else { continue }
                let payload = ExchangeAsset(asset: asset)
                keys.append(payload.key)
                if !payload.hasUserData, !seen.contains(payload.key) {
                    exported.append(payload)
                    seen.insert(payload.key)
                }
            }
            if !keys.isEmpty {
                collections.append(ExchangeCollection(name: collection.name, assets: keys))
            }
        }
        return try makeDocument(settings: settings, assets: exported,
                                collections: collections, appVersion: appVersion)
    }

    /// 从文件读取并计算导入计划（命令行与 App 共用）。
    public static func plan(fromFile url: URL, catalog: Catalog) throws -> ExchangeImportPlan {
        let data = try Data(contentsOf: url)
        let document = try decode(data)
        return plan(document: document, assets: try catalog.loadAssets(limit: 200_000, includeGroupMembers: true))
    }
}

extension SettingsExchange {
    /// 计算导入计划：按「文件名 + 字节数」匹配，`relativePath` 仅用于多候选消歧。
    public static func plan(document: ExchangeDocument, assets: [Asset]) -> ExchangeImportPlan {
        var buckets: [String: [Asset]] = [:]
        for asset in assets {
            buckets["\(asset.fileName.lowercased())|\(asset.sizeBytes)", default: []].append(asset)
        }
        var matched: [ExchangeMatchedAsset] = []
        var unmatched: [ExchangeAssetKey] = []

        for payload in document.assets {
            let key = "\(payload.key.normalizedFileName)|\(payload.sizeBytes)"
            guard var candidates = buckets[key], !candidates.isEmpty else {
                unmatched.append(payload.key)
                continue
            }
            if candidates.count > 1, let relative = payload.relativePath?.lowercased() {
                let narrowed = candidates.filter { ($0.relativePath ?? "").lowercased().hasSuffix(relative) }
                if !narrowed.isEmpty { candidates = narrowed }
            }
            let target = candidates[0]
            matched.append(ExchangeMatchedAsset(assetID: target.id, payload: payload, current: target))
        }

        let idToKey = Dictionary(uniqueKeysWithValues: matched.map { ($0.assetID, $0.payload.key) })
        var collections: [(name: String, assetIDs: [String])] = []
        for collection in document.collections {
            var ids: [String] = []
            for key in collection.assets {
                let bucketKey = "\(key.normalizedFileName)|\(key.sizeBytes)"
                if let asset = assets.first(where: { $0.fileName.lowercased() == key.normalizedFileName && $0.sizeBytes == key.sizeBytes }) {
                    ids.append(asset.id)
                }
            }
            if !ids.isEmpty { collections.append((collection.name, ids)) }
        }
        _ = idToKey

        return ExchangeImportPlan(settings: document.settings, matched: matched, unmatched: unmatched,
                                  collections: collections, sourceApp: document.app)
    }

    /// 把计划写入素材库。返回摘要。
    public static func apply(plan: ExchangeImportPlan,
                             mode: ExchangeMergeMode,
                             catalog: Catalog) throws -> ExchangeImportSummary {
        var summary = ExchangeImportSummary(updatedAssets: 0, skippedAssets: 0,
                                            createdCollections: 0, addedCollectionMembers: 0,
                                            settingsApplied: false)
        var grouped: [String: [String]] = [:]
        var signaturePatches: [String: AssetPatch] = [:]

        for item in plan.matched {
            let patch = makePatch(payload: item.payload, current: item.current, mode: mode)
            guard !patch.isEmpty else {
                summary.skippedAssets += 1
                continue
            }
            let signature = describe(patch)
            grouped[signature, default: []].append(item.assetID)
            signaturePatches[signature] = patch
        }

        for (signature, ids) in grouped {
            guard let patch = signaturePatches[signature] else { continue }
            try catalog.update(patch: patch, ids: ids)
            summary.updatedAssets += ids.count
        }

        // 合集：按名称合并（不删除已有成员）。
        var existing: [String: String] = [:]
        for collection in try catalog.collections() {
            existing[collection.name] = collection.id
        }
        for entry in plan.collections {
            if let id = existing[entry.name] {
                try catalog.addToCollection(collectionID: id, assetIDs: entry.assetIDs)
                summary.addedCollectionMembers += entry.assetIDs.count
            } else {
                let id = try catalog.createCollection(name: entry.name)
                try catalog.addToCollection(collectionID: id, assetIDs: entry.assetIDs)
                existing[entry.name] = id
                summary.createdCollections += 1
                summary.addedCollectionMembers += entry.assetIDs.count
            }
        }

        return summary
    }

    private static func makePatch(payload: ExchangeAsset,
                                  current: Asset,
                                  mode: ExchangeMergeMode) -> AssetPatch {
        var patch = AssetPatch()
        func shouldApply<T: Equatable>(incoming: T?, currentValue: T, defaultValue: T) -> Bool {
            guard let incoming else { return false }
            switch mode {
            case .overwrite: return true
            case .fillEmpty: return currentValue == defaultValue
            }
        }

        if shouldApply(incoming: payload.rating, currentValue: current.rating, defaultValue: 0) {
            patch.rating = payload.rating
        }
        if shouldApply(incoming: payload.postProcess, currentValue: current.postProcess, defaultValue: 1) {
            patch.postProcess = payload.postProcess
        }
        if let value = payload.colorLabel, mode == .overwrite || (current.colorLabel?.isEmpty ?? true) {
            patch.colorLabel = .set(value)
        }
        if let value = payload.isPick, mode == .overwrite || current.isPick == nil {
            patch.isPick = .set(value)
        }
        if let value = payload.city, mode == .overwrite || (current.city?.isEmpty ?? true) {
            patch.city = value.isEmpty ? .clear : .set(value)
        }
        if let value = payload.country, mode == .overwrite || (current.country?.isEmpty ?? true) {
            patch.country = value.isEmpty ? .clear : .set(value)
        }
        if let value = payload.province, mode == .overwrite || (current.province?.isEmpty ?? true) {
            patch.province = value.isEmpty ? .clear : .set(value)
        }
        if let value = payload.tone, mode == .overwrite || (current.tone?.isEmpty ?? true) {
            patch.tone = value.isEmpty ? .clear : .set(value)
        }
        if let value = payload.themes, mode == .overwrite || current.themes.isEmpty {
            patch.themes = value.isEmpty ? .clear : .set(value)
        }
        if let value = payload.keywords, mode == .overwrite || current.keywords.isEmpty {
            patch.keywords = value.isEmpty ? .clear : .set(value)
        }
        return patch
    }

    private static func describe(_ patch: AssetPatch) -> String {
        var parts: [String] = []
        if let rating = patch.rating { parts.append("r=\(rating)") }
        if let post = patch.postProcess { parts.append("p=\(post)") }
        func add<T>(_ name: String, _ value: PatchValue<T>) where T: Encodable {
            switch value {
            case .unchanged: break
            case .clear: parts.append("\(name)=∅")
            case .set(let v):
                if let data = try? JSONEncoder().encode(v), let text = String(data: data, encoding: .utf8) {
                    parts.append("\(name)=\(text)")
                }
            }
        }
        add("c", patch.colorLabel)
        add("k", patch.isPick)
        add("city", patch.city)
        add("country", patch.country)
        add("province", patch.province)
        add("tone", patch.tone)
        add("themes", patch.themes)
        add("kw", patch.keywords)
        return parts.joined(separator: "|")
    }
}

// MARK: - 设置快照 / 应用

extension SettingsExchange {
    /// 从本机设置生成跨平台可迁移子集。
    @MainActor
    public static func snapshot(from settings: SettingsStore) -> ExchangeSettings {
        ExchangeSettings(
            themeMode: settings.appearance.rawValue,
            density: settings.density.rawValue,
            sort: ExchangeSort(key: settings.sortKey.rawValue, ascending: settings.sortAscending),
            panels: ExchangePanels(sidebar: settings.showsSidebar, inspector: settings.showsInspector),
            foldRawPairs: settings.foldRawPairs
        )
    }

    /// 应用交换文件里的设置（只应用到本机支持的项；未识别值忽略）。
    @MainActor
    @discardableResult
    public static func apply(settings: ExchangeSettings, to store: SettingsStore) -> Bool {
        guard !settings.isEmpty else { return false }
        if let mode = settings.themeMode, let appearance = AppearanceMode(rawValue: mode) {
            store.appearance = appearance
        }
        if let density = settings.density, let value = LibraryDensity(rawValue: density) {
            store.density = value
        } else if let density = settings.density, let value = LibraryDensity(legacyName: density) {
            store.density = value
        }
        if let sort = settings.sort, let key = SortKey(rawValue: sort.key) {
            store.sortKey = key
            store.sortAscending = sort.ascending
        }
        if let panels = settings.panels {
            store.showsSidebar = panels.sidebar
            store.showsInspector = panels.inspector
        }
        if let fold = settings.foldRawPairs {
            store.foldRawPairs = fold
        }
        return true
    }
}

extension LibraryDensity {
    /// 兼容 Windows 侧的 compact / comfortable / spacious 命名。
    public init?(legacyName: String) {
        switch legacyName.lowercased() {
        case "compact": self = .small
        case "comfortable": self = .medium
        case "spacious": self = .large
        case "list", "detailed": self = .list
        default: return nil
        }
    }
}